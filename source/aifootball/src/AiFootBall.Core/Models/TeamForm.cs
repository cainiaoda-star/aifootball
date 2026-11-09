namespace AiFootBall.Core.Models;

/// <summary>
/// 球队近期赛程/状态（2026-11-08 加，曼城复盘的「体能项」落地）。
///
/// 起因：11-07 诺丁汉森林 3:1 曼城，事前可说的理由只有「曼城三连败 + 刚打完欧战、只歇 69 小时」。
/// 但把这两项拿去回测（2025-08 起，只取热门方赔率 ≤2.50 的场次）结论是**反的**：
///
///   分组                      n     实际兑现   市场隐含   偏离
///   欧战后 + 三连败            3     66.7%     53.9%    +12.8
///   欧战后 96 小时内          61     63.9%     59.1%     +4.8
///   欧战后休息充足            12     58.3%     63.8%     −5.4
///   三连败（非欧战）          15     53.3%     47.0%     +6.3
///   其余                    394     54.1%     53.4%     +0.6
///
/// 也就是说「累了的强队会输」在数据上不成立，市场反而是**低估**了这类强队。
/// 所以这类信息只做标注、不做概率扣分（见 <see cref="MilkPick.FatigueTag"/>）。
/// 样本（3~61 场）还远不够下结论，等积累后重算再决定要不要真扣。
///
/// ⚠ 曼城那场真正能事前拦住的是**价格背离**（竞彩客胜 1.60→1.43 即 −10.6%，必发只动 −1.1%），
/// 那条已在 <c>PriceDivergence</c> 上线。本类不是它的替代品。
/// </summary>
public sealed class TeamForm
{
    public string TeamName { get; init; } = "";

    /// <summary>本场之前最近一场完赛的时间；null = 库里没有更早的比赛（新队/升班马/数据缺口）。</summary>
    public DateTime? LastMatchTime { get; init; }

    /// <summary>最近一场的联赛名，用于判「上一场是不是欧战/亚冠」。</summary>
    public string? LastLeague { get; init; }

    /// <summary>最近 3 场结果（+1 胜 / 0 平 / −1 负），[0] 为最近一场；不足 3 场时为 null。</summary>
    public int? Last1 { get; init; }
    public int? Last2 { get; init; }
    public int? Last3 { get; init; }

    /// <summary>距离本场开赛的休息小时数。取不到上一场时返回 null（调用方不要当成 0）。</summary>
    public int? RestHours(DateTime matchTime)
        => LastMatchTime is DateTime t ? (int)Math.Round((matchTime - t).TotalHours) : null;

    /// <summary>三连败。样本不足 3 场时为 false——宁可漏标，也不要把「不知道」标成「是」。</summary>
    public bool ThreeLossStreak => Last1 == -1 && Last2 == -1 && Last3 == -1;

    /// <summary>上一场是洲际赛事（欧冠/欧罗巴/欧协联/亚冠/解放者杯）。</summary>
    public bool PlayedContinentalLast => IsContinentalLeague(LastLeague);

    /// <summary>
    /// 联赛名是否属洲际赛事。用「包含」而不是等值：库里的写法有
    /// 「欧冠」「欧洲冠军联赛」「欧罗巴」「欧罗巴联赛」「欧协联」「亚冠」「亚冠精英」等多种。
    /// </summary>
    public static bool IsContinentalLeague(string? league)
    {
        if (string.IsNullOrWhiteSpace(league)) return false;
        var s = league.Trim();
        return s.Contains("欧冠", StringComparison.Ordinal)
            || s.Contains("欧洲冠军", StringComparison.Ordinal)
            || s.Contains("欧罗巴", StringComparison.Ordinal)
            || s.Contains("欧联", StringComparison.Ordinal)
            || s.Contains("欧协", StringComparison.Ordinal)
            || s.Contains("亚冠", StringComparison.Ordinal)
            || s.Contains("解放者", StringComparison.Ordinal)
            || s.Contains("南美杯", StringComparison.Ordinal);
    }
}
