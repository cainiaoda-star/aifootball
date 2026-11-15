using AiFootBall.Core.Models;
using AiFootBall.Data;

namespace AiFootBall.Sources;

/// <summary>
/// <see cref="RecommendationService.RecommendAllAndSaveAsync"/> 的可注入接缝。
/// 生产传 null。替身不得返回已拼好的 <see cref="RecommendSaveResult"/>——
/// 发布与否必须仍经 <see cref="RecommendSaveResult.FromMilkOutcome"/> 传播。
/// </summary>
public sealed class RecommendSaveHooks
{
    public Func<CancellationToken, Task<MilkGateResult>>? EvaluateGate { get; init; }

    /// <summary>
    /// 过闸后的大奶落库替身：返回 boards + <see cref="MilkSaveOutcome"/>。
    /// 入口仍调用 FromMilkOutcome；不得在此直接宣称 Published。
    /// </summary>
    public Func<CancellationToken, Task<(Dictionary<string, List<MatchRecommendation>> Boards, MilkSaveOutcome Milk)>>?
        AfterGateSaveMilk { get; init; }

    public Func<MilkGateResult, CancellationToken, Task>? RecordRejection { get; init; }

    public Func<DateTime, CancellationToken, Task<MilkBoardStatus?>>? GetBoardStatus { get; init; }
}
