using AiFootBall.Core.Models;
using Dapper;
using Microsoft.Data.SqlClient;

namespace AiFootBall.Data;

/// <summary>
/// 空跑台账（2026-11-08 加）。回答一个问题：**照首页榜面买，两周下来是赚是亏。**
///
/// 与 FbMilkPick 的分工：
///   FbMilkPick  = 活的当日榜，每次重算 DELETE 未结算行再 INSERT，随时会变。
///   FbPaperTrade = 冻结的终版快照，只增不改（撤注标记除外），专供长期评价。
///
/// 冻结时点取「停售后」：工作日 21:58、周末 22:58 停售，次日凌晨再补跑一次，
/// 保证即使 23:30 那次进程没起来，第二天也能补上（`FreezeMissingAsync`）。
/// </summary>
public sealed class PaperTradeRepository
{
    private readonly SqlConnectionFactory _factory;

    public PaperTradeRepository(SqlConnectionFactory factory) => _factory = factory;

    private const string Ddl = @"
IF OBJECT_ID(N'dbo.FbPaperTrade', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.FbPaperTrade (
        TradeId              BIGINT IDENTITY(1,1) NOT NULL,
        TradeDate            DATE NOT NULL,
        MatchId              BIGINT NOT NULL,
        MatchNo              NVARCHAR(32) NOT NULL,
        LeagueName           NVARCHAR(64) NULL,
        HomeTeam             NVARCHAR(64) NULL,
        AwayTeam             NVARCHAR(64) NULL,
        MatchTime            DATETIME2 NULL,
        PlayType             VARCHAR(10) NOT NULL,
        OptionCode           VARCHAR(20) NOT NULL,
        OptionName           NVARCHAR(64) NOT NULL,
        Odds                 DECIMAL(10,3) NOT NULL,
        FinalOdds            DECIMAL(10,3) NULL,
        FinalAt              DATETIME2(0) NULL,
        FinalSource          NVARCHAR(12) NULL,
        ModelProbability     DECIMAL(10,4) NULL,
        BaselineProbability  DECIMAL(10,4) NULL,
        BaselineEv           DECIMAL(10,4) NULL,
        ModelEv              DECIMAL(10,4) NULL,
        CounterHigh          BIT NOT NULL CONSTRAINT DF_FbPaperTrade_CH DEFAULT(0),
        ModelOverconfident   BIT NOT NULL CONSTRAINT DF_FbPaperTrade_OC DEFAULT(0),
        ModelLowConfidence   BIT NOT NULL CONSTRAINT DF_FbPaperTrade_LC DEFAULT(0),
        Voided               BIT NOT NULL CONSTRAINT DF_FbPaperTrade_VD DEFAULT(0),
        AlertSeverity        NVARCHAR(16) NULL,
        AlertReason          NVARCHAR(512) NULL,
        FrozenAt             DATETIME2(0) NOT NULL CONSTRAINT DF_FbPaperTrade_FA DEFAULT(SYSDATETIME()),
        ActualScore          VARCHAR(16) NULL,
        ActualHalfScore      VARCHAR(16) NULL,
        Hit                  BIT NULL,
        SettledAt            DATETIME2(0) NULL,
        Pnl                  DECIMAL(10,4) NULL,
        CONSTRAINT PK_FbPaperTrade PRIMARY KEY (TradeId),
        CONSTRAINT UQ_FbPaperTrade UNIQUE (TradeDate, MatchId, PlayType, OptionCode)
    );
    CREATE INDEX IX_FbPaperTrade_Date ON dbo.FbPaperTrade (TradeDate) INCLUDE (PlayType, Odds, Hit, Voided);
END
-- 老表兜底：终版价三列（2026-11-10 加，逐列判断，幂等）
IF OBJECT_ID(N'dbo.FbPaperTrade', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH('dbo.FbPaperTrade','FinalOdds') IS NULL
        ALTER TABLE dbo.FbPaperTrade ADD FinalOdds DECIMAL(10,3) NULL;
    IF COL_LENGTH('dbo.FbPaperTrade','FinalAt') IS NULL
        ALTER TABLE dbo.FbPaperTrade ADD FinalAt DATETIME2(0) NULL;
    IF COL_LENGTH('dbo.FbPaperTrade','FinalSource') IS NULL
        ALTER TABLE dbo.FbPaperTrade ADD FinalSource NVARCHAR(12) NULL;
END";

    /// <summary>确保表存在（幂等）。</summary>
    public async Task EnsureTableAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(Ddl, cancellationToken: ct));
    }

