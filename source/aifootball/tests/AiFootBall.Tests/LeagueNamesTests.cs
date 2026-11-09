using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 联赛名对齐的护栏。这件事的代价是被真实场次量出来的：
/// 竞彩「韩职」在比分库里一场都没有（真名 K1联赛 1351 场），
/// 模型按「韩职」查队 → 0/0 场 → 整场判「样本不足」→ 整类联赛永不出注。
/// 2026-11-08 周日002 首尔FC vs 蔚山现代、005 江原FC vs 全北现代 都是这样被判死的。
/// </summary>
public class LeagueNamesTests
{
    [Theory]
    [InlineData("韩职", "K1联赛")]
    [InlineData("日乙", "日职乙")]
    [InlineData("瑞超", "瑞典超")]
    [InlineData("美职足", "美职联")]
    [InlineData("芬兰超级联赛", "芬超")]
    [InlineData("亚冠", "亚冠杯")]
    [InlineData("亚洲冠军乙级联赛", "亚冠联2")]
    [InlineData("英联赛杯", "英联杯")]
    [InlineData(" 韩职 ", "K1联赛")]   // 采集端带空格也不能漏
    public void Resolve_MapsJcNamesToSourceNames(string jc, string expected)
        => Assert.Equal(expected, LeagueNames.Resolve(jc));

    [Fact]
    public void Resolve_LeavesUnknownNamesAlone()
    {
        // 表里没有的一律原样返回：模糊匹配猜错的代价是按别人的联赛均值出注，比查不到更糟。
        Assert.Equal("英超", LeagueNames.Resolve("英超"));
        Assert.Equal("某新联赛", LeagueNames.Resolve("某新联赛"));
        Assert.Equal("", LeagueNames.Resolve(null));
        Assert.Equal("", LeagueNames.Resolve("   "));
    }

    [Fact]
    public void ResolveLeague_UsesTableWhenTargetLeagueHasSamples()
    {
        var model = FitKoreanSamples();
        // 「韩职」在模型里是 0 场（训练样本只有 K1联赛），必须被对齐过去
        Assert.Equal("K1联赛", model.ResolveLeague("韩职", "首尔FC", "蔚山现代"));
        Assert.True(model.HasLeague("K1联赛"));
        Assert.False(model.HasLeague("韩职"));
    }

    [Fact]
    public void ResolveLeague_FallsBackToTeamsWhenTableMisses()
    {
        var model = FitKoreanSamples();
        // 表里没有的名字，靠「两队共同出场最多的联赛」反查——新联赛出现两套叫法时不用改表
        Assert.Equal("K1联赛", model.ResolveLeague("韩国顶级联赛", "首尔FC", "蔚山现代"));
    }

    [Fact]
    public void ResolveLeague_KeepsRealLeagueNameUnchanged()
    {
        var model = FitKoreanSamples();
        // 联赛名本来就对得上（训练集里有样本），不能被任何兜底改走
        Assert.Equal("K1联赛", model.ResolveLeague("K1联赛", "首尔FC", "蔚山现代"));
    }

    [Fact]
    public void ResolveLeague_ReturnsOriginalWhenNothingMatches()
    {
        var model = FitKoreanSamples();
        // 两队都不认识、名字也不在表里 → 原样返回，行为与改动之前一致（宁可丢场，不乱猜）
        Assert.Equal("某杯赛", model.ResolveLeague("某杯赛", "甲队", "乙队"));
    }

    [Fact]
    public void HalfFullBaseline_AcceptsJcLeagueName()
    {
        // 表里那一行是「K1联赛」写的；竞彩写「韩职」也必须取到同一行，否则永远走兜底均值
        Assert.True(LeagueHalfFullBaseline.Known("韩职"));
        Assert.Equal(LeagueHalfFullBaseline.Pct("K1联赛", "3-0"), LeagueHalfFullBaseline.Pct("韩职", "3-0"));
        Assert.NotEqual(LeagueHalfFullBaseline.Pct("英超", "3-0"), LeagueHalfFullBaseline.Pct("韩职", "3-0"));
    }

    /// <summary>造一个只含 K1联赛 的训练集（Fit 要求 ≥30 场，否则返回 Empty）。</summary>
    private static ScorelineModel FitKoreanSamples()
    {
        var teams = new[] { "首尔FC", "蔚山现代", "江原FC", "全北现代" };
        var samples = new List<ScoreSample>();
        for (var i = 0; i < 20; i++)
        {
            for (var t = 0; t < teams.Length; t += 2)
            {
                samples.Add(new ScoreSample
                {
                    LeagueName = "K1联赛",
                    HomeTeam = teams[t],
                    AwayTeam = teams[t + 1],
                    HomeScore = 1 + (i + t) % 3,
                    AwayScore = (i + t) % 2,
                });
            }
        }
        return ScorelineModel.Fit(samples);
    }
}
