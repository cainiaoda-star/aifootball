using AiFootBall.Core.Models;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 队名别名解析（2026-11-05 加）。
///
/// 背景：竞彩官方给简称（布斯巴达 / 帕梅拉斯 / 阿尔克马），训练样本是数据源全称
/// （布拉格斯巴达 / 帕尔梅拉斯 / 阿尔克马尔）。当天 13 场受注卡只认出 4 场，
/// 其余 9 场被当成「模型不认识的队」丢掉——不是模型不行，是名字对不上。
/// </summary>
public class ScorelineAliasTests
{
    private static ScorelineModel Build()
    {
        var samples = new List<ScoreSample>();
        // 凑够 30 场，否则 Fit 会退回 Empty（Ready=false），任何 KnowsTeam 都是 false。
        var teams = new[]
        {
            ("布拉格斯巴达", "维戈塞尔塔"), ("AC米兰", "国际米兰"),
            ("特拉布宗体育", "阿尔克马尔"), ("帕尔梅拉斯", "弗鲁米嫩塞")
        };
        for (var i = 0; i < 12; i++)
        {
            foreach (var (home, away) in teams)
                samples.Add(new ScoreSample
                {
                    LeagueName = "欧罗巴",
                    HomeTeam = home,
                    AwayTeam = away,
                    HomeScore = 1 + (i % 3),
                    AwayScore = i % 2
                });
        }
        return ScorelineModel.Fit(samples);
    }

    [Fact]
    public void Model_IsReady_WithEnoughSamples()
        => Assert.True(Build().Ready);

    [Theory]
    [InlineData("布斯巴达", "布拉格斯巴达")]
    [InlineData("塞尔塔", "维戈塞尔塔")]
    [InlineData("特拉布宗", "特拉布宗体育")]
    [InlineData("阿尔克马", "阿尔克马尔")]
    [InlineData("帕梅拉斯", "帕尔梅拉斯")]
    public void ShortName_ResolvesToFullName(string shortName, string fullName)
    {
        var m = Build();
        Assert.True(m.KnowsTeam("欧罗巴", shortName));
        Assert.Equal(fullName, m.Resolve("欧罗巴", shortName));
    }

    [Fact]
    public void ExactName_StillWorks()
        => Assert.Equal("布拉格斯巴达", Build().Resolve("欧罗巴", "布拉格斯巴达"));

    [Fact]
    public void AmbiguousShortName_IsRejected()
    {
        // 「米兰」同时是 AC米兰 和 国际米兰 的子串，猜错队比不猜更糟。
        var m = Build();
        Assert.Null(m.Resolve("欧罗巴", "米兰"));
        Assert.False(m.KnowsTeam("欧罗巴", "米兰"));
    }

    /// <summary>
    /// 简称撞车时的消歧：梯队要排除，前缀优先（2026-11-05 加）。
    ///
    /// 真实案例：竞彩「波兹南」（Lech Poznań）在库里同时能匹配到「瓦塔波兹南」（Warta Poznań，另一支队）
    /// 和「波兹南莱赫青年队」「波兹南莱赫B队」（梯队）。按老的「命中两支就判歧义」，
    /// 这场的 98 场样本白白作废，整场不出注。规则应该是：梯队不算，剩下的取前缀那支。
    /// </summary>
    [Fact]
    public void ShortName_PrefersFirstTeam_OverReserveAndOtherCity()
    {
        var samples = new List<ScoreSample>();
        var pool = new[]
        {
            ("波兹南莱赫", "甲队"), ("瓦塔波兹南", "乙队"),
            ("波兹南莱赫青年队", "丙队"), ("波兹南莱赫B队", "丁队")
        };
        for (var i = 0; i < 10; i++)
            foreach (var (home, away) in pool)
                samples.Add(new ScoreSample { LeagueName = "波甲", HomeTeam = home, AwayTeam = away, HomeScore = 1, AwayScore = 1 });

        var m = ScorelineModel.Fit(samples);
        Assert.Equal("波兹南莱赫", m.Resolve("欧罗巴", "波兹南"));
        // 一线队和梯队是两套数据，不能因为简称相同就被合并
        Assert.True(m.TeamGames("波甲", "波兹南莱赫") > 0);
        Assert.True(m.TeamGames("波甲", "波兹南莱赫青年队") > 0);
    }

