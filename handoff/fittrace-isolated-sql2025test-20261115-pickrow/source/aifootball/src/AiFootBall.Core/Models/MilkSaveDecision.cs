namespace AiFootBall.Core.Models;

/// <summary>
/// 关闭轮次时 plan / steps / ledger → <see cref="MilkSaveOutcome"/> 的唯一判定。
/// <c>SaveMilkAsync</c> 必须调用本函数；测试直接调用同一实现，禁止自行复制 Skip/Fail/Ok 映射。
/// </summary>
public static class MilkSaveDecision
{
    public static readonly string[] RequiredSteps =
        { "picks", "gate", "input", "snapshot", "source" };

    /// <param name="ledgerOk">
    /// Rewrite 且步骤齐全时必填：核对通过为 true；核对失败 / 无法执行为 false，
    /// 原因写在 <paramref name="ledgerDetail"/>。
    /// Skip/Partial 时忽略。
    /// </param>
    /// <param name="publishedAt">仅成功发布路径使用；Skip/Fail 忽略。</param>
    public static MilkSaveOutcome Conclude(
        MilkBoardPlan plan,
        string runId,
        MilkRunSteps steps,
        int inputCount,
        int snapshotRows,
        int pickRows,
        bool ledgerOk,
        string? ledgerDetail,
        DateTime publishedAt)
    {
        if (plan.Mode == MilkBoardMode.Partial)
            return MilkSaveOutcome.Fail(plan.Reason, runId, "partial");

        if (plan.Mode != MilkBoardMode.Rewrite)
            return MilkSaveOutcome.Skip(plan.Reason, runId, "skip");

        if (!steps.AllOk(RequiredSteps)
            || !MilkRunSteps.SnapshotCoversInputs(inputCount, snapshotRows))
        {
            var extra = MilkRunSteps.SnapshotCoversInputs(inputCount, snapshotRows)
                ? steps.Detail
                : $"存档缺失：输入 {inputCount} 场但存档 0 行。{steps.Detail}";
            return MilkSaveOutcome.Fail(
                $"落库未全部成功：{steps.Summary}。{extra}",
                runId, "rewrite", pickRows);
        }

        if (!ledgerOk)
        {
            var why = string.IsNullOrWhiteSpace(ledgerDetail) ? "逐档核对未通过" : ledgerDetail;
            return MilkSaveOutcome.Fail($"落库核对失败：{why}", runId, "rewrite", pickRows);
        }

        return MilkSaveOutcome.Ok(runId, plan.Reason, publishedAt, pickRows, "rewrite");
    }
}
