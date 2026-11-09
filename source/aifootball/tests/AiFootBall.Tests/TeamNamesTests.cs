using AiFootBall.Core.Models;
using Xunit;

namespace AiFootBall.Tests;

public class TeamNamesTests
{
    [Theory]
    [InlineData("布里斯班狮吼", "布里斯班狮吼 FC")]
    [InlineData("布里斯班狮吼", "  布里斯班狮吼　")]
    [InlineData("曼城", "曼城队")]
    [InlineData("大阪钢巴", "大阪钢巴足球俱乐部")]
    [InlineData("奥克兰FC", "奥克兰")]
    public void Same_Ignores_Space_And_Club_Suffix(string left, string right)
    {
        Assert.True(TeamNames.Same(left, right));
        Assert.Equal(TeamNames.PairKey(left, "奥克兰"), TeamNames.PairKey(right, "奥克兰 FC"));
    }

    [Fact]
    public void Same_Does_Not_Treat_Short_Names_As_Aliases()
    {
        Assert.False(TeamNames.Same("曼联", "曼彻斯特联"));
        Assert.False(TeamNames.Same("曼城", "曼城青年"));
        Assert.False(TeamNames.Same("FC", "曼城"));
        Assert.Equal("", TeamNames.PairKey("队", "曼城"));
    }

    [Fact]
    public void ContainsPattern_Escapes_Like_Wildcards()
    {
        Assert.Equal("%曼城%", TeamNames.ContainsPattern("曼城"));
        Assert.Equal("%a[[]b[%]c[_]%", TeamNames.ContainsPattern("a[b%c_"));
    }
}
