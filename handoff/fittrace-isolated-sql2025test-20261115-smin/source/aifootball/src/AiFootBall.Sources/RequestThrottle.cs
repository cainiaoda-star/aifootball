using System.Diagnostics;

namespace AiFootBall.Sources;

/// <summary>
/// 全局自适应请求节流器：保证相邻请求的最小间隔，并在收到 HTTP 429 时冷却并自动降速。
/// 所有对 odds.500.com 的请求共用同一个实例。
/// </summary>
public sealed class RequestThrottle
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int _baseIntervalMs;
    private readonly int _maxIntervalMs;

    private int _intervalMs;
    private long _nextSlotTicks;      // Stopwatch 时间戳
    private long _cooldownUntilTicks;
    private int _successStreak;

    public RequestThrottle(int baseIntervalMs = 300, int maxIntervalMs = 4000)
    {
        _baseIntervalMs = Math.Max(50, baseIntervalMs);
        _maxIntervalMs = Math.Max(_baseIntervalMs, maxIntervalMs);
        _intervalMs = _baseIntervalMs;
    }

    public int CurrentIntervalMs => Volatile.Read(ref _intervalMs);

    /// <summary>基线间隔。请求全部成功、没有被 429 惩罚时会逐步回到这个值。</summary>
    public int BaseIntervalMs => _baseIntervalMs;

    /// <summary>间隔上限。被连续限流时不会超过这个值。</summary>
    public int MaxIntervalMs => _maxIntervalMs;

    /// <summary>排队等待下一个请求槽位。</summary>
    public async Task WaitTurnAsync(CancellationToken ct = default)
    {
        while (true)
        {
            long waitMs;
            await _gate.WaitAsync(ct);
            try
            {
                var now = Stopwatch.GetTimestamp();
                var slot = Math.Max(_nextSlotTicks, _cooldownUntilTicks);
                var waitTicks = slot - now;
                if (waitTicks <= 0)
                {
                    _nextSlotTicks = now + MsToTicks(_intervalMs);
                    return;
                }
                waitMs = (long)TicksToMs(waitTicks) + 5;
            }
            finally
            {
                _gate.Release();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(waitMs, 3000)), ct);
        }
    }

    /// <summary>短冷却（用于 428 等软限流），不改变基准间隔。</summary>
    public void Cooldown(int seconds)
    {
        var until = Stopwatch.GetTimestamp() + MsToTicks(Math.Max(1, seconds) * 1000L);
        long current;
        do
        {
            current = Interlocked.Read(ref _cooldownUntilTicks);
            if (current >= until) return;
        } while (Interlocked.CompareExchange(ref _cooldownUntilTicks, until, current) != current);
    }

    /// <summary>收到 429：短冷却 + 提高基准间隔（冷却上限 8 秒；实测 429 多为瞬时抖动）</summary>
    public void OnRateLimited(int retryAfterSeconds)
    {
        var cooldown = Math.Clamp(retryAfterSeconds, 2, 8);
        Interlocked.Exchange(ref _intervalMs, Math.Min(_intervalMs * 2, _maxIntervalMs));
        Interlocked.Exchange(ref _successStreak, 0);
        Interlocked.Exchange(ref _cooldownUntilTicks, Stopwatch.GetTimestamp() + MsToTicks(cooldown * 1000L));
    }

    /// <summary>请求成功：连续成功后逐步恢复到基线间隔。</summary>
    public void OnSuccess()
    {
        if (Interlocked.Increment(ref _successStreak) < 20) return;
        Interlocked.Exchange(ref _successStreak, 0);

        var current = Volatile.Read(ref _intervalMs);
        if (current > _baseIntervalMs)
            Interlocked.Exchange(ref _intervalMs, Math.Max(_baseIntervalMs, current - Math.Max(50, current / 8)));
    }

    private static long MsToTicks(long ms) => ms * Stopwatch.Frequency / 1000;
    private static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
