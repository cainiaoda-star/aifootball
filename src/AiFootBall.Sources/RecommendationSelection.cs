using AiFootBall.Core.Models;

namespace AiFootBall.Sources;

    /// <summary>各板块对外展示和落库时保留哪些场次。</summary>
public static class RecommendationSelection
{
    /// <summary>竞彩每日重点推荐上限。够格不足时有多少发多少，不凑数。宁少勿滥。</summary>
    public const int PublishLimit = 8;

    /// <summary>北单可选场次上限（胜负 / 比分勾选投注）。</summary>
    public const int BdPublishLimit = 11;

    /// <summary>北单「强推荐」场次：从够格名单里按强度再取前 N 场打标。</summary>
    public const int BdStrongLimit = 8;

    /// <summary>
    /// 竞彩销售：北京时间每天 9:00 开售。周一到周五 21:55 停，周六日 22:55 停。
    /// 停售到次日开售之间开赛的场次，最后能买的时刻是前一个停售点。
    /// </summary>
    public static class JcSaleHours
    {
        public static readonly TimeOnly Open = new(9, 0);
        public static readonly TimeOnly WeekdayClose = new(21, 55);
        public static readonly TimeOnly WeekendClose = new(22, 55);

        public static TimeOnly CloseOn(DateTime day)
            => day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? WeekendClose : WeekdayClose;

        public static DateTime Now()
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
            return TimeZoneInfo.ConvertTime(DateTime.UtcNow, tz);
        }

        public static bool IsOpen(DateTime beijing)
        {
            var clock = TimeOnly.FromDateTime(beijing);
            return clock >= Open && clock <= CloseOn(beijing);
        }

        /// <summary>这场在 beijing 这一刻是否还来得及买。已开赛为否。</summary>
        public static bool CanStillBuy(DateTime beijing, DateTime kickoff)
        {
            if (kickoff <= beijing) return false;
            return beijing <= LastBuyAt(kickoff);
        }

        public static DateTime LastBuyAt(DateTime kickoff)
        {
            var close = CloseOn(kickoff);
            var clock = TimeOnly.FromDateTime(kickoff);
            if (clock >= Open && clock <= close) return kickoff;
            if (clock > close) return kickoff.Date + close.ToTimeSpan();
            var prev = kickoff.Date.AddDays(-1);
            return prev + CloseOn(prev).ToTimeSpan();
        }

        /// <summary>
        /// 开售时段只留当天那一期。9:00 前，当天仍算前一天。
        /// 停售之后到次日 9:00，当天剩下的场次已经买不了，同时带上下一期。
        /// 周六白天仍不选「周日xxx」。周六停售改到 22:55，22:55 之前仍只留周六。
        /// </summary>
        public static bool IsTodaysIssue(string? matchNo, DateTime beijing)
        {
            if (string.IsNullOrWhiteSpace(matchNo)) return false;
            var no = matchNo.Trim();
            if (no.StartsWith(WeekdayName(SaleDay(beijing)), StringComparison.Ordinal))
                return true;
            return !IsOpen(beijing)
                   && no.StartsWith(WeekdayName(NextSaleDay(beijing)), StringComparison.Ordinal);
        }

        static DateTime SaleDay(DateTime beijing)
            => TimeOnly.FromDateTime(beijing) < Open ? beijing.Date.AddDays(-1) : beijing.Date;

        static DateTime NextSaleDay(DateTime beijing)
        {
            var clock = TimeOnly.FromDateTime(beijing);
            if (clock > CloseOn(beijing)) return beijing.Date.AddDays(1);
            if (clock < Open) return beijing.Date;
            return beijing.Date.AddDays(1);
        }

        static string WeekdayName(DateTime day) => day.DayOfWeek switch
        {
            DayOfWeek.Monday => "周一",
            DayOfWeek.Tuesday => "周二",
            DayOfWeek.Wednesday => "周三",
            DayOfWeek.Thursday => "周四",
            DayOfWeek.Friday => "周五",
            DayOfWeek.Saturday => "周六",
            _ => "周日"
        };

