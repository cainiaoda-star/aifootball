namespace AiFootBall.Core.Models;

/// <summary>推荐是否算一次有效预测，以及和赛果比较后是否命中。</summary>
public static class RecommendationScoring
{
    /// <summary>竞彩/北单对外发布、以及默认命中率计入的最低置信度。</summary>
    public const decimal PublishConfidence = 60m;

    /// <summary>
    /// 精选门槛：校准后的预计命中率。写死的 61.3、73.8、85.7 不能代替它。
    /// </summary>
    public const decimal MinFeaturedHitProbability = 55m;

    /// <summary>低于此水位只标注。1.50 及以下注明奖金薄，1.50 到 1.90 注明不是稳胆。不抬置信度。</summary>
    public const decimal MinPublishOdds = 1.90m;

    public static bool IsDirectionalPick(char? recResult, bool isCoverAll)
        => !isCoverAll && recResult is '3' or '1' or '0';

    /// <summary>全包或没有方向时返回 null，不参与命中率。</summary>
    public static bool? DirectionalHit(char? recResult, bool isCoverAll, char actualResult)
    {
        if (!IsDirectionalPick(recResult, isCoverAll)) return null;
        return recResult == actualResult;
    }

    public static bool CountsInHitRate(char? recResult, bool isCoverAll, decimal? confidence)
        => IsDirectionalPick(recResult, isCoverAll) && confidence >= PublishConfidence;
}
