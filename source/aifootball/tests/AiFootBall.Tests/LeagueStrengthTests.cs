using AiFootBall.Core.Models;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 联赛强度校准（方案 1，2026-11-05 加）。
///
/// 用跨联赛交手（两队主联赛不同）的净胜球反推每个联赛的强度系数。
/// 回测结论：强度系数本身估得合理（英超 &gt; 中超），但对欧战预测无帮助——
/// 跨联赛样本（无论按不按强度折算）都不如「只用本联赛 + 先验」，所以线上不启用。
/// 这里只验证估计方法本身是对的，不验证「它能不能救模型」（那是回测台的职责）。
/// </summary>
public class LeagueStrengthTests
{
    /// <summary>构造「英超明显强于中超」的合成数据：英超队在跨联赛交手里稳定赢中超队。</summary>
    private static List<ScoreSample> BuildStrongVsWeak()
    {
        var samples = new List<ScoreSample>();
        // 英超队内部比赛（同联赛，主队略占优，用于判定主联赛 + 提供联赛场均）
        for (var i = 0; i < 40; i++)
        {
            samples.Add(new ScoreSample { LeagueName = "英超", HomeTeam = "英超A", AwayTeam = "英超B", HomeScore = 1 + i % 3, AwayScore = i % 2 });
            samples.Add(new ScoreSample { LeagueName = "英超", HomeTeam = "英超B", AwayTeam = "英超C", HomeScore = 1 + i % 2, AwayScore = i % 3 });
            samples.Add(new ScoreSample { LeagueName = "英超", HomeTeam = "英超C", AwayTeam = "英超A", HomeScore = i % 3, AwayScore = 1 + i % 2 });
        }
        // 中超队内部比赛（同联赛）
        for (var i = 0; i < 40; i++)
        {
            samples.Add(new ScoreSample { LeagueName = "中超", HomeTeam = "中超A", AwayTeam = "中超B", HomeScore = 1 + i % 2, AwayScore = i % 3 });
            samples.Add(new ScoreSample { LeagueName = "中超", HomeTeam = "中超B", AwayTeam = "中超C", HomeScore = i % 3, AwayScore = 1 + i % 2 });
            samples.Add(new ScoreSample { LeagueName = "中超", HomeTeam = "中超C", AwayTeam = "中超A", HomeScore = 1 + i % 3, AwayScore = i % 2 });
        }
        // 跨联赛交手（欧冠）：英超队稳定净胜中超队 2 球，主场优势再 +1
        for (var i = 0; i < 40; i++)
        {
            samples.Add(new ScoreSample { LeagueName = "欧冠", HomeTeam = "英超A", AwayTeam = "中超A", HomeScore = 3 + i % 2, AwayScore = i % 2 });
            samples.Add(new ScoreSample { LeagueName = "欧冠", HomeTeam = "英超B", AwayTeam = "中超B", HomeScore = 3 + i % 2, AwayScore = i % 2 });
            samples.Add(new ScoreSample { LeagueName = "欧冠", HomeTeam = "英超C", AwayTeam = "中超C", HomeScore = 3 + i % 2, AwayScore = i % 2 });
            samples.Add(new ScoreSample { LeagueName = "欧冠", HomeTeam = "中超A", AwayTeam = "英超A", HomeScore = i % 2, AwayScore = 2 + i % 2 });
            samples.Add(new ScoreSample { LeagueName = "欧冠", HomeTeam = "中超B", AwayTeam = "英超B", HomeScore = i % 2, AwayScore = 2 + i % 2 });
            samples.Add(new ScoreSample { LeagueName = "欧冠", HomeTeam = "中超C", AwayTeam = "英超C", HomeScore = i % 2, AwayScore = 2 + i % 2 });
        }
        return samples;
    }

    [Fact]
    public void EstimateLeagueStrength_RanksStrongLeagueAboveWeak()
    {
        var samples = BuildStrongVsWeak();
        var strength = ScorelineModel.EstimateLeagueStrength(samples, minGames: 20);

        Assert.True(strength.ContainsKey("英超"), "英超应被估计出强度");
        Assert.True(strength.ContainsKey("中超"), "中超应被估计出强度");

        // 英超在跨联赛交手里稳定净胜，强度系数必须显著高于中超。
        Assert.True(strength["英超"] > strength["中超"],
            $"英超强度 {strength["英超"]:F3} 应大于中超 {strength["中超"]:F3}");
    }

    [Fact]
    public void EstimateLeagueStrength_EmptyOnNoCrossLeague()
    {
        // 全是同联赛比赛，没有跨联赛交手 → 估不出，返回空字典（=不校准）。
        var samples = new List<ScoreSample>();
        for (var i = 0; i < 60; i++)
            samples.Add(new ScoreSample { LeagueName = "英超", HomeTeam = "A", AwayTeam = "B", HomeScore = 1, AwayScore = 0 });

        var strength = ScorelineModel.EstimateLeagueStrength(samples, minGames: 20);
        Assert.Empty(strength);
    }

    [Fact]
    public void Fit_WithStrength_DoesNotChangeSameLeagueSamples()
    {
        // 有联赛强度时，同联赛（≥6 场样本）的场次结果应和没强度时一致——
        // 强度只在跨联赛兜底（样本不足）时起作用，外科手术式，不污染样本充足的场次。
        var samples = BuildStrongVsWeak();
        var noStrength = ScorelineModel.Fit(samples, crossLeague: true);
        var strength = ScorelineModel.EstimateLeagueStrength(samples, minGames: 20);
        var withStrength = ScorelineModel.Fit(samples, crossLeague: true, leagueStrength: strength);

        // 英超A 在英超有 40 场样本，TeamGames（跨联赛口径）应两版一致。
        var a = noStrength.TeamGames("英超", "英超A");
        var b = withStrength.TeamGames("英超", "英超A");
        Assert.Equal(a, b);
    }
}
