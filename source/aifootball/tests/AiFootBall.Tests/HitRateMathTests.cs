using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

public class HitRateMathTests
{
    [Fact]
    public void Wilson_interval_for_a_small_sample_is_wide()
    {
        var w = HitRateMath.Wilson(29, 55);
        Assert.InRange(w.Point, 0.52, 0.53);
        Assert.InRange(w.Low, 0.39, 0.41);
        Assert.InRange(w.High, 0.64, 0.66);
    }

    [Fact]
    public void Devig_removes_the_overround_and_sums_to_one()
    {
        var p = HitRateMath.Devig(2.00m, 3.20m, 4.00m);
        Assert.NotNull(p);
        Assert.Equal(1m, decimal.Round(p.Value.Home + p.Value.Draw + p.Value.Away, 6));
        Assert.True(p.Value.Home > p.Value.Draw);
        Assert.True(p.Value.Draw > p.Value.Away);
    }

    [Fact]
    public void Shrink_pulls_six_out_of_seven_back_toward_the_market()
    {
        var p = HitRateMath.Shrink(6, 7, 0.45m, 80m);
        Assert.InRange(p, 0.47m, 0.50m);
    }

    [Fact]
    public void Parlay_multiplies_the_single_match_probabilities()
    {
        var p = HitRateMath.Parlay(new[] { 0.50m, 0.40m });
        Assert.Equal(0.20m, p);
    }

    [Fact]
    public void Logistic_beats_devig_when_the_line_move_decides_the_result()
    {
        var rows = new List<HitTrainingRow>();
        var start = new DateTime(2024, 1, 1);
        for (var i = 0; i < 500; i++)
        {
            var home = 1.80m + (i % 7) * 0.15m;
            var draw = 3.40m;
            var away = 4.20m - (i % 5) * 0.10m;
            var move = (i % 3) - 1;
            var ahOpen = 0m;
            var ahClose = ahOpen - move * 0.5m;
            var homeWins = move > 0 || (move == 0 && i % 2 == 0);
            rows.Add(new HitTrainingRow
            {
                MatchId = i + 1,
                MatchTime = start.AddHours(i),
                Result = homeWins ? "3" : "0",
                HomeOdds = home,
                DrawOdds = draw,
                AwayOdds = away < 1.2m ? 1.2m : away,
                AhOpen = ahOpen,
                AhClose = ahClose,
                RatioHome = homeWins ? 62 : 40,
                RatioAway = homeWins ? 20 : 40
            });
        }

        var model = HitProbabilityModel.Fit(rows);
        Assert.True(model.UsesLogistic);
        Assert.True(model.HoldoutBrierModel < model.HoldoutBrierDevig);
    }

    [Fact]
    public void Stamp_uses_the_shrunk_probability_not_the_old_rule_rate()
    {
        var rec = new MatchRecommendation { RecResult = '3', RatioHome = 62, RatioAway = 20 };
        var buckets = new Dictionary<(char, string), (int Hits, int Total)>
        {
            [('3', "14to2")] = (6, 7)
        };
        HitProbabilityService.Stamp(
            rec,
            new HitProbabilityService.Snapshot(HitProbabilityModel.DevigOnly, buckets, true),
            1.80m, 3.40m, 4.50m, null, null);

        Assert.NotNull(rec.HitProbability);
        Assert.InRange(rec.HitProbability!.Value, 40m, 60m);
    }

    [Fact]
    public void Price_drop_raises_home_probability()
    {
        var rows = new List<HitTrainingRow>();
        var start = new DateTime(2024, 1, 1);
        for (var i = 0; i < 500; i++)
        {
            var drop = i % 2 == 0;
            var delta = drop ? -0.28m : 0.28m;
            var close = 2.40m;
            rows.Add(new HitTrainingRow
            {
                MatchId = i + 1,
                MatchTime = start.AddHours(i),
                Result = drop ? "3" : "0",
                HomeOdds = close,
                HomeOddsOpen = close - delta,
                DrawOdds = 3.30m,
                AwayOdds = 2.90m,
                AhOpen = 0m,
                AhClose = 0m,
                RatioHome = 45,
                RatioAway = 30,
                LeagueName = "混合"
            });
        }

        var model = HitProbabilityModel.Fit(rows);
        Assert.True(model.UsesLogistic);
        var down = model.Predict(2.40m, 3.30m, 2.90m, 0m, 0m, 45m, 30m, null, "混合", -0.28m);
        var up = model.Predict(2.40m, 3.30m, 2.90m, 0m, 0m, 45m, 30m, null, "混合", 0.28m);
        Assert.True(down.Home > up.Home + 0.08m);
    }