    /// <summary>
    /// 把某一天的终版榜冻结进台账。幂等：
    ///   已存在的注（同 TradeDate+MatchId+PlayType+OptionCode）**不改赔率、不改概率**，
    ///   只同步「撤注 / 告警」——赔率是当时能买到的价，事后重算不算数。
    /// 返回 (新增, 同步撤注标记)。
    /// </summary>
    public async Task<(int Inserted, int Synced)> FreezeDayAsync(DateTime date, CancellationToken ct = default)
    {
        var d = date.Date;
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(Ddl, cancellationToken: ct));

        // 只冻「还在榜上」的注。已结算的不重冻（它们早就定稿），撤注的照样要进来。
        var rows = (await conn.QueryAsync<PaperTradeRow>(new CommandDefinition(
            @"SELECT @d AS TradeDate, MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
                     PlayType, OptionCode, OptionName, Odds,
                     ModelProbability, BaselineProbability, BaselineEv,
                     Ev AS ModelEv, CounterHigh, ModelOverconfident, ModelLowConfidence,
                     Voided, AlertSeverity, AlertReason
              FROM dbo.FbMilkPick
              WHERE PickDate = @d",
            new { d }, cancellationToken: ct))).ToList();

        if (rows.Count == 0) return (0, 0);

        var inserted = 0;
        var synced = 0;
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        foreach (var r in rows)
        {
            // 先试同步：只动撤注与告警，赔率/概率一律不覆盖。
            var upd = await conn.ExecuteAsync(new CommandDefinition(
                @"UPDATE dbo.FbPaperTrade
                  SET Voided = @voided, AlertSeverity = @sev, AlertReason = @reason
                  WHERE TradeDate = @d AND MatchId = @mid AND PlayType = @play AND OptionCode = @code
                    AND (Voided <> @voided
                         OR ISNULL(AlertSeverity,'') <> ISNULL(@sev,'')
                         OR ISNULL(AlertReason,'')  <> ISNULL(@reason,''))",
                new
                {
                    d, mid = r.MatchId, play = r.PlayType, code = r.OptionCode,
                    voided = r.Voided,
                    sev = r.AlertSeverity,
                    reason = Trim(r.AlertReason, 512)
                }, tx, cancellationToken: ct));
            synced += upd;
            if (upd > 0) continue;

            var ins = await conn.ExecuteAsync(new CommandDefinition(
                @"INSERT INTO dbo.FbPaperTrade
                    (TradeDate, MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
                     PlayType, OptionCode, OptionName, Odds,
                     ModelProbability, BaselineProbability, BaselineEv, ModelEv,
                     CounterHigh, ModelOverconfident, ModelLowConfidence,
                     Voided, AlertSeverity, AlertReason)
                  SELECT @d, @mid, @no, @lg, @home, @away, @time,
                         @play, @code, @name, @odds,
                         @prob, @basePct, @baseEv, @modelEv,
                         @ch, @oc, @lc,
                         @voided, @sev, @reason
                  WHERE NOT EXISTS (
                      SELECT 1 FROM dbo.FbPaperTrade
                      WHERE TradeDate = @d AND MatchId = @mid AND PlayType = @play AND OptionCode = @code)",
                new
                {
                    d, mid = r.MatchId, no = r.MatchNo, lg = r.LeagueName,
                    home = r.HomeTeam, away = r.AwayTeam, time = r.MatchTime,
                    play = r.PlayType, code = r.OptionCode, name = r.OptionName, odds = r.Odds,
                    prob = r.ModelProbability, basePct = r.BaselineProbability,
                    baseEv = r.BaselineEv, modelEv = r.ModelEv,
                    ch = r.CounterHigh, oc = r.ModelOverconfident, lc = r.ModelLowConfidence,
                    voided = r.Voided, sev = r.AlertSeverity, reason = Trim(r.AlertReason, 512)
                }, tx, cancellationToken: ct));
            inserted += ins;
        }

