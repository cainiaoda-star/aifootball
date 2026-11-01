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
}
