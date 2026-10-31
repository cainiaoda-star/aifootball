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

    public static bool IsChallenge(string html) =>
        html.Length < 2500 && html.Contains("__tst_status", StringComparison.Ordinal);

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
