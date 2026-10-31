using System.Collections.Concurrent;
using AiFootBall.Core.Models;

namespace AiFootBall.Api;

/// <summary>简单登录会话管理：登录成功发 token，内存存储，带过期。</summary>
public sealed class AuthService
{
    private sealed record Session(string UserName, string NickName, DateTime ExpiresAt);

    private static readonly ConcurrentDictionary<string, Session> Sessions = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    public string Issue(UserAccount user)
    {
        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        Sessions[token] = new Session(user.UserName, user.NickName ?? user.UserName, DateTime.Now.Add(Ttl));
        return token;
    }

    public bool TryGet(string token, out string userName, out string nickName)
    {
        userName = nickName = "";
        if (string.IsNullOrWhiteSpace(token)) return false;
        if (!Sessions.TryGetValue(token, out var s)) return false;
        if (s.ExpiresAt < DateTime.Now) { Sessions.TryRemove(token, out _); return false; }
        userName = s.UserName;
        nickName = s.NickName;
        return true;
    }

    public void Revoke(string token) => Sessions.TryRemove(token, out _);
}
