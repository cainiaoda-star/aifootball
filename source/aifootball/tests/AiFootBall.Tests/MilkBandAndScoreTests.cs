using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 2026-11-16：半全场不再卡赔率区间，比分重新出票。
/// 「能中」的定义（概率门槛、期望、基线 EV）保持原样。
/// 总进球 4~10、不发 5 球，让球只发让平，这三件事也锁在这里。
/// </summary>
public class MilkBandAndScoreTests
{
    private static ScorelineModel StrongHome()
    {
        var samples = new List<ScoreSample>();
        for (var i = 0; i < 20; i++)
        {
            samples.Add(new ScoreSample { LeagueName = "英超", HomeTeam = "主队", AwayTeam = "客" + i, HomeScore = 2, AwayScore = 1 });
            samples.Add(new ScoreSample { LeagueName = "英超", HomeTeam = "客队", AwayTeam = "主" + i, HomeScore = 1, AwayScore = 2 });
        }
        return ScorelineModel.Fit(samples);
    }

    private static MatchRecommendation Rec(long id) => new()
    {
        Board = Boards.Jc,
        MatchNo = "周三001",
        MatchId = id,
        LeagueName = "英超",
        HomeTeam = "主队",
        AwayTeam = "客队",
        MatchTime = DateTime.Today.AddHours(20),
        TotalLine = 2.5m,
        Handicap = -1m
    };

    private static JcOddsHistoryPoint Pt(long mid, string play, string code, decimal odds) => new()
    {
        MatchId = mid,
        PlayType = play,
        OptionCode = code,
        OptionName = code,
        Odds = odds
    };

    [Fact]
    public void HalfFull_BelowFour_WhenCanHit_IsPublished()
    {
        var picks = MilkPickEngine.Build(
            new[] { Rec(1) },
            new Dictionary<long, List<JcOddsHistoryPoint>>
            {
                [1] = new() { Pt(1, JcPlayTypes.Bq, "3-3", 3.50m) }
            },
            StrongHome());

        Assert.Contains(picks, p => p.PlayType == JcPlayTypes.Bq && p.OptionCode == "3-3" && p.Odds == 3.50m);
    }

    /// <summary>两队都常打 2:2，胜平的联合概率才进得了「能中」。</summary>
    private static ScorelineModel Level()
    {
        var samples = new List<ScoreSample>();
        for (var i = 0; i < 20; i++)
        {
            samples.Add(new ScoreSample { LeagueName = "英超", HomeTeam = "主队", AwayTeam = "客" + i, HomeScore = 2, AwayScore = 2 });
            samples.Add(new ScoreSample { LeagueName = "英超", HomeTeam = "客队", AwayTeam = "主" + i, HomeScore = 2, AwayScore = 2 });
        }
        return ScorelineModel.Fit(samples);
    }

    [Fact]
    public void HalfFull_AboveFifteen_WhenCanHit_IsPublished()
    {
        // 英超胜平基线 6.90%，16.5 倍仍在基线 EV 窗口内，并且高于旧上限 8 和口头说的 15。
        // 均势模型下这一档过得了概率门槛和期望闸。主胜模型过不了，那是「能中」为假，不是赔率闸。
        var picks = MilkPickEngine.Build(
            new[] { Rec(1) },
            new Dictionary<long, List<JcOddsHistoryPoint>>
            {
                [1] = new() { Pt(1, JcPlayTypes.Bq, "3-1", 16.5m) }
            },
            Level());

        Assert.Contains(picks, p => p.PlayType == JcPlayTypes.Bq && p.OptionCode == "3-1" && p.Odds == 16.5m);
    }

    [Theory]
    [InlineData("0-0", 3.50)]
    [InlineData("0-0", 16)]
    [InlineData("3-3", 30)]
    public void HalfFull_WhenCannotHit_IsNotPublished(string code, double odds)
    {
        var picks = MilkPickEngine.Build(
            new[] { Rec(1) },
            new Dictionary<long, List<JcOddsHistoryPoint>>
            {
                [1] = new() { Pt(1, JcPlayTypes.Bq, code, (decimal)odds) }
            },
            StrongHome());

        Assert.DoesNotContain(picks, p => p.PlayType == JcPlayTypes.Bq && p.OptionCode == code);
    }

    [Fact]
    public void Score_WhenCanHit_IsPublished()
    {
        Assert.True(MilkPickEngine.EnableScorePicks);

        var picks = MilkPickEngine.Build(
            new[] { Rec(1) },
            new Dictionary<long, List<JcOddsHistoryPoint>>
            {
                [1] = new() { Pt(1, JcPlayTypes.Bf, "2:1", 10m) }
            },
            StrongHome());

        Assert.Contains(picks, p => p.PlayType == JcPlayTypes.Bf && p.OptionCode == "2:1");
    }

