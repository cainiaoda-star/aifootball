namespace AiFootBall.Sources;

/// <summary>
/// 联赛分层「半全场反转档」实测基线（2026-11-07 建）。
///
/// 为什么需要它：全库口径的 0.585% 胜负档是被低水平联赛 + 友谊赛稀释出来的均值，
/// 拿它去否决赛场，等于用南非乙级联赛的概率否定英冠。实测（<see cref="Matches"/> 场/联赛）：
/// 苏超 3.20% / 英超 2.68% / 欧冠 2.66% / 荷乙 2.62% / 英冠 2.56%，
/// 而友谊赛只有 0.05%~0.28%——差 10~60 倍。同一个 32 倍档，在不同联赛里是完全不同的东西。
///
/// 数据来源：dbo.FbLeagueHalfFullBase（脚本由 FbMatch 全库聚合，Reliable=1 即样本≥2000 的联赛）。
/// 代码内嵌一份快照而不是每次查库，理由有两个：
/// ① 引擎是纯静态、无 DbContext 的纯函数，加数据库依赖会把单测全部拖成集成测试；
/// ② 基线一年只变一次（换赛季），内嵌足够，表作为「数据凭证 + 可刷新源」保留。
/// 若表刷新，用 <see cref="DumpSqlHint"/> 重新生成这张字典，不要手改单个数字。
///
/// 编码顺序与竞彩半全场 OptionCode 一致：X-Y = 半场方向 - 全场方向（3 胜 / 1 平 / 0 负）。
/// </summary>
public static class LeagueHalfFullBaseline
{
    /// <summary>六个反转档的编码（半场方向 ≠ 全场方向）。</summary>
    public static readonly string[] ReverseCodes = { "3-1", "3-0", "1-3", "1-0", "0-3", "0-1" };

    /// <summary>
    /// 联赛 → 六档实测概率（百分数，顺序见 <see cref="ReverseCodes"/>）。
    /// 只收样本 ≥ 2000 的联赛（Reliable=1）；不足 2000 的联赛走 <see cref="DefaultPct"/> 兜底，
    /// 而不是硬塞一个噪声值——样本不够的联赛，它的「异常高反转」多半只是小样本波动。
    /// </summary>
    private static readonly Dictionary<string, decimal[]> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        // 联赛名            胜平   胜负   平胜   平负   负胜   负平
        ["英甲"] = new[] { 5.17m, 1.87m, 14.62m, 10.46m, 2.36m, 5.32m },
        ["英冠"] = new[] { 4.99m, 2.56m, 14.38m, 11.05m, 2.93m, 4.34m },
        ["阿甲"] = new[] { 4.99m, 1.23m, 14.96m, 10.19m, 1.66m, 4.37m },
        ["美职联"] = new[] { 5.18m, 2.16m, 12.48m, 9.22m, 2.85m, 4.48m },
        ["西乙"] = new[] { 4.39m, 1.70m, 14.04m, 9.03m, 2.06m, 4.28m },
        ["巴甲"] = new[] { 5.88m, 1.76m, 16.21m, 9.26m, 2.02m, 4.16m },
        ["日职乙"] = new[] { 5.05m, 2.35m, 13.18m, 12.31m, 1.83m, 5.18m },
        ["荷乙"] = new[] { 5.89m, 2.62m, 12.66m, 10.43m, 3.14m, 4.06m },
        ["意甲"] = new[] { 5.51m, 2.33m, 13.75m, 10.89m, 2.64m, 5.38m },
        ["西甲"] = new[] { 4.83m, 2.35m, 15.99m, 11.34m, 2.26m, 5.24m },
        ["英超"] = new[] { 5.55m, 2.68m, 13.18m, 11.18m, 3.14m, 4.18m },
        ["日职"] = new[] { 4.73m, 1.74m, 14.66m, 11.38m, 2.41m, 4.34m },
        ["法乙"] = new[] { 4.88m, 2.13m, 15.85m, 10.10m, 2.37m, 4.64m },
        ["土超"] = new[] { 3.82m, 1.65m, 8.71m, 5.52m, 1.55m, 2.71m },

        // 样本 800~2000（Reliable=0）但属主流赛事，值稳定、必须收——不然后面这几种联赛全走兜底。
        ["苏超"] = new[] { 4.87m, 3.20m, 13.01m, 8.60m, 2.36m, 5.02m },
        ["欧冠"] = new[] { 3.52m, 2.66m, 12.79m, 10.27m, 2.37m, 2.80m },
        ["德甲"] = new[] { 4.59m, 2.18m, 12.64m, 9.56m, 3.24m, 5.37m },
        ["法甲"] = new[] { 4.39m, 2.35m, 12.52m, 10.99m, 3.12m, 5.57m },
        ["葡超"] = new[] { 5.37m, 1.75m, 14.19m, 11.67m, 2.74m, 4.49m },
        ["K1联赛"] = new[] { 4.59m, 2.00m, 15.11m, 12.74m, 2.82m, 4.67m },

        // 友谊赛不是「低反转」，是「几乎不反转」：23k 场里胜负档 0.05%。
        // 收它进来不是为了给机会，而是为了让 CounterHigh 的联赛准入直接判死（0.05% < MinCounterHighLeaguePct）。
        ["友谊赛"] = new[] { 0.15m, 0.05m, 0.28m, 0.26m, 0.06m, 0.17m },
        ["球会友谊"] = new[] { 0.15m, 0.05m, 0.28m, 0.26m, 0.06m, 0.17m }
    };

    /// <summary>样本 ≥2000 的 14 个联赛的六档均值，未知联赛用它兜底（不是拍脑袋的 1.5%）。</summary>
    private static readonly decimal[] DefaultPct = { 5.13m, 2.08m, 13.90m, 10.88m, 2.30m, 4.33m };

    /// <summary>刷新基线表的 SQL 位置提示（换赛季重跑时用）。</summary>
    public const string DumpSqlHint = "dbo.FbLeagueHalfFullBase";

    /// <summary>
    /// 取某联赛某反转档的实测概率（百分数）。联赛不在表里走 <see cref="DefaultPct"/> 兜底。
    /// 非反转档返回 null——顺风档（胜胜/平平/负负）不走分层，仍是全局 12% 门槛。
    /// </summary>
    public static decimal? Pct(string? league, string code)
    {
        var i = Array.IndexOf(ReverseCodes, code);
        if (i < 0) return null;
        if (!string.IsNullOrWhiteSpace(league) && Table.TryGetValue(league.Trim(), out var row))
            return row[i];
        return DefaultPct[i];
    }

    /// <summary>
    /// 该联赛该档的实测基线是否够格拿「高赔单列」名额。
    /// 判据是「这个档在该联赛里到底会不会发生」，不是「模型觉得它会不会」——
    /// 模型在冷门联赛的样本本来就不对称（见记忆教训4：欧罗巴豪门样本错配），不能自我背书。
    /// </summary>
    public static bool Passes(string? league, string code, decimal minPct)
        => Pct(league, code) is decimal p && p >= minPct;

    /// <summary>该联赛是否在基线表里（前端/诊断用：false = 走的是兜底均值，可信度低一档）。</summary>
    public static bool Known(string? league)
        => !string.IsNullOrWhiteSpace(league) && Table.ContainsKey(league.Trim());
}
