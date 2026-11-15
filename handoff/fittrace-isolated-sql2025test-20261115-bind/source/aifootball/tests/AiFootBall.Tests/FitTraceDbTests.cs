using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using AiFootBall.Core.Models;
using AiFootBall.Data;
using AiFootBall.Sources;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace AiFootBall.Tests;

/// <summary>
/// 隔离库只接受显式指定的实例。未设置时拒绝连接。
/// 不读生产连接配置，也不把本机默认实例当作隔离库。
/// </summary>
public static class FitTraceDbGuard
{
    public const string DatabaseName = "AiFootBall_FitTrace_Test";
    public const string ConnectionVariable = "AIFOOTBALL_FITTRACE_CONN";
    public const string InstanceVariable = "AIFOOTBALL_FITTRACE_INSTANCE";
    public const string ProductionMachine = "WINSEV25DATA";

    public static FitTraceDecision Resolve(string? connection, string? expectedInstance)
    {
        if (string.IsNullOrWhiteSpace(connection) || string.IsNullOrWhiteSpace(expectedInstance))
            return FitTraceDecision.Refuse(
                "未显式指定隔离实例。未同时设置 " + ConnectionVariable + " 与 "
                + InstanceVariable + " 时拒绝连接。");

        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(connection);
        }
        catch (Exception ex)
        {
            return FitTraceDecision.Refuse("连接串无法解析：" + ex.GetType().Name);
        }

        if (!string.Equals(builder.InitialCatalog, DatabaseName, StringComparison.Ordinal))
            return FitTraceDecision.Refuse(
                "测试库名必须精确等于 " + DatabaseName + "。当前是「" + builder.InitialCatalog + "」。");
        if (string.IsNullOrWhiteSpace(builder.DataSource))
            return FitTraceDecision.Refuse("连接串没有目标实例。");
        if (IsDocumentedProductionTarget(builder.DataSource))
            return FitTraceDecision.Refuse("目标指向已记录的生产地址，拒绝连接。");

        builder.TrustServerCertificate = true;
        if (builder.ConnectTimeout <= 0 || builder.ConnectTimeout > 15)
            builder.ConnectTimeout = 15;
        return FitTraceDecision.Accept(builder.ConnectionString, expectedInstance.Trim());
    }

    public static void AssertInstance(string? serverName, string expectedInstance)
    {
        if (!string.Equals((serverName ?? "").Trim(), expectedInstance.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "实例身份不符：连接到「" + serverName + "」，声明的是「" + expectedInstance
                + "」。仅库名相同不能证明隔离。停止。");
    }

    public static void AssertDatabase(string? databaseName, string expectedDatabase)
    {
        if (!string.Equals(databaseName, expectedDatabase, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "库名不符：连接到「" + databaseName + "」，要求「" + expectedDatabase + "」。停止。");
    }

    public static bool IsDocumentedProductionTarget(string dataSource)
    {
        var text = dataSource.Trim();
        return text.Contains("192.168.117.128", StringComparison.OrdinalIgnoreCase)
               || text.Contains("192.168.41.128", StringComparison.OrdinalIgnoreCase);
    }
}

public readonly record struct FitTraceDecision(bool Accepted, string Reason, string ConnectionString, string ExpectedInstance)
{
    public static FitTraceDecision Refuse(string reason) => new(false, reason, "", "");

    public static FitTraceDecision Accept(string connectionString, string expectedInstance)
        => new(true, "", connectionString, expectedInstance);
}

public sealed class FitTraceDbProbe
{
    public const string DatabaseName = FitTraceDbGuard.DatabaseName;
    public static bool Available { get; }
    public static string Reason { get; }
    public static string ExpectedInstance { get; }
    public static string ConnectionString { get; }

    static FitTraceDbProbe()
    {
        ExpectedInstance = "";
        ConnectionString = "";
        if (string.Equals(Environment.MachineName, FitTraceDbGuard.ProductionMachine, StringComparison.OrdinalIgnoreCase))
        {
            Available = false;
            Reason = "阻塞：当前进程在生产机 " + FitTraceDbGuard.ProductionMachine
                     + " 上。不连接任何实例，也不读取生产连接配置。";
            return;
        }

        var decision = FitTraceDbGuard.Resolve(
            Environment.GetEnvironmentVariable(FitTraceDbGuard.ConnectionVariable),
            Environment.GetEnvironmentVariable(FitTraceDbGuard.InstanceVariable));
        Available = decision.Accepted;
        Reason = decision.Reason;
        ExpectedInstance = decision.ExpectedInstance;
        ConnectionString = decision.ConnectionString;
    }
}

public sealed class FitTraceDbFactAttribute : FactAttribute
{
}

public sealed class FitTraceDbFixture : IAsyncLifetime
{
    public SqlConnectionFactory Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (!FitTraceDbProbe.Available)
            throw new InvalidOperationException(FitTraceDbProbe.Reason
                + " 阻塞：DDL、批量写入、回滚、并发、内容复用、内容变化、SaveMilkAsync 真入口、RefreshAsync 留痕失败、旧版兼容都没有在隔离库执行。未连接生产机默认实例。");

        try
        {
            var masterBuilder = new SqlConnectionStringBuilder(FitTraceDbProbe.ConnectionString)
            {
                InitialCatalog = "master"
            };
            await using (var master = new SqlConnection(masterBuilder.ConnectionString))
            {
                await master.OpenAsync();
                var serverName = await ScalarText(master, "SELECT CONVERT(nvarchar(256), SERVERPROPERTY('ServerName'))");
                FitTraceDbGuard.AssertInstance(serverName, FitTraceDbProbe.ExpectedInstance);
                await using var create = master.CreateCommand();
                create.CommandTimeout = 60;
                create.CommandText =
                    "IF DB_ID(N'" + FitTraceDbProbe.DatabaseName + "') IS NULL CREATE DATABASE ["
                    + FitTraceDbProbe.DatabaseName + "];";
                await create.ExecuteNonQueryAsync();
            }

            Factory = new SqlConnectionFactory(FitTraceDbProbe.ConnectionString);
            await using var conn = await Factory.OpenAsync();
            var landedServer = await ScalarText(conn, "SELECT CONVERT(nvarchar(256), SERVERPROPERTY('ServerName'))");
            var landedDb = await ScalarText(conn, "SELECT DB_NAME()");
            FitTraceDbGuard.AssertInstance(landedServer, FitTraceDbProbe.ExpectedInstance);
            FitTraceDbGuard.AssertDatabase(landedDb, FitTraceDbProbe.DatabaseName);
            await EnsureStubMilkTablesAsync(conn);
            await ApplyPendingScriptAsync(conn);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "隔离库连接或身份校验失败，停止。未改用其它实例。" + ex.GetType().Name, ex);
        }
    }

    internal static async Task EnsureStubMilkTablesAsync(SqlConnection conn)
    {
        await Exec(conn, """
            IF OBJECT_ID(N'dbo.FbMilkRun', N'U') IS NULL
            CREATE TABLE dbo.FbMilkRun (
                RunId nvarchar(64) NOT NULL CONSTRAINT PK_FbMilkRun PRIMARY KEY,
                Status varchar(16) NULL
            );
            IF OBJECT_ID(N'dbo.FbMilkInput', N'U') IS NULL
            CREATE TABLE dbo.FbMilkInput (
                RunId nvarchar(64) NOT NULL,
                MatchId bigint NOT NULL,
                CONSTRAINT PK_FbMilkInput PRIMARY KEY (RunId, MatchId)
            );
            """);
    }

    internal static async Task ApplyPendingScriptAsync(SqlConnection conn)
    {
        var script = await File.ReadAllTextAsync(Path.Combine(
            FitTraceTests.RepoRoot(), "source", "aifootball", "sql", "pending", "2026-11-15_milk_model_fit.sql"));
        foreach (var batch in Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            await Exec(conn, batch);
        }
    }

    internal static async Task<string> ScalarText(SqlConnection conn, string sql)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? "" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }

    public Task DisposeAsync() => Task.CompletedTask;

    internal static async Task Exec(SqlConnection conn, string sql, params SqlParameter[] parameters)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        cmd.Parameters.AddRange(parameters);
        await cmd.ExecuteNonQueryAsync();
    }

    internal static async Task<long> Scalar(SqlConnection conn, string sql, params SqlParameter[] parameters)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        cmd.Parameters.AddRange(parameters);
        var value = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }
}

