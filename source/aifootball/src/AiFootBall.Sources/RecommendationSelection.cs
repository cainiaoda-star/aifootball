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
    /// 竞彩销售：北京时间每天 9:00 开售。周一到周五 21:58 停，周六日 22:58 停
    /// （2026-11-04 用户口径：停售 21:58 / 22:58，推荐终版在停售前 8 分钟定稿——工作日 21:50、周末 22:50）。
    /// 停售到次日开售之间开赛的场次，最后能买的时刻是前一个停售点。
    /// </summary>
    public static class JcSaleHours
    {
        public static readonly TimeOnly Open = new(9, 0);
        public static readonly TimeOnly WeekdayClose = new(21, 58);
        public static readonly TimeOnly WeekendClose = new(22, 58);

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
        /// 周六白天仍不选「周日xxx」。周六停售 22:58，22:58 之前仍只留周六。
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
            ? "只选当天开售的这一期，第二天的不选。周六日 22:58 停，其余 21:58 停，之后开赛的过了停售不推荐"
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
        rec.HandicapDrawProbability = null;
        rec.HandicapDrawOdds = null;
        rec.HandicapModelProbability = null;
        rec.HandicapMarketProbability = null;
        if (rec.Handicap is not decimal hcp) return;
        var p = HandicapMath.FromOdds(spfHome, spfDraw, spfAway, totalLine, hcp);
        if (p is null) return;

        var options = new[] { ('3', p.Value.Win, spWin), ('1', p.Value.Draw, spDraw), ('0', p.Value.Loss, spLoss) };
        var best = options.OrderByDescending(o => o.Item2).First();
        rec.RecHandicap = best.Item1;
        rec.HandicapProbability = Math.Round(best.Item2 * 100m, 1);

        // 模型 vs 市场双留（2026-11-11 加）。方向是按模型概率挑的，所以要能事后回答
        // 「模型挑的这一档，比盘口认为的概率高还是低」——这是唯一能验证 edge 的口径。
        // 55 注实测：模型概率均值 48.5%、恰好这些档的市场隐含均值 51.4%，
        // 模型系统性保守 2.9 个点，p×SP 期望因此只有 0.983（微亏）。
        // 只有把两边都存下来，才谈得上「只出模型比市场乐观的注」。
        rec.HandicapModelProbability = Math.Round(best.Item2 * 100m, 1);
        if (HitRateMath.Devig(spWin, spDraw, spLoss) is { } mkt)
        {
            var mp = best.Item1 switch
            {
                '3' => mkt.Home,
                '1' => mkt.Draw,
                _ => mkt.Away
            };
            rec.HandicapMarketProbability = Math.Round(mp * 100m, 1);
        }
        rec.HandicapOdds = best.Item3 is > 1 ? best.Item3 : null;
        // 让平档单留一份：BdSpfPick 的「赢球不赢盘→让平优先」规则要用（教训 1e/1l）。
        rec.HandicapDrawProbability = Math.Round(p.Value.Draw * 100m, 1);
        rec.HandicapDrawOdds = spDraw is > 1 ? spDraw : null;

        var hcpText = hcp > 0 ? $"+{hcp:0.##}" : $"{hcp:0.##}";
        var sp = rec.HandicapOdds is decimal o ? $"，北单 SP {o:0.00}（开奖前会变）" : "";
        var plain = string.IsNullOrWhiteSpace(rec.SignalDesc) ? "" : $"；不让球参考（北单不卖）：{rec.SignalDesc}";
        var held = Math.Abs(hcp) >= 1.5m && totalLine is not > 0.5m
            ? "；让球达到 1.5 且没有大小球盘，不进精选" : "";
        if (held.Length > 0) rec.Tags |= SignalTag.NotFeatured;
        rec.SignalDesc = $"北单让{hcpText}：{HandicapOptionName(best.Item1)} 约 {rec.HandicapProbability:0.0}%{sp}{plain}{held}";
    }

    /// <summary>
    /// 竞彩让球胜平负：推三项里概率最高的一项。
    ///
    /// 概率有两个来源，优先用盘口：
    /// - 盘口 = 竞彩让球固定奖金三项直接去水。这是对这一注本身的定价，抽水结构已经按同一市场归一。
    /// - 模型 = 欧赔去水 → 泊松拟合 → 折成该让球数下的三项。欧赔是「不让球」市场，折过来的让球数
    ///   还要靠大小球盘校准入球期望，链条长，偏差会在末端放大。
    ///
    /// 拿不让球市场反推让球市场，两者抽水结构不同，会系统性偏离；直接对让球盘去水更准。
    /// 缺让球盘时退回模型，两个值都留在 rec 上，事后才能回答「谁更准」。
    ///
    /// 胜平负精选仍单独筛。澳超让负不发。没有让球数就不给。
    /// </summary>
    public static void ApplyJcHandicap(
        MatchRecommendation rec, decimal? spfHome, decimal? spfDraw, decimal? spfAway, decimal? totalLine,
        decimal? rqWin, decimal? rqDraw, decimal? rqLoss)
    {
        rec.RecHandicap = null;
        rec.HandicapProbability = null;
        rec.HandicapOdds = null;
        rec.HandicapModelProbability = null;
        rec.HandicapMarketProbability = null;
        rec.HandicapSource = null;
        if (rec.Handicap is not decimal hcp) return;

        var model = HandicapMath.FromOdds(spfHome, spfDraw, spfAway, totalLine, hcp);
        var market = HitRateMath.Devig(rqWin, rqDraw, rqLoss);
        if (model is null && market is null) return;

        // 有盘口就按盘口挑方向：庄家已经把让球数带来的偏差定进去了，
        // 模型挑的边在盘口面前没有额外信息量。
        char side;
        decimal sideProb;
        if (market is not null)
        {
            var m = market.Value;
            side = m.Home >= m.Draw && m.Home >= m.Away ? '3' : m.Draw >= m.Away ? '1' : '0';
            sideProb = side == '3' ? m.Home : side == '1' ? m.Draw : m.Away;
        }
        else
        {
            var m = model!.Value;
            side = m.Win >= m.Draw && m.Win >= m.Loss ? '3' : m.Draw >= m.Loss ? '1' : '0';
            sideProb = side == '3' ? m.Win : side == '1' ? m.Draw : m.Loss;
        }

        if (side == '0' && RecommendationEngine.IsALeague(rec.LeagueName)) return;

        rec.RecHandicap = side;
        rec.HandicapProbability = Math.Round(sideProb * 100m, 1);
        rec.HandicapOdds = side switch
        {
            '3' => rqWin is > 1 ? rqWin : null,
            '1' => rqDraw is > 1 ? rqDraw : null,
            _ => rqLoss is > 1 ? rqLoss : null
        };
        // 2026-11-07 合理性闸：北单让球胜平负 SP 正常区间约 1.0~15，极端死局盘也不该超过 25。
        // 超过说明抓串了别的玩法/选项（场227 让负曾串成让平的 36.47），置空显示「SP未知」，宁缺毋错。
        if (rec.HandicapOdds is > 25m) rec.HandicapOdds = null;
        rec.HandicapMarketProbability = market is null
            ? null
            : Math.Round((side == '3' ? market.Value.Home : side == '1' ? market.Value.Draw : market.Value.Away) * 100m, 1);
        rec.HandicapModelProbability = model is null
            ? null
            : Math.Round((side == '3' ? model.Value.Win : side == '1' ? model.Value.Draw : model.Value.Loss) * 100m, 1);
        rec.HandicapSource = market is null ? "model" : "market";
    }

    /// <summary>
    /// 竞彩让球：让胜、让平、让负里概率最高的一项推出来，最多 8 场。
    /// 水位已知且低于 <see cref="RecommendationScoring.MinPickOdds"/> 的不推（2026-11-04 起低赔不进任何推荐）。
    /// 水位未知的保留——那是数据缺口，不是低赔。深盘又没有大小球盘的不进。
    /// </summary>
    public static List<MatchRecommendation> ForJcHandicap(IReadOnlyList<MatchRecommendation> recs)
    {
        return recs
            .Where(x => x.RecHandicap is '3' or '1' or '0'
                        && x.HandicapOdds is null or >= RecommendationScoring.MinPickOdds
                        && !DeepHandicapWithoutTotal(x))
            .OrderByDescending(x => x.HandicapProbability ?? decimal.MinValue)
            .ThenBy(x => OfficialMatchOrder(x.MatchNo))
            .Take(PublishLimit)
            .OrderBy(x => OfficialMatchOrder(x.MatchNo))
            .ThenBy(x => x.MatchNo, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 竞彩票腿：赔率 ≥ <see cref="RecommendationScoring.MinPickOdds"/> 的让球场次，
    /// 概率只要过 <see cref="RecommendationScoring.MinTicketLegProbability"/> 的垃圾线即可。
    /// 2026-11-04 之前这里是 55% 命中率门槛，配合让球盘的定价结构只会选出 1.45–1.60 的温吞腿；
    /// 用户明确指示低赔不组票后，赔率顶到第一道闸，概率降为垃圾过滤。
    /// 出票时的实时价位再由 <see cref="JcCoverTicket.TryHandicapLeg"/> 把关（快照价可能已变）。
    /// </summary>
    public static List<MatchRecommendation> ForJcTicketLegs(IReadOnlyList<MatchRecommendation> recs)
    {
        return recs
            .Where(x => x.RecHandicap is '3' or '1' or '0'
                        && x.HandicapOdds is null or >= RecommendationScoring.MinPickOdds
                        && !DeepHandicapWithoutTotal(x)
                        && x.HandicapProbability >= RecommendationScoring.MinTicketLegProbability)
            .OrderByDescending(x => x.HandicapProbability ?? decimal.MinValue)
            .ThenBy(x => OfficialMatchOrder(x.MatchNo))
            .Take(PublishLimit)
            .OrderBy(x => OfficialMatchOrder(x.MatchNo))
            .ThenBy(x => x.MatchNo, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 竞彩胜平负：算法已经给出的主胜、平、客胜推出来，最多 8 场。
    /// 水位已知且低于 <see cref="RecommendationScoring.MinPickOdds"/> 的不推（低赔不进推荐，2026-11-04）。
    /// 够 55% 且等级够的仍标精选；不够的照算出来的概率发，不标精选。澳超客胜不发。
    /// </summary>
    public static List<MatchRecommendation> ForJcCalculated(IReadOnlyList<MatchRecommendation> recs)
    {
        var featured = recs.Where(x => IssuedSpf(x) && JcPickOddsOk(x) && QualifiesForFeatured(x)).ToList();
        var featuredNos = featured
            .Select(x => x.MatchNo)
            .Where(x => !string.IsNullOrEmpty(x))
            .ToHashSet(StringComparer.Ordinal);
        var rest = recs
            .Where(x => IssuedSpf(x) && JcPickOddsOk(x) && (x.MatchNo is null || !featuredNos.Contains(x.MatchNo)))
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
    /// 竞彩推荐的水位闸：已知水位低于下限的算低赔，不进推荐；
    /// 水位未知（没跑过模型、没快照）的保留——那是数据缺口，不该当低赔踢掉。
    /// </summary>
    private static bool JcPickOddsOk(MatchRecommendation x)
        => x.PickOdds is null or >= RecommendationScoring.MinPickOdds;

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
    /// 判定看 SignalTag 的平局位，不看文案——文案改字不该改变发不发。
    /// </summary>
    public static bool IsIssuedDraw(MatchRecommendation x)
    {
        if (x.RecResult != '1' || x.IsOddsFallback) return false;
        return (x.Tags & (SignalTag.DrawEven | SignalTag.DrawByModel | SignalTag.DrawLowBacktest)) != 0;
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
    ///
    /// 过关档位是 (n-2)串1 + (n-1)串1 + n串1，注数 C(n,2)+n+1，金额按注数算。
    /// 满 8 场就是传统写的「6串1#7串1#8串1、37 注 74 元」，这是**上限**而不是常态：
    /// 2026-11-04 起票腿要过水位 ≥1.80 的闸（低赔不组票，用户明确指示），
    /// 一天通常只有 2–3 场够格，实际出的是单关/2串1/3串1，凑不出腿就干脆不出票。
    /// 长串档位留着，是因为真凑到 6 场时那是正确玩法，但它不是「默认就该发 8 腿」。
    ///
    /// 8 是竞彩过关的上限（最高 8 串 1），所以所有按腿数算注数、算概率的入口
    /// 都以 <see cref="MaxLegs"/> 为硬边界：多传的腿直接判为算不出，不生成 9串1。
    /// </summary>
    public static class JcCoverTicket
    {
        /// <summary>一场让球票最多 8 腿，等于竞彩过关上限。</summary>
        public const int MaxLegs = 8;
        public const int LegCount = MaxLegs;
        public const int Multiple = 1;
        public const decimal UnitStake = 2m;

        /// <summary>腿数是否落在可售区间。超过 8 腿没有合法票型，一律按「算不出」处理，不静默截断。</summary>
        private static bool Sellable(int n) => n is > 0 and <= MaxLegs;

        /// <summary>n 场的过关。8 场是 6串1#7串1#8串1，6 场是 4串1#5串1#6串1。</summary>
        public static string PassFor(int n)
        {
            if (!Sellable(n)) return "";
            if (n == 1) return "单关";
            if (n == 2) return "2串1";
            // n=3 时 n-2=1，写成「1串1」既不是彩民的叫法，也会让票面 KPI 把
            // 同一票型拆成两个 key。单关就写单关，注数与结算都按 1 场组合算。
            var lowest = n - 2 == 1 ? "单关" : $"{n - 2}串1";
            return $"{lowest}#{n - 1}串1#{n}串1";
        }

        public static int BetCountFor(int n)
        {
            if (!Sellable(n)) return 0;
            if (n == 1) return 1;
            if (n == 2) return 1;
            return Comb(n, 2) + n + 1;
        }

        public static decimal StakeFor(int n) => BetCountFor(n) * UnitStake;

        /// <summary>某票型的注数。单关 n 注、2串1 全包 C(n,2) 注，其余按本项目固定档位。</summary>
        public static int BetCountOf(string pass, int n)
        {
            if (!Sellable(n)) return 0;
            if (pass == "单关") return n;
            if (pass == "2串1") return n < 2 ? 0 : Comb(n, 2);
            return BetCountFor(n);
        }

        public static decimal StakeOf(string pass, int n) => BetCountOf(pass, n) * UnitStake;

        /// <summary>
        /// 任意票型的中奖率 / 回本率 / 期望回报。
        /// 回本率是「奖金 ≥ 本金」的概率——串关经常中奖却亏钱，只看中奖率会高估自己。
        /// 期望回报 = Σ 概率×奖金 ÷ 本金，等于 1 才不亏；竞彩返还率下必然小于 1。
        /// 超过 8 腿没有合法票型，返回 0 让调用方当作「算不出」，不要生成假数。
        /// </summary>
        public static (decimal AnyPrize, decimal BackStake, decimal Expected) Plan(
            IReadOnlyList<(decimal Odds, decimal P)> legs, string pass)
        {
            var n = legs.Count;
            if (n == 0 || n > MaxLegs) return (0, 0, 0);
            var stake = StakeOf(pass, n);
            if (stake <= 0) return (0, 0, 0);

            decimal any = 0, back = 0, ev = 0;
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
                ev += p * prize;
            }
            return (any, back, ev / stake);
        }

        /// <summary>让球三个价去水后，这一边打过盘的概率（0–1）。</summary>
        public static decimal? LegProbability(char side, decimal nspfWin, decimal nspfDraw, decimal nspfLoss)
        {
            var p = HitRateMath.Devig(nspfWin, nspfDraw, nspfLoss);
            if (p is null) return null;
            return side == '3' ? p.Value.Home : side == '0' ? p.Value.Away : p.Value.Draw;
        }

        /// <summary>模型与盘口分歧超过这个百分点，票面要提示这笔票本身不确定。</summary>
        public const decimal DisagreementPoints = 8m;

        /// <summary>
        /// 票面用哪一条腿的概率。两个来源都不含经验校准，谁也不能自称准：
        /// 模型 = 引擎用欧赔去水 + 大小球折算到竞彩让球数（选腿的 55% 门槛就按它判）；
        /// 盘口 = 竞彩让球三项直接去水（更贴真实定价，但是庄家的数）。
        /// 取两者较低值，避免用乐观的那一边算「有多少概率回本」。同时返回原值供页面并排显示。
        /// </summary>
        public static (decimal? Value, decimal? Model, decimal? Market) PickProbability(
            decimal? modelP, decimal? marketP)
        {
            var valid = new List<decimal>(2);
            if (modelP is > 0 and < 1) valid.Add(modelP.Value);
            if (marketP is > 0 and < 1) valid.Add(marketP.Value);
            if (valid.Count == 0) return (null, modelP, marketP);
            return (valid.Min(), modelP, marketP);
        }

        /// <summary>模型与盘口分歧的百分点（取绝对值）。任一缺失返回 null。</summary>
        public static decimal? Disagreement(decimal? modelP, decimal? marketP)
        {
            if (modelP is not decimal m || marketP is not decimal k) return null;
            if (m is <= 0 or >= 1 || k is <= 0 or >= 1) return null;
            return Math.Round(Math.Abs(m - k) * 100m, 1);
        }

        /// <summary>
        /// 按每场概率独立相乘，列出全部中/不中组合：有奖金、奖金不少于本金、全中三种概率（0–1）。
        /// 超过 8 腿没有合法票型，返回 0。
        /// </summary>
        public static (decimal AnyPrize, decimal BackStake, decimal AllHit) Chances(
            IReadOnlyList<(decimal Odds, decimal P)> legs)
        {
            var n = legs.Count;
            if (!Sellable(n)) return (0, 0, 0);
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
            // 2026-11-04 用户指示：低赔不组票。概率门槛从 55 降到垃圾线 45，
            // 赔率下限 1.80 顶上来当第一道闸——这是页面和落库共用的唯一咽喉，
            // 改这里两张票同时生效。
            if (probability is null || probability < RecommendationScoring.MinTicketLegProbability)
                return null;
            var odds = side == '3' ? win : side == '1' ? draw : loss;
            if (odds < RecommendationScoring.MinPickOdds) return null;
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
            if (!Sellable(odds.Count)) return 0;
            var legs = odds.Select(o => (hit: true, odds: (decimal?)o)).ToList();
            return PurchaseSettleMath.CalculatePrize(legs, PassFor(odds.Count), Multiple);
        }

        public static (decimal Low, decimal High) OneMiss(IReadOnlyList<decimal> odds) => Range(odds, 1);

        public static (decimal Low, decimal High) TwoMiss(IReadOnlyList<decimal> odds) => Range(odds, 2);

        private static (decimal Low, decimal High) Range(IReadOnlyList<decimal> odds, int misses)
        {
            if (!Sellable(odds.Count) || misses <= 0 || misses >= odds.Count) return (0, 0);
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
