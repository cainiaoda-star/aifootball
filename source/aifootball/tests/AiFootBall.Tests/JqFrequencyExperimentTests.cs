using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 总进球条件频率实验。现行出榜仍用全库固定频率；这里只锁实验路径自己的截断和拒答。
/// </summary>
public class JqFrequencyExperimentTests
{
    static readonly DateTime AsOf = new(2026, 10, 1, 12, 0, 0);

    static JqFrequencyExperiment.PastMatch Row(
        DateTime kickoff, int home, int away, decimal homeOdds = 1.70m, string league = "英超",
        DateTime? knownAt = null, bool euro = true)
        => new(kickoff, knownAt, league, homeOdds, euro, home, away);

    static List<JqFrequencyExperiment.PastMatch> Known(int n, int homeGoals, int awayGoals, string league = "英超")
    {
        var rows = new List<JqFrequencyExperiment.PastMatch>(n);
        for (var i = 0; i < n; i++)
            rows.Add(Row(AsOf.AddDays(-30 - i), homeGoals, awayGoals, league: league));
        return rows;
    }

    [Fact]
    public void ProductionBaseline_IgnoresLeagueAndOdds_AndStaysGlobal()
    {
        Assert.Equal(22.31m, OptionBaseline.Pct("英超", JcPlayTypes.Jq, "2", 1.40m, 4.50m));
        Assert.Equal(22.31m, OptionBaseline.Pct("荷甲", JcPlayTypes.Jq, "2", 3.20m, 2.10m));

        var pick = new MilkPick
        {
            MatchId = 1,
            LeagueName = "英超",
            PlayType = JcPlayTypes.Jq,
            OptionCode = "2",
            Odds = 4.20m
        };
        MilkPickEngine.MarkBaseline(new[] { pick });
        Assert.Equal(22.31m, pick.BaselineProbability);
        Assert.Equal(0.937m, pick.BaselineEv);
    }

    [Fact]
    public void EnoughSample_UsesOnlyThatCell_AndPercentSumsTo100()
    {
        var history = Known(9, 0, 0);
        history.AddRange(Known(3, 1, 1));
        var dist = JqFrequencyExperiment.Distribution(history, AsOf, "英超", 1.70m, true);
        Assert.NotNull(dist);
        Assert.Equal(100m, dist!.Sum());
        Assert.Equal(75.00m, dist[0]);
        Assert.Equal(25.00m, dist[2]);

        var decision = JqFrequencyExperiment.Judge(history, AsOf, "英超", 1.70m, true, 2, 4.20m);
        Assert.True(decision.Recommend);
        Assert.Equal(25.00m, decision.ProbabilityPct);
        Assert.Equal(12, decision.Sample);
        Assert.Equal(1.050m, decision.BaselineEv);
        Assert.NotEqual(22.31m, decision.ProbabilityPct);
    }

    [Fact]
    public void ElevenMatches_Abstains_AndDoesNotFillTheGlobalFrequency()
    {
        var history = Known(11, 1, 1);
        var decision = JqFrequencyExperiment.Judge(history, AsOf, "英超", 1.70m, true, 2, 4.20m);
        Assert.False(decision.Recommend);
        Assert.Null(decision.ProbabilityPct);
        Assert.Null(decision.BaselineEv);
        Assert.Equal(11, decision.Sample);
        Assert.Equal(JqFrequencyExperiment.ReasonInsufficient, decision.Reason);
        Assert.Null(JqFrequencyExperiment.Distribution(history, AsOf, "英超", 1.70m, true));
    }

    [Fact]
    public void MatchesKnownAfterTheDecision_AreExcluded()
    {
        var history = Known(12, 1, 1);
        history.Add(Row(AsOf.AddHours(-1), 0, 0, knownAt: AsOf.AddMinutes(1)));
        history.Add(Row(AsOf.AddDays(1), 0, 0));
        var decision = JqFrequencyExperiment.Judge(history, AsOf, "英超", 1.70m, true, 2, 4.20m);
        Assert.Equal(12, decision.Sample);
        Assert.Equal(100.00m, decision.ProbabilityPct);
    }