    [Fact]
    public void Score_WhenCanHit_IsNotCutByDailyQuota()
    {
        var recs = Enumerable.Range(1, 6).Select(i => Rec(i)).ToArray();
        var odds = recs.ToDictionary(
            r => r.MatchId!.Value,
            r => new List<JcOddsHistoryPoint> { Pt(r.MatchId!.Value, JcPlayTypes.Bf, "2:1", 10m) });

        var picks = MilkPickEngine.Build(recs, odds, StrongHome());

        var scores = picks.Where(p => p.PlayType == JcPlayTypes.Bf && p.OptionCode == "2:1").ToList();
        Assert.Equal(6, scores.Select(p => p.MatchId).Distinct().Count());
    }

    [Fact]
    public void Score_WhenCanHit_StaysWhenHalfFullAlreadyFillsTheMatch()
    {
        // 半全场胜胜和总进球 3 球会占满非比分的每场两注。能中的 2:1 仍要在。
        var picks = MilkPickEngine.Build(
            new[] { Rec(1) },
            new Dictionary<long, List<JcOddsHistoryPoint>>
            {
                [1] = new()
                {
                    Pt(1, JcPlayTypes.Bq, "3-3", 4.2m),
                    Pt(1, JcPlayTypes.Jq, "3", 5.0m),
                    Pt(1, JcPlayTypes.Bf, "2:1", 10m)
                }
            },
            StrongHome());

        Assert.Contains(picks, p => p.PlayType == JcPlayTypes.Bq && p.OptionCode == "3-3");
        Assert.Contains(picks, p => p.PlayType == JcPlayTypes.Jq && p.OptionCode == "3");
        Assert.Contains(picks, p => p.PlayType == JcPlayTypes.Bf && p.OptionCode == "2:1");
    }

    [Fact]
    public void Score_WhenCannotHit_IsNotPublished()
    {
        var picks = MilkPickEngine.Build(
            new[] { Rec(1) },
            new Dictionary<long, List<JcOddsHistoryPoint>>
            {
                [1] = new() { Pt(1, JcPlayTypes.Bf, "0:5", 10m) }
            },
            StrongHome());

        Assert.DoesNotContain(picks, p => p.PlayType == JcPlayTypes.Bf);
    }

    [Fact]
    public void Goals_AndHandicap_KeepTheirGates()
    {
        var picks = MilkPickEngine.Build(
            new[] { Rec(1) },
            new Dictionary<long, List<JcOddsHistoryPoint>>
            {
                [1] = new()
                {
                    Pt(1, JcPlayTypes.Jq, "3", 3.45m),
                    Pt(1, JcPlayTypes.Jq, "3", 5.0m),
                    Pt(1, JcPlayTypes.Jq, "5", 6.0m),
                    Pt(1, JcPlayTypes.Jq, "6", 11m),
                    Pt(1, JcPlayTypes.Nspf, "3", 4.5m),
                    Pt(1, JcPlayTypes.Nspf, "1", 4.5m),
                    Pt(1, JcPlayTypes.Nspf, "0", 4.5m),
                    Pt(1, JcPlayTypes.Spf, "3", 1.40m),
                    Pt(1, JcPlayTypes.Spf, "1", 4.20m),
                    Pt(1, JcPlayTypes.Spf, "0", 7.00m)
                }
            },
            StrongHome());

        var jq = picks.Where(p => p.PlayType == JcPlayTypes.Jq).Select(p => (p.OptionCode, p.Odds)).ToList();
        Assert.Contains(("3", 5.0m), jq);
        Assert.DoesNotContain(jq, p => p.Odds == 3.45m);
        Assert.DoesNotContain(jq, p => p.OptionCode == "5");
        Assert.DoesNotContain(jq, p => p.Odds == 11m);

        var nspf = picks.Where(p => p.PlayType == JcPlayTypes.Nspf).ToList();
        Assert.DoesNotContain(nspf, p => p.OptionCode is "3" or "0");
        Assert.All(nspf, p => Assert.Equal("1", p.OptionCode));
    }

    private static MatchRecommendation Nations(long id, string league = "欧国联") => new()
    {
        Board = Boards.Jc,
        MatchNo = "周一001",
        MatchId = id,
        LeagueName = league,
        HomeTeam = "主队",
        AwayTeam = "客队",
        MatchTime = DateTime.Today.AddHours(20),
        TotalLine = 2.5m,
        Handicap = -1m
    };

