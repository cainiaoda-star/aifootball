using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

public class BigWinScoreTests
{
    [Theory]
    [InlineData('0', 9.40, 1.15, 3.34, 2)]   // 勒阿弗尔 vs 巴黎：客胜 1.15，大小球 3.34
    [InlineData('0', 9.40, 1.15, 2.75, 1)]   // 盘口不够高
    [InlineData('3', 2.57, 2.17, 2.79, 1)]   // 尼斯 vs 雷恩：势均，不算
    [InlineData('3', 9.40, 1.15, 3.50, 1)]   // 选的不是热门那边
    public void BigWinMargin_OnlyStrongFavouriteWithHighLine(
        char dir, double home, double away, double line, int expected)
    {
        Assert.Equal(expected, RecommendationEngine.BigWinMargin(dir, (decimal)home, (decimal)away, (decimal)line));
    }

    [Fact]
    public void Scoreline_MinMargin_DropsOneGoalWins()
    {
        var samples = new List<ScoreSample>();
        for (var i = 0; i < 40; i++)
        {
            samples.Add(new ScoreSample { LeagueName = "法甲", HomeTeam = "勒阿弗尔", AwayTeam = "X" + i, HomeScore = 1, AwayScore = 2 });
            samples.Add(new ScoreSample { LeagueName = "法甲", HomeTeam = "Y" + i, AwayTeam = "巴黎", HomeScore = 0, AwayScore = 3 });
        }
        var model = ScorelineModel.Fit(samples);
        Assert.True(model.Ready);

        var top = model.Top("法甲", "勒阿弗尔", "巴黎", '0', 3.34m, 2m, minMargin: 2);
        Assert.NotEmpty(top);
        Assert.All(top, s => Assert.True(s.Away - s.Home >= 2, $"{s.Home}:{s.Away}"));
    }
}
