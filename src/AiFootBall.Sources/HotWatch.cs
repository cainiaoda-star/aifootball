using AiFootBall.Core.Models;

namespace AiFootBall.Sources;

/// <summary>
/// 必发主胜或客胜达到 75% 是极端大热。这一侧不留在推荐里，改到大热区只展示。
/// 60% 到 75% 仍是偏热，可以留在推荐里。
/// </summary>
public static class HotWatch
{
    public const decimal Line = 75m;

    public static void Mark(MatchRecommendation rec)
    {
        char? side = null;
        decimal? ratio = null;
        if (rec.RatioHome >= Line && (rec.RatioAway is null || rec.RatioHome >= rec.RatioAway))
        {
            side = '3';
            ratio = rec.RatioHome;
        }
        else if (rec.RatioAway >= Line)
        {
            side = '0';
            ratio = rec.RatioAway;
        }

        if (side is null || ratio is null) return;

        rec.HotWatch = true;
        rec.HotSide = side;
        rec.HotRatio = ratio;
        var name = side == '3' ? "主胜" : "客胜";
        rec.HotNote = $"必发{name} {ratio:0.#}%，极端大热，不追。从推荐挪到这里，只展示。";

        if (rec.RecResult == side)
        {
            rec.RecResult = null;
            rec.IsFeatured = false;
            rec.SignalLevel = 0;
            rec.Confidence = null;
        }

        if (rec.RecHandicap == side)
        {
            rec.RecHandicap = null;
            rec.HandicapProbability = null;
            rec.HandicapOdds = null;
        }
    }
}
