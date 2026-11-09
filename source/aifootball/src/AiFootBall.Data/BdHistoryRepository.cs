using AiFootBall.Core.Models;
using Dapper;

namespace AiFootBall.Data;

/// <summary>北单历史开奖仓储。按（期号, 序号）覆盖写，可重复回填。</summary>
public sealed class BdHistoryRepository
{
    private readonly SqlConnectionFactory _factory;

    public BdHistoryRepository(SqlConnectionFactory factory) => _factory = factory;

    public async Task<int> UpsertAsync(IReadOnlyList<BdHistoryRow> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return 0;
        const string sql = @"
MERGE dbo.FbBdHistory AS t
USING (SELECT @Expect AS Expect, @MatchNo AS MatchNo) AS s ON t.Expect = s.Expect AND t.MatchNo = s.MatchNo
WHEN MATCHED THEN UPDATE SET
    MatchId = @MatchId, LeagueName = @LeagueName, EndTime = @EndTime, HomeTeam = @HomeTeam, AwayTeam = @AwayTeam,
    Handicap = @Handicap, AvgHome = @AvgHome, AvgDraw = @AvgDraw, AvgAway = @AvgAway, AsianHand = @AsianHand,
    HomeScore = @HomeScore, AwayScore = @AwayScore, ResultCode = @ResultCode, ResultSp = @ResultSp,
    CapturedAt = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT
    (Expect, MatchNo, MatchId, LeagueName, EndTime, HomeTeam, AwayTeam, Handicap, AvgHome, AvgDraw, AvgAway,
     AsianHand, HomeScore, AwayScore, ResultCode, ResultSp)
    VALUES (@Expect, @MatchNo, @MatchId, @LeagueName, @EndTime, @HomeTeam, @AwayTeam, @Handicap, @AvgHome, @AvgDraw,
            @AvgAway, @AsianHand, @HomeScore, @AwayScore, @ResultCode, @ResultSp);";

        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(sql, rows, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return n;
    }

    /// <summary>已经收完的期：最后一场截止已超过一天。在售期不算，下次还会重抓。</summary>
    public async Task<HashSet<int>> GetExpectsAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<int>(new CommandDefinition(
            @"SELECT Expect FROM dbo.FbBdHistory GROUP BY Expect
              HAVING MAX(EndTime) < DATEADD(day, -1, SYSDATETIME())", cancellationToken: ct))).ToHashSet();
    }
}
