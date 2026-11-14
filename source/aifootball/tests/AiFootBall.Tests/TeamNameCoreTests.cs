using AiFootBall.Core.Models;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 队名归一化兜底（2026-11-14，1419319 大宫松鼠RB 事件）。
///
/// 现场：竞彩把球队写成「大宫松鼠RB」（红牛冠名），库内 39 场全叫「大宫松鼠」。
/// 名字解析只允许**查询名比库内名短**（`cand.Length &lt;= shortName.Length` 直接跳过），
/// 于是查询名更长时规则完全不触发 → 整场被判「模型不认识」→
/// 没有输入凭证、没有存档、没有闸门，静默消失，而库里那 39 场一直躺在「大宫松鼠」名下。
///
/// 这两条测试分别钉住两个修法：
///   · 人工别名（TeamAliases）—— 已知的译名/冠名差异；
///   · 核心名兜底（去 ASCII 标记后一字不差且全库唯一）—— 没见过的新后缀。
/// 同时钉住边界：核心名撞两支以上时**不许猜**，返回「认不出」。
/// </summary>
public class TeamNameCoreTests
{
    private static ScorelineModel Model(params (string Home, string Away)[] extra)
    {
        var samples = new List<ScoreSample>();
        var day = new DateTime(2026, 1, 1);
        for (var i = 0; i < 20; i++)
        {
            samples.Add(new ScoreSample
            {
                LeagueName = "日职乙", HomeTeam = "大宫松鼠", AwayTeam = "山形山神",
                HomeScore = 1 + i % 3, AwayScore = i % 2, MatchTime = day.AddDays(i)
            });
            samples.Add(new ScoreSample
            {
                LeagueName = "日职乙", HomeTeam = "山形山神", AwayTeam = "大宫松鼠",
                HomeScore = i % 2, AwayScore = 1 + i % 3, MatchTime = day.AddDays(i + 30)
            });
        }

        foreach (var (h, a) in extra)
            for (var i = 0; i < 3; i++)
                samples.Add(new ScoreSample
                {
                    LeagueName = "日职乙", HomeTeam = h, AwayTeam = a,
                    HomeScore = 1, AwayScore = 1, MatchTime = day.AddDays(60 + i)
                });

        return ScorelineModel.Fit(samples);
    }

    [Fact]
    public void SponsorSuffix_ResolvesViaAlias()
    {
        var model = Model();
        Assert.True(model.KnowsTeam("日职乙", "大宫松鼠"));
        Assert.Equal("大宫松鼠", model.Resolve("日职乙", "大宫松鼠RB"));
        Assert.True(model.TeamGames("日职乙", "大宫松鼠RB") > 0);
    }

    [Fact]
    public void UnknownAsciiSuffix_ResolvesViaCoreName()
    {
        // 「山形山神SC」不在别名表里：靠核心名（去掉 SC）唯一命中。
        var model = Model();
        Assert.Equal("山形山神", model.Resolve("日职乙", "山形山神SC"));
    }

    [Fact]
    public void AmbiguousCoreNames_AreNotGuessed()
    {
        // 同核心名对应两支不同球队时，必须返回「认不出」——宁可丢场，不猜错队。
        var model = Model(("测试甲队", "测试乙队"), ("测试甲队FC", "测试乙队"));
        Assert.Null(model.Resolve("日职乙", "测试甲队SC"));
    }

    [Fact]
    public void ReserveTeamIsNeverMatchedForSeniorQuery()
    {
        // 2026-11-14 实测的两条误配：库内一线队没样本时，唯一核心名是梯队，
        // 「全库唯一」就把一线队配成了梯队。唯一性只保证不猜，不保证是同一支队。
        //   不来梅 → JFV不来梅U19
        //   谢周三 → 谢周三U21
        var model = Model(("JFV不来梅U19", "奥格斯堡青年队"), ("谢周三U21", "女王公园巡游者U21"));
        Assert.Null(model.Resolve("德甲", "不来梅"));
        Assert.Null(model.Resolve("英锦标赛", "谢周三"));
    }

    [Fact]
    public void ExplicitYouthQuery_StillResolvesYouthTeam()
    {
        // 反向不能误伤：查询名自己带梯队标记时，队内精确命中照旧——
        // 修的是「一线队被配成梯队」，不是「不许用梯队」。
        var model = Model(("JFV不来梅U19", "奥格斯堡青年队"), ("谢周三U21", "女王公园巡游者U21"));
        Assert.Equal("谢周三U21", model.Resolve("英锦标赛", "谢周三U21"));
        Assert.Equal("JFV不来梅U19", model.Resolve("德甲", "JFV不来梅U19"));
    }

    [Fact]
    public void DifferentYouthLevels_DoNotMerge()
    {
        // U19 与 U21 是两支不同的队：核心名相同，但标记不同，不能互配。
        var model = Model(("谢周三U21", "女王公园巡游者U21"), ("谢周三U19", "女王公园巡游者U19"));
        Assert.Null(model.Resolve("英锦标赛", "谢周三U23"));
    }

    [Fact]
    public void CoreKey_StripsReserveMarks_WhichIsWhyIdentityGateExists()
    {
        // 核心名会把 U21 一起抹掉——所以「核心名相等」本身不能作为身份判据，
        // 必须另加梯队标记与 ASCII 记号两道闸（见 ScorelineModel.Resolve）。
        Assert.Equal(TeamNames.CoreKey("谢周三"), TeamNames.CoreKey("谢周三U21"));
        Assert.Equal(TeamNames.CoreKey("大宫松鼠"), TeamNames.CoreKey("大宫松鼠RB"));
    }

    [Fact]
    public void CoreKey_StripsOnlyAsciiMarkers()
    {
        Assert.Equal("大宫松鼠", TeamNames.CoreKey("大宫松鼠RB"));
        Assert.Equal("大宫松鼠", TeamNames.CoreKey("大宫松鼠"));
        Assert.Equal("蔚山现代", TeamNames.CoreKey("蔚山HD"));
        // 不是「相似就行」：改字（塞维利亚 vs 塞维尔亚）推不出来，必须一模一样。
        Assert.NotEqual(TeamNames.CoreKey("塞维利亚"), TeamNames.CoreKey("塞维尔亚"));
    }
}
