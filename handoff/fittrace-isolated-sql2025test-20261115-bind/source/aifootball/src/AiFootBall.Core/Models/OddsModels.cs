namespace AiFootBall.Core.Models;

/// <summary>赔率玩法常量</summary>
public static class OddsTypes
{
    /// <summary>欧赔（胜平负）</summary>
    public const string Eu = "EU";
    /// <summary>亚盘（让球）</summary>
    public const string Ah = "AH";
    /// <summary>大小球</summary>
    public const string Ou = "OU";
}

/// <summary>赔率记录（对应 dbo.FbOdds）。三种玩法共用一张表，按 OddsType 区分。</summary>
public sealed class OddsRecord
{
    public long MatchId { get; set; }

    /// <summary>500.com 公司编号；0 表示页面给出的平均值</summary>
    public int CompanyId { get; set; }

    public string Company { get; set; } = "";

    /// <summary>EU / AH / OU</summary>
    public string OddsType { get; set; } = OddsTypes.Eu;

    /// <summary>true=初盘，false=终盘（未开赛时为最新即时盘）</summary>
    public bool IsOpening { get; set; }

    /* 欧赔 */
    public decimal? HomeWin { get; set; }
    public decimal? Draw { get; set; }
    public decimal? AwayWin { get; set; }
    public decimal? ReturnRate { get; set; }
    public decimal? KellyWin { get; set; }
    public decimal? KellyDraw { get; set; }
    public decimal? KellyLoss { get; set; }

    /* 亚盘：负值表示主队让球 */
    public decimal? Handicap { get; set; }
    public decimal? HomeWater { get; set; }
    public decimal? AwayWater { get; set; }

    /* 大小球 */
    public decimal? TotalLine { get; set; }
    public decimal? OverWater { get; set; }
    public decimal? UnderWater { get; set; }

    /// <summary>是否有一条有效数值，用于过滤空行（大小球 0 盘口是页面占位，不算有效）</summary>
    public bool HasValue =>
        HomeWin.HasValue || Draw.HasValue || AwayWin.HasValue ||
        Handicap.HasValue || (TotalLine.HasValue && TotalLine.Value > 0);
}

/// <summary>单场赔率采集结果</summary>
public sealed class MatchOddsResult
{
    public long MatchId { get; init; }
    public IReadOnlyList<OddsRecord> Odds { get; init; } = Array.Empty<OddsRecord>();

    /// <summary>1=有赔率 0=页面无赔率数据 2=抓取失败</summary>
    public int Status { get; init; }

    public int CompanyCount { get; init; }
    public string? Error { get; init; }
}

/// <summary>赔率时间序列点（对应 dbo.FbOddsHistory）。由每次采样时的「即时盘」落库形成轨迹。</summary>
public sealed class OddsHistoryPoint
{
    public long MatchId { get; set; }
    public DateTime CapturedAt { get; set; }
    public int? MinutesToKick { get; set; }
    /// <summary>0=赛前 1=临场(≤60min) 2=滚球/赛后</summary>
    public byte Phase { get; set; }
    public string OddsType { get; set; } = OddsTypes.Eu;
    public int CompanyId { get; set; }
    public string Company { get; set; } = "";

    public decimal? HomeWin { get; set; }
    public decimal? Draw { get; set; }
    public decimal? AwayWin { get; set; }
    public decimal? ReturnRate { get; set; }
    public decimal? KellyWin { get; set; }
    public decimal? KellyDraw { get; set; }
    public decimal? KellyLoss { get; set; }

    public decimal? Handicap { get; set; }
    public decimal? HomeWater { get; set; }
    public decimal? AwayWater { get; set; }

    public decimal? TotalLine { get; set; }
    public decimal? OverWater { get; set; }
    public decimal? UnderWater { get; set; }
}
