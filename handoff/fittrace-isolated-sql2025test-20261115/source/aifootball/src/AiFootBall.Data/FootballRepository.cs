using AiFootBall.Core.Models;
using Dapper;

namespace AiFootBall.Data;

/// <summary>足球数据仓储：联赛、球队、赛事。</summary>
public sealed class FootballRepository
{
    private readonly SqlConnectionFactory _factory;

    public FootballRepository(SqlConnectionFactory factory) => _factory = factory;

    public async Task<(int Leagues, int Teams, int Matches)> UpsertAsync(
        IReadOnlyList<LeagueRecord> leagues,
        IReadOnlyList<TeamRecord> teams,
        IReadOnlyList<MatchRecord> matches,
        CancellationToken ct = default)
    {
        const string leagueSql = @"
MERGE dbo.FbLeague AS t
USING (SELECT @LeagueId AS LeagueId) AS s ON t.LeagueId = s.LeagueId
WHEN MATCHED THEN UPDATE SET Name = @Name, Country = ISNULL(@Country, t.Country), UpdatedAt = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (LeagueId, Name, Country, Season, UpdatedAt)
    VALUES (@LeagueId, @Name, @Country, NULL, SYSDATETIME());";

        const string teamSql = @"
MERGE dbo.FbTeam AS t
USING (SELECT @TeamId AS TeamId) AS s ON t.TeamId = s.TeamId
WHEN MATCHED THEN UPDATE SET Name = @Name, LeagueId = ISNULL(@LeagueId, t.LeagueId), UpdatedAt = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (TeamId, Name, LeagueId, UpdatedAt)
    VALUES (@TeamId, @Name, @LeagueId, SYSDATETIME());";

        const string matchSql = @"
MERGE dbo.FbMatch AS t
USING (SELECT @MatchId AS MatchId) AS s ON t.MatchId = s.MatchId
WHEN MATCHED THEN UPDATE SET
    LeagueId = ISNULL(@LeagueId, t.LeagueId), MatchTime = @MatchTime,
    LeagueName = CASE WHEN @LeagueName = '' THEN t.LeagueName ELSE @LeagueName END,
    HomeTeamId = ISNULL(@HomeTeamId, t.HomeTeamId), AwayTeamId = ISNULL(@AwayTeamId, t.AwayTeamId),
    HomeTeamName = @HomeTeamName, AwayTeamName = @AwayTeamName,
    HomeScore = ISNULL(@HomeScore, t.HomeScore), AwayScore = ISNULL(@AwayScore, t.AwayScore),
    HalfHomeScore = ISNULL(@HalfHomeScore, t.HalfHomeScore), HalfAwayScore = ISNULL(@HalfAwayScore, t.HalfAwayScore),
    Status = CASE WHEN @Status > t.Status THEN @Status ELSE t.Status END, Handicap = ISNULL(@Handicap, t.Handicap),
    ResultSpf = ISNULL(@ResultSpf, t.ResultSpf), ResultHandicap = ISNULL(@ResultHandicap, t.ResultHandicap),
    TotalGoals = ISNULL(@TotalGoals, t.TotalGoals), ScoreText = ISNULL(@ScoreText, t.ScoreText),
    UpdatedAt = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT
    (MatchId, LeagueId, LeagueName, MatchTime, HomeTeamId, AwayTeamId, HomeTeamName, AwayTeamName,
     HomeScore, AwayScore, HalfHomeScore, HalfAwayScore, Status, Handicap,
     ResultSpf, ResultHandicap, TotalGoals, ScoreText, Source)
    VALUES (@MatchId, @LeagueId, @LeagueName, @MatchTime, @HomeTeamId, @AwayTeamId, @HomeTeamName, @AwayTeamName,
            @HomeScore, @AwayScore, @HalfHomeScore, @HalfAwayScore, @Status, @Handicap,
            @ResultSpf, @ResultHandicap, @TotalGoals, @ScoreText, @Source);";

        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            var l = (await conn.ExecuteAsync(leagueSql, leagues.Select(x => new
            {
                x.LeagueId, x.Name, x.Country
            }), tx));

            var distinctTeams = teams.GroupBy(t => t.TeamId).Select(g => g.First()).ToList();
            var tm = await conn.ExecuteAsync(teamSql, distinctTeams, tx);
            var mc = await conn.ExecuteAsync(matchSql, matches.Select(m => new
            {
                m.MatchId,
                m.LeagueId,
                m.LeagueName,
                MatchTime = m.MatchTime,
                m.HomeTeamId,
                m.AwayTeamId,
                m.HomeTeamName,
                m.AwayTeamName,
                m.HomeScore,
                m.AwayScore,
                m.HalfHomeScore,
                m.HalfAwayScore,
                m.Status,
                m.Handicap,
                m.ResultSpf,
                m.ResultHandicap,
                m.TotalGoals,
                ScoreText = m.IsFinished ? $"{m.HomeScore}-{m.AwayScore}" : null,
                m.Source
            }), tx);

            await tx.CommitAsync(ct);
            return (l, tm, mc);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<List<MatchRecord>> GetMatchesAsync(DateOnly from, DateOnly to, int? leagueId = null,
        int take = 1000, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<MatchRecord>(
            @"SELECT TOP (@Take) m.MatchId, m.LeagueId, COALESCE(l.Name, m.LeagueName) AS LeagueName, m.MatchTime,
                     m.HomeTeamId, m.AwayTeamId, m.HomeTeamName, m.AwayTeamName,
                     m.HomeScore, m.AwayScore, m.HalfHomeScore, m.HalfAwayScore,
                     m.Status, m.Handicap, m.ResultSpf, m.ResultHandicap, m.TotalGoals
              FROM dbo.FbMatch m
              LEFT JOIN dbo.FbLeague l ON l.LeagueId = m.LeagueId
              WHERE m.MatchTime >= @From AND m.MatchTime < @To
                AND (@LeagueId IS NULL OR m.LeagueId = @LeagueId)
              ORDER BY m.MatchTime",
            new
            {
                Take = take,
                From = from.ToDateTime(TimeOnly.MinValue),
                To = to.AddDays(1).ToDateTime(TimeOnly.MinValue),
                LeagueId = leagueId
            });
        return rows.ToList();
    }

    /// <summary>按赛事 ID 取单场信息</summary>
    public async Task<MatchRecord?> GetMatchAsync(long matchId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<MatchRecord>(
            @"SELECT m.MatchId, m.LeagueId, COALESCE(l.Name, m.LeagueName) AS LeagueName, m.MatchTime,
                     m.HomeTeamId, m.AwayTeamId, m.HomeTeamName, m.AwayTeamName,
                     m.HomeScore, m.AwayScore, m.HalfHomeScore, m.HalfAwayScore,
                     m.Status, m.Handicap, m.ResultSpf, m.ResultHandicap, m.TotalGoals
              FROM dbo.FbMatch m
              LEFT JOIN dbo.FbLeague l ON l.LeagueId = m.LeagueId
              WHERE m.MatchId = @MatchId", new { MatchId = matchId });
    }

    /// <summary>按 fid 批量取完场赛事（自动结算用）。</summary>
    public async Task<Dictionary<long, MatchRecord>> GetFinishedByIdsAsync(IEnumerable<long> matchIds, CancellationToken ct = default)
    {
        var ids = matchIds.Where(x => x > 0).Distinct().ToArray();
        var map = new Dictionary<long, MatchRecord>();
        if (ids.Length == 0) return map;

        await using var conn = await _factory.OpenAsync(ct);
        for (var i = 0; i < ids.Length; i += 500)
        {
            var chunk = ids.Skip(i).Take(500).ToArray();
            var rows = await conn.QueryAsync<MatchRecord>(new CommandDefinition(
                @"SELECT m.MatchId, m.LeagueId, COALESCE(l.Name, m.LeagueName) AS LeagueName, m.MatchTime,
                         m.HomeTeamId, m.AwayTeamId, m.HomeTeamName, m.AwayTeamName,
                         m.HomeScore, m.AwayScore, m.HalfHomeScore, m.HalfAwayScore,
                         m.Status, m.Handicap, m.ResultSpf, m.ResultHandicap, m.TotalGoals
                  FROM dbo.FbMatch m
                  LEFT JOIN dbo.FbLeague l ON l.LeagueId = m.LeagueId
                  WHERE m.HomeScore IS NOT NULL AND m.AwayScore IS NOT NULL
                    AND (m.Status = 3 OR m.MatchTime <= DATEADD(minute, -150, GETDATE()))
                    AND m.MatchId IN @ids",
                new { ids = chunk }, cancellationToken: ct));
            foreach (var r in rows) map[r.MatchId] = r;
        }
        return map;
    }

    /// <summary>哪些场次已取消（FbMatch.Status=2）。购票结算按官方规则把取消腿按赔率 1 计，退本金。</summary>
    public async Task<HashSet<long>> GetCancelledByIdsAsync(IEnumerable<long> matchIds, CancellationToken ct = default)
    {
        var ids = matchIds.Where(x => x > 0).Distinct().ToArray();
        var set = new HashSet<long>();
        if (ids.Length == 0) return set;

        await using var conn = await _factory.OpenAsync(ct);
        for (var i = 0; i < ids.Length; i += 500)
        {
            var chunk = ids.Skip(i).Take(500).ToArray();
            var rows = await conn.QueryAsync<long>(new CommandDefinition(
                @"SELECT MatchId FROM dbo.FbMatch
                  WHERE Status = 2 AND MatchId IN @ids",
                new { ids = chunk }, cancellationToken: ct));
            foreach (var id in rows) set.Add(id);
        }
        return set;
    }

    /// <summary>北单这场开出的让球数（最近一张北单受注卡）。</summary>
    public async Task<decimal?> GetBdHandicapAsync(long matchId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<decimal?>(new CommandDefinition(
            @"SELECT TOP 1 Handicap FROM dbo.BettingCard
              WHERE Board = 'bd' AND MatchId = @id AND Handicap IS NOT NULL
              ORDER BY CapturedAt DESC",
            new { id = matchId }, cancellationToken: ct));
    }

    /// <summary>场次号 → 最近一张受注卡的 MatchId（结算补链）。</summary>
    public async Task<long?> ResolveMatchIdByNoAsync(string? matchNo, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(matchNo)) return null;
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            @"SELECT TOP 1 MatchId FROM dbo.BettingCard
              WHERE MatchNo=@no AND MatchId IS NOT NULL
              ORDER BY CapturedAt DESC",
            new { no = matchNo.Trim() }, cancellationToken: ct));
    }

    /// <summary>主客队名对齐最近一场。忽略空格和 FC、俱乐部、队这类后缀，不猜别名。</summary>
    public async Task<long?> ResolveMatchIdByTeamsAsync(string? home, string? away, CancellationToken ct = default)
    {
        var homeKey = TeamNames.Key(home);
        var awayKey = TeamNames.Key(away);
        if (homeKey.Length < 2 || awayKey.Length < 2) return null;

        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<TeamNameHit>(new CommandDefinition(
            @"SELECT TOP 80 MatchId, HomeTeamName, AwayTeamName
              FROM dbo.FbMatch
              WHERE (HomeTeamName = @h OR HomeTeamName LIKE @hLike)
                AND (AwayTeamName = @a OR AwayTeamName LIKE @aLike)
              ORDER BY CASE WHEN Status = 3 THEN 0 ELSE 1 END, MatchTime DESC",
            new
            {
                h = home!.Trim(),
                a = away!.Trim(),
                hLike = TeamNames.ContainsPattern(homeKey),
                aLike = TeamNames.ContainsPattern(awayKey)
            }, cancellationToken: ct));

        foreach (var row in rows)
        {
            if (TeamNames.Same(home, row.HomeTeamName) && TeamNames.Same(away, row.AwayTeamName))
                return row.MatchId;
        }

        return null;
    }

    public async Task<List<LeagueStat>> GetLeagueStatsAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<LeagueStat>(
            @"SELECT m.LeagueId, COALESCE(l.Name, m.LeagueName, N'未知') AS LeagueName,
                     COUNT(*) AS Matches,
                     SUM(CASE WHEN m.ResultSpf = '3' THEN 1 ELSE 0 END) AS HomeWins,
                     SUM(CASE WHEN m.ResultSpf = '1' THEN 1 ELSE 0 END) AS Draws,
                     SUM(CASE WHEN m.ResultSpf = '0' THEN 1 ELSE 0 END) AS AwayWins,
                     CAST(ROUND(AVG(CAST(m.TotalGoals AS FLOAT)), 2) AS FLOAT) AS AvgGoals,
                     CAST(ROUND(AVG(CASE WHEN m.TotalGoals >= 3 THEN 1.0 ELSE 0 END) * 100, 2) AS FLOAT) AS Over25Rate,
                     CAST(ROUND(AVG(CASE WHEN m.HomeScore > 0 AND m.AwayScore > 0 THEN 1.0 ELSE 0 END) * 100, 2) AS FLOAT) AS BothScoreRate
              FROM dbo.FbMatch m
              LEFT JOIN dbo.FbLeague l ON l.LeagueId = m.LeagueId
              WHERE m.Status = 3 AND m.MatchTime >= @From AND m.MatchTime < @To
              GROUP BY m.LeagueId, COALESCE(l.Name, m.LeagueName, N'未知')
              HAVING COUNT(*) >= 3
              ORDER BY COUNT(*) DESC",
            new
            {
                From = from.ToDateTime(TimeOnly.MinValue),
                To = to.AddDays(1).ToDateTime(TimeOnly.MinValue)
            });
        return rows.ToList();
    }

    public async Task<List<dynamic>> GetOverviewAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync(@"
SELECT (SELECT COUNT(*) FROM dbo.FbMatch)                          AS MatchCount,
       (SELECT COUNT(*) FROM dbo.FbMatch WHERE Status = 3)         AS FinishedCount,
       (SELECT COUNT(*) FROM dbo.FbLeague)                         AS LeagueCount,
       (SELECT COUNT(*) FROM dbo.FbTeam)                           AS TeamCount,
       (SELECT CONVERT(varchar(10), MIN(MatchTime), 23) FROM dbo.FbMatch) AS FirstDate,
       (SELECT CONVERT(varchar(10), MAX(MatchTime), 23) FROM dbo.FbMatch) AS LastDate");
        return rows.ToList();
    }

    private sealed class TeamNameHit
    {
        public long MatchId { get; set; }
        public string HomeTeamName { get; set; } = "";
        public string AwayTeamName { get; set; } = "";
    }
}
