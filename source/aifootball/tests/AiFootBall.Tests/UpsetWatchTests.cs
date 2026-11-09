using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

public class UpsetWatchTests
{
    [Fact]
    public void Leverkusen_Shape_Is_Flagged_Until_The_Heat_Crosses_75()
    {
        Assert.True(UpsetWatch.Matches(1.66m, -0.09m, 72.2m));
        Assert.True(UpsetWatch.Matches(1.76m, -0.10m, 72.4m));
        Assert.False(UpsetWatch.Matches(1.66m, -0.09m, 75.2m));
        Assert.False(UpsetWatch.Matches(1.66m, -0.09m, 59.9m));
        Assert.False(UpsetWatch.Matches(1.50m, -0.09m, 72m));
        Assert.False(UpsetWatch.Matches(1.90m, -0.09m, 72m));
        Assert.False(UpsetWatch.Matches(1.66m, -0.05m, 72m));
        Assert.False(UpsetWatch.Matches(1.66m, -0.20m, 72m));
        Assert.False(UpsetWatch.Matches(1.23m, -0.06m, 68.9m));
    }

    [Fact]
    public void Note_Says_Watch_The_Upset_And_Do_Not_Buy()
    {
        var note = UpsetWatch.Note(1.66m, -0.09m, 72.2m);
        Assert.Contains("警惕爆冷", note);
        Assert.Contains("不购买", note);
        Assert.Contains("1.66", note);
    }
}