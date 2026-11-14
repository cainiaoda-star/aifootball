using AiFootBall.Core.Models;
using AiFootBall.Data;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 构造接线 + 闸路径。发布判据见 <see cref="MilkPublishPropagationTests"/>（经 FromMilkOutcome）。
/// </summary>
public class RecommendSaveWiringTests
{
    private static RecommendationService Svc(RecommendSaveHooks hooks)
    {
        var http = new HttpClient();
        var factory = new SqlConnectionFactory(
            "Server=(localdb)\\none;Database=AiFootBall_WiringProbe_DO_NOT_CONNECT;Trusted_Connection=True;TrustServerCertificate=True");
        return new RecommendationService(
            new BettingCardSource(http),
            new BettingRepository(factory),
            new JcOddsRepository(factory),
            hooks: hooks);
    }

    [Fact]
    public void Construction_Without_Sync_Reports_Missing()
    {
        var http = new HttpClient();
        var factory = new SqlConnectionFactory(
            "Server=(localdb)\\none;Database=AiFootBall_WiringProbe_DO_NOT_CONNECT;Trusted_Connection=True;TrustServerCertificate=True");
        var svc = new RecommendationService(
            new BettingCardSource(http),
            new BettingRepository(factory),
            new JcOddsRepository(factory));
        Assert.False(svc.HasSyncLog);
    }

    [Fact]
    public void Construction_With_Sync_Reports_Present()
    {
        var http = new HttpClient();
        var factory = new SqlConnectionFactory(
            "Server=(localdb)\\none;Database=AiFootBall_WiringProbe_DO_NOT_CONNECT;Trusted_Connection=True;TrustServerCertificate=True");
        var sync = new SyncLogRepository(factory);
        var svc = new RecommendationService(
            new BettingCardSource(http),
            new BettingRepository(factory),
            new JcOddsRepository(factory),
            sync: sync);
        Assert.True(svc.HasSyncLog);
    }

    [Fact]
    public async Task Reject_Records_Trace_And_Does_Not_Publish()
    {
        var recorded = new List<MilkGateResult>();
        var milkHookCalled = false;
        var boardAt = new DateTime(2026, 11, 14, 11, 40, 0);
        var hooks = new RecommendSaveHooks
        {
            EvaluateGate = _ => Task.FromResult(MilkGateResult.Reject(
                "manual-only", boardAt, boardAt, "manual-only:11:40:16:30:degrade=off")),
            RecordRejection = (g, _) => { recorded.Add(g); return Task.CompletedTask; },
            AfterGateSaveMilk = _ =>
            {
                milkHookCalled = true;
                return Task.FromResult((
                    new Dictionary<string, List<MatchRecommendation>>(),
                    MilkSaveOutcome.Ok("x", "should-not", DateTime.Now, 0)));
            },
            GetBoardStatus = (_, _) => Task.FromResult<MilkBoardStatus?>(new MilkBoardStatus
            {
                SalesDate = DateTime.Today,
                PublishedAt = boardAt,
                PublishedRunId = "old-run",
                LastStatus = "success"
            })
        };

        var save = await Svc(hooks).RecommendAllAndSaveAsync();

        Assert.False(save.Published);
        Assert.Equal("gate-reject", save.Outcome);
        Assert.Equal("manual-only", save.Verdict);
        Assert.Equal(boardAt, save.BoardPublishedAt);
        Assert.False(milkHookCalled);
        Assert.Single(recorded);
    }

    [Fact]
    public async Task CrossDay_Skip_Is_GateSkip_Not_Failure_Banner()
    {
        MilkGateResult? gate = null;
        var hooks = new RecommendSaveHooks
        {
            EvaluateGate = _ => Task.FromResult(MilkGateResult.Skip(
                "cross-day-skip", new DateTime(2026, 11, 13, 20, 30, 0), "cross-day@…")),
            RecordRejection = (g, _) => { gate = g; return Task.CompletedTask; },
            AfterGateSaveMilk = _ => throw new InvalidOperationException("must not publish"),
            GetBoardStatus = (_, _) => Task.FromResult<MilkBoardStatus?>(new MilkBoardStatus
            {
                SalesDate = DateTime.Today,
                PublishedAt = new DateTime(2026, 11, 13, 20, 40, 0),
                LastStatus = "skipped",
                LastFailReason = "cross-day@…"
            })
        };

        var save = await Svc(hooks).RecommendAllAndSaveAsync();
        Assert.False(save.Published);
        Assert.Equal("gate-skip", save.Outcome);
        Assert.Equal("cross-day-skip", save.Verdict);
        Assert.Equal(MilkGateBand.Skip, gate!.Band);

        var st = await hooks.GetBoardStatus!(DateTime.Today, default);
        Assert.True(st!.LastAttemptSkipped);
        Assert.False(st.LastAttemptFailed);
    }

    [Fact]
    public async Task NoSale_Alert_Does_Not_Publish()
    {
        var recorded = 0;
        var hooks = new RecommendSaveHooks
        {
            EvaluateGate = _ => Task.FromResult(MilkGateResult.Alert(
                "no-sale", DateTime.Now, DateTime.Now, "no-sale:…:degrade=off")),
            RecordRejection = (_, _) => { recorded++; return Task.CompletedTask; },
            AfterGateSaveMilk = _ => throw new InvalidOperationException("must not"),
            GetBoardStatus = (_, _) => Task.FromResult<MilkBoardStatus?>(new MilkBoardStatus
            {
                PublishedAt = new DateTime(2026, 11, 14, 11, 40, 0)
            })
        };

        var save = await Svc(hooks).RecommendAllAndSaveAsync();
        Assert.False(save.Published);
        Assert.Equal("gate-reject", save.Outcome);
        Assert.Equal("no-sale", save.Verdict);
        Assert.Equal(1, recorded);
    }
}
