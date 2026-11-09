using AiFootBall.Core.Models;
using Xunit;

namespace AiFootBall.Tests;

public class TotalLineMathTests
{
    [Fact]
    public void High_Scoring_Teams_On_A_Low_Line_Lean_Over()
    {
        var split = TotalLineMath.Chances(2.2, 1.8, 2.5m);
        Assert.NotNull(split);
        Assert.True(split.Value.Over > split.Value.Under);
        Assert.True(split.Value.Over > 0.65m);
        Assert.Equal(0m, split.Value.OverPush);
    }

    [Fact]
    public void Integer_Line_Keeps_The_Push()
    {
        var split = TotalLineMath.Chances(1.4, 1.4, 3.0m);
        Assert.NotNull(split);
        var sum = split.Value.Over + split.Value.Under + split.Value.OverPush;
        Assert.InRange(sum, 0.98m, 1.02m);
        Assert.True(split.Value.OverPush > 0m);
    }

    [Fact]
    public void Quarter_Line_Counts_A_Half_Win()
    {
        var split = TotalLineMath.Chances(1.6, 1.4, 2.75m);
        Assert.NotNull(split);
        Assert.True(split.Value.OverPush > 0m);
        Assert.True(split.Value.Over + split.Value.Under <= 1.01m);
    }

    [Fact]
    public void Note_Says_Reference_Only()
    {
        var rec = new MatchRecommendation
        {
            LeagueName = "德甲",
            HomeTeam = "勒沃库森",
            AwayTeam = "斯图加特",
            TotalLine = 3.5m
        };
        TotalLineMath.Apply(rec, ScorelineModel.Empty);
        Assert.NotNull(rec.OuSide);
        Assert.Contains("只作参考，不进精选", rec.OuNote);
        Assert.Contains("3.5", rec.OuNote);
    }

    [Theory]
    [InlineData(3, 2.5, "大", "赢")]
    [InlineData(2, 2.5, "大", "输")]
    [InlineData(2, 2.5, "小", "赢")]
    [InlineData(2, 2.0, "大", "走盘")]
    [InlineData(2, 2.0, "小", "走盘")]
    [InlineData(3, 2.0, "大", "赢")]
    [InlineData(2, 2.25, "大", "输半")]
    [InlineData(2, 2.25, "小", "赢半")]
    [InlineData(3, 2.75, "大", "赢半")]
    [InlineData(3, 2.75, "小", "输半")]
    [InlineData(4, 2.75, "大", "赢")]
    [InlineData(2, 2.75, "大", "输")]
    public void Grade_Settles_Asian_Totals(int goals, double line, string side, string expected)
    {
        Assert.Equal(expected, TotalLineMath.Grade(goals, (decimal)line, side));
    }
}
