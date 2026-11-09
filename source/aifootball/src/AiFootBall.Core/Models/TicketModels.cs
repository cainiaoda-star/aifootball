namespace AiFootBall.Core.Models;

/// <summary>票内一腿：选了哪个让球选项、什么水位、模型给的概率。</summary>
public sealed class TicketLegDraft
{
    public int Seq { get; set; }
    public long? MatchId { get; set; }
    public string? MatchNo { get; set; }
    public string? HomeTeam { get; set; }
    public string? AwayTeam { get; set; }
    public string? PickOption { get; set; }
    /// <summary>让球方向：3=让胜 / 1=让平 / 0=让负。结算靠它 + 让球数，不依赖推荐表。</summary>
    public char? PickSide { get; set; }
    /// <summary>开具这张票时该场的让球数。赛后用它把比分折成让球结果。</summary>
    public decimal? Handicap { get; set; }
    /// <summary>
    /// 玩法代码（BF/BQ/JQ）。大奶票腿不是让球方向，判定走比分/半场比分/总进球；
    /// NULL = 老的让球腿，维持原判定逻辑。
    /// </summary>
    public string? PlayType { get; set; }
    /// <summary>玩法内的选项代码（比分 "3:1"、半全场 "3-0"、总进球 "7"）。与 PickOption 显示名分开落库。</summary>
    public string? PickCode { get; set; }
    public decimal? Odds { get; set; }
    /// <summary>
    /// 票面用来算奖金概率的那条腿概率（%）。取「模型」和「盘口」两者较低值，
    /// 因为两者都不含经验校准，谁也不能自称准，用乐观的一边算回本率会高估。
    /// </summary>
    public decimal? LegProbability { get; set; }
    /// <summary>模型概率（%，引擎用欧赔去水 + 大小球折算让球）。选腿的 55% 门槛按它判。</summary>
    public decimal? ModelProbability { get; set; }
    /// <summary>盘口概率（%，竞彩让球三项直接去水）。留着才能事后对比模型和盘口谁准。</summary>
    public decimal? MarketProbability { get; set; }
}

/// <summary>一张待落库的票。概率来自 JcCoverTicket.Plan，与页面上显示的是同一份计算。</summary>
public sealed class TicketDraft
{
    public DateTime PlanDate { get; set; }
    public string Board { get; set; } = "";
    public string PassType { get; set; } = "";
    public int LegCount { get; set; }
    public int BetCount { get; set; }
    public int Multiple { get; set; } = 1;
    public decimal BetAmount { get; set; }
    /// <summary>票面中奖率（%）：至少有一注中奖的概率。</summary>
    public decimal? HitProbability { get; set; }
    /// <summary>票面回本率（%）：奖金不低于本金的概率。串关这两个数会明显分叉。</summary>
    public decimal? BackProbability { get; set; }
    /// <summary>期望回报（元/元）。竞彩返还率 0.905 下必然小于 1。</summary>
    public decimal? ExpectedReturn { get; set; }
    public List<TicketLegDraft> Legs { get; set; } = new();
}

/// <summary>库里读回来的票。</summary>
public sealed class TicketRecord
{
    public long TicketId { get; set; }
    public DateTime PlanDate { get; set; }
    public string Board { get; set; } = "";
    public string PassType { get; set; } = "";
    public int LegCount { get; set; }
    public int BetCount { get; set; }
    public int Multiple { get; set; }
    public decimal BetAmount { get; set; }
    public decimal? HitProbability { get; set; }
    public decimal? BackProbability { get; set; }
    public decimal? ExpectedReturn { get; set; }
    public string Status { get; set; } = "";
    public int? HitLegCount { get; set; }
    public decimal? PrizeAmount { get; set; }
    public DateTime? SettledAt { get; set; }
    public List<TicketLegRecord> Legs { get; set; } = new();

    public bool HasPrize => PrizeAmount is > 0;
    /// <summary>回本：奖金盖过本金。串关里「中奖」和「回本」不是一回事。</summary>
    public bool Backed => (PrizeAmount ?? 0) >= BetAmount;
    public decimal Profit => (PrizeAmount ?? 0) - BetAmount;
}

public sealed class TicketLegRecord
{
    public long TicketId { get; set; }
    public int Seq { get; set; }
    public long? MatchId { get; set; }
    public string? MatchNo { get; set; }
    public string? HomeTeam { get; set; }
    public string? AwayTeam { get; set; }
    public string? PickOption { get; set; }
    public char? PickSide { get; set; }
    public decimal? Handicap { get; set; }
    public string? PlayType { get; set; }
    public string? PickCode { get; set; }
    public decimal? Odds { get; set; }
    public decimal? LegProbability { get; set; }
    public decimal? ModelProbability { get; set; }
    public decimal? MarketProbability { get; set; }
    public bool? LineHit { get; set; }
}

/// <summary>按票型分组的 KPI 一行。</summary>
public sealed class TicketKpiRow
{
    public string Board { get; set; } = "";
    public string PassType { get; set; } = "";
    public int LegCount { get; set; }
    /// <summary>已结算票数。</summary>
    public int Tickets { get; set; }
    /// <summary>有奖票数（至少一注中）。</summary>
    public int Won { get; set; }
    /// <summary>回本票数（奖金 ≥ 本金）。</summary>
    public int Backed { get; set; }
    public decimal Bet { get; set; }
    public decimal Prize { get; set; }
    /// <summary>模型给的票面中奖率均值（%），用来对照实际。</summary>
    public decimal? PredictedHit { get; set; }
    public decimal ActualHit => Tickets == 0 ? 0 : Math.Round(Won * 100m / Tickets, 1);
    public decimal ActualBack => Tickets == 0 ? 0 : Math.Round(Backed * 100m / Tickets, 1);
    public decimal Profit => Prize - Bet;
    /// <summary>平注实际回报率（奖金 ÷ 本金）。</summary>
    public decimal Roi => Bet == 0 ? 0 : Math.Round(Prize / Bet, 3);
}

/// <summary>票面 KPI 总表。</summary>
public sealed class TicketKpiSummary
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public int TotalTickets { get; set; }
    public int PendingTickets { get; set; }
    public decimal TotalBet { get; set; }
    public decimal TotalPrize { get; set; }
    public List<TicketKpiRow> Rows { get; set; } = new();
    public string Verdict { get; set; } = "";
}
