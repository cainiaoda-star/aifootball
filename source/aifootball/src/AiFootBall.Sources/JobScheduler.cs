using AiFootBall.Core.Options;
using AiFootBall.Data;
using NCrontab;

namespace AiFootBall.Sources;

/// <summary>
/// 内置 Cron 调度器（标准 5 段：分 时 日 月 周）。
/// 作业运行记录写入 dbo.SyncLog（TaskName = job:{name}），进程重启后按上次运行时间推算下次触发。
/// </summary>
public sealed class JobScheduler
{
    private readonly JobsOptions _options;
    private readonly CollectionService _collection;
    private readonly SyncLogRepository _sync;
    private readonly RecommendationService? _recommendation;
    private readonly PurchaseSettlementService? _purchaseSettle;
    private readonly LottoRecommendationService? _lottoRec;
    private readonly DailyFollowService? _dailyFollow;
    private readonly BdSpfPickService? _bdSpfPick;
    private readonly BdSpfPickRepository? _bdSpfRepo;
    private readonly StandingService? _standing;
    private readonly PaperTradeRepository? _paper;
    private readonly SteadyPickRepository? _steady;
    private readonly FinalPriceRepository? _finalPrice;
    private readonly BdHistoryRepository? _bdHistory;
    private readonly BettingCardSource? _cards;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly List<Entry> _entries = new();

    public JobScheduler(JobsOptions options, CollectionService collection, SyncLogRepository sync,
        RecommendationService? recommendation = null, PurchaseSettlementService? purchaseSettle = null,
        Action<string>? log = null, LottoRecommendationService? lottoRec = null,
        DailyFollowService? dailyFollow = null, BdSpfPickService? bdSpfPick = null,
        BdSpfPickRepository? bdSpfRepo = null, StandingService? standing = null,
        PaperTradeRepository? paper = null, SteadyPickRepository? steady = null,
        FinalPriceRepository? finalPrice = null, BdHistoryRepository? bdHistory = null,
        BettingCardSource? cards = null)
    {
        _options = options;
        _collection = collection;
        _sync = sync;
        _recommendation = recommendation;
        _purchaseSettle = purchaseSettle;
        _lottoRec = lottoRec;
        _dailyFollow = dailyFollow;
        _bdSpfPick = bdSpfPick;
        _bdSpfRepo = bdSpfRepo;
        _standing = standing;
        _paper = paper;
        _steady = steady;
        _finalPrice = finalPrice;
        _bdHistory = bdHistory;
        _cards = cards;
        _log = log;
    }

    private sealed class Entry
    {
        public ScheduledJob Job { get; init; } = new();
        public CrontabSchedule Schedule { get; set; } = null!;
        public DateTime Next { get; set; }
        public DateTime? LastRun { get; set; }
        public string? LastStatus { get; set; }
        public string? LastMessage { get; set; }
        public bool Running { get; set; }
        /// <summary>本次开始执行的时刻，用于观测卡死时长和看门狗判定。</summary>
        public DateTime? RunningSince { get; set; }
        /// <summary>
        /// 本次排队的取消源。看门狗判定卡死时取消它，让还堵在通道里的那一份真正退出——
        /// 否则「已强制释放（本次未真正执行）」是假话：任务仍在通道里等着，稍后照样执行，
        /// 而且 Next 已重算，下一轮还会再排一份，同一个作业在通道里叠两份。
        /// </summary>
        public CancellationTokenSource? Pending { get; set; }
    }

