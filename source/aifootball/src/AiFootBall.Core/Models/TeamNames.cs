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

    /// <summary>
    /// 青年队 / 后备队 / B 队 / 女足这类附属队标记（没命中返回空串）。2026-11-14 第九轮搬到这里，
    /// 因为**名字解析**与**历史样本计数**必须用同一份判定——解析时把一线队与梯队分开、
    /// 计数时又合在一起，等于同一支队有两套历史。
    ///
    /// 返回具体标记而不只是 true/false：「谢周三U21」与「谢周三U19」是两支不同的队，
    /// 只判「都是梯队」会把它们混起来。
    /// </summary>
    public static string ReserveMark(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        foreach (var mark in ReserveMarks)
            if (name.Contains(mark, StringComparison.Ordinal)) return mark;
        return "";
    }

    /// <summary>队名里的 ASCII 记号集合（小写），如 `大宫松鼠RB` → {rb}、`谢周三U21` → {u21}。</summary>
    public static HashSet<string> AsciiMarks(string? name)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(name)) return set;
        var sb = new System.Text.StringBuilder(4);
        foreach (var ch in name)
        {
            if (ch is >= 'A' and <= 'Z') { sb.Append((char)(ch + 32)); continue; }
            if (ch is >= 'a' and <= 'z') { sb.Append(ch); continue; }
            if (ch is >= '0' and <= '9') { sb.Append(ch); continue; }
            if (sb.Length > 0) { set.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length > 0) set.Add(sb.ToString());
        return set;
    }

    /// <summary>
    /// **解析身份**：`candidate`（库内写法）能否被当作 `query`（竞彩写法）的同一支队。
    ///
    /// 两条闸，方向不能反：
    ///   ① 梯队标记必须相同 —— 一线队查询绝不受理梯队候选，U21 与 U19 也不互配；
    ///   ② 候选的 ASCII 记号必须是查询的子集 —— 查询多 `RB`（冠名后缀）允许，
    ///      候选多 `U21`/`II`/`JFV`（另一支队）拒绝。
    /// 核心名「全库唯一」只保证不猜，不保证对（第七轮踩过：不来梅 → JFV不来梅U19）。
    /// </summary>
    public static bool SameResolveIdentity(string? query, string? candidate)
    {
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(candidate)) return false;
        if (!string.Equals(ReserveMark(query), ReserveMark(candidate), StringComparison.Ordinal)) return false;
        return AsciiMarks(candidate).IsSubsetOf(AsciiMarks(query));
    }

    /// <summary>
    /// **计数身份**：`candidate` 的历史是否该算进 `query` 的样本量。
    ///
    /// 与 <see cref="SameResolveIdentity"/> 只差一处：ASCII 记号允许**互相包含**。
    /// 理由：库内同一支队会有「大宫松鼠」与「大宫松鼠RB」两种写法，谁多谁少取决于当年从哪个源入库；
    /// 计数是为了「这支队到底踢过几场」，任一方向包含都该合并。梯队那道闸不变——
    /// `JFV不来梅U19` 的 ReserveMark 是 `U19`，永远不会被算进 `不来梅`。
    /// </summary>
    public static bool SameCountIdentity(string? query, string? candidate)
    {
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(candidate)) return false;
        if (!string.Equals(ReserveMark(query), ReserveMark(candidate), StringComparison.Ordinal)) return false;
        var q = AsciiMarks(query);
        var c = AsciiMarks(candidate);
        return c.IsSubsetOf(q) || q.IsSubsetOf(c);
    }

    /// <summary>附属队（青年队 / 后备队 / B 队 / 女足）。它们和一线队是两套数据，不能混。</summary>
    public static bool IsReserveOrYouth(string? name) => ReserveMark(name).Length > 0;

    private static readonly string[] ReserveMarks =
        { "青年队", "后备队", "预备队", "B队", "U19", "U20", "U21", "U23", "女足", "女子" };

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
