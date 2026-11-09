using AiFootBall.Core.Models;
using Dapper;
using Microsoft.Data.SqlClient;

namespace AiFootBall.Data;

/// <summary>购买记录（自购方案）仓储：方案主从表 + 开奖回填 + 中奖统计。</summary>
public sealed class PurchaseRepository
{
    private readonly SqlConnectionFactory _factory;

    public PurchaseRepository(SqlConnectionFactory factory) => _factory = factory;

    /* ---------------- 录入 / 更新 ---------------- */

    /// <summary>新增或更新一个购买方案（含场次明细，事务内先删旧明细再插入）。</summary>
    public async Task<long> UpsertAsync(PurchaseUpsertRequest req, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        long purchaseId;
        if (req.PurchaseId is > 0)
        {
            purchaseId = req.PurchaseId.Value;
            await conn.ExecuteAsync(new CommandDefinition(
                @"UPDATE dbo.Purchase SET
                    Board=@b, Expect=@e, PlayType=@pt, PassType=@pa, Multiple=@m, BetCount=@bc,
                    BetAmount=@ba, ExpectedWin=@ew, ActualWin=@aw, Status=@st, BuyDate=@bd,
                    TicketImage=@ti, ResultImage=@ri, IsHit=@ih, HitLevel=@hl, Remark=@rm, UpdatedAt=SYSDATETIME()
                  WHERE PurchaseId=@id",
                new
                {
                    id = purchaseId, b = req.Board, e = req.Expect, pt = req.PlayType, pa = req.PassType,
                    m = req.Multiple, bc = req.BetCount, ba = req.BetAmount, ew = req.ExpectedWin,
                    aw = req.ActualWin, st = req.Status ?? "PENDING", bd = req.BuyDate ?? DateTime.Today,
                    ti = req.TicketImage, ri = req.ResultImage, ih = req.IsHit, hl = req.HitLevel, rm = req.Remark
                }, tx, cancellationToken: ct));

            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM dbo.PurchaseLine WHERE PurchaseId=@id", new { id = purchaseId }, tx, cancellationToken: ct));
        }
        else
        {
            purchaseId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                @"INSERT INTO dbo.Purchase (Board, Expect, PlayType, PassType, Multiple, BetCount, BetAmount, ExpectedWin, ActualWin, Status, BuyDate, TicketImage, ResultImage, IsHit, HitLevel, Remark)
                  OUTPUT INSERTED.PurchaseId
                  VALUES (@b,@e,@pt,@pa,@m,@bc,@ba,@ew,@aw,@st,@bd,@ti,@ri,@ih,@hl,@rm)",
                new
                {
                    b = req.Board, e = req.Expect, pt = req.PlayType, pa = req.PassType,
                    m = req.Multiple, bc = req.BetCount, ba = req.BetAmount, ew = req.ExpectedWin,
                    aw = req.ActualWin, st = req.Status ?? "PENDING", bd = req.BuyDate ?? DateTime.Today,
                    ti = req.TicketImage, ri = req.ResultImage, ih = req.IsHit, hl = req.HitLevel, rm = req.Remark
                }, tx, cancellationToken: ct));
        }

        var seq = 0;
        foreach (var l in req.Lines)
        {
            seq++;
            await conn.ExecuteAsync(new CommandDefinition(
                @"INSERT INTO dbo.PurchaseLine (PurchaseId, Seq, MatchNo, MatchId, LeagueName, HomeTeam, AwayTeam, MatchTime, Handicap, Option1, Option2, Odds1, Odds2, ActualScore, ActualGoals, ActualResult, LineHit)
                  SELECT @pid,@seq,@no,
                         COALESCE(@mid, (
                             SELECT TOP 1 c.MatchId FROM dbo.BettingCard c
                             WHERE c.MatchNo = @no AND c.MatchId IS NOT NULL
                             ORDER BY c.CapturedAt DESC
                         ), (
                             SELECT TOP 1 m.MatchId FROM dbo.FbMatch m
                             WHERE m.HomeTeamName = @h AND m.AwayTeamName = @a
                             ORDER BY m.MatchTime DESC
                         )),
                         @lg,@h,@a,@mt,@hcp,@o1,@o2,@od1,@od2,@as,@ag,@ar,@lh",
                new
                {
                    pid = purchaseId, seq, no = l.MatchNo, mid = l.MatchId, lg = l.LeagueName,
                    h = l.HomeTeam, a = l.AwayTeam, mt = l.MatchTime, hcp = l.Handicap,
                    o1 = l.Option1, o2 = l.Option2, od1 = l.Odds1, od2 = l.Odds2,
                    @as = l.ActualScore, ag = l.ActualGoals, ar = l.ActualResult, lh = l.LineHit
                }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return purchaseId;
    }

    /// <summary>回填开奖结果（整体中奖 + 各场实际比分）。</summary>
    public async Task SettleAsync(PurchaseSettleRequest req, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(
            @"UPDATE dbo.Purchase SET
                ActualWin=@aw, IsHit=@ih, HitLevel=@hl, ResultImage=COALESCE(@ri, ResultImage),
                Status=@st, Remark=@rm,
                PlayType=COALESCE(@pt, PlayType),
                UpdatedAt=SYSDATETIME()
              WHERE PurchaseId=@id",
            new
            {
                id = req.PurchaseId, aw = req.ActualWin, ih = req.IsHit, hl = req.HitLevel,
                ri = req.ResultImage, st = req.IsHit ? "WON" : "LOST", rm = req.Remark,
                pt = req.PlayType
            }, tx, cancellationToken: ct));

        foreach (var l in req.Lines)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                @"UPDATE dbo.PurchaseLine SET ActualScore=@as, ActualGoals=@ag, ActualResult=@ar, LineHit=@lh
                  WHERE PurchaseId=@pid AND Seq=@seq",
                new { pid = req.PurchaseId, seq = l.Seq, @as = l.ActualScore, ag = l.ActualGoals, ar = l.ActualResult, lh = l.LineHit },
                tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>只更新某方案的图片路径（购买图/开奖图）。</summary>
    public async Task UpdateImageAsync(long purchaseId, string field, string path, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var sql = field == "result"
            ? "UPDATE dbo.Purchase SET ResultImage=@p, UpdatedAt=SYSDATETIME() WHERE PurchaseId=@id"
            : "UPDATE dbo.Purchase SET TicketImage=@p, UpdatedAt=SYSDATETIME() WHERE PurchaseId=@id";
        await conn.ExecuteAsync(new CommandDefinition(sql, new { id = purchaseId, p = path }, cancellationToken: ct));
    }

    /* ---------------- 查询 ---------------- */

    /// <summary>待开奖/待出票方案（自动结算用）。</summary>
    public async Task<List<PurchaseRecord>> GetUnsettledAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var heads = (await conn.QueryAsync<PurchaseRecord>(new CommandDefinition(
            @"SELECT * FROM dbo.Purchase
              WHERE Status IN ('WAIT','PENDING')
              ORDER BY BuyDate DESC, PurchaseId DESC", cancellationToken: ct))).ToList();
        if (heads.Count == 0) return heads;

        var ids = heads.Select(h => h.PurchaseId).ToArray();
        var lines = (await conn.QueryAsync<PurchaseLineRecord>(new CommandDefinition(
            @"SELECT * FROM dbo.PurchaseLine WHERE PurchaseId IN @ids ORDER BY PurchaseId, Seq",
            new { ids }, cancellationToken: ct))).ToList();

        var map = lines.GroupBy(l => l.PurchaseId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var h in heads)
            if (map.TryGetValue(h.PurchaseId, out var ls)) h.Lines = ls;
        return heads;
    }

    /// <summary>按 ID 取单个方案（含明细）。</summary>
    public async Task<PurchaseRecord?> GetByIdAsync(long purchaseId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var head = await conn.QueryFirstOrDefaultAsync<PurchaseRecord>(new CommandDefinition(
            "SELECT * FROM dbo.Purchase WHERE PurchaseId=@id", new { id = purchaseId }, cancellationToken: ct));
        if (head is null) return null;
        head.Lines = (await conn.QueryAsync<PurchaseLineRecord>(new CommandDefinition(
            "SELECT * FROM dbo.PurchaseLine WHERE PurchaseId=@id ORDER BY Seq",
            new { id = purchaseId }, cancellationToken: ct))).ToList();
        return head;
    }

    /// <summary>仅回填场次赛果（方案仍待开奖时用）。</summary>
    public async Task UpdateLineResultsAsync(long purchaseId, IEnumerable<PurchaseLineSettle> lines, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        foreach (var l in lines)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                @"UPDATE dbo.PurchaseLine SET ActualScore=@as, ActualGoals=@ag, ActualResult=@ar, LineHit=@lh
                  WHERE PurchaseId=@pid AND Seq=@seq",
                new { pid = purchaseId, seq = l.Seq, @as = l.ActualScore, ag = l.ActualGoals, ar = l.ActualResult, lh = l.LineHit },
                cancellationToken: ct));
        }
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.Purchase SET UpdatedAt=SYSDATETIME() WHERE PurchaseId=@id",
            new { id = purchaseId }, cancellationToken: ct));
    }

    /// <summary>补写明细 MatchId（场次号反查成功后）。</summary>
    public async Task SetLineMatchIdAsync(long purchaseId, int seq, long matchId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.PurchaseLine SET MatchId=@mid WHERE PurchaseId=@pid AND Seq=@seq AND (MatchId IS NULL OR MatchId<>@mid)",
            new { pid = purchaseId, seq, mid = matchId }, cancellationToken: ct));
    }

    /// <summary>取全部购买方案（含明细），按购买日期倒序。</summary>
    public async Task<List<PurchaseRecord>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var heads = (await conn.QueryAsync<PurchaseRecord>(new CommandDefinition(
            @"SELECT * FROM dbo.Purchase ORDER BY BuyDate DESC, PurchaseId DESC", cancellationToken: ct))).ToList();
        if (heads.Count == 0) return heads;

        var ids = heads.Select(h => h.PurchaseId).ToArray();
        var lines = (await conn.QueryAsync<PurchaseLineRecord>(new CommandDefinition(
            @"SELECT * FROM dbo.PurchaseLine WHERE PurchaseId IN @ids ORDER BY PurchaseId, Seq",
            new { ids }, cancellationToken: ct))).ToList();

        var map = lines.GroupBy(l => l.PurchaseId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var h in heads)
            if (map.TryGetValue(h.PurchaseId, out var ls)) h.Lines = ls;

        return heads;
    }

    /// <summary>取某日购买方案。</summary>
    public async Task<List<PurchaseRecord>> GetByDateAsync(DateTime date, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var heads = (await conn.QueryAsync<PurchaseRecord>(new CommandDefinition(
            @"SELECT * FROM dbo.Purchase WHERE BuyDate=@d ORDER BY PurchaseId DESC",
            new { d = date.Date }, cancellationToken: ct))).ToList();
        if (heads.Count == 0) return heads;

        var ids = heads.Select(h => h.PurchaseId).ToArray();
        var lines = (await conn.QueryAsync<PurchaseLineRecord>(new CommandDefinition(
            @"SELECT * FROM dbo.PurchaseLine WHERE PurchaseId IN @ids ORDER BY PurchaseId, Seq",
            new { ids }, cancellationToken: ct))).ToList();

        var map = lines.GroupBy(l => l.PurchaseId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var h in heads)
            if (map.TryGetValue(h.PurchaseId, out var ls)) h.Lines = ls;

        return heads;
    }

    /// <summary>删除一个方案（级联删明细）。</summary>
    public async Task DeleteAsync(long purchaseId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.Purchase WHERE PurchaseId=@id", new { id = purchaseId }, cancellationToken: ct));
    }

    /* ---------------- 统计 ---------------- */

    /// <summary>中奖统计：总注数、中奖方案数、投入/回报、命中率。</summary>
    public async Task<object?> GetStatsAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync(new CommandDefinition(
            @"SELECT
                COUNT(*) AS TotalPurchases,
                SUM(CASE WHEN Status='WON' THEN 1 ELSE 0 END) AS WonPurchases,
                SUM(CASE WHEN Status='LOST' THEN 1 ELSE 0 END) AS LostPurchases,
                SUM(CASE WHEN Status='WAIT' OR Status='PENDING' THEN 1 ELSE 0 END) AS PendingPurchases,
                SUM(BetAmount) AS TotalBet,
                SUM(ActualWin) AS TotalWin,
                CAST(ROUND(AVG(CASE WHEN Status='WON' THEN 100.0 ELSE 0 END), 1) AS FLOAT) AS WinRate
              FROM dbo.Purchase", cancellationToken: ct));
    }
}
