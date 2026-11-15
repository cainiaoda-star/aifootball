using AiFootBall.Core.Models;
using Dapper;

namespace AiFootBall.Data;

/// <summary>
/// 稳胆台账（dbo.FbSteadyPick，2026-11-09 加）。
///
/// 为什么另开一张表，不塞进 FbMilkPick：
///   · 主榜的每一次重算都是 `DELETE 未结算行 + INSERT`，两种产品混在一张表里，
///     任何一次主榜重算都会把稳胆的行一起删掉（或反之），两边的账都不可信；
///   · 两个栏目的命中率差 5 倍（14% vs 77%），合在一起统计出来的数字谁也不是。
///   **分开存、分开结算、分开记账，这是硬要求。**
///
/// 开赛保护与主榜同口径：重算只重写「还没开赛」的注，开赛 = 票已买，不许被重算冲掉。
/// </summary>
public sealed class SteadyPickRepository
{
    private readonly SqlConnectionFactory _factory;

    public SteadyPickRepository(SqlConnectionFactory factory) => _factory = factory;

    /// <summary>建表（幂等）。首次部署时由作业自己建，不依赖手工 DDL。</summary>
    public async Task EnsureTableAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            @"IF OBJECT_ID('dbo.FbSteadyPick') IS NULL
              CREATE TABLE dbo.FbSteadyPick
              (
                  PickId      BIGINT IDENTITY(1,1) PRIMARY KEY,
                  PickDate    DATE NOT NULL,
                  MatchId     BIGINT NOT NULL,
                  MatchNo     NVARCHAR(20) NOT NULL,
                  LeagueName  NVARCHAR(80) NULL,
                  HomeTeam    NVARCHAR(80) NULL,
                  AwayTeam    NVARCHAR(80) NULL,
                  MatchTime   DATETIME2(0) NULL,
                  Side        CHAR(1) NOT NULL,          -- 3 主胜 / 1 平 / 0 客胜（竞彩胜平负，不让球）
                  SideName    NVARCHAR(10) NULL,
                  Odds        DECIMAL(8,2) NOT NULL,
                  FinalOdds   DECIMAL(8,2) NULL,           -- 终版价（开赛前最后一次采样），2026-11-10 加
                  FinalAt     DATETIME2(0) NULL,           -- 终版价对应时刻
                  FinalSource NVARCHAR(12) NULL,           -- jc-close
                  MarketPct   DECIMAL(6,2) NULL,         -- 市场去水概率，选它的唯一依据
                  [Rank]      INT NULL,                  -- 当日 1..3，1 = 首选
                  Note        NVARCHAR(200) NULL,
                  ActualScore NVARCHAR(20) NULL,
                  Hit         BIT NULL,
                  SettledAt   DATETIME2(0) NULL,
                  CreatedAt   DATETIME2(0) NOT NULL DEFAULT SYSDATETIME()
              );
              -- 老表兜底（2026-11-10 终版价增列，逐列判断，幂等）
              IF COL_LENGTH('dbo.FbSteadyPick','FinalOdds') IS NULL
                  ALTER TABLE dbo.FbSteadyPick ADD FinalOdds DECIMAL(8,2) NULL;
              IF COL_LENGTH('dbo.FbSteadyPick','FinalAt') IS NULL
                  ALTER TABLE dbo.FbSteadyPick ADD FinalAt DATETIME2(0) NULL;
              IF COL_LENGTH('dbo.FbSteadyPick','FinalSource') IS NULL
                  ALTER TABLE dbo.FbSteadyPick ADD FinalSource NVARCHAR(12) NULL;
              IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_SteadyPick_Date'
                             AND object_id=OBJECT_ID('dbo.FbSteadyPick'))
                  CREATE INDEX IX_SteadyPick_Date ON dbo.FbSteadyPick (PickDate, MatchId);",
            cancellationToken: ct));
    }

    /// <summary>
    /// 重写当日稳胆。
    ///
    /// 去重键 = PickDate + MatchId（一天一场最多一注，因为口径就是「这场最看好的那一档」）。
    /// 已开赛的行不删也不改：开赛以后赔率还在动，但用户买的是当时那一注，
    /// 让重算去改它等于事后改账。
    /// </summary>
    public async Task<int> SaveDayAsync(DateTime pickDate, IReadOnlyList<SteadyPick> picks, CancellationToken ct = default)
    {
        var d = pickDate.Date;
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(
            @"DELETE FROM dbo.FbSteadyPick
              WHERE PickDate=@d AND Hit IS NULL AND SettledAt IS NULL
                AND MatchTime > DATEADD(minute, 5, SYSDATETIME())",
            new { d }, tx, cancellationToken: ct));

        var rows = 0;
        foreach (var p in picks)
        {
            if (p.MatchId is not long mid) continue;
            rows += await conn.ExecuteAsync(new CommandDefinition(
                @"INSERT INTO dbo.FbSteadyPick
                    (PickDate, MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
                     Side, SideName, Odds, MarketPct, [Rank], Note)
                  SELECT @d, @mid, @no, @lg, @home, @away, @time,
                         @side, @sideName, @odds, @pct, @rank, @note
                  WHERE NOT EXISTS (
                      SELECT 1 FROM dbo.FbSteadyPick
                       WHERE PickDate=@d AND MatchId=@mid)",
                new
                {
                    d, mid, no = p.MatchNo, lg = p.LeagueName, home = p.HomeTeam, away = p.AwayTeam,
                    time = p.MatchTime, side = p.Side.ToString(), sideName = p.SideName,
                    odds = p.Odds, pct = p.MarketPct, rank = p.Rank, note = p.Note
                }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return rows;
    }

    /// <summary>
    /// 按比分结算稳胆（竞彩胜平负，不让球）。
    ///
    /// 完场判据与主榜/北单一致：比分齐全 && (Status=3 || 开赛已过 150 分钟)。
    /// 不信 Status 是因为重采会把 3 洗回 0，只在 Status 上看会让注永远挂在「待结算」。
    /// 比分缺失的一律不结算——腰斩/取消的场次本来就不该进命中率。
    /// 幂等：只动 Hit IS NULL 的行。
    /// </summary>
    public async Task<int> SettleAsync(int lookbackDays = 7, CancellationToken ct = default)
    {
        var lookback = Math.Clamp(lookbackDays, 1, 90);
        await using var conn = await _factory.OpenAsync(ct);

        var pending = (await conn.QueryAsync<PendingRow>(new CommandDefinition(
            @"SELECT p.PickId, p.Side,
                     CAST(m.HomeScore AS INT) AS HomeScore,
                     CAST(m.AwayScore AS INT) AS AwayScore
                FROM dbo.FbSteadyPick p
                JOIN dbo.FbMatch m ON m.MatchId = p.MatchId
               WHERE p.Hit IS NULL
                 AND p.PickDate >= DATEADD(day, -@lookback, CAST(SYSDATETIME() AS DATE))
                 AND m.HomeScore IS NOT NULL AND m.AwayScore IS NOT NULL
                 AND (m.Status = 3 OR p.MatchTime <= DATEADD(minute, -150, SYSDATETIME()))",
            new { lookback }, cancellationToken: ct))).ToList();

        if (pending.Count == 0) return 0;

        await using var tx = await conn.BeginTransactionAsync(ct);
        var settled = 0;
        foreach (var r in pending)
        {
            if (string.IsNullOrEmpty(r.Side)) continue;
            var hit = SteadySettlement.Judge(r.Side[0], r.HomeScore, r.AwayScore);
            if (hit is null) continue;
            settled += await conn.ExecuteAsync(new CommandDefinition(
                @"UPDATE dbo.FbSteadyPick
                     SET ActualScore=@score, Hit=@hit, SettledAt=SYSDATETIME()
                   WHERE PickId=@id AND Hit IS NULL",
                new
                {
                    id = r.PickId,
                    score = SteadySettlement.ScoreText(r.HomeScore, r.AwayScore),
                    hit = hit.Value ? 1 : 0
                }, tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);
        return settled;
    }

    /// <summary>
    /// 读某日稳胆（含近 30 天战绩）。
    ///
    /// 当日没有数据时**回退到最近一个有数据的日子**，并把实际日期放进 <see cref="SteadyPickDay.Day"/>——
    /// 前端必须显示这个日期，不能悄悄把昨天的注说成今天的。
    /// 回退的理由：稳胆是「每天赔率最低的三场」，遇到国际赛窗口（NoPickLeagues 全拦）会连续几天 0 注，
    /// 页面空着会让人以为功能坏了；而显示「X 日的注」是诚实的。
    /// </summary>
    public async Task<SteadyPickDay> GetDayAsync(DateTime pickDate, CancellationToken ct = default)
    {
        var d = pickDate.Date;
        await using var conn = await _factory.OpenAsync(ct);

        var rows = await QueryAsync(conn, d, ct);

        if (rows.Count == 0)
        {
            var last = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                "SELECT MAX(PickDate) FROM dbo.FbSteadyPick WHERE PickDate < @d",
                new { d }, cancellationToken: ct));
            if (last is DateTime ld)
            {
                d = ld.Date;
                rows = await QueryAsync(conn, d, ct);
            }
        }

        return new SteadyPickDay
        {
            Day = d,
            Picks = rows,
            Stats = await GetStatsAsync(conn, 30, ct)
        };
    }

    private static async Task<List<SteadyPick>> QueryAsync(
        System.Data.Common.DbConnection conn, DateTime d, CancellationToken ct)
        => (await conn.QueryAsync<SteadyPick>(new CommandDefinition(
            @"SELECT PickDate, MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
                     Side, SideName, Odds, FinalOdds, FinalAt, FinalSource,
                     MarketPct, [Rank], Note, ActualScore, Hit, SettledAt
              FROM dbo.FbSteadyPick
              WHERE PickDate=@d
              ORDER BY ISNULL([Rank], 99), Odds",
            new { d }, cancellationToken: ct))).ToList();

    /// <summary>
    /// 近 N 天战绩。口径与主榜台账一致：
    /// 分母 = 已结算注数，回报 = 命中赔率合计 ÷ 注数（平注 1 单位），未结算单独计数。
    ///
    /// **两栏（2026-11-10 加）**：Roi 用推荐价、RoiFinal 用终版价（停售前最后采样）。
    /// 稳胆赔率只有 1.0~1.75，3% 的价差就能翻转结论，所以跟单评价一律看 RoiFinal，
    /// Roi 只用于和 11-09 之前的历史数字衔接。
    /// </summary>
    public async Task<SteadyStats> GetStatsAsync(int days = 30, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await GetStatsAsync(conn, days, ct);
    }

    private static async Task<SteadyStats> GetStatsAsync(
        System.Data.Common.DbConnection conn, int days, CancellationToken ct)
    {
        var n = Math.Clamp(days, 1, 365);
        var stats = await conn.QuerySingleAsync<SteadyStats>(new CommandDefinition(
            @"SELECT COUNT(CASE WHEN Hit IS NOT NULL THEN 1 END) AS Settled,
                     ISNULL(SUM(CASE WHEN Hit = 1 THEN 1 ELSE 0 END), 0) AS Hits,
                     ISNULL(SUM(CASE WHEN Hit IS NULL THEN 1 ELSE 0 END), 0) AS Pending,
                     CAST(ISNULL(AVG(CASE WHEN Hit IS NOT NULL THEN Odds END), 0) AS DECIMAL(8,2)) AS AvgOdds,
                     CAST(ISNULL(SUM(CASE WHEN Hit = 1 THEN Odds ELSE 0 END) * 100.0
                          / NULLIF(COUNT(CASE WHEN Hit IS NOT NULL THEN 1 END), 0), 0) AS DECIMAL(6,1)) AS HitPct,
                     CAST(ISNULL(SUM(CASE WHEN Hit = 1 THEN Odds ELSE 0 END)
                          / NULLIF(COUNT(CASE WHEN Hit IS NOT NULL THEN 1 END), 0), 0) AS DECIMAL(6,3)) AS Roi,
                     ISNULL(SUM(CASE WHEN Hit = 1 AND FinalOdds IS NOT NULL THEN 1 ELSE 0 END), 0) AS FinalHits,
                     CAST(ISNULL(SUM(CASE WHEN Hit = 1 THEN ISNULL(FinalOdds, 0) ELSE 0 END)
                          / NULLIF(COUNT(CASE WHEN Hit IS NOT NULL THEN 1 END), 0), 0) AS DECIMAL(6,3)) AS RoiFinal
              FROM dbo.FbSteadyPick
              WHERE PickDate >= DATEADD(day, -@n, CAST(SYSDATETIME() AS DATE))",
            new { n }, cancellationToken: ct));
        stats.Days = n;
        stats.FinalCoverage = FinalPriceMath.HitCoverage(stats.Hits, stats.FinalHits);
        return stats;
    }

    /// <summary>逐注明细（前端「稳胆战绩」表用）。</summary>
    public async Task<List<SteadyPick>> GetRowsAsync(int days = 30, CancellationToken ct = default)
    {
        var n = Math.Clamp(days, 1, 365);
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<SteadyPick>(new CommandDefinition(
            @"SELECT PickDate, MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
                     Side, SideName, Odds, FinalOdds, FinalAt, FinalSource,
                     MarketPct, [Rank], Note, ActualScore, Hit, SettledAt
              FROM dbo.FbSteadyPick
              WHERE PickDate >= DATEADD(day, -@n, CAST(SYSDATETIME() AS DATE))
              ORDER BY PickDate DESC, ISNULL([Rank], 99)",
            new { n }, cancellationToken: ct))).ToList();
    }

    private sealed class PendingRow
    {
        public long PickId { get; set; }
        public string? Side { get; set; }
        public int HomeScore { get; set; }
        public int AwayScore { get; set; }
    }
}
