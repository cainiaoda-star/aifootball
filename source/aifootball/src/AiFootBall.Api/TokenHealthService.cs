using AiFootBall.Sources;

namespace AiFootBall.Api;

/// <summary>
/// token 健康守护：盯着"人机验证拦截"计数，连续被拦就自动续期。
///
/// 为什么按"拦截次数"而不是按"时间"触发：
///   token 寿命由请求量决定，空闲时几小时都不失效，回补时二十分钟就可能被打掉。
///   按时间定时续期会白开浏览器窗口，按拦截次数触发则刚好在真正需要时动手。
///
/// 阈值取 4：单场采集是 3 个页面，连续 4 次拦截基本可判定为 token 已被降级，
/// 而不是偶发的网络抖动。
/// </summary>
public sealed class TokenHealthService : BackgroundService
{
    private readonly ILogger<TokenHealthService> _logger;

    /// <summary>连续拦截阈值：累计达到即续期</summary>
    private const long VerifyThreshold = 3;

    /// <summary>巡检间隔（秒）</summary>
    private const int CheckSeconds = 30;

    /// <summary>多久没有新的拦截就把累计清零（分钟），避免偶发拦截跨天累加成误判</summary>
    private const int DecayMinutes = 10;

    public TokenHealthService(ILogger<TokenHealthService> logger) => _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);

        if (!TokenRefresher.Enabled)
        {
            _logger.LogWarning("token 自动续期未启用（找不到 _probe/harvest.mjs），被拦截时需人工执行 harvest.mjs");
            return;
        }

        _logger.LogInformation("token 自动续期已启用：脚本 {Script}，token {Cookie}，阈值 {Threshold} 次拦截",
            TokenRefresher.ScriptPath, TokenRefresher.CookiePath, VerifyThreshold);

        var lastVerify = OddsHttp.StatVerify;
        long pending = 0;
        var lastHitAt = DateTime.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(CheckSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                var now = OddsHttp.StatVerify;
                // 计数器是进程内累加的；若出现回退（理论上不该发生）直接重新对齐
                var delta = now >= lastVerify ? now - lastVerify : 0;
                lastVerify = now;

                // 关键：单次验证拦截会触发全站冷却，命中率被压到很低，
                // 所以不能只看"本次 30 秒窗口内是否够 4 次"，必须跨窗口累计。
                if (delta > 0)
                {
                    pending += delta;
                    lastHitAt = DateTime.Now;
                }
                else if (pending > 0 && (DateTime.Now - lastHitAt).TotalMinutes >= DecayMinutes)
                {
                    pending = 0;
                }

                if (pending < VerifyThreshold || TokenRefresher.IsRunning) continue;

                _logger.LogWarning("累计 {Count} 次人机验证拦截，开始自动续期 token", pending);
                var (ok, message) = await TokenRefresher.TryRefreshAsync("verify-hits",
                    m => _logger.LogInformation("{Message}", m), stoppingToken);

                if (ok) _logger.LogInformation("自动续期成功：{Message}", message);
                else _logger.LogWarning("自动续期未成功：{Message}", message);

                // 续期动作本身会消耗若干次请求，重新对齐基线
                lastVerify = OddsHttp.StatVerify;
                pending = 0;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "token 健康巡检异常");
            }
        }
    }
}
