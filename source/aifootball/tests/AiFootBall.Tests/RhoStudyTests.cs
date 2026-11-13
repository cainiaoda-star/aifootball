using AiFootBall.Core.Models;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// ρ 对比台用到的两个新入口（2026-11-13 加）：`WithRho` 与 `Score`。
///
/// 这两个方法存在的唯一理由是「固定 λ、只动 ρ」跑前向对比。它们一旦行为不对，
/// 对比出来的数字就是假的——比没有数字更糟，因为看起来像证据。
/// </summary>
public class RhoStudyTests
{
    private static List<ScoreSample> Make(int count = 240, int homeGoals = 2, int awayGoals = 1)
        => Enumerable.Range(0, count).Select(i => new ScoreSample
        {
            LeagueName = "测试联赛",
            HomeTeam = i % 2 == 0 ? "甲队" : "丙队",
            AwayTeam = i % 2 == 0 ? "乙队" : "丁队",
            HomeScore = homeGoals,
            AwayScore = awayGoals,
            MatchTime = new DateTime(2026, 1, 1).AddDays(i * 2)
        }).ToList();

    [Fact]
    public void WithRho_OnlyMovesLowScoreCells()
    {
        var model = ScorelineModel.Fit(Make());
        Assert.True(model.Ready);

        var near = model.WithRho(0.010);
        var deep = model.WithRho(-0.060);

        double P(ScorelineModel m, int h, int a)
            => m.Distribution("测试联赛", "甲队", "乙队", null)
                .First(c => c.Home == h && c.Away == a).P;

        // tau(0,0) = 1 − λh·λa·ρ：ρ 越负，0:0 概率越高。
        Assert.True(P(deep, 0, 0) > P(near, 0, 0),
            $"ρ 变负时 0:0 应变大，实际 {P(near, 0, 0):0.000000} → {P(deep, 0, 0):0.000000}");

        // tau(1,0) = 1 + λa·ρ：ρ 越负，1:0 概率越低。
        Assert.True(P(deep, 1, 0) < P(near, 1, 0),
            $"ρ 变负时 1:0 应变小，实际 {P(near, 1, 0):0.000000} → {P(deep, 1, 0):0.000000}");

        // 2 球以上的格子不受 ρ 影响（tau = 1），固定 λ 的对比才成立。
        Assert.Equal(P(near, 3, 2), P(deep, 3, 2), 12);
        Assert.Equal(P(near, 2, 0), P(deep, 2, 0), 12);
    }

    [Fact]
    public void WithRho_KeepsEverythingElse()
    {
        var model = ScorelineModel.Fit(Make());
        var moved = model.WithRho(model.Rho - 0.02);

        Assert.Equal(model.Ready, moved.Ready);
        Assert.Equal(model.FitGames, moved.FitGames);
        Assert.Equal(model.FitFrom, moved.FitFrom);
        Assert.Equal(model.FitTo, moved.FitTo);
        Assert.Equal(model.Rho - 0.02, moved.Rho, 12);
        // 队名解析不受影响
        Assert.True(moved.KnowsTeam("测试联赛", "甲队"));
        Assert.Equal(model.TeamGames("测试联赛", "甲队"), moved.TeamGames("测试联赛", "甲队"));
    }

    [Fact]
    public void Score_IsFiniteAndRespondsToRho()
    {
        var model = ScorelineModel.Fit(Make());

        // 必须含低比分格：ρ 只改 tau(0,0)/tau(1,0)/tau(0,1)/tau(1,1)，
        // 拿清一色 2:1 去比，两边会一模一样，测不出「有没有响应」。
        var eval = Enumerable.Range(0, 80).Select(i => new ScoreSample
        {
            LeagueName = "测试联赛",
            HomeTeam = "甲队",
            AwayTeam = "乙队",
            HomeScore = i % 4 == 0 ? 0 : i % 4 == 1 ? 1 : i % 4 == 2 ? 1 : 2,
            AwayScore = i % 4 == 0 ? 0 : i % 4 == 1 ? 0 : 1,
            MatchTime = new DateTime(2026, 8, 1).AddHours(i)
        }).ToList();

        var (ll, n) = model.Score(eval);

        Assert.True(double.IsFinite(ll));
        Assert.Equal(eval.Count, n);
        Assert.True(ll < 0, "平均逐场对数似然必须为负");

        var (ll2, n2) = model.WithRho(model.Rho - 0.10).Score(eval);
        Assert.Equal(n, n2);
        Assert.NotEqual(ll, ll2);
    }

    [Fact]
    public void Score_SkipsUnusableSamples()
    {
        var samples = Make();
        samples.Add(new ScoreSample
        {
            LeagueName = "测试联赛",
            HomeTeam = "",
            AwayTeam = "乙队",
            HomeScore = 1,
            AwayScore = 0,
            MatchTime = samples[0].MatchTime
        });

        var (_, n) = ScorelineModel.Fit(samples).Score(samples);

        Assert.Equal(samples.Count - 1, n);
    }

    // ---------------- ρ 固定（2026-11-13 用户拍板选项 B） ----------------

    /// <summary>
    /// 钉死拍板值。改这个常量等于改模型，必须带前向回测与用户批准；
    /// 让它先红一条测试，比让它悄悄变一个数字安全。
    /// </summary>
    [Fact]
    public void DefaultRho_IsApprovedBaseline()
        => Assert.Equal(0.010, ScorelineModel.DefaultRho, 12);

    /// <summary>
    /// ρ 不再由数据估计：不同样本形态（进球多的、进球少的、平局多的）都必须落同一个值。
    /// 旧实现这里会随样本变化，正是「同一份代码换顺序就出不同 ρ」的根。
    /// </summary>
    [Theory]
    [InlineData(2, 1)]
    [InlineData(0, 0)]
    [InlineData(4, 0)]
    [InlineData(1, 1)]
    public void Fit_UsesFixedRho_ForAnySampleShape(int homeGoals, int awayGoals)
    {
        var model = ScorelineModel.Fit(Make(homeGoals: homeGoals, awayGoals: awayGoals));

        Assert.True(model.Ready);
        Assert.Equal(ScorelineModel.DefaultRho, model.Rho, 12);
        Assert.Contains($"rho={ScorelineModel.DefaultRho:0.000}", model.VersionTag);
    }

    /// <summary>
    /// 换物理顺序（正序/倒序/随机）必须得到逐字相同的版本戳。
    /// 这是审计的前提：版本戳不同，事后就无法解释「这条注当时用的哪个模型」。
    /// </summary>
    [Fact]
    public void Fit_RhoDoesNotDependOnSampleOrder()
    {
        var samples = Make(600);
        var forward = ScorelineModel.Fit(samples);
        var backward = ScorelineModel.Fit(Enumerable.Reverse(samples).ToList());
        var rnd = new Random(20261113);
        var shuffled = samples.OrderBy(_ => rnd.Next()).ToList();
        var byShuffle = ScorelineModel.Fit(shuffled);

        Assert.Equal(ScorelineModel.DefaultRho, forward.Rho, 12);
        Assert.Equal(forward.Rho, backward.Rho, 12);
        Assert.Equal(forward.Rho, byShuffle.Rho, 12);
        Assert.Equal(forward.VersionTag, backward.VersionTag);
        Assert.Equal(forward.VersionTag, byShuffle.VersionTag);
    }
}
