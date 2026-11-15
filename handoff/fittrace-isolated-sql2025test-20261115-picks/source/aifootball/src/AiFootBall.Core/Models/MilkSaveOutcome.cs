namespace AiFootBall.Core.Models;

/// <summary>
/// <c>SaveMilkAsync</c> 的真实落库结论（2026-11-14 复审）。
/// 发布与否只认这里，不得在入口无条件置 Published=true。
/// </summary>
public enum MilkSaveKind
{
    /// <summary>已 PublishRunAsync，FbMilkRunDate 已写，Status=success。</summary>
    Published,
    /// <summary>MilkBoardPlan Skip / 未启动轮次头等：一行不动。</summary>
    Skipped,
    /// <summary>Partial / 落库失败 / 核对失败：未发布。</summary>
    Failed,
    /// <summary>milkPicks/tickets 未注入，本进程无法出榜。</summary>
    NotConfigured
}

public sealed class MilkSaveOutcome
{
    public MilkSaveKind Kind { get; init; }
    public string Reason { get; init; } = "";
    public string? RunId { get; init; }
    public string? PlanMode { get; init; }
    public DateTime? PublishedAt { get; init; }
    public int PickRows { get; init; }

    public bool Published => Kind == MilkSaveKind.Published;

    public static MilkSaveOutcome Ok(string runId, string reason, DateTime publishedAt, int pickRows, string? planMode = null)
        => new()
        {
            Kind = MilkSaveKind.Published,
            RunId = runId,
            Reason = reason,
            PublishedAt = publishedAt,
            PickRows = pickRows,
            PlanMode = planMode
        };

    public static MilkSaveOutcome Skip(string reason, string? runId = null, string? planMode = null)
        => new()
        {
            Kind = MilkSaveKind.Skipped,
            Reason = reason,
            RunId = runId,
            PlanMode = planMode
        };

    public static MilkSaveOutcome Fail(string reason, string? runId = null, string? planMode = null, int pickRows = 0)
        => new()
        {
            Kind = MilkSaveKind.Failed,
            Reason = reason,
            RunId = runId,
            PlanMode = planMode,
            PickRows = pickRows
        };

    public static MilkSaveOutcome NotConfigured()
        => new() { Kind = MilkSaveKind.NotConfigured, Reason = "MilkPick/Ticket 未注入" };
}
