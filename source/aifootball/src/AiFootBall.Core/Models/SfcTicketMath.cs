namespace AiFootBall.Core.Models;

/// <summary>
/// 胜负彩 / 任选九注数与中奖注数。规则对齐 500.com：每注 2 元。
/// 14 场胜负彩注数是各场选项个数连乘；一等奖 14 场全中，二等奖恰好 13 场。
/// 任选九从已选场次里取 9 场，只有一个奖级。多选的错误项只加注数，不加中奖注数。
/// </summary>
public static class SfcTicketMath
{
    public const decimal UnitStake = 2m;

    public static HashSet<char> ParseOptions(string? option1, string? option2)
    {
        var set = new HashSet<char>();
        Add(option1);
        Add(option2);
        return set;

        void Add(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            foreach (var part in raw.Split(new[] { ',', '，', ' ', '/', '|', '、', ';', '+' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = part.Trim()
                    .Replace("主胜", "胜", StringComparison.Ordinal)
                    .Replace("客胜", "负", StringComparison.Ordinal)
                    .Replace("主负", "负", StringComparison.Ordinal)
                    .Replace("平局", "平", StringComparison.Ordinal);
                if (t is "胜" or "3") set.Add('3');
                else if (t is "平" or "1") set.Add('1');
                else if (t is "负" or "0") set.Add('0');
            }
        }
    }

    public static string FormatOptions(HashSet<char> opts) =>
        string.Join(",", new[] { '3', '1', '0' }.Where(opts.Contains).Select(Label));

    public static string Label(char bit) => bit switch { '3' => "胜", '1' => "平", '0' => "负", _ => "" };

    /// <summary>14 场胜负彩注数。场次不是 14，或某场选项不是 1–3 个，返回 0。</summary>
    public static int FourteenBets(IReadOnlyList<int> optionCounts)
    {
        if (optionCounts.Count != 14) return 0;
        long prod = 1;
        foreach (var c in optionCounts)
        {
            if (c is < 1 or > 3) return 0;
            prod *= c;
        }
        return prod > int.MaxValue ? 0 : (int)prod;
    }

    /// <summary>任选九注数：每个 9 场子集连乘该子集的选项个数，再相加。</summary>
    public static int RenjiuBets(IReadOnlyList<int> optionCounts)
    {
        var n = optionCounts.Count;
        if (n is < 9 or > 14) return 0;
        if (optionCounts.Any(c => c is < 1 or > 3)) return 0;
        long sum = 0;
        Walk(0, 9, 1);
        return sum > int.MaxValue ? 0 : (int)sum;

        void Walk(int start, int left, long prod)
        {
            if (left == 0)
            {
                sum += prod;
                return;
            }
            for (var i = start; i <= n - left; i++)
                Walk(i + 1, left - 1, prod * optionCounts[i]);
        }
    }

    public static int Comb(int n, int k)
    {
        if (k < 0 || k > n) return 0;
        if (k == 0 || k == n) return 1;
        k = Math.Min(k, n - k);
        long r = 1;
        for (var i = 1; i <= k; i++)
            r = r * (n - k + i) / i;
        return (int)r;
    }

    public readonly record struct WinCounts(int Fourteen, int Thirteen, int Renjiu);

    /// <summary>
    /// 按场次号 1–14 对开奖串计数。开奖串下标 0 是第 1 场。
    /// 14 场全含正确项时一等奖 1 注；二等奖是恰好错 1 场的组合数。
    /// 任九中奖注数是「选项里含正确结果的场次」里取 9 场的组合数。
    /// </summary>
    public static WinCounts CountWins(IReadOnlyList<(int MatchNo, HashSet<char> Options)> lines, string result14)
    {
        if (result14.Length != 14) return default;
        var byNo = new Dictionary<int, HashSet<char>>();
        foreach (var (no, opts) in lines)
        {
            if (no is < 1 or > 14 || opts.Count == 0) return default;
            if (!byNo.TryAdd(no, opts)) return default;
        }

        var n14 = 0;
        var n13 = 0;
        if (byNo.Count == 14)
        {
            var good = new bool[14];
            var extra = new int[14];
            for (var i = 0; i < 14; i++)
            {
                var opts = byNo[i + 1];
                good[i] = opts.Contains(result14[i]);
                extra[i] = good[i] ? opts.Count - 1 : opts.Count;
            }
            var misses = new List<int>();
            for (var i = 0; i < 14; i++)
                if (!good[i]) misses.Add(i);
            if (misses.Count == 0)
            {
                n14 = 1;
                n13 = extra.Sum();
            }
            else if (misses.Count == 1)
                n13 = extra[misses[0]];
        }

        var goodMatches = byNo.Count(kv => kv.Value.Contains(result14[kv.Key - 1]));
        var n9 = goodMatches >= 9 ? Comb(goodMatches, 9) : 0;
        return new WinCounts(n14, n13, n9);
    }

    /// <summary>税前奖金：中奖注数 × 500.com 公布的单注奖金 × 倍数。不在这里扣税。</summary>
    public static decimal PrizeYuan(bool renjiu, WinCounts wins, decimal first, decimal second, decimal renjiuPrize, int multiple)
    {
        var m = multiple < 1 ? 1 : Math.Min(multiple, 99);
        if (renjiu)
            return wins.Renjiu <= 0 ? 0 : wins.Renjiu * renjiuPrize * m;
        if (wins.Fourteen <= 0 && wins.Thirteen <= 0) return 0;
        return (wins.Fourteen * first + wins.Thirteen * second) * m;
    }

    /// <summary>按 500.com 重算注数和金额。通过时改写请求；不通过返回给前端的原因。</summary>
    public static string? Normalize(PurchaseUpsertRequest req)
    {
        var play = req.PlayType?.Trim();
        if (play is not ("胜负彩" or "任选九"))
            return "胜负彩玩法应为 胜负彩 或 任选九";
        var expect = (req.Expect ?? "").Trim();
        if (expect.Length == 0 || expect.Any(c => !char.IsDigit(c)))
            return "胜负彩要填写期号，例如 26155";

        var mult = req.Multiple < 1 ? 1 : Math.Min(req.Multiple, 99);
        var parsed = new List<(PurchaseLineRecord Line, int No, HashSet<char> Opts)>();
        var seen = new HashSet<int>();
        foreach (var line in req.Lines)
        {
            var blank = string.IsNullOrWhiteSpace(line.MatchNo)
                        && string.IsNullOrWhiteSpace(line.Option1)
                        && string.IsNullOrWhiteSpace(line.Option2);
            if (blank) continue;
            if (!int.TryParse(line.MatchNo?.Trim(), out var no) || no is < 1 or > 14)
                return "胜负彩场次号要是 1 到 14";
            if (!seen.Add(no))
                return $"场次 {no} 重复了";
            var opts = ParseOptions(line.Option1, line.Option2);
            if (opts.Count == 0)
                return $"第 {no} 场至少选一个胜、平或负";
            line.Handicap = null;
            line.Option1 = FormatOptions(opts);
            line.Option2 = null;
            line.Odds1 = null;
            line.Odds2 = null;
            parsed.Add((line, no, opts));
        }

        var counts = parsed.OrderBy(p => p.No).Select(p => p.Opts.Count).ToList();
        int bets;
        if (play == "胜负彩")
        {
            if (parsed.Count != 14)
                return "胜负彩要 14 场都选，每场至少一个。9 到 13 场请改任选九";
            bets = FourteenBets(counts);
        }
        else
        {
            if (parsed.Count is < 9 or > 14)
                return "任选九要 9 到 14 场";
            bets = RenjiuBets(counts);
        }
        if (bets <= 0)
            return "注数算不出来";

        req.Board = Boards.Sfc;
        req.Expect = expect;
        req.PlayType = play;
        req.PassType = null;
        req.Multiple = mult;
        req.BetCount = bets;
        req.BetAmount = bets * UnitStake * mult;
        req.Lines = parsed.OrderBy(p => p.No).Select(p => p.Line).ToList();
        return null;
    }
}

/// <summary>500.com 胜负彩一期开奖：14 位 3/1/0，以及三个奖级的税前单注奖金。</summary>
public sealed class SfcDraw
{
    public string Expect { get; init; } = "";
    public string Result { get; init; } = "";
    public decimal FirstPrize { get; init; }
    public decimal SecondPrize { get; init; }
    public decimal RenjiuPrize { get; init; }
    public bool Complete { get; init; }
}
