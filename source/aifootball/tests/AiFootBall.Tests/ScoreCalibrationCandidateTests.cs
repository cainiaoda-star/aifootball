using AiFootBall.Core.Models;
using AiFootBall.Core.Models.Candidate;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

public class ScoreCalibrationCandidateTests
{
    [Fact]
    public void DixonColes_is_nonnegative_normalized_and_keeps_the_tail()
    {
        var d = FullSupportDixonColes.Create(1.4, 1.1, 0.010);
        Assert.Equal("ok", d.Status);
        Assert.True(d.Normalizer > 0);
        Assert.InRange(d.TailMass, 0, 1e-8);
        double sum = d.TailMass;
        for (var h = 0; h < d.Stride; h++)
        for (var a = 0; a < d.Stride; a++)
        {
            Assert.True(d.Prob(h, a) >= 0);
            sum += d.Prob(h, a);
        }
        Assert.InRange(sum, 1 - 1e-8, 1 + 1e-8);
        Assert.True(d.Prob(8, 0) > 0);
        var scores = PlayMargins.AggregateScores(d);
        Assert.Equal("ok", scores.Status);
        Assert.True(scores.Values[Array.IndexOf(AiFootBall.Core.Models.JcPlayDomain.Bf.ToArray(), "胜其它")] > d.Prob(8, 0));
    }

    [Fact]
    public void Negative_tau_is_rejected()
    {
        var d = FullSupportDixonColes.Create(4.5, 4.5, 0.2);
        Assert.Equal("tau-negative", d.Status);
        Assert.Equal(0, d.Prob(0, 0));
    }

    [Fact]
    public void Extreme_strength_still_converges()
    {
        var d = FullSupportDixonColes.Create(0.15, 4.5, 0.010);
        Assert.Equal("ok", d.Status);
        Assert.InRange(d.TailMass, 0, 1e-9);
        var scaled = TemperatureCalibrator.Apply(d, 1.6);
        Assert.Equal("ok", scaled.Status);
        Assert.InRange(scaled.TailMass, 0, 1e-9);
        var scores = PlayMargins.AggregateScores(scaled);
        var goals = PlayMargins.AggregateGoals(scaled);
        Assert.Equal("ok", scores.Status);
        Assert.Equal("ok", goals.Status);
        Assert.InRange(scores.Values.Sum(), 1 - 1e-6, 1 + 1e-6);
        Assert.InRange(goals.Values.Sum(), 1 - 1e-6, 1 + 1e-6);
    }

    [Fact]
    public void Temperature_one_is_identity()
    {
        var raw = FullSupportDixonColes.Create(1.3, 1.2, 0.010);
        var same = TemperatureCalibrator.Apply(raw, 1);
        Assert.Equal(raw.Prob(1, 0), same.Prob(1, 0));
        Assert.Equal(raw.TailMass, same.TailMass);
    }

    [Fact]
    public void Temperature_renormalizes()
    {
        var raw = FullSupportDixonColes.Create(1.3, 1.2, 0.010);
        var scaled = TemperatureCalibrator.Apply(raw, 1.4);
        Assert.Equal("ok", scaled.Status);
        double sum = scaled.TailMass;
        for (var h = 0; h < scaled.Stride; h++)
        for (var a = 0; a < scaled.Stride; a++)
            sum += scaled.Prob(h, a);
        Assert.InRange(sum, 1 - 1e-8, 1 + 1e-8);
    }

    [Fact]
    public void Goals_and_scores_come_from_the_same_distribution()
    {
        var d = FullSupportDixonColes.Create(1.35, 1.05, 0.010);
        var goals = PlayMargins.AggregateGoals(d);
        var scores = PlayMargins.AggregateScores(d);
        Assert.Equal(8, goals.Values.Length);
        Assert.Equal(31, scores.Values.Length);
        Assert.InRange(goals.Sum, 1 - 1e-6, 1 + 1e-6);
        Assert.InRange(scores.Sum, 1 - 1e-6, 1 + 1e-6);
        var seven = 0d;
        for (var h = 0; h < d.Stride; h++)
        for (var a = 0; a < d.Stride; a++)
            if (h + a >= 7) seven += d.Prob(h, a);
        Assert.InRange(goals.Values[7], seven, seven + d.TailMass + 1e-12);
    }

    [Fact]
    public void Half_full_without_a_joint_is_blocked()
    {
        Assert.Equal("half-full-joint-missing", PlayMargins.AggregateHalfFull().Status);
    }

    [Fact]
    public void Dejuice_refuses_a_missing_price_and_does_not_replace_market_pct()
    {
        Assert.False(DejuicedMarket.TryNormalize(new[] { 2m, 3.2m }, 3, out _, out var status));
        Assert.Equal("incomplete", status);
        Assert.True(DejuicedMarket.TryNormalize(new[] { 2m, 3m, 6m }, 3, out var probs, out status));
        Assert.Equal("ok", status);
        Assert.InRange(probs.Sum(), 1m - 0.0001m, 1m + 0.0001m);
        var market = MilkPickEngine.MarketPct(10m, "BF");
        Assert.Equal(100m / 10m / 1.410m, market);
    }

    [Fact]
    public void Candidate_stays_off_and_refuses_unverified_probabilities()
    {
        Assert.False(CandidateTickets.Enabled);
        var off = CandidateTickets.Decide(new CandidateRequest());
        Assert.Equal("candidate-disabled", off.Status);

        var noCal = CandidateTickets.DecideForTest(new CandidateRequest { Play = "BF", Odds = 10m, Stake = 1 });
        Assert.Equal("calibrator-missing", noCal.Status);

        var unbound = CandidateTickets.DecideForTest(new CandidateRequest
        {
            Calibrator = AcceptedFit(),
            Play = "BF",
            OptionCode = "1:0",
            Odds = 10m,
            Stake = 1,
            StakeLimit = 2,
            DayLimit = 5
        });
        Assert.Equal("calibrator-not-bound", unbound.Status);

        var half = CandidateTickets.DecideForTest(new CandidateRequest
        {
            Calibrator = TemperatureFit.ForTest("m", "c"),
            ModelVersion = "m",
            CodeHash = "c",
            Play = "BQ",
            OptionCode = "3-3",
            Odds = 4m,
            Stake = 1,
            StakeLimit = 2,
            DayLimit = 5,
            Sellable = true,
            Kickoff = new DateTime(2026, 11, 20),
            DecisionTime = new DateTime(2026, 11, 19)
        });
        Assert.Equal("half-full-joint-missing", half.Status);
    }

    [Fact]
    public void Joint_risk_uses_the_score_cells()
    {
        var d = FullSupportDixonColes.Create(1.3, 1.1, 0.010);
        var book = CandidateTickets.Joint(d, new[]
        {
            new OutcomeBet(1m, 10m, (h, a) => h == 1 && a == 0),
            new OutcomeBet(1m, 4m, (h, a) => h + a == 1)
        });
        Assert.Equal("ok", book.Status);
        Assert.Equal(-2m, book.AllLose);
        Assert.True(book.Best > book.AllLose);
        Assert.NotEqual(book.AllLose, book.Expected);
    }

    [Fact]
    public void Temperature_powers_each_cell_and_refuses_a_lumped_tail()
    {
        var cells = new double[16];
        cells[0] = 0.50;
        cells[1] = 0.25;
        cells[2] = 0.125;
        cells[3] = 0.125;
        var raw = SupportDistribution.Accepted(cells, 4, 1, 0);
        var scaled = TemperatureCalibrator.Apply(raw, 2);
        double z = 0;
        for (var i = 0; i < cells.Length; i++) z += Math.Sqrt(cells[i]);
        Assert.InRange(scaled.Prob(0, 2), Math.Sqrt(0.125) / z - 1e-12, Math.Sqrt(0.125) / z + 1e-12);
        var lumped = SupportDistribution.Accepted(cells, 4, 1, 0.2);
        Assert.Equal("tail-not-enumerated", TemperatureCalibrator.Apply(lumped, 2).Status);
    }

    [Fact]
    public void Result_and_integer_handicap_use_each_score_cell()
    {
        var cell = new double[7 * 7];
        cell[6 * 7 + 0] = 1;
        var d = SupportDistribution.Accepted(cell, 7, 1, 0);
        var spf = PlayMargins.AggregateResult(d);
        Assert.Equal("ok", spf.Status);
        Assert.Equal(1, spf.Values[0], 9);
        var give = PlayMargins.AggregateHandicap(d, -1m);
        Assert.Equal(1, give.Values[0], 9);
        var receive = PlayMargins.AggregateHandicap(d, 1.5m);
        Assert.Equal("handicap-not-integer", receive.Status);

        var even = new double[4];
        even[0] = even[1] = even[2] = even[3] = 0.25;
        var grid = SupportDistribution.Accepted(even, 2, 1, 0);
        var plus = PlayMargins.AggregateHandicap(grid, 1m);
        Assert.Equal(0.75, plus.Values[0], 9);
        Assert.Equal(0.25, plus.Values[1], 9);
        Assert.Equal(0, plus.Values[2], 9);
        var minus = PlayMargins.AggregateHandicap(grid, -1m);
        Assert.Equal(0.25, minus.Values[1], 9);
        Assert.Equal(0.75, minus.Values[2], 9);
        var level = PlayMargins.AggregateHandicap(grid, 0m);
        Assert.Equal(0.5, level.Values[1], 9);
        var wide = new double[9 * 9];
        wide[8 * 9 + 0] = 1;
        var eight = SupportDistribution.Accepted(wide, 9, 1, 0);
        Assert.Equal(1, PlayMargins.AggregateHandicap(eight, -1m).Values[0], 9);
        Assert.Equal("hit", CandidateTickets.Settle("NSPF", "3", 8, 0, -1m));
        Assert.Equal("void:half-full-joint-missing", CandidateTickets.Settle("BQ", "3-3", 1, 0, null));
        Assert.Equal("unsettled", CandidateTickets.Settle("SPF", "3", null, null, null));
    }

    [Fact]
    public void Exposure_uses_nonnegative_loss_and_rejects_the_limit_cases()
    {
        var d = FullSupportDixonColes.Create(1.2, 1.0, 0.010);
        Assert.Equal("budget-exceeded", CandidateTickets.DecideForTest(Ready(d, 100m, 1m)).Status);
        Assert.Equal("stake-not-positive", CandidateTickets.DecideForTest(Ready(d, 0m, 10m)).Status);
        Assert.Equal("stake-negative", CandidateTickets.DecideForTest(Ready(d, -1m, 10m)).Status);
        var open = Ready(d, 1m, 1m);
        open = new CandidateRequest
        {
            Calibrator = open.Calibrator, ModelVersion = "m", CodeHash = "c", Distribution = d,
            Play = "JQ", OptionCode = "2", Odds = 4m, Stake = 1m, StakeLimit = 1m, DayLimit = 1000m,
            Sellable = true, Kickoff = open.Kickoff, DecisionTime = open.DecisionTime,
            MatchKey = "match", DayKey = "2026-11-19", Ledger = new CandidateLedger(), MinNetEv = -1m,
            SameMatchStake = 1m, ExposureVersion = 0, ObservedDayUsed = 0m, ReservationId = "open",
            OpenBets = new[] { new OutcomeBet(1m, 4m, (_, _) => false) }
        };
        Assert.Equal("budget-exceeded", CandidateTickets.DecideForTest(open).Status);
        var ledger = new CandidateLedger();
        var first = Ready(d, 1m, 1m);
        var second = Ready(d, 1m, 1m);
        first = new CandidateRequest
        {
            Calibrator = first.Calibrator, ModelVersion = "m", CodeHash = "c", Distribution = d,
            Play = "JQ", OptionCode = "2", Odds = 4m, Stake = 1m, StakeLimit = 1m, DayLimit = 1000m,
            Sellable = true, Kickoff = first.Kickoff, DecisionTime = first.DecisionTime,
            MatchKey = "match", DayKey = "2026-11-19", Ledger = ledger, MinNetEv = -1m,
            ExposureVersion = 0, ObservedDayUsed = 0m, ReservationId = "left"
        };
        second = new CandidateRequest
        {
            Calibrator = second.Calibrator, ModelVersion = "m", CodeHash = "c", Distribution = d,
            Play = "JQ", OptionCode = "2", Odds = 4m, Stake = 1m, StakeLimit = 1m, DayLimit = 1000m,
            Sellable = true, Kickoff = second.Kickoff, DecisionTime = second.DecisionTime,
            MatchKey = "match", DayKey = "2026-11-19", Ledger = ledger, MinNetEv = -1m,
            ExposureVersion = 0, ObservedDayUsed = 0m, ReservationId = "right"
        };
        var left = CandidateTickets.DecideForTest(first);
        var right = CandidateTickets.DecideForTest(second);
        Assert.Equal("selected", left.Status);
        Assert.Equal("concurrent-exposure", right.Status);
        var book = CandidateTickets.Joint(d, new[]
        {
            new OutcomeBet(1m, 10m, (h, a) => h == 1 && a == 0),
            new OutcomeBet(1m, 4m, (h, a) => h + a == 1)
        });
        Assert.True(book.MaxLoss > 0);
        Assert.True(book.WorstNet < 0);
        Assert.NotEqual(book.MaxLoss, book.WorstNet);
    }

