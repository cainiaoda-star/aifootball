using AiFootBall.Core.Options;
using AiFootBall.Sources;

namespace AiFootBall.Api;

/// <summary>把内置 Cron 调度器挂到 Web 宿主上，随 API 一起运行。</summary>
public sealed class SchedulerHostedService : BackgroundService
{
    private readonly JobScheduler _scheduler;
    private readonly JobsOptions _options;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<SchedulerHostedService> _logger;

    public SchedulerHostedService(
        JobScheduler scheduler, JobsOptions options, IWebHostEnvironment env, ILogger<SchedulerHostedService> logger)
    {
        _scheduler = scheduler;
        _options = options;
        _env = env;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("定时调度已在配置中关闭（Jobs:Enabled = false）");
            return;
        }

        try
        {
            // 先落心跳起点：InitializeAsync 读库失败时下面 catch 只打一行日志就结束，
            // 主循环不会再进；没有这个起点，/api/health 会一直把「调度器已死」报成 ok。
            _scheduler.MarkStarting();
            await _scheduler.InitializeAsync(stoppingToken);
            foreach (var saved in JobScheduleStore.Load(JobScheduleStore.FilePath(_env.ContentRootPath)))
            {
                var (ok, message) = _scheduler.Update(saved.Name, saved.Cron, saved.Enabled, saved.Hours, saved.RecheckMinutes);
                if (!ok) _logger.LogWarning("忽略已保存的采集时间 {Name}：{Message}", saved.Name, message);
            }
            await _scheduler.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "定时调度异常终止");
        }
    }
}