    [Fact]
    public void AliasFallsBackToGlobal_WhenLeagueNameMismatch()
    {
        // 旧语义是「只在同联赛里认，跨联赛一律不认」，2026-11-05 推翻。
        // 起因：竞彩把「亚冠联2」写成「亚洲冠军乙级联赛」，联赛名一错，
        // 町田泽维（库里 85 场）、上海申花（71 场）全被判成「模型不认识」——不是没数据，是名字对不上。
        // 现在联赛查不到就退到全库找，仍要求唯一命中，宁可丢场也不猜错队。
        var m = Build();
        Assert.True(m.KnowsTeam("英超", "布斯巴达"));
        Assert.Equal("布拉格斯巴达", m.Resolve("英超", "布斯巴达"));

        // 但全库也找不到的，仍然要老实说不认识
        Assert.Null(m.Resolve("英超", "町田泽维"));
    }

    [Fact]
    public void UnknownTeam_IsRejected()
        => Assert.False(Build().KnowsTeam("欧罗巴", "町田泽维"));

    [Fact]
    public void AliasTeam_ProducesSameDistributionAsFullName()
    {
        // 别名不只要「认得出」，还要真的用上这支队的数据——否则等于换个说法继续退回先验。
        var m = Build();
        var viaShort = m.Distribution("欧罗巴", "布斯巴达", "塞尔塔", null);
        var viaFull = m.Distribution("欧罗巴", "布拉格斯巴达", "维戈塞尔塔", null);
        Assert.Equal(viaFull.Count, viaShort.Count);
        for (var i = 0; i < viaFull.Count; i++)
            Assert.Equal(viaFull[i].P, viaShort[i].P, 10);
    }

    /// <summary>
    /// 球队样本必须跨联赛累计（2026-11-05 加）。
    ///
    /// 这是「今天大奶榜为什么全空」的根因：模型原来用「联赛+队名」当键，欧战球队在本赛事只有 2 场，
    /// 尽管它在国内联赛有上百场也帮不上忙，全被「样本不足」挡掉。一支队踢联赛、杯赛、欧战是同一支队。
    /// 但也不能直接相加进球——荷甲场均进球比意甲高，直接加会把联赛风格当成球队实力（11-04 朗斯 1:9）。
    /// 所以每场先按该场联赛的场均归一，累加倍率，出场时再乘回目标联赛的场均。
    /// </summary>
    [Fact]
    public void TeamSample_IsAggregatedAcrossLeagues()
    {
        var samples = new List<ScoreSample>();
        // 阿尔克马尔：荷甲 12 场（2 球/场），欧罗巴 2 场（0 球）。共 14 场。
        for (var i = 0; i < 12; i++)
            samples.Add(new ScoreSample { LeagueName = "荷甲", HomeTeam = "阿尔克马尔", AwayTeam = "荷甲陪练" + i, HomeScore = 2, AwayScore = 1 });
        for (var i = 0; i < 2; i++)
            samples.Add(new ScoreSample { LeagueName = "欧罗巴", HomeTeam = "阿尔克马尔", AwayTeam = "欧战强队" + i, HomeScore = 0, AwayScore = 2 });
        // 凑够 30 场，否则 Fit 退回 Empty
        for (var i = 0; i < 20; i++)
            samples.Add(new ScoreSample { LeagueName = "荷甲", HomeTeam = "甲队" + i, AwayTeam = "乙队" + i, HomeScore = 1, AwayScore = 1 });

        // 注意：跨联赛兜底默认关闭——回测证明它在「本联赛样本不足」的 339 场上反而更差
        // （Brier 0.6279 vs 0.6123）。这里显式打开，只为锁住机制本身：将来若换更好的跨联赛算法，
        // 这段行为语义不能悄悄变。
        var cross = ScorelineModel.Fit(samples, crossLeague: true);
        var production = ScorelineModel.Fit(samples);

        Assert.Equal(14, cross.TeamGames("欧罗巴", "阿尔克马尔"));
        Assert.Equal(2, production.TeamGames("欧罗巴", "阿尔克马尔"));

        // 计数变了还不够，预期进球也必须真的跟着变，否则说明只是改了个数字口径
        var crossEg = cross.ExpectedGoals("欧罗巴", "阿尔克马尔", "欧战强队0", null);
        var prodEg = production.ExpectedGoals("欧罗巴", "阿尔克马尔", "欧战强队0", null);
        Assert.NotNull(crossEg);
        Assert.NotNull(prodEg);
        Assert.True(Math.Abs(crossEg!.Value.Home - prodEg!.Value.Home) > 0.05,
            $"跨联赛累计必须改变预期进球，实测 {crossEg.Value.Home:F3} vs {prodEg!.Value.Home:F3}");
    }
}
