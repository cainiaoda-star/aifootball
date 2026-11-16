using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AiFootBall.Sources;

/// <summary>
/// odds.500.com 的「浏览器兜底通道」。
///
/// 背景（2026-11-10 22:07 起实测）：
///   odds.500.com 整个主机名被 EdgeOne 定向降级 —— 同一套 cookie 在 Chrome 里
///   能正常打开赔率页，换成程序请求立刻回 TEOCaptcha 人机验证页。已排除的变量：
///   请求头（sec-ch-ua / sec-fetch-* / priority 全对齐）、HTTP 版本（h1.1 与 h2 一样）、
///   TLS 指纹（curl_cffi 的 chrome / chrome131 / chrome124 / chrome120 / safari17 /
///   edge101 六种指纹全部被拦）、cookie 完整性（把 Chrome 里 20 条 cookie 全带上一样）。
///   JS 挑战本身可解（BotGuard 算出的 __tst_status 与站点脚本一致），但提交后立刻
///   升级到二级人机验证 —— 它判的是浏览器行为，不是任何能伪造的头部。
///
///   而在跑的 Chrome 调试实例能正常取数，所以把「被拦」的请求转给它。
///
/// 设计约束：
///   · 单 tab 串行。同一时刻只允许一个请求，否则多个 Page.navigate 互相踩。
///   · 只做兜底。正常 HTTP 通道能拿到就不走这里 —— 一个页面 1~2 秒，全走会把
///     一轮采样从几十秒拖到十几分钟。
///   · 端口不通就当作没这条通道，不抛异常、不阻塞主链路。
/// </summary>
public static class ChromeBridge
{
    private const int CdpPort = 9222;

    /// <summary>串行闸：一个 Chrome tab 只能被一个请求驱动。</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>端口探测结果缓存，避免每页都打一次 /json/version。</summary>
    private static DateTime _probedAt = DateTime.MinValue;
    private static bool _probedOk;

