using System.Diagnostics;
using System.Text;

namespace AiFootBall.Sources;

/// <summary>
/// 500.com 人机验证 token 自动续期。
///
/// 背景：站点把门槛从"纯 JS 计算挑战"升级为"JS 挑战 + EdgeOne 交互式人机验证"。
/// 第二级纯算法解不出来，只能靠真实浏览器指纹通过后种下的 EO-Bot-Captcha-Token。
/// 实测这个 token 的寿命不由日历决定，而由请求量决定——几千次请求后就可能被降级，
/// 表现为：带 token 也只能拿到 2 KB 的 "Security Verification" 页面。
///
/// 对策：检测到连续被拦时，自动用本机 Chrome 重取一次（_probe/harvest.mjs），
/// 落盘后交由 BotGuard.LoadPersisted 热装载。全程不重启进程、不中断正在跑的作业。
/// 有节流（最短间隔 &amp; 每小时上限）与并发锁，避免把站点和本机 CPU 顶死。
/// </summary>
public static class TokenRefresher
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object Sync = new();
    private static readonly Queue<string> History = new();

    /// <summary>两次自动续期之间的最小间隔（分钟）。过密会连续弹浏览器窗口且无意义。</summary>
    private const int MinIntervalMinutes = 8;

    /// <summary>每小时最多自动续期次数。超过说明是 IP 级封禁，续 token 也没用，需要人工介入。</summary>
    private const int MaxPerHour = 4;

    /// <summary>单次续期最长等待（秒）。超时即杀掉浏览器进程，避免卡死。</summary>
    private const int RunTimeoutSeconds = 150;

    private static string? _cookiePath;
    private static string? _scriptPath;
    private static string _node = "node";
    private static DateTime _lastAttempt = DateTime.MinValue;
    private static DateTime _lastSuccess = DateTime.MinValue;
    private static DateTime _hourStart = DateTime.MinValue;
    private static int _hourCount;
    private static string? _lastError;
    private static string? _lastMessage;
    private static string? _runningReason;

    /// <summary>是否具备自动续期条件（找得到脚本与 token 文件）</summary>
    public static bool Enabled => _scriptPath is not null && _cookiePath is not null;

    public static bool IsRunning => Volatile.Read(ref _runningReason) is not null;

    public static string? ScriptPath => _scriptPath;

    public static string? CookiePath => _cookiePath;

    /// <summary>
    /// 最近一次「脚本跑完 + Cookie 装载成功」的时刻。
    ///
    /// ★ 这只证明续期流程走通了，**不证明新 token 能用**。11-09 全天报了 41 次
    /// 「token 续期成功」（每 8 分钟一次，凌晨 00:00~07:35 从未断过），而
    /// FbOddsHistory 当天首条仍等到 08:05——脚本每次都在写 cookie，站点每次都拒。
    /// 要判断 token 是否真有效，只能看它装载之后还有没有被拦（见 TokenHealthService）。
    /// </summary>
    public static DateTime LastSuccessAt => _lastSuccess;

    /// <summary>
    /// 是否处于最小间隔冷却中。调用方应当先看这个再决定要不要调 TryRefreshAsync：
    /// 原来每 30 秒就白试一次，一天刷出 913 行「累计 3 次拦截，开始自动续期」，
    /// 其中 492 行被每小时上限挡回、其余被冷却挡回，纯空转还淹掉真日志。
    /// </summary>
    public static bool IsCoolingDown =>
        _lastAttempt != DateTime.MinValue &&
        (DateTime.Now - _lastAttempt).TotalMinutes < MinIntervalMinutes;

    /// <summary>
    /// 续期「假成功」判定：已经装过新 cookie，之后又被拦够一整轮。
    ///
    /// 为什么必须单独成一个可测函数：这个判据写错，两种后果都很糟——
    /// 阈值太低，每隔几分钟就喊一次「需人工」，人会被训练成不看告警；
    /// 阈值太高或条件写反，就是 11-09 那种整夜 41 次「成功」而数据一个字没进。
    ///
    /// <paramref name="successAt"/> 为 <see cref="DateTime.MinValue"/>（本次进程还从未续期成功）
    /// 时一律判 false：没有「续期之后」这个基准，就无从谈「续期无效」。
    /// 用 <c>Math.Max(0, ...)</c> 兜住进程重启后 StatVerify 归零导致差值为负的情况。
    /// </summary>
    public static bool RefreshProvenIneffective(DateTime successAt, long verifyNow,
        long verifyAtSuccess, long threshold)
        => successAt != DateTime.MinValue
           && Math.Max(0, verifyNow - verifyAtSuccess) >= threshold;

    /// <summary>
    /// 配置续期器。<paramref name="cookiePath"/> 是 secrets/500-cookie.txt，
    /// 脚本默认取仓库根的 _probe/harvest.mjs。
    /// </summary>
    public static void Configure(string? cookiePath, string? scriptPath = null)
    {
        _cookiePath = cookiePath;
        if (scriptPath is null && cookiePath is not null)
        {
            // <root>/secrets/500-cookie.txt → <root>/_probe/harvest.mjs
            var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(cookiePath)));
            if (root is not null) scriptPath = Path.Combine(root, "_probe", "harvest.mjs");
        }

        _scriptPath = scriptPath is not null && File.Exists(scriptPath) ? scriptPath : null;
        _node = ResolveNode();
    }

    /// <summary>状态快照，供 /api/token/status 与日志使用</summary>
    public static object Snapshot()
    {
        lock (Sync)
        {
            var recent = History.ToArray();
            return new
            {
                enabled = Enabled,
                running = IsRunning,
                node = _node,
                script = _scriptPath,
                cookie = _cookiePath,
                lastAttempt = _lastAttempt == DateTime.MinValue ? (DateTime?)null : _lastAttempt,
                lastSuccess = _lastSuccess == DateTime.MinValue ? (DateTime?)null : _lastSuccess,
                lastMessage = _lastMessage,
                lastError = _lastError,
                recent
            };
        }
    }

    /// <summary>
    /// 执行一次续期。返回 (是否成功, 说明)。
    /// 已在执行、处于冷却期或超过每小时上限时直接返回 false 并给出原因（不抛异常）。
    /// </summary>
    public static async Task<(bool Ok, string Message)> TryRefreshAsync(string reason,
        Action<string>? log = null, CancellationToken ct = default, bool force = false)
    {
        if (!Enabled) return (false, "未配置续期脚本（找不到 _probe/harvest.mjs）");

        if (!await Gate.WaitAsync(0, ct))
            return (false, "已有一个续期任务在执行");

        try
        {
            var now = DateTime.Now;
            // force = 人工主动触发：绕过冷却与每小时上限（自动续期仍受限制，避免频繁弹窗）
            if (!force && _lastAttempt != DateTime.MinValue && (now - _lastAttempt).TotalMinutes < MinIntervalMinutes)
                return (false, $"冷却中（{MinIntervalMinutes} 分钟间隔，上次 {_lastAttempt:HH:mm:ss}）");

            if ((now - _hourStart).TotalHours >= 1) { _hourStart = now; _hourCount = 0; }
            if (!force && _hourCount >= MaxPerHour)
                return (false, $"超过每小时上限 {MaxPerHour} 次，判定为 IP 级限制，需人工检查");

            _lastAttempt = now;
            _hourCount++;
            Volatile.Write(ref _runningReason, reason);
            Note($"开始续期（触发原因：{reason}）");

            var psi = new ProcessStartInfo(_node)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add(_scriptPath!);
            psi.ArgumentList.Add(ProbeUrl);
            psi.ArgumentList.Add(_cookiePath!);
            psi.ArgumentList.Add("110");

            var before = File.Exists(_cookiePath) ? File.GetLastWriteTimeUtc(_cookiePath) : DateTime.MinValue;

            using var proc = new Process { StartInfo = psi };
            var output = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (Sync) output.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (Sync) output.AppendLine("[err] " + e.Data); };

            if (!proc.Start())
            {
                _lastError = "无法启动 node 进程";
                Note(_lastError);
                return (false, _lastError);
            }

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(RunTimeoutSeconds));

            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
                _lastError = $"续期超时（{RunTimeoutSeconds}s），已终止浏览器进程";
                Note(_lastError);
                log?.Invoke(_lastError);
                return (false, _lastError);
            }

            var after = File.Exists(_cookiePath) ? File.GetLastWriteTimeUtc(_cookiePath) : DateTime.MinValue;
            string text;
            lock (Sync) text = output.ToString();

            if (after <= before)
            {
                _lastError = $"脚本未产出新 token（exit={proc.ExitCode}）";
                Note(_lastError);
                Note(Tail(text));
                log?.Invoke(_lastError + " " + Tail(text));
                return (false, _lastError);
            }

            // 立刻热装载：不等下一次 RefreshIfChanged 的 5 秒节流
            var loaded = BotGuard.LoadPersisted(_cookiePath);
            _lastSuccess = DateTime.Now;
            _lastError = null;
            _lastMessage = $"已装载 {loaded} 项 Cookie（{_lastSuccess:HH:mm:ss}）";
            Note(_lastMessage);
            log?.Invoke("token 续期成功：" + _lastMessage);
            return (true, _lastMessage);
        }
        catch (Exception ex)
        {
            _lastError = ex.GetType().Name + ": " + ex.Message;
            Note(_lastError);
            log?.Invoke("token 续期异常：" + _lastError);
            return (false, _lastError);
        }
        finally
        {
            Volatile.Write(ref _runningReason, null);
            Gate.Release();
        }
    }

    /// <summary>续期时打开的探针页。用固定的一场历史赛事即可——盘口页本身是长期有效的。</summary>
    public static string ProbeUrl { get; set; } = "https://odds.500.com/fenxi/ouzhi-1364148.shtml";

    private static void Note(string message)
    {
        var line = $"{DateTime.Now:MM-dd HH:mm:ss} {message}";
        lock (Sync)
        {
            History.Enqueue(line);
            while (History.Count > 20) History.Dequeue();
        }
    }

    private static string Tail(string text)
    {
        var s = text.Trim();
        if (s.Length == 0) return "";
        return s.Length <= 300 ? s : s[^300..];
    }

    private static string ResolveNode()
    {
        // 优先 PATH 里的 node；找不到再试常见安装位置
        try
        {
            var psi = new ProcessStartInfo("where.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("node");
            using var p = Process.Start(psi);
            if (p is not null)
            {
                var first = p.StandardOutput.ReadLine();
                p.WaitForExit(3000);
                if (!string.IsNullOrWhiteSpace(first) && File.Exists(first.Trim()))
                    return first.Trim();
            }
        }
        catch { /* 忽略，走兜底 */ }

        foreach (var candidate in new[]
                 {
                     @"C:\Program Files\nodejs\node.exe",
                     @"C:\Program Files (x86)\nodejs\node.exe",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         @"Programs\nodejs\node.exe")
                 })
        {
            if (File.Exists(candidate)) return candidate;
        }

        return "node";
    }
}
