using System.Net;

namespace AiFootBall.Sources;

/// <summary>
/// odds.500.com 的统一 HTTP 通道：共享节流、反爬挑战处理、429/428 自适应退避。
/// 赔率采集与必发采集共用同一个节流器，避免两条流水线互相把访问额度耗尽。
/// </summary>
public static class OddsHttp
{
    /// <summary>全站共享节流器（进程内所有 odds.500.com 请求排队通过）</summary>
    public static readonly RequestThrottle Throttle = new(baseIntervalMs: 900, maxIntervalMs: 4000);

    /// <summary>运行期计数器：成功 / 428 / 429 / 挑战页</summary>
    public static long StatOk, Stat428, Stat429, StatChallenge;

    public static int ThrottleIntervalMs => Throttle.CurrentIntervalMs;

    public static void ResetStats()
    {
        Interlocked.Exchange(ref StatOk, 0);
        Interlocked.Exchange(ref Stat428, 0);
        Interlocked.Exchange(ref Stat429, 0);
        Interlocked.Exchange(ref StatChallenge, 0);
    }

    /// <summary>抓取一个 GBK 页面，自动处理限流与 JS 挑战页。</summary>
    public static async Task<(string? Html, string Reason)> GetAsync(
        HttpClient http, string url, string referer, CancellationToken ct)
    {
        var reason = "";
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                await Throttle.WaitTurnAsync(ct);

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Referrer = new Uri(referer);
                req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);

                if ((int)resp.StatusCode == 429)
                {
                    var retryAfter = resp.Headers.RetryAfter?.Delta?.TotalSeconds
                                     ?? (resp.Headers.RetryAfter?.Date is { } d
                                         ? Math.Max(3, (d - DateTimeOffset.UtcNow).TotalSeconds)
                                         : 0);
                    // EdgeOne 的 429 多为瞬时抖动：优先短冷却；仅当服务端明确给出 Retry-After 时才整体降速
                    if (retryAfter > 0) Throttle.OnRateLimited((int)retryAfter);
                    else Throttle.Cooldown(3);
                    Interlocked.Increment(ref Stat429);
                    reason = "http429";
                    continue;
                }

                // 428 = Edgio 软限流，短冷却后重试即可
                if ((int)resp.StatusCode == 428)
                {
                    Throttle.Cooldown(2);
                    Interlocked.Increment(ref Stat428);
                    reason = "http428";
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    reason = $"http{(int)resp.StatusCode}";
                    return (null, reason);
                }

                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length < 400)
                {
                    reason = $"short{bytes.Length}";
                    return (null, reason);
                }

                var html = TextDecoder.DecodeGbk(bytes);

                // 命中反爬挑战：解析出校验 Cookie 后重试
                if (BotGuard.IsChallenge(html))
                {
                    Interlocked.Increment(ref StatChallenge);
                    reason = "challenge";
                    BotGuard.Solve(html);
                    await BotGuard.WaitAsync(ct);
                    continue;
                }

                if (bytes.Length < 1000)
                {
                    reason = $"empty{bytes.Length}";
                    return (null, reason);
                }

                Throttle.OnSuccess();
                Interlocked.Increment(ref StatOk);
                return (html, "");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException)
            {
                reason = "network";
                if (attempt < 7) await Task.Delay(1000, ct);
            }
            catch (TaskCanceledException)
            {
                reason = "timeout";
                if (attempt < 7) await Task.Delay(1000, ct);
            }
        }
        return (null, reason.Length > 0 ? reason : "retry-exhausted");
    }

    /// <summary>构造共享 HttpClient（复用 BotGuard 所需的 Cookie 容器）。</summary>
    public static HttpClient CreateClient(int concurrency)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = Math.Max(8, concurrency * 3),
            UseCookies = true,
            CookieContainer = BotGuard.Cookies
        };
        var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        return http;
    }
}
