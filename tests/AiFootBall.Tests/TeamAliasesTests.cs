using AiFootBall.Core.Models;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 队名别名的护栏（2026-11-08）。代价是被真实场次量出来的：
/// 库里「AIK索尔纳」205 场、「索尔纳」55 场，是同一支瑞典超球队的两套写法。
/// 原来的别名表只在「精确匹配失败」时才查，两个写法都在库里有样本时精确匹配先命中，
/// 别名永远不生效 → 模型把一支队当两支，攻防估计各自被噪声主导。
/// </summary>
public class TeamAliasesTests
{
    [Fact]
    public void Canonical_MergesAikSolna()
    {
        Assert.Equal("AIK索尔纳", TeamAliases.Canonical("索尔纳"));
        Assert.Equal("AIK索尔纳", TeamAliases.Canonical(" AIK索尔纳 "));   // 采集端带空格
        Assert.Equal("AIK索尔纳", TeamAliases.Canonical("AIK索尔纳"));
    }

    [Fact]
    public void Canonical_LeavesUnknownNamesAlone()
    {
        Assert.Equal("英超队", TeamAliases.Canonical("英超队"));
        Assert.Equal("", TeamAliases.Canonical(null));
        Assert.Equal("", TeamAliases.Canonical("   "));
    }

    [Fact]
    public void FormsOf_ExpandsEverySpelling()
    {
        var forms = TeamAliases.FormsOf("索尔纳");
        Assert.Contains("索尔纳", forms);
        Assert.Contains("AIK索尔纳", forms);

        // 从规范名反查，结果必须一样——否则查库时会漏掉半段历史
        Assert.Equal(forms.OrderBy(x => x), TeamAliases.FormsOf("AIK索尔纳").OrderBy(x => x));

        Assert.Equal(new[] { "某个队" }, TeamAliases.FormsOf("某个队"));
    }

    [Fact]
    public void Map_IsWellFormed()
    {
        foreach (var (alias, canonical) in TeamAliases.Map)
        {
            Assert.NotEmpty(alias);
            Assert.NotEmpty(canonical);
            Assert.NotEqual(alias, canonical);                       // 不许自环
            Assert.Equal(canonical, TeamAliases.Canonical(alias));   // 一跳到底
        }
    }

    [Fact]
    public void Fit_MergesSamplesOfBothSpellings()
    {
        var model = FitSplitTeam();

        // 合并后：10 场「AIK索尔纳」+ 10 场「索尔纳」= 20 场
        Assert.Equal(20, model.TeamGames("瑞典超", "AIK索尔纳"));
        Assert.Equal(20, model.TeamGames("瑞典超", "索尔纳"));
        Assert.True(model.KnowsTeam("瑞典超", "索尔纳"));

        // 没被误合并的队，场次仍是自己的：陪练0 = 作客 6 场（两组各 3）+ 主场 5 场 = 11
        Assert.Equal(11, model.TeamGames("瑞典超", "陪练0"));
    }

    [Fact]
    public void Resolve_ReturnsCanonicalForAlias()
    {
        var model = FitSplitTeam();
        Assert.Equal("AIK索尔纳", model.Resolve("瑞典超", "索尔纳"));

        // 联赛名传错也要认（别名表是全局的，不依赖联赛维度）
        Assert.Equal("AIK索尔纳", model.Resolve("瑞典超XX", "索尔纳"));
    }

    [Fact]
    public void TeamNames_SameTreatsAliasAsOneTeam()
    {
        // 竞彩写「索尔纳」、数据源写「AIK索尔纳」时，配对同一场球必须成功；
        // 否则那场比赛找不到比分，等于从样本里凭空消失。
        Assert.True(TeamNames.Same("索尔纳", "AIK索尔纳"));
        Assert.True(TeamNames.Same("AIK索尔纳", "索尔纳"));
        Assert.Equal(
            TeamNames.PairKey("索尔纳", "赫根"),
            TeamNames.PairKey("AIK索尔纳", "赫根"));

        // 不相干的队不能被牵连
        Assert.False(TeamNames.Same("索尔纳", "赫根"));
    }

    /// <summary>10 场写「AIK索尔纳」、10 场写「索尔纳」，外加 20 场别的队凑够 Fit 的下限。</summary>
    private static ScorelineModel FitSplitTeam()
    {
        var samples = new List<ScoreSample>();
        for (var i = 0; i < 10; i++)
            samples.Add(new ScoreSample
            {
                LeagueName = "瑞典超", HomeTeam = "AIK索尔纳", AwayTeam = "陪练" + i % 4,
                HomeScore = 2, AwayScore = 1
            });
        for (var i = 0; i < 10; i++)
            samples.Add(new ScoreSample
            {
                LeagueName = "瑞典超", HomeTeam = "索尔纳", AwayTeam = "陪练" + i % 4,
                HomeScore = 2, AwayScore = 1
            });
        for (var i = 0; i < 20; i++)
            samples.Add(new ScoreSample
            {
                LeagueName = "瑞典超", HomeTeam = "陪练" + i % 4, AwayTeam = "别的队" + i % 4,
                HomeScore = 1, AwayScore = 1
            });
        return ScorelineModel.Fit(samples);
    }
}
