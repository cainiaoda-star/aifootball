using AiFootBall.Core.Models;
using AiFootBall.Data;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 2026-11-14 复审：发布结论经 <see cref="MilkSaveDecision.Conclude"/>（与 SaveMilkAsync 同一函数）
/// 再 <see cref="RecommendSaveResult.FromMilkOutcome"/>；禁止测试自行复制 Skip/Fail/Ok 映射。
/// </summary>
public class MilkPublishPropagationTests
{
    private static MilkCaptureState CompleteCapture()
        => MilkCaptureState.Build(
            batchAt: DateTime.Now,
            now: DateTime.Now,
            maxAge: TimeSpan.FromHours(12),
            issues: new List<MilkIssueState>
            {
                new() { Issue = "周五", BatchMatches = 11, KnownMatches = 11 }
            });

    private static MilkRunSteps AllStepsOk()
    {
        var s = new MilkRunSteps();
        foreach (var step in MilkSaveDecision.RequiredSteps) s.Ok(step);
        return s;
    }

    [Fact]
    public void Conclude_Skip_When_Plan_Skip()
    {
        var plan = MilkBoardPlan.Create(
            new MilkRun { RawMatches = 0, SalesDates = Array.Empty<DateTime>() },
            Array.Empty<MilkPick>(),
            DateTime.Today);
        Assert.Equal(MilkBoardMode.Skip, plan.Mode);

        var milk = MilkSaveDecision.Conclude(
            plan, "run-skip", new MilkRunSteps(), 0, 0, 0,
            ledgerOk: false, ledgerDetail: null, publishedAt: DateTime.Now);

        Assert.Equal(MilkSaveKind.Skipped, milk.Kind);
        Assert.Equal("skip", milk.PlanMode);
        Assert.False(milk.Published);

        var r = RecommendSaveResult.FromMilkOutcome(
            milk,
            new Dictionary<string, List<MatchRecommendation>> { [Boards.Jc] = new() },
            priorBoardPublishedAt: new DateTime(2026, 11, 14, 11, 40, 0));
        Assert.False(r.Published);
        Assert.Equal("milk-skipped", r.Outcome);
    }

    [Fact]
    public void Conclude_Partial_Is_Failed_Not_Published()
    {
        var capture = MilkCaptureState.Build(
            batchAt: DateTime.Now,
            now: DateTime.Now,
            maxAge: TimeSpan.FromHours(12),
            issues: new List<MilkIssueState>
            {
                new() { Issue = "周五", BatchMatches = 8, KnownMatches = 11 }
            });
        Assert.False(capture.Complete);

        var plan = MilkBoardPlan.Create(
            new MilkRun
            {
                RawMatches = 8,
                SalesDates = new[] { DateTime.Today },
                Capture = capture
            },
            Array.Empty<MilkPick>(),
            DateTime.Today);
        Assert.Equal(MilkBoardMode.Partial, plan.Mode);

        var milk = MilkSaveDecision.Conclude(
            plan, "run-p", AllStepsOk(), 8, 10, 0,
            ledgerOk: true, ledgerDetail: "should-not-matter", publishedAt: DateTime.Now);

        Assert.Equal(MilkSaveKind.Failed, milk.Kind);
        Assert.Equal("partial", milk.PlanMode);
        Assert.False(milk.Published);
        Assert.Equal("milk-failed", RecommendSaveResult.FromMilkOutcome(
            milk, new Dictionary<string, List<MatchRecommendation>>(), null).Outcome);
    }

