using AiFootBall.Core.Models;
using Xunit;

namespace AiFootBall.Tests;

public class MilkEventTaxonomyTests
{
    [Fact]
    public void Goals7p_Hits()
    {
        var tags = MilkEventTaxonomy.ClassifyFinished(new() { Home = 4, Away = 3 });
        Assert.Contains(tags, t => t.Tag == MilkEventTaxonomy.Tags.Goals7p);
        Assert.Contains(tags, t => t.Tag == MilkEventTaxonomy.Tags.ScoreOtherHome); // 4:3 胜其它
    }

    [Fact]
    public void StandardScore_NotOther()
    {
        var tags = MilkEventTaxonomy.ClassifyFinished(new() { Home = 2, Away = 1 });
        Assert.DoesNotContain(tags, t => t.Tag == MilkEventTaxonomy.Tags.ScoreOtherHome);
        Assert.DoesNotContain(tags, t => t.Tag == MilkEventTaxonomy.Tags.Goals7p);
    }

    [Fact]
    public void SixNil_IsScoreOtherHome()
    {
        var tags = MilkEventTaxonomy.ClassifyFinished(new() { Home = 6, Away = 0 });
        Assert.Contains(tags, t => t.Tag == MilkEventTaxonomy.Tags.ScoreOtherHome);
        Assert.DoesNotContain(tags, t => t.Tag == MilkEventTaxonomy.Tags.Goals7p); // 6 球不算 7+
    }

    [Fact]
    public void ZeroSix_IsScoreOtherAway()
    {
        var tags = MilkEventTaxonomy.ClassifyFinished(new() { Home = 0, Away = 6 });
        Assert.Contains(tags, t => t.Tag == MilkEventTaxonomy.Tags.ScoreOtherAway);
    }

    [Fact]
    public void HfWinLose_And_LoseWin()
    {
        var winLose = MilkEventTaxonomy.ClassifyFinished(new()
        {
            Home = 1, Away = 2, HalfHome = 1, HalfAway = 0
        });
        Assert.Contains(winLose, t => t.Tag == MilkEventTaxonomy.Tags.HfWinLose);

        var loseWin = MilkEventTaxonomy.ClassifyFinished(new()
        {
            Home = 2, Away = 1, HalfHome = 0, HalfAway = 1
        });
        Assert.Contains(loseWin, t => t.Tag == MilkEventTaxonomy.Tags.HfLoseWin);
    }

    [Fact]
    public void UpsetHome_LikeBarcaStyle()
    {
        var tags = MilkEventTaxonomy.ClassifyFinished(new()
        {
            Home = 0, Away = 4,
            EuHome = 1.25m,
            ExHome = 82m
        });
        Assert.Contains(tags, t => t.Tag == MilkEventTaxonomy.Tags.UpsetHome);
        // 0:4 在竞彩标准客胜格内，不算负其它
        Assert.DoesNotContain(tags, t => t.Tag == MilkEventTaxonomy.Tags.ScoreOtherAway);
    }

    [Fact]
    public void ZeroFour_IsExactAwayNotOther()
    {
        Assert.False(MilkEventTaxonomy.IsJcScoreOtherAway(0, 4));
        Assert.Contains("0:4", MilkEventTaxonomy.JcExactAwayScores);
    }

    [Fact]
    public void Watch_HighGoals_And_Upset()
    {
        var tags = MilkEventTaxonomy.ClassifyWatch(new()
        {
            TotalLine = 3.25m,
            Jq7Sp = 15m,
            EuHome = 1.18m,
            ExHome = 85m,
            BqWinLoseSp = 12m,
            BfOtherHomeSp = 40m
        });
        Assert.Contains(tags, t => t.Tag == MilkEventTaxonomy.Tags.Goals7pWatch);
        Assert.Contains(tags, t => t.Tag == MilkEventTaxonomy.Tags.UpsetWatch);
        Assert.Contains(tags, t => t.Tag == MilkEventTaxonomy.Tags.HfFlipWatch);
        Assert.Contains(tags, t => t.Tag == MilkEventTaxonomy.Tags.ScoreOtherWatch);
    }

    [Fact]
    public void LoseDraw_LikeSunday001()
    {
        var tags = MilkEventTaxonomy.ClassifyFinished(new()
        {
            Home = 1, Away = 1, HalfHome = 0, HalfAway = 1
        });
        Assert.Contains(tags, t => t.Tag == MilkEventTaxonomy.Tags.HfLoseDraw);
        Assert.DoesNotContain(tags, t => t.Tag == MilkEventTaxonomy.Tags.HfLoseWin);
    }

    [Fact]
    public void Watch_DrawBack_At_15_Is_In_And_Just_Over_Is_Out()
    {
        var on = MilkEventTaxonomy.ClassifyWatch(new()
        {
            BqWinDrawSp = 15m,
            BqLoseDrawSp = 14.50m,
            BqLoseWinSp = 24m
        });
        var note = Assert.Single(on, t => t.Tag == MilkEventTaxonomy.Tags.HfFlipWatch).Note;
        Assert.Contains("胜/平 SP 15", note);
        Assert.Contains("负/平 SP 14.5", note);
        Assert.DoesNotContain("负/胜", note);

        var off = MilkEventTaxonomy.ClassifyWatch(new() { BqLoseDrawSp = 15.5m, BqWinDrawSp = 16m });
        Assert.DoesNotContain(off, t => t.Tag == MilkEventTaxonomy.Tags.HfFlipWatch);
    }
}
