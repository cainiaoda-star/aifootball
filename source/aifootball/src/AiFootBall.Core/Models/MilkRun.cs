using System;
using System.Collections.Generic;
using System.Linq;

namespace AiFootBall.Core.Models;

/// <summary>一轮大奶计算的状态（2026-11-13 第三轮）。</summary>
public static class MilkRunStatus
{
    /// <summary>已开始、结果未定。允许出现在库里，**永不允许被榜面选中**。</summary>
    public const string Running = "running";

    /// <summary>四类落库（注单/闸门/输入/存档）全部成功，且已发布榜面。只有它可以被榜面选中。</summary>
    public const string Success = "success";

    /// <summary>任一步失败：数据可能只写了一半，不许发布，榜面继续显示上一轮成功的。</summary>
    public const string Failed = "failed";

    /// <summary>没有可用批次/期号，本轮一行不动（采集中断不等于「今天没有推荐」）。</summary>
    public const string Skipped = "skipped";

    /// <summary>期号确认了，但采集不完整——本轮不重写，旧票保留（不许误撤）。</summary>
    public const string Partial = "partial";
}

/// <summary>
/// 某一期（`周五`、`周六`）的采集完整度。
///
/// 为什么不能只看「本批场次数 &gt; 0」：`BettingCard` 按 (Board, Expect, MatchNo) 做 MERGE，
/// 一次只抓到 8 场时，另外 3 行的 `CapturedAt` 停在上一批，而 `GetCardsAsync` 只读
/// `CapturedAt = MAX(...)` 那一批 —— 于是**部分采集会让工作集凭空缩小 3 场**。
/// 此时若按「有输入就重写」处理，那 3 场的旧票会被当成「本轮没出」撤掉。
/// </summary>
public sealed class MilkIssueState
{
    /// <summary>期号前缀（场次号前两位，如 `周五`）。</summary>
    public string Issue { get; init; } = string.Empty;

    /// <summary>最新一批里的在售场次数。</summary>
    public int BatchMatches { get; init; }

    /// <summary>该期已知的在售场次数（含停留在旧批次的残留行）。</summary>
    public int KnownMatches { get; init; }

    /// <summary>本批是否已覆盖该期已知的全部在售场次。</summary>
    public bool Complete => BatchMatches > 0 && BatchMatches >= KnownMatches;

    public int Missing => Math.Max(0, KnownMatches - BatchMatches);

    public override string ToString() => $"{Issue} {BatchMatches}/{KnownMatches}";
}

/// <summary>
/// 采集状态（2026-11-13 第三轮，用户要求「用明确的采集成功／期号完整状态决定是否重写」）。
///
/// 两个字段分得很清：
///   · <see cref="Succeeded"/> —— 有没有一个**可用的**批次（拿到批次，且不算太旧）；
///   · <see cref="Complete"/>  —— 这个批次是否覆盖了各期已知的在售场次。
/// 只有 Complete 才允许重写榜面。Succeeded 但 !Complete 就是「只采到部分场次」。
/// </summary>
public sealed class MilkCaptureState
{
    /// <summary>
    /// 批次可用的最大年龄。
    ///
    /// 取值依据：`cards-daily` 一天只在几个时点重写 BettingCard（实测 11-13 只有 16:31 一批），
    /// 夜间到次日早上会隔十来个小时。这里放宽到 12 小时，只用来挡「采集早就死了、
    /// 拿昨天的批次当今天算」这种形态；「采到几场」由 <see cref="Complete"/> 判，不靠时长。
    /// </summary>
    public static readonly TimeSpan MaxBatchAge = TimeSpan.FromHours(12);

    /// <summary>批次可用。</summary>
    public bool Succeeded { get; private init; }

    /// <summary>批次时刻（`MAX(CapturedAt)`）。</summary>
    public DateTime? BatchAt { get; private init; }

    /// <summary>本批涉及的各期完整度。只含「本批里至少有 1 场」的期。</summary>
    public IReadOnlyList<MilkIssueState> Issues { get; private init; } = Array.Empty<MilkIssueState>();

    /// <summary>不可用/不完整的原因（给日志用）。</summary>
    public string? Reason { get; private init; }

    public bool Complete => Succeeded && Issues.Count > 0 && Issues.All(i => i.Complete);

    public int BatchTotal => Issues.Sum(i => i.BatchMatches);

    public int KnownTotal => Issues.Sum(i => i.KnownMatches);

    public int MissingTotal => Issues.Sum(i => i.Missing);

    /// <summary>形如 `周五 8/11、周六 11/11`。</summary>
    public string IssueText => string.Join("、", Issues.Select(i => i.ToString()));

    public static MilkCaptureState Missing(string reason) => new() { Succeeded = false, Reason = reason };