    [Fact]
    public void Conclude_StepFailure_Does_Not_Publish()
    {
        var plan = MilkBoardPlan.Create(
            new MilkRun
            {
                RawMatches = 11,
                SalesDates = new[] { DateTime.Today },
                Capture = CompleteCapture()
            },
            Array.Empty<MilkPick>(),
            DateTime.Today);
        Assert.Equal(MilkBoardMode.Rewrite, plan.Mode);

        var steps = new MilkRunSteps();
        steps.Ok("picks");
        steps.Fail("gate", "SqlException: gate down");
        steps.Ok("input");
        steps.Ok("snapshot");
        steps.Ok("source");

        var milk = MilkSaveDecision.Conclude(
            plan, "run-step", steps, 5, 20, 3,
            ledgerOk: true, ledgerDetail: null, publishedAt: DateTime.Now);

        Assert.Equal(MilkSaveKind.Failed, milk.Kind);
        Assert.Contains("落库未全部成功", milk.Reason);
        Assert.Contains("gate", milk.Reason);
        Assert.False(milk.Published);
    }

    [Fact]
    public void Conclude_LedgerFailure_Does_Not_Publish()
    {
        var plan = MilkBoardPlan.Create(
            new MilkRun
            {
                RawMatches = 11,
                SalesDates = new[] { DateTime.Today },
                Capture = CompleteCapture()
            },
            Array.Empty<MilkPick>(),
            DateTime.Today);

        var milk = MilkSaveDecision.Conclude(
            plan, "run-ledger", AllStepsOk(), 5, 20, 3,
            ledgerOk: false,
            ledgerDetail: "逐档核对不通过（缺失 2、重复 0、多写 0、豁免/拒绝不成立 0）：snap:1 missing",
            publishedAt: DateTime.Now);

        Assert.Equal(MilkSaveKind.Failed, milk.Kind);
        Assert.StartsWith("落库核对失败：", milk.Reason);
        Assert.False(milk.Published);
    }

    [Fact]
    public void Conclude_Success_Only_When_Rewrite_Steps_And_Ledger_Ok()
    {
        var plan = MilkBoardPlan.Create(
            new MilkRun
            {
                RawMatches = 11,
                SalesDates = new[] { DateTime.Today },
                Capture = CompleteCapture()
            },
            Array.Empty<MilkPick>(),
            DateTime.Today);
        var at = new DateTime(2026, 11, 14, 16, 40, 0);

        var milk = MilkSaveDecision.Conclude(
            plan, "run-ok", AllStepsOk(), 5, 20, 3,
            ledgerOk: true, ledgerDetail: "ok", publishedAt: at);

        Assert.Equal(MilkSaveKind.Published, milk.Kind);
        Assert.True(milk.Published);
        Assert.Equal(at, milk.PublishedAt);
        Assert.Equal("run-ok", milk.RunId);

        var r = RecommendSaveResult.FromMilkOutcome(
            milk, new Dictionary<string, List<MatchRecommendation>>(), null);
        Assert.True(r.Published);
        Assert.Equal("published", r.Outcome);
    }

    [Fact]
    public void FromMilk_BoardStatusError_Does_Not_Fabricate_PublishedAt()
    {
        var plan = MilkBoardPlan.Create(
            new MilkRun
            {
                RawMatches = 11,
                SalesDates = new[] { DateTime.Today },
                Capture = CompleteCapture()
            },
            Array.Empty<MilkPick>(),
            DateTime.Today);
        var milk = MilkSaveDecision.Conclude(
            plan, "run-2", AllStepsOk(), 1, 4, 1,
            ledgerOk: true, ledgerDetail: "ok",
            publishedAt: new DateTime(2026, 11, 14, 16, 40, 0));

        var r = RecommendSaveResult.FromMilkOutcome(
            milk, new Dictionary<string, List<MatchRecommendation>>(),
            priorBoardPublishedAt: null,
            boardStatusError: "SqlException: timeout");
        Assert.True(r.Published);
        Assert.Equal(new DateTime(2026, 11, 14, 16, 40, 0), r.BoardPublishedAt);
        Assert.Contains("timeout", r.BoardStatusError);
    }

