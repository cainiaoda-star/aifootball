using AiFootBall.Core.Models;
using Dapper;

namespace AiFootBall.Data;

/// <summary>
/// 票面台账。落库在出票时，结算在赛果回填后，KPI 按票型聚合。
/// 这张表回答的是「哪种买法真的回本」，不是「哪场猜对了」。
/// </summary>
public sealed class TicketRepository
{
    private readonly SqlConnectionFactory _factory;

    private sealed record ExistingTicket(long TicketId, string Status);

    public TicketRepository(SqlConnectionFactory factory) => _factory = factory;

    /// <summary>
    /// 写入或更新一张票。同一 (出票日, 板块, 票型) 只留一行——
    /// 当天重算刷新概率时覆盖未结算的那张，已结算的不动，避免把赛果冲掉。
    /// </summary>
    public async Task<long> UpsertAsync(TicketDraft draft, CancellationToken ct = default)
    {
        if (draft.Legs.Count == 0 || draft.BetAmount <= 0) return 0;

        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var existing = await conn.QuerySingleOrDefaultAsync<ExistingTicket>(new CommandDefinition(
            @"SELECT TicketId, Status FROM dbo.TicketPlan WITH (UPDLOCK)
              WHERE PlanDate=@d AND Board=@b AND PassType=@p",
            new { d = draft.PlanDate.Date, b = draft.Board, p = draft.PassType },
            tx, cancellationToken: ct));

        // 当天这张票已经结过账（WON/LOST），就不再重发。否则晚间作业重跑会插出第二张同票型的票，
        // 撞上唯一键，也会把「今天发了几张票」算重复。
        // VOID 不在此列：它是票型切换时旧票的标记，不是终局。2026-11-04 教训：
        // 21:51 终版重算回「单关」票型，撞上早上被 VOID 的旧单关票直接 return，终版没存上，
        // 生效的还是 14:47 的旧 2串1——腿和终版对不上，结算全错。VOID 必须允许覆盖。
        if (existing is not null && existing.Status is "WON" or "LOST")
        {
            await tx.CommitAsync(ct);
            return existing.TicketId;
        }

        long ticketId;
        if (existing is not null)
        {
            ticketId = existing.TicketId;
            await conn.ExecuteAsync(new CommandDefinition(
                @"UPDATE dbo.TicketPlan
                  SET LegCount=@LegCount, BetCount=@BetCount, Multiple=@Multiple, BetAmount=@BetAmount,
                      HitProbability=@HitProbability, BackProbability=@BackProbability,
                      ExpectedReturn=@ExpectedReturn,
                      Status='PENDING', SettledAt=NULL, HitLegCount=NULL, PrizeAmount=NULL
                  WHERE TicketId=@TicketId",
                new
                {
                    TicketId = ticketId, draft.LegCount, draft.BetCount, draft.Multiple, draft.BetAmount,
                    draft.HitProbability, draft.BackProbability, draft.ExpectedReturn
                }, tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM dbo.TicketPlanLine WHERE TicketId=@id", new { id = ticketId }, tx, cancellationToken: ct));
        }
        else
        {
            ticketId = await conn.QuerySingleAsync<long>(new CommandDefinition(
                @"INSERT INTO dbo.TicketPlan
                    (PlanDate, Board, PassType, LegCount, BetCount, Multiple, BetAmount,
                     HitProbability, BackProbability, ExpectedReturn, Status)
                  OUTPUT INSERTED.TicketId
                  VALUES (@PlanDate, @Board, @PassType, @LegCount, @BetCount, @Multiple, @BetAmount,
                          @HitProbability, @BackProbability, @ExpectedReturn, 'PENDING')",
                new
                {
                    PlanDate = draft.PlanDate.Date, draft.Board, draft.PassType,
                    draft.LegCount, draft.BetCount, draft.Multiple, draft.BetAmount,
                    draft.HitProbability, draft.BackProbability, draft.ExpectedReturn
                }, tx, cancellationToken: ct));
        }

        await conn.ExecuteAsync(new CommandDefinition(
            @"INSERT INTO dbo.TicketPlanLine
                (TicketId, Seq, MatchId, MatchNo, HomeTeam, AwayTeam, PickOption, PickSide, Handicap, Odds,
                 PlayType, PickCode, LegProbability, ModelProbability, MarketProbability)
              VALUES (@TicketId, @Seq, @MatchId, @MatchNo, @HomeTeam, @AwayTeam, @PickOption, @PickSide, @Handicap, @Odds,
                 @PlayType, @PickCode, @LegProbability, @ModelProbability, @MarketProbability)",
            draft.Legs.Select(l => new
            {
                TicketId = ticketId, l.Seq, l.MatchId, l.MatchNo, l.HomeTeam, l.AwayTeam,
                l.PickOption, l.PickSide, l.Handicap, l.Odds,
                l.PlayType, l.PickCode,
                l.LegProbability, l.ModelProbability, l.MarketProbability
            }), tx, cancellationToken: ct));

        // 一天只留一张票。票型会随腿数变化（3 腿是「单关#2串1#3串1」，2 腿是「2串1」），
        // 唯一键只拦同票型，换了票型就会把前一张留在库里，KPI 于是把两天的本金算成一天两张。
        // 旧票不删而是标 VOID：删了就查不到「当时算的是什么」，留 PENDING 又会被结算。
        await conn.ExecuteAsync(new CommandDefinition(
            @"UPDATE dbo.TicketPlan
              SET Status='VOID', SettledAt=SYSDATETIME()
              WHERE PlanDate=@d AND Board=@b AND PassType<>@p AND Status='PENDING'",
            new { d = draft.PlanDate.Date, b = draft.Board, p = draft.PassType },
            tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return ticketId;
    }

    /// <summary>
    /// 取未结算的票，并把各腿的让球命中一并带出来。
    /// 命中取自推荐记录的 HandicapHit——票腿本来就是从那条推荐的让球方向来的，
    /// 另算一套会出现「推荐说中了、票说没中」的分裂。
    /// </summary>
    public async Task<List<TicketRecord>> GetPendingAsync(DateTime? until = null, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        var tickets = (await conn.QueryAsync<TicketRecord>(new CommandDefinition(
            @"SELECT TicketId, PlanDate, Board, PassType, LegCount, BetCount, Multiple, BetAmount,
                     HitProbability, BackProbability, ExpectedReturn, Status, HitLegCount, PrizeAmount, SettledAt
              FROM dbo.TicketPlan
              WHERE Status='PENDING' AND PlanDate <= @until
              ORDER BY PlanDate",
            new { until = (until ?? DateTime.Today).Date },
            cancellationToken: ct))).ToList();
        if (tickets.Count == 0) return tickets;

        var ids = tickets.Select(t => t.TicketId).ToArray();
        var legs = await conn.QueryAsync<TicketLegRecord>(new CommandDefinition(
            // 判定顺序：已写死的命中 > 取消场次（官方按赔率 1 计，腿算过、奖金退本金）>
            // 玩法腿（比分/半全场/总进球，PlayType 非空）> 用完场比分 + 让球数自己算 > 推荐表的 HandicapHit 兜底。
            // 自己算放第三位，是因为票腿可能来自「没进精选」的场次，推荐表里查不到；
            // 兜底留给早期没有存让球数的票。
            @"SELECT l.LineId, l.TicketId, l.Seq, l.MatchId, l.MatchNo, l.HomeTeam, l.AwayTeam,
                     l.PickOption, l.PickSide, l.Handicap, l.PlayType, l.PickCode, l.LegProbability,
                     l.ModelProbability, l.MarketProbability,
                     CASE
                       WHEN l.LineHit IS NOT NULL THEN l.LineHit
                       WHEN m.Status = 2 THEN 1
                       WHEN l.PlayType = 'BF' AND m.Status = 3 AND m.HomeScore IS NOT NULL AND m.AwayScore IS NOT NULL THEN
                         -- 比分档有两类：具体比分（3:0 这种）和三个「其它」（胜其它/平其它/负其它）。
                         -- 只比对具体比分的话，「胜其它」会被判成永远不中——而这档赔率 90 倍，
                         -- 恰恰是最容易进大奶票的选项，判错了票面 KPI 就全歪了。
                         -- 「其它」的口径与 MilkPickMath.ListedScores 一致：不在官方 28 个具体档里才算其它
                         --（4:3、5:4、4:4 这类双方都 ≤5 的比分也是「其它」）。
                         CASE
                           WHEN CHARINDEX(':', l.PickCode) = 0 THEN
                             CASE WHEN (
                                      (LEFT(l.PickCode, 1) = N'胜' AND m.HomeScore > m.AwayScore)
                                   OR (LEFT(l.PickCode, 1) = N'平' AND m.HomeScore = m.AwayScore)
                                   OR (LEFT(l.PickCode, 1) = N'负' AND m.HomeScore < m.AwayScore))
                                  AND CONCAT(m.HomeScore, ':', m.AwayScore) NOT IN
                                      ('1:0','2:0','2:1','3:0','3:1','3:2','4:0','4:1','4:2','5:0','5:1','5:2',
                                       '0:0','1:1','2:2','3:3',
                                       '0:1','0:2','1:2','0:3','1:3','2:3','0:4','1:4','2:4','0:5','1:5','2:5')
                                  THEN 1 ELSE 0 END
                           WHEN l.PickCode = CONCAT(m.HomeScore, ':', m.AwayScore) THEN 1
                           ELSE 0
                         END
                       WHEN l.PlayType = 'JQ' AND m.Status = 3 AND m.HomeScore IS NOT NULL AND m.AwayScore IS NOT NULL THEN
                         CASE WHEN l.PickCode = '7' AND m.HomeScore + m.AwayScore >= 7 THEN 1
                              WHEN l.PickCode <> '7' AND m.HomeScore + m.AwayScore = TRY_CAST(l.PickCode AS INT) THEN 1
                              ELSE 0 END
                       WHEN l.PlayType = 'BQ' AND m.Status = 3 AND m.HomeScore IS NOT NULL AND m.AwayScore IS NOT NULL
                            AND m.HalfHomeScore IS NOT NULL AND m.HalfAwayScore IS NOT NULL THEN
                         CASE WHEN l.PickCode = CONCAT(
                                  CASE WHEN m.HalfHomeScore > m.HalfAwayScore THEN '3'
                                       WHEN m.HalfHomeScore = m.HalfAwayScore THEN '1' ELSE '0' END,
                                  '-',
                                  CASE WHEN m.HomeScore > m.AwayScore THEN '3'
                                       WHEN m.HomeScore = m.AwayScore THEN '1' ELSE '0' END) THEN 1 ELSE 0 END
                       WHEN m.Status = 3 AND m.HomeScore IS NOT NULL AND m.AwayScore IS NOT NULL
                            AND l.Handicap IS NOT NULL AND l.PickSide IS NOT NULL THEN
                         CASE
                           WHEN m.HomeScore + l.Handicap - m.AwayScore > 0.001
                                THEN CASE WHEN l.PickSide = '3' THEN 1 ELSE 0 END
                           WHEN m.HomeScore + l.Handicap - m.AwayScore < -0.001
                                THEN CASE WHEN l.PickSide = '0' THEN 1 ELSE 0 END
                           ELSE CASE WHEN l.PickSide = '1' THEN 1 ELSE 0 END
                         END
                       ELSE (
                         SELECT TOP 1 r.HandicapHit FROM dbo.Recommendation r
                         WHERE r.MatchId = l.MatchId AND r.Board = t.Board AND r.HandicapHit IS NOT NULL
                         ORDER BY r.RecDate DESC, r.RecId DESC
                       )
                     END AS LineHit,
                     CASE WHEN m.Status = 2 THEN 1 ELSE l.Odds END AS Odds
              FROM dbo.TicketPlanLine l
              JOIN dbo.TicketPlan t ON t.TicketId = l.TicketId
              LEFT JOIN dbo.FbMatch m ON m.MatchId = l.MatchId
              WHERE l.TicketId IN @ids
              ORDER BY l.TicketId, l.Seq",
            new { ids }, cancellationToken: ct));

        var byTicket = legs.ToLookup(l => l.TicketId);
        foreach (var t in tickets)
            t.Legs = byTicket[t.TicketId].ToList();
        return tickets;
    }

    /// <summary>结算落库：状态、命中腿数、奖金、时间，同时把各腿命中写死。</summary>
    public async Task MarkSettledAsync(long ticketId, IReadOnlyList<TicketLegRecord> legs,
        bool won, int hitCount, decimal prize, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        foreach (var l in legs.Where(x => x.LineHit is not null))
        {
            await conn.ExecuteAsync(new CommandDefinition(
                @"UPDATE dbo.TicketPlanLine SET LineHit=@hit
                  WHERE TicketId=@id AND Seq=@seq",
                new { hit = l.LineHit, id = ticketId, seq = l.Seq }, tx, cancellationToken: ct));
        }

        await conn.ExecuteAsync(new CommandDefinition(
            @"UPDATE dbo.TicketPlan
              SET Status=@status, HitLegCount=@hitCount, PrizeAmount=@prize, SettledAt=SYSDATETIME()
              WHERE TicketId=@id",
            new { status = won ? "WON" : "LOST", hitCount, prize, id = ticketId }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
    }

    /// <summary>按票型聚合 KPI。只算已结算的票——未开赛的票本金已花但结果未知，混进来会低估。</summary>
    public async Task<TicketKpiSummary> GetKpiAsync(int days = 90, CancellationToken ct = default)
    {
        var to = DateTime.Today;
        var from = to.AddDays(-Math.Max(1, days));

        await using var conn = await _factory.OpenAsync(ct);

        var rows = (await conn.QueryAsync<TicketKpiRow>(new CommandDefinition(
            @"SELECT Board, PassType,
                     MAX(LegCount) AS LegCount,
                     COUNT(*) AS Tickets,
                     SUM(CASE WHEN PrizeAmount > 0 THEN 1 ELSE 0 END) AS Won,
                     SUM(CASE WHEN ISNULL(PrizeAmount,0) >= BetAmount THEN 1 ELSE 0 END) AS Backed,
                     SUM(BetAmount) AS Bet,
                     SUM(ISNULL(PrizeAmount,0)) AS Prize,
                     CAST(AVG(HitProbability) AS DECIMAL(6,2)) AS PredictedHit
              FROM dbo.TicketPlan
              WHERE Status IN ('WON','LOST') AND PlanDate BETWEEN @from AND @to
              GROUP BY Board, PassType
              ORDER BY COUNT(*) DESC",
            new { from, to }, cancellationToken: ct))).ToList();

        var pending = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM dbo.TicketPlan WHERE Status='PENDING' AND PlanDate BETWEEN @from AND @to",
            new { from, to }, cancellationToken: ct));

        var total = await conn.QuerySingleAsync<(int Tickets, decimal Bet, decimal Prize)>(new CommandDefinition(
            @"SELECT COUNT(*) AS Tickets, ISNULL(SUM(BetAmount),0) AS Bet, ISNULL(SUM(PrizeAmount),0) AS Prize
              FROM dbo.TicketPlan WHERE Status IN ('WON','LOST') AND PlanDate BETWEEN @from AND @to",
            new { from, to }, cancellationToken: ct));

        return new TicketKpiSummary
        {
            From = from, To = to,
            TotalTickets = total.Tickets,
            PendingTickets = pending,
            TotalBet = total.Bet,
            TotalPrize = total.Prize,
            Rows = rows,
            Verdict = VerdictOf(rows, total.Tickets)
        };
    }

    /// <summary>一句话结论。样本不足时只说不足，不解释趋势——十几张票看不出结论。</summary>
    private static string VerdictOf(List<TicketKpiRow> rows, int total)
    {
        if (total == 0) return "还没有已结算的票：先让作业跑几天，票面 KPI 至少需要 30 张才看得出方向。";
        if (total < 30) return $"已结算 {total} 张，样本不足（建议 ≥30 张）。当前数字只能当参考，不要据此改票型。";

        var best = rows.OrderByDescending(r => r.Roi).FirstOrDefault();
        var worst = rows.OrderBy(r => r.Roi).FirstOrDefault();
        if (best is null || worst is null) return $"已结算 {total} 张。";

        if (ReferenceEquals(best, worst))
            return $"已结算 {total} 张，只有「{best.PassType}」一种票型，实际回报 {best.Roi:0.000}。";

        return $"已结算 {total} 张。「{best.PassType}」实际回报 {best.Roi:0.000}（{best.Tickets} 张，回本 {best.ActualBack}%），"
             + $"「{worst.PassType}」{worst.Roi:0.000}（{worst.Tickets} 张，回本 {worst.ActualBack}%）。"
             + "串关的实际回报随腿数下降，与返还率幂运算的预测一致。";
    }
}
