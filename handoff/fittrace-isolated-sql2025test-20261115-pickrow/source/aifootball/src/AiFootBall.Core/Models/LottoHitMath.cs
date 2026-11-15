namespace AiFootBall.Core.Models;

/// <summary>数字彩复盘：按玩法对奖。结果带结构化 Status，不以 Note 文案判中奖。</summary>
public static class LottoHitMath
{
    public sealed class Grade
    {
        public LottoHitStatus Status { get; init; } = LottoHitStatus.Pending;
        /// <summary>已有可靠奖级结论（Won/Lost）。奖级未判定为 false。</summary>
        public bool Determined => Status is LottoHitStatus.Won or LottoHitStatus.Lost;
        /// <summary>有号码匹配明细可展示球标（含奖级未判定）。</summary>
        public bool HasMatchDetail => LottoHitStatusRules.ShowMatchMarks(Status);
        public bool[] RedHit { get; init; } = [];
        public bool[] BlueHit { get; init; } = [];
        public bool[] DrawRedHit { get; init; } = [];
        public bool[] DrawBlueHit { get; init; } = [];
        public int RedHits { get; init; }
        public int BlueHits { get; init; }
        public string Note { get; init; } = "";
    }

    /// <summary>
    /// 已核实数字型的官方开奖位数：fc3d/pl3=3，pl5=5。其余（含七星彩）返回 0=不截断。
    /// </summary>
    public static int OfficialDigitCount(string? lotteryCode)
    {
        var code = LottoCodes.Normalize(lotteryCode);
        if (code == LottoCodes.Pl5) return 5;
        if (LottoCodes.IsDigitGame(code)) return 3;
        return 0;
    }

