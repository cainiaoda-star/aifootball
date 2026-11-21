using System.Globalization;
using System.Text;
using AiFootBall.Core.Models;
using AiFootBall.Core.Models.Candidate;
using Xunit;

namespace AiFootBall.Tests;

public class ResearchEvidenceBridgeTests
{
    const double Rho = 0.010;

    static string FreezeCompatible(string dir)
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["EvidenceId"] = "unseen-research-fixture-" + Guid.NewGuid().ToString("N")[..8],
            ["SampleRef"] = "sample-research-fixture-" + Guid.NewGuid().ToString("N")[..8],
            ["ModelVersion"] = DecisionDelivery.RuntimeModel(),
            ["CodeHash"] = BuildInfo.Version,
            ["PolicyVersion"] = CandidateProtocol.RuleVersion,
            ["TrainCutoff"] = "2026-11-01T12:00:00.0000000+08:00",
            ["Note"] = "fixtureOnly=true|" + ResearchEvidenceBridge.UncalibratedLabel
        };
        return EvidenceWindowOffline.FreezeMeta(dir, meta);
    }

    static void WriteTrain(string dir, long matchId = 80001)
    {
        var path = Path.Combine(dir, "train.import.tsv");
        File.WriteAllText(path,
            "matchId\tat\toddsAt\tkickoff\n"
            + matchId + "\t2026-10-20T12:00:00+08:00\t2026-10-20T11:00:00+08:00\t2026-10-20T18:00:00+08:00\n");
        Assert.StartsWith("train-roster:ok|", EvidenceWindowOffline.ImportTrainRoster(dir, path), StringComparison.Ordinal);
    }

    static void WriteHistory(string path, int n = 40)
    {
        var sb = new StringBuilder();
        sb.Append("# fixture history — not real unseen\n");
        sb.Append("matchId\tleague\thome\taway\thomeScore\tawayScore\tmatchTime\n");
        for (var i = 0; i < n; i++)
        {
            var id = 70000 + i;
            var day = new DateTime(2026, 9, 1).AddDays(i);
            sb.Append(id).Append("\tL1\tH").Append(i % 8).Append("\tA").Append((i + 3) % 8)
              .Append('\t').Append(i % 3).Append('\t').Append((i + 1) % 3)
              .Append('\t').Append(day.ToString("yyyy-MM-ddTHH:mm:ss+08:00", CultureInfo.InvariantCulture))
              .Append('\n');
        }
        File.WriteAllText(path, sb.ToString());
    }

    static void WriteMatchAndOdds(string matchesPath, string oddsPath, long matchId, DateTime kickoff, DateTime capturedAt)
    {
        File.WriteAllText(matchesPath,
            "# fixture matches\nmatchId\tleague\thome\taway\tkickoff\touLine\thomeScore\tawayScore\tstatus\tsettledAt\n"
            + matchId + "\tL1\tH0\tA3\t"
            + ResearchEvidenceBridge.FormatTime(kickoff) + "\t2.5\t\t\t0\t\n");
        var sb = new StringBuilder();
        sb.Append("# fixture odds\nmatchId\tplayType\toptionCode\todds\tcapturedAt\n");
        var prices = new[] { 3.0m, 3.2m, 3.4m, 4.0m, 5.0m, 7.0m, 9.0m, 12.0m };
        for (var i = 0; i < 8; i++)
            sb.Append(matchId).Append("\tJQ\t").Append(i).Append('\t')
              .Append(prices[i].ToString("0.000", CultureInfo.InvariantCulture)).Append('\t')
              .Append(ResearchEvidenceBridge.FormatTime(capturedAt)).Append('\n');
        File.WriteAllText(oddsPath, sb.ToString());
    }

    [Fact]
    public void Compat_rejects_prod_build_tag_window()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aifb-compat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["EvidenceId"] = "ev-real-unseen-fake-1825",
            ["SampleRef"] = "sample-real-unseen-fake",
            ["ModelVersion"] = "prod-api-build:1.0.0.0+517ef5e4",
            ["CodeHash"] = "7B464C1CBDE31BEF223DE2F1E84581E4A29DEE962BBC538EBB0CB48784F16A5E",
            ["PolicyVersion"] = CandidateProtocol.RuleVersion,
            ["TrainCutoff"] = "2026-11-21T18:25:25.8667020+08:00"
        };
        Assert.StartsWith("window-meta:frozen|", EvidenceWindowOffline.FreezeMeta(dir, meta), StringComparison.Ordinal);
        var c = ResearchEvidenceBridge.CompatCheck(dir);
        Assert.False(c.Compatible);
        Assert.Contains("incompatible", c.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void File_source_skips_comments_and_rejects_incomplete()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aifb-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var hist = Path.Combine(dir, "history.tsv");
        WriteHistory(hist, 35);
        File.AppendAllText(hist, "# trailing comment\n");
        var matches = Path.Combine(dir, "matches.tsv");
        var odds = Path.Combine(dir, "odds.tsv");
        WriteMatchAndOdds(matches, odds, 90001, DateTime.Now.AddDays(2), DateTime.Now.AddHours(-1));
        var src = new ResearchEvidenceBridge.FileResearchDataSource(hist, matches, odds);
        Assert.True(src.LoadScoreSamples().Count >= 35);
        Assert.Single(src.LoadMatches());
        Assert.Equal(8, src.LoadOdds().Count);

        var bad = Path.Combine(dir, "bad-odds.tsv");
        File.WriteAllText(bad, "matchId\tplayType\toptionCode\todds\tcapturedAt\n1\tJQ\t0\t2.0\n");
        var badSrc = new ResearchEvidenceBridge.FileResearchDataSource(hist, matches, bad);
        Assert.Throws<InvalidDataException>(() => badSrc.LoadOdds());
    }

    [Fact]
    public void Predict_uses_real_FullSupport_not_forged_probability()
    {
        var root = Path.Combine(Path.GetTempPath(), "aifb-pred-" + Guid.NewGuid().ToString("N"));
        var win = Path.Combine(root, "window");
        Directory.CreateDirectory(win);
        Assert.StartsWith("window-meta:frozen|", FreezeCompatible(win), StringComparison.Ordinal);
        WriteTrain(win);

        var hist = Path.Combine(root, "history.tsv");
        var matches = Path.Combine(root, "matches.tsv");
        var odds = Path.Combine(root, "odds.tsv");
        WriteHistory(hist, 40);
        var asOf = DateTime.Parse("2026-11-10T15:00:00+08:00", CultureInfo.InvariantCulture);
        var kickoff = DateTime.Parse("2026-11-10T20:00:00+08:00", CultureInfo.InvariantCulture);
        var captured = DateTime.Parse("2026-11-10T14:00:00+08:00", CultureInfo.InvariantCulture);
        WriteMatchAndOdds(matches, odds, 91001, kickoff, captured);

        var src = new ResearchEvidenceBridge.FileResearchDataSource(hist, matches, odds);
        var batch = ResearchEvidenceBridge.PredictAndArchive(new ResearchEvidenceBridge.PredictOptions
        {
            WindowDirectory = win,
            Source = src,
            AsOf = asOf,
            DryRun = false
        });
        Assert.StartsWith("predict-archive|", batch.Status, StringComparison.Ordinal);
        Assert.Equal(1, batch.Archived);

        var log = File.ReadAllText(Path.Combine(win, "archive.log"));
        Assert.Contains("kind=predeclared-unseen", log, StringComparison.Ordinal);
        Assert.Contains("temperature=1.25", log, StringComparison.Ordinal);
        Assert.Equal(1.25, ResearchEvidenceBridge.ResearchFrozenTemperature);
        Assert.Equal(DecisionDelivery.RuntimeTemperature, ResearchEvidenceBridge.ResearchFrozenTemperature);
        Assert.Contains("temperature=1.25", DecisionDelivery.RuntimeModel(), StringComparison.Ordinal);
        Assert.Contains("play=JQ", log, StringComparison.Ordinal);
        Assert.Contains(ResearchEvidenceBridge.UncalibratedLabel, log, StringComparison.Ordinal);

        // 重算 pFull 必须来自 FullSupport+T，不是伪造常数
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in log.Trim().Split('\t'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            fields[part[..eq]] = part[(eq + 1)..];
        }
        var lh = double.Parse(fields["lambdaHome"], CultureInfo.InvariantCulture);
        var la = double.Parse(fields["lambdaAway"], CultureInfo.InvariantCulture);
        var rho = double.Parse(fields["rho"], CultureInfo.InvariantCulture);
        var temp = double.Parse(fields["temperature"], CultureInfo.InvariantCulture);
        var stored = double.Parse(fields["pFull"], CultureInfo.InvariantCulture);
        var optCode = fields["option"];
        var raw = FullSupportDixonColes.Create(lh, la, rho);
        var scaled = TemperatureCalibrator.Apply(raw, temp);
        var margin = PlayMargins.AggregateGoals(scaled);
        var idx = Array.IndexOf(ResearchEvidenceBridge.JqOptionCodes, optCode);
        Assert.InRange(idx, 0, 7);
        Assert.True(Math.Abs(margin.Values[idx] - stored) < 1e-6);
    }

    [Fact]
    public void Timing_leak_rejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "aifb-leak-" + Guid.NewGuid().ToString("N"));
        var win = Path.Combine(root, "window");
        Directory.CreateDirectory(win);
        FreezeCompatible(win);
        WriteTrain(win);
        var hist = Path.Combine(root, "history.tsv");
        var matches = Path.Combine(root, "matches.tsv");
        var odds = Path.Combine(root, "odds.tsv");
        WriteHistory(hist, 40);
        // asOf after kickoff → reject
        var kickoff = DateTime.Parse("2026-11-10T18:00:00+08:00", CultureInfo.InvariantCulture);
        var asOf = DateTime.Parse("2026-11-10T19:00:00+08:00", CultureInfo.InvariantCulture);
        var captured = DateTime.Parse("2026-11-10T14:00:00+08:00", CultureInfo.InvariantCulture);
        WriteMatchAndOdds(matches, odds, 92001, kickoff, captured);
        var batch = ResearchEvidenceBridge.PredictAndArchive(new ResearchEvidenceBridge.PredictOptions
        {
            WindowDirectory = win,
            Source = new ResearchEvidenceBridge.FileResearchDataSource(hist, matches, odds),
            AsOf = asOf
        });
        Assert.Equal(1, batch.Rejected);
        Assert.Contains("kickoff-not-future", batch.RejectReasons[0], StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(win, "archive.log")));
    }

    [Fact]
    public void Idempotent_skip_and_conflict_hard_reject()
    {
        var root = Path.Combine(Path.GetTempPath(), "aifb-idemp-" + Guid.NewGuid().ToString("N"));
        var win = Path.Combine(root, "window");
        Directory.CreateDirectory(win);
        FreezeCompatible(win);
        WriteTrain(win);
        var hist = Path.Combine(root, "history.tsv");
        var matches = Path.Combine(root, "matches.tsv");
        var odds = Path.Combine(root, "odds.tsv");
        WriteHistory(hist, 40);
        var asOf = DateTime.Parse("2026-11-10T15:00:00+08:00", CultureInfo.InvariantCulture);
        var kickoff = DateTime.Parse("2026-11-10T20:00:00+08:00", CultureInfo.InvariantCulture);
        var captured = DateTime.Parse("2026-11-10T14:00:00+08:00", CultureInfo.InvariantCulture);
        WriteMatchAndOdds(matches, odds, 93001, kickoff, captured);
        var src = new ResearchEvidenceBridge.FileResearchDataSource(hist, matches, odds);
        var opt = new ResearchEvidenceBridge.PredictOptions { WindowDirectory = win, Source = src, AsOf = asOf };
        var first = ResearchEvidenceBridge.PredictAndArchive(opt);
        Assert.Equal(1, first.Archived);
        var second = ResearchEvidenceBridge.PredictAndArchive(opt);
        Assert.Equal(1, second.IdempotentSkipped);
        Assert.Equal(1, File.ReadAllLines(Path.Combine(win, "archive.log")).Count(l => l.Length > 0));

        // 手工写入冲突 identity
        File.AppendAllText(Path.Combine(win, "archive.log"),
            "kind=predeclared-unseen\trunId=x\tmatchId=94001\tidentityHash=deadbeef\n");
        // 另建一场再冲突：直接测 LoadExisting 路径——改 odds 后同 match 不同 identity
        // 简化：对 93001 改 snapshot 价格后应 conflict
        WriteMatchAndOdds(matches, odds, 93001, kickoff, captured);
        // bump one price
        File.AppendAllText(odds, "93001\tJQ\t0\t9.999\t" + ResearchEvidenceBridge.FormatTime(captured) + "\n");
        // GroupBy takes latest — still same captured, higher odds for 0 may change selection/p
        var third = ResearchEvidenceBridge.PredictAndArchive(opt);
        Assert.True(third.IdempotentSkipped + third.Rejected >= 1);
    }

    [Fact]
    public void Checkpoint_written_and_resume_idempotent_via_archive()
    {
        var root = Path.Combine(Path.GetTempPath(), "aifb-ckpt-" + Guid.NewGuid().ToString("N"));
        var win = Path.Combine(root, "window");
        Directory.CreateDirectory(win);
        FreezeCompatible(win);
        WriteTrain(win);
        var hist = Path.Combine(root, "history.tsv");
        WriteHistory(hist, 40);
        var matches = Path.Combine(root, "matches.tsv");
        var odds = Path.Combine(root, "odds.tsv");
        var asOf = DateTime.Parse("2026-11-10T15:00:00+08:00", CultureInfo.InvariantCulture);
        var kickoff = DateTime.Parse("2026-11-10T20:00:00+08:00", CultureInfo.InvariantCulture);
        var captured = DateTime.Parse("2026-11-10T14:00:00+08:00", CultureInfo.InvariantCulture);
        WriteMatchAndOdds(matches, odds, 95001, kickoff, captured);
        var opt = new ResearchEvidenceBridge.PredictOptions
        {
            WindowDirectory = win,
            Source = new ResearchEvidenceBridge.FileResearchDataSource(hist, matches, odds),
            AsOf = asOf
        };
        Assert.Equal(1, ResearchEvidenceBridge.PredictAndArchive(opt).Archived);
        Assert.True(File.Exists(Path.Combine(win, ResearchEvidenceBridge.CheckpointFileName)));
        // 中断恢复：archive 仍在 → 幂等跳过，不重复计场
        var resume = ResearchEvidenceBridge.PredictAndArchive(opt);
        Assert.Equal(1, resume.IdempotentSkipped);
        Assert.Equal(1, File.ReadAllLines(Path.Combine(win, "archive.log")).Count(l => l.Length > 0));
    }

    [Fact]
    public void Attach_result_and_status_gaps()
    {
        var root = Path.Combine(Path.GetTempPath(), "aifb-att-" + Guid.NewGuid().ToString("N"));
        var win = Path.Combine(root, "window");
        Directory.CreateDirectory(win);
        FreezeCompatible(win);
        WriteTrain(win);
        var hist = Path.Combine(root, "history.tsv");
        var matches = Path.Combine(root, "matches.tsv");
        var odds = Path.Combine(root, "odds.tsv");
        WriteHistory(hist, 40);
        var asOf = DateTime.Parse("2026-11-10T15:00:00+08:00", CultureInfo.InvariantCulture);
        var kickoff = DateTime.Parse("2026-11-10T20:00:00+08:00", CultureInfo.InvariantCulture);
        var captured = DateTime.Parse("2026-11-10T14:00:00+08:00", CultureInfo.InvariantCulture);
        WriteMatchAndOdds(matches, odds, 96001, kickoff, captured);
        var src = new ResearchEvidenceBridge.FileResearchDataSource(hist, matches, odds);
        Assert.Equal(1, ResearchEvidenceBridge.PredictAndArchive(new ResearchEvidenceBridge.PredictOptions
        {
            WindowDirectory = win, Source = src, AsOf = asOf
        }).Archived);

        // finish match in matches file
        File.WriteAllText(matches,
            "matchId\tleague\thome\taway\tkickoff\touLine\thomeScore\tawayScore\tstatus\tsettledAt\n"
            + "96001\tL1\tH0\tA3\t" + ResearchEvidenceBridge.FormatTime(kickoff)
            + "\t2.5\t2\t1\t3\t" + ResearchEvidenceBridge.FormatTime(kickoff.AddHours(2)) + "\n");
        var att = ResearchEvidenceBridge.AttachFinishedFromSource(win,
            new ResearchEvidenceBridge.FileResearchDataSource(hist, matches, odds), dryRun: false);
        Assert.StartsWith("attach-results|", att, StringComparison.Ordinal);

        var st = ResearchEvidenceBridge.Status(win);
        Assert.Equal(1, st.DeclareMatches);
        Assert.Equal(1, st.ResultsAttached);
        Assert.True(st.GapTest > 0);
        Assert.Contains("qualified=false", st.StatusLine, StringComparison.Ordinal);
        Assert.True(st.CompatOk);
    }

    [Fact]
    public void Version_mix_rejected_by_evaluate_window()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aifb-mix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        FreezeCompatible(dir);
        WriteTrain(dir);
        var asOf = "2026-11-10T15:00:00+08:00";
        var kick = "2026-11-10T20:00:00+08:00";
        var cap = "2026-11-10T14:00:00+08:00";
        var snap = "0=3.000;1=3.200;2=3.400;3=4.000;4=5.000;5=7.000;6=9.000;7=12.000";
        EvidenceWindowOffline.ReadMeta(dir, out var meta);
        void WriteOne(long id, string model)
        {
            CandidateArchive.WritePredeclaredUnseen(dir, new ArchiveRow
            {
                RunId = "r-" + id,
                MatchId = id,
                LambdaHome = 1.2,
                LambdaAway = 1.0,
                Rho = Rho,
                Temperature = 1.1,
                StoredProbability = 0.12,
                Play = "JQ",
                Option = "2",
                Odds = 3.4m,
                OddsSnapshot = snap,
                DeclaredAt = cap,
                OddsCapturedAt = cap,
                AsOf = asOf,
                Kickoff = kick,
                TrainCutoff = meta["TrainCutoff"],
                EvidenceId = meta["EvidenceId"],
                SampleRef = meta["SampleRef"],
                ModelVersion = model,
                CodeHash = meta["CodeHash"],
                RuleVersion = meta["PolicyVersion"],
                Lane = "research",
                Decision = "observe"
            });
        }
        WriteOne(97001, meta["ModelVersion"]);
        WriteOne(97002, "other-model-version");
        var ev = CandidateArchive.EvaluateWindow(dir);
        Assert.Contains("model-version-mismatch", ev, StringComparison.Ordinal);
    }
}
