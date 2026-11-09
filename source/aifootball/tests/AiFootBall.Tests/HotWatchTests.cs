using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

public class HotWatchTests
{
    [Fact]
    public void Home_At_75_Leaves_The_Recommendation_And_Is_Shown_As_Hot()
    {
        var rec = new MatchRecommendation
        {
            Board = "jc",
            MatchNo = "周日003",
            RecResult = '3',
            RecHandicap = '3',
            SignalLevel = 2,
            HitProbability = 60.1m,
            HandicapProbability = 58m,
            RatioHome = 75.5m,
            RatioAway = 20.5m
        };

        HotWatch.Mark(rec);

        Assert.True(rec.HotWatch);
        Assert.Equal('3', rec.HotSide);
        Assert.Equal(75.5m, rec.HotRatio);
        Assert.Null(rec.RecResult);
        Assert.Null(rec.RecHandicap);
        Assert.Empty(RecommendationSelection.ForJcCalculated(new[] { rec }));
        Assert.Empty(RecommendationSelection.ForJcHandicap(new[] { rec }));
    }

    [Fact]
    public void Home_At_74_4_Stays_In_The_Recommendation()
    {
        var rec = new MatchRecommendation
        {
            Board = "jc",
            MatchNo = "周日003",
            RecResult = '3',
            RecHandicap = '1',
            SignalLevel = 2,
            HitProbability = 60.1m,
            HandicapProbability = 55m,
            RatioHome = 74.4m,
            RatioAway = 18.2m
        };

        HotWatch.Mark(rec);

        Assert.False(rec.HotWatch);
        Assert.Equal('3', rec.RecResult);
        Assert.Equal('1', rec.RecHandicap);
        Assert.Contains(RecommendationSelection.ForJcCalculated(new[] { rec }), x => x.MatchNo == "周日003");
        Assert.Contains(RecommendationSelection.ForJcHandicap(new[] { rec }), x => x.RecHandicap == '1');
    }

    [Fact]
    public void 热线只有一个数值来源()
    {
        // Line 必须是 RecommendationScoring.ExtremeHotRatio 的别名，不是另起的 75m。
        // 引擎和模型护栏已经直接引用那个常量了，这里守住第三处。
        Assert.Equal(RecommendationScoring.ExtremeHotRatio, HotWatch.Line);
    }

    [Theory]
    [InlineData(74.9)]
    [InlineData(75.0)]
    public void 热线卡在七十五整(decimal ratio)
    {
        var rec = new MatchRecommendation
        {
            Board = Boards.Jc, MatchNo = "001", RecResult = '3', SignalLevel = 2,
            RatioHome = ratio, RatioAway = 100m - ratio
        };

        HotWatch.Mark(rec);

        // 74.9 只是偏热，仍留在推荐里；75.0 恰好到线，挪去大热区
        Assert.Equal(ratio >= RecommendationScoring.ExtremeHotRatio, rec.HotWatch);
    }
}
