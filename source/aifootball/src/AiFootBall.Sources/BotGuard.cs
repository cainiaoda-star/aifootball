using System.Net;
using System.Text.RegularExpressions;

namespace AiFootBall.Sources;

/// <summary>
/// 500.com 反爬挑战（TencentEdgeOne Bot 防护）求解器。
/// 命中阈值后站点返回约 988 字节的 JS 挑战页，要求客户端写入：
///   __tst_status=&lt;三个随机数之和&gt;#  、 EO_Bot_Ssid=&lt;随机加数&gt;
/// 然后 1.2 秒后重载。两个值都能从挑战页正文直接算出，无需浏览器。
/// </summary>
public static class BotGuard
{
    private static readonly Regex AddendRegex = new(@"\]\(t,(\d+)\)", RegexOptions.Compiled);
    private static readonly Regex ObjectRegex = new(@"var e=\{(.*?)\},t=0", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex NumberRegex = new(@":(\d{6,})", RegexOptions.Compiled);

    /// <summary>观测到的固定校验值（解析失败时的兜底）</summary>
    private const long FallbackStatus = 2406177862L;

    private static readonly object Gate = new();
    private static DateTime _lastSolved = DateTime.MinValue;

    /// <summary>共享 Cookie 容器（由 HttpClientHandler 使用）</summary>
    public static CookieContainer Cookies { get; } = new();

    /// <summary>
    /// 从文件装载人工/浏览器验证后取得的 Cookie（当前必需的是 EO-Bot-Captcha-Token）。
    /// 站点已从"纯 JS 计算挑战"升级为"JS 挑战 + 交互式人机验证"两级，
    /// 只有第二级通过后种下的 token 才能拿到真实页面，纯算法解不出来。
    /// 返回实际装载的条目数。
    /// </summary>
    public static int LoadPersisted(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return 0;

        _cookiePath = path;
        _cookieMtime = File.GetLastWriteTimeUtc(path);
        _lastCheck = DateTime.Now;

        var uri = new Uri("https://odds.500.com/");
        var loaded = 0;

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var sep = line.IndexOf('=');
            if (sep <= 0) continue;

            var key = line[..sep].Trim();
            var value = line[(sep + 1)..].Trim();
            if (value.Length == 0) continue;

            if (key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
            {
                // token 与 UA 绑定，必须用同一个 UA 发请求
                UserAgent = value;
                continue;
            }

            lock (Gate)
            {
                Cookies.SetCookies(uri, $"{key}={value}; path=/");
                loaded++;
            }
        }

        return loaded;
    }

    private static string? _cookiePath;
    private static DateTime _cookieMtime = DateTime.MinValue;
    private static DateTime _lastCheck = DateTime.MinValue;

    /// <summary>
    /// 检查 token 文件是否被替换过，变了就重新装载。
    /// 站点在访问量上来后会把 token 打回"需要重新验证"，续期只能换新 token；
    /// 热更新可以做到"换文件即生效"，不用重启进程、不打断正在跑的作业。
    /// </summary>
    public static bool RefreshIfChanged()
    {
        var path = _cookiePath;
        if (path is null) return false;

        var now = DateTime.Now;
        if ((now - _lastCheck).TotalSeconds < 5) return false;
        _lastCheck = now;

        try
        {
            if (!File.Exists(path)) return false;
            var mtime = File.GetLastWriteTimeUtc(path);
            if (mtime == _cookieMtime) return false;
            _cookieMtime = mtime;   // 先更新，避免并发重复装载
            LoadPersisted(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>与已装载 token 配套的 UA（token 绑指纹，不能随便换）</summary>
    public static string? UserAgent { get; private set; }

    public static bool IsChallenge(string html) =>
        html.Length < 2500 && html.Contains("__tst_status", StringComparison.Ordinal);

    /// <summary>
    /// 第二级人机验证页。特征是极小体积 + "正在验证连接安全性"/TEOCaptcha。
    /// 这类响应以前会被当成"这场比赛没有盘口"，导致日志显示失败 0、赔率行 0 的假象。
    /// </summary>
    public static bool IsVerificationPage(string html) =>
        html.Length < 6000
        && (html.Contains("正在验证连接安全性", StringComparison.Ordinal)
            || html.Contains("TEOCaptcha", StringComparison.Ordinal)
            || html.Contains("Security Verification", StringComparison.Ordinal));

    /// <summary>解析挑战页并写入 Cookie，返回是否成功。</summary>
    public static bool Solve(string html)
    {
        var addendMatch = AddendRegex.Match(html);
        if (!addendMatch.Success) return false;

        var addend = addendMatch.Groups[1].Value;

        long status = FallbackStatus;
        var obj = ObjectRegex.Match(html);
        if (obj.Success)
        {
            var nums = NumberRegex.Matches(obj.Groups[1].Value)
                .Select(m => long.TryParse(m.Groups[1].Value, out var v) ? v : 0)
                .Where(v => v > 0)
                .ToList();
            if (nums.Count >= 3) status = nums.Take(3).Sum();
        }

        var uri = new Uri("https://odds.500.com/");
        lock (Gate)
        {
            Cookies.SetCookies(uri, $"__tst_status={status}; path=/");
            Cookies.SetCookies(uri, $"EO_Bot_Ssid={addend}; path=/");
            _lastSolved = DateTime.Now;
        }
        return true;
    }

    /// <summary>求解后需等待页面要求的 1.2 秒（并发场景下只等一次）</summary>
    public static async Task WaitAsync(CancellationToken ct = default)
    {
        TimeSpan delay;
        lock (Gate)
        {
            var elapsed = DateTime.Now - _lastSolved;
            delay = TimeSpan.FromMilliseconds(1400) - elapsed;
        }
        if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
    }
}
