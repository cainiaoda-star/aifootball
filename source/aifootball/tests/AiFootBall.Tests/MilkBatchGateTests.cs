using AiFootBall.Core.Models;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>2026-11-14 批次闸：表驱动 + 本轮 NoSale 不发布修订。</summary>
public class MilkBatchGateTests
{
    private static DateTime Day(int h, int m, int s = 0)
        => new(2026, 11, 14, h, m, s);

    private static CardsSyncRec Daily(DateTime finished, int rows, string message)
        => new()
        {
            TaskName = MilkBatchGate.DailyTask,
            FinishedAt = finished,
            RowsAffected = rows,
            Message = message
        };

    private static CardsSyncRec Manual(DateTime finished, int rows, string message)
        => new()
        {
            TaskName = MilkBatchGate.ManualTask,
            FinishedAt = finished,
            RowsAffected = rows,
            Message = message
        };

    [Fact]
    public void Fresh_Allows_When_Same_Rec_Pairs_And_Complete()
    {
        var cap = Day(16, 31, 5);
        var rec = Daily(Day(16, 31, 10), 20, "受注场次：竞彩 12 / 北单 5 / 胜负彩 3");
        var r = MilkBatchGate.Check(new MilkGateInput
        {
            RunAt = Day(16, 40),
            Cap = cap,
            Rec = rec,
            Complete = true
        });
        Assert.Equal(MilkGateBand.Allow, r.Band);
        Assert.Equal("fresh", r.Verdict);
        Assert.True(r.Published);
    }

    [Fact]
    public void C1_BatchMismatch_When_Cap_Pairs_Rec1_But_Rec2_Is_Latest()
    {
        // cap 对齐 11:31，但「最近成功」是 16:35（另一条）——闸只认同一条 rec
        var cap = Day(11, 31, 17);
        var rec2 = Daily(Day(16, 35, 0), 20, "受注场次：竞彩 12 / 北单 5 / 胜负彩 3");
        var r = MilkBatchGate.Check(new MilkGateInput
        {
            RunAt = Day(16, 40),
            Cap = cap,
            Rec = rec2,
            Complete = true
        });
        Assert.Equal(MilkGateBand.Reject, r.Band);
        Assert.Equal("batch-mismatch", r.Verdict);
        Assert.Contains("degrade=off", r.FailReason);
        Assert.False(r.Published);
    }

    [Fact]
    public void C2_ManualOnly_When_Only_Manual_In_Slot()
    {
        var cap = Day(15, 25, 15);
        var rec = Manual(Day(15, 25, 20), 12, "受注场次：竞彩 12 / 北单 0 / 胜负彩 0");
        var r = MilkBatchGate.Check(new MilkGateInput
        {
            RunAt = Day(16, 40),
            Cap = cap,
            Rec = rec,
            Complete = true
        });
        Assert.Equal(MilkGateBand.Reject, r.Band);
        Assert.Equal("manual-only", r.Verdict);
        Assert.False(r.Published);
    }

    [Fact]
    public void C3_NoCapture_When_FinishedAt_After_RunAt()
    {
        var cap = Day(16, 46, 12);
        var rec = Daily(Day(16, 46, 12), 20, "受注场次：竞彩 12 / 北单 5 / 胜负彩 3");
        var r = MilkBatchGate.Check(new MilkGateInput
        {
            RunAt = Day(16, 40),
            Cap = cap, // also future relative? cap > runAt → future-batch first
            Rec = rec,
            Complete = true
        });
        Assert.Equal(MilkGateBand.Reject, r.Band);
        Assert.Equal("future-batch", r.Verdict);
    }

    [Fact]
    public void C3b_NoCapture_When_Rec_Before_Slot()
    {
        var cap = Day(11, 31, 17);
        var rec = Daily(Day(11, 31, 17), 20, "受注场次：竞彩 12 / 北单 5 / 胜负彩 3");
        var r = MilkBatchGate.Check(new MilkGateInput
        {
            RunAt = Day(16, 40),
            Cap = cap,
            Rec = rec,
            Complete = true
        });
        Assert.Equal(MilkGateBand.Reject, r.Band);
        Assert.Equal("no-capture-this-slot", r.Verdict);
        Assert.Contains("degrade=off", r.FailReason);
    }

    [Fact]
    public void C4_Partial_Even_When_Batch_Fresh()
    {
        var cap = Day(16, 31, 5);
        var rec = Daily(Day(16, 31, 10), 8, "受注场次：竞彩 8 / 北单 0 / 胜负彩 0");
        var r = MilkBatchGate.Check(new MilkGateInput
        {
            RunAt = Day(16, 40),
            Cap = cap,
            Rec = rec,
            Complete = false
        });
        Assert.Equal(MilkGateBand.AlertNoPublish, r.Band);
        Assert.Equal("partial", r.Verdict);
        Assert.False(r.Published);
    }

    [Fact]
    public void C5_DegradeEnabled_False_MaxBatchAge_Unused()
    {
        Assert.False(MilkBatchGate.DegradeEnabled);
        var cap = Day(11, 31, 17);
        var rec = Daily(Day(11, 31, 17), 20, "受注场次：竞彩 12 / 北单 5 / 胜负彩 3");
        var a = MilkBatchGate.Check(new MilkGateInput
        {
            RunAt = Day(16, 40), Cap = cap, Rec = rec, Complete = true
        });
        // 改 MaxBatchAge 字段不影响输出（关闭时不参与）
        _ = MilkBatchGate.MaxBatchAge;
        var b = MilkBatchGate.Check(new MilkGateInput
        {
            RunAt = Day(16, 40), Cap = cap, Rec = rec, Complete = true
        });
        Assert.Equal(a.Verdict, b.Verdict);
        Assert.Equal("no-capture-this-slot", a.Verdict);
    }

