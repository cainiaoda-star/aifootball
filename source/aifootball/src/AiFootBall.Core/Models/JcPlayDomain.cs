using System;
using System.Collections.Generic;
using System.Linq;

namespace AiFootBall.Core.Models;

/// <summary>
/// 竞彩每个玩法的**完整档位域**（2026-11-14 第八轮加）。
///
/// 为什么需要它：之前只有 `MilkPickEngine.SnapshotComplete` 里的「31/9/8/3/3」五个数字（只数档数），
/// 而发布核对是拿「源侧采到几档」当预期——源侧自己少采一档时，两边同时少，照样通过。
/// 这和 1419319 是同一类病因：**预期不能取自被检验的一方**。
///
/// 域必须是代码清单而不是档数：
///   · 只比档数，`1:0` 换成 `平/胜` 这种脏代码也照样「31 档齐」，真正缺的 `1:0` 反而看不见；
///   · 半全场生产上出现过 `平/胜`/`平/负`/`平/平` 三条脏档（3 场），
///     比档数的话它们会被当成有效档位。
///
/// 硬编码的是**竞彩规则**，不是某一场的观测值——所以「今日恒为 54 档」只作日志摘要，
/// 不作判据：判据是逐玩法 × 逐档位。
/// </summary>
public static class JcPlayDomain
{
    /// <summary>胜平负：胜/平/负（不让球）。</summary>
    public static readonly IReadOnlyList<string> Spf = new[] { "3", "1", "0" };

    /// <summary>让球胜平负：让胜/让平/让负。</summary>
    public static readonly IReadOnlyList<string> Nspf = new[] { "3", "1", "0" };

    /// <summary>总进球：0~7（7 表示 7 球及以上）。</summary>
    public static readonly IReadOnlyList<string> Jq = new[] { "0", "1", "2", "3", "4", "5", "6", "7" };

    /// <summary>半全场：9 个组合，代码形态是「半场-全场」（3=胜 1=平 0=负）。</summary>
    public static readonly IReadOnlyList<string> Bq = new[]
    {
        "3-3", "3-1", "3-0",
        "1-3", "1-1", "1-0",
        "0-3", "0-1", "0-0"
    };

    /// <summary>
    /// 比分：31 档 = 主胜 13（含「胜其它」）+ 平 5（含「平其它」）+ 客胜 13（含「负其它」）。
    /// </summary>
    public static readonly IReadOnlyList<string> Bf = new[]
    {
        "1:0", "2:0", "2:1", "3:0", "3:1", "3:2", "4:0", "4:1", "4:2", "5:0", "5:1", "5:2", "胜其它",
        "0:0", "1:1", "2:2", "3:3", "平其它",
        "0:1", "0:2", "1:2", "0:3", "1:3", "2:3", "0:4", "1:4", "2:4", "0:5", "1:5", "2:5", "负其它"
    };

    /// <summary>竞彩在售场次的五个玩法（顺序只影响日志可读性）。</summary>
    public static readonly string[] Plays =
    {
        JcPlayTypes.Spf, JcPlayTypes.Nspf, JcPlayTypes.Bq, JcPlayTypes.Jq, JcPlayTypes.Bf
    };

    /// <summary>域内档位；未知玩法返回 null（未知玩法不能拿来做「缺档」判据，也不能当通过）。</summary>
    public static IReadOnlyList<string>? Of(string? playType) => playType switch
    {
        JcPlayTypes.Spf => Spf,
        JcPlayTypes.Nspf => Nspf,
        JcPlayTypes.Bq => Bq,
        JcPlayTypes.Jq => Jq,
        JcPlayTypes.Bf => Bf,
        _ => null
    };

    /// <summary>域大小；未知玩法返回 0。</summary>
    public static int Size(string? playType) => Of(playType)?.Count ?? 0;

