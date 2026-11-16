namespace AiFootBall.Core.Models;

/// <summary>
/// 保存前投注校验（纯函数）：彩种 + 玩法 + 形态。失败不得写库。
/// 缺省玩法按彩种契约解析，不随意落成「单式」。
/// 本轮用户票只支持可完整保存且按单注计价的玩法；复式/胆拖因拖码与金额无法可靠表达而拒绝。
/// </summary>
public static class LottoBetValidator
{
    public const string UnsupportedMultiBetMessage =
        "本轮用户票不支持复式/胆拖保存：拖码无法落库、多注金额无法按 2 元/注可靠计价。请拆成单式（或快乐8选十）后再存。";

    /// <summary>空白玩法 → 彩种默认；非空白原样返回（再交给校验，未知玩法拒绝）。</summary>
    public static string NormalizePlayForLottery(string? lotteryCode, string? playType)
    {
        if (!string.IsNullOrWhiteSpace(playType))
            return playType.Trim();
        return DefaultPlay(LottoCodes.Normalize(lotteryCode));
    }

    public static string DefaultPlay(string? lotteryCode) => LottoCodes.Normalize(lotteryCode) switch
    {
        LottoCodes.Kl8 => "选十",
        LottoCodes.Fc3d or LottoCodes.Pl3 or LottoCodes.Pl5 => "直选",
        _ => "单式"
    };

    public static bool TryValidate(
        string? lotteryCode,
        string? playType,
        IReadOnlyList<int> reds,
        IReadOnlyList<int> blues,
        IReadOnlyList<int>? tuoReds,
        out string normalizedPlay,
        out string error)
    {
        normalizedPlay = "";
        error = "";
        var code = LottoCodes.Normalize(lotteryCode);
        if (string.IsNullOrEmpty(code) || !LottoCodes.All.Contains(code, StringComparer.OrdinalIgnoreCase))
        {
            error = $"不支持的彩种：{lotteryCode}";
            return false;
        }

        if (reds.Count == 0)
        {
            error = "主号码不能为空";
            return false;
        }

        // 先规范化（仅补默认），再校验；不把其它彩种的未知玩法改写成合法值。
        var incoming = NormalizePlayForLottery(code, playType);

        if (LooksLikeUnsupportedMultiBet(incoming) || (tuoReds is { Count: > 0 }))
            return Fail(out normalizedPlay, out error, UnsupportedMultiBetMessage);

        return code switch
        {
            LottoCodes.Ssq => ValidateSsq(incoming, reds, blues, out normalizedPlay, out error),
            LottoCodes.Dlt => ValidateDlt(incoming, reds, blues, out normalizedPlay, out error),
            LottoCodes.Kl8 => ValidateKl8(incoming, reds, blues, out normalizedPlay, out error),
            LottoCodes.Fc3d or LottoCodes.Pl3 => ValidateDigit3(code, incoming, reds, blues, out normalizedPlay, out error),
            LottoCodes.Pl5 => ValidatePl5(incoming, reds, blues, out normalizedPlay, out error),
            _ => Fail(out normalizedPlay, out error, $"彩种 {code} 无投注契约")
        };
    }

    private static bool LooksLikeUnsupportedMultiBet(string play) =>
        play.Contains("复式", StringComparison.Ordinal)
        || play.Contains("胆拖", StringComparison.Ordinal)
        || play.Contains("四胆", StringComparison.Ordinal);

    private static bool ValidateSsq(string playType, IReadOnlyList<int> reds, IReadOnlyList<int> blues,
        out string play, out string error)
    {
        play = MatchAllowed(playType, ["单式"]);
        if (play == "")
            return Fail(out play, out error, "双色球用户票仅支持玩法：单式；未知玩法已拒绝");

        if (blues.Count != 1)
            return Fail(out play, out error, "双色球须恰好 1 个蓝球（1–16）");
        if (blues[0] is < 1 or > 16)
            return Fail(out play, out error, "蓝球超出范围 1–16");
        if (reds.Count != 6)
            return Fail(out play, out error, "双色球单式须恰好 6 个红球");
        if (!AllUniqueInRange(reds, 1, 33, out error))
            return Fail(out play, out error, "双色球红球：" + error);
        error = "";
        return true;
    }

