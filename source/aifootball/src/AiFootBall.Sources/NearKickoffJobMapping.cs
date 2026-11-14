using AiFootBall.Core.Models;

namespace AiFootBall.Sources;

/// <summary>
/// 临场重算作业记账：按结构化 Outcome/Verdict 分跳过 / 拒绝 / 其它失败，
/// 不得把非发布一律压成「批次闸未放行 FAILED」。
/// </summary>
public static class NearKickoffJobMapping
{
    public static CollectionResult ToCollectionResult(NearKickoffRefreshResult r)
    {
        if (r.Stale <= 0)
            return CollectionResult.Ok(0, "临场重算：没有需要重算的场次");

        if (r.Published)
            return CollectionResult.Ok(r.Saved,
                $"临场重算：{r.Stale} 场临近开赛但数据早于赛前 60 分钟，已重算并保存 {r.Saved} 场"
                + (r.RunId is not null ? $"；RunId={r.RunId}" : ""));

        var boardNote = r.BoardPublishedAt is DateTime at
            ? $"；当前榜面发布时间 {at:yyyy-MM-dd HH:mm:ss}"
            : (r.BoardStatusError is not null
                ? $"；榜面状态读取失败（未伪造时间）：{r.BoardStatusError}"
                : "");
        var detail = $"临场重算未发布[{r.Outcome}/{r.Verdict}] {r.Message}"
                     + (r.RunId is not null ? $"；RunId={r.RunId}" : "")
                     + $"；待重算 {r.Stale} 场{boardNote}";

        if (r.Outcome is "gate-skip" || r.Verdict == "cross-day-skip")
            return CollectionResult.Skipped(detail);

        // gate-reject / milk-skipped / milk-failed / not-configured：FAILED，文案保留真实 outcome
        return CollectionResult.Fail(0, detail);
    }
}
