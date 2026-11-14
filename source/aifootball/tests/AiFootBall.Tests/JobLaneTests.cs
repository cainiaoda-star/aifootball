using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

public class JobLaneTests
{
    [Fact]
    public void A1_Cards_Lane_Is_Independent()
    {
        var cards = JobScheduler.LaneNameOf("cards");
        Assert.Equal("cards", cards);
        Assert.NotEqual("main", cards);
        Assert.NotEqual("recommend", cards);
        Assert.NotEqual("jc-odds", cards);
    }

    [Fact]
    public void Recommend_And_JcOdds_Lanes_Unchanged()
    {
        Assert.Equal("recommend", JobScheduler.LaneNameOf("save-recommendations"));
        Assert.Equal("jc-odds", JobScheduler.LaneNameOf("jc-odds-sample"));
        Assert.Equal("main", JobScheduler.LaneNameOf("odds-sample"));
    }
}
