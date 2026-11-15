namespace AiFootBall.Core.Models;

/// <summary>
/// <c>RecommendAllAndSaveAsync</c> 的结构化结果。
/// <see cref="Published"/> 只在大奶真正 PublishRun 成功时为 true；
/// 闸拒绝 / MilkBoardPlan 跳过 / 落库失败 一律 false。
/// </summary>
public sealed class RecommendSaveResult
{
    public bool Published { get; init; }
    /// <summary>gate-reject / gate-skip / published / milk-skipped / milk-failed / not-configured</summary>
    public string Outcome { get; init; } = "";
    public string Verdict { get; init; } = "";
    public string Message { get; init; } = "";
    public string? RunId { get; init; }
    public Dictionary<string, List<MatchRecommendation>> Boards { get; init; } = new();
    /// <summary>当前页面对应受注日的成功发布时间；读失败时保持 null，不得伪造 Now。</summary>
    public DateTime? BoardPublishedAt { get; init; }
    /// <summary>辅助读 board-status 失败时的真实错误（不阻断主链）。</summary>
    public string? BoardStatusError { get; init; }

    public int SavedCount => Boards.Values.Sum(v => v.Count);

    /// <summary>
    /// 由 <see cref="MilkSaveOutcome"/> 传播到入口结果——测试必须走这条，
    /// 不得用 hook 整段绕过「闸放行后是否真发布」的判据。
    /// </summary>
    public static RecommendSaveResult FromMilkOutcome(
        MilkSaveOutcome milk,
        Dictionary<string, List<MatchRecommendation>> boards,
        DateTime? priorBoardPublishedAt,
        string? boardStatusError = null)
    {
        return milk.Kind switch
        {
            MilkSaveKind.Published => new RecommendSaveResult
            {
                Published = true,
                Outcome = "published",
                Verdict = "fresh",
                Message = milk.Reason,
                RunId = milk.RunId,
                Boards = boards,
                // 只用本轮 Publish 回传的时刻；没有则保留事先读到的旧榜时间，绝不 DateTime.Now
                BoardPublishedAt = milk.PublishedAt ?? priorBoardPublishedAt,
                BoardStatusError = boardStatusError
            },
            MilkSaveKind.Skipped => new RecommendSaveResult
            {
                Published = false,
                Outcome = "milk-skipped",
                Verdict = milk.PlanMode ?? "skip",
                Message = milk.Reason,
                RunId = milk.RunId,
                Boards = boards,
                BoardPublishedAt = priorBoardPublishedAt,
                BoardStatusError = boardStatusError
            },
            MilkSaveKind.NotConfigured => new RecommendSaveResult
            {
                Published = false,
                Outcome = "not-configured",
                Verdict = "not-configured",
                Message = milk.Reason,
                Boards = boards,
                BoardPublishedAt = priorBoardPublishedAt,
                BoardStatusError = boardStatusError
            },
            _ => new RecommendSaveResult
            {
                Published = false,
                Outcome = "milk-failed",
                Verdict = milk.PlanMode ?? "failed",
                Message = milk.Reason,
                RunId = milk.RunId,
                Boards = boards,
                BoardPublishedAt = priorBoardPublishedAt,
                BoardStatusError = boardStatusError
            }
        };
    }

    public static RecommendSaveResult FromGate(
        MilkGateResult gate, DateTime? boardPublishedAt, string? boardStatusError = null)
    {
        var skip = gate.Band == MilkGateBand.Skip;
        return new RecommendSaveResult
        {
            Published = false,
            Outcome = skip ? "gate-skip" : "gate-reject",
            Verdict = gate.Verdict,
            Message = string.IsNullOrWhiteSpace(gate.FailReason) ? gate.Verdict : gate.FailReason,
            Boards = new Dictionary<string, List<MatchRecommendation>>(),
            BoardPublishedAt = boardPublishedAt,
            BoardStatusError = boardStatusError
        };
    }
}
