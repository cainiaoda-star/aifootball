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
    [InlineData("日联赛杯", "日联杯")]   // 2026-11-11：竞彩卡写法，FbMatch 里 407 场叫「日联杯」
    [InlineData(" 韩职 ", "K1联赛")]   // 采集端带空格也不能漏
    public void Resolve_MapsJcNamesToSourceNames(string jc, string expected)
        => Assert.Equal(expected, LeagueNames.Resolve(jc));

    [Fact]
    public void Resolve_DoesNotTouchWomensLeague()
    {
        // 「女日联杯」是另一项赛事，403 条精确匹配不能改成模糊前缀，否则会把女足挂到男足均值上。
        Assert.Equal("女日联杯", LeagueNames.Resolve("女日联杯"));
        Assert.Equal("女日联赛杯", LeagueNames.Resolve("女日联赛杯"));
    }

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

    [Fact]
    public void ResolveLeague_FallsBackWhenAliasedLeagueLacksTeamSamples()
    {
        // 2026-11-11 事故复现（用户看到的现象：榜上 4 场忽然只剩 2 场）。
        // 真实现场：近 18 个月训练窗口里 鹿岛鹿角 日联杯 0 场、神户胜利船/町田泽维亚 各 2 场，
        // 而它们在日职各有 56~59 场。别名把联赛从「日职」改成「日联杯」之后，
        // 这两场整场被判「模型不认识」/「样本不足」直接从榜上消失。
        // 结论：别名只在该联赛**两队都有量**时才允许盖过队名反查。
        var model = FitJLeagueCupThinC();
        Assert.Equal("日职", model.ResolveLeague("日联赛杯", "横滨水手", "鹿岛鹿角", minGames: 3));
        // 两队都在日联杯有量时，仍然尊重别名（这才是别名本来的作用）
        Assert.Equal("日联杯", model.ResolveLeague("日联赛杯", "横滨水手", "川崎前锋", minGames: 3));
        // 不传门槛时行为与改动前一致，老调用点不会被这次修改影响
        Assert.Equal("日联杯", model.ResolveLeague("日联赛杯", "横滨水手", "鹿岛鹿角"));
    }

    [Fact]
    public void ResolveLeague_JLeagueCup_BeatsTeamInference()
    {
        // 反面教材现场（2026-11-11）：横滨水手 / 鹿岛鹿角 在日职交手远多于日联杯，
        // 所以一旦别名缺席，「两队共同出场最多的联赛」会把杯赛猜成日职——
        // 拿联赛场均去算一场杯赛。别名在表里时必须在第一级就命中，不许走到队名反查。
        var model = FitJLeagueSamples();
        Assert.True(model.HasLeague("日联杯"));
        Assert.False(model.HasLeague("日联赛杯"));
        Assert.Equal("日联杯", model.ResolveLeague("日联赛杯", "横滨水手", "鹿岛鹿角"));
    }

    /// <summary>
    /// 造「日联杯里没有鹿岛鹿角」的样本，复现 2026-11-11 的真实缺口：
    /// 横滨/川崎 两队踢满日联杯，鹿岛/柏只踢日职。
    /// </summary>
    private static ScorelineModel FitJLeagueCupThinC()
    {
        var samples = new List<ScoreSample>();
        void Add(string league, string home, string away, int times)
        {
            for (var i = 0; i < times; i++)
                samples.Add(new ScoreSample
                {
                    LeagueName = league,
                    HomeTeam = home,
                    AwayTeam = away,
                    HomeScore = 1 + i % 3,
                    AwayScore = i % 2,
                });
        }
        Add("日联杯", "横滨水手", "川崎前锋", 20);   // 这两队在日联杯各有 20 场
        Add("日职", "横滨水手", "鹿岛鹿角", 20);     // 鹿岛只在日职出场
        Add("日职", "川崎前锋", "柏太阳神", 20);     // 柏同理
        Add("日职", "横滨水手", "柏太阳神", 10);
        Add("日职", "鹿岛鹿角", "川崎前锋", 10);
        return ScorelineModel.Fit(samples);
    }

    /// <summary>造日联杯 + 日职 两套样本，且日职场次明显更多（模拟真实的交手次数差）。</summary>
    private static ScorelineModel FitJLeagueSamples()
    {
        var teams = new[] { "横滨水手", "鹿岛鹿角", "川崎前锋", "柏太阳神" };
        var samples = new List<ScoreSample>();
        void Add(string league, int times)
        {
            for (var i = 0; i < times; i++)
                for (var t = 0; t < teams.Length; t += 2)
                    samples.Add(new ScoreSample
                    {
                        LeagueName = league,
                        HomeTeam = teams[t],
                        AwayTeam = teams[t + 1],
                        HomeScore = 1 + (i + t) % 3,
                        AwayScore = (i + t) % 2,
                    });
        }
        Add("日联杯", 20);   // 40 场
        Add("日职", 60);     // 120 场 —— 队名反查会选它，所以才需要别名
        return ScorelineModel.Fit(samples);
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
