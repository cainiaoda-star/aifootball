using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace AiFootBall.Sources;

/// <summary>
/// 模型刷新的维护登记。只纳管 <see cref="MaintenanceScope.ModelRefresh"/>。
/// 数字彩、采集作业、手工采集和 token 续期不在这里，排空成功不等于整个进程可以停。
/// </summary>
public static class MaintenanceScope
{
    public const string ModelRefresh = "model-refresh";

    public static readonly string[] Managed = [ModelRefresh];

    /// <summary>这些工作这次不加准入，也不取消。停机前不能把它们当成已经结束。</summary>
    public static readonly string[] NotManaged =
    [
        "lotto-daily",
        "lotto-recommend",
        "job-scheduler",
        "manual-collect",
        "token-health",
        "odds-collection"
    ];
}

/// <summary>没有可用缓存，维护期又不允许新拟合。</summary>
public sealed class MaintenanceUnavailableException : Exception
{
    public MaintenanceUnavailableException()
        : base("维护中，没有可用的模型缓存")
    {
    }
}

public enum MaintenancePhase
{
    Normal,
    Draining,
    Drained
}

public sealed record MaintenanceActivityView(string Category, string State, DateTime StartedAt);

public sealed record MaintenanceSnapshot(
    string Phase,
    int ActivityCount,
    IReadOnlyList<MaintenanceActivityView> Activities,
    DateTime? MaintenanceStartedAt,
    bool Drained,
    string? TimeoutReason,
    string? LastError); // TimeoutReason 只描述当前这次等待。已排空时为 null，不另留历史字段。

public sealed class MaintenanceWork
{
    internal MaintenanceWork(string id, string category, DateTime startedAt)
    {
        Id = id;
        Category = category;
        StartedAt = startedAt;
    }

    public string Id { get; }
    public string Category { get; }
    public DateTime StartedAt { get; }
    public string State { get; internal set; } = "queued";
    public Task Task { get; internal set; } = Task.CompletedTask;
}

/// <summary>
/// 准入和登记共用一把锁。维护标志先于等待翻转，等待超时不把仍在跑的任务移出清单。
/// </summary>
public sealed class MaintenanceGate
{
    private readonly object _gate = new();
    private readonly Dictionary<string, MaintenanceWork> _items = new(StringComparer.Ordinal);
    private MaintenancePhase _phase = MaintenancePhase.Normal;
    private DateTime? _startedAt;
    private string? _timeoutReason;
    private string? _lastError;
    private bool _timeoutNoted;
    private int _seq;

    /// <summary>进入、退出、超时各记一次。查询不调用。</summary>
    public Action<string>? Notice { get; set; }

    /// <summary>测试在锁内观察「已登记、尚未执行」。回调里不能再进这把锁。</summary>
    internal Action<MaintenanceSnapshot>? OnRegistered { get; set; }

    public bool TrySchedule(string category, Func<Task> body, out MaintenanceWork? work)
        => TryScheduleCore(category, body, parent: null, out work);

    internal bool TryScheduleChild(MaintenanceWork parent, string category, Func<Task> body, out MaintenanceWork? work)
        => TryScheduleCore(category, body, parent, out work);

    public MaintenanceSnapshot Snapshot()
    {
        lock (_gate) return Copy();
    }

    public async Task<MaintenanceSnapshot> EnterAndWaitAsync(TimeSpan timeout)
    {
        lock (_gate)
        {
            if (_phase == MaintenancePhase.Normal)
            {
                _phase = MaintenancePhase.Draining;
                _startedAt = DateTime.UtcNow;
                _timeoutReason = null;
                _timeoutNoted = false;
                Note("进入维护，停止新的模型刷新");
            }
        }
        return await WaitForIdleAsync(DateTime.UtcNow + timeout, CancellationToken.None, shutdown: false)
            .ConfigureAwait(false);
    }

    public MaintenanceSnapshot Exit()
    {
        lock (_gate)
        {
            if (_phase != MaintenancePhase.Normal)
            {
                _phase = MaintenancePhase.Normal;
                _startedAt = null;
                _timeoutReason = null;
                _timeoutNoted = false;
                Note("退出维护");
            }
            return Copy();
        }
    }