    private static bool ValidateDlt(string playType, IReadOnlyList<int> reds, IReadOnlyList<int> blues,
        out string play, out string error)
    {
        play = MatchAllowed(playType, ["单式"]);
        if (play == "")
            return Fail(out play, out error, "大乐透用户票仅支持玩法：单式；未知玩法已拒绝（复式/胆拖本轮不保存）");

        if (blues.Count != 2)
            return Fail(out play, out error, "大乐透后区须恰好 2 个号（1–12）");
        if (!AllUniqueInRange(blues, 1, 12, out error))
            return Fail(out play, out error, "后区：" + error);
        if (reds.Count != 5)
            return Fail(out play, out error, "大乐透单式前区须恰好 5 个号");
        if (!AllUniqueInRange(reds, 1, 35, out error))
            return Fail(out play, out error, "前区：" + error);
        error = "";
        return true;
    }

    private static bool ValidateKl8(string playType, IReadOnlyList<int> reds, IReadOnlyList<int> blues,
        out string play, out string error)
    {
        play = MatchAllowed(playType, ["选十"]);
        if (play == "")
            return Fail(out play, out error, "快乐8仅支持玩法：选十（不得提交单式/其它玩法）；未知玩法已拒绝");
        if (blues.Count != 0)
            return Fail(out play, out error, "快乐8选十无蓝球");
        if (reds.Count != 10)
            return Fail(out play, out error, "快乐8选十须恰好 10 个号");
        if (!AllUniqueInRange(reds, 1, 80, out error))
            return Fail(out play, out error, "快乐8：" + error);
        error = "";
        return true;
    }

    private static bool ValidateDigit3(string code, string playType, IReadOnlyList<int> reds, IReadOnlyList<int> blues,
        out string play, out string error)
    {
        play = MatchAllowed(playType, ["直选", "组选三", "组选六"]);
        if (play == "")
            return Fail(out play, out error, $"{LottoCodes.DisplayName(code)}仅支持：直选、组选三、组选六；未知玩法已拒绝");
        if (blues.Count != 0)
            return Fail(out play, out error, "数字型无蓝球");

        if (play == "直选")
        {
            if (reds.Count != 3)
                return Fail(out play, out error, "直选须恰好 3 位");
            if (reds.Any(d => d is < 0 or > 9))
                return Fail(out play, out error, "直选各位须为 0–9");
            error = "";
            return true;
        }

        if (play == "组选六")
        {
            if (reds.Count != 3 || reds.Distinct().Count() != 3 || reds.Any(d => d is < 0 or > 9))
                return Fail(out play, out error, "组选六须三位且互不相同（0–9）");
            error = "";
            return true;
        }

        if (!LottoHitMath.TryNormalizeGroupThreeBet(reds, out _, out error))
            return Fail(out play, out error, error);
        error = "";
        return true;
    }

    private static bool ValidatePl5(string playType, IReadOnlyList<int> reds, IReadOnlyList<int> blues,
        out string play, out string error)
    {
        play = MatchAllowed(playType, ["直选"]);
        if (play == "")
            return Fail(out play, out error, "排列五仅支持玩法：直选；未知玩法已拒绝");
        if (blues.Count != 0)
            return Fail(out play, out error, "排列五无蓝球");
        if (reds.Count != 5)
            return Fail(out play, out error, "排列五直选须恰好 5 位");
        if (reds.Any(d => d is < 0 or > 9))
            return Fail(out play, out error, "排列五各位须为 0–9");
        error = "";
        return true;
    }

    /// <summary>精确匹配允许列表（Equals），不做子串宽松放行。</summary>
    private static string MatchAllowed(string playType, string[] allowed)
    {
        foreach (var a in allowed)
        {
            if (playType.Equals(a, StringComparison.Ordinal))
                return a;
        }
        return "";
    }

    private static bool AllUniqueInRange(IReadOnlyList<int> nums, int lo, int hi, out string error)
    {
        error = "";
        if (nums.Distinct().Count() != nums.Count)
        {
            error = "号码不得重复";
            return false;
        }
        if (nums.Any(n => n < lo || n > hi))
        {
            error = $"号码须在 {lo}–{hi}";
            return false;
        }
        return true;
    }

    private static bool Fail(out string play, out string error, string msg)
    {
        play = "";
        error = msg;
        return false;
    }
}