public class FitTraceDbTests : IClassFixture<FitTraceDbFixture>
{
    private readonly FitTraceDbFixture _db;
    private readonly ITestOutputHelper _output;

    public FitTraceDbTests(FitTraceDbFixture db, ITestOutputHelper output)
    {
        _db = db;
        _output = output;
    }

    [FitTraceDbFact]
    public async Task SameContent_IsReused_AndClocksStayOnEachInstance()
    {
        await ResetAsync();
        var trace = Trace(away: 0);
        var store = new MilkModelFitStore(_db.Factory);
        var first = await store.SaveReadyInstanceAsync(trace, new DateTime(2026, 11, 15, 8, 0, 0), new DateTime(2026, 11, 15, 8, 0, 1), "w=70/70/50/80/30|bf=off", default);
        var second = await store.SaveReadyInstanceAsync(trace, new DateTime(2026, 11, 15, 9, 0, 0), new DateTime(2026, 11, 15, 9, 0, 1), "w=70/70/50/80/30|bf=off", default);

        await using var conn = await _db.Factory.OpenAsync();
        Assert.Equal(1, await FitTraceDbFixture.Scalar(conn, "SELECT COUNT(*) FROM dbo.FbMilkModelFitContent WHERE Status='ready'"));
        Assert.Equal(2, await FitTraceDbFixture.Scalar(conn, "SELECT COUNT(*) FROM dbo.FbMilkModelFitInstance WHERE Status='ready'"));
        Assert.NotEqual(first, second);
        var cuts = await FitTraceDbFixture.Scalar(conn,
            "SELECT COUNT(DISTINCT FitCutoff) FROM dbo.FbMilkModelFitInstance");
        Assert.Equal(2, cuts);
    }

    [FitTraceDbFact]
    public async Task ChangedScore_InsertsASecondReadyContent()
    {
        await ResetAsync();
        var store = new MilkModelFitStore(_db.Factory);
        await store.SaveReadyInstanceAsync(Trace(0), new DateTime(2026, 11, 15, 8, 0, 0), new DateTime(2026, 11, 15, 8, 0, 1), "w=70/70/50/80/30|bf=off", default);
        await store.SaveReadyInstanceAsync(Trace(1), new DateTime(2026, 11, 15, 8, 0, 0), new DateTime(2026, 11, 15, 8, 0, 1), "w=70/70/50/80/30|bf=off", default);
        await using var conn = await _db.Factory.OpenAsync();
        Assert.Equal(2, await FitTraceDbFixture.Scalar(conn, "SELECT COUNT(*) FROM dbo.FbMilkModelFitContent WHERE Status='ready'"));
    }

    [FitTraceDbFact]
    public async Task ConcurrentSameContent_Dedupes_AndFailedCallerHasNoPartial()
    {
        await ResetAsync();
        var trace = Trace(0);
        var store = new MilkModelFitStore(_db.Factory);
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var ids = new System.Collections.Concurrent.ConcurrentBag<long>();
        var cut = new DateTime(2026, 11, 15, 10, 0, 0);
        var clock = new DateTime(2026, 11, 15, 10, 0, 2);

        async Task Once(DateTime ownClock)
        {
            try
            {
                ids.Add(await store.SaveReadyInstanceAsync(trace, cut, ownClock, "w=70/70/50/80/30|bf=off", default));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add(ex);
            }
        }

        await Task.WhenAll(Once(clock), Once(clock.AddSeconds(1)));

        await using var conn = await _db.Factory.OpenAsync();
        Assert.Equal(1, await FitTraceDbFixture.Scalar(conn,
            "SELECT COUNT(*) FROM dbo.FbMilkModelFitContent WHERE Status='ready'"));
        Assert.Equal(0, await FitTraceDbFixture.Scalar(conn,
            "SELECT COUNT(*) FROM dbo.FbMilkModelFitContent WHERE Status<>'ready'"));
        Assert.Equal(ids.Count, await FitTraceDbFixture.Scalar(conn,
            "SELECT COUNT(*) FROM dbo.FbMilkModelFitInstance WHERE Status='ready'"));
        Assert.NotEmpty(ids);
        foreach (var id in ids)
        {
            var ready = await FitTraceDbFixture.Scalar(conn,
                "SELECT COUNT(*) FROM dbo.FbMilkModelFitInstance WHERE InstanceId=@id AND Status='ready'",
                new SqlParameter("@id", id));
            Assert.Equal(1, ready);
        }
        _output.WriteLine("concurrent ids={0} errors={1}", ids.Count, errors.Count);
    }

    [FitTraceDbFact]
    public async Task MidWriteFailure_RollsBack_AndWritingRowIsNotReferenced()
    {
        await ResetAsync();
        var trace = Trace(0);
        var sha = FitContentCodec.Hash(trace);
        await using (var seed = await _db.Factory.OpenAsync())
        {
            await FitTraceDbFixture.Exec(seed, """
                INSERT INTO dbo.FbMilkModelFitContent
                    (ContentSha256, Status, SampleCount, GlobalHome, GlobalAway, Rho, CrossLeague,
                     WindowMonths, DurationMinutes, ScoreMin, ScoreMax, PriorGames, MinOwnGames, LeaguePrior,
                     LambdaMin, LambdaMax, RateMin, RateMax, ScaleMin, ScaleMax, HalfGoalShare, MaxHalfGoals)
                VALUES
                    (@sha, 'writing', 1, 1, 1, 0.010, 0,
                     18, 150, 0, 12, 8, 5, 30,
                     0.15, 4.5, 0.2, 4.5, 0.65, 1.55, 0.45, 6);
                INSERT INTO dbo.FbMilkModelFitSample
                    (ContentId, Ordinal, MatchId, LeagueName, HomeTeam, AwayTeam, CanonHome, CanonAway, HomeScore, AwayScore)
                SELECT ContentId, 0, 1, N'半份', N'主', N'客', N'主', N'客', 1, 0
                  FROM dbo.FbMilkModelFitContent WHERE Status='writing';
                """, new SqlParameter("@sha", sha));
        }

        var broken = new FitModelTrace
        {
            Samples = trace.Samples,
            Leagues = new[] { new FitLeagueTotal(new string('联', 200), 1, 0, 1, 1) },
            TeamLeagues = trace.TeamLeagues,
            Teams = trace.Teams,
            GlobalHome = trace.GlobalHome,
            GlobalAway = trace.GlobalAway,
            Rho = trace.Rho,
            HalfLifeDays = trace.HalfLifeDays,
            CrossLeague = trace.CrossLeague
        };
        var store = new MilkModelFitStore(_db.Factory);
        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveReadyInstanceAsync(
            broken, new DateTime(2026, 11, 15, 11, 0, 0), new DateTime(2026, 11, 15, 11, 0, 1),
            "w=70/70/50/80/30|bf=off", default));

