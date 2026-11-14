using System;
using System.Collections.Generic;

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
