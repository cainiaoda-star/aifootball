using Dapper;

namespace AiFootBall.Data;

/// <summary>
/// 变盘台账读取（写入在 <see cref="JcOddsRepository.SaveSnapshotAsync"/> 里，与快照同事务）。
///
/// 为什么单独一张表而不是视图：变盘是「事件」，要能被别的东西引用（出注时标记、
/// 结算时回看、日报里点名）。视图每次现算 124 万行，且无法记录「当时检测到的时刻」。
/// </summary>
public sealed class OddsMoveRepository
{
    private readonly SqlConnectionFactory _factory;

    public OddsMoveRepository(SqlConnectionFactory factory) => _factory = factory;

    /// <summary>某一天（按开赛日）的变盘，按发生时间倒序。</summary>
    public async Task<List<OddsMoveRow>> GetByMatchDayAsync(
        DateOnly day, int take = 500, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<OddsMoveRow>(new CommandDefinition(
            @"SELECT TOP (@take) v.MoveId, v.MatchId, v.MatchNo, v.LeagueName, v.HomeTeam, v.AwayTeam, v.MatchTime,
                     v.PlayType, v.OptionCode, v.OptionName, v.PrevOdds, v.CurrOdds, v.ChangePct,
                     v.PrevAt, v.MoveAt, v.MinutesToKick, m.Status AS MatchStatus
              FROM dbo.FbOddsMove v
              JOIN dbo.FbMatch m ON m.MatchId = v.MatchId
              WHERE CONVERT(date, v.MatchTime) = @day
              ORDER BY v.MoveAt DESC, v.MatchId, v.PlayType, v.OptionCode",
            new { day = day.ToDateTime(TimeOnly.MinValue), take }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>
    /// 按开赛时间范围取变盘，按发生时间倒序。
    /// 竞彩档期和自然日错位：周四档的欧战多在次日凌晨开赛，单看 MatchTime=今天会漏掉一整批在售场次。
    /// 所以「今天在售」要用 [今天 00:00, 后天 00:00) 这个窗口（今天 + 明天凌晨）。
    /// </summary>
    public async Task<List<OddsMoveRow>> GetByMatchRangeAsync(
        DateTime from, DateTime to, int take = 500, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<OddsMoveRow>(new CommandDefinition(
            @"SELECT TOP (@take) v.MoveId, v.MatchId, v.MatchNo, v.LeagueName, v.HomeTeam, v.AwayTeam, v.MatchTime,
                     v.PlayType, v.OptionCode, v.OptionName, v.PrevOdds, v.CurrOdds, v.ChangePct,
                     v.PrevAt, v.MoveAt, v.MinutesToKick, m.Status AS MatchStatus
              FROM dbo.FbOddsMove v
              JOIN dbo.FbMatch m ON m.MatchId = v.MatchId
              WHERE v.MatchTime >= @from AND v.MatchTime < @to
              ORDER BY v.MoveAt DESC, v.MatchId, v.PlayType, v.OptionCode",
            new { from, to, take }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>某一场的全部变盘，按时间正序——用来看「方向是怎么一步步走出来的」。</summary>
    public async Task<List<OddsMoveRow>> GetByMatchAsync(long matchId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<OddsMoveRow>(new CommandDefinition(
            @"SELECT MoveId, MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
                     PlayType, OptionCode, OptionName, PrevOdds, CurrOdds, ChangePct,
                     PrevAt, MoveAt, MinutesToKick
              FROM dbo.FbOddsMove
              WHERE MatchId = @matchId
              ORDER BY MoveAt, PlayType, OptionCode",
            new { matchId }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>
    /// 近 N 天的变盘密度，按天 + 玩法。用来回答「今天有没有异常」——
    /// 某天某玩法条数突然翻倍，通常意味着临场有资金进场。
    /// </summary>
    public async Task<List<OddsMoveDaily>> GetDailyAsync(int days = 14, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<OddsMoveDaily>(new CommandDefinition(
            @"SELECT CONVERT(date, MatchTime)  AS [Day],
                     PlayType,
                     COUNT(*)                AS Moves,
                     COUNT(DISTINCT MatchId) AS Matches,
                     MIN(MoveAt)             AS FirstMove,
                     MAX(MoveAt)             AS LastMove
              FROM dbo.FbOddsMove
              WHERE MatchTime >= DATEADD(day, -1 * @days, CAST(GETDATE() AS date))
              GROUP BY CONVERT(date, MatchTime), PlayType
              ORDER BY [Day] DESC, PlayType",
            new { days }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>
    /// 最近一次采样时刻。页面上显示「监控在活着」——没这条，
    /// 分不清「今天没变盘」和「检测根本没跑」。
    ///
    /// 取 FbJcOddsHistory 而不是 FbOddsMove：变盘表只在有变动时才有新行，
    /// 一个平静的下午会让它停在几小时前，看着像进程死了。
    /// 采样和检测在同一个事务里，采样时间就是检测时间。
    /// </summary>
    public async Task<DateTime?> GetLastScanAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            @"SELECT MAX(CapturedAt) FROM dbo.FbJcOddsHistory", cancellationToken: ct));
    }
}

public sealed class OddsMoveRow
{
    public long MoveId { get; set; }
    public long MatchId { get; set; }
    public string? MatchNo { get; set; }
    public string? LeagueName { get; set; }
    public string? HomeTeam { get; set; }
    public string? AwayTeam { get; set; }
    public DateTime? MatchTime { get; set; }
    public string PlayType { get; set; } = "";
    public string OptionCode { get; set; } = "";
    public string? OptionName { get; set; }
    public decimal PrevOdds { get; set; }
    public decimal CurrOdds { get; set; }
    public decimal ChangePct { get; set; }
    public DateTime PrevAt { get; set; }
    public DateTime MoveAt { get; set; }
    public int? MinutesToKick { get; set; }

    /// <summary>FbMatch.Status（联表带出）：3 = 已完赛。前端用它把踢完的比赛折叠掉。</summary>
    public byte? MatchStatus { get; set; }
}

public sealed class OddsMoveDaily
{
    public DateTime Day { get; set; }
    public string PlayType { get; set; } = "";
    public int Moves { get; set; }
    public int Matches { get; set; }
    public DateTime FirstMove { get; set; }
    public DateTime LastMove { get; set; }
}
