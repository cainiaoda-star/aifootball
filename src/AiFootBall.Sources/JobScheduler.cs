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
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly List<Entry> _entries = new();

    public JobScheduler(JobsOptions options, CollectionService collection, SyncLogRepository sync,
        RecommendationService? recommendation = null, PurchaseSettlementService? purchaseSettle = null,
        Action<string>? log = null, LottoRecommendationService? lottoRec = null)
    {
        _options = options;
        _collection = collection;
        _sync = sync;
        _recommendation = recommendation;
        _purchaseSettle = purchaseSettle;
        _lottoRec = lottoRec;
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
    }

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
            var baseline = last == default ? DateTime.Now.AddMinutes(-1) : last;
            var next = schedule.GetNextOccurrence(baseline);

            _entries.Add(new Entry
            {
                Job = job,
                Schedule = schedule,
                Next = next < DateTime.Now ? DateTime.Now.AddSeconds(5) : next,
                LastRun = last == default ? null : last
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
        running = e.Running
    }).ToList();

    /// <summary>主循环：每 20 秒检查一次到期作业，串行执行避免抢占带宽。</summary>
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

        while (!ct.IsCancellationRequested)
        {
            List<Entry> due;
            lock (_gate)
            {
                due = _entries.Where(x => x.Job.Enabled && !x.Running && DateTime.Now >= x.Next).ToList();
                foreach (var entry in due)
                {
                    entry.Next = entry.Schedule.GetNextOccurrence(DateTime.Now);
                    entry.Running = true;
                }
            }

            foreach (var entry in due)
            {
                try
                {
                    await ExecuteAsync(entry, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    entry.Running = false;
                    return;
                }
                catch (Exception ex)
                {
                    entry.LastStatus = "FAILED";
                    entry.LastMessage = ex.Message;
                    _log?.Invoke($"[{entry.Job.Name}] 执行异常：{ex.Message}");
                }
                finally
                {
                    entry.Running = false;
                }
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
        try
        {
            var result = await ExecuteAsync(entry, ct);
            return (result.Success, result.Message);
        }
        finally
        {
            entry.Running = false;
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
            case "daily-review":
            {
                if (_recommendation is null)
                {
                    result = CollectionResult.Fail("推荐服务未注入");
                    break;
                }
                var now = DateTime.Now;
                var day = now.Hour < 12 ? now.Date.AddDays(-1) : now.Date;
                var path = await _recommendation.WriteDailyReviewAsync(day, ct);
                result = CollectionResult.Ok(1, $"复盘已写入 {path}");
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
                var saved = all.Values.Sum(v => v.Count);
                result = CollectionResult.Ok(saved, $"已保存今日推荐 {saved} 场（竞彩 {all.GetValueOrDefault("jc")?.Count ?? 0} / 北单 {all.GetValueOrDefault("bd")?.Count ?? 0} / 胜负彩 {all.GetValueOrDefault("sfc")?.Count ?? 0}）");
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