    [Fact]
    public void Failed_Milk_Keeps_Prior_Board_Time_Not_Now()
    {
        var prior = new DateTime(2026, 11, 14, 11, 40, 0);
        var plan = MilkBoardPlan.Create(
            new MilkRun { RawMatches = 0, SalesDates = Array.Empty<DateTime>() },
            Array.Empty<MilkPick>(), DateTime.Today);
        var milk = MilkSaveDecision.Conclude(
            plan, "run-x", new MilkRunSteps(), 0, 0, 0,
            ledgerOk: false, ledgerDetail: null, publishedAt: DateTime.Now);

        var r = RecommendSaveResult.FromMilkOutcome(
            milk, new Dictionary<string, List<MatchRecommendation>>(), prior);
        Assert.False(r.Published);
        Assert.Equal(prior, r.BoardPublishedAt);
    }

    [Fact]
    public async Task Wiring_AfterGate_Uses_Conclude_Then_FromMilkOutcome()
    {
        var hooks = new RecommendSaveHooks
        {
            EvaluateGate = _ => Task.FromResult(MilkGateResult.Allow("fresh", DateTime.Now, DateTime.Now)),
            AfterGateSaveMilk = _ =>
            {
                var plan = MilkBoardPlan.Create(
                    new MilkRun { RawMatches = 0, SalesDates = Array.Empty<DateTime>() },
                    Array.Empty<MilkPick>(), DateTime.Today);
                // 与生产同一判定函数，禁止手写 Skip(...)
                var milk = MilkSaveDecision.Conclude(
                    plan, "hook-run", new MilkRunSteps(), 0, 0, 0,
                    ledgerOk: false, ledgerDetail: null, publishedAt: DateTime.Now);
                return Task.FromResult((
                    new Dictionary<string, List<MatchRecommendation>> { [Boards.Jc] = new() },
                    milk));
            },
            GetBoardStatus = (_, _) => Task.FromResult<MilkBoardStatus?>(new MilkBoardStatus
            {
                PublishedAt = new DateTime(2026, 11, 14, 11, 40, 0)
            })
        };

        var http = new HttpClient();
        var factory = new SqlConnectionFactory(
            "Server=(localdb)\\none;Database=AiFootBall_WiringProbe_DO_NOT_CONNECT;Trusted_Connection=True;TrustServerCertificate=True");
        var svc = new RecommendationService(
            new BettingCardSource(http),
            new BettingRepository(factory),
            new JcOddsRepository(factory),
            hooks: hooks);

        var save = await svc.RecommendAllAndSaveAsync();
        Assert.False(save.Published);
        Assert.Equal("milk-skipped", save.Outcome);
        Assert.Equal(new DateTime(2026, 11, 14, 11, 40, 0), save.BoardPublishedAt);
    }

    [Fact]
    public async Task BoardStatus_Read_Failure_Does_Not_Block_Gate_Reject()
    {
        var hooks = new RecommendSaveHooks
        {
            EvaluateGate = _ => Task.FromResult(MilkGateResult.Reject(
                "manual-only", null, DateTime.Today.AddHours(16.5), "manual-only:…:degrade=off")),
            RecordRejection = (_, _) => Task.CompletedTask,
            GetBoardStatus = (_, _) => throw new InvalidOperationException("board-status-down")
        };
        var http = new HttpClient();
        var factory = new SqlConnectionFactory(
            "Server=(localdb)\\none;Database=AiFootBall_WiringProbe_DO_NOT_CONNECT;Trusted_Connection=True;TrustServerCertificate=True");
        var svc = new RecommendationService(
            new BettingCardSource(http),
            new BettingRepository(factory),
            new JcOddsRepository(factory),
            hooks: hooks);

        var save = await svc.RecommendAllAndSaveAsync();
        Assert.False(save.Published);
        Assert.Equal("gate-reject", save.Outcome);
        Assert.Null(save.BoardPublishedAt);
        Assert.Contains("board-status-down", save.BoardStatusError);
    }
}
