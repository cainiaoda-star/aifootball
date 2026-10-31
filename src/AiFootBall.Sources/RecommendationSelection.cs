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
    /// 竞彩销售：北京时间每天 9:00 开售，21:58 停售。
    /// 停售到次日开售之间开赛的场次，最后能买的时刻是前一个 21:58。
    /// </summary>
    public static class JcSaleHours
    {
        public static readonly TimeOnly Open = new(9, 0);
        public static readonly TimeOnly Close = new(21, 58);

        public static DateTime Now()
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
            return TimeZoneInfo.ConvertTime(DateTime.UtcNow, tz);
        }

        public static bool IsOpen(DateTime beijing)
        {
            var clock = TimeOnly.FromDateTime(beijing);
            return clock >= Open && clock <= Close;
        }

        /// <summary>这场在 beijing 这一刻是否还来得及买。已开赛为否。</summary>
        public static bool CanStillBuy(DateTime beijing, DateTime kickoff)
        {
            if (kickoff <= beijing) return false;
            return beijing <= LastBuyAt(kickoff);
        }

        public static DateTime LastBuyAt(DateTime kickoff)
        {
            var clock = TimeOnly.FromDateTime(kickoff);
            if (clock >= Open && clock <= Close) return kickoff;
            if (clock > Close) return kickoff.Date + Close.ToTimeSpan();
            return kickoff.Date.AddDays(-1) + Close.ToTimeSpan();
        }

        public static string Note(DateTime beijing) => IsOpen(beijing)
            ? "每天 21:58 停售，次日 9:00 再开。这段时间开赛的，过了 21:58 就不推荐"
            : "现在停售，9:00 再开。21:58 到 9:00 之间开赛的场次已去掉";
    }

    /// <summary>
    /// 竞彩精选过关建议：3 中 1 挂时只打高串会几乎全废，默认带 2串1。
    /// </summary>
    public static string? PassTip(string board, int featuredCount)
    {
        if (board != Boards.Jc || featuredCount < 2) return null;
        if (featuredCount == 2) return "建议过关：2串1";
        if (featuredCount == 3) return "建议过关：2串1 + 3串1（防单腿）";
        return "这几场胜平负是低水，不要串。中高水让球用 74 元票：8 场，6串1+7串1+8串1";
    }

    /// <summary>够格进精选：等级≥2，且校准命中率达标。没有校准命中率不进。</summary>
    public static bool QualifiesForFeatured(MatchRecommendation x)
        => x.SignalLevel >= 2
           && x.HitProbability >= RecommendationScoring.MinFeaturedHitProbability;

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
            .OrderByDescending(x => x.HitProbability ?? x.Confidence ?? decimal.MinValue)
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
            // 比分、让球、进球、半全场的命中率都低于胜平负，不跟在同一条精选上。
            r.PublishMarkets = false;
            r.ScoreCandidates.Clear();
            r.GoalCandidates.Clear();
            r.HalfFullPick = null;
            r.RecHandicap = null;
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
    /// 74 元中高水票：8 场让球，6串1+7串1+8串1，1 倍。
    /// 注数 C(8,6)+C(8,7)+C(8,8)=37，单注 2 元。
    /// 错一场还剩不含该场的 7串1 和 6串1；错两场还剩其余 6 场的那注 6串1。
    /// </summary>
    public static class JcCoverTicket
    {
        public const int LegCount = 8;
        public const int Multiple = 1;
        public const decimal UnitStake = 2m;
        public const decimal MinOdds = 2.20m;
        public const decimal MaxOdds = 4.20m;
        public const string Pass = "6串1#7串1#8串1";
        public const int BetCount = 37;
        public const decimal Stake = 74m;

        public static List<T> Pick<T>(IEnumerable<T> legs, Func<T, decimal> odds, Func<T, decimal?> hit)
        {
            return legs
                .Where(x => odds(x) >= MinOdds && odds(x) <= MaxOdds)
                .OrderByDescending(x => hit(x) ?? decimal.MinValue)
                .ThenByDescending(odds)
                .Take(LegCount)
                .ToList();
        }

        public static decimal AllHit(IReadOnlyList<decimal> odds)
        {
            if (odds.Count != LegCount) return 0;
            var legs = odds.Select(o => (hit: true, odds: (decimal?)o)).ToList();
            return PurchaseSettleMath.CalculatePrize(legs, Pass, Multiple);
        }

        public static (decimal Low, decimal High) OneMiss(IReadOnlyList<decimal> odds) => Range(odds, 1);

        public static (decimal Low, decimal High) TwoMiss(IReadOnlyList<decimal> odds) => Range(odds, 2);

        private static (decimal Low, decimal High) Range(IReadOnlyList<decimal> odds, int misses)
        {
            if (odds.Count != LegCount || misses <= 0 || misses >= odds.Count) return (0, 0);
            var prizes = new List<decimal>();
            foreach (var drop in Combinations(Enumerable.Range(0, odds.Count).ToArray(), misses))
            {
                var dropped = drop.ToHashSet();
                var scene = odds.Select((o, i) => (hit: !dropped.Contains(i), odds: (decimal?)o)).ToList();
                prizes.Add(PurchaseSettleMath.CalculatePrize(scene, Pass, Multiple));
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
