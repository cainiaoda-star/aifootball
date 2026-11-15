using AiFootBall.Core.Models;
using AiFootBall.Data;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 留痕的纯函数边界。不连数据库。隔离库上的批量、回滚和并发在 FitTraceDbTests。
/// </summary>
public class FitTraceTests
{
    [Fact]
    public void SameSamples_DifferentFitMoment_KeepTheSameContentHash()
    {
        var samples = Samples(away: 0);
        var early = ScorelineModel.Fit(samples, asOf: new DateTime(2026, 6, 1, 12, 0, 0));
        var late = ScorelineModel.Fit(samples, asOf: new DateTime(2026, 8, 1, 18, 30, 0));

        Assert.NotNull(early.Trace);
        Assert.NotNull(late.Trace);
        Assert.Equal(FitContentCodec.Hash(early.Trace!), FitContentCodec.Hash(late.Trace!));
        Assert.DoesNotContain("2026-06-01T12", FitContentCodec.Text(early.Trace!), StringComparison.Ordinal);
        Assert.DoesNotContain("2026-08-01T18", FitContentCodec.Text(late.Trace!), StringComparison.Ordinal);
    }

    [Fact]
    public void OneScoreChange_ChangesTheContentHash()
    {
        var left = ScorelineModel.Fit(Samples(away: 0), asOf: new DateTime(2026, 6, 1));
        var right = ScorelineModel.Fit(Samples(away: 1), asOf: new DateTime(2026, 6, 1));
        Assert.NotEqual(FitContentCodec.Hash(left.Trace!), FitContentCodec.Hash(right.Trace!));
    }

    [Fact]
    public void EmptyModel_HasNoTrace_AndCannotPublish()
    {
        var thin = ScorelineModel.Fit(Samples(away: 0).Take(8).ToList(), asOf: new DateTime(2026, 6, 1));
        Assert.False(thin.Ready);
        Assert.Null(thin.Trace);
        Assert.False(MilkFitTraceGate.AllowsPublish(MilkFitBindResult.Reject("x")));
        Assert.True(MilkFitTraceGate.AllowsPublish(MilkFitBindResult.Ok()));
        Assert.Equal("模型留痕未提交", MilkFitTraceGate.BlockReason);
    }

    [Fact]
    public void TraceCopies_MatchTheFittedTotals()
    {
        var samples = Samples(away: 0);
        var model = ScorelineModel.Fit(samples, asOf: new DateTime(2026, 6, 1));
        var trace = model.Trace!;

        Assert.Equal(model.FitGames, trace.Samples.Count);
        Assert.Equal(samples.Count, trace.Leagues.Single().N);
        Assert.Equal(samples.Sum(s => s.HomeScore), trace.Leagues.Single().HomeSum, 6);
        Assert.Equal(samples.Average(s => s.HomeScore), trace.GlobalHome, 6);
        Assert.Equal(samples.Count * 2, trace.TeamLeagues.Sum(t => t.Games));
        Assert.Equal(samples.Select(s => TeamAliases.Canonical(s.HomeTeam)).Distinct().Count()
                     + samples.Select(s => TeamAliases.Canonical(s.AwayTeam)).Distinct().Count(),
            trace.Teams.Count);
        Assert.Contains("fit-content-1", FitContentCodec.Text(trace), StringComparison.Ordinal);
        var (info, mvid) = FitContentCodec.CodeIdentity();
        Assert.Contains(info, FitContentCodec.ParameterLine(trace), StringComparison.Ordinal);
        Assert.Contains(mvid, FitContentCodec.ParameterLine(trace), StringComparison.Ordinal);
        Assert.Equal(32, mvid.Length);
    }