    private static ScorelineModel NationsStrong(string league)
    {
        var samples = new List<ScoreSample>();
        for (var i = 0; i < 20; i++)
        {
            samples.Add(new ScoreSample { LeagueName = league, HomeTeam = "主队", AwayTeam = "客" + i, HomeScore = 2, AwayScore = 1 });
            samples.Add(new ScoreSample { LeagueName = league, HomeTeam = "客队", AwayTeam = "主" + i, HomeScore = 1, AwayScore = 2 });
        }
        return ScorelineModel.Fit(samples);
    }

    [Theory]
    [InlineData("欧国联")]
    [InlineData("友谊赛")]
    [InlineData("澳超")]
    public void JcOpenedLeague_IsNotDroppedBeforeTheFunnel(string league)
    {
        var built = MilkPickEngine.BuildDetailed(
            new[] { Nations(1, league) },
            new Dictionary<long, List<JcOddsHistoryPoint>>
            {
                [1] = new() { Pt(1, JcPlayTypes.Bq, "3-3", 3.50m) }
            },
            NationsStrong(league));

        Assert.DoesNotContain(built.Rejections, r => r.Reason == MilkInputRejection.ReasonLeagueExcluded);
        Assert.DoesNotContain(built.Funnel.Skipped, s => s.Contains("不推名单") || s.Contains("黑名单"));
        Assert.True(built.Funnel.WithOdds >= 1);
    }

    [Fact]
    public void JcNationsLeague_WhenCanHit_IsPublished()
    {
        var picks = MilkPickEngine.Build(
            new[] { Nations(1) },
            new Dictionary<long, List<JcOddsHistoryPoint>>
            {
                [1] = new() { Pt(1, JcPlayTypes.Bq, "3-3", 3.50m) }
            },
            NationsStrong("欧国联"));

        Assert.Contains(picks, p => p.PlayType == JcPlayTypes.Bq && p.OptionCode == "3-3" && p.Odds == 3.50m);
    }

    [Fact]
    public void JcNationsLeague_WhenCannotHit_IsNotPublished()
    {
        var built = MilkPickEngine.BuildDetailed(
            new[] { Nations(1) },
            new Dictionary<long, List<JcOddsHistoryPoint>>
            {
                [1] = new() { Pt(1, JcPlayTypes.Bq, "0-0", 3.50m) }
            },
            NationsStrong("欧国联"));

        Assert.True(built.Funnel.WithOdds >= 1);
        Assert.DoesNotContain(built.Rejections, r => r.Reason == MilkInputRejection.ReasonLeagueExcluded);
        Assert.DoesNotContain(built.Picks, p => p.PlayType == JcPlayTypes.Bq && p.OptionCode == "0-0");
    }

    [Fact]
    public void JcNationsLeague_GoalsAndHandicapGatesStillHold()
    {
        var picks = MilkPickEngine.Build(
            new[] { Nations(1) },
            new Dictionary<long, List<JcOddsHistoryPoint>>
            {
                [1] = new()
                {
                    Pt(1, JcPlayTypes.Bq, "3-3", 3.50m),
                    Pt(1, JcPlayTypes.Jq, "3", 3.45m),
                    Pt(1, JcPlayTypes.Jq, "3", 5.0m),
                    Pt(1, JcPlayTypes.Jq, "5", 6.0m),
                    Pt(1, JcPlayTypes.Jq, "6", 11m),
                    Pt(1, JcPlayTypes.Nspf, "3", 4.5m),
                    Pt(1, JcPlayTypes.Nspf, "1", 4.5m),
                    Pt(1, JcPlayTypes.Nspf, "0", 4.5m)
                }
            },
            NationsStrong("欧国联"));

        Assert.Contains(picks, p => p.PlayType == JcPlayTypes.Bq && p.OptionCode == "3-3");
        var jq = picks.Where(p => p.PlayType == JcPlayTypes.Jq).Select(p => (p.OptionCode, p.Odds)).ToList();
        Assert.Contains(("3", 5.0m), jq);
        Assert.DoesNotContain(jq, p => p.Odds == 3.45m);
        Assert.DoesNotContain(jq, p => p.OptionCode == "5");
        Assert.DoesNotContain(jq, p => p.Odds == 11m);
        Assert.DoesNotContain(picks, p => p.PlayType == JcPlayTypes.Nspf && p.OptionCode is "3" or "0");
    }
}
