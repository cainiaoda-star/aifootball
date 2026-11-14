using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 临场重算：结构化结果传播 + 调度记账（gate-skip→IDLE，拒绝/失败→FAILED，异常另记）。
/// </summary>
public class NearKickoffRefreshTests
{
    [Fact]
    public void FromSave_Preserves_GateSkip_Outcome()
    {
        var save = RecommendSaveResult.FromGate(
            MilkGateResult.Skip("cross-day-skip", DateTime.Today.AddDays(-1), "cross-day@…"),
            boardPublishedAt: new DateTime(2026, 11, 13, 20, 30, 0));
        var r = NearKickoffRefreshResult.FromSave(stale: 3, save);

        Assert.False(r.Published);
        Assert.Equal(3, r.Stale);
        Assert.Equal("gate-skip", r.Outcome);
        Assert.Equal("cross-day-skip", r.Verdict);
        Assert.Equal(0, r.Saved);

        var job = NearKickoffJobMapping.ToCollectionResult(r);
        Assert.True(job.Idle);
        Assert.True(job.Success);
        Assert.Equal("IDLE", job.StatusText);
        Assert.Contains("gate-skip", job.Message);
        Assert.Contains("cross-day-skip", job.Message);
    }

    [Fact]
    public void FromSave_GateReject_Is_Failed_Not_Idle()
    {
        var save = RecommendSaveResult.FromGate(
            MilkGateResult.Reject("manual-only", null, DateTime.Today.AddHours(16.5), "manual-only:…"),
            boardPublishedAt: null);
        var r = NearKickoffRefreshResult.FromSave(2, save);
        var job = NearKickoffJobMapping.ToCollectionResult(r);

        Assert.Equal("gate-reject", r.Outcome);
        Assert.False(job.Success);
        Assert.False(job.Idle);
        Assert.Equal("FAILED", job.StatusText);
        Assert.Contains("gate-reject", job.Message);
        Assert.DoesNotContain("批次闸未放行", job.Message); // 旧笼统文案不得再出现
    }

    [Fact]
    public void FromSave_MilkSkipped_Keeps_Outcome_Not_Fake_Gate()
    {
        var plan = MilkBoardPlan.Create(
            new MilkRun { RawMatches = 0, SalesDates = Array.Empty<DateTime>() },
            Array.Empty<MilkPick>(), DateTime.Today);
        var milk = MilkSaveDecision.Conclude(
            plan, "nk-skip", new MilkRunSteps(), 0, 0, 0,
            ledgerOk: false, ledgerDetail: null, publishedAt: DateTime.Now);
        var save = RecommendSaveResult.FromMilkOutcome(
            milk, new Dictionary<string, List<MatchRecommendation>>(), null);
        var r = NearKickoffRefreshResult.FromSave(1, save);
        var job = NearKickoffJobMapping.ToCollectionResult(r);

        Assert.Equal("milk-skipped", r.Outcome);
        Assert.False(job.Success);
        Assert.Contains("milk-skipped", job.Message);
    }

    [Fact]
    public void FromSave_Published_Is_Success()
    {
        var capture = MilkCaptureState.Build(
            DateTime.Now, DateTime.Now, TimeSpan.FromHours(12),
            new List<MilkIssueState>
            {
                new() { Issue = "周五", BatchMatches = 5, KnownMatches = 5 }
            });
        var plan = MilkBoardPlan.Create(
            new MilkRun
            {
                RawMatches = 5,
                SalesDates = new[] { DateTime.Today },
                Capture = capture
            },
            Array.Empty<MilkPick>(), DateTime.Today);
        var steps = new MilkRunSteps();
        foreach (var s in MilkSaveDecision.RequiredSteps) steps.Ok(s);
        var milk = MilkSaveDecision.Conclude(
            plan, "nk-ok", steps, 2, 8, 1,
            ledgerOk: true, ledgerDetail: "ok",
            publishedAt: new DateTime(2026, 11, 14, 20, 10, 0));
        var boards = new Dictionary<string, List<MatchRecommendation>>
        {
            [Boards.Jc] = new() { new MatchRecommendation { MatchNo = "周五001" } }
        };
        var save = RecommendSaveResult.FromMilkOutcome(milk, boards, null);
        var r = NearKickoffRefreshResult.FromSave(4, save);
        var job = NearKickoffJobMapping.ToCollectionResult(r);

        Assert.True(r.Published);
        Assert.Equal(4, r.Stale);
        Assert.True(job.Success);
        Assert.False(job.Idle);
        Assert.Contains("已重算并保存", job.Message);
        Assert.Contains("nk-ok", job.Message);
    }

    [Fact]
    public void Idle_When_No_Stale()
    {
        var r = NearKickoffRefreshResult.Idle();
        var job = NearKickoffJobMapping.ToCollectionResult(r);
        Assert.Equal("idle", r.Outcome);
        Assert.True(job.Success);
        Assert.False(job.Idle);
        Assert.Contains("没有需要重算", job.Message);
    }
}