    public static List<int> OfficialDigits(string? lotteryCode, IReadOnlyList<int> reds)
    {
        var n = OfficialDigitCount(lotteryCode);
        if (n <= 0) return reds.ToList();
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
        var code = LottoCodes.Normalize(lotteryCode);
        if (LottoCodes.IsDigitGame(code))
            return MarkDigit(playType, pickReds, OfficialDigits(code, drawReds), code);

        var play = playType ?? "";
        if (play.Contains("直选", StringComparison.Ordinal))
            return PositionalWin(pickReds, drawReds);
        if (play.Contains("组选六", StringComparison.Ordinal))
            return GroupSix(pickReds, drawReds);
        if (play.Contains("组选三", StringComparison.Ordinal))
            return GroupThree(pickReds, drawReds);
        if (play.Contains("组选", StringComparison.Ordinal))
            return Undetermined(pickReds.Count, drawReds.Count, "无法判定（组选子类未识别）");
        if (play.Contains("四胆全拖", StringComparison.Ordinal) || play.Contains("胆拖", StringComparison.Ordinal))
            return DanTuo(pickReds, pickBlues, drawReds, drawBlues);
        // 单式 / 复式 / 选十：只报告号码匹配，奖级规则未可靠实现 → 奖级未判定（禁止全中条件判 Lost/Won）
        if (play.Contains("选十", StringComparison.Ordinal) || play.Contains("单式", StringComparison.Ordinal)
            || play.Contains("复式", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(play))
            return SetsMatchOnly(pickReds, pickBlues, drawReds, drawBlues, play.Contains("选十", StringComparison.Ordinal));

        return Undetermined(pickReds.Count, drawReds.Count, $"无法判定（未识别玩法：{play}）");
    }

    /// <summary>未开奖时的占位结果。</summary>
    public static Grade Pending(int pickRedCount = 0, int pickBlueCount = 0) => new()
    {
        Status = LottoHitStatus.Pending,
        RedHit = new bool[pickRedCount],
        BlueHit = new bool[pickBlueCount],
        Note = "还没开奖"
    };

    private static Grade MarkDigit(string? playType, IReadOnlyList<int> pick, IReadOnlyList<int> draw, string code)
    {
        var play = (playType ?? "").Trim();
        if (play.Contains("直选", StringComparison.Ordinal))
        {
            var need = OfficialDigitCount(code);
            if (need > 0 && pick.Count != need)
                return Illegal(pick.Count, draw.Count, $"投注形态不合法（直选须 {need} 位）");
            return PositionalWin(pick, draw);
        }

        if (play.Contains("组选六", StringComparison.Ordinal))
            return GroupSix(pick, draw);

        if (play.Contains("组选三", StringComparison.Ordinal))
            return GroupThree(pick, draw);

        if (play.Contains("组选", StringComparison.Ordinal))
            return Undetermined(pick.Count, draw.Count, "无法判定（组选子类未识别）");

        if (string.IsNullOrWhiteSpace(play))
            return Undetermined(pick.Count, draw.Count, "无法判定（未填写玩法）");

        return Undetermined(pick.Count, draw.Count, $"无法判定（未识别玩法：{play}）");
    }

    private static Grade PositionalWin(IReadOnlyList<int> pick, IReadOnlyList<int> draw)
    {
        var grade = Positional(pick, draw);
        var names = draw.Count >= 5
            ? (draw.Count >= 7
                ? new[] { "第一位", "第二位", "第三位", "第四位", "第五位", "第六位", "第七位" }
                : new[] { "万位", "千位", "百位", "十位", "个位" })
            : new[] { "百位", "十位", "个位" };
        var hit = new List<string>();
        for (var i = 0; i < grade.RedHit.Length && i < names.Length; i++)
        {
            if (grade.RedHit[i]) hit.Add(names[i]);
        }

        string note;
        if (draw.Count >= 7)
            note = hit.Count == 0 ? "各位都没对上" : string.Join("、", hit) + "中";
        else if (draw.Count >= 5)
            note = hit.Count == 0 ? "万千百十个都没对上" : string.Join("、", hit) + "中";
        else
            note = hit.Count == 0 ? "个十百位都没对上" : string.Join("、", hit) + "中";

        var won = pick.Count > 0 && grade.RedHits == pick.Count && pick.Count <= draw.Count;
        return new Grade
        {
            Status = won ? LottoHitStatus.Won : LottoHitStatus.Lost,
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
            Status = LottoHitStatus.Lost,
            RedHit = redHit,
            DrawRedHit = drawHit,
            RedHits = n,
            Note = $"位置中 {n}/{pick.Count}"
        };
    }

    private static Grade GroupSix(IReadOnlyList<int> pick, IReadOnlyList<int> draw)
    {
        if (pick.Count != 3 || pick.Distinct().Count() != 3)
            return Illegal(pick.Count, draw.Count, "投注形态不合法（组选六须三位且互不相同）");
        if (draw.Count != 3 || draw.Distinct().Count() != 3)
            return new Grade
            {
                Status = LottoHitStatus.Lost,
                RedHit = new bool[pick.Count],
                DrawRedHit = new bool[draw.Count],
                Note = "开奖不是组六形态，组选六未中"
            };

        var bag = Bag(pick, draw, "号码中");
        var won = bag.RedHits == 3;
        return new Grade
        {
            Status = won ? LottoHitStatus.Won : LottoHitStatus.Lost,
            RedHit = bag.RedHit,
            DrawRedHit = bag.DrawRedHit,
            RedHits = bag.RedHits,
            Note = won ? "组选六中" : $"组选六未中（对上 {bag.RedHits}/3）"
        };
    }

    /// <summary>
    /// 组选三：仅接受三位且恰为 AAB（一对重号）的票面。
    /// 中奖须与开奖号码多重集合完全一致（112 不能命中 122）。
    /// 两位 AB 在现有票面结构下无法可靠表达「两注及金额」，一律拒绝，不改库硬凑。
    /// </summary>
    private static Grade GroupThree(IReadOnlyList<int> pick, IReadOnlyList<int> draw)
    {
        if (!TryNormalizeGroupThreeBet(pick, out var betSorted, out var err))
            return Illegal(pick.Count, draw.Count, err);

        if (draw.Count != 3)
            return Illegal(pick.Count, draw.Count, "开奖位数不是三位，组选三无法对奖");

        var drawCounts = draw.GroupBy(x => x).Select(g => g.Count()).OrderByDescending(c => c).ToList();
        if (drawCounts.Count != 2 || drawCounts[0] != 2 || drawCounts[1] != 1)
            return new Grade
            {
                Status = LottoHitStatus.Lost,
                RedHit = new bool[pick.Count],
                DrawRedHit = new bool[draw.Count],
                Note = "开奖不是组三形态，组选三未中"
            };

        var drawSorted = draw.OrderBy(x => x).ToList();
        var won = betSorted.SequenceEqual(drawSorted);
        var bag = Bag(pick, draw, "号码中");
        return new Grade
        {
            Status = won ? LottoHitStatus.Won : LottoHitStatus.Lost,
            RedHit = won ? bag.RedHit : new bool[pick.Count],
            DrawRedHit = won ? bag.DrawRedHit : new bool[draw.Count],
            RedHits = won ? 3 : 0,
            Note = won ? "组选三中" : "组选三未中"
        };
    }

    /// <summary>
    /// 解析组选三：仅三位 AAB。返回升序多重集合。两位 AB 拒绝。
    /// </summary>
    public static bool TryNormalizeGroupThreeBet(IReadOnlyList<int> pick, out List<int> sortedMultiset, out string error)
    {
        sortedMultiset = new List<int>();
        error = "";
        if (pick.Count == 2)
        {
            error =
                "投注形态不合法（组选三两位 AB 在现有结构下无法可靠表达两注覆盖与金额；请改为三位 AAB 单式，如 1,1,2）";
            return false;
        }

        if (pick.Count != 3)
        {
            error = "投注形态不合法（组选三须三位且恰有一对重号，如 1,1,2）";
            return false;
        }

        if (pick.Any(d => d is < 0 or > 9))
        {
            error = "投注形态不合法（组选三号码须为 0–9）";
            return false;
        }

        var groups = pick.GroupBy(x => x).OrderByDescending(g => g.Count()).ToList();
        if (groups.Count != 2 || groups[0].Count() != 2 || groups[1].Count() != 1)
        {
            error = "投注形态不合法（三位组选三须恰有一对重号，如 1,1,2；豹子或组六请用对应玩法）";
            return false;
        }

        sortedMultiset = pick.OrderBy(x => x).ToList();
        return true;
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
            Status = LottoHitStatus.Lost,
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
        // 胆拖奖级（展开注×浮动奖）未可靠实现：只报告匹配，奖级未判定。
        return new Grade
        {
            Status = LottoHitStatus.PrizeUndetermined,
            RedHit = reds.PickHit,
            BlueHit = back.PickHit,
            DrawRedHit = reds.DrawHit,
            DrawBlueHit = back.DrawHit,
            RedHits = reds.Hits,
            BlueHits = back.Hits,
            Note = $"胆中 {reds.Hits}/{dan.Count}，后区中 {back.Hits}/{blues.Count}（奖级未判定）"
        };
    }

    private static Grade SetsMatchOnly(
        IReadOnlyList<int> pickReds, IReadOnlyList<int> pickBlues,
        IReadOnlyList<int> drawReds, IReadOnlyList<int> drawBlues,
        bool selectTen)
    {
        var reds = SetSide(pickReds, drawReds);
        var blues = SetSide(pickBlues, drawBlues);
        var match = selectTen
            ? $"号码命中 {reds.Hits}/{pickReds.Count}"
            : pickBlues.Count == 0
                ? $"红球命中 {reds.Hits}/{pickReds.Count}"
                : $"红球命中 {reds.Hits}/{pickReds.Count}，蓝球命中 {blues.Hits}/{pickBlues.Count}";
        return new Grade
        {
            Status = LottoHitStatus.PrizeUndetermined,
            RedHit = reds.PickHit,
            BlueHit = blues.PickHit,
            DrawRedHit = reds.DrawHit,
            DrawBlueHit = blues.DrawHit,
            RedHits = reds.Hits,
            BlueHits = blues.Hits,
            Note = match + "（奖级未判定）"
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

    private static Grade Undetermined(int pickLen, int drawLen, string note) => new()
    {
        Status = LottoHitStatus.Undetermined,
        RedHit = new bool[pickLen],
        DrawRedHit = new bool[drawLen],
        Note = note
    };

    private static Grade Illegal(int pickLen, int drawLen, string note) => new()
    {
        Status = LottoHitStatus.Illegal,
        RedHit = new bool[pickLen],
        DrawRedHit = new bool[drawLen],
        Note = note
    };
}