        await tx.CommitAsync(ct);
        return (inserted, synced);
    }

    /// <summary>
    /// 补冻最近 <paramref name="days"/> 天里「有榜但没冻」的日子。
    /// 进程在 23:30 没起来、或者部署当天漏跑，靠这个兜底。
    /// 返回补冻的天数与总注数。
    /// </summary>
    public async Task<(int Days, int Rows)> FreezeMissingAsync(int days, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(Ddl, cancellationToken: ct));

        var missing = (await conn.QueryAsync<DateTime>(new CommandDefinition(
            @"SELECT DISTINCT p.PickDate
              FROM dbo.FbMilkPick p
              WHERE p.PickDate >= DATEADD(day, -@days, CAST(GETDATE() AS date))
                AND p.PickDate <  CAST(GETDATE() AS date)
                AND NOT EXISTS (SELECT 1 FROM dbo.FbPaperTrade t WHERE t.TradeDate = p.PickDate)
              ORDER BY p.PickDate",
            new { days }, cancellationToken: ct))).ToList();

        var total = 0;
        foreach (var d in missing)
            total += (await FreezeDayAsync(d, ct)).Inserted;

        return (missing.Count, total);
    }

    /// <summary>
    /// 结算：把已完场但还没判定的台账行补上赛果与盈亏。
    /// 完场判据与 FbMilkPick 结算同源（比分齐全 + (Status=3 或 开赛 150 分钟后)），
    /// 半全场缺半场比分就留到下轮。
    /// 返回本次判定注数。
    /// </summary>
    public async Task<int> SettleAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        if (!await TableExistsAsync(conn, ct)) return 0;

        // 先同步最近两天的撤注标记：冻结发生在停售后，而 turn-guard 之后还会继续撤凌晨场次的注。
        await conn.ExecuteAsync(new CommandDefinition(
            @"UPDATE t
              SET t.Voided = p.Voided, t.AlertSeverity = p.AlertSeverity,
                  t.AlertReason = LEFT(p.AlertReason, 512)
              FROM dbo.FbPaperTrade t
              JOIN dbo.FbMilkPick p
                ON p.PickDate = t.TradeDate AND p.MatchId = t.MatchId
               AND p.PlayType = t.PlayType AND p.OptionCode = t.OptionCode
              WHERE t.TradeDate >= DATEADD(day, -2, CAST(GETDATE() AS date))
                AND t.SettledAt IS NULL
                AND (t.Voided <> p.Voided OR ISNULL(t.AlertSeverity,'') <> ISNULL(p.AlertSeverity,'')
                     OR ISNULL(t.AlertReason,'') <> ISNULL(LEFT(p.AlertReason,512),''))",
            new { }, cancellationToken: ct));

        var pending = (await conn.QueryAsync<PaperPending>(new CommandDefinition(
            @"SELECT t.TradeId, t.PlayType, t.OptionCode, t.Voided, t.Odds,
                     m.HomeScore, m.AwayScore, m.HalfHomeScore, m.HalfAwayScore, m.Status, m.MatchTime
              FROM dbo.FbPaperTrade t
              JOIN dbo.FbMatch m ON m.MatchId = t.MatchId
              WHERE t.Hit IS NULL
                AND m.HomeScore IS NOT NULL AND m.AwayScore IS NOT NULL
                AND (m.Status = 3 OR m.MatchTime <= DATEADD(minute, -150, GETDATE()))",
            new { }, cancellationToken: ct))).ToList();

        var settled = 0;
        foreach (var row in pending)
        {
            bool? hit;
            if (row.PlayType == JcPlayTypes.Bq)
            {
                if (row.HalfHomeScore is not int hh || row.HalfAwayScore is not int ha) continue;
                hit = MilkPickMath.IsHit(row.PlayType, row.OptionCode,
                    row.HomeScore!.Value, row.AwayScore!.Value, hh, ha);
            }
            else
            {
                hit = MilkPickMath.IsHit(row.PlayType, row.OptionCode,
                    row.HomeScore!.Value, row.AwayScore!.Value, null, null);
            }

            var score = $"{row.HomeScore}:{row.AwayScore}";
            var half = row.HalfHomeScore is int h1 && row.HalfAwayScore is int h2 ? $"{h1}:{h2}" : null;

            // 撤注 = 没下这笔，盈亏记 NULL，不进回报分母。命中与否照记，留着评价「撤对了没有」。
            var pnl = PaperTradeMath.PnlOf(row.Voided, hit, row.Odds);

            settled += await conn.ExecuteAsync(new CommandDefinition(
                @"UPDATE dbo.FbPaperTrade
                  SET Hit = @hit, ActualScore = @score, ActualHalfScore = @half,
                      Pnl = @pnl, SettledAt = SYSDATETIME()
                  WHERE TradeId = @id", new { hit, score, half, pnl, id = row.TradeId }, cancellationToken: ct));
        }

        // 取消场次：开赛时间已过、比分仍空、Status=2。与 FbMilkPick 同一套防误杀条件。
        //
        // ★ 2026-11-09 修（真事故）：条件原来是「开赛时间已过」，但**比赛正在进行中**同样满足
        // 「开赛时间已过 + 比分仍是 NULL」。数据源在上下半场会把进行中的比赛短暂标成 Status=2，
        // 于是 11-09 04:19 那次 paper-settle 把正在踢的 朗斯-马赛（实际 1:2）判成「取消」，
        // 写死终态、永久退出结算队列。用户看到的就是「这注永远挂在那里没有结果」。
        // 真正的取消场次在开赛 150 分钟后也拿不到比分，所以把判据收紧到 150 分钟，
        // 和上面完场判据同一条时间线——同一个时间窗里，要么有赛果，要么才是取消。
        await conn.ExecuteAsync(new CommandDefinition(
            @"UPDATE t SET t.ActualScore = N'取消', t.SettledAt = SYSDATETIME()
              FROM dbo.FbPaperTrade t
              JOIN dbo.FbMatch m ON m.MatchId = t.MatchId
              WHERE t.Hit IS NULL AND t.SettledAt IS NULL AND m.Status = 2
                AND m.MatchTime < DATEADD(minute, -150, SYSDATETIME())
                AND m.HomeScore IS NULL AND m.AwayScore IS NULL",
            new { }, cancellationToken: ct));

        // 撤注且已完场的：Pnl 保持 NULL（没下注），但别让它永远挂在 pending 里。
        await conn.ExecuteAsync(new CommandDefinition(
            @"UPDATE t SET t.SettledAt = SYSDATETIME(), t.Pnl = NULL
              FROM dbo.FbPaperTrade t
              JOIN dbo.FbMatch m ON m.MatchId = t.MatchId
              WHERE t.SettledAt IS NULL AND t.Voided = 1
                AND m.HomeScore IS NOT NULL AND m.AwayScore IS NOT NULL
                AND (m.Status = 3 OR m.MatchTime <= DATEADD(minute, -150, GETDATE()))",
            new { }, cancellationToken: ct));

        return settled;
    }

    /// <summary>
    /// 累计战绩。分母只算「已结算且未撤注」，撤注单独计数。
    /// 报表不参与任何决策，只回答「跟到现在会是几分」。
    /// </summary>
    public async Task<PaperReport> GetReportAsync(int days, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 365);
        var to = DateTime.Today;
        var from = to.AddDays(-(days - 1));

        await using var conn = await _factory.OpenAsync(ct);
        if (!await TableExistsAsync(conn, ct))
            return new PaperReport(days, from, to, 0,
                PaperSlice.Empty("全部"), Array.Empty<PaperSlice>(),
                Array.Empty<PaperSlice>(), Array.Empty<PaperSlice>());

        var rows = (await conn.QueryAsync<PaperTradeRow>(new CommandDefinition(
            @"SELECT TradeId, TradeDate, MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
                     PlayType, OptionCode, OptionName, Odds, ModelProbability, BaselineProbability,
                     BaselineEv, ModelEv, CounterHigh, ModelOverconfident, ModelLowConfidence,
                     Voided, AlertSeverity, AlertReason, FrozenAt,
                     FinalOdds, FinalAt, FinalSource,
                     ActualScore, ActualHalfScore, Hit, SettledAt, Pnl
              FROM dbo.FbPaperTrade
              WHERE TradeDate >= @from AND TradeDate <= @to
              ORDER BY TradeDate, MatchTime, PlayType",
            new { from, to }, cancellationToken: ct))).ToList();

        var frozenDays = rows.Select(r => r.TradeDate.Date).Distinct().Count();

        // 「以发布的为准」（用户 2026-11-09 拍板）：同一场同玩法若中途换过档，
        // 台账里会留下两行（只增不改），但只有最后发布的那一档是一注。
        // 换档的旧档不进任何切片，只在报表的 Superseded 里报个数，方便和榜面对账。
        var superseded = PaperTradeMath.SupersededCount(rows);
        var final = PaperTradeMath.FinalVersion(rows);

        return new PaperReport(
            days, from, to, frozenDays,
            Slice("全部", final),
            final.GroupBy(r => r.PlayType).OrderBy(g => g.Key)
                .Select(g => Slice(g.Key, g.ToList())).ToList(),
            final.GroupBy(r => r.TradeDate.ToString("MM-dd")).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => Slice(g.Key, g.ToList())).ToList(),
            new[]
            {
                Slice("常规注", final.Where(r => !r.CounterHigh && !r.ModelOverconfident && !r.ModelLowConfidence).ToList()),
                Slice("高赔单列", final.Where(r => r.CounterHigh).ToList()),
                Slice("模型高估", final.Where(r => r.ModelOverconfident).ToList()),
                Slice("模型低可信", final.Where(r => r.ModelLowConfidence).ToList())
            },
            superseded);
    }

    /// <summary>台账明细（页面上逐注看）。</summary>
    public async Task<List<PaperTradeRow>> GetRowsAsync(int days, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 365);
        var to = DateTime.Today;
        var from = to.AddDays(-(days - 1));
        await using var conn = await _factory.OpenAsync(ct);
        if (!await TableExistsAsync(conn, ct)) return new();

        var rows = (await conn.QueryAsync<PaperTradeRow>(new CommandDefinition(
            @"SELECT TradeId, TradeDate, MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
                     PlayType, OptionCode, OptionName, Odds, ModelProbability, BaselineProbability,
                     BaselineEv, ModelEv, CounterHigh, ModelOverconfident, ModelLowConfidence,
                     Voided, AlertSeverity, AlertReason, FrozenAt,
                     FinalOdds, FinalAt, FinalSource,
                     ActualScore, ActualHalfScore, Hit, SettledAt, Pnl
              FROM dbo.FbPaperTrade
              WHERE TradeDate >= @from AND TradeDate <= @to
              ORDER BY TradeDate DESC, MatchTime, PlayType",
            new { from, to }, cancellationToken: ct))).ToList();

        // 明细保留全部行（含换档旧档），但标出哪行不算注——否则「明细 8 行、报表 7 注」没法对账。
        var keep = PaperTradeMath.FinalVersion(rows).Select(r => r.TradeId).ToHashSet();
        foreach (var r in rows) r.Superseded = !keep.Contains(r.TradeId);

        return rows;
    }

    // ── 内部 ──

    /// <summary>口径全部在 PaperTradeMath（可被单测钉住），这里只做转发。</summary>
    private static PaperSlice Slice(string key, IReadOnlyList<PaperTradeRow> rows)
        => PaperTradeMath.Slice(key, rows);

    private static string? Trim(string? s, int max) =>
        s is null ? null : (s.Length <= max ? s : s[..max]);

    private static async Task<bool> TableExistsAsync(SqlConnection conn, CancellationToken ct) =>
        await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT CASE WHEN OBJECT_ID(N'dbo.FbPaperTrade', N'U') IS NULL THEN 0 ELSE 1 END",
            cancellationToken: ct)) == 1;

    private sealed class PaperPending
    {
        public long TradeId { get; set; }
        public string PlayType { get; set; } = string.Empty;
        public string OptionCode { get; set; } = string.Empty;
        public bool Voided { get; set; }
        public decimal Odds { get; set; }
        public int? HomeScore { get; set; }
        public int? AwayScore { get; set; }
        public int? HalfHomeScore { get; set; }
        public int? HalfAwayScore { get; set; }
        public int? Status { get; set; }
        public DateTime? MatchTime { get; set; }
    }
}
