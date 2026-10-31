namespace AiFootBall.Core.Models;

/// <summary>购买方案自动结算：单场命中判定 + 过关奖金计算（竞彩 2 元单注）。</summary>
public static class PurchaseSettleMath
{
    public sealed class MatchScore
    {
        public int Home { get; init; }
        public int Away { get; init; }
        public int? HalfHome { get; init; }
        public int? HalfAway { get; init; }
        public decimal? Handicap { get; init; }

        public string ScoreColon => $"{Home}:{Away}";
        public string ScoreHyphen => $"{Home}-{Away}";
        public int Goals => Home + Away;
        public char Spf => Home > Away ? '3' : Home == Away ? '1' : '0';

        public char? HandicapSpf(decimal? lineHandicap)
        {
            var hcp = lineHandicap ?? Handicap;
            if (hcp is null) return null;
            var diff = Home + hcp.Value - Away;
            return diff > 0 ? '3' : Math.Abs(diff) < 0.001m ? '1' : '0';
        }

        public string? HalfFull
        {
            get
            {
                if (HalfHome is null || HalfAway is null) return null;
                return SpfText(HalfHome > HalfAway ? '3' : HalfHome == HalfAway ? '1' : '0')
                     + "/" + SpfText(Spf);
            }
        }

        private static string SpfText(char c) => c switch { '3' => "胜", '1' => "平", '0' => "负", _ => "?" };
    }

    public sealed class OptionHit
    {
        public bool Hit { get; init; }
        public decimal? Odds { get; init; }
        public string? Option { get; init; }
    }

    /// <summary>判定一场的选项命中；option1/option2 任一命中即该场过关可用。</summary>
    public static OptionHit EvaluateLine(string? playType, PurchaseLineRecord line, MatchScore score)
    {
        var hits = new List<(string opt, decimal? odds)>();
        TryOption(playType, line, line.Option1, line.Odds1, score, hits);
        TryOption(playType, line, line.Option2, line.Odds2, score, hits);
        if (hits.Count == 0) return new OptionHit { Hit = false };
        // 比分等互斥玩法最多中一个；若异常双中，取赔率较高者
        var best = hits.OrderByDescending(h => h.odds ?? 0).First();
        return new OptionHit { Hit = true, Odds = best.odds, Option = best.opt };
    }

    private static void TryOption(string? playType, PurchaseLineRecord line, string? opt, decimal? odds,
        MatchScore score, List<(string, decimal?)> hits)
    {
        if (string.IsNullOrWhiteSpace(opt)) return;
        foreach (var part in SplitOptions(opt))
        {
            if (OptionMatches(playType, line, part, score))
                hits.Add((part, odds));
        }
    }

