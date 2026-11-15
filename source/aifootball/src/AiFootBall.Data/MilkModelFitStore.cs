using System.Data;
using System.Globalization;
using AiFootBall.Core.Models;
using Microsoft.Data.SqlClient;

namespace AiFootBall.Data;

/// <summary>
/// 拟合留痕的写入。不建表、不改已有概率列。表要先有待审脚本。
/// 只有事务提交后的 ready 实例编号会返回给调用方。
/// </summary>
public class MilkModelFitStore
{
    public const int CommandTimeoutSeconds = 60;

    /// <summary>回滚不沿用已经取消的请求令牌。</summary>
    public static readonly CancellationToken RollbackToken = CancellationToken.None;
    private readonly SqlConnectionFactory _factory;

    public MilkModelFitStore(SqlConnectionFactory factory) => _factory = factory;

    public async Task<long> SaveReadyInstanceAsync(
        FitModelTrace trace, DateTime fitCutoff, DateTime queryClock, string configVersion, CancellationToken ct)
    {
        var sha = FitContentCodec.Hash(trace);
        try
        {
            return await WriteAsync(trace, sha, fitCutoff, queryClock, configVersion, reuseOnly: false, ct);
        }
        catch (Exception ex) when (IsDuplicate(ex))
        {
            return await WriteAsync(trace, sha, fitCutoff, queryClock, configVersion, reuseOnly: true, ct);
        }
    }

