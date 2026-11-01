namespace AiFootBall.Core.Models;

/// <summary>数字彩支持的推荐彩种。</summary>
public static class LottoCodes
{
    public const string Ssq = "ssq";   // 双色球（福利彩票）
    public const string Dlt = "dlt";   // 大乐透
    public const string Fc3d = "fc3d"; // 福彩3D
    public const string Kl8 = "kl8";   // 快乐8，每天开奖，选十
    public const string Pl3 = "pl3";   // 排列三
    public const string Pl5 = "pl5";   // 排列五

    public static readonly string[] All = [Ssq, Dlt, Fc3d, Kl8, Pl3, Pl5];

    public static string DisplayName(string code) => code.ToLowerInvariant() switch
    {
        Ssq => "双色球",
        Dlt => "大乐透",
        Fc3d => "福彩3D",
        Kl8 => "快乐8",
        Pl3 => "排列三",
        Pl5 => "排列五",
        _ => code
    };

    public static bool IsLottoCombo(string code) =>
        code is Ssq or Dlt;

    public static bool IsDigitGame(string code) =>
        code is Fc3d or Pl3 or Pl5;

    public static bool IsKl8(string code) =>
        code.Equals(Kl8, StringComparison.OrdinalIgnoreCase);

    public static bool IsDlt(string code) =>
        code.Equals(Dlt, StringComparison.OrdinalIgnoreCase);
}

/// <summary>一注选号方案。</summary>
public sealed class LottoPick
{
    public int RankNo { get; set; }
    public bool IsPrimary { get; set; }
    /// <summary>单式 / 直选 / 组选三 / 组选六</summary>
    public string PlayType { get; set; } = "单式";
    public List<int> Reds { get; set; } = new();
    public List<int> Blues { get; set; } = new();
    /// <summary>胆拖的拖码。四胆全拖时为前区除胆以外的全部号码，不入库。</summary>
    public List<int> TuoReds { get; set; } = new();
    /// <summary>胆拖展开后的注数。单式为空。</summary>
    public int? BetCount { get; set; }
    public decimal Score { get; set; }
    public string SignalDesc { get; set; } = "";

    public string RedText => string.Join(" ", Reds.Select(FormatBall));
    public string BlueText => Blues.Count == 0 ? "" : string.Join(" ", Blues.Select(FormatBall));
    public string RedCsv => string.Join(",", Reds.Select(x => x.ToString("D2")));
    public string? BlueCsv => Blues.Count == 0 ? null : string.Join(",", Blues.Select(x => x.ToString("D2")));

    private static string FormatBall(int n) => n < 10 ? n.ToString("D2") : n.ToString();
}

/// <summary>某彩种某期的整套方案（主推 + 备选）。</summary>
public sealed class LottoPlanSet
{
    public string LotteryCode { get; set; } = "";
    public string LotteryName { get; set; } = "";
    public string TargetIssue { get; set; } = "";
    public string? BasedOnIssue { get; set; }
    public DateOnly RecDate { get; set; }
    public List<LottoPick> Picks { get; set; } = new();
    public string Summary { get; set; } = "";
}

/// <summary>落库记录。</summary>
public sealed class LottoRecommendationRecord
{
    public long RecId { get; set; }
    public string LotteryCode { get; set; } = "";
    public string TargetIssue { get; set; } = "";
    public string? BasedOnIssue { get; set; }
    public int RankNo { get; set; }
    public bool IsPrimary { get; set; }
    public string PlayType { get; set; } = "";
    public string RedNumbers { get; set; } = "";
    public string? BlueNumbers { get; set; }
    public decimal? Score { get; set; }
    public string? SignalDesc { get; set; }
    public DateTime RecDate { get; set; }
    public DateTime CreatedAt { get; set; }
}
