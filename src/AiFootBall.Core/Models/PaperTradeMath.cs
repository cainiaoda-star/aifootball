namespace AiFootBall.Core.Models;

/// <summary>
/// 空跑台账的算法（2026-11-08 加）。抽成纯函数，只为了能被测试钉住——
/// 这套口径以后会天天被引用，一旦被改错，两周边际结论全部作废且没人看得出来。
///
/// 三条硬口径：
///   1. 分母 = 已结算 **且未撤注** 的注数。撤注 = 没下这笔，不能算「没中」。
///   2. 回报 = 命中注的赔率合计 ÷ 注数（平注 1 个单位），不是赔率×概率的期望值。
///   3. 撤注单独计数，不进分母也不进分子——它评价的是撤注判据，不是选注判据。
/// </summary>
public static class PaperTradeMath
{
    /// <summary>平注盈亏：撤注没下注（NULL），命中赚 赔率−1，未中亏 1。</summary>
    public static decimal? PnlOf(bool voided, bool? hit, decimal odds)
    {
        if (voided) return null;
        if (hit is null) return null;
        return hit.Value ? odds - 1m : -1m;
    }

    public static PaperSlice Slice(string key, IReadOnlyList<PaperTradeRow> rows)
    {
        var settled = rows.Where(r => !r.Voided && r.Hit is not null).ToList();
        var hits = settled.Count(r => r.Hit == true);
        var stake = settled.Count;                                  // 平注 1 个单位
        var ret = settled.Where(r => r.Hit == true).Sum(r => r.Odds);
        return new PaperSlice(
            key,
            rows.Count,
            rows.Count(r => r.Voided),
            settled.Count,
            hits,
            settled.Count == 0 ? 0m : Math.Round(hits * 100m / settled.Count, 2),
            stake,
            ret,
            stake == 0 ? 0m : Math.Round(ret / stake, 4));
    }

    /// <summary>样本够不够下结论：已结算未撤注 ≥ 150 注（用户 2026-11-08 拍板）。</summary>
    public const int EnoughSettled = 150;
}