    /// <summary>
    /// 单次执行的超时上限（按作业类型）。主循环是串行的，一个作业挂在网络或数据库上不返回，
    /// 后面排队的作业就全部停在 Running=true 永远不被调度——2026-11-03 就是这样停摆了 65 分钟。
    /// </summary>
    private static readonly Dictionary<string, TimeSpan> JobTimeouts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["odds-sample"] = TimeSpan.FromMinutes(10),
        ["odds-sample-late"] = TimeSpan.FromMinutes(8),
        ["water-sample"] = TimeSpan.FromMinutes(10),
        ["jc-odds-sample"] = TimeSpan.FromMinutes(8),
        ["jc-odds-backfill"] = TimeSpan.FromMinutes(30),
        ["exchange"] = TimeSpan.FromMinutes(15),
        ["odds-upcoming"] = TimeSpan.FromMinutes(30),
        ["cards"] = TimeSpan.FromMinutes(30),
        ["football"] = TimeSpan.FromMinutes(60),
        ["odds-backfill"] = TimeSpan.FromMinutes(90),
        ["save-recommendations"] = TimeSpan.FromMinutes(30),
        ["refresh-near-kickoff"] = TimeSpan.FromMinutes(30),
        ["turn-guard"] = TimeSpan.FromMinutes(10),
        ["recommend-evidence"] = TimeSpan.FromMinutes(20),
        ["daily-review"] = TimeSpan.FromMinutes(20),
        ["daily-follow-build"] = TimeSpan.FromMinutes(20),
        ["daily-follow-settle"] = TimeSpan.FromMinutes(20),
        ["daily-follow-skill"] = TimeSpan.FromMinutes(10),
        ["settle-hourly"] = TimeSpan.FromMinutes(30),
        ["settle-purchases"] = TimeSpan.FromMinutes(20),
        ["settle-recommendations"] = TimeSpan.FromMinutes(30),
        ["paper-freeze"] = TimeSpan.FromMinutes(20),
        ["paper-settle"] = TimeSpan.FromMinutes(20),
        ["lotto"] = TimeSpan.FromMinutes(30),
        ["lotto-recommend"] = TimeSpan.FromMinutes(30),
        ["bd-spf-pick"] = TimeSpan.FromMinutes(30),
        // 2026-11-09 加：北单结算。只读写本库，正常 1 秒内完成，给 10 分钟纯属兜底。
        ["bd-spf-settle"] = TimeSpan.FromMinutes(10),
        // 2026-11-09 加：稳胆出榜要跑一遍竞彩卡片管线（和 save-recommendations 同源），给 30 分钟；
        // 结算只读写本库，10 分钟足够，多给纯属兜底。
        ["save-steady-picks"] = TimeSpan.FromMinutes(30),
        ["steady-settle"] = TimeSpan.FromMinutes(10),
        // 2026-11-10 加：终版价回填只读写本库（两条按索引 seek 的 UPDATE），秒级完成。
        ["finalize-prices"] = TimeSpan.FromMinutes(10),
        // 2026-11-10 加：北单开奖补采要打 500.com，每期一次请求 + 1.5s 间隔，一次最多 6 期。
        ["bd-history"] = TimeSpan.FromMinutes(20),
        ["rebuild-standings"] = TimeSpan.FromMinutes(30)
    };

    private static TimeSpan TimeoutFor(ScheduledJob job) =>
        JobTimeouts.TryGetValue(job.Type, out var t) ? t : TimeSpan.FromMinutes(20);

    /// <summary>jc-odds-backfill 每次回补最近几天（含漏跑的兜底）。写入幂等，重复补不会重复行。</summary>
    private const int BackfillJcDays = 3;

    /// <summary>超时保护失效时（代码阻塞在不可取消的调用上）的兜底阈值。</summary>
    private static TimeSpan HardLimitFor(ScheduledJob job) => TimeoutFor(job) + TimeSpan.FromMinutes(5);

    /// <summary>解析 cron 并载入历史运行时间。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _entries.Clear();
        var lastRuns = await _sync.GetLastRunsAsync(ct);

        foreach (var job in _options.Jobs)
        {
            CrontabSchedule schedule;
            try
            {
                schedule = CrontabSchedule.Parse(job.Cron,
                    new CrontabSchedule.ParseOptions { IncludingSeconds = false });
            }
            catch (Exception ex)
            {
                _log?.Invoke($"作业 {job.Name} 的 cron 表达式无效（{job.Cron}）：{ex.Message}");
                continue;
            }

            lastRuns.TryGetValue($"job:{job.Name}", out var last);
            // 从「现在」往后算下一次，而不是从上次运行时间往后算。
            //
            // 原来的写法是 GetNextOccurrence(上次运行)，错过的那一次会落在过去，
            // 于是被下面那行改写成「5 秒后立刻执行」——重启后所有欠账一起补跑。
            // 11-05 11:49 就是这么堵死的：odds-backfill（全量补缺，上限 90 分钟）
            // 在服务重启后立刻被拉起来，串行队列被它一个人占住，
            // jc-odds-sample 从 11:15 起 21 分钟一次都没跑成。
            //
            // 错过就不补，等下一个周期。日任务等明天，2 分钟任务最多等 2 分钟——
            // 补跑的价值远小于「把正在跑的采样挤掉」的代价。
            var next = schedule.GetNextOccurrence(DateTime.Now);
            if (next < DateTime.Now) next = DateTime.Now.AddSeconds(5);

            _entries.Add(new Entry
            {
                Job = job,
                Schedule = schedule,
                Next = next,
                LastRun = last?.FinishedAt,
                // 状态与消息同样从日志恢复：重启后监控面板不该失明
                LastStatus = last?.Status,
                LastMessage = last?.Message
            });
        }
    }

    /// <summary>作业状态快照（接口/面板用）。</summary>
    public IReadOnlyList<object> Describe() => _entries.Select(e => (object)new
    {
        name = e.Job.Name,
        cron = e.Job.Cron,
        type = e.Job.Type,
        enabled = e.Job.Enabled,
        hours = e.Job.Hours,
        recheckMinutes = e.Job.RecheckMinutes,
        nextRun = e.Next,
        lastRun = e.LastRun,
        lastStatus = e.LastStatus,
        lastMessage = e.LastMessage,
        running = e.Running,
        // 正在运行的作业已跑了多久，页面据此判断是正常采样还是卡死
        runningSeconds = e.Running && e.RunningSince.HasValue
            ? (int)Math.Round((DateTime.Now - e.RunningSince.Value).TotalSeconds)
            : (int?)null,
        timeoutMinutes = (int)TimeoutFor(e.Job).TotalMinutes
    }).ToList();

    /// <summary>主循环最近一次心跳的时刻（进程刚起、还没跑完第一轮时为 <see cref="DateTime.MinValue"/>）。</summary>
    private long _lastTickTicks;

    /// <summary>
    /// 调度器心跳。健康检查用它判断「调度循环是否还在转」——
    /// 进程活着但循环卡死时，端口照常监听、HTTP 照常 200，只有这个时间会停住。
    /// </summary>
    public DateTime LastTickAt
    {
        get
        {
            var t = Interlocked.Read(ref _lastTickTicks);
            return t == 0 ? DateTime.MinValue : new DateTime(t);
        }
    }

    /// <summary>
    /// 标记「调度器已经准备启动」。放在 InitializeAsync 之前调用。
    ///
    /// 为什么单独一个方法：InitializeAsync 要读库，DB 不通时它会抛；抛了以后
    /// SchedulerHostedService 只打一行日志就退出，主循环一次都不进，
    /// 心跳永远停在 MinValue —— 健康检查会把它当成「还没起来」继续报 ok。
    /// 先落一个起点，5 分钟没有后续 tick 就能被认出来。
    /// </summary>
    public void MarkStarting() => _lastTickTicks = DateTime.Now.Ticks;

    /// <summary>
    /// 主循环：每 20 秒检查一次到期作业。
    ///
    /// 除 <see cref="DetachedJobTypes"/> 里的短周期采样外，其余作业串行执行——
    /// 它们都要打同一个 500 接口，并发会被限流，得不偿失。
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        if (_entries.Count == 0)
        {
            _log?.Invoke("没有可运行的定时作业，请检查 appsettings.json 的 Jobs 配置");
            return;
        }

        _log?.Invoke($"调度器已启动（{_entries.Count} 个作业）：");
        foreach (var e in _entries)
            _log?.Invoke($"  {e.Job.Name,-18} {e.Job.Cron,-16} 下次 {e.Next:yyyy-MM-dd HH:mm}");

        // 脱离串行队列的任务句柄。放着不 await，只在每轮清理已完成的。
        var detached = new List<Task>();

        while (!ct.IsCancellationRequested)
        {
            // 心跳：主循环每 20 秒跑到这里一次。健康检查靠它区分「进程活着」和「调度器还活着」。
            //
            // 没有这个字段之前，进程僵在循环里（比如下面那句写 STUCK 日志用了
            // CancellationToken.None、DB 不响应就永远等下去）时 /api/health 仍然返回 200，
            // 看门狗只看 status code，于是天天报「一切正常」而作业一个都不跑。
            // 这个项目已经在「监控到了却不动」上栽过好几次，不能再留这种假绿灯。
            _lastTickTicks = DateTime.Now.Ticks;

            List<Entry> due;
            var reclaimed = new List<string>();
            var stuck = new List<(string Name, string Type, TimeSpan Elapsed)>();
            lock (_gate)
            {
                // 看门狗：Running=true 但早该结束（执行超时后仍未复位）的作业强制释放，
                // 否则串行队列会被一个挂死的任务永久拖住。
                var now = DateTime.Now;
                foreach (var entry in _entries)
                {
                    if (!entry.Running || entry.RunningSince is null) continue;
                    var limit = HardLimitFor(entry.Job);
                    if (now - entry.RunningSince.Value <= limit) continue;

                    var elapsed = now - entry.RunningSince.Value;
                    entry.Running = false;
                    entry.RunningSince = null;
                    // 真正取消还堵在通道里的那一份，否则下面那句「本次未真正执行」是假的
                    try { entry.Pending?.Cancel(); } catch (ObjectDisposedException) { }
                    entry.LastStatus = "STUCK";
                    entry.LastMessage = $"执行超过 {limit.TotalMinutes:0} 分钟未返回，已强制释放运行标志";
                    entry.Next = entry.Schedule.GetNextOccurrence(now);
                    reclaimed.Add($"{entry.Job.Name}（已卡 {elapsed.TotalMinutes:0} 分钟）");
                    // 被强制释放的作业连 RunGuardedAsync 都没进过，SyncLog 里不会留下任何痕迹。
                    // 2026-11-08 的复盘就是这么消失的：日志一片正常，页面上只有一句「还没写」。
                    stuck.Add((entry.Job.Name, entry.Job.Type, elapsed));
                }

                due = _entries.Where(x => x.Job.Enabled && !x.Running && DateTime.Now >= x.Next).ToList();
                foreach (var entry in due)
                {
                    entry.Next = entry.Schedule.GetNextOccurrence(DateTime.Now);
                    entry.Running = true;
                    entry.RunningSince = DateTime.Now;
                    // 每次排队一个新取消源；上一份若还没被回收（正常不会），先弃掉
                    try { entry.Pending?.Cancel(); } catch (ObjectDisposedException) { }
                    entry.Pending = new CancellationTokenSource();
                }
            }

            foreach (var name in reclaimed)
                _log?.Invoke($"[看门狗] 作业 {name} 长时间未返回，已强制释放");

            // 强制释放的作业落一条 STUCK，页面的作业日志里才查得到「这次到底跑没跑」。
            //
            // 注意这里是在主循环里 await 的：写日志本身必须带上限，不能像以前那样用
            // CancellationToken.None —— DB 一旦不响应，整条主循环就永久停在这一句上，
            // 所有作业一起停摆，而端口和 /api/health 照常正常。
            foreach (var (name, type, elapsed) in stuck)
            {
                try
                {
                    using var logCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await _sync.WriteAsync($"job:{name}", type, "STUCK", 0,
                        $"排队/执行卡住 {elapsed.TotalMinutes:0} 分钟未完成，已强制释放（本次未真正执行）",
                        logCts.Token);
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"[{name}] STUCK 记录写入失败：{ex.Message}");
                }
            }

            foreach (var entry in due)
            {
                // 全部丢进通道，主循环一次都不 await。
                //
                // 原来的写法是 foreach 里 await ExecuteAsync：一个任务跑多久，整个调度器就停多久。
                // water-sample 每 2 分钟跑一次、每次 110 秒（赔率 + 必发各一轮），
                // 主循环 90% 的时间都卡在它身上，tick 形同虚设——
                // 11-05 11:56 采完之后 11:58 那一轮就是这么丢的。
                //
                // 通道（lane）：同源作业串行，不同源并行。
                // 500 系列的作业共用一个通道，避免互相限流；竞彩官方接口单独一个通道。
                detached.Add(EnqueueAsync(entry, ct));
            }

            // 已完成的脱离任务清出列表，避免无限增长
            detached.RemoveAll(t => t.IsCompleted);

            // 日更复盘漏跑自查（2026-11-08 加）：daily-review 一天只跑一次，08:20 那一次
            // 若因重启错过 tick（InitializeAsync 是「错过不补」），或被 500 通道里连续超时
            // 的采集任务顶到看不见的地方，当天就永久没有复盘——2026-11-08 早上就是这样，
            // 11-07 的复盘到 11:00 仍是空的，页面只会说一句「还没写」，不会自己补。
            // 这里用文件存在性每 5 分钟自查一次，缺了就重新排进队列。
            if (DateTime.Now - _reviewSweepAt > TimeSpan.FromMinutes(5))
            {
                _reviewSweepAt = DateTime.Now;
                ScheduleMissingReview();
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // 进程要退了，给脱离的任务一点时间收尾（否则采样会在写库中途被掐断）
        if (detached.Count > 0)
        {
            try { await Task.WhenAll(detached).WaitAsync(TimeSpan.FromSeconds(15)); }
            catch { /* 收尾超时就算了，下次启动会重新采 */ }
        }
    }

    /// <summary>上一次做「复盘漏跑」自查的时刻，节流到 5 分钟一次。</summary>
    private DateTime _reviewSweepAt = DateTime.MinValue;

    /// <summary>已经补排过哪一天的复盘。一天最多补两次——写失败不无限重试，留给人工。</summary>
    private DateTime? _reviewPatchedFor;

    /// <summary>当天已经补排过几次。</summary>
    private int _reviewPatchCount;

    /// <summary>
    /// 该有的复盘文件不存在，就把 daily-review 重新排进队列。
    ///
    /// 只在「今天的计划时刻已过」之后动手：否则会在 08:20 之前写出一篇昨天的残缺复盘，
    /// 而且写完文件就存在了，08:20 的正常那一次反而被当成「已跑过」。
    /// 目标日固定是「昨天」，与 handler 的取日口径保持一致。
    /// </summary>
    private void ScheduleMissingReview()
    {
        var entry = _entries.FirstOrDefault(e => e.Job.Name == "daily-review");
        if (entry is null || !entry.Job.Enabled) return;

        var todayPlan = entry.Schedule.GetNextOccurrence(DateTime.Today);
        if (DateTime.Now < todayPlan) return;

        var target = DateTime.Today.AddDays(-1);
        if (_reviewPatchedFor == target && _reviewPatchCount >= 2) return;
        if (DailyReview.Exists(target)) return;

        lock (_gate)
        {
            if (entry.Running) return;
            entry.Next = DateTime.Now.AddSeconds(60);
        }
        if (_reviewPatchedFor != target) { _reviewPatchedFor = target; _reviewPatchCount = 0; }
        _reviewPatchCount++;
        _log?.Invoke($"[补跑] {target:yyyy-MM-dd} 的复盘没有落盘，已排进队列（60 秒后执行，今天第 {_reviewPatchCount} 次）");
    }

    /// <summary>500 系列（欧赔 / 必发 / 补数据 / 结算）共用一个通道，串行执行。</summary>
    private readonly SemaphoreSlim _mainLane = new(1, 1);
    /// <summary>竞彩官方接口单独一个通道。它和 500 不同源，不该排在必发后面等。</summary>
    private readonly SemaphoreSlim _jcLane = new(1, 1);

    /// <summary>
    /// 推荐生成专用通道（save-recommendations 及终版 save-final-weekday/weekend 都走这里）。
    ///
    /// 为什么单独：终版推荐是「卡用户截止时间」的关键任务（工作日 21:50、周末 22:50），
    /// 而它原来和 water-sample（每轮 ~110 秒）、exchange（1~2 分钟）挤在同一个主通道里串行。
    /// 21:50 到点的终版会被前面还没跑完的采集任务顶到 21:54+ 才执行，晚于用户截止时间落库。
    /// 推荐生成只读模型结果 + 落库，不抢 500/竞彩接口，放独立通道安全且能保证准点。
    /// </summary>
    private readonly SemaphoreSlim _recommendLane = new(1, 1);

    /// <summary>
    /// 把作业丢进对应通道执行。主循环不等待，只保留句柄用于退出时收尾。
    ///
    /// 通道内部是串行的：同一个通道里前一个没跑完，后一个就等着。
    /// 通道之间是并行的：竞彩采样不会因为必发在跑就推迟。
    /// </summary>
    private async Task EnqueueAsync(Entry entry, CancellationToken ct)
    {
        var pending = entry.Pending;
        var lane = RecommendJobTypes.Contains(entry.Job.Type) ? _recommendLane
            : DetachedJobTypes.Contains(entry.Job.Type) ? _jcLane : _mainLane;
        using var linked = pending is null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : CancellationTokenSource.CreateLinkedTokenSource(ct, pending.Token);
        try
        {
            await lane.WaitAsync(linked.Token);
            try
            {
                await RunGuardedAsync(entry, linked.Token);
            }
            finally
            {
                lane.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // 进程退出，或看门狗已判定卡死并取消了这一份——排队中的作业直接丢弃，下轮会重新算。
            // 后者以前不生效：取消源没接进来，被「强制释放」的任务其实还在通道里等着，稍后照样跑。
        }
        finally
        {
            entry.Running = false;
            entry.RunningSince = null;
            if (ReferenceEquals(entry.Pending, pending))
            {
                pending?.Dispose();
                entry.Pending = null;
            }
        }
    }

    /// <summary>
    /// 单个作业的执行包装：超时、状态回填、异常兜底。
    /// finally 里的 Running 复位是硬要求——漏一次，这个作业就永远触发不了。
    /// </summary>
    private async Task RunGuardedAsync(Entry entry, CancellationToken ct)
    {
        var job = entry.Job;
        var limit = TimeoutFor(job);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        runCts.CancelAfter(limit);
        try
        {
            await ExecuteAsync(entry, runCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 进程退出，不算失败
        }
        catch (OperationCanceledException) when (runCts.IsCancellationRequested)
        {
            entry.LastStatus = "TIMEOUT";
            entry.LastMessage = $"执行超过 {limit.TotalMinutes:0} 分钟未完成，已中止";
            _log?.Invoke($"[{job.Name}] 执行超时（>{limit.TotalMinutes:0} 分钟），已中止");
            await LogTimeoutAsync(entry, limit);
        }
        catch (Exception ex)
        {
            entry.LastStatus = "FAILED";
            entry.LastMessage = ex.Message;
            _log?.Invoke($"[{job.Name}] 执行异常：{ex.Message}");
        }
        finally
        {
            entry.Running = false;
            entry.RunningSince = null;
        }
    }

    /// <summary>
    /// 使用独立通道的作业类型。
    ///
    /// 只收「周期 ≤5 分钟、只打一个源、单轮 <1 分钟」的采样任务。放进来之前先问两件事：
    /// 它和主通道抢不抢同一个接口？它卡住了会不会把别人的数据搞坏？
    /// water-sample / exchange 都打 500 且要跑 1~2 分钟，留在主通道里。
    /// </summary>
    private static readonly HashSet<string> DetachedJobTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "jc-odds-sample"
    };

    /// <summary>
    /// 纯计算类作业：只读库、不打外部接口，走推荐专用通道，不被采集任务阻塞。
    ///
    /// daily-review 必须在这里。它以前和 500 采集挤同一个主通道，而 exchange / odds-sample
    /// 的超时是 8~15 分钟一条，连续几次超时就能把主通道占一小时以上。
    /// 2026-11-08 实测：08:20 的复盘进了队列，一直等到超过 25 分钟硬上限被看门狗释放，
    /// 从头到尾没执行过一次（SyncLog 里连 FAIL 都没有，只有页面上一句「还没写」）。
    /// 复盘只读 FbMatch / 赔率历史 + 写一个 md 文件，和采集不抢接口，没有任何理由排队。
    /// </summary>
    private static readonly HashSet<string> RecommendJobTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "save-recommendations",
        "daily-review",
        // 2026-11-08 加：这两个都会**重写当天的榜**（DELETE 未结算行 + 重新 INSERT）。
        // turn-guard 现在会在出现新高危告警时立刻重算，和 8:40/11:40/16:40/20:40 的
        // save-recommendations 撞车时，两条 DELETE+INSERT 交错就会把榜写成两份。
        // 放进同一个串行通道是这里唯一可靠的互斥手段（库上没有唯一约束兜底）。
        "turn-guard",
        "refresh-near-kickoff",
        // 2026-11-09 加：两个结算作业只读库判中、不打任何外部接口，却一直和 water-sample
        // （每轮 110 秒）挤在主通道串行。11-09 早上的实测后果：paper-settle 排队 25 分钟才轮到，
        // 看门狗把「排队」判成 STUCK，页面上一片红；更麻烦的是结算被拖到深夜之后，
        // 而未结算的注正是重算会碰的那批行（见 MilkPickRepository.SaveDayAsync 的注释）。
        // 结算链是「赛果 → 判中 → 台账」的最后一环，必须和出票同优先级，不能排在采集后面。
        "paper-settle",
        "settle-recommendations",
        // 2026-11-09 再加：今天统计出来的漏网之鱼，全是「只读写本库、不打任何外部接口」，
        // 却因为排在 water-sample（每轮 110 秒）/ odds-sample-late（8 分钟超时）后面被饿死。
        // 11-09 00:00~08:00 实测被看门狗强制释放的次数：paper-freeze 11、settle-hourly 6、
        // settle-purchases 6、daily-follow-settle 9 —— 全都是「排队」而不是「执行慢」。
        //
        // paper-freeze 必须在这一条：它读 FbMilkPick、写 FbPaperTrade，而 save-recommendations
        // 正是对 FbMilkPick 做 DELETE + INSERT 的那个作业。只有同一条串行通道才能保证它读到
        // 完整的榜面；排在采集后面既会读不到（被重算撞上），也会漏冻（11-08 丢 3 注的成因之一）。
        "settle-hourly",
        "settle-purchases",
        "paper-freeze",
        // 跟单链同样是「读榜面 → 落库」，不能排在采集后面。
        "daily-follow-build",
        "daily-follow-settle",
        // 2026-11-09 加：北单结算只读 FbMatch / 写 FbBdSpfPick，不打任何外部接口，
        // 而且是「赛果 → 判中 → 台账」的最后一环，没理由排在 water-sample 后面。
        // ★ 它和 bd-spf-pick 不是互斥关系：bd-spf-pick 只 DELETE 当日**未结算**行
        //   （Hit IS NULL AND SettledAt IS NULL），已经结算的行它碰不到，所以进不进同一条通道
        //   都不会互相写坏；进这条通道纯粹是为了不被采集饿死。
        "bd-spf-settle",
        // 2026-11-09 加：稳胆。save-steady-picks 要读当日场次 + 胜平负快照，steady-settle 只读写本库；
        // 两者和主榜的 save-recommendations 一样是 DELETE+INSERT 形态，**必须串行**，
        // 否则同一天两次重算交错会把「赔率最低的三场」写成两批（库上同样没有唯一约束兜底）。
        "save-steady-picks",
        "steady-settle",
        // 2026-11-10 加：终版价回填写的就是这三张台账（稳胆 / 空跑 / 北单）。
        // 它只 UPDATE FinalOdds 列、不删行，但仍然必须和 DELETE + INSERT 的两个出榜作业串行，
        // 否则「先插行、再被对方的 DELETE 删掉、最后回填落空」这一串完全无声。
        "finalize-prices"
    };

    /// <summary>超时中止后补一条运行记录，方便页面上查。写日志本身失败不再抛出。</summary>
    private async Task LogTimeoutAsync(Entry entry, TimeSpan limit)
    {
        try
        {
            // 同 STUCK 那条：写日志必须有上限。这里虽然不在主循环里，但它是持着通道跑的，
            // DB 不响应就会把这个通道永久占住，后面排队的作业全部陪葬。
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _sync.WriteAsync($"job:{entry.Job.Name}", entry.Job.Type, "TIMEOUT", 0,
                $"执行超过 {limit.TotalMinutes:0} 分钟未完成，已中止", cts.Token);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[{entry.Job.Name}] 超时记录写入失败：{ex.Message}");
        }
    }

    /// <summary>按页面上保存的时间改期。下次触发从现在起按新 cron 计算。</summary>
    public (bool Ok, string Message) Update(string name, string cron, bool enabled, int? hours, int? recheckMinutes)
    {
        if (string.IsNullOrWhiteSpace(cron)) return (false, "没有填写时间");
        CrontabSchedule schedule;
        try
        {
            schedule = CrontabSchedule.Parse(cron.Trim(),
                new CrontabSchedule.ParseOptions { IncludingSeconds = false });
        }
        catch (Exception ex)
        {
            return (false, $"时间格式无效：{ex.Message}");
        }

        lock (_gate)
        {
            var entry = _entries.FirstOrDefault(e => e.Job.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return (false, $"未找到作业 {name}");
            entry.Job.Cron = cron.Trim();
            entry.Job.Enabled = enabled;
            if (hours is >= 0) entry.Job.Hours = hours.Value;
            if (recheckMinutes is >= 1) entry.Job.RecheckMinutes = recheckMinutes.Value;
            entry.Schedule = schedule;
            entry.Next = enabled ? schedule.GetNextOccurrence(DateTime.Now) : DateTime.MaxValue;
            _log?.Invoke($"作业 {entry.Job.Name} 已改期：{entry.Job.Cron}，下次 {(enabled ? entry.Next.ToString("yyyy-MM-dd HH:mm") : "已停用")}");
            return (true, enabled ? $"下次 {entry.Next:yyyy-MM-dd HH:mm}" : "已停用");
        }
    }

    /// <summary>手动触发一次（不影响既定计划）。</summary>
    public async Task<(bool Ok, string Message)> RunOnceAsync(string name, CancellationToken ct = default)
    {
        var entry = _entries.FirstOrDefault(e => e.Job.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return (false, $"未找到作业 {name}");
        if (entry.Running) return (false, $"作业 {name} 正在运行");

        entry.Running = true;
        entry.RunningSince = DateTime.Now;
        var limit = TimeoutFor(entry.Job);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        runCts.CancelAfter(limit);
        try
        {
            var result = await ExecuteAsync(entry, runCts.Token);
            return (result.Success, result.Message);
        }
        catch (OperationCanceledException) when (runCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            entry.LastStatus = "TIMEOUT";
            entry.LastMessage = $"手动触发超过 {limit.TotalMinutes:0} 分钟未完成，已中止";
            await LogTimeoutAsync(entry, limit);
            return (false, entry.LastMessage);
        }
        finally
        {
            entry.Running = false;
            entry.RunningSince = null;
        }
    }

    private async Task<CollectionResult> ExecuteAsync(Entry entry, CancellationToken ct)
    {
        var job = entry.Job;
        _log?.Invoke($"[{DateTime.Now:HH:mm:ss}] 开始执行作业 {job.Name}（{job.Type}）");

        CollectionResult result;
        switch (job.Type.ToLowerInvariant())
        {
            case "lotto":
            {
                result = await _collection.CollectLotteryAsync(job.LotteryCode, true, null, ct);
                // 开奖入库后立刻生成下一期选号方案
                if (_lottoRec is not null)
                {
                    try
                    {
                        var plans = await _lottoRec.GenerateAllAndSaveAsync(ct);
                        var n = plans.Sum(p => p.Picks.Count);
                        result = CollectionResult.Ok(result.Rows + n,
                            $"{result.Message}；数字彩选号 {n} 注");
                    }
                    catch (Exception ex)
                    {
                        _log?.Invoke($"数字彩选号生成失败：{ex.Message}");
                    }
                }
                break;
            }

            case "lotto-recommend":
            {
                if (_lottoRec is null)
                {
                    result = CollectionResult.Fail("未配置数字彩选号服务");
                    break;
                }
                var plans = await _lottoRec.GenerateAllAndSaveAsync(ct);
                result = CollectionResult.Ok(plans.Sum(p => p.Picks.Count),
                    string.Join("；", plans.Select(p => $"{p.LotteryName} {p.Picks.Count}注")));
                break;
            }

            case "football":
            {
                var days = job.Days <= 0 ? 3 : job.Days;
                var today = DateOnly.FromDateTime(DateTime.Today);
                var dates = Enumerable.Range(0, days).Select(i => today.AddDays(-i)).ToList();
                // 同时抓未来 3 天赛程：赛前赔率/必发的时间序列采样依赖这些场次记录
                dates.AddRange(Enumerable.Range(1, 3).Select(i => today.AddDays(i)));
                result = await _collection.CollectFootballAsync(dates, null, ct);
                break;
            }

            case "odds-upcoming":
            {
                var hours = job.Hours <= 0 ? 24 : job.Hours;
                result = await _collection.CollectOddsAsync(
                    DateTime.Now.AddHours(-2), DateTime.Now.AddHours(hours), false, job.Limit, null, null, ct);
                break;
            }

            // 赔率轨迹采样：落库同时写入 FbOddsHistory，形成「初盘 → 赛前 → 开赛」曲线
            case "odds-sample":
            {
                // 默认 72h：覆盖竞彩周五售周六/日；在售受注场另有强制纳入，不单靠窗口
                var hours = job.Hours <= 0 ? 72 : job.Hours;
                result = await _collection.SampleOddsAsync(hours, job.HoursBehind, job.RecheckMinutes,
                    job.Limit, null, ct);
                break;
            }

            // 临场加密采样：只盯住 90 分钟内即将开赛的场次
            case "odds-sample-late":
                result = await _collection.SampleOddsAsync(1.5, Math.Max(0.5, job.HoursBehind),
                    job.RecheckMinutes <= 0 ? 8 : job.RecheckMinutes, job.Limit, null, ct);
                break;

            // 必发成交数据（成交量 / 庄家盈亏 / 大额明细）
            case "exchange":
            {
                var hours = job.Hours <= 0 ? 72 : job.Hours;
                result = await _collection.CollectExchangeAsync(hours, job.HoursBehind, job.RecheckMinutes,
                    job.Limit, null, ct);
                break;
            }

            // 水位高频采样：胜平负三线欧赔 + 必发成交量 同步采样（默认 1 分钟一次），
            // 分别写入 FbOddsHistory 与 FbExchange，形成「赔率水位 + 资金流」随时间变化的过程记录。
            case "water-sample":
            {
                var hours = job.Hours <= 0 ? 8 : job.Hours;
                var recheck = job.RecheckMinutes <= 0 ? 1 : job.RecheckMinutes;
                var resOdds = await _collection.SampleOddsAsync(hours, job.HoursBehind, recheck,
                    job.Limit, null, ct);
                var resEx = await _collection.CollectExchangeAsync(hours, job.HoursBehind, recheck,
                    job.Limit, null, ct);
                result = CollectionResult.Ok(resOdds.Rows + resEx.Rows,
                    $"水位采样：赔率 {resOdds.Rows} 点 / 必发 {resEx.Rows} 场（间隔 {recheck} 分钟）");
                break;
            }

            case "odds-backfill":
                result = await _collection.CollectOddsAsync(
                    new DateTime(2005, 1, 1), DateTime.Now.AddHours(2), true, job.Limit, null, null, ct);
                break;

            // 竞彩固定奖金四类玩法采样（让球胜平负/胜平负/比分/总进球/半全场），
            // 写入 FbJcOddsHistory 形成时间序列，供前端展示即时赔率涨跌。
            case "jc-odds-sample":
            {
                if (_recommendation is null)
                {
                    result = CollectionResult.Fail("推荐服务未注入");
                    break;
                }
                result = await _recommendation.SampleJcOddsAsync(ct);
                break;
            }

            // 竞彩四类玩法「最终固定奖金」回填（昨日已完场）。
            // 采样是即时价，页面上看到的会随盘口浮动；大奶复盘对账要按出票那一刻之后的截止价。
            // 补最近 3 天而不是只补昨天：进程重启、网络故障、500 抽风任一天漏跑，
            // 第二天还能兜回来；写入按 (MatchNo, CapturedAt) 先删后插，重复跑不会产生重复行。
            case "jc-odds-backfill":
            {
                if (_recommendation is null)
                {
                    result = CollectionResult.Fail("推荐服务未注入");
                    break;
                }
                var total = 0;
                var parts = new List<string>();
                for (var back = 1; back <= BackfillJcDays; back++)
                {
                    var day = DateOnly.FromDateTime(DateTime.Today.AddDays(-back));
                    var r = await _recommendation.BackfillJcDayAsync(day, ct);
                    total += r.Rows;
                    parts.Add(r.Message);
                    if (back < BackfillJcDays) await Task.Delay(1500, ct);
                }
                result = CollectionResult.Ok(total, string.Join("；", parts));
                break;
            }

            case "cards":
            {
                if (_recommendation is null)
                {
                    result = CollectionResult.Fail("推荐服务未注入");
                    break;
                }
                var (jc, bd, sfc) = await _recommendation.CollectCardsAsync(ct);
                result = CollectionResult.Ok(jc + bd + sfc, $"受注场次：竞彩 {jc} / 北单 {bd} / 胜负彩 {sfc}");
                break;
            }

            // 页面和数据库各存一份，并画成图片。按分钟建目录，不覆盖。
            case "recommend-evidence":
            {
                if (_recommendation is null)
                {
                    result = CollectionResult.Fail("推荐服务未注入");
                    break;
                }
                var dir = await _recommendation.CaptureEvidenceAsync(ct);
                result = CollectionResult.Ok(1, $"推荐证据已写入 {dir}");
                break;
            }

            // 每天早上写前一天的复盘。竞彩和胜负彩全部，北单 A/B/C，没进精选的也写。
            case "daily-review":            {
                if (_recommendation is null)
                {
                    result = CollectionResult.Fail("推荐服务未注入");
                    break;
                }
                // 复盘固定写「前一天」。原来是 now.Hour<12 ? 昨天 : 今天，
                // 一旦 08:20 那次被顶到 12 点后（补跑、队列拥塞），就会去写「今天」——
                // 而今天的比赛还没踢完，写出来是空壳，真正缺的「昨天」永远补不上。
                // 2026-11-08 实测踩到：11-07 的复盘一直空着，就是因为没人补「昨天」。
                var day = DateTime.Today.AddDays(-1);
                var path = await _recommendation.WriteDailyReviewAsync(day, ct);
                result = CollectionResult.Ok(1, $"复盘已写入 {path}");
                break;
            }

            // 联赛积分榜回算（2026-11-08 加）：每场赛前排名 + 当日积分榜快照。
            // 数据只能从历史比分自己算——第三方榜只给"当天"，拿不到"当时"；回测要的正是赛前那一眼。
            // 全量重算约 30 秒，以 2021-01-01 为起点（更短的窗口会把赛季起点切错）。
            case "rebuild-standings":
            {
                if (_standing is null)
                {
                    result = CollectionResult.Fail("积分榜服务未注入");
                    break;
                }
                var r = await _standing.RebuildAsync(new DateTime(2021, 1, 1), ct);
                result = CollectionResult.Ok(1, r.ToString());
                break;
            }

            // 每日逐场跟进（2026-11-06 起新口径）：竞彩每场一注 + 抓取时间 + 水位必发变化标注。
            case "daily-follow-build":
            {
                if (_dailyFollow is null)
                {
                    result = CollectionResult.Fail("逐场跟进服务未注入");
                    break;
                }
                result = await _dailyFollow.BuildDailyAsync(DateTime.Today, ct);
                break;
            }

            // 逐场跟进赛后复盘：抓赛果，每场回填命中/错误原因。
            case "daily-follow-settle":
            {
                if (_dailyFollow is null)
                {
                    result = CollectionResult.Fail("逐场跟进服务未注入");
                    break;
                }
                // 竞彩受注日跨零点：周六 22:00 之后开球的场次（如周六022-029，01:00-05:00 开球）
                // 记在「次日」的跟随榜里，白天场才记在当天。旧逻辑 12 点前只结算昨天，
                // 结果凌晨那批要拖到中午 12:00 才结（2026-11-08 00:10 实测发现，正好撞上今晚这 6 场）。
                // 结算本身幂等——只处理 SettledAt IS NULL 的行，多扫一天没有副作用。
                var now = DateTime.Now;
                var primary = now.Hour < 12 ? now.Date.AddDays(-1) : now.Date;
                var secondary = primary == now.Date ? now.Date.AddDays(-1) : now.Date;
                var first = await _dailyFollow.SettleDailyAsync(primary, ct);
                var second = await _dailyFollow.SettleDailyAsync(secondary, ct);
                result = CollectionResult.Ok(first.Rows + second.Rows, $"{first.Message}；{second.Message}");
                break;
            }

            // 每日技能总结：从当日复盘提炼一条知识技能。
            case "daily-follow-skill":
            {
                if (_dailyFollow is null)
                {
                    result = CollectionResult.Fail("逐场跟进服务未注入");
                    break;
                }
                var now = DateTime.Now;
                var day = now.Hour < 12 ? now.Date.AddDays(-1) : now.Date;
                result = await _dailyFollow.SummarizeSkillAsync(day, ct);
                break;
            }

            // 临场重算：只挑「马上开赛、但最后一版数据还早于赛前 60 分钟」的场次。
            // 四个固定保存时点之外，赛前盘的变化必须进来，否则「终盘规则」用的是早盘价。
            case "refresh-near-kickoff":
            {
                if (_recommendation is null)
                {
                    result = CollectionResult.Fail("推荐服务未注入");
                    break;
                }
                var (refreshed, stale, saved) = await _recommendation.RefreshNearKickoffAsync(ct);
                result = refreshed
                    ? CollectionResult.Ok(saved, $"临场重算：{stale} 场临近开赛但数据早于赛前 60 分钟，已重算并保存 {saved} 场")
                    : CollectionResult.Ok(0, "临场重算：没有需要重算的场次");
                break;
            }

            // 临场转向哨兵：开赛前扫当天已发出的未开赛大奶注，市场方向漂走就报警。
            // 这是「市场在偷偷转向、系统却没喊」的直接补丁（2026-11-05 加）。
            case "turn-guard":
            {
                if (_recommendation is null)
                {
                    result = CollectionResult.Fail("推荐服务未注入");
                    break;
                }
                var guard = await _recommendation.RunTurnGuardAsync(ct);
                // 文案必须说「榜面变了什么」，不能只说「喊了几声」——
                // 2026-11-08 用户的原话是「你监控到了，为啥不去改首页上的推荐结果」。
                var msg = guard.Alerts == 0
                    ? "临场转向哨兵：本轮无市场转向告警"
                    : $"临场转向哨兵：{guard.Alerts} 注告警（高危 {guard.High}），已回写榜面红标 {guard.Marked} 注、撤注 {guard.Voided} 注"
                      + (guard.Recalculated ? "；出现新高危，已立即重算当日榜" : "");
                result = CollectionResult.Ok(guard.Alerts, msg);
                break;
            }

            // 北单胜平负（不让球）推荐：套技能选 10 注落库，供「今日北单推荐」板块。
            case "bd-spf-pick":
            {
                if (_bdSpfPick is null || _bdSpfRepo is null)
                {
                    result = CollectionResult.Fail("北单推荐服务未注入");
                    break;
                }
                var day = await _bdSpfPick.BuildAsync(ct);
                if (day is null)
                {
                    result = CollectionResult.Ok(0, "北单推荐：今天没有可选的场次或模型未就绪");
                    break;
                }
                await _bdSpfRepo.EnsureTableAsync(ct);
                var rows = await _bdSpfRepo.SaveDayAsync(DateTime.Today, day, ct);
                result = CollectionResult.Ok(rows,
                    $"北单推荐：确定性正路 {day.Steady.Count} 注 + 反向价值 {day.Reverse.Count} 注，已落库");
                break;
            }

            // 北单结算（2026-11-09 加）：按「比分 + 北单让球数」判让胜/让平/让负，回写 Hit。
            // 建这条之前这张表从来没结算过（46 行 Hit 全 NULL），「北单成功率」只能靠临时 SQL 回算。
            case "bd-spf-settle":
            {
                if (_bdSpfRepo is null)
                {
                    result = CollectionResult.Fail("北单台账未注入");
                    break;
                }
                var settled = await _bdSpfRepo.SettleAsync(7, ct);
                var rec = await _bdSpfRepo.GetRecordAsync(30, ct);
                var t = rec.Total;
                result = CollectionResult.Ok(settled, settled == 0
                    ? $"北单结算：没有新的可判注单（近 30 天已结算 {t.Settled} 注、待结算 {t.Pending} 注）"
                    : $"北单结算：补判 {settled} 注；近 30 天累计 {t.Settled} 注 {t.Hits} 中"
                      + $"（{t.HitPct:0.0}%），回报 {t.Roi:0.000}（推荐时点 SP，非实购价）");
                break;
            }

            // 稳胆榜（2026-11-09 加，用户拍板）：取市场最看好档、每日最多 3 注。
            // 与主榜分开落库（FbSteadyPick），互不覆盖——两边命中率差 5 倍，混在一起统计谁也不是。
            case "save-steady-picks":
            {
                if (_recommendation is null || _steady is null)
                {
                    result = CollectionResult.Fail("稳胆依赖未注入");
                    break;
                }
                var steady = await _recommendation.BuildSteadyAsync(null, ct);
                await _steady.EnsureTableAsync(ct);
                // 一张都没出时不要执行 DELETE：否则一次「快照还没到」的早跑会把当天已发的注清空。
                // 稳胆的规则是「赔率最低的三场」，早跑和晚跑可能完全不同，清空代价很大。
                var saved = steady.Picks.Count == 0
                    ? 0
                    : await _steady.SaveDayAsync(DateTime.Today, steady.Picks, ct);
                var st = await _steady.GetStatsAsync(30, ct);
                result = CollectionResult.Ok(saved, steady.Picks.Count == 0
                    ? $"稳胆：今日无符合条件场次（候选 {steady.Candidates}、名单外 {steady.Excluded}、"
                      + $"超赔率上限 {steady.OverOdds}）；近 30 天 {st.Settled} 注 {st.Hits} 中（{st.HitPct:0.0}%）"
                    : $"稳胆：{steady.Picks.Count} 注已落库（候选 {steady.Candidates}，"
                      + $"被每日上限截掉 {steady.CutByRank}）；近 30 天 {st.Settled} 注 {st.Hits} 中"
                      + $"（{st.HitPct:0.0}%），回报 {st.Roi:0.000}");
                break;
            }

            // 稳胆结算（按比分判竞彩胜平负，不让球）
            case "steady-settle":
            {
                if (_steady is null)
                {
                    result = CollectionResult.Fail("稳胆台账未注入");
                    break;
                }
                await _steady.EnsureTableAsync(ct);
                var settled2 = await _steady.SettleAsync(7, ct);
                var st2 = await _steady.GetStatsAsync(30, ct);
                result = CollectionResult.Ok(settled2, settled2 == 0
                    ? $"稳胆结算：没有新的可判注单（近 30 天已结算 {st2.Settled} 注、待结算 {st2.Pending} 注）"
                    : $"稳胆结算：补判 {settled2} 注；近 30 天累计 {st2.Settled} 注 {st2.Hits} 中"
                      + $"（{st2.HitPct:0.0}%），回报 {st2.Roi:0.000}");
                break;
            }

            // 终版价回填（2026-11-10 加，用户拍板做「B：终版价口径」）。
            //
            // 三张台账存的都是「推荐生成那一刻」的价，而用户真正买到的/拿到的不是那个价：
            // 北单实测差 10~46%（阿尔杰什 2.91→4.25），稳胆赔率只有 1.0~1.75，差 3% 就能翻转结论。
            // 这条作业把「停售前最后可得价」补进 FinalOdds，战绩从此能分两栏报。
            //
            // 只写 FinalOdds IS NULL 的行，所以高频跑安全；已回填的价永不被覆盖。
            // 进 _recommendLane：save-recommendations / save-steady-picks 都是 DELETE + INSERT，
            // 同一张表的写入串起来才不会被对方的删除撞掉。
            case "finalize-prices":
            {
                if (_finalPrice is null)
                {
                    result = CollectionResult.Fail("终版价仓储未注入");
                    break;
                }
                var (s1, p1, b1) = await _finalPrice.FinalizeAsync(60, ct);
                var total = s1 + p1 + b1;
                result = CollectionResult.Ok(total, total == 0
                    ? "终版价回填：没有新的可回填注单（已开赛的注都已有终版价，或采样未覆盖）"
                    : $"终版价回填：稳胆 {s1} 注、空跑 {p1} 注、北单 {b1} 注"
                      + "（竞彩取停售前最后采样，北单取开奖 SP）");
                break;
            }

            // 北单开奖补采（2026-11-10 加）。
            //
            // 为什么必须有这条：开奖 SP 是北单唯一真实的赔付价，而它只能从「开奖页」按**期号**抓。
            // 在此之前只有手动 `bdback` CLI 会去抓，最后一次是 11-05（期 26107），
            // 于是 11-06 之后的 45 注北单全部拿不到开奖 SP —— 终版价口径有一半是空的。
            // 自动补采把这个缺口焊死：每天跑一次，只抓「库里没有的期」和「还没开完奖的期」。
            //
            // 期号 = 两位年 + 两位月 + 期序（26091 = 2026 年 9 月第 1 期）。所以只扫本月和上月。
            case "bd-history":
            {
                if (_bdHistory is null || _cards is null)
                {
                    result = CollectionResult.Fail("北单开奖补采依赖未注入");
                    break;
                }
                var done = await _bdHistory.GetExpectsAsync(ct);
                // 一次最多抓 6 期，避免把 500.com 惹毛（每期之间 1.5s 间隔）。
                const int MaxFetchPerRun = 6;
                var fetched = 0;
                var saved = 0;
                var misses = 0;
                for (var back = 0; back <= 1 && fetched < MaxFetchPerRun; back++)
                {
                    var month = DateTime.Today.AddMonths(-back);
                    var prefix = int.Parse($"{month:yyMM}");
                    misses = 0;
                    for (var k = 1; k <= 9 && misses < 2 && fetched < MaxFetchPerRun; k++)
                    {
                        var expect = prefix * 10 + k;
                        if (done.Contains(expect)) { misses = 0; continue; }
                        var rows = await _cards.FetchBdIssueAsync(expect, ct);
                        await Task.Delay(1500, ct);
                        // 被限流时页面也是空的，和「期号不存在」分不开：等一会再试一次。
                        if (rows.Count == 0)
                        {
                            await Task.Delay(5000, ct);
                            rows = await _cards.FetchBdIssueAsync(expect, ct);
                            await Task.Delay(1500, ct);
                        }
                        fetched++;
                        if (rows.Count == 0) { misses++; continue; }
                        misses = 0;
                        saved += await _bdHistory.UpsertAsync(rows, ct);
                    }
                }
                result = CollectionResult.Ok(saved, saved == 0
                    ? $"北单开奖：本月及上月没有待补的期（已收完 {done.Count} 期，本次试抓 {fetched} 期）"
                    : $"北单开奖：补采 {fetched} 期、写入 {saved} 场");
                break;
            }

            // 保存今日推荐（落库，供开奖后复盘）
            case "save-recommendations":
            {
                if (_recommendation is null)
                {
                    result = CollectionResult.Fail("推荐服务未注入");
                    break;
                }
                var all = await _recommendation.RecommendAllAndSaveAsync(ct);
                var sample = all.Values.Sum(v => v.Count);
                // 2026-11-04 用户拍板：胜平负/让球不再对外发布（「发出来还是错的，浪费」）。
                // 这批行只是校准样本 + 否决台账的分母，所以文案不再说「已保存今日推荐」——
                // 那句话会让人以为页面上真能照着买。对外只有大奶。
                result = CollectionResult.Ok(sample, sample == 0
                    ? "大奶榜已刷新；今天没有可算的胜平负方向（不影响大奶）"
                    : $"大奶榜已刷新；胜平负方向 {sample} 场只作校准样本，不对外发布");
                break;
            }

            // 复盘：回填已完场赛果并统计命中
            case "settle-recommendations":
            {
                if (_recommendation is null)
                {
                    result = CollectionResult.Fail("推荐服务未注入");
                    break;
                }
                var s = await _recommendation.SettleAsync(ct);
                result = CollectionResult.Ok(s.Total, $"复盘回填 {s.Total} 场，命中 {s.Hit} 场（命中率 {s.HitRate}%）");
                break;
            }

            // 我的投注：按完场比分自动结算购买方案
            case "settle-purchases":
            {
                if (_purchaseSettle is null)
                {
                    result = CollectionResult.Fail("购买结算服务未注入");
                    break;
                }
                var ps = await _purchaseSettle.AutoSettleAsync(null, ct);
                result = CollectionResult.Ok(ps.Settled,
                    $"购买结算：检查 {ps.Checked}，已结 {ps.Settled}（中 {ps.Won}/未中 {ps.Lost}），待赛果 {ps.Waiting}");
                break;
            }

            // 空跑台账：把某一天的终版榜冻结下来（2026-11-08 加）。
            //
            // 为什么要单独冻一份而不直接查 FbMilkPick：每次重算都会 DELETE 未结算行，
            // 盘中每一版的榜都会把上一版冲掉；撤注的行也是未结算行，下一次重算直接消失。
            // 「照着榜买到底赚不赚」这个评价必须建立在不会变的账本上。
            case "paper-freeze":
            {
                if (_paper is null)
                {
                    result = CollectionResult.Fail("空跑台账仓储未注入");
                    break;
                }
                await _paper.EnsureTableAsync(ct);

                // 先补最近 7 天漏冻的（进程 23:30 没起来、当天部署、重启都靠这个兜底）。
                var (missDays, missRows) = await _paper.FreezeMissingAsync(7, ct);

                // 台账口径（2026-11-08 修正）：**注单一旦上过榜就记账，以首次上榜的价为准。**
                // 原来只在停售后冻「终版」，会永久漏掉两类注：
                //   ① 引擎放弃某注时是整行 DELETE（`SaveDayAsync` 删未结算行），不留撤注标记，
                //      于是「推荐过又收回」完全无痕；
                //   ② 早场注在开赛后被移出榜面（如 21:30 开赛的注，21:40 那版重算就没了）。
                // 11-08 实测漏了 3 注（周日009 / 周日010 / 周日019）。
                // FreezeDayAsync 幂等（先 UPDATE 同步撤注与告警，再 INSERT ... WHERE NOT EXISTS），
                // 高频重复跑不会重复插行，也不会覆盖已记录的赔率。
                var todayR = await _paper.FreezeDayAsync(DateTime.Today, ct);
                var (ins, synced) = (todayR.Inserted, todayR.Synced);
                var todayMsg = $"今 {ins} 注，撤注标记同步 {synced} 条";

                // 凌晨 0~4 点再回冻昨天：不是重复冻，而是**同步撤注标记**——
                // turn-guard 每 10 分钟还在盯 00:30 / 03:45 的场次，撤掉的注在
                // FbMilkPick 里是未结算行，下一次重算就删了，只有这个窗口还读得到。
                if (DateTime.Now.Hour < 4)
                {
                    var y = await _paper.FreezeDayAsync(DateTime.Today.AddDays(-1), ct);
                    ins += y.Inserted;
                    synced += y.Synced;
                    todayMsg += $"；昨 {y.Inserted} 注（同步 {y.Synced} 条）";
                }

                var back = missDays > 0 ? $"；补冻历史 {missDays} 天 {missRows} 注" : "";
                result = CollectionResult.Ok(ins + missRows, $"空跑台账 {todayMsg}{back}");
                break;
            }

            // 空跑台账结算：补赛果与盈亏。每小时跑，和 FbMilkPick 的结算同源判据。
            case "paper-settle":
            {
                if (_paper is null)
                {
                    result = CollectionResult.Fail("空跑台账仓储未注入");
                    break;
                }
                var n = await _paper.SettleAsync(ct);
                result = CollectionResult.Ok(n, n == 0
                    ? "空跑台账：没有待结算的注（赛果未到或半场比分未采到）"
                    : $"空跑台账结算 {n} 注");
                break;
            }

            // 每小时整点复盘：先采集当日完场赛果，再回填推荐命中 + 购买自动结算
            case "settle-hourly":
            {
                if (_recommendation is null)
                {
                    result = CollectionResult.Fail("推荐服务未注入");
                    break;
                }
                var today = DateOnly.FromDateTime(DateTime.Today);
                var collect = await _collection.CollectFootballAsync(new[] { today }, null, ct);
                var s = await _recommendation.SettleAsync(ct);
                var buyMsg = "";
                var buyRows = 0;
                if (_purchaseSettle is not null)
                {
                    var ps = await _purchaseSettle.AutoSettleAsync(null, ct);
                    buyRows = ps.Settled;
                    buyMsg = $"；购买结算 {ps.Settled}（中{ps.Won}/未中{ps.Lost}，待{ps.Waiting}）";
                }
                var msg = $"赛果采集 {collect.Rows} 场；复盘回填 {s.Total} 场，命中 {s.Hit} 场（命中率 {s.HitRate}%）{buyMsg}";
                result = CollectionResult.Ok(collect.Rows + s.Total + buyRows, msg);
                break;
            }

            default:
                result = CollectionResult.Fail($"未知作业类型 {job.Type}");
                break;
        }

        entry.LastRun = DateTime.Now;
        entry.LastStatus = result.Success ? "SUCCESS" : "FAILED";
        entry.LastMessage = result.Message;

        await _sync.WriteAsync($"job:{job.Name}", job.Type, result.Success ? "SUCCESS" : "FAILED",
            result.Rows, result.Message, ct);

        _log?.Invoke($"[{DateTime.Now:HH:mm:ss}] 作业 {job.Name} 结束：{result.Message}");
        return result;
    }
}