    [Fact]
    public void FingerprintConstants_MatchTheEngineConstants()
    {
        Assert.Equal(MilkPickEngine.HalfGoalShare, FitContentCodec.HalfGoalShare);
        Assert.Equal(MilkPickEngine.MaxHalfGoals, FitContentCodec.MaxHalfGoals);
        var model = ScorelineModel.Fit(Samples(away: 0));
        var version = MilkPickEngine.PickVersion(model);
        var config = MilkPickEngine.FitConfigVersion();
        Assert.EndsWith(config, version, StringComparison.Ordinal);
        Assert.Equal("w=70/70/50/80/30|bf=off", config);
    }

    [Fact]
    public void ScoreSampleSql_KeepsTheOriginalClockPredicates()
    {
        var sql = BettingRepository.ScoreSampleSql;
        Assert.Contains("SELECT f.MatchId,", sql, StringComparison.Ordinal);
        Assert.Contains("CONVERT(datetime2(7), GETDATE()) AS QueryClock", sql, StringComparison.Ordinal);
        Assert.Contains("f.MatchTime >= DATEADD(month, -18, GETDATE())", sql, StringComparison.Ordinal);
        Assert.Contains("f.MatchTime <= DATEADD(minute, -@dur, @asOf)", sql, StringComparison.Ordinal);
        Assert.Contains("f.MatchTime <= DATEADD(minute, -@dur, GETDATE())", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY f.MatchTime, f.MatchId", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecommendKeepsSnapshotA_WhenCacheBecomesB_AndBindUsesA()
    {
        var samples = new List<ScoreSample>();
        var start = new DateTime(2025, 6, 1, 15, 0, 0);
        var id = 1;
        for (var i = 0; i < 6; i++)
            samples.Add(Row(id++, "英超", "甲队", "丙" + i, 4, 0, start));
        for (var i = 0; i < 6; i++)
            samples.Add(Row(id++, "英超", "丁" + i, "乙队", 0, 1, start));
        while (samples.Count < 30)
            samples.Add(Row(id++, "英超", "北" + id, "南" + id, 1, 0, start));

        var modelA = ScorelineModel.Fit(samples);
        Assert.True(modelA.Ready);
        Assert.True(modelA.TeamGames("英超", "甲队") >= MilkPickEngine.MinTeamGames);
        Assert.True(modelA.TeamGames("英超", "乙队") >= MilkPickEngine.MinTeamGames);
        const decimal line = 2.5m;
        var fromA = modelA.ExpectedGoals("英超", "甲队", "乙队", line);
        var fromB = ScorelineModel.Empty.ExpectedGoals("英超", "甲队", "乙队", line);
        Assert.NotNull(fromA);
        Assert.Null(fromB);

        var http = new HttpClient();
        var factory = new SqlConnectionFactory(
            "Server=(localdb)\\none;Database=AiFootBall_WiringProbe_DO_NOT_CONNECT;Trusted_Connection=True;TrustServerCertificate=True;Connect Timeout=1");
        var repo = new BettingRepository(factory);
        var hit = new HitProbabilityService(repo);
        var snapA = new HitProbabilityService.Snapshot(
            HitProbabilityModel.DevigOnly, new(), true, modelA, 11);
        var snapB = HitProbabilityService.Snapshot.Empty;
        hit.SeedCache(snapA);

        var store = new RecordingFitStore(factory);
        var svc = new RecommendationService(
            new BettingCardSource(http), repo, new JcOddsRepository(factory),
            hit: hit, fitStore: store)
        {
            OddsReader = (_, _, _) => Task.FromResult(new List<JcOddsHistoryPoint>
            {
                new() { MatchId = 1, PlayType = JcPlayTypes.Spf, OptionCode = "3", OptionName = "胜", Odds = 2.1m }
            }),
            AfterMilkSnapshotCaptured = () => hit.SeedCache(snapB)
        };

        var built = await svc.RecommendMilkWithGateAsync(new[]
        {
            new MatchRecommendation
            {
                Board = Boards.Jc, MatchNo = "周日001", MatchId = 1,
                LeagueName = "英超", HomeTeam = "甲队", AwayTeam = "乙队",
                MatchTime = DateTime.Today.AddDays(1),
                TotalLine = line
            }
        });

        var row = Assert.Single(built.Inputs);
        Assert.Equal(fromA.Value.Home, row.LambdaHome);
        Assert.Equal(fromA.Value.Away, row.LambdaAway);
        Assert.Equal(line, row.TotalLine);
        Assert.True(built.Funnel.ScoresReady);
        Assert.Equal(11, built.FitInstanceId);

        Assert.Same(snapB, hit.Cached);
        Assert.False(hit.Cached.Scores.Ready);
        Assert.Null(hit.Cached.FitInstanceId);

        store.Next = MilkFitBindResult.Ok();
        Assert.Null(await svc.BindFitTraceAsync("run-a", built, default));
        Assert.Equal(11, store.SeenId);
    }

    [Fact]
    public async Task MissingInstance_IsRejectedByTheStoreBeforeAnyConnection()
    {
        var factory = new SqlConnectionFactory(
            "Server=(localdb)\\none;Database=AiFootBall_WiringProbe_DO_NOT_CONNECT;Trusted_Connection=True;TrustServerCertificate=True;Connect Timeout=1");
        var store = new MilkModelFitStore(factory);
        var result = await store.BindRunAsync("run-empty", null, Array.Empty<MilkInputMatch>(), default);
        Assert.False(result.Committed);
        Assert.False(MilkFitTraceGate.AllowsPublish(result));
        Assert.Contains("轮次 run-empty", result.Reason, StringComparison.Ordinal);
        Assert.Contains("实例 无", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BindFailure_ScrubsConnectionString_AndKeepsThisSnapshot()
    {
        var http = new HttpClient();
        var factory = new SqlConnectionFactory(
            "Server=(localdb)\\none;Database=AiFootBall_WiringProbe_DO_NOT_CONNECT;Trusted_Connection=True;TrustServerCertificate=True;Connect Timeout=1");
        var hit = new HitProbabilityService(new BettingRepository(factory));
        hit.SeedCache(new HitProbabilityService.Snapshot(
            HitProbabilityModel.DevigOnly, new(), true, ScorelineModel.Empty, 9, "SNAP-KEEP"));
        var store = new ThrowingFitStore(factory);
        var svc = new RecommendationService(
            new BettingCardSource(http), new BettingRepository(factory), new JcOddsRepository(factory),
            hit: hit, fitStore: store);
        var reason = await svc.BindFitTraceAsync("run-secret", new MilkBuildResult
        {
            FitInstanceId = 9,
            TraceError = "SNAP-KEEP"
        }, default);

        Assert.Contains("轮次 run-secret", reason, StringComparison.Ordinal);
        Assert.Contains("实例 9", reason, StringComparison.Ordinal);
        Assert.Contains("SNAP-KEEP", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("SuperSecret", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("10.1.2.3", reason, StringComparison.Ordinal);
        Assert.Contains("password=***", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiagnosticWriteFailure_DoesNotReplaceTheComputedSnapshot()
    {
        var factory = new SqlConnectionFactory(
            "Server=(localdb)\\none;Database=AiFootBall_WiringProbe_DO_NOT_CONNECT;Trusted_Connection=True;TrustServerCertificate=True;Connect Timeout=1");
        var hit = new HitProbabilityService(new BettingRepository(factory));
        var computed = new HitProbabilityService.Snapshot(
            HitProbabilityModel.DevigOnly, new(), true, ScorelineModel.Empty, null, "SNAP-KEEP");
        hit.TraceDiagnostic = _ => throw new InvalidOperationException("Password=SuperSecret;Server=10.1.2.3");
        hit.WriteTraceDiagnostic(computed.TraceError!);
        hit.CommitComputedSnapshot(computed);

        Assert.Same(computed, hit.Cached);
        Assert.Equal("SNAP-KEEP", hit.Cached.TraceError);
        Assert.Null(hit.Cached.FitInstanceId);
        Assert.False(hit.Cached.Scores.Ready);
    }

    [Fact]
    public void RollbackFailure_StaysAttachedToTheOriginalException()
    {
        Assert.Equal(CancellationToken.None, MilkModelFitStore.RollbackToken);
        var original = new InvalidOperationException("样本写入中断");
        MilkFitTraceDiagnostics.AttachRollback(original,
            new InvalidOperationException("Password=SuperSecret;Server=10.1.2.3 rollback"));
        var text = MilkFitTraceDiagnostics.Describe(original);
        Assert.Contains("样本写入中断", text, StringComparison.Ordinal);
        Assert.Contains("回滚失败", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SuperSecret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("10.1.2.3", text, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(original);
    }

    [Fact]
    public void ShortSampleTeam_RecordsLineAndTheSameExpectedGoals()
    {
        var samples = new List<ScoreSample>();
        var start = new DateTime(2025, 3, 1, 15, 0, 0);
        for (var i = 0; i < 4; i++)
            samples.Add(Row(i, "英超", "甲队", "乙队", 1, 0, start));
        for (var i = 0; i < 4; i++)
            samples.Add(Row(10 + i, "英超", "丙队", "丁队", 5, 0, start));
        for (var i = 0; i < 22; i++)
            samples.Add(Row(30 + i, "英超", "北队", "南队", 1, 0, start));

        var model = ScorelineModel.Fit(samples, asOf: new DateTime(2026, 6, 1));
        Assert.True(model.Ready);
        Assert.Equal(4, model.TeamGames("英超", "甲队"));
        Assert.Equal(4, model.TeamGames("英超", "丙队"));
        Assert.InRange(model.TeamGames("英超", "甲队"), MilkPickEngine.MinTeamGames, 4);

        var unscaledHome = model.ExpectedGoals("英超", "甲队", "乙队", null);
        var unscaledAwayPair = model.ExpectedGoals("英超", "丙队", "丁队", null);
        Assert.Equal(unscaledHome, unscaledAwayPair);

        var scaled = model.ExpectedGoals("英超", "甲队", "乙队", 2.5m);
        var odds = new Dictionary<long, List<JcOddsHistoryPoint>>
        {
            [1] = new() { Point(1) },
            [2] = new() { Point(2) }
        };
        var built = MilkPickEngine.BuildDetailed(new[]
        {
            Rec(1, "甲队", "乙队", 2.5m),
            Rec(2, "丙队", "丁队", null)
        }, odds, model);

        var withLine = Assert.Single(built.Inputs, x => x.MatchId == 1);
        var noLine = Assert.Single(built.Inputs, x => x.MatchId == 2);
        Assert.Equal("英超", withLine.ResolvedLeague);
        Assert.Equal(2.5m, withLine.TotalLine);
        Assert.Equal(scaled!.Value.Home, withLine.LambdaHome);
        Assert.Equal(scaled.Value.Away, withLine.LambdaAway);
        Assert.Equal("英超", noLine.ResolvedLeague);
        Assert.Null(noLine.TotalLine);
        Assert.Equal(unscaledHome!.Value.Home, noLine.LambdaHome);
        Assert.Equal(unscaledHome.Value.Away, noLine.LambdaAway);
    }

    [Fact]
    public void Probe_RefusesUntilAnIsolatedInstanceIsNamed()
    {
        var missing = FitTraceDbGuard.Resolve(null, null);
        Assert.False(missing.Accepted);
        Assert.Equal("", missing.ConnectionString);

        var wrongDb = FitTraceDbGuard.Resolve(
            "Server=lab\\FITTRACE;Database=AiFootBall;Integrated Security=true",
            "LAB\\FITTRACE");
        Assert.False(wrongDb.Accepted);

        var productionHost = FitTraceDbGuard.Resolve(
            "Server=192.168.117.128,1433;Database=AiFootBall_FitTrace_Test;Integrated Security=true",
            "WINSEV25DATA");
        Assert.False(productionHost.Accepted);

        var named = FitTraceDbGuard.Resolve(
            "Server=lab\\FITTRACE;Database=AiFootBall_FitTrace_Test;Integrated Security=true",
            "LAB\\FITTRACE");
        Assert.True(named.Accepted);
        Assert.Equal("LAB\\FITTRACE", named.ExpectedInstance);
        Assert.Contains("AiFootBall_FitTrace_Test", named.ConnectionString, StringComparison.Ordinal);

        var sameCatalog = Assert.Throws<InvalidOperationException>(() =>
            FitTraceDbGuard.AssertInstance("WINSEV25DATA", "LAB\\FITTRACE"));
        Assert.Contains("仅库名相同不能证明隔离", sameCatalog.Message, StringComparison.Ordinal);
        FitTraceDbGuard.AssertInstance("LAB\\FITTRACE", "lab\\FITTRACE");
        Assert.Throws<InvalidOperationException>(() =>
            FitTraceDbGuard.AssertDatabase("AiFootBall", FitTraceDbGuard.DatabaseName));

        if (string.Equals(Environment.MachineName, FitTraceDbGuard.ProductionMachine, StringComparison.OrdinalIgnoreCase))
        {
            Assert.False(FitTraceDbProbe.Available);
            Assert.Contains(FitTraceDbGuard.ProductionMachine, FitTraceDbProbe.Reason, StringComparison.Ordinal);
            Assert.Equal("", FitTraceDbProbe.ConnectionString);
        }

        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "source", "aifootball", "tests", "AiFootBall.Tests", "FitTraceDbTests.cs"));
        Assert.DoesNotContain("db-credential", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("appsettings", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AIFOOTBALL_CONN", source, StringComparison.Ordinal);
        Assert.DoesNotContain("localdb", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Frozen82c93bce_LoadsDataCoreOnly_AndRefusesTheLiveHost()
    {
        var root = RepoRoot();
        var names = LegacyBinaryProbe.Inspect(root);
        Assert.Equal(new[] { "AiFootBall.Core", "AiFootBall.Data" }, names);

        var live = Path.Combine(root, "deploy-package", "app", "api");
        var refused = Assert.Throws<InvalidOperationException>(() => LegacyBinaryProbe.AssertDirectoryAllowed(live));
        Assert.Contains("现网内容根", refused.Message, StringComparison.Ordinal);

        var prod = Assert.Throws<InvalidOperationException>(() => LegacyBinaryProbe.AssertIsolatedConnection(
            "Server=192.168.117.128;Database=AiFootBall_FitTrace_Test;Trusted_Connection=True"));
        Assert.Contains("生产地址", prod.Message, StringComparison.Ordinal);

        if (string.Equals(Environment.MachineName, FitTraceDbGuard.ProductionMachine, StringComparison.OrdinalIgnoreCase))
        {
            var blocked = Assert.Throws<InvalidOperationException>(() => LegacyBinaryProbe.AssertIsolatedConnection(
                "Server=127.0.0.1;Database=AiFootBall_FitTrace_Test;Trusted_Connection=True"));
            Assert.Contains(FitTraceDbGuard.ProductionMachine, blocked.Message, StringComparison.Ordinal);
        }

        var probe = File.ReadAllText(Path.Combine(
            root, "source", "aifootball", "tests", "AiFootBall.Tests", "LegacyBinaryProbe.cs"));
        Assert.DoesNotContain("Process.Start", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessStartInfo", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("AIFOOTBALL_CONN", probe, StringComparison.Ordinal);

        var db = File.ReadAllText(Path.Combine(
            root, "source", "aifootball", "tests", "AiFootBall.Tests", "FitTraceDbTests.cs"));
        Assert.DoesNotContain("LegacyRun_SurvivesDdl_AndRollbackRemovesOnlyTheNewShape", db, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP TABLE dbo.FbMilkModelFitContent", db, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP TABLE IF EXISTS dbo.FbMilkModelFitContent", db, StringComparison.Ordinal);
        Assert.Contains("LegacyBinary_ReadsAndWritesWhileNewShapeRemains", db, StringComparison.Ordinal);
    }

    [Fact]
    public void PendingScript_IsNotEmbeddedOrEnsured()
    {
        var root = RepoRoot();
        var pending = Path.Combine(root, "source", "aifootball", "sql", "pending", "2026-11-15_milk_model_fit.sql");
        Assert.True(File.Exists(pending));
        var dbDir = Path.Combine(root, "source", "aifootball", "db");
        Assert.DoesNotContain(Directory.GetFiles(dbDir, "*.sql"),
            path => path.Contains("milk_model_fit", StringComparison.OrdinalIgnoreCase));

        var names = typeof(SqlConnectionFactory).Assembly.GetManifestResourceNames();
        Assert.DoesNotContain(names, name => name.Contains("milk_model_fit", StringComparison.OrdinalIgnoreCase));

        var schema = File.ReadAllText(Path.Combine(root, "source", "aifootball", "src", "AiFootBall.Data", "RecommendationSchema.cs"));
        var picks = File.ReadAllText(Path.Combine(root, "source", "aifootball", "src", "AiFootBall.Data", "MilkPickRepository.cs"));
        var store = File.ReadAllText(Path.Combine(root, "source", "aifootball", "src", "AiFootBall.Data", "MilkModelFitStore.cs"));
        Assert.DoesNotContain("milk_model_fit", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FbMilkModelFit", picks, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE", store, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER TABLE", store, StringComparison.OrdinalIgnoreCase);
    }

    private static List<ScoreSample> Samples(int away)
    {
        var start = new DateTime(2025, 1, 1, 15, 0, 0);
        var rows = new List<ScoreSample>();
        for (var i = 0; i < 30; i++)
            rows.Add(Row(1000 + i, "英超", "主" + i, "客" + i, 1, away, start));
        return rows;
    }

    private static ScoreSample Row(int id, string league, string home, string away, int hs, int ascore, DateTime start)
        => new()
        {
            MatchId = id,
            LeagueName = league,
            HomeTeam = home,
            AwayTeam = away,
            HomeScore = hs,
            AwayScore = ascore,
            MatchTime = start.AddHours(id)
        };

    private static MatchRecommendation Rec(long id, string home, string away, decimal? line) => new()
    {
        Board = Boards.Jc,
        MatchNo = "周三00" + id,
        MatchId = id,
        LeagueName = "英超",
        HomeTeam = home,
        AwayTeam = away,
        MatchTime = new DateTime(2026, 11, 18, 20, 0, 0),
        TotalLine = line
    };

    private static JcOddsHistoryPoint Point(long id) => new()
    {
        MatchId = id,
        PlayType = JcPlayTypes.Spf,
        OptionCode = "3",
        OptionName = "胜",
        Odds = 2.1m
    };

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var pending = Path.Combine(dir.FullName, "source", "aifootball", "sql", "pending", "2026-11-15_milk_model_fit.sql");
            if (File.Exists(pending)) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到待审脚本，无法定位仓库根");
    }
}

sealed class RecordingFitStore : MilkModelFitStore
{
    public RecordingFitStore(SqlConnectionFactory factory) : base(factory) { }

    public long? SeenId { get; private set; }
    public MilkFitBindResult Next { get; set; } = MilkFitBindResult.Reject("未设置");

    public override Task<MilkFitBindResult> BindRunAsync(
        string runId, long? instanceId, IReadOnlyList<MilkInputMatch> inputs, CancellationToken ct)
    {
        SeenId = instanceId;
        return Task.FromResult(Next);
    }
}

sealed class ThrowingFitStore : MilkModelFitStore
{
    public ThrowingFitStore(SqlConnectionFactory factory) : base(factory) { }

    public override Task<MilkFitBindResult> BindRunAsync(
        string runId, long? instanceId, IReadOnlyList<MilkInputMatch> inputs, CancellationToken ct)
        => throw new InvalidOperationException("login failed Password=SuperSecret;Server=10.1.2.3");
}
