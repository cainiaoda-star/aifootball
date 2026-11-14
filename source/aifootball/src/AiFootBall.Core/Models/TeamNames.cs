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

    /// <summary>
    /// 「去掉赞助商/俱乐部英文标记」后的核心名（2026-11-14 加）。
    ///
    /// 起因（1419319 大宫松鼠RB vs 山形山神）：竞彩把球队写成了「大宫松鼠RB」，
    /// 而库内训练样本全叫「大宫松鼠」。名字解析用的是「简称是全称的子序列」，
    /// 只允许**查询名比库内名短**；这次查询名反而更长，规则直接不触发 → 整场被判
    /// 「模型不认识」→ 没有输入凭证、没有存档、没有闸门，静默消失。
    ///
    /// 这里只回答一个问题：**两个写法是不是同一支队的名字变体**。
    /// 判据是严格相等（不是模糊相似）：去掉 ASCII 字母数字（RB / FC / SC / U23 这类标记）、
    /// 空白与分隔符之后必须一字不差。**不用于配对赛果**（那仍走 <see cref="Same"/>），
    /// 只用于「解析兜底」与「独立库检时把同一支队的历史捞全」。
    /// </summary>
    public static string CoreKey(string? name)
    {
        var key = Key(name);
        if (key.Length == 0) return "";
        var sb = new System.Text.StringBuilder(key.Length);
        foreach (var ch in key)
        {
            if (ch is >= 'a' and <= 'z') continue;      // Key 已 tolower
            if (ch is >= '0' and <= '9') continue;
            if (ch is ' ' or '　' or '\t' or '·' or '・' or '.' or '-' or '_') continue;
            sb.Append(ch);
        }
        return sb.ToString();
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
