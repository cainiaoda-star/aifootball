using System.Collections.Concurrent;
using System.Text.Json;
using AiFootBall.Core.Models;

namespace AiFootBall.Api;

/// <summary>
/// 简单登录会话管理：登录成功发 token，带过期。
/// 2026-11-07 起持久化到磁盘：纯内存存储时，每次重启服务都会把所有登录态清空，
/// 页面集体 401 变空白（当日事故：两次重启后主接口全部 401）。改为每次变更落盘、启动时加载。
/// </summary>
public sealed class AuthService
{
    private sealed record Session(string UserName, string NickName, DateTime ExpiresAt);

    private static readonly ConcurrentDictionary<string, Session> Sessions = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private static readonly object SaveLock = new();

    private static readonly string StorePath = ResolveStorePath();

    private static string ResolveStorePath()
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "auth-sessions.json");
        }
        catch { return Path.Combine(Path.GetTempPath(), "aifootball-auth-sessions.json"); }
    }

    static AuthService()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, Session>>(File.ReadAllText(StorePath));
                if (loaded != null)
                    foreach (var (token, s) in loaded)
                        if (s.ExpiresAt > DateTime.Now)
                            Sessions[token] = s;
            }
        }
        catch { /* 会话文件损坏不阻塞启动，重新登录即可 */ }
    }

    private static void Save()
    {
        try
        {
            var live = Sessions.Where(kv => kv.Value.ExpiresAt > DateTime.Now)
                               .ToDictionary(kv => kv.Key, kv => kv.Value);
            lock (SaveLock)
                File.WriteAllText(StorePath, JsonSerializer.Serialize(live));
        }
        catch { /* 落盘失败不阻塞登录，最坏情况是下次重启要重登 */ }
    }

    public string Issue(UserAccount user)
    {
        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        Sessions[token] = new Session(user.UserName, user.NickName ?? user.UserName, DateTime.Now.Add(Ttl));
        Save();
        return token;
    }

    public bool TryGet(string token, out string userName, out string nickName)
    {
        userName = nickName = "";
        if (string.IsNullOrWhiteSpace(token)) return false;
        if (!Sessions.TryGetValue(token, out var s)) return false;
        if (s.ExpiresAt < DateTime.Now) { Sessions.TryRemove(token, out _); Save(); return false; }
        userName = s.UserName;
        nickName = s.NickName;
        return true;
    }

    public void Revoke(string token)
    {
        if (Sessions.TryRemove(token, out _)) Save();
    }
}