    private static IEnumerable<string> SplitOptions(string opt) =>
        opt.Split(new[] { ',', '，', '、', '+', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool OptionMatches(string? playType, PurchaseLineRecord line, string raw, MatchScore score)
    {
        var opt = NormalizeOption(raw);
        if (opt.Length == 0) return false;

        var play = playType ?? "";
        var inferred = InferPlay(play, opt);

        return inferred switch
        {
            "全场比分" => ScoreEquals(opt, score),
            "总进球" => GoalsEquals(opt, score.Goals),
            "半全场" => HalfFullEquals(opt, score.HalfFull),
            "让球胜平负" => SpfEquals(opt, score.HandicapSpf(line.Handicap), handicap: true),
            _ => SpfEquals(opt, score.Spf, handicap: false)
        };
    }

    private static string InferPlay(string play, string opt)
    {
        // 票面已标明胜平负：让胜/让平/让负按胜/平/负算，不用让球盘。
        if (play == "胜平负")
            return IsScoreToken(opt) ? "全场比分" : "胜平负";
        // 选项形态优先：避免方案玩法误标成「全场比分」时把「让胜」当比分判飞
        if (opt.StartsWith("让", StringComparison.Ordinal) || opt is "让胜" or "让平" or "让负")
            return "让球胜平负";
        if (IsScoreToken(opt) || opt is "平其它" or "胜其它" or "负其它")
            return "全场比分";
        if (opt.Contains('/')) return "半全场";
        if (IsGoalsToken(opt)) return "总进球";
        if (opt is "胜" or "平" or "负" or "3" or "1" or "0")
            return play.Contains("让球") ? "让球胜平负" : "胜平负";

        if (play.Contains("让球")) return "让球胜平负";
        if (play.Contains("比分")) return "全场比分";
        if (play.Contains("半全场")) return "半全场";
        if (play.Contains("总进球")) return "总进球";
        return "胜平负";
    }

    private static string NormalizeOption(string s) =>
        s.Trim()
            .Replace('：', ':')
            .Replace('－', '-')
            .Replace('—', '-')
            .Replace("主胜", "胜")
            .Replace("客胜", "负")
            .Replace("主负", "负");

    private static bool IsScoreToken(string opt) =>
        System.Text.RegularExpressions.Regex.IsMatch(opt, @"^\d{1,2}[:：\-]\d{1,2}$");

    private static bool IsGoalsToken(string opt) =>
        System.Text.RegularExpressions.Regex.IsMatch(opt, @"^(\d|7\+|7球以上|\d球)$");

    private static bool ScoreEquals(string opt, MatchScore score)
    {
        var n = opt.Replace('-', ':').Replace('：', ':');
        return n == score.ScoreColon;
    }

    private static bool GoalsEquals(string opt, int goals)
    {
        if (opt is "7+" or "7球以上") return goals >= 7;
        var m = System.Text.RegularExpressions.Regex.Match(opt, @"^(\d)");
        return m.Success && int.Parse(m.Groups[1].Value) == goals;
    }

    private static bool HalfFullEquals(string opt, string? actual)
    {
        if (actual is null) return false;
        var a = NormalizeHalfFull(opt);
        var b = NormalizeHalfFull(actual);
        return a == b;
    }

    private static string NormalizeHalfFull(string s) =>
        s.Replace("主胜", "胜").Replace("客胜", "负").Replace(" ", "");

    private static bool SpfEquals(string opt, char? actual, bool handicap)
    {
        if (actual is null) return false;
        var t = opt.Replace("让", "");
        var want = t switch
        {
            "胜" or "3" => '3',
            "平" or "1" => '1',
            "负" or "0" => '0',
            _ => '?'
        };
        if (want == '?') return false;
        _ = handicap;
        return want == actual;
    }

    /// <summary>
    /// 按过关方式计算税前奖金。支持 M串1；多选场次按「命中选项赔率」过关。
    /// 单注 2 元 × 倍数；单注奖金 &gt; 10000 按 20% 扣税。
    /// </summary>
    public static decimal CalculatePrize(
        IReadOnlyList<(bool hit, decimal? odds)> legs,
        string? passType,
        int multiple)
    {
        if (legs.Count == 0) return 0;
        var mult = Math.Max(1, multiple);
        var sizes = ParsePassSizes(passType, legs.Count);
        if (sizes.Count == 0)
        {
            // 无过关信息：全部命中则按单关累加（有选项赔率的场）
            if (legs.All(l => l.hit) && legs.Count == 1 && legs[0].odds is > 0)
                return Tax(2m * legs[0].odds.Value * mult);
            return 0;
        }

        decimal total = 0;
        var n = legs.Count;
        var idxs = Enumerable.Range(0, n).ToArray();
        foreach (var size in sizes)
        {
            if (size <= 0 || size > n) continue;
            foreach (var combo in Combinations(idxs, size))
            {
                decimal prod = 1;
                var ok = true;
                foreach (var i in combo)
                {
                    if (!legs[i].hit || legs[i].odds is null or <= 0) { ok = false; break; }
                    prod *= legs[i].odds!.Value;
                }
                if (!ok) continue;
                total += Tax(2m * prod * mult);
            }
        }
        return Math.Round(total, 2, MidpointRounding.AwayFromZero);
    }

    private static decimal Tax(decimal prize) =>
        prize > 10000m ? Math.Round(prize * 0.8m, 2, MidpointRounding.AwayFromZero) : Math.Round(prize, 2, MidpointRounding.AwayFromZero);

    /// <summary>解析「2串1#3串1」→ 组合场次数 [2,3]。仅处理 *串1；其他串关按常见展开近似。</summary>
    public static List<int> ParsePassSizes(string? passType, int matchCount)
    {
        var sizes = new List<int>();
        if (string.IsNullOrWhiteSpace(passType))
        {
            if (matchCount == 1) sizes.Add(1);
            return sizes;
        }

        var src = passType.Replace('ｘ', 'x').Replace('×', 'x');
        foreach (var part in src.Split(new[] { '#', ',', '，', '+', ';', '；' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var m = System.Text.RegularExpressions.Regex.Match(part.Trim(), @"(\d+)\s*串\s*(\d+)");
            if (!m.Success)
            {
                m = System.Text.RegularExpressions.Regex.Match(part.Trim(), @"(\d+)\s*[xX]\s*(\d+)");
                if (!m.Success) continue;
            }
            var a = int.Parse(m.Groups[1].Value);
            var b = int.Parse(m.Groups[2].Value);
            foreach (var s in ExpandPass(a, b, matchCount))
                if (!sizes.Contains(s)) sizes.Add(s);
        }

        // 「单关」
        if (src.Contains("单关", StringComparison.Ordinal) && !sizes.Contains(1))
            sizes.Add(1);

        return sizes;
    }

    /// <summary>竞彩常见串关：M串1 → 取 M 场；选了 N 场时对 C(N,M) 组合结算。</summary>
    private static IEnumerable<int> ExpandPass(int m, int n, int selected)
    {
        // 票面写的是「3串1」且选了 4 场 → 组合大小仍为 3
        if (n == 1) { yield return m; yield break; }
        if (m == n && m == selected) { yield return selected; yield break; }

        // 简化：M串N 且 N>1 时，按「包含的串1 档位」展开（够用的常见表）
        foreach (var s in KnownPassLegs(m, n))
            yield return s;
    }

    private static IEnumerable<int> KnownPassLegs(int m, int code)
    {
        // 摘录常用：键 = (选场数暗示 m, 串关代号 n) → 组合大小列表
        // 用户主路径是 *串1，这里兜底
        return (m, code) switch
        {
            (_, 1) => new[] { m },
            (3, 3) => new[] { 2 },
            (3, 4) => new[] { 2, 3 },
            (3, 7) => new[] { 1, 2, 3 },
            (4, 4) => new[] { 3 },
            (4, 5) => new[] { 3, 4 },
            (4, 6) => new[] { 2 },
            (4, 11) => new[] { 2, 3, 4 },
            (4, 15) => new[] { 1, 2, 3, 4 },
            (5, 5) => new[] { 4 },
            (5, 6) => new[] { 4, 5 },
            (5, 16) => new[] { 3, 4, 5 },
            (5, 26) => new[] { 2, 3, 4, 5 },
            (5, 31) => new[] { 1, 2, 3, 4, 5 },
            _ => new[] { m } // 未知时按 M串1 处理
        };
    }

    private static IEnumerable<int[]> Combinations(int[] items, int k)
    {
        var n = items.Length;
        if (k <= 0 || k > n) yield break;
        var buf = new int[k];
        foreach (var c in Comb(0, 0))
            yield return c;

        IEnumerable<int[]> Comb(int start, int depth)
        {
            if (depth == k)
            {
                yield return (int[])buf.Clone();
                yield break;
            }
            for (var i = start; i <= n - (k - depth); i++)
            {
                buf[depth] = items[i];
                foreach (var c in Comb(i + 1, depth + 1))
                    yield return c;
            }
        }
    }
}