    /// <summary>
    /// 同一事务里确认实例是 ready，再写轮次和当场 λ。
    /// 编号为空、轮次不存在或状态不是 ready，都不提交。
    /// </summary>
    public virtual async Task<MilkFitBindResult> BindRunAsync(
        string runId, long? instanceId, IReadOnlyList<MilkInputMatch> inputs, CancellationToken ct)
    {
        if (instanceId is not > 0)
            return MilkFitBindResult.Reject(
                MilkFitTraceDiagnostics.BindRejected(runId, instanceId, "没有本轮实例编号"));

        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var bound = await ExecAsync(conn, tx, ct,
                @"UPDATE dbo.FbMilkRun
                     SET ModelFitInstanceId = @id
                   WHERE RunId = @run
                     AND EXISTS (
                         SELECT 1 FROM dbo.FbMilkModelFitInstance
                          WHERE InstanceId = @id AND Status = 'ready')",
                new SqlParameter("@id", instanceId.Value),
                new SqlParameter("@run", runId));
            if (bound != 1)
            {
                var rollbackNote = await RollbackWithoutCancellingAsync(tx, original: null);
                var detail = "实例不是 ready 或轮次不存在";
                if (rollbackNote is not null)
                    detail += "；回滚失败：" + rollbackNote;
                return MilkFitBindResult.Reject(
                    MilkFitTraceDiagnostics.BindRejected(runId, instanceId, detail));
            }

            foreach (var row in inputs.Where(r => r.MatchId > 0).GroupBy(r => r.MatchId).Select(g => g.First()))
            {
                await ExecAsync(conn, tx, ct,
                    @"UPDATE dbo.FbMilkInput
                         SET ResolvedLeague = @lg, TotalLine = @tl, LambdaHome = @lh, LambdaAway = @la
                       WHERE RunId = @run AND MatchId = @mid",
                    new SqlParameter("@lg", (object?)row.ResolvedLeague ?? DBNull.Value),
                    new SqlParameter("@tl", (object?)row.TotalLine ?? DBNull.Value),
                    new SqlParameter("@lh", (object?)row.LambdaHome ?? DBNull.Value),
                    new SqlParameter("@la", (object?)row.LambdaAway ?? DBNull.Value),
                    new SqlParameter("@run", runId),
                    new SqlParameter("@mid", row.MatchId));
            }

            await tx.CommitAsync(ct);
            return MilkFitBindResult.Ok();
        }
        catch (Exception ex)
        {
            await RollbackWithoutCancellingAsync(tx, ex);
            throw;
        }
    }

    private async Task<long> WriteAsync(
        FitModelTrace trace, string sha, DateTime fitCutoff, DateTime queryClock,
        string configVersion, bool reuseOnly, CancellationToken ct)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var contentId = await ReadIdAsync(conn, tx, ct,
                @"SELECT ContentId
                    FROM dbo.FbMilkModelFitContent WITH (UPDLOCK, HOLDLOCK)
                   WHERE ContentSha256 = @sha AND Status = 'ready'",
                new SqlParameter("@sha", sha));

            if (contentId is null)
            {
                if (reuseOnly)
                    throw new InvalidOperationException("相同内容尚未形成可引用的 ready 记录");
                contentId = await InsertContentAsync(conn, tx, trace, sha, ct);
                await AfterContentInsertedAsync(ct);
                await BulkAsync(conn, tx, "dbo.FbMilkModelFitSample", SampleTable(contentId.Value, trace), ct);
                await BulkAsync(conn, tx, "dbo.FbMilkModelFitLeague", LeagueTable(contentId.Value, trace), ct);
                await BulkAsync(conn, tx, "dbo.FbMilkModelFitTeamLeague", TeamLeagueTable(contentId.Value, trace), ct);
                await BulkAsync(conn, tx, "dbo.FbMilkModelFitTeam", TeamTable(contentId.Value, trace), ct);
            }

            var instanceId = await ReadIdAsync(conn, tx, ct, InsertInstanceSql(),
                InstanceParameters(contentId.Value, fitCutoff, queryClock, configVersion));
            await tx.CommitAsync(ct);
            return instanceId ?? throw new InvalidOperationException("实例行没有返回编号");
        }
        catch (Exception ex)
        {
            await RollbackWithoutCancellingAsync(tx, ex);
            throw;
        }
    }

    /// <summary>
    /// 用与请求无关的令牌回滚。回滚再失败时把原因附在原始异常上，原始异常仍是被抛出的那个。
    /// 没有原始异常时，返回回滚失败的文本。
    /// </summary>
    private static async Task<string?> RollbackWithoutCancellingAsync(SqlTransaction tx, Exception? original)
    {
        try
        {
            await tx.RollbackAsync(RollbackToken);
            return null;
        }
        catch (Exception rollbackEx)
        {
            if (original is not null)
            {
                MilkFitTraceDiagnostics.AttachRollback(original, rollbackEx);
                return null;
            }
            return MilkFitTraceDiagnostics.Describe(rollbackEx);
        }
    }

    private static async Task<long> InsertContentAsync(
        SqlConnection conn, SqlTransaction tx, FitModelTrace trace, string sha, CancellationToken ct)
    {
        var times = trace.Samples.Where(s => s.MatchTime is not null).Select(s => s.MatchTime!.Value).ToList();
        DateTime? from = times.Count == 0 ? null : times.Min();
        DateTime? to = times.Count == 0 ? null : times.Max();
        var id = await ReadIdAsync(conn, tx, ct,
            @"INSERT INTO dbo.FbMilkModelFitContent
                (ContentSha256, Status, SampleCount, FitFrom, FitTo, GlobalHome, GlobalAway,
                 Rho, HalfLifeDays, CrossLeague, WindowMonths, DurationMinutes, ScoreMin, ScoreMax,
                 PriorGames, MinOwnGames, LeaguePrior, LambdaMin, LambdaMax, RateMin, RateMax,
                 ScaleMin, ScaleMax, HalfGoalShare, MaxHalfGoals)
              OUTPUT INSERTED.ContentId
              VALUES
                (@sha, 'ready', @n, @from, @to, @gh, @ga,
                 @rho, @half, @cross, @months, @dur, @smin, @smax,
                 @prior, @own, @lprior, @lmin, @lmax, @rmin, @rmax,
                 @scmin, @scmax, @halfGoal, @maxHalf)",
            new SqlParameter("@sha", SqlDbType.Char, 64) { Value = sha },
            IntParameter("@n", trace.Samples.Count),
            new SqlParameter("@from", SqlDbType.DateTime2) { Value = (object?)from ?? DBNull.Value },
            new SqlParameter("@to", SqlDbType.DateTime2) { Value = (object?)to ?? DBNull.Value },
            FloatParameter("@gh", trace.GlobalHome),
            FloatParameter("@ga", trace.GlobalAway),
            FloatParameter("@rho", trace.Rho),
            FloatParameter("@half", trace.HalfLifeDays),
            BitParameter("@cross", trace.CrossLeague),
            IntParameter("@months", FitContentCodec.WindowMonths),
            IntParameter("@dur", ScorelineModel.MatchDurationMinutes),
            IntParameter("@smin", FitContentCodec.ScoreMin),
            IntParameter("@smax", FitContentCodec.ScoreMax),
            IntParameter("@prior", FitContentCodec.PriorGames),
            IntParameter("@own", FitContentCodec.MinOwnGames),
            IntParameter("@lprior", FitContentCodec.LeaguePrior),
            FloatParameter("@lmin", FitContentCodec.LambdaMin),
            FloatParameter("@lmax", FitContentCodec.LambdaMax),
            FloatParameter("@rmin", FitContentCodec.RateMin),
            FloatParameter("@rmax", FitContentCodec.RateMax),
            FloatParameter("@scmin", FitContentCodec.ScaleMin),
            FloatParameter("@scmax", FitContentCodec.ScaleMax),
            FloatParameter("@halfGoal", FitContentCodec.HalfGoalShare),
            IntParameter("@maxHalf", FitContentCodec.MaxHalfGoals));
        return id ?? throw new InvalidOperationException("内容行没有返回编号");
    }

    /// <summary>
    /// 内容行已经写入当前事务、子表还没写。测试在这里取消，用来证明取消发生在插入之后。
    /// </summary>
    protected virtual Task AfterContentInsertedAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// 整数常量 0 会匹配 <see cref="SqlParameter(string, SqlDbType)"/>，Value 保持空，执行时报参数未提供。
    /// 数值一律先指定类型，再写入 Value。
    /// </summary>
    internal static SqlParameter IntParameter(string name, int value)
        => new SqlParameter(name, SqlDbType.Int) { Value = value };

    internal static SqlParameter FloatParameter(string name, double? value)
        => new SqlParameter(name, SqlDbType.Float) { Value = (object?)value ?? DBNull.Value };

    internal static SqlParameter BitParameter(string name, bool value)
        => new SqlParameter(name, SqlDbType.Bit) { Value = value };

    private static string InsertInstanceSql()
        => @"INSERT INTO dbo.FbMilkModelFitInstance
                (ContentId, Status, FitCutoff, QueryClock, UsableCut, CodeVersion, ConfigVersion,
                 AliasMapSha, LeagueMapSha)
             OUTPUT INSERTED.InstanceId
             VALUES
                (@content, 'ready', @cut, @clock, @usable, @code, @config, @alias, @league)";

    private static SqlParameter[] InstanceParameters(
        long contentId, DateTime fitCutoff, DateTime queryClock, string configVersion)
    {
        var (info, mvid) = FitContentCodec.CodeIdentity();
        var code = info + "|" + mvid;
        if (code.Length > 120)
            throw new InvalidOperationException("代码版本超过 CodeVersion 列宽");
        return
        [
            new SqlParameter("@content", contentId),
            new SqlParameter("@cut", fitCutoff),
            new SqlParameter("@clock", queryClock),
            new SqlParameter("@usable", fitCutoff.AddMinutes(-ScorelineModel.MatchDurationMinutes)),
            new SqlParameter("@code", code),
            new SqlParameter("@config", configVersion),
            new SqlParameter("@alias", FitContentCodec.MapSha(TeamAliases.FingerprintPairs())),
            new SqlParameter("@league", FitContentCodec.MapSha(LeagueNames.FingerprintPairs()))
        ];
    }

    private static DataTable SampleTable(long contentId, FitModelTrace trace)
    {
        var table = new DataTable();
        table.Columns.Add("ContentId", typeof(long));
        table.Columns.Add("Ordinal", typeof(int));
        table.Columns.Add("MatchId", typeof(long));
        table.Columns.Add("LeagueName", typeof(string));
        table.Columns.Add("HomeTeam", typeof(string));
        table.Columns.Add("AwayTeam", typeof(string));
        table.Columns.Add("CanonHome", typeof(string));
        table.Columns.Add("CanonAway", typeof(string));
        table.Columns.Add("HomeScore", typeof(short));
        table.Columns.Add("AwayScore", typeof(short));
        table.Columns.Add("MatchTime", typeof(DateTime));
        table.Columns.Add("TimeKind", typeof(byte));
        var ordinal = 0;
        foreach (var row in FitContentCodec.Ordered(trace.Samples))
        {
            table.Rows.Add(
                contentId,
                ordinal++,
                row.MatchId,
                row.LeagueName,
                row.HomeTeam,
                row.AwayTeam,
                row.CanonHome,
                row.CanonAway,
                (short)row.HomeScore,
                (short)row.AwayScore,
                (object?)row.MatchTime ?? DBNull.Value,
                row.MatchTime is DateTime t ? (byte)t.Kind : DBNull.Value);
        }
        return table;
    }

    private static DataTable LeagueTable(long contentId, FitModelTrace trace)
    {
        var table = new DataTable();
        table.Columns.Add("ContentId", typeof(long));
        table.Columns.Add("LeagueName", typeof(string));
        table.Columns.Add("HomeSum", typeof(double));
        table.Columns.Add("AwaySum", typeof(double));
        table.Columns.Add("Weight", typeof(double));
        table.Columns.Add("SampleCount", typeof(int));
        foreach (var row in trace.Leagues)
            table.Rows.Add(contentId, row.League, row.HomeSum, row.AwaySum, row.Weight, row.N);
        return table;
    }

    private static DataTable TeamLeagueTable(long contentId, FitModelTrace trace)
    {
        var table = new DataTable();
        table.Columns.Add("ContentId", typeof(long));
        table.Columns.Add("LeagueName", typeof(string));
        table.Columns.Add("TeamName", typeof(string));
        table.Columns.Add("GoalsFor", typeof(double));
        table.Columns.Add("GoalsAgainst", typeof(double));
        table.Columns.Add("Weight", typeof(double));
        table.Columns.Add("Games", typeof(int));
        foreach (var row in trace.TeamLeagues)
            table.Rows.Add(contentId, row.League, row.Team, row.GoalsFor, row.GoalsAgainst, row.Weight, row.Games);
        return table;
    }

    private static DataTable TeamTable(long contentId, FitModelTrace trace)
    {
        var table = new DataTable();
        table.Columns.Add("ContentId", typeof(long));
        table.Columns.Add("TeamName", typeof(string));
        table.Columns.Add("AttSum", typeof(double));
        table.Columns.Add("DefSum", typeof(double));
        table.Columns.Add("Weight", typeof(double));
        table.Columns.Add("Games", typeof(int));
        foreach (var row in trace.Teams)
            table.Rows.Add(contentId, row.Team, row.AttSum, row.DefSum, row.Weight, row.Games);
        return table;
    }

    private static async Task BulkAsync(
        SqlConnection conn, SqlTransaction tx, string table, DataTable data, CancellationToken ct)
    {
        if (data.Rows.Count == 0) return;
        using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.CheckConstraints, tx)
        {
            DestinationTableName = table,
            BulkCopyTimeout = CommandTimeoutSeconds
        };
        foreach (DataColumn column in data.Columns)
            bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        await bulk.WriteToServerAsync(data, ct);
    }

    private static async Task<int> ExecAsync(
        SqlConnection conn, SqlTransaction tx, CancellationToken ct, string sql, params SqlParameter[] parameters)
    {
        await using var cmd = new SqlCommand(sql, conn, tx) { CommandTimeout = CommandTimeoutSeconds };
        cmd.Parameters.AddRange(parameters);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long?> ReadIdAsync(
        SqlConnection conn, SqlTransaction tx, CancellationToken ct, string sql, params SqlParameter[] parameters)
    {
        await using var cmd = new SqlCommand(sql, conn, tx) { CommandTimeout = CommandTimeoutSeconds };
        cmd.Parameters.AddRange(parameters);
        var value = await cmd.ExecuteScalarAsync(ct);
        if (value is null or DBNull) return null;
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static bool IsDuplicate(Exception ex)
    {
        for (var cursor = ex; cursor is not null; cursor = cursor.InnerException)
        {
            if (cursor is SqlException sql && sql.Number is 2601 or 2627) return true;
        }
        return false;
    }
}