    /// <summary>
    /// 由「批次时刻 + 各期计数」判定采集状态。纯函数，便于把判定规则钉进测试。
    /// </summary>
    public static MilkCaptureState Build(
        DateTime? batchAt, DateTime now, TimeSpan maxAge, IReadOnlyList<MilkIssueState>? issues)
    {
        var list = (issues ?? Array.Empty<MilkIssueState>())
            .Where(i => i.BatchMatches > 0)      // 本批里没有的期不参与判定：那是「本轮没算它」，不是残缺
            .OrderBy(i => i.Issue, StringComparer.Ordinal)
            .ToList();

        if (batchAt is null)
            return Missing("没有受注批次（BettingCard 无记录）");

        if (batchAt.Value < now - maxAge)
            return new MilkCaptureState
            {
                Succeeded = false,
                BatchAt = batchAt,
                Issues = list,
                Reason = $"受注批次过期（{batchAt:MM-dd HH:mm}，超过 {maxAge.TotalHours:0.#} 小时）"
            };

        if (list.Count == 0)
            return new MilkCaptureState
            {
                Succeeded = false,
                BatchAt = batchAt,
                Reason = "批次里没有可识别的期号"
            };

        return new MilkCaptureState { Succeeded = true, BatchAt = batchAt, Issues = list };
    }
}

/// <summary>
/// 一轮大奶计算的标识与范围（2026-11-13 第二轮加，第三轮补采集状态）。
///
/// 为什么需要「轮」（RunId）：此前输入凭证、全档存档、闸门、注单四张表各自按日期写，
/// 同一期一天要重算 3~5 次，事后只能靠 `CreatedAt` 猜「这几行是不是同一批算出来的」。
/// 猜错的代价很实在：11-13 那次空跑把 48 行存档删光，后来连「这张表到底写过没有」
/// 都只能靠 `IDENT_CURRENT` 反推。有了 RunId，四张表能关联到同一轮，
/// 并且**保留历史**——不再靠「整期覆盖」来保证唯一性。
///
/// `RunId` 的格式是 `yyyyMMddTHHmmss-六位十六进制`，可排序、可肉眼看出时刻。
/// **但排序不是语义**：哪一轮算「当前榜面」由 `FbMilkRun.Status` + `PublishedAt` 决定，
/// 失败轮的 RunId 再大也不会被选中（见 `MilkPickRepository.GetPublishedRunIdSql`）。
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
    /// 若拿它当「本轮有没有输入」的判据，就会得出「没有输入，所以不清榜」。
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

    /// <summary>采集状态。null 只出现在「还没查」的调用点，会被当成采集不可用处理。</summary>
    public MilkCaptureState? Capture { get; init; }

    /// <summary>生成一个新标识。同一时刻调两次也必然不同。</summary>
    public static string NewId(DateTime at)
        => at.ToString("yyyyMMdd'T'HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
}

/// <summary>榜面重写模式。</summary>
public enum MilkBoardMode
{
    /// <summary>本轮没取得可用期号：榜面一行都不许动（采集中断不等于「今天没有推荐」）。</summary>
    Skip = 0,

    /// <summary>已确认受注日且采集完整：按日重写。注单列表为空 = 撤掉该日应撤的旧票。</summary>
    Rewrite = 1,

    /// <summary>期号确认了但采集不完整：本轮不写、不删，旧票保留（不许误撤）。</summary>
    Partial = 2
}

/// <summary>某一个受注日的重写内容。列表为空也是有效指令（等于清掉该日未开赛旧票）。</summary>
public sealed class MilkBoardWrite
{
    public DateTime SalesDate { get; init; }
    public IReadOnlyList<MilkPick> Picks { get; init; } = Array.Empty<MilkPick>();
}

/// <summary>发布某一天时写进 `FbMilkRunDate` 的计数（0 注也要写，榜面才能「归零」）。</summary>
public sealed class MilkRunDateCount
{
    public DateTime SalesDate { get; init; }
    public int PickCount { get; init; }
}

/// <summary>
/// 榜面重写计划（2026-11-13 第二轮；第三轮加采集完整性闸）。
///
/// 这里只做决策、不碰数据库，所以几种形态可以直接单测。
///
/// 三种形态必须分开（用户 11-13 两次指出）：
/// · **确认期号、采集完整、全部被过滤**：必须按那些受注日重写，撤掉失效旧票。
/// · **确认期号、但只采到部分场次**：不许重写——那会把没采到的场次的旧票一起撤掉。
///   这是第三轮新增的一档，判据是 `MilkCaptureState.Complete`，不是 `RawMatches &gt; 0`。
/// · **采集失败 / 连期号都确认不了**：一行都不动。
///
/// 判据只有一个来源：**过滤前**的受注场次 + 采集状态。
/// 绝不能从过滤后的 `Inputs` 或下游的 `FbMilkGate` / `Recommendation` 反推——
/// 那些都是「算完之后」的产物，全部被过滤时恰好都是 0。
/// </summary>
public sealed class MilkBoardPlan
{
    public MilkBoardMode Mode { get; private init; } = MilkBoardMode.Skip;

