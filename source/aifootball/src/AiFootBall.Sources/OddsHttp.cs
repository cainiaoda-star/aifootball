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

    /// <summary>运行期计数器：成功 / 428 / 429 / 挑战页 / 人机验证拦截</summary>
    public static long StatOk, Stat428, Stat429, StatChallenge, StatVerify;

    /// <summary>经由浏览器通道取回的页数（2026-11-10 起 odds.500.com 主要靠这条通道）。</summary>
    public static long StatChrome;

    /// <summary>
    /// 连续「HTTP 直连被拦」次数。达到 <see cref="SkipHttpAfter"/> 之后直接走浏览器通道，
    /// 不再每页先打一次注定失败的 HTTP —— 每页省 1~2 秒，也少给风控送样本。
    /// </summary>
    private static int _httpBlockedStreak;

    /// <summary>连续被拦多少次后改为「默认走浏览器」。</summary>
    private const int SkipHttpAfter = 3;

    public static int HttpBlockedStreak => Volatile.Read(ref _httpBlockedStreak);

    public static int ThrottleIntervalMs => Throttle.CurrentIntervalMs;

    /// <summary>节流基线（全部请求成功时会回落到这里）。报出去用于判断「慢」是不是被限流惩罚。</summary>
    public static int ThrottleBaseMs => Throttle.BaseIntervalMs;

    /// <summary>节流上限。</summary>
    public static int ThrottleMaxMs => Throttle.MaxIntervalMs;

    public static void ResetStats()
    {
        Interlocked.Exchange(ref StatOk, 0);
        Interlocked.Exchange(ref Stat428, 0);
        Interlocked.Exchange(ref Stat429, 0);
        Interlocked.Exchange(ref StatChallenge, 0);
        Interlocked.Exchange(ref StatVerify, 0);
        Interlocked.Exchange(ref StatChrome, 0);
        Interlocked.Exchange(ref StatChromeBlocked, 0);
    }

    /// <summary>
    /// 走浏览器通道取一页。成功计数；失败返回 null 让调用方继续原流程。
    /// 浏览器通道是串行的（单个 tab），并发采集在这里排队，这是它的固有代价。
    ///
    /// ★ 这里不清 <see cref="_httpBlockedStreak"/>：清了就等于每页都先打一次注定失败的 HTTP，
    /// 快速通道永远进不去。归零只在 HTTP 直连真正成功时做（那才叫恢复了）。
    ///
    /// ★★ 浏览器也会被打回验证页（2026-11-10 23:20 实测：过一次验证后只抓了几页就又弹
    /// 「请勾选下方复选框」）。那种响应必须当**失败**处理 —— 若当成采集成功，解析器会从
    /// 验证页里解析出零条赔率，页面显示"采集正常、0 条数据"，这就是本项目最怕的静默脏数据。
    /// </summary>
    private static async Task<string?> TryChromeAsync(string url, CancellationToken ct)
    {
        if (!ChromeBridge.Enabled) return null;
        var html = await ChromeBridge.GetHtmlAsync(url, ct);
        if (html is null) return null;

        if (BotGuard.IsVerificationPage(html) || BotGuard.IsChallenge(html))
        {
            Interlocked.Increment(ref StatChromeBlocked);
            ChromeBridge.NoteBlocked();
            return null;
        }

        Interlocked.Increment(ref StatChrome);
        ChromeBridge.NoteOk();
        return html;
    }

    /// <summary>浏览器通道也被打回验证页的次数。与 StatVerify（HTTP 通道）分开计数，
    /// 两者的成因不同：前者是会话被高频请求打回，后者是客户端指纹被降级。</summary>
    public static long StatChromeBlocked;

    /// <summary>上一次放行 HTTP 直连探测的时刻（快速通道生效期间，每 30 分钟探一次恢复情况）。</summary>
    private static DateTime _lastHttpProbeAt = DateTime.MinValue;

    /// <summary>抓取一个 GBK 页面，自动处理限流与 JS 挑战页。</summary>
    public static async Task<(string? Html, string Reason)> GetAsync(
        HttpClient http, string url, string referer, CancellationToken ct)
    {
        var reason = "";
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                // 快速通道：连续被拦之后，HTTP 直连在这种状态下拿不到真页面，
                // 每页先打一次只会白等 + 给风控送样本。直接问浏览器。
                // 但要留一个恢复口子：每 30 分钟放一页走 HTTP，站点恢复正常时会自动切回去。
                if (Volatile.Read(ref _httpBlockedStreak) >= SkipHttpAfter)
                {
                    var probeDue = (DateTime.Now - _lastHttpProbeAt).TotalMinutes >= 30;
                    if (!probeDue)
                    {
                        var fast = await TryChromeAsync(url, ct);
                        if (fast is not null)
                        {
                            Interlocked.Increment(ref StatOk);
                            return (fast, "chrome");
                        }
                    }
                    else
                    {
                        _lastHttpProbeAt = DateTime.Now;
                    }
                }

                // token 文件被替换后自动生效（站点会在访问量上来后打发 token，需人工续期）
                BotGuard.RefreshIfChanged();

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
                    Interlocked.Increment(ref _httpBlockedStreak);
                    reason = "challenge";

                    // 先问浏览器。2026-11-10 实测：挑战页能算对 __tst_status（与站点脚本一致），
                    // 但提交后立刻升级到 TEOCaptcha 二级人机验证 —— 那一级判的是浏览器行为，
                    // .NET / curl / curl_cffi（六种指纹）全部过不去。所以 Solve 只留作
                    // "浏览器没开"时的兜底，不再是主路径。
                    var viaChrome = await TryChromeAsync(url, ct);
                    if (viaChrome is not null)
                    {
                        Interlocked.Increment(ref StatOk);
                        return (viaChrome, "chrome");
                    }

                    BotGuard.Solve(html);
                    await BotGuard.WaitAsync(ct);
                    continue;
                }

                // 二级人机验证页：token 缺失或过期。重试没有意义，直接给出可辨识的原因。
                // 冷却 10 秒（不是 30）：冷却过长会让"连续 6 次拦截即中止"要等 3 分钟才触发，
                // 期间全站节流器被占着，续期完成前什么都干不了。
                if (BotGuard.IsVerificationPage(html))
                {
                    Interlocked.Increment(ref StatVerify);
                    Interlocked.Increment(ref _httpBlockedStreak);

                    // 同上：这一级只能靠浏览器过。走通就不必冷却，否则兜底会被 10 秒冷却拖死。
                    var viaChrome = await TryChromeAsync(url, ct);
                    if (viaChrome is not null)
                    {
                        Interlocked.Increment(ref StatOk);
                        return (viaChrome, "chrome");
                    }

                    Throttle.Cooldown(10);
                    return (null, "verify-required");
                }

                if (bytes.Length < 1000)
                {
                    reason = $"empty{bytes.Length}";
                    return (null, reason);
                }

                Throttle.OnSuccess();
                Interlocked.Increment(ref StatOk);
                Interlocked.Exchange(ref _httpBlockedStreak, 0);
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
            BotGuard.UserAgent
            ?? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        return http;
    }
}
