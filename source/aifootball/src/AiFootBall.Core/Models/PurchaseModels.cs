namespace AiFootBall.Core.Models;

/// <summary>购买方案（自购方案主表，对应 dbo.Purchase）</summary>
public sealed class PurchaseRecord
{
    public long PurchaseId { get; set; }
    public string Board { get; set; } = Boards.Jc;
    public string? Expect { get; set; }
    /// <summary>玩法：胜平负 / 让球胜平负 / 全场比分 / 总进球</summary>
    public string? PlayType { get; set; }
    /// <summary>过关方式：单关 / 2串1 / 2串1#3串1</summary>
    public string? PassType { get; set; }
    public int Multiple { get; set; } = 1;
    public int? BetCount { get; set; }
    public decimal BetAmount { get; set; }
    public decimal? ExpectedWin { get; set; }
    public decimal? ActualWin { get; set; }
    /// <summary>PENDING待出票 / WAIT待开奖 / WON中奖 / LOST未中 / CANCELLED撤销</summary>
    public string Status { get; set; } = "PENDING";
    public DateTime BuyDate { get; set; } = DateTime.Today;
    public string? TicketImage { get; set; }
    public string? ResultImage { get; set; }
    public bool? IsHit { get; set; }
    public string? HitLevel { get; set; }
    public string? Remark { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    /// <summary>方案内场次明细（读写时附加，不直接映射单表列）</summary>
    public List<PurchaseLineRecord> Lines { get; set; } = new();
}

/// <summary>方案内场次明细（对应 dbo.PurchaseLine）</summary>
public sealed class PurchaseLineRecord
{
    public long LineId { get; set; }
    public long PurchaseId { get; set; }
    public int Seq { get; set; }
    public string? MatchNo { get; set; }
    public long? MatchId { get; set; }
    public string? LeagueName { get; set; }
    public string? HomeTeam { get; set; }
    public string? AwayTeam { get; set; }
    public DateTime? MatchTime { get; set; }
    public decimal? Handicap { get; set; }
    /// <summary>投注选项1（如 比分 2:0 / 主胜）</summary>
    public string? Option1 { get; set; }
    /// <summary>投注选项2（如 3:1）</summary>
    public string? Option2 { get; set; }
    public decimal? Odds1 { get; set; }
    public decimal? Odds2 { get; set; }
    public string? ActualScore { get; set; }
    public int? ActualGoals { get; set; }
    public char? ActualResult { get; set; }
    public bool? LineHit { get; set; }
}

/// <summary>购买方案录入请求（前端提交，含明细）</summary>
public sealed class PurchaseUpsertRequest
{
    public long? PurchaseId { get; set; }
    public string Board { get; set; } = Boards.Jc;
    public string? Expect { get; set; }
    public string? PlayType { get; set; }
    public string? PassType { get; set; }
    public int Multiple { get; set; } = 1;
    public int? BetCount { get; set; }
    public decimal BetAmount { get; set; }
    public decimal? ExpectedWin { get; set; }
    public decimal? ActualWin { get; set; }
    public string? Status { get; set; }
    public DateTime? BuyDate { get; set; }
    public string? TicketImage { get; set; }
    public string? ResultImage { get; set; }
    public bool? IsHit { get; set; }
    public string? HitLevel { get; set; }
    public string? Remark { get; set; }
    public List<PurchaseLineRecord> Lines { get; set; } = new();
}

/// <summary>开奖结果录入请求</summary>
public sealed class PurchaseSettleRequest
{
    public long PurchaseId { get; set; }
    public decimal? ActualWin { get; set; }
    public bool IsHit { get; set; }
    public string? HitLevel { get; set; }
    public string? ResultImage { get; set; }
    public string? Remark { get; set; }
    /// <summary>结算时纠正玩法（如让球票被误标成全场比分）</summary>
    public string? PlayType { get; set; }
    /// <summary>按场次序号回填的实际比分（可选）</summary>
    public List<PurchaseLineSettle> Lines { get; set; } = new();
}

public sealed class PurchaseLineSettle
{
    public int Seq { get; set; }
    public string? ActualScore { get; set; }
    public int? ActualGoals { get; set; }
    public char? ActualResult { get; set; }
    public bool? LineHit { get; set; }
}