    [Fact]
    public void ResultStillInsideTheMatchWindow_IsNotYetUsable()
    {
        var history = Known(11, 1, 1);
        history.Add(Row(AsOf.AddMinutes(-149), 0, 0));
        var decision = JqFrequencyExperiment.Judge(history, AsOf, "英超", 1.70m, true, 2, 4.20m);
        Assert.Equal(11, decision.Sample);
        Assert.Equal(JqFrequencyExperiment.ReasonInsufficient, decision.Reason);
        Assert.Null(decision.ProbabilityPct);
    }

    [Fact]
    public void OtherLeagueOrOtherOddsBand_DoesNotPadTheSample()
    {
        var history = Known(11, 1, 1);
        history.AddRange(Known(20, 0, 0, "西甲"));
        history.Add(Row(AsOf.AddDays(-3), 0, 0, homeOdds: 3.40m));
        var decision = JqFrequencyExperiment.Judge(history, AsOf, "英超", 1.70m, true, 2, 4.20m);
        Assert.Equal(11, decision.Sample);
        Assert.Null(decision.ProbabilityPct);
    }

    [Fact]
    public void MappedLeagueName_SharesTheSameCell()
    {
        var history = Known(12, 2, 0, "K1联赛");
        var decision = JqFrequencyExperiment.Judge(history, AsOf, "韩职", 1.70m, true, 2, 4.20m);
        Assert.Equal(12, decision.Sample);
        Assert.Equal(100.00m, decision.ProbabilityPct);
    }

    [Fact]
    public void JingcaiHomePrice_UsesTheExistingEuroConversion()
    {
        var history = Known(12, 1, 1, "英超");
        var byEuro = JqFrequencyExperiment.Judge(history, AsOf, "英超", 1.70m, true, 2, 4.20m);
        var byJc = JqFrequencyExperiment.Judge(history, AsOf, "英超", 1.70m / OptionBaseline.JcToEuro, false, 2, 4.20m);
        Assert.Equal(byEuro.Sample, byJc.Sample);
        Assert.Equal(byEuro.ProbabilityPct, byJc.ProbabilityPct);
    }

    [Fact]
    public void MissingLeagueOrOdds_AbstainsWithoutANumber()
    {
        var history = Known(12, 1, 1);
        var noLeague = JqFrequencyExperiment.Judge(history, AsOf, "  ", 1.70m, true, 2, 4.20m);
        var noOdds = JqFrequencyExperiment.Judge(history, AsOf, "英超", null, true, 2, 4.20m);
        Assert.Equal(JqFrequencyExperiment.ReasonMissingLeague, noLeague.Reason);
        Assert.Equal(JqFrequencyExperiment.ReasonMissingOdds, noOdds.Reason);
        Assert.Null(noLeague.ProbabilityPct);
        Assert.Null(noOdds.ProbabilityPct);
    }

    [Fact]
    public void LowConditionalFrequency_FailsTheSameValueBand_ButKeepsTheProbability()
    {
        var history = Known(12, 0, 0);
        var decision = JqFrequencyExperiment.Judge(history, AsOf, "英超", 1.70m, true, 2, 4.20m);
        Assert.False(decision.Recommend);
        Assert.Equal(0m, decision.ProbabilityPct);
        Assert.Equal(0m, decision.BaselineEv);
        Assert.Equal(JqFrequencyExperiment.ReasonBelowFloor, decision.Reason);
    }

    [Fact]
    public void PublishPath_DoesNotReferenceTheExperiment()
    {
        var root = FindRepoRoot();
        var files = new[]
        {
            Path.Combine(root, "src", "AiFootBall.Sources", "MilkPickEngine.cs"),
            Path.Combine(root, "src", "AiFootBall.Sources", "RecommendationService.cs"),
            Path.Combine(root, "src", "AiFootBall.Sources", "JobScheduler.cs"),
            Path.Combine(root, "src", "AiFootBall.Sources", "DailyFollowService.cs"),
            Path.Combine(root, "src", "AiFootBall.Api", "Program.cs"),
            Path.Combine(root, "src", "AiFootBall.Data", "MilkPickRepository.cs")
        };
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("JqFrequencyExperiment", text);
        }
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AiFootBall.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到 AiFootBall.sln");
    }
}
