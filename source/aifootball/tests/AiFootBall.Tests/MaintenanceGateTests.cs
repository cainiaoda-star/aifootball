using AiFootBall.Core.Models;
using AiFootBall.Data;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 维护登记的并发边界。等待用 TaskCompletionSource，不用 Sleep 碰时机。
/// </summary>
public class MaintenanceGateTests
{
    private static TaskCompletionSource Hold()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task Refresh_Already_Running_Is_Waited_Out()
    {
        var gate = new MaintenanceGate();
        var hold = Hold();
        Assert.True(gate.TrySchedule(MaintenanceScope.ModelRefresh, () => hold.Task, out _));

        var waiting = gate.EnterAndWaitAsync(TimeSpan.FromMinutes(1));
        Assert.False(waiting.IsCompleted);

        hold.SetResult();
        var snap = await waiting;
        Assert.True(snap.Drained);
        Assert.Equal("drained", snap.Phase);
        Assert.Equal(0, snap.ActivityCount);
    }

    [Fact]
    public async Task Enter_And_Start_Cannot_Leave_A_Refresh_Untracked()
    {
        var gate = new MaintenanceGate();
        var hold = Hold();
        var started = Task.Run(() =>
        {
            var ok = gate.TrySchedule(MaintenanceScope.ModelRefresh, () => hold.Task, out var work);
            return ok ? work : null;
        });
        var entered = Task.Run(() => gate.EnterAndWaitAsync(TimeSpan.Zero));
        await Task.WhenAll(started, entered);

        var work = await started;
        var snap = await entered;
        if (work is null)
        {
            Assert.NotEqual("normal", snap.Phase);
            Assert.Equal(0, snap.ActivityCount);
            Assert.False(gate.TrySchedule(MaintenanceScope.ModelRefresh, () => Task.CompletedTask, out _));
        }
        else
        {
            Assert.False(work.Task.IsCompleted);
            Assert.False(snap.Drained);
            Assert.Equal(1, gate.Snapshot().ActivityCount);
            hold.SetResult();
            var done = await gate.EnterAndWaitAsync(TimeSpan.FromMinutes(1));
            Assert.True(done.Drained);
        }
    }

    [Fact]
    public async Task Queued_And_Derived_Work_Block_Drain()
    {
        var gate = new MaintenanceGate();
        var childHold = Hold();
        var parentGo = new TaskCompletionSource<MaintenanceWork>(TaskCreationOptions.RunContinuationsAsynchronously);
        MaintenanceSnapshot? registered = null;
        gate.OnRegistered = snap => registered ??= snap;

        Assert.True(gate.TrySchedule(MaintenanceScope.ModelRefresh, async () =>
        {
            var self = await parentGo.Task;
            Assert.True(gate.TryScheduleChild(self, "model-write", () => childHold.Task, out _));
        }, out var parent));
        Assert.Equal("queued", registered!.Activities.Single().State);

        parentGo.SetResult(parent!);
        var sawChild = Hold();
        gate.OnRegistered = snap =>
        {
            if (snap.Activities.Any(a => a.Category == "model-write"))
                sawChild.TrySetResult();
        };
        await sawChild.Task;

        var blocked = await gate.EnterAndWaitAsync(TimeSpan.Zero);
        Assert.False(blocked.Drained);
        Assert.Contains(blocked.Activities, a => a.Category == "model-write");
        Assert.Equal("draining", blocked.Phase);

        childHold.SetResult();
        var done = await gate.EnterAndWaitAsync(TimeSpan.FromMinutes(1));
        Assert.True(done.Drained);
    }

