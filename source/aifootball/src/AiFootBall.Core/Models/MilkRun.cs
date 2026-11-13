using System;
using System.Collections.Generic;
using System.Linq;

namespace AiFootBall.Core.Models;

/// <summary>
/// 一轮大奶计算的标识与范围（2026-11-13 第二轮加）。
///
/// 为什么需要「轮」（RunId）：此前输入凭证、全档存档、闸门、注单四张表各自按日期写，
/// 同一期一天要重算 3~5 次，事后只能靠 `CreatedAt` 猜「这几行是不是同一批算出来的」。
/// 猜错的代价很实在：11-13 那次空跑把 48 行存档删光，后来连「这张表到底写过没有」
/// 都只能靠 `IDENT_CURRENT` 反推。有了 RunId，四张表能关联到同一轮，
/// 并且**保留历史**——不再靠「整期覆盖」来保证唯一性。
///
/// `RunId` 的格式刻意做成 `yyyyMMddTHHmmss-六位十六进制`：可排序（`MAX(RunId)` 即最新一轮），
/// 也可肉眼看出大致时刻。后半段随机串保证同一秒内两次重算不会撞。
/// </summary>
public sealed class MilkRun
{
    /// <summary>本轮标识，同时是四张表的关联键。</summary>
    public string RunId { get; init; } = string.Empty;

    /// <summary>本轮开始时刻（本地时间）。</summary>
    public DateTime StartedAt { get; init; }

    /// <summary>
    /// **过滤前**进入计算的场次数（`raw`，即 `RecommendAllAndSaveAsync` 里 `RecommendAsync` 的原样输出）。
    ///
    /// 刻意不用 `funnel.Distributed`：那是过了禁推名单之后的数。
    /// 11-13 的教训正是——11 场全被禁推挡掉时 `Distributed=0`，
    /// 若拿它当「本轮有没有输入」的判据，就会得出「没有输入，所以不清榜」，旧票继续挂着。
    /// </summary>
    public int RawMatches { get; init; }

    /// <summary>
    /// 本轮涉及的受注日（由过滤前的场次算出，**不是**「今天」）。
    ///
    /// 空集 = 无法确认期号（采集失败 / 整期确实没场次），此时一行都不许动。
    /// </summary>
    public IReadOnlyList<DateTime> SalesDates { get; init; } = Array.Empty<DateTime>();

    /// <summary>本轮模型版本戳（与注单/存档同源），用于把「这一轮」和「这一版模型」对上。</summary>
    public string? VersionTag { get; init; }

    /// <summary>生成一个新标识。同一时刻调两次也必然不同。</summary>
    public static string NewId(DateTime at)
        => at.ToString("yyyyMMdd'T'HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
}

/// <summary>榜面重写模式。</summary>
public enum MilkBoardMode
{
    /// <summary>本轮没取得有效期号：榜面一行都不许动（采集中断不等于「今天没有推荐」）。</summary>
    Skip = 0,

    /// <summary>已确认受注日：按日重写。注单列表为空 = 撤掉该日应撤的旧票。</summary>
    Rewrite = 1
}

/// <summary>某一个受注日的重写内容。列表为空也是有效指令（等于清掉该日未开赛旧票）。</summary>
public sealed class MilkBoardWrite
{
    public DateTime SalesDate { get; init; }
    public IReadOnlyList<MilkPick> Picks { get; init; } = Array.Empty<MilkPick>();
}

/// <summary>
/// 榜面重写计划（2026-11-13 第二轮）。
///
/// 这里只做决策、不碰数据库，所以「全部被过滤」和「采集失败」两种形态可以直接单测。
///
/// 两种形态必须分开（用户 11-13 明确指出）：
/// · **成功取得有效期号、全部被过滤**：本轮受注 N 场（N&gt;0），一场都没出票。
///   这是正常的业务结果，必须按那些受注日撤掉失效旧票，否则 11-13 那注会一直挂着。
/// · **采集失败 / 输入不完整**：本轮受注 0 场，连期号都确认不了。
///   这时清榜等于拿采集中断当撤榜理由，会把停售后定版的好票删掉。
///
/// 判据只有一个：**过滤前**的受注场次与受注日。绝不能从过滤后的 `Inputs`
/// 或下游的 `FbMilkGate` / `Recommendation` 反推——那些都是「算完之后」的产物，
/// 全部被过滤时恰好都是 0，正好把要修的这种情况判成「没有输入」。
/// </summary>
public sealed class MilkBoardPlan
{
    public MilkBoardMode Mode { get; private init; } = MilkBoardMode.Skip;

    /// <summary>给日志用的一句话理由。</summary>
    public string Reason { get; private init; } = string.Empty;

    public IReadOnlyList<MilkBoardWrite> Writes { get; private init; } = Array.Empty<MilkBoardWrite>();

    /// <summary>本轮出票总数（跨受注日）。</summary>
    public int PickTotal => Writes.Sum(w => w.Picks.Count);

    /// <summary>有受注日、但一注都没出——「全部被过滤」的形态。</summary>
    public bool AllFiltered => Mode == MilkBoardMode.Rewrite && PickTotal == 0;

    public static MilkBoardPlan Skip(string reason)
        => new() { Mode = MilkBoardMode.Skip, Reason = reason };

    /// <param name="run">本轮范围。`RawMatches` 与 `SalesDates` 都必须来自过滤前的场次。</param>
    /// <param name="picks">本轮引擎实际出的注（可以为空）。</param>
    /// <param name="today">解析受注日时用的「今天」，与落库口径一致。</param>
    public static MilkBoardPlan Create(MilkRun run, IReadOnlyList<MilkPick>? picks, DateTime today)
    {
        if (run is null || run.RawMatches <= 0)
            return Skip("过滤前受注场次为 0（采集失败或整期无场次），不清榜");

        if (run.SalesDates.Count == 0)
            return Skip("过滤前受注场次有行但算不出受注日，无法确认期号，不清榜");

        var byDate = (picks ?? Array.Empty<MilkPick>())
            .GroupBy(p => SteadyPickRules.SalesDate(
                string.IsNullOrWhiteSpace(p.MatchNo) ? null : p.MatchNo, p.MatchTime, today))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<MilkPick>)g.ToList());

        var dates = run.SalesDates.Where(x => x != default).Select(x => x.Date).Distinct().ToList();
        // 注单若落在范围外的受注日（理论上不该出现），补进来——否则这些注会写不进去、
        // 而下一次重算又会被当成新出现的日期，永远对不上。
        foreach (var d in byDate.Keys)
            if (!dates.Contains(d)) dates.Add(d);
        dates.Sort();

        var writes = dates
            .Select(d => new MilkBoardWrite
            {
                SalesDate = d,
                Picks = byDate.TryGetValue(d, out var list) ? list : Array.Empty<MilkPick>()
            })
            .ToList();

        var pickTotal = writes.Sum(w => w.Picks.Count);
        return new MilkBoardPlan
        {
            Mode = MilkBoardMode.Rewrite,
            Writes = writes,
            Reason = writes.Count == 0
                ? "本轮无可重写受注日"
                : pickTotal == 0
                    ? $"本轮受注 {run.RawMatches} 场全部被过滤（0 注），按 {writes.Count} 个受注日撤掉失效旧票"
                    : $"本轮受注 {run.RawMatches} 场、出票 {pickTotal} 注，按 {writes.Count} 个受注日重写"
        };
    }
}
