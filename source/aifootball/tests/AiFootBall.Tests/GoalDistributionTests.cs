using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 复盘里「进球与比分分布」这一节。它只做统计，但一旦算错，会直接把人往错的档位推——
/// 所以档位归属、占比、基线对照这三件事都锁死。
/// </summary>
public class GoalDistributionTests
{
    static ReviewRow Row(string matchNo, string league, string score) => new()
    {
        Board = Boards.Jc, MatchNo = matchNo, League = league,
        Home = "主队", Away = "客队", ScoreText = score, Finished = true
    };

    [Fact]
    public void Buckets_Count_By_Total_Goals_And_7Plus_Is_Last()
    {
        var rows = new[]
        {
            Row("周六001", "英超", "0:0"),   // 0
            Row("周六002", "英超", "1:0"),   // 1
            Row("周六003", "德甲", "1:1"),   // 2
            Row("周六004", "德甲", "2:1"),   // 3
            Row("周六005", "西甲", "2:2"),   // 4
            Row("周六006", "西甲", "5:2"),   // 7 → 7+
            Row("周六007", "西甲", "4:3")    // 7 → 7+
        };

        var md = GoalDistribution.ToMarkdown(rows, null, 730);

        Assert.Contains("| 0 球 | 1 | 14.3% |", md);
        Assert.Contains("| 3 球 | 1 | 14.3% |", md);
        // 7 球和 9 球都进「7+ 球」这一档，不能各算一行
        Assert.Contains("| 7+ 球 | 2 | 28.6% |", md);
        Assert.Contains("5:2", md);
        Assert.Contains("4:3", md);
    }

    [Fact]
    public void Without_Baseline_Still_Renders_And_Says_So()
    {
        var md = GoalDistribution.ToMarkdown(new[] { Row("周六001", "英超", "1:1") }, null, 730);

        // 没有基线时不能凭空编一列基线占比
        Assert.DoesNotContain("近 730 天占比", md);
        Assert.Contains("没取到基线", md);
        Assert.Contains("1:1", md);
    }

    [Fact]
    public void Baseline_Column_Is_Percentage_Of_Baseline_Not_Of_Today()
    {
        var rows = new[]
        {
            Row("周六001", "英超", "1:1"),
            Row("周六002", "英超", "1:1")
        };
        // 基线 100 场里 2 球 25 场 = 25.0%
        var baseline = new[]
        {
            new GoalSampleRow { LeagueName = "英超", HomeScore = 1, AwayScore = 1, Matches = 25 },
            new GoalSampleRow { LeagueName = "英超", HomeScore = 1, AwayScore = 0, Matches = 75 }
        };

        var md = GoalDistribution.ToMarkdown(rows, baseline, 730);

        // 今天 2 球占 100%，基线占 25.0%，差额 +75.0pt
        Assert.Contains("| 2 球 | 2 | 100.0% | 25.0% | +75.0pt |", md);
    }

    [Fact]
    public void Unfinished_Match_Is_Listed_As_Pending_Not_Counted()
    {
        var rows = new[]
        {
            Row("周六001", "英超", "1:0"),
            new ReviewRow { Board = Boards.Jc, MatchNo = "周六030", League = "美职足", Home = "盐湖城", Away = "洛城银河", ScoreText = "", Finished = false }
        };

        var md = GoalDistribution.ToMarkdown(rows, null, 730);

        Assert.Contains("1 场待补", md);
        Assert.Contains("周六030", md);
        // 统计分母只能用有比分的那一场
        Assert.Contains("今天共 1 球，场均 1.00", md);
    }
}
