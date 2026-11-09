namespace AiFootBall.Core.Models;

/// <summary>必发（Betfair）成交快照（对应 dbo.FbExchange）。</summary>
public sealed class ExchangeSnapshot
{
    public long MatchId { get; set; }
    public DateTime CapturedAt { get; set; }
    public int? MinutesToKick { get; set; }

    /* 百家欧指 */
    public decimal? EuroHome { get; set; }
    public decimal? EuroDraw { get; set; }
    public decimal? EuroAway { get; set; }
    public decimal? EuroProbHome { get; set; }
    public decimal? EuroProbDraw { get; set; }
    public decimal? EuroProbAway { get; set; }

    /* 必发成交价 / 成交量 / 庄家盈亏 */
    public decimal? PriceHome { get; set; }
    public decimal? PriceDraw { get; set; }
    public decimal? PriceAway { get; set; }
    public decimal? VolumeHome { get; set; }
    public decimal? VolumeDraw { get; set; }
    public decimal? VolumeAway { get; set; }
    public decimal? PnlHome { get; set; }
    public decimal? PnlDraw { get; set; }
    public decimal? PnlAway { get; set; }

    /* 交易比例 */
    public decimal? RatioHome { get; set; }
    public decimal? RatioDraw { get; set; }
    public decimal? RatioAway { get; set; }
    public decimal? BdRatioHome { get; set; }
    public decimal? BdRatioDraw { get; set; }
    public decimal? BdRatioAway { get; set; }

    /* 指数分析 */
    public decimal? BfIdxHome { get; set; }
    public decimal? BfIdxDraw { get; set; }
    public decimal? BfIdxAway { get; set; }
    public decimal? HotIdxHome { get; set; }
    public decimal? HotIdxDraw { get; set; }
    public decimal? HotIdxAway { get; set; }
    public decimal? PnlIdxHome { get; set; }
    public decimal? PnlIdxDraw { get; set; }
    public decimal? PnlIdxAway { get; set; }

    /* 规模 */
    public decimal? TotalVolume { get; set; }
    public decimal? BigTotal { get; set; }
    public decimal? BigHome { get; set; }
    public decimal? BigDraw { get; set; }
    public decimal? BigAway { get; set; }

    public string? TipScale { get; set; }
    public string? TipLean { get; set; }

    /// <summary>是否抓到任何成交量，用于判定「该场有无必发盘」</summary>
    public bool HasValue => VolumeHome.HasValue || VolumeDraw.HasValue || VolumeAway.HasValue;
}

/// <summary>必发大额交易明细（对应 dbo.FbExchangeTrade）。</summary>
public sealed class ExchangeTrade
{
    public long MatchId { get; set; }
    public DateTime TradeTime { get; set; }
    /// <summary>H=主 D=平 A=客</summary>
    public string Side { get; set; } = "H";
    /// <summary>买 / 卖</summary>
    public string Direction { get; set; } = "";
    public decimal Volume { get; set; }
    public decimal? Ratio { get; set; }
    public DateTime CapturedAt { get; set; }
}

/// <summary>单场必发采集结果</summary>
public sealed class MatchExchangeResult
{
    public long MatchId { get; init; }
    public ExchangeSnapshot? Snapshot { get; init; }
    public IReadOnlyList<ExchangeTrade> Trades { get; init; } = Array.Empty<ExchangeTrade>();

    /// <summary>1=有必发数据 0=该场无必发盘 2=抓取失败</summary>
    public int Status { get; init; }
    public string? Error { get; init; }
}