    [Fact]
    public void League_with_enough_games_moves_the_probability_a_short_league_does_not()
    {
        var rows = new List<HitTrainingRow>();
        var start = new DateTime(2024, 1, 1);
        for (var i = 0; i < 400; i++)
        {
            rows.Add(new HitTrainingRow
            {
                MatchId = i + 1,
                MatchTime = start.AddHours(i),
                Result = i % 5 == 0 ? "1" : i % 2 == 0 ? "3" : "0",
                HomeOdds = 2.50m,
                DrawOdds = 3.20m,
                AwayOdds = 2.70m,
                LeagueName = "混合"
            });
        }
        for (var i = 0; i < 80; i++)
        {
            rows.Add(new HitTrainingRow
            {
                MatchId = 1000 + i,
                MatchTime = start.AddHours(500 + i),
                Result = "0",
                HomeOdds = 2.50m,
                DrawOdds = 3.20m,
                AwayOdds = 2.70m,
                LeagueName = "客胜联赛"
            });
        }
        for (var i = 0; i < 6; i++)
        {
            rows.Add(new HitTrainingRow
            {
                MatchId = 2000 + i,
                MatchTime = start.AddHours(600 + i),
                Result = "0",
                HomeOdds = 2.50m,
                DrawOdds = 3.20m,
                AwayOdds = 2.70m,
                LeagueName = "样本太少"
            });
        }

        var model = HitProbabilityModel.Fit(rows);
        var plain = model.Predict(2.50m, 3.20m, 2.70m, null, null, null, null, null, null);
        var awayLeague = model.Predict(2.50m, 3.20m, 2.70m, null, null, null, null, null, "客胜联赛");
        var tiny = model.Predict(2.50m, 3.20m, 2.70m, null, null, null, null, null, "样本太少");
        Assert.True(awayLeague.Away > plain.Away + 0.05m);
        Assert.Equal(plain.Away, tiny.Away);
    }

    [Fact]
    public void Model_can_override_a_big_drop_watch_when_the_price_drop_is_the_home_side()
    {
        var rows = new List<HitTrainingRow>();
        var start = new DateTime(2024, 1, 1);
        for (var i = 0; i < 500; i++)
        {
            var drop = i % 2 == 0;
            var delta = drop ? -0.35m : 0.35m;
            rows.Add(new HitTrainingRow
            {
                MatchId = i + 1,
                MatchTime = start.AddHours(i),
                Result = drop ? "3" : "0",
                HomeOdds = 2.40m,
                HomeOddsOpen = 2.40m - delta,
                DrawOdds = 3.30m,
                AwayOdds = 2.90m,
                AhOpen = 0m,
                AhClose = 0m,
                RatioHome = 45,
                RatioAway = 30
            });
        }
        var model = HitProbabilityModel.Fit(rows);
        Assert.True(model.UsesLogistic);

        var rec = RecommendationEngine.Recommend(
            Boards.Jc, null, "001", 1, "英超", "主队", "客队",
            DateTime.Today.AddHours(20), 0m,
            -0.35m, 40m, 30m, null,
            2.40m, 3.30m, 2.90m);
        Assert.Null(rec.RecResult);

        var changed = HitProbabilityService.Stamp(
            rec,
            new HitProbabilityService.Snapshot(model, new(), true),
            2.40m, 3.30m, 2.90m, 0m, 0m);
        Assert.True(changed);
        Assert.Equal('3', rec.RecResult);
        Assert.Contains("模型改看主胜", rec.SignalDesc);
    }

    [Fact]
    public void Weak_home_attack_does_not_chase_a_three_one()
    {
        var samples = new List<ScoreSample>();
        for (var i = 0; i < 24; i++)
        {
            samples.Add(new ScoreSample { LeagueName = "测", HomeTeam = "弱主", AwayTeam = "陪练", HomeScore = i % 3 == 0 ? 1 : 0, AwayScore = i % 4 == 0 ? 1 : 0 });
            samples.Add(new ScoreSample { LeagueName = "测", HomeTeam = "铁卫", AwayTeam = "弱主", HomeScore = 0, AwayScore = i % 5 == 0 ? 1 : 0 });
            samples.Add(new ScoreSample { LeagueName = "测", HomeTeam = "客强", AwayTeam = "陪练", HomeScore = 2, AwayScore = i % 3 });
        }

        var model = ScorelineModel.Fit(samples);
        Assert.True(model.Ready);
        var top = model.Top("测", "弱主", "客强", '3', 2.5m, 1m);
        Assert.NotEmpty(top);
        Assert.Equal(1, top[0].Home - top[0].Away);
        Assert.DoesNotContain(top, s => s.Home >= 3);
    }
}