    /// <summary>
    /// 档位代码归一（2026-11-14 第十轮）。只做一件事：把「其它」与「其他」两种写法统一。
    ///
    /// 为什么需要：采集端的写法并不统一，500 用「胜其它」，个别源出现过「胜其他」，
    /// 而引擎的候选段（`bfPure` 的「其它」分支）本来就两种都认。
    /// 如果域判定只认「其它」，一条「胜其他」就会被当成域外脏码 —— 结果不是「多算一档」，
    /// 而是「整场比分判为不全、退回纯模型」甚至「缺档阻断发布」，把一个改名当成了缺数据。
    /// 归一放在这里而不是改写上游的 `OptionCode`：落库键、去重键、显示名都不该被这一步牵动。
    /// </summary>
    public static string Normalize(string? code)
        => string.IsNullOrEmpty(code) ? string.Empty : code.Replace("其他", "其它", StringComparison.Ordinal);

    /// <summary>
    /// 该玩法下这个档位代码是否属于**标准域**（2026-11-14 第十轮）。
    ///
    /// 完整性判断（<c>SnapshotComplete</c>）、去水归一（<c>BlendNormalizer</c>）、
    /// 计算入口（候选生成与全档存档）必须共用这一个判定：
    /// 少一处过滤，域外脏码就会参与 Σ1/赔率 把合法档的概率整体压低；
    /// 反过来，只用档数（`>= 31`）判完整，一条脏码换掉一条合法档也看不出来。
    /// </summary>
    public static bool Contains(string? playType, string? code)
    {
        if (string.IsNullOrEmpty(code)) return false;
        var domain = Of(playType);
        if (domain is null || domain.Count == 0) return false;
        var c = Normalize(code);
        foreach (var d in domain)
            if (string.Equals(d, c, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>一个玩法少了几档。样例最多 4 个代码，多的记成省略号。</summary>
    public readonly record struct OptionGap(string Play, int Missing, int DomainSize, string Sample);

    /// <summary>
    /// 按标准域列出缺档。键的形态是 <c>玩法|代码</c>。
    /// 「其他」先归一成「其它」。域外脏码不补成缺档，也不拿来抵销缺档。
    /// </summary>
    public static IReadOnlyList<OptionGap> Gaps(IEnumerable<string>? optionKeys)
    {
        var have = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (optionKeys is not null)
        {
            foreach (var key in optionKeys)
            {
                if (string.IsNullOrEmpty(key)) continue;
                var bar = key.IndexOf('|');
                if (bar <= 0) continue;
                var play = key[..bar];
                var code = Normalize(key[(bar + 1)..]);
                if (code.Length == 0) continue;
                if (!have.TryGetValue(play, out var set))
                    have[play] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(code);
            }
        }

        var gaps = new List<OptionGap>();
        foreach (var play in Plays)
        {
            var domain = Of(play)!;
            have.TryGetValue(play, out var set);
            var missing = new List<string>();
            foreach (var code in domain)
                if (set is null || !set.Contains(code)) missing.Add(code);
            if (missing.Count == 0) continue;
            var sample = string.Join(",", missing.Take(4));
            if (missing.Count > 4) sample += ",…";
            gaps.Add(new OptionGap(play, missing.Count, domain.Count, sample));
        }
        return gaps;
    }

    /// <summary>
    /// 缺档证据。第一段固定是 <c>sale-basis=unavailable</c>，后面按玩法顺序列出缺了什么。
    /// 核对器用整串相等来确认「拒绝的就是源侧这一份缺档」。
    /// </summary>
    public static string GapEvidence(IEnumerable<string>? optionKeys)
    {
        var parts = new List<string> { "sale-basis=" + MilkSourceSlate.SaleBasis };
        foreach (var gap in Gaps(optionKeys))
            parts.Add($"{gap.Play}={gap.Missing}/{gap.DomainSize}:{gap.Sample}");
        return string.Join(";", parts);
    }

    /// <summary>全五玩法的总档数（3+3+9+8+31=54）。**只用于日志与展示**，不得作为核对判据。</summary>
    public static int TotalSize
    {
        get
        {
            var n = 0;
            foreach (var p in Plays) n += Size(p);
            return n;
        }
    }
}