    /// <summary>
    /// ★ Chrome 的 DevTools HTTP 端点在每条 TCP 连接上只服务一次请求：同一个 HttpClient
    /// 复用 keep-alive 连接发第二个请求会拿到 404（同一代码用 curl 每次都新连接，所以看着正常）。
    /// 实测「同 HttpClient 连发 3 次」= 200/404/404，「每次新建」= 200/200/200。
    /// 这里用 PooledConnectionLifetime=Zero 强制每请求新连接，既保留单实例又避开这个坑。
    /// </summary>
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.Zero,
        PooledConnectionIdleTimeout = TimeSpan.Zero,
        ConnectTimeout = TimeSpan.FromSeconds(3),
    }, disposeHandler: false)
    { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>本轮是否用过浏览器通道（供日志说明"这批数据是浏览器取回来的"）。</summary>
    public static int UsedCount => _used;
    private static int _used;

    /// <summary>
    /// 最近一次失败原因。这条通道是兜底用的，失败必须可诊断 ——
    /// 否则只会表现成「偶发少几场数据」，没人知道是浏览器没连上还是页面没渲染完。
    /// </summary>
    public static string? LastError { get; private set; }

    /// <summary>连续拿到验证页的次数。达到阈值就冷却，避免高频撞击把会话越坐越死。</summary>
    private static int _blockedStreak;

    /// <summary>冷却到期时刻。</summary>
    private static DateTime _cooldownUntil = DateTime.MinValue;

    /// <summary>连续几次验证页后开始冷却。</summary>
    private const int BlockedBeforeCooldown = 3;

    /// <summary>冷却时长（分钟）。</summary>
    private const int CooldownMinutes = 5;

    /// <summary>
    /// 2026-11-10 23:20 实测：过一次人机验证后只抓了几页，Chrome 自己又被要求验证。
    /// 说明站点对"已验证会话"仍有请求频率限制，浏览器通道不是无限额度。
    /// 撞到验证页时立刻停手冷却，否则越撞越死（而且每次都白等 2~3 秒）。
    /// </summary>
    public static void NoteBlocked()
    {
        if (Interlocked.Increment(ref _blockedStreak) < BlockedBeforeCooldown) return;
        _cooldownUntil = DateTime.Now.AddMinutes(CooldownMinutes);
        Interlocked.Exchange(ref _blockedStreak, 0);
        LastError = $"连续 {BlockedBeforeCooldown} 次拿到验证页，浏览器通道冷却 {CooldownMinutes} 分钟";
    }

    /// <summary>浏览器通道冷却中。</summary>
    public static bool InCooldown => _cooldownUntil != DateTime.MinValue && DateTime.Now < _cooldownUntil;

    /// <summary>冷却到期时刻，供诊断展示。</summary>
    public static DateTime? CooldownUntil => _cooldownUntil == DateTime.MinValue ? null : _cooldownUntil;

    /// <summary>成功取到一页：清空连续被拦计数。</summary>
    public static void NoteOk() => Interlocked.Exchange(ref _blockedStreak, 0);

    /// <summary>端口是否可用。30 秒内复用上次探测结果。用裸 TCP 探测，不消耗 DevTools HTTP 连接。</summary>
    public static bool Enabled
    {
        get
        {
            if (InCooldown) return false;
            var now = DateTime.Now;
            if ((now - _probedAt).TotalSeconds < 30) return _probedOk;
            _probedAt = now;
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                _probedOk = tcp.ConnectAsync("127.0.0.1", CdpPort).Wait(TimeSpan.FromSeconds(2))
                            && tcp.Connected;
            }
            catch
            {
                _probedOk = false;
            }
            return _probedOk;
        }
    }

    /// <summary>
    /// 让 Chrome 打开 <paramref name="url"/> 并返回渲染后的 outerHTML。
    /// 失败返回 null（调用方按"取不到"处理，不许抛）。
    /// </summary>
    public static async Task<string?> GetHtmlAsync(string url, CancellationToken ct = default)
    {
        if (!Enabled) return null;

        // 等闸最多 40 秒：一轮采样里可能有十几个页面排队，但单个页面正常 1~2 秒。
        if (!await Gate.WaitAsync(TimeSpan.FromSeconds(40), ct)) return null;
        try
        {
            var html = await FetchCoreAsync(url, ct);
            if (html is not null)
            {
                Interlocked.Increment(ref _used);
                LastError = null;
            }
            return html;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<string?> FetchCoreAsync(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));

        // 不走 /json/list：那个端点在 .NET HttpClient 下会随机 404（同样请求用 curl 是 200，
        // 加 Accept / 显式 Host / Connection: close 都可能触发），不可靠。
        // 改从 /json/version 拿 browser 级 ws —— 这个端点始终 200 —— 再用 Target 域操作页面。
        var browserWs = await GetBrowserWsAsync(timeout.Token);
        if (browserWs is null) { LastError = "拿不到 browser webSocketDebuggerUrl"; return null; }

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(browserWs), timeout.Token);

        var chat = new CdpSession(ws);
        // 读循环必须在使用会话之前起来，否则第一帧响应就没人收，SendAsync 永远等不到 id。
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var pump = chat.PumpAsync(pumpCts.Token);
        var createdTarget = false;
        string? targetId = null;
        try
        {
            targetId = await FindPageTargetAsync(chat, timeout.Token);
            if (targetId is null)
            {
                var created = await chat.SendAsync("Target.createTarget", new { url = "about:blank" }, null, timeout.Token);
                targetId = created.GetProperty("result").GetProperty("targetId").GetString();
                createdTarget = true;
            }
            if (targetId is null) { LastError = "没有可用的 page target"; return null; }

            var attach = await chat.SendAsync("Target.attachToTarget",
                new { targetId, flatten = true }, null, timeout.Token);
            var sessionId = attach.TryGetProperty("result", out var ar) && ar.TryGetProperty("sessionId", out var sid)
                ? sid.GetString()
                : null;
            if (sessionId is null) { LastError = "attachToTarget 未返回 sessionId"; return null; }

            await chat.SendAsync("Page.enable", null, sessionId, timeout.Token);
            await chat.SendAsync("Page.navigate", new { url }, sessionId, timeout.Token);

            // 轮询 readyState 而不是等 Page.loadEventFired：
            // 挑战页会自动 location.reload()，load 事件会来两次，取第一次反而拿到挑战页。
            var ready = false;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(18))
            {
                await Task.Delay(400, timeout.Token);
                var state = await chat.EvaluateAsync("document.readyState", sessionId, timeout.Token);
                if (state == "complete") { ready = true; break; }
            }
            if (!ready) { LastError = "页面 18 秒内没到 readyState=complete"; return null; }

            // 再给页面 1.2 秒稳定时间（挑战页脚本要求的重载延迟就是 1.2s）。
            await Task.Delay(1200, timeout.Token);

            var html = await chat.EvaluateAsync("document.documentElement.outerHTML", sessionId, timeout.Token);
            return string.IsNullOrWhiteSpace(html) ? null : html;
        }
        finally
        {
            if (createdTarget && targetId is not null)
            {
                // 自己开的 tab 自己关；复用已有 tab 时不动它（可能是用户正在看的页面）。
                try { await chat.SendAsync("Target.closeTarget", new { targetId }, null, CancellationToken.None); }
                catch { /* 关不掉不影响结果 */ }
            }
            pumpCts.Cancel();
            try { await pump.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* 读循环收尾失败不影响结果 */ }
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
            catch { /* 关不上就算了，Chrome 会自己回收 */ }
        }
    }

    /// <summary>从 /json/version 取 browser 级 webSocketDebuggerUrl。这个端点比 /json/list 稳。</summary>
    private static async Task<string?> GetBrowserWsAsync(CancellationToken ct)
    {
        try
        {
            var json = await Http.GetStringAsync($"http://127.0.0.1:{CdpPort}/json/version", ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("webSocketDebuggerUrl", out var w) ? w.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>在已有 target 里找一个 500.com 的 page；没有就返回 null（调用方新建）。</summary>
    private static async Task<string?> FindPageTargetAsync(CdpSession chat, CancellationToken ct)
    {
        var res = await chat.SendAsync("Target.getTargets", null, null, ct);
        if (!res.TryGetProperty("result", out var r) || !r.TryGetProperty("targetInfos", out var infos))
            return null;

        string? fallback = null;
        foreach (var t in infos.EnumerateArray())
        {
            if (!t.TryGetProperty("type", out var type) || type.GetString() != "page") continue;
            if (!t.TryGetProperty("targetId", out var idEl)) continue;
            var id = idEl.GetString();
            if (id is null) continue;
            var u = t.TryGetProperty("url", out var ue) ? ue.GetString() ?? "" : "";
            if (u.Contains("500.com", StringComparison.OrdinalIgnoreCase)) return id;
            fallback ??= id;
        }
        return fallback;
    }

    /// <summary>极简 CDP 会话：按 id 匹配响应，忽略事件通知。支持 flatten 模式的 sessionId。</summary>
    private sealed class CdpSession
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private readonly ClientWebSocket _ws;
        private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
        private int _id;

        public CdpSession(ClientWebSocket ws) => _ws = ws;

        public async Task<JsonElement> SendAsync(string method, object? param, string? sessionId, CancellationToken ct)
        {
            var tcs = await DispatchAsync(method, param, sessionId, ct);
            return await tcs.Task.WaitAsync(ct);
        }

        public async Task<string?> EvaluateAsync(string expression, string? sessionId, CancellationToken ct)
        {
            var tcs = await DispatchAsync("Runtime.evaluate",
                new { expression, returnByValue = true, awaitPromise = true }, sessionId, ct);
            var result = await tcs.Task.WaitAsync(ct);
            if (result.TryGetProperty("result", out var r)
                && r.TryGetProperty("result", out var inner)
                && inner.TryGetProperty("value", out var v))
            {
                return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
            }
            return null;
        }

        private async Task<TaskCompletionSource<JsonElement>> DispatchAsync(string method, object? param,
            string? sessionId, CancellationToken ct)
        {
            var id = ++_id;
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            var payload = JsonSerializer.Serialize(new { id, method, @params = param, sessionId }, JsonOpts);
            await _ws.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, ct);
            return tcs;
        }

        /// <summary>后台读循环。必须在 Send 前启动。</summary>
        public async Task PumpAsync(CancellationToken ct)
        {
            var buf = new byte[64 * 1024];
            var sb = new StringBuilder();
            while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var r = await _ws.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close) return;
                sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                if (!r.EndOfMessage) continue;

                var text = sb.ToString();
                sb.Clear();
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id)
                        && _pending.TryGetValue(id, out var tcs))
                    {
                        _pending.Remove(id);
                        tcs.TrySetResult(root.Clone());
                    }
                    // 没有 id 的是事件通知（Page.loadEventFired 等），这里不关心
                }
                catch { /* 非 JSON 帧，忽略 */ }
            }
        }
    }
}
