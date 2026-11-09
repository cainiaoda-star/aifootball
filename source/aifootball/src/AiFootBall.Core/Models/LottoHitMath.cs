namespace AiFootBall.Core.Models;

/// <summary>数字彩复盘：把推荐号码和开奖对上，标出开出来的那些。</summary>
public static class LottoHitMath
{
    public sealed class Grade
    {
        public bool[] RedHit { get; init; } = [];
        public bool[] BlueHit { get; init; } = [];
        public bool[] DrawRedHit { get; init; } = [];
        public bool[] DrawBlueHit { get; init; } = [];
        public int RedHits { get; init; }
        public int BlueHits { get; init; }
        public string Note { get; init; } = "";
    }

    /// <summary>
    /// 福彩3D、排列三的开奖是百、十、个三位。排列五是万、千、百、十、个。
    /// 采集行里 3D 和排列三后面还跟着别的号，开奖只取前面这几位。
    /// </summary>
    public static List<int> OfficialDigits(string? lotteryCode, IReadOnlyList<int> reds)
    {
        var n = LottoCodes.IsDigitGame(lotteryCode ?? "")
            ? (LottoCodes.Pl5.Equals(lotteryCode, StringComparison.OrdinalIgnoreCase) ? 5 : 3)
            : reds.Count;
        return reds.Take(n).ToList();
    }

    public static Grade Mark(
        string? playType,
        IReadOnlyList<int> pickReds,
        IReadOnlyList<int> pickBlues,
        IReadOnlyList<int> drawReds,
        IReadOnlyList<int> drawBlues,
        string? lotteryCode = null)
    {
        if (LottoCodes.IsDigitGame(lotteryCode ?? ""))
            return ByPlace(pickReds, OfficialDigits(lotteryCode, drawReds));

        var play = playType ?? "";
        if (play.Contains("直选", StringComparison.Ordinal))
            return Positional(pickReds, drawReds);
        if (play.Contains("组选", StringComparison.Ordinal))
            return Bag(pickReds, drawReds, "号码中");
        if (play.Contains("四胆全拖", StringComparison.Ordinal))
            return DanTuo(pickReds, pickBlues, drawReds, drawBlues);
        return Sets(pickReds, pickBlues, drawReds, drawBlues, play.Contains("选十", StringComparison.Ordinal));
    }

    private static Grade ByPlace(IReadOnlyList<int> pick, IReadOnlyList<int> draw)
    {
        var grade = Positional(pick, draw);
        var names = draw.Count >= 5
            ? new[] { "万位", "千位", "百位", "十位", "个位" }
            : new[] { "百位", "十位", "个位" };
        var hit = new List<string>();
        for (var i = 0; i < grade.RedHit.Length && i < names.Length; i++)
        {
            if (grade.RedHit[i]) hit.Add(names[i]);
        }

        var note = hit.Count == 0 ? "个十百位都没对上" : string.Join("、", hit) + "中";
        if (draw.Count >= 5 && hit.Count == 0) note = "万千百十个都没对上";
        return new Grade
        {
            RedHit = grade.RedHit,
            DrawRedHit = grade.DrawRedHit,
            RedHits = grade.RedHits,
            Note = note
        };
    }

    private static Grade Positional(IReadOnlyList<int> pick, IReadOnlyList<int> draw)
    {
        var redHit = new bool[pick.Count];
        var drawHit = new bool[draw.Count];
        var n = 0;
        for (var i = 0; i < pick.Count && i < draw.Count; i++)
        {
            if (pick[i] != draw[i]) continue;
            redHit[i] = true;
            drawHit[i] = true;
            n++;
        }

        return new Grade
        {
            RedHit = redHit,
            DrawRedHit = drawHit,
            RedHits = n,
            Note = $"位置中 {n}/{pick.Count}"
        };
    }

    private static Grade Bag(IReadOnlyList<int> pick, IReadOnlyList<int> draw, string label)
    {
        var bag = draw.ToList();
        var redHit = new bool[pick.Count];
        var drawHit = new bool[draw.Count];
        var used = new bool[draw.Count];
        var n = 0;
        for (var i = 0; i < pick.Count; i++)
        {
            var at = -1;
            for (var j = 0; j < bag.Count; j++)
            {
                if (used[j] || bag[j] != pick[i]) continue;
                at = j;
                break;
            }
            if (at < 0) continue;
            used[at] = true;
            redHit[i] = true;
            drawHit[at] = true;
            n++;
        }

        return new Grade
        {
            RedHit = redHit,
            DrawRedHit = drawHit,
            RedHits = n,
            Note = $"{label} {n}/{pick.Count}"
        };
    }

    private static Grade DanTuo(
        IReadOnlyList<int> dan, IReadOnlyList<int> blues,
        IReadOnlyList<int> drawReds, IReadOnlyList<int> drawBlues)
    {
        var reds = SetSide(dan, drawReds);
        var back = SetSide(blues, drawBlues);
        return new Grade
        {
            RedHit = reds.PickHit,
            BlueHit = back.PickHit,
            DrawRedHit = reds.DrawHit,
            DrawBlueHit = back.DrawHit,
            RedHits = reds.Hits,
            BlueHits = back.Hits,
            Note = $"胆中 {reds.Hits}/{dan.Count}，后区中 {back.Hits}/{blues.Count}"
        };
    }

    private static Grade Sets(
        IReadOnlyList<int> pickReds, IReadOnlyList<int> pickBlues,
        IReadOnlyList<int> drawReds, IReadOnlyList<int> drawBlues,
        bool selectTen)
    {
        var reds = SetSide(pickReds, drawReds);
        var blues = SetSide(pickBlues, drawBlues);
        var note = selectTen
            ? $"中 {reds.Hits}/{pickReds.Count}"
            : pickBlues.Count == 0
                ? $"红球中 {reds.Hits}/{pickReds.Count}"
                : $"红球中 {reds.Hits}/{pickReds.Count}，蓝球中 {blues.Hits}/{pickBlues.Count}";
        return new Grade
        {
            RedHit = reds.PickHit,
            BlueHit = blues.PickHit,
            DrawRedHit = reds.DrawHit,
            DrawBlueHit = blues.DrawHit,
            RedHits = reds.Hits,
            BlueHits = blues.Hits,
            Note = note
        };
    }

    private static (bool[] PickHit, bool[] DrawHit, int Hits) SetSide(IReadOnlyList<int> pick, IReadOnlyList<int> draw)
    {
        var drawn = draw.ToHashSet();
        var picked = pick.ToHashSet();
        var pickHit = new bool[pick.Count];
        var drawHit = new bool[draw.Count];
        var n = 0;
        for (var i = 0; i < pick.Count; i++)
        {
            if (!drawn.Contains(pick[i])) continue;
            pickHit[i] = true;
            n++;
        }
        for (var i = 0; i < draw.Count; i++)
            drawHit[i] = picked.Contains(draw[i]);
        return (pickHit, drawHit, n);
    }
}
