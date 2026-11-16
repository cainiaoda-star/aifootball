namespace AiFootBall.Sources;

/// <summary>
/// 勒沃库森那种热门：主胜水位在 1.50 到 1.90，小降，必发主胜 60% 到 75%。
/// 只标注「警惕爆冷」，不进精选门槛，也不进投注。
/// </summary>
public static class UpsetWatch
{
    public static bool Matches(decimal? homeOdds, decimal? homeDelta, decimal? ratioHome)
    {
        if (homeOdds is null || homeOdds <= 1.50m || homeOdds >= 1.90m) return false;
        if (homeDelta is null || homeDelta >= -0.05m || homeDelta <= -0.20m) return false;
        if (ratioHome is null || ratioHome < 60m || ratioHome >= 75m) return false;
        return true;
    }

    public static string Note(decimal homeOdds, decimal homeDelta, decimal ratioHome)
        => $"警惕爆冷：主胜水位 {homeOdds:0.00}，小降 {homeDelta:0.00}，必发主胜 {ratioHome:0.#}%。1.50 到 1.90 的热门不是稳胆，不购买。";
}