    /// <summary>给日志用的一句话理由。</summary>
    public string Reason { get; private init; } = string.Empty;

    public IReadOnlyList<MilkBoardWrite> Writes { get; private init; } = Array.Empty<MilkBoardWrite>();

    /// <summary>采集缺了多少场（Partial 时的数字，非 Partial 恒 0）。</summary>
    public int MissingMatches { get; private init; }

    /// <summary>本轮出票总数（跨受注日）。</summary>
    public int PickTotal => Writes.Sum(w => w.Picks.Count);

    /// <summary>有受注日、但一注都没出——「全部被过滤」的形态。</summary>
    public bool AllFiltered => Mode == MilkBoardMode.Rewrite && PickTotal == 0;

    /// <summary>落 `FbMilkRun.Status` 用。</summary>
    public string RunStatus => Mode switch
    {
        MilkBoardMode.Rewrite => MilkRunStatus.Success,
        MilkBoardMode.Partial => MilkRunStatus.Partial,
        _ => MilkRunStatus.Skipped
    };

    public static MilkBoardPlan Skip(string reason)
        => new() { Mode = MilkBoardMode.Skip, Reason = reason };

    /// <param name="run">本轮范围。`RawMatches`、`SalesDates`、`Capture` 都必须来自过滤前的场次与采集批次。</param>
    /// <param name="picks">本轮引擎实际出的注（可以为空）。</param>
    /// <param name="today">解析受注日时用的「今天」，与落库口径一致。</param>
    public static MilkBoardPlan Create(MilkRun? run, IReadOnlyList<MilkPick>? picks, DateTime today)
    {
        if (run is null || run.RawMatches <= 0)
            return Skip("过滤前受注场次为 0（采集失败或整期无场次），不清榜");

        if (run.SalesDates.Count == 0)
            return Skip("过滤前受注场次有行但算不出受注日，无法确认期号，不清榜");

        // ★ 采集完整性（2026-11-13 第三轮）：RawMatches>0 只证明「拿到了一些场次」。
        // 部分采集会让工作集缩小，按不完整的工作集重写 = 把没采到的那几场的旧票撤掉。
        var capture = run.Capture;
        if (capture is null || !capture.Succeeded)
            return Skip($"采集批次不可用（{capture?.Reason ?? "未查询"}），不清榜");

        if (!capture.Complete)
            return new MilkBoardPlan
            {
                Mode = MilkBoardMode.Partial,
                MissingMatches = capture.MissingTotal,
                Reason = $"只采到 {capture.BatchTotal}/{capture.KnownTotal} 场（{capture.IssueText}），"
                         + "本轮不重写榜面，旧票保留"
            };

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

/// <summary>
/// 一轮里每一步的落库结果（2026-11-13 第三轮）。
///
/// 发布判据不看「有没有行」，看**四类落库是否都成功**：注单、闸门、输入、存档。
/// 任一步失败都可能只写了一半，此时切榜面等于让用户看到半份数据；正确处置是
/// 把这一轮标成 failed，榜面继续显示上一轮成功的。
/// </summary>
public sealed class MilkRunSteps
{
    private readonly List<(string Step, string? Error)> _items = new();

    public void Ok(string step) => _items.Add((step, null));

    public void Fail(string step, string error) => _items.Add((step, error));

    public void Fail(string step, Exception ex) => Fail(step, $"{ex.GetType().Name}: {ex.Message}");

    public int Count => _items.Count;

    /// <summary>指定的步骤是否都记录为成功（没记录 = 没跑成，一律不算成功）。</summary>
    public bool AllOk(params string[] required)
        => required.All(step => _items.Any(i => i.Step == step && i.Error is null));

    public string? FirstFailure => _items.FirstOrDefault(i => i.Error is not null).Error;

    /// <summary>形如 `picks=ok,gate=ok,input=ok,snapshot=FAIL(…)`。</summary>
    public string Summary => string.Join(",", _items.Select(i => $"{i.Step}={(i.Error is null ? "ok" : "FAIL")}"));

    public string Detail => string.Join("；", _items.Where(i => i.Error is not null)
        .Select(i => $"{i.Step}: {i.Error}"));

    /// <summary>
    /// 有输入就必须有存档（引擎在同一个点产出两者，一一对应：分布算出之后、方向闸之前）。
    /// 输入 &gt; 0 而存档 0 行 = 存档缺失，这一轮不许发布——
    /// 否则会出现「榜更新了、审计材料是空的」，正是 11-13 那种事后无法复盘的状态。
    /// </summary>
    public static bool SnapshotCoversInputs(int inputCount, int snapshotRows)
        => inputCount == 0 || snapshotRows > 0;
}
