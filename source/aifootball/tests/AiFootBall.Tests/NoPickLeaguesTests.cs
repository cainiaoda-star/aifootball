using AiFootBall.Core.Models;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 名单分类测试（2026-11-09 加）。
///
/// 2026-11-16 起这张表不再决定竞彩大奶入不入漏斗。这里只钉名字还在不在名单里，
/// 大奶是否出注看 MilkBandAndScoreTests 的「能中」。
/// </summary>
public class NoPickLeaguesTests
{
    [Theory]
    [InlineData("欧国联")]
    [InlineData("女欧国联")]
    [InlineData("友谊赛")]
    [InlineData("国际友谊赛")]
    [InlineData("球会友谊")]
    [InlineData("国际赛")]     // 竞彩对国家队热身赛的叫法（2026-11-13 补，11-13 周五001 就是这么漏的）
    [InlineData("热身赛")]
    [InlineData("欧U17")]
    [InlineData("亚U17")]
    [InlineData("欧U19")]
    public void 国际赛与青年赛不出注(string league)
        => Assert.True(NoPickLeagues.IsExcluded(league), $"{league} 应当被拦");

    [Theory]
    [InlineData("西甲")]
    [InlineData("英超")]
    [InlineData("欧罗巴")]
    [InlineData("日职")]
    [InlineData("巴甲")]
    [InlineData("澳超")]   // 澳超只是不进串关，单关照发——两张名单不能混
    [InlineData("英冠")]
    [InlineData("世界杯")]      // 正式国际赛事不受「国际赛=友谊赛」的连累
    [InlineData("欧洲杯")]
    [InlineData("世预赛")]
    public void 常规联赛照常出注(string league)
        => Assert.False(NoPickLeagues.IsExcluded(league), $"{league} 不该被拦");

    [Fact]
    public void 空联赛名不拦()
    {
        Assert.False(NoPickLeagues.IsExcluded(null));
        Assert.False(NoPickLeagues.IsExcluded(""));
        Assert.False(NoPickLeagues.IsExcluded("   "));
    }

    [Fact]
    public void 不与不进串关名单混同()
    {
        // 澳超：不进串关（NoParlayLeagues）但照常出单关（NoPickLeagues 不拦）。
        Assert.True(NoParlayLeagues.IsExcluded("澳超"));
        Assert.False(NoPickLeagues.IsExcluded("澳超"));

        // 欧国联仍在稳胆名单里。竞彩大奶不再因此在入漏斗前丢掉整场。
        Assert.True(NoPickLeagues.IsExcluded("欧国联"));
    }
}
