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
    /// **队名身份**：`candidate`（库内写法）是否属于 `query`（竞彩写法）这支球队。
    /// 队名解析与历史样本计数**共用这一个函数**（2026-11-14 第十轮）。
    ///
    /// 判据只有一条：**梯队层次必须相同**（见 <see cref="HierarchyMark"/>）。
    /// 一线队 (`""`) 绝不接受 `U21`/`II`/`B`/`女足`/`二队` 这些候选——那是另一支队；
    /// `U21` 与 `U19` 也不互配。
    ///
    /// 为什么不再用「候选的 ASCII 记号必须是查询的子集」（第九、十轮之交的写法）：
    /// 那会把「多一个俱乐部前缀」也当成另一支队，实测误杀两组真匹配——
    /// `埃因霍温 → PSV埃因霍温`（库内 147 场）、`辛辛那提 → FC辛辛那提`（75 场）。
    /// 两种情况的机器可辨差别不是「有没有字母」，而是「字母是不是梯队层次」：
    /// `PSV`/`FC`/`RB`/`SC` 是俱乐部名的一部分，`U18`/`II`/`B` 是另一支队的标记。
    /// 「查询名是某个带前缀写法的一部分、但库内还有别的队也叫这个名字」（`米兰` → `国际米兰`/`AC米兰`）
    /// 属于**歧义**，由 <c>ScorelineModel.Resolve</c> 的歧义判定处理，不该在这一层判死。
    /// </summary>
    public static bool SameIdentity(string? query, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(candidate)) return false;

        // 先过别名表：`蔚山HD` 与 `蔚山现代` 是同一支队的两种写法，别名表已经登记过。
        var q = TeamAliases.Canonical(query);
        var c = TeamAliases.Canonical(candidate);

        return string.Equals(HierarchyMark(q), HierarchyMark(c), StringComparison.Ordinal);
    }

    /// <summary>
    /// 梯队层次标记（没命中返回空串）。比 <see cref="ReserveMark"/> 多认两类写法：
    ///   · 年龄段 ASCII 记号 `U15`~`U23`；
    ///   · 罗马数字 `II`/`III`/`IV` 与单独的 `B`（库里 `BKMA II`、`帕丘卡III`、`马拉加 B` 都是二队）。
    ///
    /// 为什么必须多认：`IsReserveOrYouth` 原来只认中文与 U19/U20/U21/U23，
    /// 于是 `曼联U18`、`BKMA II`、`帕丘卡III` 在「一线队查询」眼里都是**合法候选**——
    /// 这两类合计在近 18 个月样本里数以千计，拿它们出票就是拿二队的战绩算一线队。
    /// </summary>
    public static string HierarchyMark(string? name)
    {
        var rm = ReserveMark(name);
        if (rm.Length > 0) return rm;
        if (string.IsNullOrEmpty(name)) return "";

        foreach (var token in AsciiMarks(name))
        {
            if (token.Length == 3 && token[0] == 'u'
                && token[1] is >= '1' and <= '2' && token[2] is >= '5' and <= '9'
                && (token[1] == '2' ? token[2] <= '3' : true))
                return token;
            if (token is "ii" or "iii" or "iv" or "b") return token;
        }
        return "";
    }

    /// <summary>附属队（青年队 / 后备队 / B 队 / 二队 / 女足 / 各年龄段）。它们和一线队是两套数据，不能混。</summary>
    public static bool IsReserveOrYouth(string? name) => HierarchyMark(name).Length > 0;

    private static readonly string[] ReserveMarks =
    {
        "青年队", "后备队", "预备队", "二队", "B队", "女足", "女子",
        "U15", "U16", "U17", "U18", "U19", "U20", "U21", "U22", "U23"
    };

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
