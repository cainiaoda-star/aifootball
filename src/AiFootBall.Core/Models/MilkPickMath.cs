namespace AiFootBall.Core.Models;

/// <summary>
/// 大奶选项的判定与显示。纯函数，结算（Data 层）、引擎（Sources 层）与单测共用同一份，不能放 Sources。
/// </summary>
public static class MilkPickMath
{
    /// <summary>大奶选项是否打出。</summary>
    /// <param name="playType">BF / BQ / JQ。</param>
    /// <param name="optionCode">比分 "3:1" 或 "胜其它"、半全场 "3-0"（半-全，3胜1平0负）、总进球 "7"（7=7+）。</param>
    /// <param name="home">全场主队进球。</param>
    /// <param name="away">全场客队进球。</param>
    /// <param name="halfHome">半场主队进球（BQ 判定必需）。</param>
    /// <param name="halfAway">半场客队进球。</param>
    public static bool IsHit(string playType, string optionCode, int home, int away, int? halfHome, int? halfAway)
    {
        switch (playType)
        {
            case JcPlayTypes.Bf:
                return optionCode.EndsWith("其它", StringComparison.Ordinal)
                    ? IsOtherScore(optionCode, home, away)
                    : optionCode == $"{home}:{away}";
            case JcPlayTypes.Jq:
            {
                if (!int.TryParse(optionCode, out var n)) return false;
                return n >= 7 ? home + away >= 7 : home + away == n;
            }
            case JcPlayTypes.Bq:
            {
                if (halfHome is not int hh || halfAway is not int ha) return false;
                if (optionCode.Length != 3) return false;
                var half = hh > ha ? '3' : hh == ha ? '1' : '0';
                var full = home > away ? '3' : home == away ? '1' : '0';
                return $"{half}-{full}" == optionCode;
            }
            default:
                return false;
        }
    }

    private static bool IsOtherScore(string optionCode, int home, int away)
    {
        if (optionCode.StartsWith("胜", StringComparison.Ordinal)) return home > away && IsOutsideListed(home, away);
        if (optionCode.StartsWith("负", StringComparison.Ordinal)) return home < away && IsOutsideListed(home, away);
        if (optionCode.StartsWith("平", StringComparison.Ordinal)) return home == away && IsOutsideListed(home, away);
        return false;
    }

    /// <summary>竞彩比分玩法官方 28 个具体比分档（不含三个「其它」）。
    /// 主胜 1:0~5:2 共 12 个、平 0:0~3:3 共 4 个、客胜 0:1~2:5 共 12 个。
    /// 之前用「任一方 &gt;5 或合计 ≥8」近似，把 4:3、5:4、4:4 这类官方没列的比分漏判成不中。
    /// </summary>
    private static readonly HashSet<string> ListedScores = new(StringComparer.Ordinal)
    {
        // 主胜
        "1:0", "2:0", "2:1", "3:0", "3:1", "3:2", "4:0", "4:1", "4:2", "5:0", "5:1", "5:2",
        // 平
        "0:0", "1:1", "2:2", "3:3",
        // 客胜
        "0:1", "0:2", "1:2", "0:3", "1:3", "2:3", "0:4", "1:4", "2:4", "0:5", "1:5", "2:5"
    };

    /// <summary>该比分是否官方已列具体档。购票结算（PurchaseSettleMath）与本判定共用一份口径。</summary>
    public static bool IsListedScore(int home, int away) => ListedScores.Contains($"{home}:{away}");

    /// <summary>官方比分档只覆盖上面 28 个；之外的（4:3、5:4、6:x、合计 8+ 等）都算「其它」。</summary>
    private static bool IsOutsideListed(int home, int away) => !IsListedScore(home, away);

    /// <summary>选项显示名：比分 3:1 / 半全场 胜负 / 总进球 4球（与竞彩官方叫法一致）。</summary>
    public static string DisplayName(string playType, string code) => playType switch
    {
        JcPlayTypes.Bf => $"比分 {code}",
        JcPlayTypes.Bq => $"半全场 {HalfFullName(code)}",
        JcPlayTypes.Jq => code == "7" ? "总进球 7+球" : $"总进球 {code}球",
        _ => code
    };

    /// <summary>列表里用短名：玩法已经在分组标题上，选项列不用再重复一遍「半全场」。</summary>
    public static string ShortName(string playType, string code) => playType switch
    {
        JcPlayTypes.Bf => code,
        JcPlayTypes.Bq => HalfFullName(code),
        JcPlayTypes.Jq => code == "7" ? "7+球" : $"{code}球",
        _ => code
    };

    public static string HalfFullName(string code) => code.Length == 3
        ? SideName(code[0]) + SideName(code[2])
        : code;

    private static string SideName(char c) => c switch
    {
        '3' => "胜",
        '1' => "平",
        '0' => "负",
        _ => c.ToString()
    };
}
