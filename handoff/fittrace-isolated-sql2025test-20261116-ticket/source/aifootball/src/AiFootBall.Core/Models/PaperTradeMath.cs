namespace AiFootBall.Core.Models;

/// <summary>
/// 空跑台账的算法（2026-11-08 加）。抽成纯函数，只为了能被测试钉住——
/// 这套口径以后会天天被引用，一旦被改错，两周边际结论全部作废且没人看得出来。
///
/// 三条硬口径：
///   1. 分母 = 已结算 **且未撤注** 的注数。撤注 = 没下这笔，不能算「没中」。
///   2. 回报 = 命中注的赔率合计 ÷ 注数（平注 1 个单位），不是赔率×概率的期望值。
///   3. 撤注单独计数，不进分母也不进分子——它评价的是撤注判据，不是选注判据。
/// 第四条（2026-11-09 用户拍板）：
///   4. 同一场同玩法出现多行 = 中途换档，**只算最后发布的那一档**，旧档不算一注。
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

    /// <summary>
    /// 「以发布的为准」：同一场同玩法在台账里可能留下多行 —— 早档先上榜被冻结，随后一轮重算
    /// 把它换成另一档（引擎是整行 DELETE 再 INSERT，台账只增不改，于是两档都留了下来）。
    /// **用户 2026-11-09 拍板：被换掉的旧档不算一注**，只算最后发布的那一档。
    ///
    /// 判据必须是 FrozenAt 最晚（并列取 TradeId 大），不能写成「未撤注优先」：
    ///   · 换档的旧档 Voided 恒为 0（引擎是删行，不留撤注标记），按未撤注优先会挑中旧档；
    ///   · 终版真被撤注的注，Voided=1 且就是最新一行，仍算撤注，语义不变。
    ///
    /// 不在这里删表里的行：台账只增不改，换档轨迹要留着复盘「换对了没有」。
    /// </summary>
    public static IReadOnlyList<PaperTradeRow> FinalVersion(IReadOnlyList<PaperTradeRow> rows)
    {
        if (rows.Count <= 1) return rows;

        return rows
            .GroupBy(r => (Day: r.TradeDate.Date, r.MatchId, r.PlayType))
            .Select(g => g.OrderByDescending(r => r.FrozenAt)
                          .ThenByDescending(r => r.TradeId)
                          .First())
            .ToList();
    }

    /// <summary>被换掉的旧档注数（同场同玩法里除终版之外的注），只用于对账展示。</summary>
    public static int SupersededCount(IReadOnlyList<PaperTradeRow> rows)
        => rows.Count - FinalVersion(rows).Count;

    public static PaperSlice Slice(string key, IReadOnlyList<PaperTradeRow> rows)
    {
        var settled = rows.Where(r => !r.Voided && r.Hit is not null).ToList();
        var hits = settled.Where(r => r.Hit == true).ToList();
        var stake = settled.Count;                                  // 平注 1 个单位
        var ret = hits.Sum(r => r.Odds);

        // 终版价口径（2026-11-10）：分子只累加「有终版价」的命中注，分母仍是全部已结算注数——
        // 缺终版价的是数据缺口，不是没中，把它们从分母剔掉会虚抬回报。
        // 覆盖率因此单列出来，低于 95% 时这个回报只能当参考。
        var finalHits = hits.Count(r => r.FinalOdds is not null);
        var retFinal = hits.Sum(r => r.FinalOdds ?? 0m);

        // 撤注对照组：撤掉的注也照常结算、照常写 Hit（SettleAsync 里 pending 不排除 Voided），
        // 只是不算钱。把它们单独数出来，「撤注判得对不对」才有分母。
        var voidedSettled = rows.Where(r => r.Voided && r.Hit is not null).ToList();
        var voidedHits = voidedSettled.Count(r => r.Hit == true);

        return new PaperSlice(
            key,
            rows.Count,
            rows.Count(r => r.Voided),
            settled.Count,
            hits.Count,
            settled.Count == 0 ? 0m : Math.Round(hits.Count * 100m / settled.Count, 2),
            stake,
            ret,
            stake == 0 ? 0m : Math.Round(ret / stake, 4),
            finalHits,
            retFinal,
            stake == 0 ? 0m : Math.Round(retFinal / stake, 4),
            voidedSettled.Count,
            voidedHits);
    }

    /// <summary>样本够不够下结论：已结算未撤注 ≥ 150 注（用户 2026-11-08 拍板）。</summary>
    public const int EnoughSettled = 150;

    /// <summary>
    /// 撤注对照组够不够下结论。比主口径低一档（50）但绝不下探到个位数：
    /// 撤注事件本身稀疏（生产上至今 0 例），10 注以内的命中率差异纯属噪声，
    /// 拿它去调阈值等于用抛硬币的结果改规则。
    /// </summary>
    public const int EnoughVoided = 50;
}
