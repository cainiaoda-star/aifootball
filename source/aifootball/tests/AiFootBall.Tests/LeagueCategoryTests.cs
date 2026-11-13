using AiFootBall.Core.Models;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 赛事类型分类测试（2026-11-13 加，随 LeagueCategory 从 MilkPickEngine 迁到 Core）。
///
/// 这一层同时被两处消费：NoPickLeagues（友谊赛整类不出注）与 MilkPickEngine（友谊赛不发反转档、
/// 杯赛低水热门触发 CupHotFade）。所以错一档就是两种后果：
///   · 把正式国际赛事当成友谊赛 → 世界杯/欧洲杯这种整类被砍，榜面悄悄少一块，线上不报错；
///   · 把热身赛当成联赛 → 押的是没有动机约束的比赛，模型按 2~4 倍高估的顺风档下注。
/// 11-13 的实据：竞彩把国家队热身赛叫「国际赛」，旧的判据只认「友谊」，整类漏网。
/// </summary>
public class LeagueCategoryTests
{
    [Theory]
    // 竞彩对国家队热身赛的叫法，以及各种友谊/热身写法
    [InlineData("国际赛")]
    [InlineData("国际友谊赛")]
    [InlineData("友谊赛")]
    [InlineData("球会友谊")]
    [InlineData("热身赛")]
    [InlineData("季前热身")]
    public void 友谊赛类(string league)
        => Assert.Equal(LeagueCategory.Friendly, LeagueCategory.Of(league));

    [Theory]
    // 正式国际赛事：绝不能被归为友谊赛。最后一条是拼接叫法的兜底（正式赛事优先判定）
    [InlineData("世界杯")]
    [InlineData("世界杯预选赛")]
    [InlineData("欧洲杯")]
    [InlineData("欧洲杯预选赛")]
    [InlineData("欧国联")]
    [InlineData("世预赛")]
    [InlineData("亚洲杯")]
    [InlineData("美洲杯")]
    [InlineData("非洲杯")]
    [InlineData("联合会杯")]
    [InlineData("国际赛-世界杯预选赛")]
    public void 正式国际赛事不算友谊赛(string league)
    {
        Assert.NotEqual(LeagueCategory.Friendly, LeagueCategory.Of(league));
        Assert.Equal(LeagueCategory.Cup, LeagueCategory.Of(league));
        Assert.True(LeagueCategory.IsMajorTournament(league), $"{league} 应当被认成正式国际赛事");
        // 连着名单一起钉：正式国际赛事不在禁推名单里（禁推的是欧国联与青年赛，不是所有国家队比赛）
        if (league != "欧国联") Assert.False(NoPickLeagues.IsExcluded(league));
    }

    [Theory]
    [InlineData("英超")]
    [InlineData("西甲")]
    [InlineData("日职")]
    [InlineData("巴甲")]
    [InlineData("英冠")]
    public void 常规联赛(string league)
    {
        Assert.Equal(LeagueCategory.League, LeagueCategory.Of(league));
        Assert.False(LeagueCategory.IsFriendly(league));
    }

    [Fact]
    public void 空名字按联赛处理()
    {
        Assert.Equal(LeagueCategory.League, LeagueCategory.Of(null));
        Assert.Equal(LeagueCategory.League, LeagueCategory.Of(""));
        Assert.Equal(LeagueCategory.League, LeagueCategory.Of("   "));
        Assert.False(LeagueCategory.IsFriendly(null));
    }

    [Fact]
    public void 禁推名单与引擎判定同源()
    {
        // 名单里的「国际赛」和引擎对友谊赛的理解必须是同一份判定：
        // 11-13 就是两处各写一份，名单只认「友谊赛」，卡名「国际赛」整类漏网。
        foreach (var lg in new[] { "国际赛", "友谊赛", "球会友谊", "热身赛", "国际友谊赛" })
        {
            Assert.True(LeagueCategory.IsFriendly(lg), $"{lg} 应判为友谊赛类");
            Assert.True(NoPickLeagues.IsExcluded(lg), $"{lg} 应进禁推名单");
        }
    }
}