    /// <summary>
    /// 宿主停止时先进入维护，再等待已登记任务。令牌取消只结束这次等待，不取消任务，也不报已排空。
    /// </summary>
    public async Task<bool> WaitForShutdownAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_phase == MaintenancePhase.Normal)
            {
                _phase = MaintenancePhase.Draining;
                _startedAt = DateTime.UtcNow;
                _timeoutReason = null;
                _timeoutNoted = false;
                Note("进程停止，停止新的模型刷新");
            }
        }

        var snap = await WaitForIdleAsync(DateTime.MaxValue, ct, shutdown: true).ConfigureAwait(false);
        return snap.Drained;
    }

    private async Task<MaintenanceSnapshot> WaitForIdleAsync(DateTime deadline, CancellationToken ct, bool shutdown)
    {
        while (true)
        {
            Task[] pending;
            lock (_gate)
            {
                if (_items.Count == 0)
                {
                    if (_phase == MaintenancePhase.Draining)
                        _phase = MaintenancePhase.Drained;
                    if (_phase == MaintenancePhase.Drained)
                        _timeoutReason = null;
                    return Copy();
                }
                pending = _items.Values.Select(i => i.Task).ToArray();
            }

            if (ct.IsCancellationRequested)
                return MarkTimeout(shutdown);

            var all = Task.WhenAll(pending);
            Task signal;
            if (shutdown)
                signal = Task.Delay(Timeout.Infinite, ct);
            else
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    return MarkTimeout(shutdown);
                signal = Task.Delay(remaining);
            }

            var finished = await Task.WhenAny(all, signal).ConfigureAwait(false);
            if (finished != all)
                return MarkTimeout(shutdown);
            _ = all.Exception;
        }
    }

    private MaintenanceSnapshot MarkTimeout(bool shutdown)
    {
        lock (_gate)
        {
            if (_items.Count == 0 && _phase == MaintenancePhase.Draining)
            {
                _phase = MaintenancePhase.Drained;
                _timeoutReason = null;
                return Copy();
            }
            if (_items.Count > 0)
            {
                var names = string.Join(",", _items.Values.Select(i => i.Category + ":" + i.State));
                _timeoutReason = (shutdown ? "宿主停止时未排空：" : "未排空：") + names;
                if (!_timeoutNoted)
                {
                    _timeoutNoted = true;
                    Note(_timeoutReason);
                }
            }
            return Copy();
        }
    }

    private bool TryScheduleCore(string category, Func<Task> body, MaintenanceWork? parent, out MaintenanceWork? work)
    {
        if (body is null) throw new ArgumentNullException(nameof(body));
        lock (_gate)
        {
            if (parent is null)
            {
                if (_phase != MaintenancePhase.Normal)
                {
                    work = null;
                    return false;
                }
                var existing = _items.Values.FirstOrDefault(i => i.Category == category);
                if (existing is not null)
                {
                    work = existing;
                    return true;
                }
            }
            else if (!_items.ContainsKey(parent.Id))
            {
                work = null;
                return false;
            }

            var item = new MaintenanceWork("m" + (++_seq), category, DateTime.UtcNow);
            _items.Add(item.Id, item);
            item.Task = Task.Run(() => RunAsync(item, body));
            ObserveFaults(item.Task);
            OnRegistered?.Invoke(Copy());
            work = item;
            return true;
        }
    }

    private async Task RunAsync(MaintenanceWork item, Func<Task> body)
    {
        try
        {
            lock (_gate) item.State = "running";
            await body().ConfigureAwait(false);
            lock (_gate) _lastError = null;
        }
        catch (Exception ex)
        {
            lock (_gate) _lastError = ex.Message;
            throw;
        }
            finally
            {
                lock (_gate)
                {
                    _items.Remove(item.Id);
                    if (_phase == MaintenancePhase.Draining && _items.Count == 0)
                    {
                        _phase = MaintenancePhase.Drained;
                        _timeoutReason = null;
                    }
                }
            }
    }

    private MaintenanceSnapshot Copy()
    {
        var phase = _phase switch
        {
            MaintenancePhase.Draining => "draining",
            MaintenancePhase.Drained => "drained",
            _ => "normal"
        };
        var activities = _items.Values
            .Select(i => new MaintenanceActivityView(i.Category, i.State, i.StartedAt))
            .ToList();
        var drained = _phase == MaintenancePhase.Drained && activities.Count == 0;
        return new MaintenanceSnapshot(
            phase,
            activities.Count,
            activities,
            _startedAt,
            drained,
            drained ? null : _timeoutReason,
            _lastError);
    }

    private void Note(string message)
    {
        try { Notice?.Invoke(message); }
        catch { /* 日志失败不改变维护状态 */ }
    }

    private static void ObserveFaults(Task task)
    {
        _ = task.ContinueWith(
            t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

public static class MaintenanceCommands
{
    /// <summary>
    /// 只看这次 TCP 连接的对端。<paramref name="forwardedFor"/> 不参与判断。
    /// 环回表示对端进程在本机，本机上的反向代理也会是环回。
    /// </summary>
    public static bool DirectLoopback(IPAddress? remoteAddress, string? forwardedFor)
    {
        _ = forwardedFor;
        return remoteAddress is { } ip && IPAddress.IsLoopback(ip);
    }

    public static bool Allows(bool bearerAuthenticated, bool fromLoopback, string? configuredToken, string? presentedToken)
    {
        if (!bearerAuthenticated || !fromLoopback) return false;
        if (string.IsNullOrEmpty(configuredToken) || string.IsNullOrEmpty(presentedToken)) return false;
        var a = Encoding.UTF8.GetBytes(configuredToken);
        var b = Encoding.UTF8.GetBytes(presentedToken);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    public static Task<MaintenanceSnapshot?> EnterAsync(MaintenanceGate gate, bool allowed, TimeSpan timeout)
    {
        if (!allowed) return Task.FromResult<MaintenanceSnapshot?>(null);
        return Await(gate.EnterAndWaitAsync(timeout));
    }

    public static MaintenanceSnapshot? Query(MaintenanceGate gate, bool allowed)
    {
        if (!allowed) return null;
        return gate.Snapshot();
    }

    public static MaintenanceSnapshot? Exit(MaintenanceGate gate, bool allowed)
    {
        if (!allowed) return null;
        return gate.Exit();
    }

    private static async Task<MaintenanceSnapshot?> Await(Task<MaintenanceSnapshot> inner) => await inner.ConfigureAwait(false);
}