    static CandidateRequest Ready(SupportDistribution d, decimal stake, decimal matchLimit) => new()
    {
        Calibrator = TemperatureFit.ForTest("m", "c"),
        ModelVersion = "m",
        CodeHash = "c",
        Distribution = d,
        Play = "JQ",
        OptionCode = "2",
        Odds = 4m,
        Stake = stake,
        StakeLimit = matchLimit,
        DayLimit = 1000m,
        Sellable = true,
        Kickoff = new DateTime(2026, 11, 20),
        DecisionTime = new DateTime(2026, 11, 19),
        MatchKey = "match",
        DayKey = "2026-11-19",
        Ledger = new CandidateLedger(),
        ExposureVersion = 0,
        ObservedDayUsed = 0m,
        ReservationId = Guid.NewGuid().ToString("N"),
        MinNetEv = -1m
    };

    [Fact]
    public void Day_limit_admits_only_one_of_two_concurrent_reservations()
    {
        var store = new VersionedExposureStore();
        store.Seed("2026-11-19", 900m, 7);
        var left = new CandidateLedger(store);
        var right = new CandidateLedger(store);
        var d = FullSupportDixonColes.Create(1.2, 1.0, 0.010);
        var a = DayRequest(d, left, "a");
        var b = DayRequest(d, right, "b");
        var chosen = CandidateTickets.DecideForTest(a);
        var results = new[] { chosen.Status, CandidateTickets.DecideForTest(b).Status };
        Assert.Equal(1, results.Count(x => x == "selected"));
        Assert.Equal(1000m, store.DayUsed("2026-11-19"));
        var binding = new DecisionBinding("JQ", "2", 4m, chosen.Probability, 0m, "m", "c", CandidateProtocol.RuleVersion, CandidateProtocol.TestEvidenceId);
        var again = store.TryReserve("a", "2026-11-19", "match-a", 7, 900m, 100m, 1000m, 100m, 1000m, binding);
        Assert.Equal("idempotent", again.Status);
        Assert.Equal(1000m, store.DayUsed("2026-11-19"));
        var moved = store.TryReserve("a", "2026-11-20", "match-a", 7, 900m, 100m, 1000m, 100m, 1000m);
        Assert.Equal("reservation-conflict", moved.Status);
        Assert.Equal(1000m, store.DayUsed("2026-11-19"));
        store.Abort("a");
        Assert.Equal(900m, store.DayUsed("2026-11-19"));
        var restarted = new CandidateLedger(store);
        Assert.Equal(900m, restarted.Store.DayUsed("2026-11-19"));
        Assert.Contains("UPDLOCK", VersionedExposureStore.SqlText, StringComparison.Ordinal);
        Assert.Contains("VersionNo=@version", VersionedExposureStore.SqlText, StringComparison.Ordinal);
    }

    [Fact]
    public void Scalar_same_match_stake_cannot_replace_the_open_bets()
    {
        var d = FullSupportDixonColes.Create(1.2, 1.0, 0.010);
        var request = Ready(d, 1m, 10m);
        request = new CandidateRequest
        {
            Calibrator = request.Calibrator, ModelVersion = "m", CodeHash = "c", Distribution = d,
            Play = "JQ", OptionCode = "2", Odds = 4m, Stake = 1m, StakeLimit = 10m, DayLimit = 1000m,
            Sellable = true, Kickoff = request.Kickoff, DecisionTime = request.DecisionTime,
            MatchKey = "match", DayKey = "2026-11-19", Ledger = request.Ledger, MinNetEv = -1m,
            ExposureVersion = 0, ObservedDayUsed = 0m, ReservationId = "scalar",
            SameMatchStake = 9999m
        };
        Assert.Equal("same-match-bets-missing", CandidateTickets.DecideForTest(request).Status);
        request = new CandidateRequest
        {
            Calibrator = request.Calibrator, ModelVersion = "m", CodeHash = "c", Distribution = d,
            Play = "JQ", OptionCode = "2", Odds = 4m, Stake = 1m, StakeLimit = 10m, DayLimit = 1000m,
            Sellable = true, Kickoff = request.Kickoff, DecisionTime = request.DecisionTime,
            MatchKey = "match", DayKey = "2026-11-19", Ledger = new CandidateLedger(), MinNetEv = -1m,
            ExposureVersion = 0, ObservedDayUsed = 0m, ReservationId = "scalar-2",
            SameMatchStake = 9999m, OpenBets = new[] { new OutcomeBet(1m, 4m, (_, _) => false) }
        };
        Assert.Equal("same-match-stake-inconsistent", CandidateTickets.DecideForTest(request).Status);
    }

    [Fact]
    public void Official_commit_persists_three_plays_and_rolls_back_when_save_fails()
    {
        var d = FullSupportDixonColes.Create(1.2, 1.0, 0.010);
        var dir = Path.Combine(Path.GetTempPath(), "aifb-commit-" + Guid.NewGuid().ToString("N"));
        var log = new AppendOnlyTicketLog(Path.Combine(dir, "tickets.log"));
        var store = new VersionedExposureStore();
        foreach (var play in new[] { "JQ", "SPF", "BF" })
        {
            var code = play == "JQ" ? "2" : play == "SPF" ? "3" : "1:0";
            var request = PlayRequest(d, store, play, code, play);
            var saved = CandidateTickets.CommitForTest(request, log);
            Assert.Equal("saved", saved.Status);
        }
        var blocked = CandidateTickets.CommitForTest(PlayRequest(d, store, "BQ", "3-3", "bq"), log);
        Assert.Equal("blocked:half-full-joint-missing", blocked.Status);
        Assert.DoesNotContain(log.Replay(), x => x.Play == "BQ");
        log.Settle("JQ", "hit");
        Assert.Equal("hit", log.Replay().Single(x => x.Id == "JQ").Settlement);
        var failed = CandidateTickets.CommitForTest(PlayRequest(d, new VersionedExposureStore(), "NSPF", "3", "nspf", -1m), log, failAfterReserve: true);
        Assert.Equal("save-failed", failed.Status);
        Assert.DoesNotContain(log.Replay(), x => x.Id == "nspf");
        Assert.Equal(3, log.Replay().Count);
        Assert.Equal("blocked:candidate-disabled", CandidateTickets.Commit(PlayRequest(d, store, "JQ", "2", "live"), log).Status);
    }