        await using var conn = await _db.Factory.OpenAsync();
        Assert.Equal(0, await FitTraceDbFixture.Scalar(conn, "SELECT COUNT(*) FROM dbo.FbMilkModelFitContent WHERE Status='ready'"));
        Assert.Equal(1, await FitTraceDbFixture.Scalar(conn, "SELECT COUNT(*) FROM dbo.FbMilkModelFitContent WHERE Status='writing'"));
        Assert.Equal(1, await FitTraceDbFixture.Scalar(conn, "SELECT COUNT(*) FROM dbo.FbMilkModelFitSample"));
        Assert.Equal(0, await FitTraceDbFixture.Scalar(conn, "SELECT COUNT(*) FROM dbo.FbMilkModelFitInstance"));

        var instanceId = await store.SaveReadyInstanceAsync(
            trace, new DateTime(2026, 11, 15, 11, 5, 0), new DateTime(2026, 11, 15, 11, 5, 1),
            "w=70/70/50/80/30|bf=off", default);
        var pointsAtWriting = await FitTraceDbFixture.Scalar(conn, """
            SELECT COUNT(*)
              FROM dbo.FbMilkModelFitInstance i
              JOIN dbo.FbMilkModelFitContent c ON c.ContentId = i.ContentId
             WHERE i.InstanceId = @id AND c.Status = 'writing'
            """, new SqlParameter("@id", instanceId));
        Assert.Equal(0, pointsAtWriting);
        Assert.Equal(1, await FitTraceDbFixture.Scalar(conn, "SELECT COUNT(*) FROM dbo.FbMilkModelFitContent WHERE Status='writing'"));

