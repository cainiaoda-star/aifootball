namespace AiFootBall.Core.Models;

/// <summary>
/// 队名对齐用的键。只去掉空白和俱乐部后缀，不把「曼联」猜成「曼彻斯特联」。
/// </summary>
public static class TeamNames
{
    public static string Key(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";

        // 先过别名表（2026-11-08）：竞彩写「索尔纳」、数据源写「AIK索尔纳」时，
        // 这层键不一致会让同一场球配不上比分、同一支球队被拆成两队统计。
        var s = TeamAliases.Canonical(name).ToLowerInvariant();
        s = s.Replace(" ", "").Replace("　", "").Replace("\t", "");
        s = s.Replace(".", "").Replace("·", "").Replace("・", "");

        string[] suffixes = ["足球俱乐部", "俱乐部", "fc", "队"];
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var suffix in suffixes)
            {
                if (s.Length > suffix.Length && s.EndsWith(suffix, StringComparison.Ordinal))
                {
                    s = s[..^suffix.Length];
                    changed = true;
                }
            }
        }

        return s;
    }

    public static bool Same(string? left, string? right)
    {
        var a = Key(left);
        var b = Key(right);
        return a.Length >= 2 && a == b;
    }

    public static string PairKey(string? home, string? away)
    {
        var h = Key(home);
        var a = Key(away);
        if (h.Length < 2 || a.Length < 2) return "";
        return h + "|" + a;
    }

    /// <summary>给 SQL LIKE 用的包含模式。队名里的通配符按字面量转义。</summary>
    public static string ContainsPattern(string key)
    {
        var escaped = key.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
        return "%" + escaped + "%";
    }
}