    [Fact]
    public void Jobs_fixture_is_off_and_has_no_secret()
    {
        Assert.True(OperatingSystem.IsWindows());
        var legacy = LegacyBinaryProbe.PackageDirectory(AppContext.BaseDirectory);
        Assert.False(legacy.Contains("deploy-package", StringComparison.OrdinalIgnoreCase));
        var path = Path.Combine(Directory.GetParent(legacy)!.FullName, "jobs.default-off.json");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("\"Enabled\": false", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_failure_retries_the_same_id_only_after_a_new_reservation()
    {
        var d = FullSupportDixonColes.Create(1.2, 1.0, 0.010);
        var dir = Path.Combine(Path.GetTempPath(), "aifb-state-" + Guid.NewGuid().ToString("N"));
        var store = new VersionedExposureStore(dir);
        var logPath = Path.Combine(dir, "tickets.log");
        Directory.CreateDirectory(dir);
        var first = PlayRequest(d, store, "JQ", "2", "retry");
        using (var block = new FileStream(logPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            var blocked = new AppendOnlyTicketLog(logPath);
            var failed = CandidateTickets.CommitForTest(first, blocked);
            Assert.Equal("save-failed", failed.Status);
            Assert.Equal(0, block.Length);
        }
        Assert.Equal("aborted", store.State("retry"));
        Assert.Equal(0m, store.DayUsed("2026-11-19"));
        var stale = CandidateTickets.CommitForTest(first, new AppendOnlyTicketLog(logPath));
        Assert.Equal("blocked:concurrent-exposure", stale.Status);
        var request = PlayRequest(d, store, "JQ", "2", "retry");
        var saved = CandidateTickets.CommitForTest(request, new AppendOnlyTicketLog(logPath));
        Assert.Equal("saved", saved.Status);
        var ticket = new AppendOnlyTicketLog(logPath).Replay().Single(x => x.Id == "retry");
        Assert.Equal(request.Stake, ticket.Stake);
        Assert.Equal(request.Stake, store.DayUsed("2026-11-19"));
        var duplicate = CandidateTickets.CommitForTest(request, new AppendOnlyTicketLog(logPath));
        Assert.Equal("idempotent", duplicate.Status);
        Assert.Equal(request.Stake, store.DayUsed("2026-11-19"));
        var otherDay = PlayRequest(d, store, "JQ", "2", "retry");
        otherDay = new CandidateRequest
        {
            Calibrator = otherDay.Calibrator, ModelVersion = "m", CodeHash = "c", Distribution = d,
            Play = "JQ", OptionCode = "2", Odds = 4m, Stake = 2m, StakeLimit = 100m, DayLimit = 1000m,
            Sellable = true, Kickoff = otherDay.Kickoff, DecisionTime = otherDay.DecisionTime,
            MatchKey = "other", DayKey = "2026-11-20", Ledger = new CandidateLedger(store), MinNetEv = -1m,
            ExposureVersion = store.Version("2026-11-20"), ObservedDayUsed = store.DayUsed("2026-11-20"),
            ReservationId = "retry", MatchId = 1
        };
        Assert.Equal("blocked:reservation-conflict", CandidateTickets.CommitForTest(otherDay, new AppendOnlyTicketLog(logPath)).Status);
        Assert.Equal(request.Stake, store.DayUsed("2026-11-19"));
        var restarted = new VersionedExposureStore(dir);
        Assert.Equal(request.Stake, restarted.DayUsed("2026-11-19"));
        Assert.Equal("saved", restarted.State("retry"));
        var other = new VersionedExposureStore(dir);
        var clash = other.TryReserve("other-id", "2026-11-19", "match-b", restarted.Version("2026-11-19"), 0m, 1m, 1000m, 1m, 1000m);
        Assert.Equal("concurrent-exposure", clash.Status);
        Assert.Equal(request.Stake, other.DayUsed("2026-11-19"));
    }

    [Fact]
    public void Saved_ticket_still_counts_when_the_request_says_zero()
    {
        var d = FullSupportDixonColes.Create(1.2, 1.0, 0.010);
        var dir = Path.Combine(Path.GetTempPath(), "aifb-match-" + Guid.NewGuid().ToString("N"));
        var store = new VersionedExposureStore(dir);
        var log = new AppendOnlyTicketLog(Path.Combine(dir, "tickets.log"));
        var first = MatchRequest(d, store, "one", 100m);
        Assert.Equal("saved", CandidateTickets.CommitForTest(first, log).Status);
        Assert.Equal("saved", store.State("one"));
        var second = MatchRequest(d, store, "two", 100m);
        Assert.Equal(0m, second.SameMatchStake);
        Assert.Null(second.OpenBets);
        Assert.Equal("blocked:budget-exceeded", CandidateTickets.CommitForTest(second, log).Status);
        Assert.Equal(100m, store.DayUsed("2026-11-19"));
        Assert.Equal("saved", store.State("one"));
        Assert.Equal("", store.State("two"));
        Assert.Equal(100m, log.Replay().Single().Stake);
    }

    [Fact]
    public void Ticket_written_before_mark_is_recovered_and_not_released()
    {
        var d = FullSupportDixonColes.Create(1.2, 1.0, 0.010);
        var dir = Path.Combine(Path.GetTempPath(), "aifb-gap-" + Guid.NewGuid().ToString("N"));
        var store = new VersionedExposureStore(dir);
        var logPath = Path.Combine(dir, "tickets.log");
        var log = new AppendOnlyTicketLog(logPath);
        var request = MatchRequest(d, store, "gap", 100m);
        request = new CandidateRequest
        {
            Calibrator = request.Calibrator, ModelVersion = "m", CodeHash = "c", Distribution = d,
            Play = "JQ", OptionCode = "2", Odds = 4m, Stake = 100m, StakeLimit = 1000m, DayLimit = 1000m,
            Sellable = true, Kickoff = request.Kickoff, DecisionTime = request.DecisionTime,
            MatchKey = "same-match", DayKey = "2026-11-19", Ledger = request.Ledger, MinNetEv = -1m,
            ExposureVersion = 0, ObservedDayUsed = 0m, ReservationId = "gap", MatchId = 1
        };
        var selected = CandidateTickets.DecideForTest(request);
        Assert.Equal("selected", selected.Status);
        var written = log.Append(new AiFootBall.Core.Models.Candidate.TicketRecord
        {
            Id = "gap", Play = "JQ", Option = "2", MatchId = 1, Odds = 4m, Stake = 100m, Lane = "official",
            DecisionAt = request.DecisionTime, Kickoff = request.Kickoff, Probability = selected.Probability,
            ModelVersion = "m", CodeHash = "c", RuleVersion = CandidateProtocol.RuleVersion,
            EvidenceId = CandidateProtocol.TestEvidenceId
        });
        Assert.Equal("saved", written.Status);
        var restarted = new VersionedExposureStore(dir);
        Assert.Equal("reserved", restarted.State("gap"));
        Assert.Equal(100m, restarted.DayUsed("2026-11-19"));
        using (new FileStream(Path.Combine(dir, "exposure.journal"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => restarted.MarkSaved("gap"));
        Assert.Equal("reserved", restarted.State("gap"));
        Assert.False(restarted.Abort("gap", new AppendOnlyTicketLog(logPath)));
        Assert.Equal(100m, restarted.DayUsed("2026-11-19"));
        var retry = new CandidateRequest
        {
            Calibrator = request.Calibrator, ModelVersion = "m", CodeHash = "c", Distribution = d,
            Play = "JQ", OptionCode = "2", Odds = 4m, Stake = 100m, StakeLimit = 1000m, DayLimit = 1000m,
            Sellable = true, Kickoff = request.Kickoff, DecisionTime = request.DecisionTime,
            MatchKey = "same-match", DayKey = "2026-11-19", Ledger = new CandidateLedger(restarted), MinNetEv = -1m,
            ExposureVersion = 0, ObservedDayUsed = 0m, ReservationId = "gap", MatchId = 1
        };
        Assert.Equal("recovered", CandidateTickets.CommitForTest(retry, new AppendOnlyTicketLog(logPath)).Status);
        Assert.Equal("saved", restarted.State("gap"));
        Assert.Equal(100m, restarted.DayUsed("2026-11-19"));
        Assert.False(restarted.Abort("gap", new AppendOnlyTicketLog(logPath)));
        Assert.Equal("idempotent", CandidateTickets.CommitForTest(retry, new AppendOnlyTicketLog(logPath)).Status);
        Assert.Equal(100m, restarted.DayUsed("2026-11-19"));
        var changed = new CandidateRequest
        {
            Calibrator = request.Calibrator, ModelVersion = "m", CodeHash = "c", Distribution = d,
            Play = "JQ", OptionCode = "2", Odds = 5m, Stake = 100m, StakeLimit = 1000m, DayLimit = 1000m,
            Sellable = true, Kickoff = request.Kickoff, DecisionTime = request.DecisionTime,
            MatchKey = "same-match", DayKey = "2026-11-19", Ledger = new CandidateLedger(restarted), MinNetEv = -1m,
            ExposureVersion = 0, ObservedDayUsed = 0m, ReservationId = "gap", MatchId = 1
        };
        Assert.Equal("blocked:reservation-conflict", CandidateTickets.CommitForTest(changed, new AppendOnlyTicketLog(logPath)).Status);
        Assert.Equal(100m, restarted.DayUsed("2026-11-19"));
        Assert.Equal(4m, new AppendOnlyTicketLog(logPath).Replay().Single().Odds);
    }

    [Fact]
    public void Corrupt_journal_blocks_and_a_torn_tail_is_recovered()
    {
        var badDir = Path.Combine(Path.GetTempPath(), "aifb-bad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(badDir);
        File.WriteAllText(Path.Combine(badDir, "exposure.journal"),
            "seed\t2026-11-19\t10\t1" + Environment.NewLine + "mystery\t1" + Environment.NewLine);
        var bad = new VersionedExposureStore(badDir);
        Assert.Equal("journal-corrupt", bad.ReadJournalStatus());
        Assert.Equal("journal-corrupt", bad.TryReserve("x", "2026-11-19", "m", 0, 0, 1, 10, 1, 10).Status);
        Assert.Throws<InvalidOperationException>(() => bad.DayUsed("2026-11-19"));

        var tornDir = Path.Combine(Path.GetTempPath(), "aifb-torn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tornDir);
        File.WriteAllText(Path.Combine(tornDir, "exposure.journal"), "seed\t2026-11-19\t10\t1" + Environment.NewLine + "rese");
        var torn = new VersionedExposureStore(tornDir);
        Assert.Equal("recovered-torn-tail", torn.ReadJournalStatus());
        Assert.Equal(10m, torn.DayUsed("2026-11-19"));
        var reserved = torn.TryReserve("tail", "2026-11-19", "m", 1, 10m, 1m, 100m, 1m, 100m);
        Assert.Equal("reserved", reserved.Status);
        var journal = File.ReadAllText(Path.Combine(tornDir, "exposure.journal"));
        Assert.EndsWith("\n", journal);
        Assert.DoesNotContain(journal.Replace("\r", "", StringComparison.Ordinal).Split('\n'), line => line == "rese");
        Assert.Contains(Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes("rese")),
            File.ReadAllText(Path.Combine(tornDir, "exposure.journal.torn")));
        var again = new VersionedExposureStore(tornDir);
        Assert.Equal("reserved", again.State("tail"));
        Assert.Equal(11m, again.DayUsed("2026-11-19"));
    }

    [Fact]
    public void Torn_ticket_tail_is_kept_then_a_new_process_rereads_the_append()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aifb-ticket-tail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "tickets.log");
        var complete = string.Join('\t', "save", "keep", "JQ", "2", "1", "4.000", "1.000", "research",
            "2026-11-19 08:00:00", "2026-11-20 08:00:00", "unsettled", "0.25000000", "0.000", "m", "c", "r", "e");
        File.WriteAllText(path, complete + Environment.NewLine + "resid");
        var receipt = new AppendOnlyTicketLog(path).Append(SampleTicket("next", 3m));
        Assert.Equal("saved", receipt.Status);
        var physical = File.ReadAllLines(path).Where(x => x.StartsWith("save\t", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, physical.Length);
        Assert.DoesNotContain("resid", File.ReadAllText(path));
        Assert.Contains(Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes("resid")), File.ReadAllText(path + ".torn"));
        var reread = new AppendOnlyTicketLog(path).Replay();
        Assert.Equal(2, reread.Count);
        Assert.Contains(reread, x => x.Id == "keep" && x.Odds == 4m && x.DecisionAt == new DateTime(2026, 11, 19, 8, 0, 0));
        Assert.Contains(reread, x => x.Id == "next" && x.Odds == 3m);
        File.WriteAllText(path, "mystery\t1" + Environment.NewLine);
        Assert.Throws<InvalidDataException>(() => new AppendOnlyTicketLog(path).Append(SampleTicket("nope", 4m)));
    }

    [Fact]
    public void Torn_tail_recovery_keeps_the_original_when_the_replacement_cannot_be_written()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aifb-ticket-fail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "tickets.log");
        var log = new AppendOnlyTicketLog(path);
        for (var i = 0; i < 100; i++)
            Assert.Equal("saved", log.Append(SampleTicket("t" + i.ToString("000"), 4m)).Status);
        var original = File.ReadAllBytes(path);
        File.AppendAllText(path, "resid");
        var torn = File.ReadAllBytes(path);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => DurableLog.QuarantineTornTail(path));
        }
        Assert.Equal(torn, File.ReadAllBytes(path));
        Assert.DoesNotContain(Directory.GetFiles(dir), file => file.EndsWith(".torn", StringComparison.Ordinal) || file.Contains(".bak-", StringComparison.Ordinal));
        Assert.Equal("saved", new AppendOnlyTicketLog(path).Append(SampleTicket("after", 5m)).Status);
        var recovered = new AppendOnlyTicketLog(path).Replay();
        Assert.Equal(101, recovered.Count);
        Assert.Contains(recovered, row => row.Id == "t000" && row.Odds == 4m);
        Assert.Contains(recovered, row => row.Id == "t099" && row.Odds == 4m);
        Assert.Contains(recovered, row => row.Id == "after" && row.Odds == 5m);
        Assert.Contains(Directory.GetFiles(dir), file => file.Contains(".bak-", StringComparison.Ordinal));
        var backup = Directory.GetFiles(dir).Single(file => file.Contains(".bak-", StringComparison.Ordinal));
        var backupText = File.ReadAllText(backup);
        Assert.Contains("resid", backupText, StringComparison.Ordinal);
        Assert.Equal(100, backupText.Split('\n').Count(line => line.StartsWith("save\t", StringComparison.Ordinal)));
        Assert.Contains(Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes("resid")), File.ReadAllText(path + ".torn"), StringComparison.Ordinal);
        var replay = RunTicketProcess(path, "replay", "0");
        replay.StartInfo.ArgumentList.Clear();
        replay.StartInfo.ArgumentList.Add(path);
        replay.StartInfo.ArgumentList.Add("replay");
        replay.Start();
        var count = replay.StandardOutput.ReadToEnd();
        _ = replay.StandardError.ReadToEnd();
        Assert.True(replay.WaitForExit(15000));
        Assert.Equal("101", count);
        Assert.Equal(100, original.Count(b => b == (byte)'\n'));
    }

    [Fact]
    public void Two_processes_leave_one_physical_ticket_that_replay_matches()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aifb-ticket-proc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "tickets.log");
        var same = new[] { RunTicketProcess(path, "same", "4.000"), RunTicketProcess(path, "same", "4.000") };
        same[0].Start();
        same[1].Start();
        var sameOut = new[] { same[0].StandardOutput.ReadToEndAsync(), same[1].StandardOutput.ReadToEndAsync() };
        var sameErr = new[] { same[0].StandardError.ReadToEndAsync(), same[1].StandardError.ReadToEndAsync() };
        Assert.True(same[0].WaitForExit(15000));
        Assert.True(same[1].WaitForExit(15000));
        var statuses = new[] { sameOut[0].GetAwaiter().GetResult(), sameOut[1].GetAwaiter().GetResult() };
        _ = sameErr[0].GetAwaiter().GetResult();
        _ = sameErr[1].GetAwaiter().GetResult();
        Assert.Equal(new[] { "idempotent", "saved" }, statuses.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        var lines = File.ReadAllLines(path).Where(x => x.StartsWith("save\t", StringComparison.Ordinal)).ToArray();
        Assert.Single(lines);
        var replay = new AppendOnlyTicketLog(path).Replay();
        var row = Assert.Single(replay);
        Assert.Equal(4m, row.Odds);
        Assert.Contains("4.000", lines[0], StringComparison.Ordinal);

        var clashDir = Path.Combine(Path.GetTempPath(), "aifb-ticket-clash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(clashDir);
        var clashPath = Path.Combine(clashDir, "tickets.log");
        var clash = new[] { RunTicketProcess(clashPath, "clash", "4.000"), RunTicketProcess(clashPath, "clash", "5.000") };
        clash[0].Start();
        clash[1].Start();
        var clashOut = new[] { clash[0].StandardOutput.ReadToEndAsync(), clash[1].StandardOutput.ReadToEndAsync() };
        var clashErr = new[] { clash[0].StandardError.ReadToEndAsync(), clash[1].StandardError.ReadToEndAsync() };
        Assert.True(clash[0].WaitForExit(15000));
        Assert.True(clash[1].WaitForExit(15000));
        var clashStatus = new[] { clashOut[0].GetAwaiter().GetResult(), clashOut[1].GetAwaiter().GetResult() };
        _ = clashErr[0].GetAwaiter().GetResult();
        _ = clashErr[1].GetAwaiter().GetResult();
        Assert.Equal(new[] { "reservation-conflict", "saved" }, clashStatus.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        var clashLines = File.ReadAllLines(clashPath).Where(x => x.StartsWith("save\t", StringComparison.Ordinal)).ToArray();
        var clashLine = Assert.Single(clashLines);
        var clashRow = Assert.Single(new AppendOnlyTicketLog(clashPath).Replay());
        Assert.Contains(clashRow.Odds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture), clashLine, StringComparison.Ordinal);
    }

    static System.Diagnostics.Process RunTicketProcess(string path, string id, string odds)
    {
        string? exe = null;
        foreach (var config in new[] { "Release", "Debug" })
        {
            var candidate = Path.Combine(FitTraceTests.SourceRoot(), "tools", "TicketLogOnce", "bin", config, "net8.0", "TicketLogOnce.exe");
            if (!File.Exists(candidate)) continue;
            exe = candidate;
            break;
        }
        Assert.False(string.IsNullOrEmpty(exe));
        return new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo(exe!)
            {
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                ArgumentList = { path, id, odds }
            }
        };
    }

    static AiFootBall.Core.Models.Candidate.TicketRecord SampleTicket(string id, decimal odds) => new()
    {
        Id = id, Play = "JQ", Option = "2", MatchId = 1, Odds = odds, Stake = 1m, Lane = "research",
        DecisionAt = new DateTime(2026, 11, 19, 8, 0, 0), Kickoff = new DateTime(2026, 11, 20, 8, 0, 0),
        Probability = 0.25, Handicap = 0m, ModelVersion = "m", CodeHash = "c", RuleVersion = "r", EvidenceId = "e"
    };

    static CandidateRequest MatchRequest(SupportDistribution d, VersionedExposureStore store, string id, decimal stake) => new()
    {
        Calibrator = TemperatureFit.ForTest("m", "c"),
        ModelVersion = "m", CodeHash = "c", Distribution = d,
        Play = "JQ", OptionCode = "2", Odds = 4m, Stake = stake, StakeLimit = 100m, DayLimit = 1000m,
        Sellable = true, Kickoff = new DateTime(2026, 11, 20), DecisionTime = new DateTime(2026, 11, 19),
        MatchKey = "same-match", DayKey = "2026-11-19", Ledger = new CandidateLedger(store), MinNetEv = -1m,
        ExposureVersion = store.Version("2026-11-19"), ObservedDayUsed = store.DayUsed("2026-11-19"),
        ReservationId = id, MatchId = 1
    };

    [Fact]
    public void Post_temperature_tail_is_not_reported_as_zero_across_the_legal_domain()
    {
        var low = FullSupportDixonColes.Create(0.15, 0.15, 0.010);
        var atTwo = TemperatureCalibrator.TruncationError(low, 0.5);
        var atSelection = TemperatureCalibrator.TruncationError(low, 1d / 1.1);
        Assert.True(atTwo.Internal + atTwo.External > 0);
        Assert.True(atSelection.Internal + atSelection.External < 1e-12);
        Assert.True(atTwo.Internal + atTwo.External > atSelection.Internal + atSelection.External);
        var scaled = TemperatureCalibrator.Apply(low, 2);
        Assert.True(scaled.Ok);
        Assert.True(scaled.TailMass + scaled.InternalTruncation > 0);
        foreach (var lambda in new[] { 0.15, 1.2, 4.5 })
        foreach (var temperature in new[] { 1.1, 1.25, 2.0 })
        {
            var raw = FullSupportDixonColes.Create(lambda, lambda, 0.010);
            var applied = TemperatureCalibrator.Apply(raw, temperature);
            Assert.True(applied.Ok);
            var error = TemperatureCalibrator.TruncationError(raw, 1d / temperature);
            Assert.True(double.IsFinite(error.Internal) && double.IsFinite(error.External));
            if (error.Internal + error.External > 0)
                Assert.True(applied.TailMass + applied.InternalTruncation > 0);
            for (var h = 0; h < raw.Stride && h <= 30; h++)
            for (var a = 0; a < raw.Stride && a <= 30; a++)
            {
                var logq = CellLog(lambda, h) + CellLog(lambda, a);
                if (Math.Exp(logq) > 1e-18) Assert.True(raw.Prob(h, a) > 0);
            }
        }
        foreach (var home in new[] { 0.15, 1.2, 4.5 })
        foreach (var away in new[] { 0.15, 1.2, 4.5 })
        {
            var pair = FullSupportDixonColes.Create(home, away, 0.010);
            Assert.True(pair.Ok);
            for (var h = 0; h < pair.Stride && h <= 30; h++)
            for (var a = 0; a < pair.Stride && a <= 30; a++)
            {
                var mass = Math.Exp(CellLog(home, h) + CellLog(away, a));
                if (mass > 1e-18) Assert.True(pair.Prob(h, a) > 0);
            }
        }
        var uneven = FullSupportDixonColes.Create(1.2, 4, 0.010);
        Assert.True(uneven.Ok);
        Assert.True(19 < uneven.Stride && 2 < uneven.Stride);
        var truth = Math.Exp(CellLog(1.2, 19) + CellLog(4, 2) - Math.Log(uneven.Normalizer));
        Assert.InRange(truth, 1e-18, 1e-16);
        Assert.True(uneven.Prob(19, 2) > 0);
        Assert.InRange(uneven.Prob(19, 2) / truth, 0.5, 1.5);
        var atTemperatureTwo = TemperatureCalibrator.Apply(uneven, 2);
        var atSelected = TemperatureCalibrator.Apply(uneven, 1.1);
        Assert.True(atTemperatureTwo.Ok && atTemperatureTwo.Prob(19, 2) > 1e-12);
        Assert.True(atSelected.Ok && atSelected.Prob(19, 2) > 0 && atSelected.Prob(19, 2) < 1e-12);
        var holes = TemperatureCalibrator.TruncationError(uneven, 0.5);
        Assert.True(holes.Internal < truth);
    }

    static double CellLog(double lambda, int k)
    {
        double s = -lambda + k * Math.Log(lambda);
        for (var i = 1; i <= k; i++) s -= Math.Log(i);
        return s;
    }

    static CandidateRequest DayRequest(SupportDistribution d, CandidateLedger ledger, string id) => new()
    {
        Calibrator = TemperatureFit.ForTest("m", "c"),
        ModelVersion = "m", CodeHash = "c", Distribution = d,
        Play = "JQ", OptionCode = "2", Odds = 4m, Stake = 100m, StakeLimit = 1000m, DayLimit = 1000m,
        Sellable = true, Kickoff = new DateTime(2026, 11, 20), DecisionTime = new DateTime(2026, 11, 19),
        MatchKey = "match-" + id, DayKey = "2026-11-19", Ledger = ledger, MinNetEv = -1m,
        ExposureVersion = 7, ObservedDayUsed = 900m, ReservationId = id
    };

    static CandidateRequest PlayRequest(SupportDistribution d, VersionedExposureStore store, string play, string code, string id, decimal handicap = 0) => new()
    {
        Calibrator = TemperatureFit.ForTest("m", "c"),
        ModelVersion = "m", CodeHash = "c", Distribution = d,
        Play = play, OptionCode = code, Odds = 4m, Stake = 1m, StakeLimit = 100m, DayLimit = 1000m,
        Sellable = true, Kickoff = new DateTime(2026, 11, 20), DecisionTime = new DateTime(2026, 11, 19),
        MatchKey = id, DayKey = "2026-11-19", Ledger = new CandidateLedger(store), MinNetEv = -1m,
        ExposureVersion = store.Version("2026-11-19"), ObservedDayUsed = store.DayUsed("2026-11-19"),
        ReservationId = id, Handicap = handicap, MatchId = 1
    };

    [Fact]
    public void Very_large_lambda_does_not_pretend_to_converge()
    {
        Assert.Equal("tail-not-converged", FullSupportDixonColes.Create(200, 200, 0.000001).Status);
    }

    [Fact]
    public void Short_history_does_not_produce_a_temperature()
    {
        var d = FullSupportDixonColes.Create(1.2, 1.1, 0.010);
        var fit = TemperatureCalibrator.Fit(
            new[] { d, d, d },
            new[] { (1, 0), (1, 0), (0, 0) },
            CandidateProtocol.AlphaInnerDefault);
        Assert.Equal("insufficient-matches", fit.Status);
        Assert.False(fit.Ok);
    }

    [Fact]
    public void Fixture_temperature_moves_above_one_when_the_raw_distribution_is_peaked()
    {
        var peaked = Peaked();
        var n = CandidateProtocol.MinCalibrationMatches;
        var predicted = Enumerable.Repeat(peaked, n).ToArray();
        var actual = Enumerable.Repeat((1, 0), n).ToArray();
        var fit = TemperatureCalibrator.Fit(predicted, actual, CandidateProtocol.AlphaInnerDefault);
        Assert.Equal("computed", fit.Status);
        Assert.Equal("", fit.EvidenceId);
        Assert.True(fit.Temperature > 1);
    }

    [Fact]
    public void Split_checker_catches_the_same_match_in_two_windows_and_late_odds()
    {
        var t = new DateTime(2026, 1, 1);
        var leak = TimeSplit.Check(new[]
        {
            ("m1", t, "train", t.AddHours(-2), t),
            ("m1", t.AddDays(40), "test", t.AddDays(40).AddHours(-2), t.AddDays(40))
        });
        Assert.Equal("match-leak", leak);

        var late = TimeSplit.Check(new[]
        {
            ("m2", t, "train", t.AddMinutes(1), t)
        });
        Assert.Equal("odds-after-kickoff", late);
    }

    [Fact]
    public void Score_metrics_refuse_a_short_sample()
    {
        var d = FullSupportDixonColes.Create(1.2, 1.0, 0.010);
        var one = DistributionScore.One(d, 1, 0);
        Assert.Equal("ok", one.Status);
        Assert.True(one.LogLoss > 0);
        Assert.True(one.Brier > 0);
        var batch = DistributionScore.Mean(new[] { d }, new[] { (1, 0) });
        Assert.Equal("insufficient-matches", batch.Status);
        Assert.Equal("insufficient-matches", DistributionScore.Reliability(new[] { (0.2, 1) }));
    }

    [Fact]
    public void Page_ev_stays_on_the_baseline()
    {
        var pick = new MilkPick { Ev = 0.20m, ModelEv = 0.20m, BaselineEv = 0.05m };
        Assert.Equal(0.05m, MilkBoardProjection.PageEv(pick));
        Assert.Equal(0.20m, MilkBoardProjection.PageModelEv(pick));
        Assert.Null(MilkBoardProjection.PageCalibratedEv(pick));
    }

    [Fact]
    public void Sources_binary_does_not_name_the_candidate_entry()
    {
        var path = typeof(MilkPickEngine).Assembly.Location;
        var bytes = File.ReadAllBytes(path);
        var needle = System.Text.Encoding.UTF8.GetBytes("CandidateTickets");
        Assert.False(Contains(bytes, needle));
    }

    static bool Contains(byte[] hay, byte[] needle)
    {
        for (var i = 0; i <= hay.Length - needle.Length; i++)
        {
            var hit = true;
            for (var j = 0; j < needle.Length; j++)
                if (hay[i + j] != needle[j]) { hit = false; break; }
            if (hit) return true;
        }
        return false;
    }

    [Fact]
    public void Missing_archive_blocks_the_outer_test()
    {
        Assert.Equal("blocked:archive-missing", CandidateArchive.Evaluate(null));
        var dir = Path.Combine(Path.GetTempPath(), "aifootball-archive-" + Guid.NewGuid().ToString("N"));
        var d = FullSupportDixonColes.Create(1.2, 1.0, 0.010);
        var path = CandidateArchive.WriteRetrospective(dir, new ArchiveRow
        {
            RunId = "retro",
            MatchId = 1,
            LambdaHome = 1.2,
            LambdaAway = 1.0,
            Rho = 0.010,
            Temperature = 1,
            Home = 1,
            Away = 0,
            StoredProbability = d.Prob(1, 0),
            ModelVersion = "v2",
            CodeHash = "abc",
            AliasHash = "def",
            SampleRef = "fixture"
        });
        Assert.Equal("retrospective:not-a-pass", CandidateArchive.Evaluate(path));
        Assert.Equal("recomputed", CandidateArchive.Recompute(path));
        var text = File.ReadAllText(path);
        text = text.Replace("lambdaHome=1.20000000", "lambdaHome=1.20000001", StringComparison.Ordinal);
        File.WriteAllText(path, text);
        Assert.Equal("blocked:identity-mismatch", CandidateArchive.Recompute(path));
        Assert.Contains("READ UNCOMMITTED", CandidateProtocol.QueryTemplate);
        Assert.Equal(400, CandidateProtocol.MinTestMatches);
        Assert.Equal(400, CandidateProtocol.MinCalibrationMatches);
        Assert.Contains("|minTest=400", CandidateVersion.Describe(), StringComparison.Ordinal);
        Assert.Contains("|minCal=400", CandidateVersion.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Predeclared_unseen_write_passes_evaluate_and_guards_still_refuse()
    {
        Assert.Equal("", CandidateProtocol.BoundEvidenceId);
        Assert.False(CandidateTickets.Enabled);
        var dir = Path.Combine(Path.GetTempPath(), "aifootball-predecl-" + Guid.NewGuid().ToString("N"));
        var d = FullSupportDixonColes.Create(1.2, 1.0, 0.010);
        var row = PredeclaredRow(d, matchId: 101, evidenceId: "unseen-fixture-A");

        var path = CandidateArchive.WritePredeclaredUnseen(dir, row);
        Assert.Equal("predeclared-unseen:ok", CandidateArchive.Evaluate(path));
        var text = File.ReadAllText(path);
        Assert.Contains("kind=predeclared-unseen", text, StringComparison.Ordinal);
        Assert.Contains("home=-", text, StringComparison.Ordinal);
        Assert.Contains("away=-", text, StringComparison.Ordinal);
        Assert.Equal("predeclared-unseen:awaiting-result", CandidateArchive.Recompute(path));
        Assert.StartsWith("train-roster:ok|", CandidateArchive.WriteTrainRoster(dir, Array.Empty<TrainRosterRow>()), StringComparison.Ordinal);
        // 空名册可写入，但不代表校准完成。
        Assert.Equal("result:attached", CandidateArchive.AttachResult(dir, 101, 1, 0, "2026-11-01 20:00:00"));
        Assert.Equal("blocked:results-duplicate", CandidateArchive.AttachResult(dir, 101, 1, 0, "2026-11-01 20:00:00"));
        Assert.Equal("blocked:results-conflict|101", CandidateArchive.AttachResult(dir, 101, 2, 0, "2026-11-01 20:00:00"));
        Assert.Equal("blocked:declare-missing", CandidateArchive.AttachResult(dir, 999, 1, 0, "2026-11-01 20:00:00"));

        var retroRow = CloneRow(PredeclaredRow(d, matchId: 102, evidenceId: "retrospective-not-unseen"),
            runId: "retro-run", evidenceId: "retrospective-not-unseen");
        var retro = CandidateArchive.WriteRetrospective(dir, retroRow);
        Assert.Equal("retrospective:not-a-pass", CandidateArchive.Evaluate(retro));

        Assert.Equal("blocked:archive-missing", CandidateArchive.Evaluate(Path.Combine(dir, "no-such.txt")));
        File.WriteAllText(Path.Combine(dir, "empty.txt"), "hello");
        Assert.Equal("blocked:archive-incomplete", CandidateArchive.Evaluate(Path.Combine(dir, "empty.txt")));

        var badRule = CandidateArchive.FormatLine(
            CloneRow(row, ruleVersion: "wrong-rule"), CalibratorBinding.KindPredeclaredUnseen);
        var badRulePath = Path.Combine(dir, "bad-rule.txt");
        File.WriteAllText(badRulePath, badRule + Environment.NewLine);
        Assert.Equal("blocked:rule-version-mismatch", CandidateArchive.Evaluate(badRulePath));

        var badId = CandidateArchive.FormatLine(
            CloneRow(row, evidenceId: "retrospective-not-unseen"), CalibratorBinding.KindPredeclaredUnseen);
        var badIdPath = Path.Combine(dir, "bad-id.txt");
        File.WriteAllText(badIdPath, badId + Environment.NewLine);
        Assert.Equal("blocked:evidence-id-unqualified", CandidateArchive.Evaluate(badIdPath));

        var withResult = text.Replace("home=-", "home=1", StringComparison.Ordinal)
            .Replace("away=-", "away=0", StringComparison.Ordinal);
        // identity 仍旧，先应因赛果字段被拒；重算 identity 后仍拒 declare-carries-result
        var carriesPath = Path.Combine(dir, "carries-result.txt");
        File.WriteAllText(carriesPath, withResult);
        Assert.Equal("blocked:declare-carries-result", CandidateArchive.Evaluate(carriesPath));

        var tampered = text.Replace("lambdaHome=1.20000000", "lambdaHome=1.20000001", StringComparison.Ordinal);
        var tamperedPath = Path.Combine(dir, "tampered.txt");
        File.WriteAllText(tamperedPath, tampered);
        Assert.Equal("blocked:identity-mismatch", CandidateArchive.Evaluate(tamperedPath));

        Assert.Throws<InvalidDataException>(() =>
            CandidateArchive.WritePredeclaredUnseen(dir, CloneRow(row, ruleVersion: "other")));
        Assert.Throws<InvalidDataException>(() =>
            CandidateArchive.WritePredeclaredUnseen(dir, CloneRow(row, evidenceId: "seen-452")));
        Assert.Throws<InvalidDataException>(() =>
            CandidateArchive.WritePredeclaredUnseen(dir, CloneRow(row, oddsSnapshot: "")));
        Assert.Throws<InvalidDataException>(() =>
            CandidateArchive.WritePredeclaredUnseen(dir, CloneRow(row, oddsCapturedAt: "2026-11-01 19:00:00")));
        Assert.Throws<InvalidDataException>(() =>
            CandidateArchive.WritePredeclaredUnseen(dir, CloneRow(row, trainCutoff: "2026-11-01 12:30:00")));
    }

    [Fact]
    public void Predeclared_window_dedup_400_rejects_padding_time_and_retro_masks()
    {
        Assert.Equal(400, CandidateProtocol.MinTestMatches);
        Assert.Equal(400, CandidateProtocol.MinCalibrationMatches);
        var shortDir = Path.Combine(Path.GetTempPath(), "aifootball-win-short-" + Guid.NewGuid().ToString("N"));
        var padDir = Path.Combine(Path.GetTempPath(), "aifootball-win-pad-" + Guid.NewGuid().ToString("N"));
        var passDir = Path.Combine(Path.GetTempPath(), "aifootball-win-pass-" + Guid.NewGuid().ToString("N"));
        var leakDir = Path.Combine(Path.GetTempPath(), "aifootball-win-leak-" + Guid.NewGuid().ToString("N"));
        var d = FullSupportDixonColes.Create(1.2, 1.0, 0.010);

        SeedDeclareOnly(shortDir, d, "unseen-window-399", count: 399, trainIds: new long[] { 90001 });
        Assert.Equal("blocked:insufficient-matches|399", CandidateArchive.EvaluateWindow(shortDir));
        Assert.False(CandidateArchive.TryBuildEvidence(shortDir, out _, out var shortStatus));
        Assert.Equal("blocked:insufficient-matches|399", shortStatus);

        // 400 行但只有 50 个独立 matchId：不得充数。
        Assert.StartsWith("train-roster:ok|", CandidateArchive.WriteTrainRoster(padDir, Array.Empty<TrainRosterRow>()), StringComparison.Ordinal);
        for (var i = 0; i < 400; i++)
            CandidateArchive.WritePredeclaredUnseen(padDir, PredeclaredRow(d, matchId: (i % 50) + 1, evidenceId: "unseen-pad-50"));
        Assert.Equal("blocked:insufficient-matches|50", CandidateArchive.EvaluateWindow(padDir));

        // 训练名册与验证声明交叉 → 拒绝（不只靠 trainCutoff 字段）。
        Assert.StartsWith("train-roster:ok|", CandidateArchive.WriteTrainRoster(leakDir, new[] { TrainRow(1), TrainRow(2) }), StringComparison.Ordinal);
        CandidateArchive.WritePredeclaredUnseen(leakDir, PredeclaredRow(d, matchId: 1, evidenceId: "unseen-leak"));
        Assert.Equal("blocked:train-test-match-leak", CandidateArchive.EvaluateWindow(leakDir));

        // 训练三列同 cutoff 代填 → 拒绝。
        Assert.Equal("blocked:train-times-placeholder", CandidateArchive.WriteTrainRoster(
            Path.Combine(Path.GetTempPath(), "aifootball-ph-" + Guid.NewGuid().ToString("N")),
            new[] { new TrainRosterRow { MatchId = 1, At = "2026-10-01 00:00:00", OddsAt = "2026-10-01 00:00:00", Kickoff = "2026-10-01 00:00:00" } }));

        SeedFullWindow(passDir, d, "unseen-window-400", count: 400, trainIds: new long[] { 90001, 90002 });
        var pass = CandidateArchive.EvaluateWindow(passDir);
        Assert.StartsWith("predeclared-unseen:pass|", pass, StringComparison.Ordinal);
        Assert.Contains("|matches=400|", pass, StringComparison.Ordinal);
        Assert.Contains("|trainRosterHash=", pass, StringComparison.Ordinal);
        Assert.Contains("|evidenceId=unseen-window-400|", pass, StringComparison.Ordinal);
        Assert.Contains("|rule=" + CandidateProtocol.RuleVersion, pass, StringComparison.Ordinal);
        Assert.StartsWith("results:ok|", CandidateArchive.EvaluateResults(passDir), StringComparison.Ordinal);
        Assert.True(CandidateArchive.TryBuildEvidence(passDir, out var evidence, out var built));
        Assert.StartsWith("predeclared-unseen:pass|", built, StringComparison.Ordinal);
        Assert.Contains("|resultsHash=", built, StringComparison.Ordinal);
        Assert.Contains("|trainRosterHash=", built, StringComparison.Ordinal);
        Assert.Equal("unseen-window-400", evidence.EvidenceId);
        Assert.Equal(400, evidence.Matches);
        Assert.Equal(400, evidence.ResultsAttached);
        Assert.Equal(2, evidence.TrainRosterCount);
        Assert.True(CalibratorBinding.IsSha256Hex(evidence.TrainRosterHash));
        Assert.Equal(CalibratorBinding.KindPredeclaredUnseen, evidence.Kind);
        Assert.Equal(CandidateProtocol.RuleVersion, evidence.PolicyVersion);
        Assert.True(CalibratorBinding.IsSha256Hex(evidence.FileHash));
        Assert.True(CalibratorBinding.IsSha256Hex(evidence.ResultsHash));
        Assert.Equal(CandidateArchive.Hash(evidence.ModelVersion), evidence.ModelFingerprint, ignoreCase: true);
        Assert.Equal(CandidateArchive.RecomputeWindowFileHash(passDir), evidence.FileHash, ignoreCase: true);
        Assert.Equal(CandidateArchive.RecomputeTrainRosterHash(passDir), evidence.TrainRosterHash, ignoreCase: true);
        Assert.Equal("evidence:window-ok", CandidateArchive.VerifyEvidenceAgainstWindow(evidence, passDir));
        Assert.Equal("predeclared-window-A", evidence.SampleWindow);

        var fit = TemperatureCalibrator.Fit(
            Enumerable.Repeat(d, 400).ToArray(),
            Enumerable.Repeat((1, 0), 400).ToArray(),
            CandidateProtocol.AlphaInnerDefault);
        // 生产 BoundEvidenceId 仍空：生产 Bind 拒；机制走 BindVerified / BindPredeclaredForTest。
        Assert.Equal("bound-evidence-id-empty", CalibratorBinding.Bind(fit, evidence, passDir).Status);
        Assert.False(CandidateTickets.Enabled);

        var pollute = CloneRow(
            PredeclaredRow(d, matchId: 9999, evidenceId: "retrospective-not-unseen"),
            runId: "pollute",
            evidenceId: "retrospective-not-unseen",
            ruleVersion: DecisionDelivery.ApprovedRule);
        CandidateArchive.WriteRetrospective(passDir, pollute);
        Assert.Equal("retrospective:not-a-pass", CandidateArchive.EvaluateWindow(passDir));
    }

    [Fact]
    public void Bind_predeclared_rechecks_live_window_not_object_self_hash()
    {
        Assert.Equal("", CandidateProtocol.BoundEvidenceId);
        Assert.False(CandidateTickets.Enabled);
        var d = FullSupportDixonColes.Create(1.2, 1.0, 0.010);
        var fit = TemperatureCalibrator.Fit(
            Enumerable.Repeat(d, 400).ToArray(),
            Enumerable.Repeat((1, 0), 400).ToArray(),
            CandidateProtocol.AlphaInnerDefault);

        var dir = Path.Combine(Path.GetTempPath(), "aifootball-bind-" + Guid.NewGuid().ToString("N"));
        SeedFullWindow(dir, d, "unseen-bind-400", count: 400, trainIds: new long[] { 80001 });
        Assert.True(CandidateArchive.TryBuildEvidence(dir, out var evidence, out _));

        // 合法证据 + requiredEvidenceId：真正到达绑定通过（不得用 bound-evidence-id-empty 当通过依据）。
        var ok = CalibratorBinding.BindVerified(fit, evidence, "unseen-bind-400", dir);
        Assert.Equal("bound", ok.Status);
        Assert.Equal("unseen-bind-400", ok.EvidenceId);

        CalibratorEvidence CloneEv(CalibratorEvidence src, Action<Dictionary<string, object>>? mut = null)
        {
            var bag = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["EvidenceId"] = src.EvidenceId,
                ["Kind"] = src.Kind,
                ["ModelVersion"] = src.ModelVersion,
                ["CodeHash"] = src.CodeHash,
                ["PolicyVersion"] = src.PolicyVersion,
                ["SampleWindow"] = src.SampleWindow,
                ["FileHash"] = src.FileHash,
                ["Matches"] = src.Matches,
                ["ModelFingerprint"] = src.ModelFingerprint,
                ["CodeFingerprint"] = src.CodeFingerprint,
                ["PolicyFingerprint"] = src.PolicyFingerprint,
                ["ResultsHash"] = src.ResultsHash,
                ["ResultsAttached"] = src.ResultsAttached,
                ["TrainRosterHash"] = src.TrainRosterHash,
                ["TrainRosterCount"] = src.TrainRosterCount,
                ["WindowMetaHash"] = src.WindowMetaHash
            };
            mut?.Invoke(bag);
            return new CalibratorEvidence
            {
                EvidenceId = (string)bag["EvidenceId"],
                Kind = (string)bag["Kind"],
                ModelVersion = (string)bag["ModelVersion"],
                CodeHash = (string)bag["CodeHash"],
                PolicyVersion = (string)bag["PolicyVersion"],
                SampleWindow = (string)bag["SampleWindow"],
                FileHash = (string)bag["FileHash"],
                Matches = (int)bag["Matches"],
                ModelFingerprint = (string)bag["ModelFingerprint"],
                CodeFingerprint = (string)bag["CodeFingerprint"],
                PolicyFingerprint = (string)bag["PolicyFingerprint"],
                ResultsHash = (string)bag["ResultsHash"],
                ResultsAttached = (int)bag["ResultsAttached"],
                TrainRosterHash = (string)bag["TrainRosterHash"],
                TrainRosterCount = (int)bag["TrainRosterCount"],
                WindowMetaHash = (string)bag["WindowMetaHash"]
            };
        }

        Assert.Equal("evidence-filehash-mismatch",
            CalibratorBinding.BindVerified(fit, CloneEv(evidence, b => b["FileHash"] = new string('A', 64)), "unseen-bind-400", dir).Status);

        Assert.Equal("evidence-policy-mismatch",
            CalibratorBinding.BindVerified(fit, CloneEv(evidence, b => b["PolicyVersion"] = "wrong-rule"), "unseen-bind-400", dir).Status);

        Assert.Equal("evidence-fingerprint-mismatch",
            CalibratorBinding.BindVerified(fit, CloneEv(evidence, b => b["ModelFingerprint"] = new string('0', 64)), "unseen-bind-400", dir).Status);

        // 缺失 archive
        var missingArchive = Path.Combine(Path.GetTempPath(), "aifootball-miss-arch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(missingArchive);
        Assert.Equal("blocked:archive-missing",
            CalibratorBinding.BindVerified(fit, evidence, "unseen-bind-400", missingArchive).Status);

        // 缺失训练名册
        var missTrain = Path.Combine(Path.GetTempPath(), "aifootball-miss-train-" + Guid.NewGuid().ToString("N"));
        SeedFullWindow(missTrain, d, "unseen-miss-train", count: 400, trainIds: new long[] { 81001 });
        Assert.True(CandidateArchive.TryBuildEvidence(missTrain, out var evMissTrain, out _));
        File.Delete(Path.Combine(missTrain, CandidateArchive.TrainRosterFileName));
        Assert.Equal("blocked:train-roster-missing",
            CalibratorBinding.BindVerified(fit, evMissTrain, "unseen-miss-train", missTrain).Status);

        // 建证后删 results.log
        var delRes = Path.Combine(Path.GetTempPath(), "aifootball-del-res-" + Guid.NewGuid().ToString("N"));
        SeedFullWindow(delRes, d, "unseen-del-res", count: 400, trainIds: Array.Empty<long>());
        Assert.True(CandidateArchive.TryBuildEvidence(delRes, out var evDelRes, out _));
        File.Delete(Path.Combine(delRes, CandidateArchive.ResultsFileName));
        Assert.Equal("blocked:results-missing",
            CalibratorBinding.BindVerified(fit, evDelRes, "unseen-del-res", delRes).Status);

        // 建证后改比分
        var chgScore = Path.Combine(Path.GetTempPath(), "aifootball-chg-score-" + Guid.NewGuid().ToString("N"));
        SeedFullWindow(chgScore, d, "unseen-chg-score", count: 400, trainIds: Array.Empty<long>());
        Assert.True(CandidateArchive.TryBuildEvidence(chgScore, out var evScore, out _));
        var resPath = Path.Combine(chgScore, CandidateArchive.ResultsFileName);
        var resLines = File.ReadAllLines(resPath);
        resLines[0] = resLines[0].Replace("home=1", "home=3", StringComparison.Ordinal);
        File.WriteAllLines(resPath, resLines);
        Assert.Equal("evidence-results-hash-mismatch",
            CalibratorBinding.BindVerified(fit, evScore, "unseen-chg-score", chgScore).Status);

        // 建证后追加冲突赛果
        var conflict = Path.Combine(Path.GetTempPath(), "aifootball-conflict-" + Guid.NewGuid().ToString("N"));
        SeedFullWindow(conflict, d, "unseen-conflict", count: 400, trainIds: Array.Empty<long>());
        Assert.True(CandidateArchive.TryBuildEvidence(conflict, out var evConflict, out _));
        File.AppendAllText(Path.Combine(conflict, CandidateArchive.ResultsFileName),
            "matchId=1\thome=9\taway=9\tsettledAt=2026-11-01 20:00:00" + Environment.NewLine);
        Assert.StartsWith("blocked:results-conflict|",
            CalibratorBinding.BindVerified(fit, evConflict, "unseen-conflict", conflict).Status, StringComparison.Ordinal);

        // 建证后追加重复赛果
        var dup = Path.Combine(Path.GetTempPath(), "aifootball-dup-" + Guid.NewGuid().ToString("N"));
        SeedFullWindow(dup, d, "unseen-dup", count: 400, trainIds: Array.Empty<long>());
        Assert.True(CandidateArchive.TryBuildEvidence(dup, out var evDup, out _));
        File.AppendAllText(Path.Combine(dup, CandidateArchive.ResultsFileName),
            "matchId=1\thome=1\taway=0\tsettledAt=2026-11-01 20:00:00" + Environment.NewLine);
        Assert.Equal("blocked:results-duplicate",
            CalibratorBinding.BindVerified(fit, evDup, "unseen-dup", dup).Status);

        // 建证后改训练名册（增不重叠 ID）
        var chgTrain = Path.Combine(Path.GetTempPath(), "aifootball-chg-train-" + Guid.NewGuid().ToString("N"));
        SeedFullWindow(chgTrain, d, "unseen-chg-train", count: 400, trainIds: new long[] { 82001 });
        Assert.True(CandidateArchive.TryBuildEvidence(chgTrain, out var evTrain, out _));
        Assert.StartsWith("train-roster:ok|", CandidateArchive.WriteTrainRoster(chgTrain, new[] { TrainRow(82001), TrainRow(82002) }), StringComparison.Ordinal);
        Assert.Equal("evidence-train-roster-hash-mismatch",
            CalibratorBinding.BindVerified(fit, evTrain, "unseen-chg-train", chgTrain).Status);

        // 建证后改 archive
        var chgArch = Path.Combine(Path.GetTempPath(), "aifootball-chg-arch-" + Guid.NewGuid().ToString("N"));
        SeedFullWindow(chgArch, d, "unseen-chg-arch", count: 400, trainIds: Array.Empty<long>());
        Assert.True(CandidateArchive.TryBuildEvidence(chgArch, out var evArch, out _));
        CandidateArchive.WritePredeclaredUnseen(chgArch, PredeclaredRow(d, matchId: 5000, evidenceId: "unseen-chg-arch"));
        Assert.Equal("result:attached", CandidateArchive.AttachResult(chgArch, 5000, 1, 0, "2026-11-01 20:00:00"));
        Assert.Equal("evidence-filehash-mismatch",
            CalibratorBinding.BindVerified(fit, evArch, "unseen-chg-arch", chgArch).Status);
    }

    [Fact]
    public void Predeclared_time_split_and_version_mismatch_still_blocked()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aifootball-time-" + Guid.NewGuid().ToString("N"));
        var d = FullSupportDixonColes.Create(1.2, 1.0, 0.010);
        var row = PredeclaredRow(d, matchId: 7, evidenceId: "unseen-time-A");

        var postOdds = CandidateArchive.FormatLine(
            CloneRow(row, oddsCapturedAt: "2026-11-01 19:30:00"), CalibratorBinding.KindPredeclaredUnseen);
        var postPath = Path.Combine(dir, "post-odds.txt");
        Directory.CreateDirectory(dir);
        File.WriteAllText(postPath, postOdds + Environment.NewLine);
        Assert.StartsWith("blocked:time-split|", CandidateArchive.Evaluate(postPath), StringComparison.Ordinal);

        var lateDeclare = CandidateArchive.FormatLine(
            CloneRow(row, declaredAt: "2026-11-01 18:30:00"), CalibratorBinding.KindPredeclaredUnseen);
        File.WriteAllText(Path.Combine(dir, "late-declare.txt"), lateDeclare + Environment.NewLine);
        Assert.Equal("blocked:declare-after-kickoff", CandidateArchive.Evaluate(Path.Combine(dir, "late-declare.txt")));

        var leak = CandidateArchive.FormatLine(
            CloneRow(row, trainCutoff: "2026-11-01 12:00:00"), CalibratorBinding.KindPredeclaredUnseen);
        File.WriteAllText(Path.Combine(dir, "train-leak.txt"), leak + Environment.NewLine);
        Assert.Equal("blocked:train-cutoff-leak", CandidateArchive.Evaluate(Path.Combine(dir, "train-leak.txt")));

        var badFp = CandidateArchive.FormatLine(row, CalibratorBinding.KindPredeclaredUnseen)
            .Replace("modelHash=" + CandidateArchive.Hash(row.ModelVersion), "modelHash=" + new string('F', 64), StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(dir, "bad-fp.txt"), badFp + Environment.NewLine);
        var fpStatus = CandidateArchive.Evaluate(Path.Combine(dir, "bad-fp.txt"));
        Assert.True(fpStatus is "blocked:fingerprint-mismatch" or "blocked:identity-mismatch", fpStatus);
    }

    static TrainRosterRow TrainRow(long matchId, int dayOffset = 0) => new()
    {
        MatchId = matchId,
        // 真实逐场时间：OddsAt < Kickoff，且不得三列同值代填；均早于验证窗 TrainCutoff=2026-10-31。
        At = $"2026-10-{20 + dayOffset % 5:00} 12:00:00",
        OddsAt = $"2026-10-{20 + dayOffset % 5:00} 11:00:00",
        Kickoff = $"2026-10-{20 + dayOffset % 5:00} 18:00:00"
    };

    static void SeedDeclareOnly(string dir, SupportDistribution d, string evidenceId, int count, IEnumerable<long> trainIds)
    {
        var rows = trainIds.Select((id, i) => TrainRow(id, i)).ToArray();
        var roster = CandidateArchive.WriteTrainRoster(dir, rows);
        Assert.StartsWith("train-roster:ok|", roster, StringComparison.Ordinal);
        for (var i = 1; i <= count; i++)
            CandidateArchive.WritePredeclaredUnseen(dir, PredeclaredRow(d, matchId: i, evidenceId: evidenceId));
    }

    static void SeedFullWindow(string dir, SupportDistribution d, string evidenceId, int count, IEnumerable<long> trainIds)
    {
        SeedDeclareOnly(dir, d, evidenceId, count, trainIds);
        for (var i = 1; i <= count; i++)
            Assert.Equal("result:attached", CandidateArchive.AttachResult(dir, i, 1, 0, "2026-11-01 20:00:00"));
    }

    static ArchiveRow PredeclaredRow(SupportDistribution d, long matchId, string evidenceId) => new()
    {
        RunId = "predecl-run",
        MatchId = matchId,
        LambdaHome = 1.2,
        LambdaAway = 1.0,
        Rho = 0.010,
        Temperature = 1,
        Home = 1,
        Away = 0,
        StoredProbability = d.Prob(1, 0),
        ModelVersion = "model-predecl-v1",
        CodeHash = "code-predecl-v1",
        AliasHash = "alias-predecl",
        SampleRef = "predeclared-window-A",
        Play = "JQ",
        Option = "2",
        Odds = 4m,
        OddsSnapshot = "0=3.000;1=3.200;2=3.400;3=4.000;4=5.000;5=7.000;6=9.000;7=12.000",
        DeclaredAt = "2026-11-01 10:00:00",
        OddsCapturedAt = "2026-11-01 11:00:00",
        AsOf = "2026-11-01 12:00:00",
        Kickoff = "2026-11-01 18:00:00",
        TrainCutoff = "2026-10-31 00:00:00",
        EvidenceId = evidenceId,
        RuleVersion = CandidateProtocol.RuleVersion,
        Lane = "research",
        Decision = "observe",
        Reason = "predeclared-fixture"
    };

    static ArchiveRow CloneRow(
        ArchiveRow row,
        string? runId = null,
        string? evidenceId = null,
        string? ruleVersion = null,
        string? oddsSnapshot = null,
        string? declaredAt = null,
        string? oddsCapturedAt = null,
        string? trainCutoff = null) => new()
    {
        RunId = runId ?? row.RunId,
        MatchId = row.MatchId,
        LambdaHome = row.LambdaHome,
        LambdaAway = row.LambdaAway,
        Rho = row.Rho,
        Temperature = row.Temperature,
        Home = row.Home,
        Away = row.Away,
        StoredProbability = row.StoredProbability,
        ModelVersion = row.ModelVersion,
        CodeHash = row.CodeHash,
        AliasHash = row.AliasHash,
        SampleRef = row.SampleRef,
        Play = row.Play,
        Option = row.Option,
        Odds = row.Odds,
        HandicapText = row.HandicapText,
        Handicap = row.Handicap,
        AsOf = row.AsOf,
        DeclaredAt = declaredAt ?? row.DeclaredAt,
        OddsCapturedAt = oddsCapturedAt ?? row.OddsCapturedAt,
        Kickoff = row.Kickoff,
        OddsSnapshot = oddsSnapshot ?? row.OddsSnapshot,
        ManifestHash = row.ManifestHash,
        TrainCutoff = trainCutoff ?? row.TrainCutoff,
        Lane = row.Lane,
        Decision = row.Decision,
        Reason = row.Reason,
        EvidenceId = evidenceId ?? row.EvidenceId,
        RuleVersion = ruleVersion ?? row.RuleVersion,
        RiskState = row.RiskState
    };

    [Fact]
    public void Test_match_count_boundary_rejects_399_and_accepts_count_at_400()
    {
        var d = FullSupportDixonColes.Create(1.2, 1.0, 0.010);
        Assert.Equal("ok", d.Status);
        var shortPredicted = Enumerable.Repeat(d, 399).ToArray();
        var shortActual = Enumerable.Repeat((1, 0), 399).ToArray();
        Assert.Equal("insufficient-matches", DistributionScore.Mean(shortPredicted, shortActual).Status);
        Assert.Equal("insufficient-matches", DistributionScore.Reliability(Enumerable.Repeat((0.55, 1), 399).ToArray()));

        var enoughPredicted = Enumerable.Repeat(d, 400).ToArray();
        var enoughActual = Enumerable.Repeat((1, 0), 400).ToArray();
        var batch = DistributionScore.Mean(enoughPredicted, enoughActual);
        Assert.Equal("ok", batch.Status);
        Assert.True(double.IsFinite(batch.LogLoss));
        var reliability = DistributionScore.Reliability(Enumerable.Repeat((0.55, 1), 400).ToArray());
        Assert.StartsWith("ok|", reliability, StringComparison.Ordinal);

        var mismatched = DistributionScore.Mean(enoughPredicted, Enumerable.Repeat((1, 0), 399).ToArray());
        Assert.Equal("insufficient-matches", mismatched.Status);
        Assert.Equal("probability-invalid", DistributionScore.Reliability(Enumerable.Repeat((1.5, 1), 400).ToArray()));
        Assert.Equal("label-invalid", DistributionScore.Reliability(Enumerable.Repeat((0.55, 2), 400).ToArray()));
    }

    [Fact]
    public void Calibration_match_count_boundary_rejects_399_and_accepts_count_at_400()
    {
        var d = FullSupportDixonColes.Create(1.2, 1.0, 0.010);
        Assert.Equal(400, CandidateProtocol.MinCalibrationMatches);
        var shortFit = TemperatureCalibrator.Fit(
            Enumerable.Repeat(d, 399).ToArray(),
            Enumerable.Repeat((1, 0), 399).ToArray(),
            CandidateProtocol.AlphaInnerDefault);
        Assert.Equal("insufficient-matches", shortFit.Status);
        Assert.False(shortFit.Ok);

        var enoughFit = TemperatureCalibrator.Fit(
            Enumerable.Repeat(d, 400).ToArray(),
            Enumerable.Repeat((1, 0), 400).ToArray(),
            CandidateProtocol.AlphaInnerDefault);
        Assert.Equal("computed", enoughFit.Status);
        Assert.Equal(400, enoughFit.Matches);
        Assert.Equal("", enoughFit.EvidenceId);
        Assert.True(double.IsFinite(enoughFit.Temperature));
        Assert.True(double.IsFinite(enoughFit.Objective));
    }

    [Fact]
    public void Calibrator_binding_rejects_empty_protocol_id_and_seen_evidence_fixture_only_for_mechanism()
    {
        Assert.Equal("", CandidateProtocol.BoundEvidenceId);
        Assert.False(CandidateTickets.Enabled);
        var fit = AcceptedFit();
        Assert.Equal("computed", fit.Status);

        var empty = CalibratorBinding.Bind(fit, new CalibratorEvidence
        {
            EvidenceId = "any",
            Kind = CalibratorBinding.KindPredeclaredUnseen,
            ModelVersion = "m",
            CodeHash = "c",
            PolicyVersion = CandidateProtocol.RuleVersion,
            SampleWindow = "2026-unseen",
            FileHash = new string('A', 64)
        });
        Assert.Equal("bound-evidence-id-empty", empty.Status);

        var retrospective = CalibratorBinding.BindForTest(fit, new CalibratorEvidence
        {
            EvidenceId = "fixture-1",
            Kind = "retrospective",
            ModelVersion = "m",
            CodeHash = "c",
            PolicyVersion = CandidateProtocol.RuleVersion,
            SampleWindow = "seen-452",
            FileHash = new string('B', 64)
        }, "fixture-1");
        Assert.Equal("evidence-seen-or-retrospective", retrospective.Status);

        var seen = CalibratorBinding.BindForTest(fit, new CalibratorEvidence
        {
            EvidenceId = "fixture-1",
            Kind = "seen-backtest",
            ModelVersion = "m",
            CodeHash = "c",
            PolicyVersion = CandidateProtocol.RuleVersion,
            SampleWindow = "seen-452",
            FileHash = new string('B', 64)
        }, "fixture-1");
        Assert.Equal("evidence-seen-or-retrospective", seen.Status);

        var mismatch = CalibratorBinding.BindForTest(fit, new CalibratorEvidence
        {
            EvidenceId = "other",
            Kind = CalibratorBinding.KindFixture,
            ModelVersion = "m",
            CodeHash = "c",
            PolicyVersion = CandidateProtocol.RuleVersion,
            SampleWindow = "fixture-window",
            FileHash = new string('C', 64)
        }, "fixture-1");
        Assert.Equal("evidence-id-mismatch", mismatch.Status);

        var policy = CalibratorBinding.BindForTest(fit, new CalibratorEvidence
        {
            EvidenceId = "fixture-1",
            Kind = CalibratorBinding.KindFixture,
            ModelVersion = "m",
            CodeHash = "c",
            PolicyVersion = "wrong-rule",
            SampleWindow = "fixture-window",
            FileHash = new string('C', 64)
        }, "fixture-1");
        Assert.Equal("evidence-policy-mismatch", policy.Status);

        var ok = CalibratorBinding.BindForTest(fit, new CalibratorEvidence
        {
            EvidenceId = "fixture-1",
            Kind = CalibratorBinding.KindFixture,
            ModelVersion = "m",
            CodeHash = "c",
            PolicyVersion = CandidateProtocol.RuleVersion,
            SampleWindow = "fixture-window",
            FileHash = new string('C', 64)
        }, "fixture-1");
        Assert.Equal("bound", ok.Status);
        Assert.Equal("fixture-1", ok.EvidenceId);
        Assert.Equal("m", ok.ModelVersion);
        Assert.Equal("c", ok.CodeHash);
        // 夹具绑定仅验证机制，不是真实算法合格，也不能绕过 BoundEvidenceId 空值。
        var stillUnbound = CandidateTickets.DecideForTest(new CandidateRequest
        {
            Calibrator = ok,
            ModelVersion = "m",
            CodeHash = "c",
            Play = "BF",
            OptionCode = "1:0",
            Odds = 10m,
            Stake = 1,
            StakeLimit = 2,
            DayLimit = 5
        });
        Assert.Equal("calibrator-not-bound", stillUnbound.Status);
    }

    [Fact]
    public void Accept_database_v10_rejects_v9_and_requires_exact_name()
    {
        Assert.Equal("AiFootBall_Accept_20261121v10", AcceptDatabase.Proposed);
        Assert.True(AcceptDatabase.Forbidden("AiFootBall_Accept_20261120v9"));
        Assert.True(AcceptDatabase.Forbidden("AiFootBall"));
        Assert.False(AcceptDatabase.Forbidden(AcceptDatabase.Proposed));
        var previous = Environment.GetEnvironmentVariable("AIFOOTBALL_ACCEPT_AUTHORIZED");
        try
        {
            Environment.SetEnvironmentVariable("AIFOOTBALL_ACCEPT_AUTHORIZED", "AiFootBall_Accept_20261120v9");
            Assert.False(AcceptDatabase.Authorized("AiFootBall_Accept_20261120v9"));
            Environment.SetEnvironmentVariable("AIFOOTBALL_ACCEPT_AUTHORIZED", AcceptDatabase.Proposed);
            Assert.True(AcceptDatabase.Authorized(AcceptDatabase.Proposed));
            Assert.False(AcceptDatabase.Authorized("AiFootBall_Accept_20261121v10_extra"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AIFOOTBALL_ACCEPT_AUTHORIZED", previous);
        }
    }

    [Fact]
    public void Decision_archive_reselects_the_market_and_rejects_tampering()
    {
        var previousSeal = DecisionDelivery.EnforceDeliverySeal;
        DecisionDelivery.EnforceDeliverySeal = false;
        try
        {
        DecisionArchiveBody();
        }
        finally
        {
            DecisionDelivery.EnforceDeliverySeal = previousSeal;
        }
    }

    void DecisionArchiveBody()
    {
        var dist = FullSupportDixonColes.Create(1.35, 1.05, 0.010);
        var scaled = TemperatureCalibrator.Apply(dist, 1.1);
        var margin = PlayMargins.AggregateResult(scaled);
        var market = new (string Code, decimal Odds)[] { ("3", 8.000m), ("1", 9.000m), ("0", 8.000m) };
        string chosen = "";
        double chosenP = -1;
        decimal chosenOdds = 0;
        foreach (var (code, price) in market)
        {
            var index = code == "3" ? 0 : code == "1" ? 1 : 2;
            var p = margin.Values[index];
            if ((decimal)p * price - 1m <= 0m) continue;
            if (p > chosenP)
            {
                chosen = code;
                chosenP = p;
                chosenOdds = price;
            }
        }
        Assert.False(string.IsNullOrEmpty(chosen));
        var loser = market.First(x => x.Code != chosen && (decimal)margin.Values[x.Code == "3" ? 0 : x.Code == "1" ? 1 : 2] * x.Odds - 1m > 0m);
        var dir = Path.Combine(Path.GetTempPath(), "aifb-decision-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var kickoff = new DateTime(2026, 11, 19, 15, 0, 45);
        var cutoff = DecisionDelivery.TrainingCutoff(kickoff).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var manifestPath = Path.Combine(dir, "decision-manifest.txt");
        var manifestHash = DecisionManifest(dir);
        var row = new ArchiveRow
        {
            RunId = DecisionDelivery.DecisionRunId, MatchId = 42, LambdaHome = 1.35, LambdaAway = 1.05, Rho = 0.010,
            Temperature = 1.1, Home = 1, Away = 0, StoredProbability = chosenP, ModelVersion = DecisionDelivery.RuntimeModel(),
            CodeHash = BuildInfo.Version, AliasHash = "official-integer-handicap", SampleRef = "fixture",
            Play = "SPF", Option = chosen, Odds = chosenOdds, OddsSnapshot = "3=8.000;1=9.000;0=8.000",
            Handicap = 0m, AsOf = "2026-11-19 12:30:45", TrainCutoff = cutoff, Decision = "ticket",
            Reason = "highest-probability-among-ev-positive", EvidenceId = "retrospective-not-unseen",
            RuleVersion = "one-ticket-ev-positive-T1.1", RiskState = "not-reserved-candidate-off",
            ManifestHash = manifestHash
        };
        var path = CandidateArchive.WriteRetrospective(dir, row);
        var written = File.ReadAllText(path);
        Assert.Contains("2026-11-19 12:30:45", written, StringComparison.Ordinal);
        Assert.Contains("\t", written, StringComparison.Ordinal);
        Assert.Equal("integrity:ok", CandidateArchive.FileIntegrity(path));
        Assert.Equal("approval:runtime", CandidateArchive.VersionApproval(path, manifestPath));
        Assert.Equal("recomputed", CandidateArchive.Recompute(path, manifestPath));
        File.WriteAllText(path, written.Replace("3=8.000", "3=9.900", StringComparison.Ordinal));
        Assert.Equal("blocked:identity-mismatch", CandidateArchive.Recompute(path));
        Assert.Equal("approval:not-checked", CandidateArchive.VersionApproval(path));
        File.WriteAllText(path, written.Replace("modelVersion=" + DecisionDelivery.RuntimeModel(), "modelVersion=other-model", StringComparison.Ordinal));
        Assert.Equal("blocked:identity-mismatch", CandidateArchive.Recompute(path));
        var loserIndex = loser.Code == "3" ? 0 : loser.Code == "1" ? 1 : 2;
        var wrong = CandidateArchive.WriteRetrospective(dir, new ArchiveRow
        {
            RunId = row.RunId, MatchId = row.MatchId, LambdaHome = row.LambdaHome, LambdaAway = row.LambdaAway,
            Rho = row.Rho, Temperature = row.Temperature, Home = 1, Away = 0,
            StoredProbability = margin.Values[loserIndex], ModelVersion = row.ModelVersion, CodeHash = row.CodeHash,
            AliasHash = row.AliasHash, SampleRef = row.SampleRef, Play = "SPF", Option = loser.Code, Odds = loser.Odds,
            OddsSnapshot = row.OddsSnapshot, Handicap = 0m, AsOf = row.AsOf, TrainCutoff = row.TrainCutoff, Decision = "ticket",
            Reason = row.Reason, EvidenceId = row.EvidenceId, RuleVersion = row.RuleVersion, RiskState = row.RiskState,
            ManifestHash = manifestHash
        });
        Assert.Equal("blocked:selection-mismatch", CandidateArchive.Recompute(wrong, manifestPath));
        Assert.DoesNotContain("lane", CandidateArchive.CheckedFields.Split(','), StringComparer.Ordinal);
        Assert.DoesNotContain("risk", CandidateArchive.CheckedFields.Split(','), StringComparer.Ordinal);
        Assert.DoesNotContain("aliasHash", CandidateArchive.CheckedFields.Split(','), StringComparer.Ordinal);

        var missing = CandidateArchive.WriteRetrospective(dir, CopyDecision(row, manifestHash, snapshot: "3=8.000;1=9.000"));
        Assert.Equal("blocked:market-incomplete", CandidateArchive.Recompute(missing, manifestPath));
        var duplicate = CandidateArchive.WriteRetrospective(dir, CopyDecision(row, manifestHash, snapshot: "3=8.000;3=8.000;1=9.000;0=8.000"));
        Assert.Equal("blocked:market-duplicate", CandidateArchive.Recompute(duplicate, manifestPath));
        var unapproved = CandidateArchive.WriteRetrospective(dir, CopyDecision(row, manifestHash, model: "other-model"));
        Assert.Equal("blocked:version-not-approved", CandidateArchive.Recompute(unapproved, manifestPath));
        File.WriteAllText(path, written.TrimEnd('\r', '\n') + "\tverified=yes\n");
        Assert.Equal("blocked:unauthenticated-claim", CandidateArchive.Recompute(path));
        var doubled = written.Replace("play=SPF", "play=SPF\tplay=JQ", StringComparison.Ordinal);
        File.WriteAllText(path, doubled);
        Assert.Equal("blocked:duplicate-field", CandidateArchive.Recompute(path));

        var fields = CandidateArchive.Rewrite(ReadArchive(written)).Split('\t').Select(part =>
        {
            var eq = part.IndexOf('=');
            return (part[..eq], part[(eq + 1)..]);
        }).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
        fields["matchId"] = "43";
        var changedId = Path.Combine(dir, "changed-id.txt");
        File.WriteAllText(changedId, CandidateArchive.Rewrite(fields) + "\n");
        Assert.Equal("integrity:ok", CandidateArchive.FileIntegrity(changedId));
        Assert.Equal("blocked:source-row-mismatch", CandidateArchive.Recompute(changedId, manifestPath));
        fields["matchId"] = "42";
        fields["asOf"] = "2099-01-01 00:00:00";
        var future = Path.Combine(dir, "future-time.txt");
        File.WriteAllText(future, CandidateArchive.Rewrite(fields) + "\n");
        Assert.Equal("integrity:ok", CandidateArchive.FileIntegrity(future));
        Assert.Equal("blocked:future-time", CandidateArchive.Recompute(future, manifestPath));

        var manifest = File.ReadAllText(manifestPath);
        var forged = manifest.Replace(DecisionDelivery.HashFile(DecisionDelivery.LoadedCorePath), new string('A', 64), StringComparison.Ordinal);
        File.WriteAllText(manifestPath, forged);
        var map = ReadArchive(written);
        map["manifestHash"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(forged)));
        var forgedPath = Path.Combine(dir, "forged-manifest.txt");
        File.WriteAllText(forgedPath, CandidateArchive.Rewrite(map) + "\n");
        Assert.Equal("integrity:ok", CandidateArchive.FileIntegrity(forgedPath));
        Assert.Equal("blocked:version-not-approved", CandidateArchive.VersionApproval(forgedPath, manifestPath));
        File.WriteAllText(manifestPath, manifest);

        var renamedRule = ReadArchive(written);
        renamedRule["ruleVersion"] = "renamed-rule";
        renamedRule["ruleHash"] = CandidateArchive.Hash("renamed-rule");
        var renamedPath = Path.Combine(dir, "renamed-rule.txt");
        File.WriteAllText(renamedPath, CandidateArchive.Rewrite(renamedRule) + "\n");
        Assert.Equal("integrity:ok", CandidateArchive.FileIntegrity(renamedPath));
        Assert.Equal("blocked:rule-not-approved", CandidateArchive.Recompute(renamedPath, manifestPath));

        var renamedEvidence = ReadArchive(written);
        renamedEvidence["evidenceId"] = "unseen-validation";
        renamedEvidence["evidenceHash"] = CandidateArchive.Hash("unseen-validation");
        var evidencePath = Path.Combine(dir, "renamed-evidence.txt");
        File.WriteAllText(evidencePath, CandidateArchive.Rewrite(renamedEvidence) + "\n");
        Assert.Equal("blocked:evidence-not-approved", CandidateArchive.Recompute(evidencePath, manifestPath));

        var shifted = FullSupportDixonColes.Create(2.8, 0.7, 0.010);
        var shiftedScaled = TemperatureCalibrator.Apply(shifted, 1.1);
        var shiftedMargin = PlayMargins.AggregateResult(shiftedScaled);
        string shiftedOption = "";
        double shiftedP = -1;
        decimal shiftedOdds = 0;
        foreach (var (code, price) in market)
        {
            var index = code == "3" ? 0 : code == "1" ? 1 : 2;
            var p = shiftedMargin.Values[index];
            if ((decimal)p * price - 1m <= 0m) continue;
            if (p > shiftedP) { shiftedOption = code; shiftedP = p; shiftedOdds = price; }
        }
        var lambdaFields = ReadArchive(written);
        lambdaFields["lambdaHome"] = 2.8.ToString("0.00000000", System.Globalization.CultureInfo.InvariantCulture);
        lambdaFields["lambdaAway"] = 0.7.ToString("0.00000000", System.Globalization.CultureInfo.InvariantCulture);
        lambdaFields["option"] = shiftedOption;
        lambdaFields["odds"] = shiftedOdds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        lambdaFields["pFull"] = shiftedP.ToString("0.00000000", System.Globalization.CultureInfo.InvariantCulture);
        var lambdaPath = Path.Combine(dir, "shifted-lambda.txt");
        File.WriteAllText(lambdaPath, CandidateArchive.Rewrite(lambdaFields) + "\n");
        Assert.Equal("integrity:ok", CandidateArchive.FileIntegrity(lambdaPath));
        Assert.Equal("blocked:fit-snapshot-mismatch", CandidateArchive.Recompute(lambdaPath, manifestPath));

        var rhoFields = ReadArchive(File.ReadAllText(lambdaPath));
        rhoFields["rho"] = 0.02.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        var rhoPath = Path.Combine(dir, "shifted-rho.txt");
        File.WriteAllText(rhoPath, CandidateArchive.Rewrite(rhoFields) + "\n");
        Assert.Equal("blocked:fit-snapshot-mismatch", CandidateArchive.Recompute(rhoPath, manifestPath));

        var copy = Path.Combine(Path.GetTempPath(), "aifb-resigned-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(copy);
        foreach (var name in new[] { "books.tsv", "odds.tsv", "history.tsv", "fit-snapshots.tsv", "protocol.md" })
            File.Copy(Path.Combine(dir, name), Path.Combine(copy, name));
        Directory.CreateDirectory(Path.Combine(copy, "dll"));
        foreach (var dllName in Directory.GetFiles(Path.Combine(dir, "dll")))
            File.Copy(dllName, Path.Combine(copy, "dll", Path.GetFileName(dllName)));
        foreach (var name in new[] { "books.tsv", "odds.tsv", "history.tsv", "fit-snapshots.tsv" })
        {
            var text = File.ReadAllText(Path.Combine(copy, name)).Replace("42", "999999", StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(copy, name), text);
        }
        var resignedManifest = File.ReadAllText(manifestPath);
        foreach (var role in new[] { "data-books", "data-odds", "data-history", "fit-snapshots" })
        {
            var relative = role == "fit-snapshots" ? "fit-snapshots.tsv" : role.Replace("data-", "") + ".tsv";
            if (role == "data-books") relative = "books.tsv";
            if (role == "data-odds") relative = "odds.tsv";
            if (role == "data-history") relative = "history.tsv";
            var oldHash = DecisionDelivery.HashFile(Path.Combine(dir, relative));
            var newHash = DecisionDelivery.HashFile(Path.Combine(copy, relative));
            resignedManifest = resignedManifest.Replace(oldHash, newHash, StringComparison.Ordinal);
        }
        var resignedPath = Path.Combine(copy, "decision-manifest.txt");
        File.WriteAllText(resignedPath, resignedManifest);
        var resignedArchive = ReadArchive(written);
        resignedArchive["matchId"] = "999999";
        resignedArchive["manifestHash"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(resignedManifest)));
        var resignedFile = Path.Combine(copy, "resigned.txt");
        File.WriteAllText(resignedFile, CandidateArchive.Rewrite(resignedArchive) + "\n");
        Assert.Equal("integrity:ok", CandidateArchive.FileIntegrity(resignedFile));
        Assert.Equal("blocked:manifest-not-approved", CandidateArchive.Recompute(resignedFile, manifestPath));
        DecisionDelivery.EnforceDeliverySeal = true;
        Assert.Equal("blocked:sibling-not-approved", CandidateArchive.Recompute(resignedFile, resignedPath));
    }

    static Dictionary<string, string> ReadArchive(string text)
    {
        var line = text.Replace("\r", "", StringComparison.Ordinal).Split('\n')[0];
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in line.Split('\t'))
        {
            var eq = part.IndexOf('=');
            if (eq > 0) map[part[..eq]] = part[(eq + 1)..];
        }
        return map;
    }

    static ArchiveRow CopyDecision(ArchiveRow row, string manifestHash, string? snapshot = null, string? model = null) => new()
    {
        RunId = row.RunId, MatchId = row.MatchId, LambdaHome = row.LambdaHome, LambdaAway = row.LambdaAway,
        Rho = row.Rho, Temperature = row.Temperature, Home = row.Home, Away = row.Away,
        StoredProbability = row.StoredProbability, ModelVersion = model ?? row.ModelVersion, CodeHash = row.CodeHash,
        AliasHash = row.AliasHash, SampleRef = row.SampleRef, Play = row.Play, Option = row.Option, Odds = row.Odds,
        OddsSnapshot = snapshot ?? row.OddsSnapshot, Handicap = row.Handicap, AsOf = row.AsOf,
        TrainCutoff = row.TrainCutoff, Decision = "ticket",
        Reason = row.Reason, EvidenceId = row.EvidenceId, RuleVersion = row.RuleVersion, RiskState = row.RiskState,
        ManifestHash = manifestHash
    };

    static string DecisionManifest(string dir)
    {
        var dll = Path.Combine(dir, "dll");
        Directory.CreateDirectory(dll);
        CopyDll(DecisionDelivery.LoadedCorePath, Path.Combine(dll, "AiFootBall.Core.dll"));
        CopyDll(typeof(MilkPickEngine).Assembly.Location, Path.Combine(dll, "AiFootBall.Sources.dll"));
        CopyDll(typeof(AiFootBall.Data.RecommendationSchema).Assembly.Location, Path.Combine(dll, "AiFootBall.Data.dll"));
        var api = Path.Combine(AppContext.BaseDirectory, "AiFootBall.Api.dll");
        if (!File.Exists(api)) throw new FileNotFoundException("测试输出里没有实际的 Api.dll", api);
        CopyDll(api, Path.Combine(dll, "AiFootBall.Api.dll"));
        File.WriteAllText(Path.Combine(dir, "books.tsv"),
            "42\t2026-11-19 15:00:45\t1\t0\tSPF\t2026-11-19 12:30:45\t0\t\teligible\n");
        File.WriteAllText(Path.Combine(dir, "odds.tsv"), "42\tSPF\t3\t8.000\n42\tSPF\t1\t9.000\n42\tSPF\t0\t8.000\n");
        File.WriteAllText(Path.Combine(dir, "history.tsv"), "42\t德甲\t拜仁\t科隆\t2026-11-19 15:00:45\t1\t0\n");
        File.WriteAllText(Path.Combine(dir, "protocol.md"), "protocol-v8 retrospective\n");
        var cutoff = DecisionDelivery.TrainingCutoff(new DateTime(2026, 11, 19, 15, 0, 45))
            .ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(dir, "fit-snapshots.tsv"),
            "cutoff\tmatchId\tlambdaHome\tlambdaAway\trho\n" + cutoff + "\t42\t1.35000000\t1.05000000\t0.010\n");
        string Role(string role, string relative)
        {
            var hash = role == "model-parameters"
                ? DecisionDelivery.HashText(DecisionDelivery.RuntimeModel())
                : DecisionDelivery.HashFile(Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar)));
            var path = role == "model-parameters" ? "-" : relative;
            return "role=" + role + "=" + path + "=" + hash;
        }
        var body = string.Join('\n', new[]
        {
            "model=" + DecisionDelivery.RuntimeModel(),
            "code=" + BuildInfo.Version,
            "rule=one-ticket-ev-positive-T1.1",
            "evidence=retrospective-not-unseen",
            "temperature=1.10",
            "database=" + AcceptDatabase.Next,
            "checked=" + CandidateArchive.CheckedFields,
            Role("dll-core", "dll/AiFootBall.Core.dll"),
            Role("dll-sources", "dll/AiFootBall.Sources.dll"),
            Role("dll-data", "dll/AiFootBall.Data.dll"),
            Role("dll-api", "dll/AiFootBall.Api.dll"),
            Role("model-parameters", "-"),
            Role("data-books", "books.tsv"),
            Role("data-odds", "odds.tsv"),
            Role("data-history", "history.tsv"),
            Role("fit-snapshots", "fit-snapshots.tsv"),
            Role("protocol", "protocol.md")
        }) + "\n";
        File.WriteAllText(Path.Combine(dir, "decision-manifest.txt"), body);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body)));
    }

    static void CopyDll(string source, string dest) => File.Copy(source, dest, true);

    [Fact]
    public void Redirected_child_output_is_captured_instead_of_filling_the_pipe()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aifb-pipe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, "write.ps1");
        File.WriteAllText(script, "'x' * 200000");
        System.Diagnostics.Process Start() => new()
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo("powershell")
            {
                ArgumentList = { "-NoProfile", "-File", script },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        var stalled = Start();
        stalled.Start();
        Assert.False(stalled.WaitForExit(2000));
        stalled.Kill(entireProcessTree: true);
        var drained = Start();
        drained.Start();
        var output = drained.StandardOutput.ReadToEndAsync();
        var error = drained.StandardError.ReadToEndAsync();
        Assert.True(drained.WaitForExit(15000));
        Assert.True(output.GetAwaiter().GetResult().Length >= 200000);
        _ = error.GetAwaiter().GetResult();
    }

    static TemperatureFit AcceptedFit()
    {
        var d = FullSupportDixonColes.Create(1.2, 1.0, 0.010);
        var n = CandidateProtocol.MinCalibrationMatches;
        return TemperatureCalibrator.Fit(
            Enumerable.Repeat(d, n).ToArray(),
            Enumerable.Repeat((1, 0), n).ToArray(),
            CandidateProtocol.AlphaInnerDefault);
    }

    static SupportDistribution Peaked()
    {
        var d = FullSupportDixonColes.Create(0.4, 0.3, 0.010);
        var n = d.Stride;
        var cell = new double[n * n];
        for (var h = 0; h < n; h++)
        for (var a = 0; a < n; a++)
            cell[h * n + a] = d.Prob(h, a);
        cell[0] = 0.80;
        var rest = 0.20 / (cell.Length - 1);
        for (var i = 1; i < cell.Length; i++) cell[i] = rest;
        return SupportDistribution.Accepted(cell, n, 1, 0);
    }
}
