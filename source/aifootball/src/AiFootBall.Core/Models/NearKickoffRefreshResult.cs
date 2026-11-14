namespace AiFootBall.Core.Models;

/// <summary>
/// <c>RefreshNearKickoffAsync</c> 的结构化结果：保留 save 的 Outcome/Verdict，
/// 调度层不得把非发布一律压成「闸未放行 FAILED」。
/// </summary>
public sealed class NearKickoffRefreshResult
{
    public int Stale { get; init; }
    public bool Refreshed { get; init; }
    public int Saved { get; init; }
    public string Outcome { get; init; } = "idle";
    public string Verdict { get; init; } = "";
    public string Message { get; init; } = "";
    public string? RunId { get; init; }
    public DateTime? BoardPublishedAt { get; init; }
    public string? BoardStatusError { get; init; }

    public bool Published => Refreshed;

    public static NearKickoffRefreshResult Idle()
        => new()
        {
            Stale = 0,
            Refreshed = false,
            Saved = 0,
            Outcome = "idle",
            Verdict = "no-stale",
            Message = "没有需要重算的场次"
        };

    public static NearKickoffRefreshResult FromSave(int stale, RecommendSaveResult save)
        => new()
        {
            Stale = stale,
            Refreshed = save.Published,
            Saved = save.Published ? save.SavedCount : 0,
            Outcome = save.Outcome,
            Verdict = save.Verdict,
            Message = save.Message,
            RunId = save.RunId,
            BoardPublishedAt = save.BoardPublishedAt,
            BoardStatusError = save.BoardStatusError
        };
}