    [Fact]
    public async Task Success_Fault_And_Cancel_Release_And_Stay_Visible()
    {
        var gate = new MaintenanceGate();
        Assert.True(gate.TrySchedule("model-refresh", () => Task.CompletedTask, out var ok));
        await ok!.Task;
        Assert.Equal(0, gate.Snapshot().ActivityCount);
        Assert.Null(gate.Snapshot().LastError);

        Assert.True(gate.TrySchedule("model-refresh", () => Task.FromException(new InvalidOperationException("boom")), out var bad));
        var boom = await Assert.ThrowsAsync<InvalidOperationException>(() => bad!.Task);
        Assert.Equal("boom", boom.Message);
        Assert.Equal("boom", gate.Snapshot().LastError);
        Assert.Equal(0, gate.Snapshot().ActivityCount);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.True(gate.TrySchedule("model-refresh", () => Task.FromCanceled(cts.Token), out var cancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled!.Task);
        Assert.False(string.IsNullOrEmpty(gate.Snapshot().LastError));
        Assert.Equal(0, gate.Snapshot().ActivityCount);
        Assert.Equal("normal", gate.Snapshot().Phase);
    }

    [Fact]
    public async Task Timeout_Keeps_The_Work_And_The_Maintenance_Flag()
    {
        var gate = new MaintenanceGate();
        var hold = Hold();
        Assert.True(gate.TrySchedule(MaintenanceScope.ModelRefresh, () => hold.Task, out var work));
        var snap = await gate.EnterAndWaitAsync(TimeSpan.Zero);
        Assert.False(snap.Drained);
        Assert.Equal("draining", snap.Phase);
        Assert.Equal(1, snap.ActivityCount);
        Assert.Contains("未排空", snap.TimeoutReason);
        Assert.False(work!.Task.IsCompleted);
        Assert.Equal("draining", gate.Snapshot().Phase);

        await gate.EnterAndWaitAsync(TimeSpan.Zero);
        Assert.Equal("draining", gate.Snapshot().Phase);
        hold.SetResult();
        Assert.True((await gate.EnterAndWaitAsync(TimeSpan.FromMinutes(1))).Drained);
    }

    [Fact]
    public async Task Repeat_Enter_Exit_And_Queries_Do_Not_Drop_Work()
    {
        var gate = new MaintenanceGate();
        var hold = Hold();
        Assert.True(gate.TrySchedule(MaintenanceScope.ModelRefresh, () => hold.Task, out _));
        var first = await gate.EnterAndWaitAsync(TimeSpan.Zero);
        var second = await gate.EnterAndWaitAsync(TimeSpan.Zero);
        Assert.Equal(first.Phase, second.Phase);
        Assert.Equal(1, second.ActivityCount);

        var reads = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => gate.Snapshot())));
        Assert.All(reads, s => Assert.Equal(1, s.ActivityCount));
        Assert.Equal("draining", gate.Snapshot().Phase);

        var left = gate.Exit();
        Assert.Equal("normal", left.Phase);
        Assert.Equal(1, left.ActivityCount);
        Assert.False(hold.Task.IsCompleted);
        var again = gate.Exit();
        Assert.Equal("normal", again.Phase);
        Assert.Equal(1, again.ActivityCount);
    }

    [Fact]
    public void Query_Does_Not_Start_Work()
    {
        var gate = new MaintenanceGate();
        var calls = 0;
        var snap = MaintenanceCommands.Query(gate, allowed: true);
        Assert.NotNull(snap);
        Assert.Equal(0, snap!.ActivityCount);
        Assert.Equal(0, calls);
        Assert.Null(MaintenanceCommands.Query(gate, allowed: false));
        Assert.Equal("normal", gate.Snapshot().Phase);
    }

    [Fact]
    public async Task Shutdown_Waits_For_The_Write_And_Cancel_Is_Not_Success()
    {
        var gate = new MaintenanceGate();
        var hold = Hold();
        Assert.True(gate.TrySchedule(MaintenanceScope.ModelRefresh, () => hold.Task, out var work));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var stopped = await gate.WaitForShutdownAsync(cts.Token);
        Assert.False(stopped);
        Assert.False(gate.Snapshot().Drained);
        Assert.Equal(1, gate.Snapshot().ActivityCount);
        Assert.False(work!.Task.IsCompleted);
        Assert.Contains("未排空", gate.Snapshot().TimeoutReason);

        var waiting = gate.WaitForShutdownAsync(CancellationToken.None);
        Assert.False(waiting.IsCompleted);
        hold.SetResult();
        Assert.True(await waiting);
        Assert.True(work.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Lotto_Is_Outside_The_Gate_And_Is_Not_Cancelled()
    {
        Assert.Equal(new[] { MaintenanceScope.ModelRefresh }, MaintenanceScope.Managed);
        Assert.Contains("lotto-daily", MaintenanceScope.NotManaged);
        Assert.Contains("lotto-recommend", MaintenanceScope.NotManaged);
        Assert.DoesNotContain("lotto-daily", MaintenanceScope.Managed);

        var gate = new MaintenanceGate();
        var hold = Hold();
        Assert.True(gate.TrySchedule("lotto-daily", () => hold.Task, out var work));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.False(await gate.WaitForShutdownAsync(cts.Token));
        Assert.False(work!.Task.IsCompleted);
        hold.SetResult();
        await work.Task;
    }

    [Fact]
    public async Task Unauthorized_Call_Does_Not_Change_Phase()
    {
        Assert.False(MaintenanceCommands.Allows(false, true, "secret", "secret"));
        Assert.False(MaintenanceCommands.Allows(true, false, "secret", "secret"));
        Assert.False(MaintenanceCommands.Allows(true, true, "", "secret"));
        Assert.False(MaintenanceCommands.Allows(true, true, "secret", "nope"));
        Assert.True(MaintenanceCommands.Allows(true, true, "secret", "secret"));

        var gate = new MaintenanceGate();
        Assert.Null(MaintenanceCommands.Exit(gate, allowed: false));
        Assert.Equal("normal", gate.Snapshot().Phase);
        Assert.Null(await MaintenanceCommands.EnterAsync(gate, allowed: false, TimeSpan.Zero));
        Assert.Equal("normal", gate.Snapshot().Phase);
    }

    [Fact]
    public async Task Maintenance_Uses_Cache_Or_Says_Unavailable_Then_Resumes_Once()
    {
        var gate = new MaintenanceGate();
        var svc = new HitProbabilityService(
            new BettingRepository(new SqlConnectionFactory("Server=127.0.0.1,1;Database=unused;TrustServerCertificate=True")),
            maintenance: gate);
        var calls = 0;
        svc.RefreshOverride = _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        };

        svc.SeedCache(Loaded());
        var fresh = await svc.GetSnapshotAsync();
        Assert.True(fresh.Loaded);
        Assert.Equal(0, calls);

        svc.MarkCacheExpired();
        Assert.True((await gate.EnterAndWaitAsync(TimeSpan.Zero)).Drained);
        var stale = await svc.GetSnapshotAsync();
        Assert.True(stale.Loaded);
        Assert.Equal(0, calls);
        Assert.Equal(0, gate.Snapshot().ActivityCount);

        gate.Exit();
        var empty = new HitProbabilityService(
            new BettingRepository(new SqlConnectionFactory("Server=127.0.0.1,1;Database=unused;TrustServerCertificate=True")),
            maintenance: gate);
        empty.RefreshOverride = _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        };
        await gate.EnterAndWaitAsync(TimeSpan.Zero);
        await Assert.ThrowsAsync<MaintenanceUnavailableException>(() => empty.GetSnapshotAsync());
        Assert.Equal(0, calls);

        gate.Exit();
        svc.MarkCacheExpired();
        var hold = Hold();
        var started = Hold();
        svc.RefreshOverride = _ =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            return hold.Task;
        };
        var reads = Task.WhenAll(svc.GetSnapshotAsync(), svc.GetSnapshotAsync());
        await started.Task;
        await reads;
        Assert.Equal(1, calls);
        Assert.Equal(1, gate.Snapshot().ActivityCount);
        hold.SetResult();
        Assert.True((await gate.EnterAndWaitAsync(TimeSpan.FromMinutes(1))).Drained);
        Assert.Equal(1, calls);
    }

    private static HitProbabilityService.Snapshot Loaded()
        => new(HitProbabilityModel.DevigOnly, new(), true);
}
