using AiFootBall.Core.Models;
using Dapper;

namespace AiFootBall.Data;

/// <summary>
/// 联赛积分榜仓储。
///
/// 为什么要有这张表（2026-11-08 用户提出）：11-07 三场大奶（007/008 胜负、027 负平）
/// 有一个共同点——**排名靠后的一方先进球**。想验证这个现象，库里却根本没有排名，
/// 只能靠人眼记。所以先落两张表：
///   FbMatchRank    每场比赛的**赛前**排名（回测直接 JOIN，不必再算）
///   FbTeamStanding 按日期保存的当期积分榜快照（看"当时"是怎么排的，不会被后来的比赛改写）
/// </summary>
public sealed class StandingRepository
{
    private readonly SqlConnectionFactory _factory;
    private static bool _schemaReady;

    public StandingRepository(SqlConnectionFactory factory) => _factory = factory;

    private async Task EnsureAsync(System.Data.Common.DbConnection conn, CancellationToken ct)
    {
        if (_schemaReady) return;
        await conn.ExecuteAsync(new CommandDefinition(
            @"IF OBJECT_ID('dbo.FbMatchRank','U') IS NULL
              CREATE TABLE dbo.FbMatchRank
              (
                  MatchId        BIGINT       NOT NULL PRIMARY KEY,
                  LeagueName     NVARCHAR(80) NOT NULL,
                  SeasonNo       INT          NOT NULL,
                  SeasonStart    DATE         NOT NULL,
                  TeamCount      INT          NOT NULL,
                  HomeRankPre    INT          NOT NULL,
                  AwayRankPre    INT          NOT NULL,
                  HomePtsPre     INT          NOT NULL,
                  AwayPtsPre     INT          NOT NULL,
                  HomeGdPre      INT          NOT NULL,
                  AwayGdPre      INT          NOT NULL,
                  HomePlayedPre  INT          NOT NULL,
                  AwayPlayedPre  INT          NOT NULL,
                  UpdatedAt      DATETIME2    NOT NULL
              );
              IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_FbMatchRank_League' AND object_id=OBJECT_ID('dbo.FbMatchRank'))
              CREATE INDEX IX_FbMatchRank_League ON dbo.FbMatchRank(LeagueName, SeasonStart);

              IF OBJECT_ID('dbo.FbTeamStanding','U') IS NULL
              CREATE TABLE dbo.FbTeamStanding
              (
                  Id          BIGINT IDENTITY(1,1) PRIMARY KEY,
                  AsOfDate    DATE         NOT NULL,
                  LeagueName  NVARCHAR(80) NOT NULL,
                  SeasonStart DATE         NOT NULL,
                  TeamName    NVARCHAR(80) NOT NULL,
                  Rank        INT          NOT NULL,
                  Played      INT          NOT NULL,
                  Points      INT          NOT NULL,
                  GoalsFor    INT          NOT NULL,
                  GoalsAgainst INT         NOT NULL,
                  TeamCount   INT          NOT NULL,
                  UpdatedAt   DATETIME2    NOT NULL
              );
              IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='UQ_FbTeamStanding' AND object_id=OBJECT_ID('dbo.FbTeamStanding'))
              CREATE UNIQUE INDEX UQ_FbTeamStanding ON dbo.FbTeamStanding(AsOfDate, LeagueName, SeasonStart, TeamName);",
            cancellationToken: ct));
        _schemaReady = true;
    }

    /// <summary>读取可回算的完场比赛（比分齐全）。赛季字段缺失，只能靠时间切。</summary>
    public async Task<List<StandingMatch>> LoadMatchesAsync(DateTime from, DateTime? to = null, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await EnsureAsync(conn, ct);
        var rows = await conn.QueryAsync<StandingMatch>(new CommandDefinition(
            @"SELECT MatchId, LeagueName, MatchTime, HomeTeamName AS HomeTeam, AwayTeamName AS AwayTeam,
                     CAST(HomeScore AS int) AS HomeScore, CAST(AwayScore AS int) AS AwayScore
              FROM dbo.FbMatch
              WHERE HomeScore IS NOT NULL AND AwayScore IS NOT NULL
                AND LeagueName IS NOT NULL AND MatchTime >= @from
                AND (@to IS NULL OR MatchTime < @to)
              ORDER BY LeagueName, MatchTime",
            new { from, to }, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>把赛前排名整批写回（MERGE 幂等；重算会覆盖旧值）。</summary>
    public async Task<int> SaveMatchRanksAsync(IReadOnlyList<MatchPreRank> ranks, CancellationToken ct = default)
    {
        if (ranks.Count == 0) return 0;
        await using var conn = await _factory.OpenAsync(ct);
        await EnsureAsync(conn, ct);

        var now = DateTime.Now;
        var rows = ranks.Select(r => new
        {
            r.MatchId, r.LeagueName, r.SeasonNo, r.SeasonStart, r.TeamCount,
            r.HomeRankPre, r.AwayRankPre, r.HomePtsPre, r.AwayPtsPre,
            r.HomeGdPre, r.AwayGdPre, r.HomePlayedPre, r.AwayPlayedPre, Now = now,
        }).ToList();

        var affected = 0;
        foreach (var chunk in rows.Chunk(2000))
        {
            affected += await conn.ExecuteAsync(new CommandDefinition(
                @"MERGE dbo.FbMatchRank AS t
                  USING (SELECT @MatchId AS MatchId, @LeagueName AS LeagueName, @SeasonNo AS SeasonNo,
                                @SeasonStart AS SeasonStart, @TeamCount AS TeamCount,
                                @HomeRankPre AS HomeRankPre, @AwayRankPre AS AwayRankPre,
                                @HomePtsPre AS HomePtsPre, @AwayPtsPre AS AwayPtsPre,
                                @HomeGdPre AS HomeGdPre, @AwayGdPre AS AwayGdPre,
                                @HomePlayedPre AS HomePlayedPre, @AwayPlayedPre AS AwayPlayedPre) AS s
                  ON t.MatchId = s.MatchId
                  WHEN MATCHED AND (t.SeasonNo <> s.SeasonNo OR t.HomeRankPre <> s.HomeRankPre
                                    OR t.AwayRankPre <> s.AwayRankPre OR t.TeamCount <> s.TeamCount) THEN UPDATE SET
                      LeagueName = s.LeagueName, SeasonNo = s.SeasonNo, SeasonStart = s.SeasonStart,
                      TeamCount = s.TeamCount, HomeRankPre = s.HomeRankPre, AwayRankPre = s.AwayRankPre,
                      HomePtsPre = s.HomePtsPre, AwayPtsPre = s.AwayPtsPre, HomeGdPre = s.HomeGdPre,
                      AwayGdPre = s.AwayGdPre, HomePlayedPre = s.HomePlayedPre, AwayPlayedPre = s.AwayPlayedPre,
                      UpdatedAt = @Now
                  WHEN NOT MATCHED THEN INSERT
                      (MatchId, LeagueName, SeasonNo, SeasonStart, TeamCount, HomeRankPre, AwayRankPre,
                       HomePtsPre, AwayPtsPre, HomeGdPre, AwayGdPre, HomePlayedPre, AwayPlayedPre, UpdatedAt)
                      VALUES (s.MatchId, s.LeagueName, s.SeasonNo, s.SeasonStart, s.TeamCount, s.HomeRankPre,
                              s.AwayRankPre, s.HomePtsPre, s.AwayPtsPre, s.HomeGdPre, s.AwayGdPre,
                              s.HomePlayedPre, s.AwayPlayedPre, @Now);",
                chunk, cancellationToken: ct));
        }
        return affected;
    }

    /// <summary>保存某一时点的完整积分榜快照（同日重跑覆盖，不重复堆行）。</summary>
    public async Task<int> SaveSnapshotAsync(DateTime asOfDate, IEnumerable<StandingRow> rows, CancellationToken ct = default)
    {
        var list = rows.ToList();
        if (list.Count == 0) return 0;
        await using var conn = await _factory.OpenAsync(ct);
        await EnsureAsync(conn, ct);

        var d = asOfDate.Date;
        var now = DateTime.Now;
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.FbTeamStanding WHERE AsOfDate=@d", new { d }, cancellationToken: ct));

        var affected = 0;
        foreach (var chunk in list.Chunk(2000))
        {
            affected += await conn.ExecuteAsync(new CommandDefinition(
                @"INSERT INTO dbo.FbTeamStanding
                    (AsOfDate, LeagueName, SeasonStart, TeamName, Rank, Played, Points, GoalsFor, GoalsAgainst, TeamCount, UpdatedAt)
                  VALUES (@d, @LeagueName, @SeasonStart, @TeamName, @Rank, @Played, @Points, @GoalsFor, @GoalsAgainst, @TeamCount, @Now)",
                chunk.Select(r => new { d, r.LeagueName, r.SeasonStart, r.TeamName, r.Rank, r.Played, r.Points, r.GoalsFor, r.GoalsAgainst, r.TeamCount, Now = now }),
                cancellationToken: ct));
        }
        return affected;
    }

    /// <summary>按场次批量取赛前排名。</summary>
    public async Task<Dictionary<long, MatchPreRank>> GetMatchRanksAsync(IEnumerable<long> matchIds, CancellationToken ct = default)
    {
        var ids = matchIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<long, MatchPreRank>();
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<MatchPreRank>(new CommandDefinition(
            @"SELECT MatchId, LeagueName, SeasonNo, SeasonStart, TeamCount, HomeRankPre, AwayRankPre,
                     HomePtsPre, AwayPtsPre, HomeGdPre, AwayGdPre, HomePlayedPre, AwayPlayedPre
              FROM dbo.FbMatchRank WHERE MatchId IN @ids",
            new { ids }, cancellationToken: ct));
        return rows.ToDictionary(r => r.MatchId);
    }

    /// <summary>
    /// 取某受注日竞彩场次的「赛前排名 + 半场比分」组合，复盘学习用。
    ///
    /// 定位受注日不能只用 MatchTime：22:00 后开球的场次记在次日，凌晨场（01:00~04:00）
    /// 的 MatchTime 已经是 day+1。只用时间窗会把次日的「周日」场次也捞进来
    /// （2026-11-08 实测：11-07 竞彩 29 场，两日窗口捞出 61 场）。
    /// 所以用场次号前缀（受注日就是星期几）+ 三天窗口，与复盘其他地方同口径。
    /// </summary>
    public async Task<List<LeadOutcomeRow>> GetLeadOutcomesAsync(DateTime day, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var from = day.Date;
        var to = from.AddDays(3);
        var prefix = "周" + "日一二三四五六"[(int)from.DayOfWeek];
        var rows = await conn.QueryAsync<LeadOutcomeRow>(new CommandDefinition(
            @"SELECT c.MatchNo, c.LeagueName, c.HomeTeam, c.AwayTeam,
                     ISNULL(r.HomeRankPre, 0) AS HomeRankPre, ISNULL(r.AwayRankPre, 0) AS AwayRankPre,
                     ISNULL(r.TeamCount, 0) AS TeamCount,
                     ISNULL(CAST(m.HalfHomeScore AS int), 0) AS HalfHome,
                     ISNULL(CAST(m.HalfAwayScore AS int), 0) AS HalfAway,
                     ISNULL(CAST(m.HomeScore AS int), 0) AS HomeScore,
                     ISNULL(CAST(m.AwayScore AS int), 0) AS AwayScore,
                     CASE WHEN r.HomeRankPre > 0 AND r.AwayRankPre > 0 THEN 1 ELSE 0 END AS HasRank,
                     CASE WHEN m.HalfHomeScore IS NOT NULL AND m.HalfAwayScore IS NOT NULL THEN 1 ELSE 0 END AS HasHalf,
                     CASE WHEN m.HomeScore IS NOT NULL AND m.AwayScore IS NOT NULL
                               AND (m.Status = 3 OR c.MatchTime <= DATEADD(minute, -150, GETDATE()))
                          THEN 1 ELSE 0 END AS Finished
              FROM dbo.BettingCard c
              JOIN dbo.FbMatch m ON m.MatchId = c.MatchId
              LEFT JOIN dbo.FbMatchRank r ON r.MatchId = c.MatchId
              WHERE c.Board = 'jc' AND c.MatchNo LIKE @prefix + '%'
                AND c.MatchTime >= @from AND c.MatchTime < @to
              ORDER BY c.MatchNo",
            new { from, to, prefix }, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>取某天保存的当期积分榜。</summary>
    public async Task<List<StandingRow>> GetSnapshotAsync(DateTime asOfDate, string? leagueName = null, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<StandingRow>(new CommandDefinition(
            @"SELECT AsOfDate, LeagueName, SeasonStart, TeamName, Rank, Played, Points, GoalsFor, GoalsAgainst, TeamCount
              FROM dbo.FbTeamStanding
              WHERE AsOfDate = @d AND (@lg IS NULL OR LeagueName = @lg)
              ORDER BY LeagueName, Rank",
            new { d = asOfDate.Date, lg = leagueName }, cancellationToken: ct));
        return rows.AsList();
    }
}