        await FitTraceDbFixture.Exec(conn,
            "INSERT INTO dbo.FbMilkRun (RunId, Status) VALUES (N'run-1', N'open')");
        await FitTraceDbFixture.Exec(conn,
            "INSERT INTO dbo.FbMilkInput (RunId, MatchId) VALUES (N'run-1', 1)");
        var inputs = new[]
        {
            new MilkInputMatch { MatchId = 1, ResolvedLeague = "英超", TotalLine = 2.5m, LambdaHome = 1.2, LambdaAway = 0.8 }
        };
        Assert.False((await store.BindRunAsync("run-1", instanceId + 1000, inputs, default)).Committed);
        Assert.True((await store.BindRunAsync("run-1", instanceId, inputs, default)).Committed);
        Assert.Equal(instanceId, await FitTraceDbFixture.Scalar(conn,
            "SELECT ModelFitInstanceId FROM dbo.FbMilkRun WHERE RunId=N'run-1'"));
        Assert.Equal(1, await FitTraceDbFixture.Scalar(conn,
            "SELECT COUNT(*) FROM dbo.FbMilkInput WHERE RunId=N'run-1' AND ResolvedLeague=N'英超' AND TotalLine=2.5"));
    }

    [FitTraceDbFact]
    public async Task CancelledWrite_RollsBackWithAnIndependentToken()
    {
        await ResetAsync();
        using var cts = new CancellationTokenSource();
        var store = new CancelAfterContentInsert(_db.Factory, cts);
        var started = store.SaveReadyInstanceAsync(
            Trace(0), new DateTime(2026, 11, 15, 13, 0, 0), new DateTime(2026, 11, 15, 13, 0, 1),
            "w=70/70/50/80/30|bf=off", cts.Token);
        var thrown = await Record.ExceptionAsync(() => started);
        Assert.True(store.ContentInserted);
        Assert.NotNull(thrown);
        Assert.IsAssignableFrom<OperationCanceledException>(thrown);
        Assert.IsNotType<AggregateException>(thrown);
        await using var conn = await _db.Factory.OpenAsync();
        Assert.Equal(0, await FitTraceDbFixture.Scalar(conn,
            "SELECT COUNT(*) FROM dbo.FbMilkModelFitContent WHERE Status='ready'"));
        _output.WriteLine("cancel exception={0}", thrown.GetType().Name);
    }

    /// <summary>
    /// 内容插入成功后才取消。参数没绑上时到不了这里，SqlException 不能算作中途取消。
    /// </summary>
    private sealed class CancelAfterContentInsert : MilkModelFitStore
    {
        private readonly CancellationTokenSource _cts;

        public CancelAfterContentInsert(SqlConnectionFactory factory, CancellationTokenSource cts)
            : base(factory) => _cts = cts;

        public bool ContentInserted { get; private set; }

        protected override Task AfterContentInsertedAsync(CancellationToken ct)
        {
            ContentInserted = true;
            _cts.Cancel();
            return Task.CompletedTask;
        }
    }

    [FitTraceDbFact]
    public async Task ZeroScoreMin_AndZeroGlobalAway_RoundTripThroughSql()
    {
        await ResetAsync();
        var trace = BulkTrace(2);
        var store = new MilkModelFitStore(_db.Factory);
        var instanceId = await store.SaveReadyInstanceAsync(
            trace, new DateTime(2026, 11, 15, 14, 0, 0), new DateTime(2026, 11, 15, 14, 0, 1),
            "w=70/70/50/80/30|bf=off", default);
        Assert.True(instanceId > 0);

        await using var conn = await _db.Factory.OpenAsync();
        Assert.Equal(0L, await FitTraceDbFixture.Scalar(conn,
            "SELECT ScoreMin FROM dbo.FbMilkModelFitContent WHERE Status='ready'"));
        Assert.Equal((long)FitContentCodec.ScoreMax, await FitTraceDbFixture.Scalar(conn,
            "SELECT ScoreMax FROM dbo.FbMilkModelFitContent WHERE Status='ready'"));
        Assert.Equal((long)FitContentCodec.WindowMonths, await FitTraceDbFixture.Scalar(conn,
            "SELECT WindowMonths FROM dbo.FbMilkModelFitContent WHERE Status='ready'"));
        Assert.Equal((long)ScorelineModel.MatchDurationMinutes, await FitTraceDbFixture.Scalar(conn,
            "SELECT DurationMinutes FROM dbo.FbMilkModelFitContent WHERE Status='ready'"));
        Assert.Equal((long)FitContentCodec.PriorGames, await FitTraceDbFixture.Scalar(conn,
            "SELECT PriorGames FROM dbo.FbMilkModelFitContent WHERE Status='ready'"));
        Assert.Equal((long)FitContentCodec.MinOwnGames, await FitTraceDbFixture.Scalar(conn,
            "SELECT MinOwnGames FROM dbo.FbMilkModelFitContent WHERE Status='ready'"));
        Assert.Equal((long)FitContentCodec.LeaguePrior, await FitTraceDbFixture.Scalar(conn,
            "SELECT LeaguePrior FROM dbo.FbMilkModelFitContent WHERE Status='ready'"));
        Assert.Equal((long)FitContentCodec.MaxHalfGoals, await FitTraceDbFixture.Scalar(conn,
            "SELECT MaxHalfGoals FROM dbo.FbMilkModelFitContent WHERE Status='ready'"));
        Assert.Equal(2L, await FitTraceDbFixture.Scalar(conn,
            "SELECT SampleCount FROM dbo.FbMilkModelFitContent WHERE Status='ready'"));
        Assert.Equal(1L, await FitTraceDbFixture.Scalar(conn,
            "SELECT CASE WHEN CrossLeague = 0 AND GlobalAway = 0 AND HalfLifeDays IS NULL THEN 1 ELSE 0 END FROM dbo.FbMilkModelFitContent WHERE Status='ready'"));
        Assert.Equal(1L, await FitTraceDbFixture.Scalar(conn,
            "SELECT CASE WHEN ABS(LambdaMin - 0.15) < 0.0000001 AND ABS(HalfGoalShare - 0.45) < 0.0000001 THEN 1 ELSE 0 END FROM dbo.FbMilkModelFitContent WHERE Status='ready'"));
    }

    [FitTraceDbFact]
    public async Task Refresh_PersistFailure_StillUpdatesCache()
    {
        await ResetAsync();
        await using (var conn = await _db.Factory.OpenAsync())
        {
            await FitTraceDbFixture.Exec(conn, """
                IF OBJECT_ID(N'dbo.FbExchange', N'U') IS NOT NULL DROP TABLE dbo.FbExchange;
                IF OBJECT_ID(N'dbo.FbOdds', N'U') IS NOT NULL DROP TABLE dbo.FbOdds;
                IF OBJECT_ID(N'dbo.Recommendation', N'U') IS NOT NULL DROP TABLE dbo.Recommendation;
                IF OBJECT_ID(N'dbo.FbMatch', N'U') IS NOT NULL DROP TABLE dbo.FbMatch;
                CREATE TABLE dbo.FbMatch (
                    MatchId bigint NOT NULL PRIMARY KEY,
                    LeagueName nvarchar(128) NULL,
                    HomeTeamName nvarchar(128) NULL,
                    AwayTeamName nvarchar(128) NULL,
                    MatchTime datetime2 NULL,
                    HomeScore int NULL,
                    AwayScore int NULL,
                    Status int NULL,
                    ResultSpf char(1) NULL
                );
                CREATE TABLE dbo.FbOdds (
                    MatchId bigint NOT NULL,
                    OddsType varchar(8) NOT NULL,
                    CompanyId int NOT NULL,
                    IsOpening bit NOT NULL,
                    HomeWin decimal(8,2) NULL,
                    Draw decimal(8,2) NULL,
                    AwayWin decimal(8,2) NULL,
                    Handicap decimal(8,2) NULL
                );
                CREATE TABLE dbo.FbExchange (
                    MatchId bigint NOT NULL,
                    RatioHome decimal(8,2) NULL,
                    RatioDraw decimal(8,2) NULL,
                    RatioAway decimal(8,2) NULL,
                    CapturedAt datetime2 NOT NULL
                );
                CREATE TABLE dbo.Recommendation (
                    RecId bigint NOT NULL PRIMARY KEY,
                    Board varchar(8) NULL,
                    MatchId bigint NULL,
                    RecResult char(1) NULL,
                    Hit bit NULL,
                    Confidence int NULL,
                    IsCoverAll bit NULL,
                    CreatedAt datetime2 NOT NULL
                );
                """);
            var start = DateTime.Now.AddDays(-40);
            for (var i = 0; i < 30; i++)
            {
                await FitTraceDbFixture.Exec(conn,
                    @"INSERT INTO dbo.FbMatch
                        (MatchId, LeagueName, HomeTeamName, AwayTeamName, MatchTime, HomeScore, AwayScore, Status, ResultSpf)
                      VALUES (@id, N'英超', @home, @away, @at, 1, 0, 3, '3')",
                    new SqlParameter("@id", 5000 + i),
                    new SqlParameter("@home", "主" + i),
                    new SqlParameter("@away", "客" + i),
                    new SqlParameter("@at", start.AddDays(i)));
            }
            await FitTraceDbFixture.Exec(conn, """
                IF OBJECT_ID(N'CK_FitTrace_TestFail', N'C') IS NULL
                    ALTER TABLE dbo.FbMilkModelFitContent
                        ADD CONSTRAINT CK_FitTrace_TestFail CHECK (1 = 0);
                """);
        }

        try
        {

        var repo = new BettingRepository(_db.Factory);
        var hit = new HitProbabilityService(repo, new MilkModelFitStore(_db.Factory))
        {
            TraceDiagnostic = _ => throw new InvalidOperationException("Password=SuperSecret;Server=10.9.9.9")
        };
        var snap = await hit.GetSnapshotAsync();
        Assert.True(snap.Scores.Ready);
        Assert.Null(snap.FitInstanceId);
        Assert.False(string.IsNullOrEmpty(snap.TraceError));
        Assert.Contains("实例未提交", snap.TraceError, StringComparison.Ordinal);
        Assert.DoesNotContain("SuperSecret", snap.TraceError, StringComparison.Ordinal);
        Assert.Same(snap, hit.Cached);
        Assert.DoesNotContain("SuperSecret", hit.LastTraceError ?? "", StringComparison.Ordinal);
        }
        finally
        {
            await using var conn = await _db.Factory.OpenAsync();
            await FitTraceDbFixture.Exec(conn,
                "IF OBJECT_ID(N'CK_FitTrace_TestFail', N'C') IS NOT NULL ALTER TABLE dbo.FbMilkModelFitContent DROP CONSTRAINT CK_FitTrace_TestFail;");
        }
    }

    [FitTraceDbFact]
    public async Task BulkWrite_AndRollback_AreMeasuredOnTheIsolatedDatabase()
    {
        await ResetAsync();
        var trace = BulkTrace(180_000);
        var store = new MilkModelFitStore(_db.Factory);
        var watch = Stopwatch.StartNew();
        long instanceId;
        try
        {
            instanceId = await store.SaveReadyInstanceAsync(
                trace, new DateTime(2026, 11, 15, 12, 0, 0), new DateTime(2026, 11, 15, 12, 0, 1),
                "w=70/70/50/80/30|bf=off", default);
        }
        catch (Exception ex)
        {
            watch.Stop();
            await WriteMeasureAsync("commit-failed", watch.Elapsed.TotalSeconds, ex.GetType().Name + ": " + ex.Message);
            throw;
        }
        watch.Stop();
        var commitSeconds = watch.Elapsed.TotalSeconds;

        await using var conn = await _db.Factory.OpenAsync();
        var space = await ReadSpaceAsync(conn, "dbo.FbMilkModelFitSample");
        var dbSpace = await ReadSpaceAsync(conn, null);
        Assert.Equal(180_000, await FitTraceDbFixture.Scalar(conn, "SELECT COUNT(*) FROM dbo.FbMilkModelFitSample"));
        Assert.True(instanceId > 0);

        watch.Restart();
        await using (var tx = (SqlTransaction)await conn.BeginTransactionAsync())
        {
            await using var cmd = new SqlCommand("""
                INSERT INTO dbo.FbMilkModelFitContent
                    (ContentSha256, Status, SampleCount, GlobalHome, GlobalAway, Rho, CrossLeague,
                     WindowMonths, DurationMinutes, ScoreMin, ScoreMax, PriorGames, MinOwnGames, LeaguePrior,
                     LambdaMin, LambdaMax, RateMin, RateMax, ScaleMin, ScaleMax, HalfGoalShare, MaxHalfGoals)
                OUTPUT INSERTED.ContentId
                VALUES
                    (@sha, 'ready', 180000, 1, 1, 0.010, 0,
                     18, 150, 0, 12, 8, 5, 30,
                     0.15, 4.5, 0.2, 4.5, 0.65, 1.55, 0.45, 6);
                """, conn, tx) { CommandTimeout = 60 };
            cmd.Parameters.Add(new SqlParameter("@sha", new string('B', 64)));
            var contentId = Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            using (var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.CheckConstraints, tx)
            {
                DestinationTableName = "dbo.FbMilkModelFitSample",
                BulkCopyTimeout = 60
            })
            {
                var table = SampleTable(contentId, trace.Samples);
                foreach (System.Data.DataColumn column in table.Columns)
                    bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                await bulk.WriteToServerAsync(table);
            }
            await tx.RollbackAsync();
        }
        watch.Stop();
        var rowsAfter = await FitTraceDbFixture.Scalar(conn, "SELECT COUNT(*) FROM dbo.FbMilkModelFitSample");
        Assert.Equal(180_000, rowsAfter);
        Assert.Equal(1, await FitTraceDbFixture.Scalar(conn,
            "SELECT COUNT(*) FROM dbo.FbMilkModelFitContent WHERE Status='ready'"));

        var note = FormattableString.Invariant(
            $"commitSeconds={commitSeconds:0.###}; rollbackSeconds={watch.Elapsed.TotalSeconds:0.###}; sampleSpace={space}; databaseSpace={dbSpace}");
        _output.WriteLine(note);
        await WriteMeasureAsync("measured", commitSeconds, note);
    }

    [FitTraceDbFact]
    public async Task PendingDdl_HasTheReviewedTablesColumnsAndReadyIndex()
    {
        await using var conn = await _db.Factory.OpenAsync();
        await AssertColumn(conn, "FbMilkModelFitContent", "ContentId", "bigint", false);
        await AssertColumn(conn, "FbMilkModelFitContent", "ContentSha256", "char", false, 64);
        await AssertColumn(conn, "FbMilkModelFitContent", "Status", "varchar", false, 16);
        await AssertColumn(conn, "FbMilkModelFitContent", "SampleCount", "int", false);
        await AssertColumn(conn, "FbMilkModelFitContent", "FitFrom", "datetime2", true);
        await AssertColumn(conn, "FbMilkModelFitContent", "GlobalHome", "float", false);
        await AssertColumn(conn, "FbMilkModelFitContent", "Rho", "decimal", false);
        await AssertColumn(conn, "FbMilkModelFitContent", "HalfLifeDays", "float", true);
        await AssertColumn(conn, "FbMilkModelFitContent", "CrossLeague", "bit", false);
        await AssertColumn(conn, "FbMilkModelFitContent", "HalfGoalShare", "float", false);
        await AssertColumn(conn, "FbMilkModelFitContent", "MaxHalfGoals", "int", false);
        await AssertColumn(conn, "FbMilkModelFitInstance", "InstanceId", "bigint", false);
        await AssertColumn(conn, "FbMilkModelFitInstance", "ContentId", "bigint", false);
        await AssertColumn(conn, "FbMilkModelFitInstance", "CodeVersion", "nvarchar", false, 240);
        await AssertColumn(conn, "FbMilkModelFitInstance", "ConfigVersion", "nvarchar", false, 160);
        await AssertColumn(conn, "FbMilkModelFitInstance", "AliasMapSha", "char", false, 64);
        await AssertColumn(conn, "FbMilkModelFitInstance", "FitCutoff", "datetime2", false);
        await AssertColumn(conn, "FbMilkModelFitSample", "Ordinal", "int", false);
        await AssertColumn(conn, "FbMilkModelFitSample", "MatchTime", "datetime2", true);
        await AssertColumn(conn, "FbMilkModelFitLeague", "LeagueName", "nvarchar", false, 256);
        await AssertColumn(conn, "FbMilkModelFitTeamLeague", "Games", "int", false);
        await AssertColumn(conn, "FbMilkModelFitTeam", "TeamName", "nvarchar", false, 256);
        await AssertColumn(conn, "FbMilkRun", "ModelFitInstanceId", "bigint", true);
        await AssertColumn(conn, "FbMilkInput", "ResolvedLeague", "nvarchar", true, 256);
        await AssertColumn(conn, "FbMilkInput", "TotalLine", "decimal", true);
        await AssertColumn(conn, "FbMilkInput", "LambdaHome", "float", true);
        await AssertColumn(conn, "FbMilkInput", "LambdaAway", "float", true);

        Assert.Equal(1, await FitTraceDbFixture.Scalar(conn, """
            SELECT COUNT(*)
              FROM sys.indexes
             WHERE name = N'UX_FbMilkModelFitContent_ReadySha'
               AND is_unique = 1
               AND has_filter = 1
               AND filter_definition LIKE N'%ready%'
            """));
        Assert.Equal(1, await FitTraceDbFixture.Scalar(conn,
            "SELECT COUNT(*) FROM sys.foreign_keys WHERE name = N'FK_FbMilkRun_ModelFitInstance'"));
        Assert.Equal(1, await FitTraceDbFixture.Scalar(conn,
            "SELECT COUNT(*) FROM sys.foreign_keys WHERE name = N'FK_FbMilkModelFitInstance_Content'"));
    }

    /// <summary>
    /// 新增结构留在库里。读写必须走冻结的 82c93bce 二进制，不能用当前工程里的仓库类代替。
    /// 删掉新表、新列不是生产回滚，这里不测。
    /// </summary>
    [FitTraceDbFact]
    public async Task LegacyBinary_ReadsAndWritesWhileNewShapeRemains()
    {
        await using (var conn = await _db.Factory.OpenAsync())
        {
            await AssertNewShapeRemainsAsync(conn);
            await EnsureColumnsTheFrozenProgramWritesAsync(conn);
        }

        try
        {
            var read = await LegacyBinaryProbe.ExerciseAsync(
                _db.Factory.ConnectionString, FitTraceTests.RepoRoot());
            Assert.Equal("legacy-82c93bce", read.RunId);
            Assert.Equal("running", read.Status);
            Assert.Equal(1, read.InputRows);

            await using var conn = await _db.Factory.OpenAsync();
            await AssertNewShapeRemainsAsync(conn);
            Assert.Equal(1, await FitTraceDbFixture.Scalar(conn,
                "SELECT COUNT(*) FROM dbo.FbMilkRun WHERE RunId=N'legacy-82c93bce' AND Status=N'running' AND ModelFitInstanceId IS NULL"));
            Assert.Equal(1, await FitTraceDbFixture.Scalar(conn,
                "SELECT COUNT(*) FROM dbo.FbMilkInput WHERE RunId=N'legacy-82c93bce' AND MatchId=88011 AND LeagueName=N'英超' AND ResolvedLeague IS NULL AND TotalLine IS NULL AND LambdaHome IS NULL AND LambdaAway IS NULL"));
        }
        finally
        {
            await RestoreStubMilkTablesAsync();
        }
    }

    [FitTraceDbFact]
    public async Task SaveMilk_PublishesWhenBindCommits_AndBlocksWhenInstanceIsNotReady()
    {
        await PreparePublishTablesAsync();
        try
        {
            var (matchNo, kick) = SellableMatch();
            await SeedCaptureAsync(matchNo, kick);
            var store = new MilkModelFitStore(_db.Factory);
            var ready = await store.SaveReadyInstanceAsync(
                Trace(0), new DateTime(2026, 11, 15, 8, 0, 0), new DateTime(2026, 11, 15, 8, 0, 1),
                "w=70/70/50/80/30|bf=off", default);

            var blocked = await SaveMilkOnceAsync(ready + 1000, matchNo, kick);
            Assert.True(blocked.Kind == MilkSaveKind.Failed,
                "未 ready 实例应在绑定检查失败，实际 " + blocked.Kind + "：" + blocked.Reason);
            Assert.False(blocked.Published);
            Assert.Contains("模型留痕未提交", blocked.Reason, StringComparison.Ordinal);
            await using (var conn = await _db.Factory.OpenAsync())
            {
                Assert.Equal(0, await FitTraceDbFixture.Scalar(conn,
                    "SELECT COUNT(*) FROM dbo.FbMilkRun WHERE RunId=@run AND Status=N'success'",
                    new SqlParameter("@run", blocked.RunId)));
            }

            var published = await SaveMilkOnceAsync(ready, matchNo, kick);
            Assert.True(published.Kind == MilkSaveKind.Published,
                "ready 实例绑定提交后应发布，实际 " + published.Kind + "：" + published.Reason);
            await using (var conn = await _db.Factory.OpenAsync())
            {
                Assert.Equal(ready, await FitTraceDbFixture.Scalar(conn,
                    "SELECT ModelFitInstanceId FROM dbo.FbMilkRun WHERE RunId=@run AND Status=N'success'",
                    new SqlParameter("@run", published.RunId)));
            }
        }
        finally
        {
            await RestoreStubMilkTablesAsync();
        }
    }

    private async Task<MilkSaveOutcome> SaveMilkOnceAsync(long instanceId, string matchNo, DateTime kick)
    {
        var factory = _db.Factory;
        var hit = new HitProbabilityService(new BettingRepository(factory));
        hit.SeedCache(new HitProbabilityService.Snapshot(
            HitProbabilityModel.DevigOnly, new(), true, ScorelineModel.Empty, instanceId));
        var svc = new RecommendationService(
            new BettingCardSource(new HttpClient()),
            new BettingRepository(factory),
            new JcOddsRepository(factory),
            hit: hit,
            tickets: new TicketRepository(factory),
            milkPicks: new MilkPickRepository(factory),
            fitStore: new MilkModelFitStore(factory))
        {
            OddsReader = (_, _, _) => Task.FromResult(new List<JcOddsHistoryPoint>())
        };
        return await svc.SaveMilkAsync(new List<MatchRecommendation>
        {
            new()
            {
                Board = Boards.Jc,
                MatchNo = matchNo,
                MatchId = 88001,
                LeagueName = "澳超",
                HomeTeam = "甲队",
                AwayTeam = "乙队",
                MatchTime = kick
            }
        }, default);
    }

    private static (string MatchNo, DateTime Kick) SellableMatch()
    {
        var now = RecommendationSelection.JcSaleHours.Now();
        var prefixes = new[] { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };
        var kicks = new[] { now.AddHours(3), now.Date.AddDays(1).AddHours(20) };
        foreach (var kick in kicks)
        {
            if (kick <= DateTime.Now) continue;
            foreach (var prefix in prefixes)
            {
                var no = prefix + "001";
                if (RecommendationSelection.JcSaleHours.Sellable(no, kick, now))
                    return (no, kick);
            }
        }
        throw new InvalidOperationException("这一刻没有可售期号，发布入口测试停住。");
    }

    private async Task SeedCaptureAsync(string matchNo, DateTime kick)
    {
        await using var conn = await _db.Factory.OpenAsync();
        // 正式表 CapturedAt 是 datetime2(0)。采集查询把 MAX(CapturedAt) 读回后，
        // Dapper 以 SqlDbType.DateTime 传回 @cap 做等值比较。datetime2(7) 的
        // SYSDATETIME() 经 datetime 舍入后对不上本批，期号列表为空，
        // MilkBoardPlan 在绑定检查之前就 Skip。这里按正式精度落到整秒。
        await FitTraceDbFixture.Exec(conn,
            @"INSERT INTO dbo.BettingCard
                (Board, MatchNo, MatchId, LeagueName, HomeTeam, AwayTeam, MatchTime, CapturedAt)
              VALUES (N'jc', @no, 88001, N'澳超', N'甲队', N'乙队', @kick, CONVERT(datetime2(0), SYSDATETIME()))",
            new SqlParameter("@no", matchNo),
            new SqlParameter("@kick", kick));
    }

    private async Task PreparePublishTablesAsync()
    {
        await using var conn = await _db.Factory.OpenAsync();
        await DropMilkTablesKeepFitAsync(conn);
        await FitTraceDbFixture.Exec(conn, """
            CREATE TABLE dbo.BettingCard (
                Board varchar(8) NOT NULL,
                MatchNo nvarchar(32) NULL,
                MatchId bigint NULL,
                LeagueName nvarchar(128) NULL,
                HomeTeam nvarchar(128) NULL,
                AwayTeam nvarchar(128) NULL,
                MatchTime datetime2(0) NULL,
                CapturedAt datetime2(0) NOT NULL
            );
            CREATE TABLE dbo.FbJcOddsHistory (
                MatchId bigint NOT NULL,
                PlayType varchar(10) NOT NULL,
                OptionCode varchar(20) NOT NULL,
                Odds decimal(8,3) NULL,
                CapturedAt datetime2 NOT NULL
            );
            CREATE TABLE dbo.FbMatch (
                MatchId bigint NOT NULL PRIMARY KEY,
                MatchTime datetime2 NULL
            );
            CREATE TABLE dbo.FbMilkPick (
                PickDate date NOT NULL,
                MatchId bigint NOT NULL,
                PlayType varchar(10) NOT NULL,
                OptionCode varchar(20) NOT NULL,
                Hit bit NULL,
                SettledAt datetime2 NULL,
                Voided bit NULL,
                MatchTime datetime2 NULL,
                RunId nvarchar(40) NULL
            );
            CREATE TABLE dbo.FbMilkGate (
                MatchId bigint NOT NULL,
                RunId nvarchar(40) NULL
            );
            """);
        var picks = new MilkPickRepository(_db.Factory);
        await picks.StartRunAsync(new MilkRun
        {
            RunId = "schema-run",
            StartedAt = DateTime.Now,
            RawMatches = 0
        }, DateTime.Today, "skip", null, default);
        await picks.SaveInputsAsync(DateTime.Today, Array.Empty<MilkInputMatch>(), "schema-run", default);
        await FitTraceDbFixture.ApplyPendingScriptAsync(conn);
        await FitTraceDbFixture.Exec(conn, """
            DELETE FROM dbo.FbMilkInput;
            DELETE FROM dbo.FbMilkRun;
            """);
    }

    private async Task RestoreStubMilkTablesAsync()
    {
        await using var conn = await _db.Factory.OpenAsync();
        await DropMilkTablesKeepFitAsync(conn);
        await FitTraceDbFixture.EnsureStubMilkTablesAsync(conn);
        await FitTraceDbFixture.ApplyPendingScriptAsync(conn);
    }

    private static async Task DropMilkTablesKeepFitAsync(SqlConnection conn)
    {
        await FitTraceDbFixture.Exec(conn, """
            IF OBJECT_ID(N'FK_FbMilkRun_ModelFitInstance', N'F') IS NOT NULL
                ALTER TABLE dbo.FbMilkRun DROP CONSTRAINT FK_FbMilkRun_ModelFitInstance;
            DROP TABLE IF EXISTS dbo.FbMilkPick;
            DROP TABLE IF EXISTS dbo.FbMilkGate;
            DROP TABLE IF EXISTS dbo.FbMilkProbSnapshot;
            DROP TABLE IF EXISTS dbo.FbMilkSourceMatch;
            DROP TABLE IF EXISTS dbo.FbMilkRunDate;
            DROP TABLE IF EXISTS dbo.BettingCard;
            DROP TABLE IF EXISTS dbo.FbJcOddsHistory;
            DROP TABLE IF EXISTS dbo.FbMatch;
            DROP TABLE IF EXISTS dbo.FbExchange;
            DROP TABLE IF EXISTS dbo.FbOdds;
            DROP TABLE IF EXISTS dbo.Recommendation;
            DROP TABLE IF EXISTS dbo.FbMilkInput;
            DROP TABLE IF EXISTS dbo.FbMilkRun;
            """);
    }

    private static async Task AssertNewShapeRemainsAsync(SqlConnection conn)
    {
        Assert.Equal(6, await FitTraceDbFixture.Scalar(conn, """
            SELECT COUNT(*) FROM sys.tables
             WHERE name IN (
                N'FbMilkModelFitContent', N'FbMilkModelFitInstance', N'FbMilkModelFitSample',
                N'FbMilkModelFitLeague', N'FbMilkModelFitTeamLeague', N'FbMilkModelFitTeam')
            """));
        Assert.Equal(1, await FitTraceDbFixture.Scalar(conn,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.FbMilkRun') AND name = N'ModelFitInstanceId'"));
        Assert.Equal(4, await FitTraceDbFixture.Scalar(conn, """
            SELECT COUNT(*) FROM sys.columns
             WHERE object_id = OBJECT_ID(N'dbo.FbMilkInput')
               AND name IN (N'ResolvedLeague', N'TotalLine', N'LambdaHome', N'LambdaAway')
            """));
        Assert.Equal(1, await FitTraceDbFixture.Scalar(conn, """
            SELECT COUNT(*) FROM sys.indexes
             WHERE name = N'UX_FbMilkModelFitContent_ReadySha' AND is_unique = 1 AND has_filter = 1
            """));
    }

    /// <summary>
    /// 夹具里的轮次表只有主键和状态。冻结程序的 INSERT 还要写旧列。
    /// 只补这些旧列，不删除已经加上的新列。
    /// </summary>
    private static Task EnsureColumnsTheFrozenProgramWritesAsync(SqlConnection conn)
        => FitTraceDbFixture.Exec(conn, """
            IF COL_LENGTH(N'dbo.FbMilkRun', N'RunDate') IS NULL ALTER TABLE dbo.FbMilkRun ADD RunDate date NULL;
            IF COL_LENGTH(N'dbo.FbMilkRun', N'StartedAt') IS NULL ALTER TABLE dbo.FbMilkRun ADD StartedAt datetime2 NULL;
            IF COL_LENGTH(N'dbo.FbMilkRun', N'RawMatches') IS NULL ALTER TABLE dbo.FbMilkRun ADD RawMatches int NULL;
            IF COL_LENGTH(N'dbo.FbMilkRun', N'SalesDates') IS NULL ALTER TABLE dbo.FbMilkRun ADD SalesDates nvarchar(200) NULL;
            IF COL_LENGTH(N'dbo.FbMilkRun', N'InputCount') IS NULL ALTER TABLE dbo.FbMilkRun ADD InputCount int NULL;
            IF COL_LENGTH(N'dbo.FbMilkRun', N'SnapshotRows') IS NULL ALTER TABLE dbo.FbMilkRun ADD SnapshotRows int NULL;
            IF COL_LENGTH(N'dbo.FbMilkRun', N'GateRows') IS NULL ALTER TABLE dbo.FbMilkRun ADD GateRows int NULL;
            IF COL_LENGTH(N'dbo.FbMilkRun', N'PickRows') IS NULL ALTER TABLE dbo.FbMilkRun ADD PickRows int NULL;
            IF COL_LENGTH(N'dbo.FbMilkRun', N'BoardMode') IS NULL ALTER TABLE dbo.FbMilkRun ADD BoardMode varchar(12) NULL;
            IF COL_LENGTH(N'dbo.FbMilkRun', N'Note') IS NULL ALTER TABLE dbo.FbMilkRun ADD Note nvarchar(400) NULL;
            IF COL_LENGTH(N'dbo.FbMilkRun', N'VersionTag') IS NULL ALTER TABLE dbo.FbMilkRun ADD VersionTag nvarchar(160) NULL;
            IF COL_LENGTH(N'dbo.FbMilkRun', N'PublishedAt') IS NULL ALTER TABLE dbo.FbMilkRun ADD PublishedAt datetime2 NULL;
            IF COL_LENGTH(N'dbo.FbMilkRun', N'FailReason') IS NULL ALTER TABLE dbo.FbMilkRun ADD FailReason nvarchar(300) NULL;
            IF COL_LENGTH(N'dbo.FbMilkRun', N'CreatedAt') IS NULL ALTER TABLE dbo.FbMilkRun ADD CreatedAt datetime2 NULL;
            IF COL_LENGTH(N'dbo.FbMilkInput', N'InputDate') IS NULL ALTER TABLE dbo.FbMilkInput ADD InputDate date NULL;
            IF COL_LENGTH(N'dbo.FbMilkInput', N'MatchNo') IS NULL ALTER TABLE dbo.FbMilkInput ADD MatchNo nvarchar(32) NULL;
            IF COL_LENGTH(N'dbo.FbMilkInput', N'LeagueName') IS NULL ALTER TABLE dbo.FbMilkInput ADD LeagueName nvarchar(128) NULL;
            IF COL_LENGTH(N'dbo.FbMilkInput', N'HomeTeam') IS NULL ALTER TABLE dbo.FbMilkInput ADD HomeTeam nvarchar(128) NULL;
            IF COL_LENGTH(N'dbo.FbMilkInput', N'AwayTeam') IS NULL ALTER TABLE dbo.FbMilkInput ADD AwayTeam nvarchar(128) NULL;
            IF COL_LENGTH(N'dbo.FbMilkInput', N'MatchTime') IS NULL ALTER TABLE dbo.FbMilkInput ADD MatchTime datetime2 NULL;
            IF COL_LENGTH(N'dbo.FbMilkInput', N'VersionTag') IS NULL ALTER TABLE dbo.FbMilkInput ADD VersionTag nvarchar(160) NULL;
            IF COL_LENGTH(N'dbo.FbMilkInput', N'CreatedAt') IS NULL ALTER TABLE dbo.FbMilkInput ADD CreatedAt datetime2 NULL;
            """);

    private static async Task AssertColumn(
        SqlConnection conn, string table, string column, string type, bool nullable, int? maxLength = null)
    {
        var count = await FitTraceDbFixture.Scalar(conn, """
            SELECT COUNT(*)
              FROM sys.columns c
              JOIN sys.types t ON t.user_type_id = c.user_type_id
             WHERE c.object_id = OBJECT_ID(@table)
               AND c.name = @column
               AND t.name = @type
               AND c.is_nullable = @nullable
               AND (@len IS NULL OR c.max_length = @len)
            """,
            new SqlParameter("@table", "dbo." + table),
            new SqlParameter("@column", column),
            new SqlParameter("@type", type),
            new SqlParameter("@nullable", nullable),
            new SqlParameter("@len", (object?)maxLength ?? DBNull.Value));
        Assert.Equal(1, count);
    }

    private async Task ResetAsync()
    {
        await using var conn = await _db.Factory.OpenAsync();
        await FitTraceDbFixture.Exec(conn, """
            IF OBJECT_ID(N'dbo.FbMilkInput', N'U') IS NOT NULL DELETE FROM dbo.FbMilkInput;
            IF OBJECT_ID(N'dbo.FbMilkRun', N'U') IS NOT NULL DELETE FROM dbo.FbMilkRun;
            IF OBJECT_ID(N'dbo.FbMilkModelFitSample', N'U') IS NOT NULL DELETE FROM dbo.FbMilkModelFitSample;
            IF OBJECT_ID(N'dbo.FbMilkModelFitLeague', N'U') IS NOT NULL DELETE FROM dbo.FbMilkModelFitLeague;
            IF OBJECT_ID(N'dbo.FbMilkModelFitTeamLeague', N'U') IS NOT NULL DELETE FROM dbo.FbMilkModelFitTeamLeague;
            IF OBJECT_ID(N'dbo.FbMilkModelFitTeam', N'U') IS NOT NULL DELETE FROM dbo.FbMilkModelFitTeam;
            IF OBJECT_ID(N'dbo.FbMilkModelFitInstance', N'U') IS NOT NULL DELETE FROM dbo.FbMilkModelFitInstance;
            IF OBJECT_ID(N'dbo.FbMilkModelFitContent', N'U') IS NOT NULL DELETE FROM dbo.FbMilkModelFitContent;
            """);
    }

    private static FitModelTrace Trace(int away)
    {
        var start = new DateTime(2025, 1, 1, 15, 0, 0);
        var samples = new List<ScoreSample>();
        for (var i = 0; i < 30; i++)
        {
            samples.Add(new ScoreSample
            {
                MatchId = 1000 + i,
                LeagueName = "英超",
                HomeTeam = "主" + i,
                AwayTeam = "客" + i,
                HomeScore = 1,
                AwayScore = away,
                MatchTime = start.AddDays(i)
            });
        }
        var model = ScorelineModel.Fit(samples, asOf: new DateTime(2026, 6, 1));
        return model.Trace ?? throw new InvalidOperationException("测试样本没有生成追踪");
    }

    private static FitModelTrace BulkTrace(int count)
    {
        var start = new DateTime(2024, 1, 1, 12, 0, 0);
        var samples = new List<FitSampleRow>(count);
        for (var i = 0; i < count; i++)
        {
            samples.Add(new FitSampleRow
            {
                MatchId = i + 1,
                LeagueName = "联" + (i % 20),
                HomeTeam = "主" + (i % 400),
                AwayTeam = "客" + (i % 400),
                CanonHome = "主" + (i % 400),
                CanonAway = "客" + (i % 400),
                HomeScore = 1,
                AwayScore = 0,
                MatchTime = start.AddMinutes(i)
            });
        }
        return new FitModelTrace
        {
            Samples = samples,
            Leagues = new[] { new FitLeagueTotal("联0", count, 0, count, count) },
            TeamLeagues = new[] { new FitTeamLeagueTotal("联0", "主0", count, 0, count, count) },
            Teams = new[] { new FitTeamTotal("主0", count, 0, count, count) },
            GlobalHome = 1,
            GlobalAway = 0,
            Rho = ScorelineModel.DefaultRho,
            HalfLifeDays = null,
            CrossLeague = false
        };
    }

    private static System.Data.DataTable SampleTable(long contentId, IReadOnlyList<FitSampleRow> samples)
    {
        var table = new System.Data.DataTable();
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
        for (var i = 0; i < samples.Count; i++)
        {
            var row = samples[i];
            table.Rows.Add(contentId, i, row.MatchId, row.LeagueName, row.HomeTeam, row.AwayTeam,
                row.CanonHome, row.CanonAway, (short)row.HomeScore, (short)row.AwayScore,
                row.MatchTime ?? (object)DBNull.Value,
                row.MatchTime is DateTime t ? (byte)t.Kind : DBNull.Value);
        }
        return table;
    }

    private static async Task<string> ReadSpaceAsync(SqlConnection conn, string? table)
    {
        await using var cmd = new SqlCommand(table is null ? "EXEC sp_spaceused" : "EXEC sp_spaceused @t", conn)
        {
            CommandTimeout = 60
        };
        if (table is not null) cmd.Parameters.Add(new SqlParameter("@t", table));
        await using var reader = await cmd.ExecuteReaderAsync();
        var parts = new List<string>();
        do
        {
            while (await reader.ReadAsync())
            {
                for (var i = 0; i < reader.FieldCount; i++)
                    parts.Add(reader.GetName(i) + "=" + reader.GetValue(i));
            }
        } while (await reader.NextResultAsync());
        return string.Join("; ", parts);
    }

    private static async Task WriteMeasureAsync(string status, double seconds, string note)
    {
        var dir = Path.Combine(FitTraceTests.RepoRoot(), "source", "aifootball", "tests", "TestResults", "20261115-fit-trace");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "db-measure.txt"),
            status + Environment.NewLine
            + "seconds=" + seconds.ToString("0.###", CultureInfo.InvariantCulture) + Environment.NewLine
            + note + Environment.NewLine);
    }
}