        public static string Note(DateTime beijing) => IsOpen(beijing)
            ? "只选当天开售的这一期，第二天的不选。周六日 22:55 停，其余 21:55 停，之后开赛的过了停售不推荐"
            : $"现在停售，9:00 再开。当天剩下的已经过了 {CloseOn(beijing):HH:mm}，名单加上下一期还能买的场次";
    }

    /// <summary>
    /// 竞彩精选过关建议：3 中 1 挂时只打高串会几乎全废，默认带 2串1。
    /// </summary>
    public static string? PassTip(string board, int featuredCount)
    {
        if (board != Boards.Jc || featuredCount < 2) return null;
        if (featuredCount == 2) return "建议过关：2串1";
        if (featuredCount == 3) return "建议过关：2串1 + 3串1（防单腿）";
        return "这几场胜平负是低水，不要串。让球够几场出几场，最多 8 场";
    }

    /// <summary>够格进精选：等级≥2，且校准命中率达标。没有校准命中率不进。北单按让球选项的概率筛。</summary>
    public static bool QualifiesForFeatured(MatchRecommendation x)
    {
        if (x.Board == Boards.Bd)
            return x.RecHandicap is not null
                   && x.HandicapProbability >= RecommendationScoring.MinFeaturedHitProbability
                   && !DeepHandicapWithoutTotal(x);
        return x.SignalLevel >= 2
               && x.HitProbability >= RecommendationScoring.MinFeaturedHitProbability;
    }

    private static decimal? RankProbability(MatchRecommendation x)
        => x.Board == Boards.Bd ? x.HandicapProbability : x.HitProbability ?? x.Confidence;

    public static string HandicapOptionName(char option) => option switch
    {
        '3' => "让胜",
        '1' => "让平",
        _ => "让负"
    };

    /// <summary>
    /// 北单只卖让球胜平负：用欧赔去水 + 大小球盘算出这个让球数下三项的概率，推概率最高的一项。
    /// 不让球方向只作参考写在说明里。没有欧赔或没有让球数时不给。
    /// </summary>
    public static void ApplyBdHandicap(
        MatchRecommendation rec, decimal? spfHome, decimal? spfDraw, decimal? spfAway, decimal? totalLine,
        decimal? spWin, decimal? spDraw, decimal? spLoss)
    {
        rec.RecHandicap = null;
        rec.HandicapProbability = null;
        rec.HandicapOdds = null;
        if (rec.Handicap is not decimal hcp) return;
        var p = HandicapMath.FromOdds(spfHome, spfDraw, spfAway, totalLine, hcp);
        if (p is null) return;

        var options = new[] { ('3', p.Value.Win, spWin), ('1', p.Value.Draw, spDraw), ('0', p.Value.Loss, spLoss) };
        var best = options.OrderByDescending(o => o.Item2).First();
        rec.RecHandicap = best.Item1;
        rec.HandicapProbability = Math.Round(best.Item2 * 100m, 1);
        rec.HandicapOdds = best.Item3 is > 1 ? best.Item3 : null;

        var hcpText = hcp > 0 ? $"+{hcp:0.##}" : $"{hcp:0.##}";
        var sp = rec.HandicapOdds is decimal o ? $"，北单 SP {o:0.00}（开奖前会变）" : "";
        var plain = string.IsNullOrWhiteSpace(rec.SignalDesc) ? "" : $"；不让球参考（北单不卖）：{rec.SignalDesc}";
        var held = Math.Abs(hcp) >= 1.5m && totalLine is not > 0.5m
            ? "；让球达到 1.5 且没有大小球盘，不进精选" : "";
        rec.SignalDesc = $"北单让{hcpText}：{HandicapOptionName(best.Item1)} 约 {rec.HandicapProbability:0.0}%{sp}{plain}{held}";
    }

    /// <summary>
    /// 竞彩让球胜平负：用欧赔去水 + 大小球盘，算这场开出的让球数下三项概率，推最高的一项。
    /// 胜平负精选仍单独筛。澳超让负不发。没有让球数就不给。
    /// </summary>
    public static void ApplyJcHandicap(
        MatchRecommendation rec, decimal? spfHome, decimal? spfDraw, decimal? spfAway, decimal? totalLine,
        decimal? rqWin, decimal? rqDraw, decimal? rqLoss)
    {
        rec.RecHandicap = null;
        rec.HandicapProbability = null;
        rec.HandicapOdds = null;
        if (rec.Handicap is not decimal hcp) return;
        var p = HandicapMath.FromOdds(spfHome, spfDraw, spfAway, totalLine, hcp);
        if (p is null) return;

        var options = new[] { ('3', p.Value.Win, rqWin), ('1', p.Value.Draw, rqDraw), ('0', p.Value.Loss, rqLoss) };
        var best = options.OrderByDescending(o => o.Item2).First();
        if (best.Item1 == '0' && RecommendationEngine.IsALeague(rec.LeagueName)) return;

        rec.RecHandicap = best.Item1;
        rec.HandicapProbability = Math.Round(best.Item2 * 100m, 1);
        rec.HandicapOdds = best.Item3 is > 1 ? best.Item3 : null;
    }

    /// <summary>
    /// 竞彩让球：让胜、让平、让负里概率最高的一项推出来，最多 8 场。
    /// 不到 55% 也发，页面不标精选。深盘又没有大小球盘的不进。
    /// </summary>
    public static List<MatchRecommendation> ForJcHandicap(IReadOnlyList<MatchRecommendation> recs)
    {
        return recs
            .Where(x => x.RecHandicap is '3' or '1' or '0'
                        && !DeepHandicapWithoutTotal(x))
            .OrderByDescending(x => x.HandicapProbability ?? decimal.MinValue)
            .ThenBy(x => OfficialMatchOrder(x.MatchNo))
            .Take(PublishLimit)
            .OrderBy(x => OfficialMatchOrder(x.MatchNo))
            .ThenBy(x => x.MatchNo, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 竞彩胜平负：算法已经给出的主胜、平、客胜推出来，最多 8 场。
    /// 够 55% 且等级够的仍标精选；不够的照算出来的概率发，不标精选。澳超客胜不发。
    /// </summary>
    public static List<MatchRecommendation> ForJcCalculated(IReadOnlyList<MatchRecommendation> recs)
    {
        var featured = recs.Where(x => IssuedSpf(x) && QualifiesForFeatured(x)).ToList();
        var featuredNos = featured
            .Select(x => x.MatchNo)
            .Where(x => !string.IsNullOrEmpty(x))
            .ToHashSet(StringComparer.Ordinal);
        var rest = recs
            .Where(x => IssuedSpf(x) && (x.MatchNo is null || !featuredNos.Contains(x.MatchNo)))
            .OrderByDescending(x => x.HitProbability ?? x.Confidence ?? decimal.MinValue)
            .ThenBy(x => OfficialMatchOrder(x.MatchNo));
        var picked = featured
            .OrderByDescending(x => x.HitProbability ?? decimal.MinValue)
            .Concat(rest)
            .Take(PublishLimit)
            .OrderBy(x => OfficialMatchOrder(x.MatchNo))
            .ThenBy(x => x.MatchNo, StringComparer.Ordinal)
            .ToList();

        foreach (var r in picked)
        {
            if (r.RefScores.Count == 0 && r.ScoreCandidates.Count > 0)
                r.RefScores = r.ScoreCandidates.ToList();
            if (r.RefGoals.Count == 0 && r.GoalCandidates.Count > 0)
                r.RefGoals = r.GoalCandidates.ToList();
            r.ScoreCandidates.Clear();
            r.GoalCandidates.Clear();
            r.HalfFullPick = null;
            r.PublishMarkets = false;
        }

        return picked;
    }

    private static bool SpfDirectionOk(MatchRecommendation x)
        => x.RecResult is '3' or '1' or '0'
           && !(x.RecResult == '0' && RecommendationEngine.IsALeague(x.LeagueName));

    /// <summary>
    /// 竞彩胜平负和北单不让球参考要发的方向。
    /// 「赔率最低、无强信号」的兜底不发。2026-11-01 周日004 用主胜 1.63 的去水标成 58%，终场 0:1。
    /// </summary>
    private static bool IssuedSpf(MatchRecommendation x)
        => SpfDirectionOk(x) && !x.IsOddsFallback;

    /// <summary>
    /// 北单让球：让胜、让平、让负里概率最高的一项推出来，最多 11 场。
    /// 过 55% 的前 8 场标强推荐。不到 55% 的也列出，不标强推荐。深盘又没有大小球盘的不进。
    /// </summary>
    public static List<MatchRecommendation> ForBdCalculated(IReadOnlyList<MatchRecommendation> recs)
    {
        var picked = recs
            .Where(x => x.RecHandicap is '3' or '1' or '0' && !DeepHandicapWithoutTotal(x))
            .OrderByDescending(x => x.HandicapProbability ?? decimal.MinValue)
            .ThenBy(x => OfficialMatchOrder(x.MatchNo))
            .Take(BdPublishLimit)
            .ToList();

        var strong = picked
            .Where(x => x.HandicapProbability >= RecommendationScoring.MinFeaturedHitProbability)
            .Take(BdStrongLimit)
            .Select(x => x.MatchNo)
            .Where(x => !string.IsNullOrEmpty(x))
            .ToHashSet(StringComparer.Ordinal);

        var ranked = picked
            .OrderBy(x => OfficialMatchOrder(x.MatchNo))
            .ThenBy(x => x.MatchNo, StringComparer.Ordinal)
            .ToList();

        foreach (var r in ranked)
        {
            r.IsFeatured = r.MatchNo is not null && strong.Contains(r.MatchNo);
            if (r.RefScores.Count == 0 && r.ScoreCandidates.Count > 0)
                r.RefScores = r.ScoreCandidates.ToList();
            if (r.RefGoals.Count == 0 && r.GoalCandidates.Count > 0)
                r.RefGoals = r.GoalCandidates.ToList();
            r.ScoreCandidates.Clear();
            r.GoalCandidates.Clear();
            r.HalfFullPick = null;
            r.PublishMarkets = false;
        }

        return ranked;
    }

    /// <summary>
    /// 北单不让球参考：算法给出的主胜、平、客胜，最多 11 场。北单不卖这项，不标强推荐。澳超客胜不发。
    /// </summary>
    public static List<MatchRecommendation> ForBdPlain(IReadOnlyList<MatchRecommendation> recs)
    {
        return recs
            .Where(IssuedSpf)
            .OrderByDescending(x => x.HitProbability ?? x.Confidence ?? decimal.MinValue)
            .ThenBy(x => OfficialMatchOrder(x.MatchNo))
            .Take(BdPublishLimit)
            .OrderBy(x => OfficialMatchOrder(x.MatchNo))
            .ThenBy(x => x.MatchNo, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 竞彩平局：均势看平、模型看平都发。不并进 55% 胜平负精选，置信度不抬。
    /// 赔率兜底写成「平局赔率最低、无强信号」的不发。
    /// </summary>
    public static bool IsIssuedDraw(MatchRecommendation x)
    {
        if (x.RecResult != '1' || x.IsOddsFallback) return false;
        var desc = x.SignalDesc ?? "";
        if (desc.Contains("赔率兜底", StringComparison.Ordinal)
            && desc.Contains("平局赔率最低", StringComparison.Ordinal))
            return false;
        return desc.Contains("均势看平", StringComparison.Ordinal)
               || desc.Contains("模型看平", StringComparison.Ordinal)
               || desc.Contains("平局回测偏低", StringComparison.Ordinal);
    }

    public static List<MatchRecommendation> ForJcDraw(IReadOnlyList<MatchRecommendation> recs)
    {
        return recs
            .Where(IsIssuedDraw)
            .OrderBy(x => OfficialMatchOrder(x.MatchNo))
            .ThenBy(x => x.MatchNo, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 让球绝对值达到 1.5、又没有真实大小球盘时，总进球按 2.6 算出的 55% 偏高。
    /// 2025–2026 验证里这类场实际命中 53.6%，不进精选。有盘口时仍按 55% 进。
    /// </summary>
    public static bool DeepHandicapWithoutTotal(MatchRecommendation x)
        => x.Handicap is decimal h && Math.Abs(h) >= 1.5m && x.TotalLine is not > 0.5m;

    /// <summary>录入方案时用的过关字符串（如 2串1#3串1）。</summary>
    public static string? PassTypeValue(string board, int featuredCount)
    {
        if (featuredCount < 2) return null;
        if (board is not (Boards.Jc or Boards.Bd)) return null;
        if (featuredCount == 3) return "2串1#3串1";
        return "2串1";
    }

    /// <summary>
    /// 竞彩取够格最好最多 8 场；北单最多 11 场。不够不凑，宁少勿滥。
    /// 排序优先预计命中率（校准后），再信号等级/置信度；选出后再按官方场次号展示。
    /// 竞彩、北单精选只发胜平负，不附带比分、让球、进球、半全场。
    /// 北单发最多 11 场，其中强度最好的 8 场标「强推荐」。胜负彩保留全部受注场次。
    /// </summary>
    public static List<MatchRecommendation> ForDisplay(string board, IReadOnlyList<MatchRecommendation> recs)
    {
        if (board == Boards.Sfc)
        {
            return recs
                .OrderBy(x => OfficialMatchOrder(x.MatchNo))
                .ThenBy(x => x.MatchNo, StringComparer.Ordinal)
                .ToList();
        }

        var limit = board == Boards.Bd ? BdPublishLimit : PublishLimit;
        var picked = recs
            .Where(QualifiesForFeatured)
            .OrderByDescending(x => RankProbability(x) ?? decimal.MinValue)
            .ThenByDescending(x => x.SignalLevel)
            .ThenByDescending(x => x.Confidence ?? decimal.MinValue)
            .ThenBy(x => OfficialMatchOrder(x.MatchNo))
            .Take(limit)
            .ToList();

        // 北单：按命中率排序后的前 N 场打强推荐标（展示仍按官方场次序）
        HashSet<string>? bdStrong = null;
        if (board == Boards.Bd)
        {
            bdStrong = picked
                .Take(BdStrongLimit)
                .Select(x => x.MatchNo)
                .Where(x => !string.IsNullOrEmpty(x))
                .ToHashSet(StringComparer.Ordinal)!;
        }

        var ranked = picked
            .OrderBy(x => OfficialMatchOrder(x.MatchNo))
            .ThenBy(x => x.MatchNo, StringComparer.Ordinal)
            .ToList();

        foreach (var r in ranked)
        {
            r.IsFeatured = board != Boards.Bd
                || (bdStrong is not null && r.MatchNo is not null && bdStrong.Contains(r.MatchNo));
            // 比分和总进球留作参考，不进勾选、不进投注单。
            r.PublishMarkets = false;
            r.RefScores = r.ScoreCandidates.ToList();
            r.RefGoals = r.GoalCandidates.ToList();
            r.ScoreCandidates.Clear();
            r.GoalCandidates.Clear();
            r.HalfFullPick = null;
            if (board != Boards.Bd) r.RecHandicap = null;
        }
        return ranked;
    }

    /// <summary>
    /// 官方场次排序键：北单纯数字；竞彩「周xNNN」按周一=1…周日=7，再按场次号。
    /// </summary>
    public static int OfficialMatchOrder(string? matchNo)
    {
        if (string.IsNullOrWhiteSpace(matchNo)) return int.MaxValue;
        var s = matchNo.Trim();
        if (int.TryParse(s, out var plain)) return plain;

        var day = 0;
        if (s.Contains("周一", StringComparison.Ordinal)) day = 1;
        else if (s.Contains("周二", StringComparison.Ordinal)) day = 2;
        else if (s.Contains("周三", StringComparison.Ordinal)) day = 3;
        else if (s.Contains("周四", StringComparison.Ordinal)) day = 4;
        else if (s.Contains("周五", StringComparison.Ordinal)) day = 5;
        else if (s.Contains("周六", StringComparison.Ordinal)) day = 6;
        else if (s.Contains("周日", StringComparison.Ordinal)) day = 7;

        var digits = new string(s.Where(char.IsDigit).ToArray());
        var num = int.TryParse(digits, out var n) ? n : 0;
        return day * 1000 + num;
    }

    /// <summary>用最可能比分和让球数折出让球胜平负。没有比分或没有让球时不给方向。</summary>
    public static char? HandicapPick(string? score, decimal? handicap)
    {
        if (string.IsNullOrWhiteSpace(score) || handicap is null) return null;
        var parts = score.Split(':', '：');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0], out var home) || !int.TryParse(parts[1], out var away)) return null;
        var diff = home + handicap.Value - away;
        if (diff > 0.001m) return '3';
        if (diff < -0.001m) return '0';
        return '1';
    }

    /// <summary>
    /// 按胜平负方向 + 常见 1 球分差折让球胜平负。
    /// 避免比分预测为满足深让球硬筛成 2:0，导致让 -1 时系统性偏「让胜」。
    /// </summary>
    public static char? HandicapFromSpf(char? spf, decimal? handicap)
    {
        if (handicap is null || spf is not ('3' or '1' or '0')) return null;
        // 主胜按 1:0、客胜按 0:1、平按 1:1（比 0:0 更常见）
        var (home, away) = spf switch
        {
            '3' => (1, 0),
            '0' => (0, 1),
            _ => (1, 1)
        };
        return HandicapPick($"{home}:{away}", handicap);
    }

    /// <summary>
    /// 让球票。够几场出几场，最多 8 场。不限 2.20–4.20。
    /// 满 8 场仍是 6串1+7串1+8串1，37 注，74 元。
    /// 不足 8 场时过关改成 (n-2)串1+(n-1)串1+n串1，金额按注数算。
    /// </summary>
    public static class JcCoverTicket
    {
        public const int LegCount = 8;
        public const int Multiple = 1;
        public const decimal UnitStake = 2m;
        public const string Pass = "6串1#7串1#8串1";
        public const int BetCount = 37;
        public const decimal Stake = 74m;

        /// <summary>n 场的过关。8 场是 6串1#7串1#8串1，6 场是 4串1#5串1#6串1。</summary>
        public static string PassFor(int n)
        {
            if (n <= 1) return "单关";
            if (n == 2) return "2串1";
            return $"{n - 2}串1#{n - 1}串1#{n}串1";
        }

        public static int BetCountFor(int n)
        {
            if (n <= 1) return n == 1 ? 1 : 0;
            if (n == 2) return 1;
            return Comb(n, 2) + n + 1;
        }

        public static decimal StakeFor(int n) => BetCountFor(n) * UnitStake;

        /// <summary>让球三个价去水后，这一边打过盘的概率（0–1）。</summary>
        public static decimal? LegProbability(char side, decimal nspfWin, decimal nspfDraw, decimal nspfLoss)
        {
            var p = HitRateMath.Devig(nspfWin, nspfDraw, nspfLoss);
            if (p is null) return null;
            return side == '3' ? p.Value.Home : side == '0' ? p.Value.Away : p.Value.Draw;
        }

        /// <summary>
        /// 按每场概率独立相乘，列出全部中/不中组合：有奖金、奖金不少于本金、全中三种概率（0–1）。
        /// </summary>
        public static (decimal AnyPrize, decimal BackStake, decimal AllHit) Chances(
            IReadOnlyList<(decimal Odds, decimal P)> legs)
        {
            var n = legs.Count;
            if (n == 0 || n > 12) return (0, 0, 0);
            var pass = PassFor(n);
            var stake = StakeFor(n);
            decimal any = 0, back = 0, all = 0;
            for (var mask = 0; mask < 1 << n; mask++)
            {
                decimal p = 1;
                var scene = new List<(bool hit, decimal? odds)>(n);
                for (var i = 0; i < n; i++)
                {
                    var hit = (mask & (1 << i)) != 0;
                    p *= hit ? legs[i].P : 1 - legs[i].P;
                    scene.Add((hit, legs[i].Odds));
                }
                if (p == 0) continue;
                var prize = PurchaseSettleMath.CalculatePrize(scene, pass, Multiple);
                if (prize > 0) any += p;
                if (prize >= stake) back += p;
                if (mask == (1 << n) - 1) all += p;
            }
            return (any, back, all);
        }

        /// <summary>
        /// 胜平负方向按 1 球分差折成让球选项，赔率用该选项自己的让球价。
        /// 让胜、让平、让负都收。预计命中率不到精选线的不进这张票。不限水位高低。
        /// </summary>
        public static (char Side, decimal Odds, string Option)? TryLeg(
            char? spf, decimal? handicap,
            decimal nspfWin, decimal nspfDraw, decimal nspfLoss,
            decimal? hitProbability)
        {
            if (hitProbability is null || hitProbability < RecommendationScoring.MinFeaturedHitProbability)
                return null;
            var side = HandicapFromSpf(spf, handicap);
            if (side is not ('3' or '1' or '0')) return null;
            var odds = side == '3' ? nspfWin : side == '1' ? nspfDraw : nspfLoss;
            if (odds <= 1) return null;
            return (side.Value, odds, HandicapOptionName(side.Value));
        }

        /// <summary>让球名单上过 55% 的让胜、让平、让负。水位不设上下限。</summary>
        public static (char Side, decimal Odds, string Option)? TryHandicapLeg(
            char? side, decimal? probability, decimal win, decimal draw, decimal loss)
        {
            if (side is not ('3' or '1' or '0')) return null;
            if (probability is null || probability < RecommendationScoring.MinFeaturedHitProbability)
                return null;
            var odds = side == '3' ? win : side == '1' ? draw : loss;
            if (odds <= 1) return null;
            return (side.Value, odds, HandicapOptionName(side.Value));
        }

        public static List<T> Pick<T>(IEnumerable<T> legs, Func<T, decimal> odds, Func<T, decimal?> hit)
        {
            return legs
                .Where(x => odds(x) > 1)
                .OrderByDescending(x => hit(x) ?? decimal.MinValue)
                .ThenByDescending(odds)
                .Take(LegCount)
                .ToList();
        }

        public static decimal AllHit(IReadOnlyList<decimal> odds)
        {
            if (odds.Count == 0) return 0;
            var legs = odds.Select(o => (hit: true, odds: (decimal?)o)).ToList();
            return PurchaseSettleMath.CalculatePrize(legs, PassFor(odds.Count), Multiple);
        }

        public static (decimal Low, decimal High) OneMiss(IReadOnlyList<decimal> odds) => Range(odds, 1);

        public static (decimal Low, decimal High) TwoMiss(IReadOnlyList<decimal> odds) => Range(odds, 2);

        private static (decimal Low, decimal High) Range(IReadOnlyList<decimal> odds, int misses)
        {
            if (odds.Count == 0 || misses <= 0 || misses >= odds.Count) return (0, 0);
            var pass = PassFor(odds.Count);
            var prizes = new List<decimal>();
            foreach (var drop in Combinations(Enumerable.Range(0, odds.Count).ToArray(), misses))
            {
                var dropped = drop.ToHashSet();
                var scene = odds.Select((o, i) => (hit: !dropped.Contains(i), odds: (decimal?)o)).ToList();
                prizes.Add(PurchaseSettleMath.CalculatePrize(scene, pass, Multiple));
            }

            return (prizes.Min(), prizes.Max());
        }

        private static IEnumerable<int[]> Combinations(int[] source, int k)
        {
            var idx = new int[k];
            for (var i = 0; i < k; i++) idx[i] = i;
            while (true)
            {
                var row = new int[k];
                for (var i = 0; i < k; i++) row[i] = source[idx[i]];
                yield return row;
                var t = k - 1;
                while (t >= 0 && idx[t] == source.Length - k + t) t--;
                if (t < 0) yield break;
                idx[t]++;
                for (var j = t + 1; j < k; j++) idx[j] = idx[j - 1] + 1;
            }
        }

        private static int Comb(int n, int k)
        {
            if (k < 0 || k > n) return 0;
            if (k == 0 || k == n) return 1;
            var r = 1;
            for (var i = 1; i <= k; i++)
                r = r * (n - k + i) / i;
            return r;
        }
    }

    /// <summary>按全场方向给两个最常见的半全场。</summary>
    public static string? HalfFullPick(char? result) => result switch
    {
        '3' => "胜/胜、平/胜",
        '1' => "平/平、胜/平",
        '0' => "负/负、平/负",
        _ => null
    };
}