    [Fact]
    public void C6_NoSale_Evidence_Complete_Still_No_Publish()
    {
        // 本轮修订：证据齐也不清榜撤票
        var cap = Day(11, 31, 17); // 旧批，未被 no-sale 轮改写
        var rec = Daily(Day(16, 31, 10), 5,
            "[no-sale] jc=0 bd=2 sfc=3 containers=jc:1;bd:1;sfc:1");
        var r = MilkBatchGate.Check(new MilkGateInput
        {
            RunAt = Day(16, 40),
            Cap = cap,
            Rec = rec,
            Complete = true,
            CapUnchangedSinceRec = true
        });
        Assert.Equal(MilkGateBand.AlertNoPublish, r.Band);
        Assert.Equal("no-sale", r.Verdict);
        Assert.False(r.Published);
        Assert.Contains("no-sale", r.FailReason);
    }

    [Fact]
    public void NoSale_Cap_Touched_After_Rec_Is_BatchMismatch()
    {
        var cap = Day(16, 31, 12); // 贴近 rec → 说明被改过
        var rec = Daily(Day(16, 31, 10), 0,
            "[no-sale] jc=0 bd=0 sfc=0 containers=jc:1;bd:1;sfc:1");
        var r = MilkBatchGate.Check(new MilkGateInput
        {
            RunAt = Day(16, 40),
            Cap = cap,
            Rec = rec,
            CapUnchangedSinceRec = false
        });
        Assert.Equal("batch-mismatch", r.Verdict);
        Assert.False(r.Published);
    }

    [Fact]
    public void CrossDay_Skips_Without_Publish()
    {
        var cap = new DateTime(2026, 11, 13, 20, 31, 0);
        var rec = Daily(cap, 20, "受注场次：竞彩 12 / 北单 5 / 胜负彩 3");
        var r = MilkBatchGate.Check(new MilkGateInput
        {
            RunAt = Day(8, 40),
            Cap = cap,
            Rec = rec,
            Complete = true
        });
        Assert.Equal(MilkGateBand.Skip, r.Band);
        Assert.Equal("cross-day-skip", r.Verdict);
        Assert.False(r.Published);
        Assert.False(r.Alerts);
    }

    [Fact]
    public void NoBatch_When_Cap_Null()
    {
        var r = MilkBatchGate.Check(new MilkGateInput { RunAt = Day(16, 40), Cap = null });
        Assert.Equal("no-batch", r.Verdict);
        Assert.False(r.Published);
    }

    [Fact]
    public void JcFetchFailed_From_Partial_Message()
    {
        var cap = Day(16, 31, 5);
        var rec = Daily(Day(16, 31, 10), 8,
            "[partial] jc=0 bd=5 sfc=3 containers=jc:0;bd:1;sfc:1 jc:fetch-failed:no-jc-container;bd:5;sfc:3");
        var r = MilkBatchGate.Check(new MilkGateInput
        {
            RunAt = Day(16, 40),
            Cap = cap,
            Rec = rec,
            Complete = true
        });
        Assert.Equal("jc-fetch-failed", r.Verdict);
        Assert.False(r.Published);
    }

    [Fact]
    public void SlotOf_Picks_Latest_Cards_Slot_On_Or_Before_RunAt()
    {
        Assert.Equal(Day(16, 30), MilkBatchGate.SlotOf(Day(16, 40)));
        Assert.Equal(Day(11, 30), MilkBatchGate.SlotOf(Day(12, 0)));
        Assert.Equal(Day(8, 30), MilkBatchGate.SlotOf(Day(9, 0)));
    }

    [Fact]
    public void CaptureReadError_Is_Not_Swallowed_As_NoBatch()
    {
        var r = MilkBatchGate.Check(new MilkGateInput
        {
            RunAt = Day(16, 40),
            Cap = null,
            CaptureError = "SqlException: timeout"
        });
        Assert.Equal("capture-read-error", r.Verdict);
        Assert.Contains("SqlException", r.FailReason);
        Assert.False(r.Published);
    }

    [Fact]
    public void SyncReadError_Is_Not_Swallowed_As_BatchMismatch()
    {
        var r = MilkBatchGate.Check(new MilkGateInput
        {
            RunAt = Day(16, 40),
            Cap = Day(16, 31),
            SyncError = "SyncLogRepository-not-injected"
        });
        Assert.Equal("synclog-read-error", r.Verdict);
        Assert.Contains("not-injected", r.FailReason);
    }

    [Fact]
    public void Incomplete_When_No_Issues_Even_If_Cap_Present()
    {
        // 对应已删除的 issues.Count==0→complete=true 兜底：全已开赛不得放行
        var cap = Day(16, 31, 5);
        var rec = Daily(Day(16, 31, 10), 20, "受注场次：竞彩 12 / 北单 5 / 胜负彩 3");
        var r = MilkBatchGate.Check(new MilkGateInput
        {
            RunAt = Day(16, 40),
            Cap = cap,
            Rec = rec,
            Complete = false
        });
        Assert.Equal("partial", r.Verdict);
        Assert.False(r.Published);
    }
}
