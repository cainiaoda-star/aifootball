using AiFootBall.Data;
using AiFootBall.Sources;

namespace AiFootBall.Api;

/// <summary>
/// token 健康守护：盯着"人机验证拦截"计数，连续被拦就自动续期。
///
/// 为什么按"拦截次数"而不是按"时间"触发：
///   token 寿命由请求量决定，空闲时几小时都不失效，回补时二十分钟就可能被打掉。
///   按时间定时续期会白开浏览器窗口，按拦截次数触发则刚好在真正需要时动手。
///
/// 阈值取 3：单场采集是 3 个页面，连续 3 次拦截基本可判定为 token 已被降级，
/// 而不是偶发的网络抖动。
///
/// 2026-11-10 补上的关键一环：**「续期成功」不等于「token 能用」**。
/// 续期成功只证明 harvest.mjs 跑完并把 cookie 写进文件了；站点照样可以拒。
/// 11-09 全天报了 41 次「token 续期成功」（每 8 分钟一次，凌晨一刻没停），
/// 而 FbOddsHistory 当天首条仍等到 08:05 —— 也就是说整夜都在「成功」地空转。
/// 现在若续期后又被拦够一整轮，就写一条 token-health/FAILED 进 SyncLog，
/// 让「需人工更换 token」从日志文件里的一行字，变成监控页能看见的失败。
/// </summary>
public sealed class TokenHealthService : BackgroundService
{
    private readonly ILogger<TokenHealthService> _logger;
    private readonly SyncLogRepository _sync;

    /// <summary>连续拦截阈值：累计达到即续期</summary>
    private const long VerifyThreshold = 3;

    /// <summary>巡检间隔（秒）</summary>
    private const int CheckSeconds = 30;

    /// <summary>多久没有新的拦截就把累计清零（分钟），避免偶发拦截跨天累加成误判</summary>
    private const int DecayMinutes = 10;

    /// <summary>
    /// 「装了新 token 却仍被拦」的判定线。一轮采样 10~58 场，全被拦也就几十次；
    /// 取 30 表示「整轮都打不开」，不是偶发抖动。低于这个数只会制造噪音。
    /// </summary>
    private const long IneffectiveAfterVerifies = 30;

    public TokenHealthService(ILogger<TokenHealthService> logger, SyncLogRepository sync)
    {
        _logger = logger;
        _sync = sync;
    }

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

        // 假成功识别用：续期装载成功那一刻的拦截图样。
        var successSeen = DateTime.MinValue;
        long verifyAtSuccess = 0;

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

                // 假成功识别：记住「续期装载成功」那一刻的拦截图样。之后每多拦一次，
                // 都说明这个新 token 越不可用；攒够一整轮就判定它根本不能用。
                var succAt = TokenRefresher.LastSuccessAt;
                if (succAt != successSeen)
                {
                    successSeen = succAt;
                    verifyAtSuccess = OddsHttp.StatVerify;
                }

                var blockedSince = successSeen == DateTime.MinValue
                    ? 0
                    : Math.Max(0, OddsHttp.StatVerify - verifyAtSuccess);

                if (TokenRefresher.RefreshProvenIneffective(successSeen, OddsHttp.StatVerify,
                        verifyAtSuccess, IneffectiveAfterVerifies))
                {
                    // 续期被证伪 → 立刻熔断，别再开浏览器。
                    // 只有新开那一次才写日志/告警，否则巡检每 30 秒喊一遍（会把人训练成不看告警）。
                    if (TokenRefresher.OpenCircuit())
                    {
                        var until = TokenRefresher.CircuitUntil;
                        var msg = $"token 续期被证伪，已熔断至 {until:HH:mm}（第 {TokenRefresher.CircuitStreak} 次）："
                                  + $"{successSeen:HH:mm:ss} 装载新 cookie 后又被拦 {blockedSince} 次"
                                  + $"（阈值 {IneffectiveAfterVerifies}）。"
                                  + "换 token 已多次无效，原因是站点对该子域整体降级（同 cookie 在浏览器里可用、"
                                  + "换成程序请求即被人机验证拦截），需等风控降温或改走浏览器通道。";
                        _logger.LogError("{Message}", msg);
                        try
                        {
                            // 铁律：写日志处不用 CancellationToken.None，一律 30 秒上限。
                            using var lcts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                            await _sync.WriteAsync("token-health", "500.com", "FAILED", 0, msg, lcts.Token);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "写入 token-health 状态失败（不影响续期）");
                        }
                    }
                }
                else if (successSeen != DateTime.MinValue && blockedSince == 0
                         && (DateTime.Now - successSeen).TotalMinutes >= 10)
                {
                    // 续期后 10 分钟一次拦截都没有 → 这次是真的救回来了，清熔断统计。
                    TokenRefresher.ResetCircuit();
                }

                if (pending < VerifyThreshold || TokenRefresher.IsRunning) continue;

                // 熔断期内不再尝试：见 TokenRefresher.CircuitBreakMinutes 的实测记录。
                if (TokenRefresher.IsCircuitOpen) continue;

                // 冷却期内不再白试。原来每 30 秒就调一次 TryRefreshAsync：11-09 一天打了
                // 913 行「累计 3 次拦截，开始自动续期」，其中 492 行被每小时上限挡回、
                // 其余被冷却挡回，真正开浏览器的只有每小时 4 次。pending 不清零，
                // 等冷却结束立刻接着做，行为不变但日志干净了。
                if (TokenRefresher.IsCoolingDown) continue;

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
