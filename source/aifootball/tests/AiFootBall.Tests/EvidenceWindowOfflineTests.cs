using System.Globalization;
using System.Text;
using AiFootBall.Core.Models.Candidate;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 离线入口端到端夹具：仅证明机制，不是真实未见证据。不连库、不部署。
/// </summary>
public class EvidenceWindowOfflineTests
{
    [Fact]
    public void Offline_pipeline_freeze_import_build_bind_then_rejects_tamper()
    {
        Assert.Equal("", CandidateProtocol.BoundEvidenceId);
        Assert.False(CandidateTickets.Enabled);

        var root = Path.Combine(Path.GetTempPath(), "aifootball-offline-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var evidenceId = "unseen-offline-fixture-400"; // 夹具 id，非生产真实 EvidenceId

        var freeze = EvidenceWindowOffline.FreezeMeta(root, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["EvidenceId"] = evidenceId,
            ["SampleRef"] = "offline-fixture-window",
            ["ModelVersion"] = "model-offline-v1",
            ["CodeHash"] = "code-offline-v1",
            ["PolicyVersion"] = CandidateProtocol.RuleVersion,
            ["TrainCutoff"] = "2026-10-31 00:00:00",
            ["Note"] = "mechanism-fixture-only-not-real-unseen"
        });
        Assert.StartsWith("window-meta:frozen|", freeze, StringComparison.Ordinal);
        Assert.Equal("blocked:window-meta-already-frozen",
            EvidenceWindowOffline.FreezeMeta(root, new Dictionary<string, string> { ["EvidenceId"] = evidenceId }));

        var trainImport = Path.Combine(root, "train.import.tsv");
        File.WriteAllText(trainImport,
            "matchId\tat\toddsAt\tkickoff\n" +
            "90001\t2026-10-20 12:00:00\t2026-10-20 11:00:00\t2026-10-20 18:00:00\n" +
            "90002\t2026-10-21 12:00:00\t2026-10-21 11:00:00\t2026-10-21 18:00:00\n",
            Encoding.UTF8);
        Assert.StartsWith("train-roster:ok|", EvidenceWindowOffline.ImportTrainRoster(root, trainImport), StringComparison.Ordinal);

        var declareImport = Path.Combine(root, "declares.import.tsv");
        var sb = new StringBuilder();
        sb.AppendLine("matchId\tlambdaH\tlambdaA\trho\ttemp\tpFull\tplay\toption\todds\toddsSnapshot\tdeclaredAt\toddsCapturedAt\tasOf\tkickoff");
        var d = FullSupportDixonColes.Create(1.2, 1.0, 0.010);
        var pFull = d.Prob(1, 0).ToString("0.00000000", CultureInfo.InvariantCulture);
        var snap = "0=3.000;1=3.200;2=3.400;3=4.000;4=5.000;5=7.000;6=9.000;7=12.000";
        for (var i = 1; i <= 400; i++)
            sb.Append(i.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append("1.2\t1.0\t0.010\t1\t").Append(pFull).Append('\t')
                .Append("JQ\t2\t4.000\t").Append(snap).Append('\t')
                .Append("2026-11-01 10:00:00\t2026-11-01 11:00:00\t2026-11-01 12:00:00\t2026-11-01 18:00:00\n");
        File.WriteAllText(declareImport, sb.ToString(), Encoding.UTF8);
        Assert.Equal("declares:imported|400", EvidenceWindowOffline.ImportDeclares(root, declareImport));

        var resultsImport = Path.Combine(root, "results.import.tsv");
        var rb = new StringBuilder();
        rb.AppendLine("matchId\thome\taway\tsettledAt");
        for (var i = 1; i <= 400; i++)
            rb.Append(i.ToString(CultureInfo.InvariantCulture)).Append("\t1\t0\t2026-11-01 20:00:00\n");
        File.WriteAllText(resultsImport, rb.ToString(), Encoding.UTF8);
        Assert.Equal("results:imported|400", EvidenceWindowOffline.ImportResults(root, resultsImport));

        var eval = EvidenceWindowOffline.EvaluateAll(root);
        Assert.Contains("predeclared-unseen:pass|", eval, StringComparison.Ordinal);
        Assert.Contains("results:ok|", eval, StringComparison.Ordinal);

        var evidencePath = Path.Combine(root, "evidence.out.txt");
        var built = EvidenceWindowOffline.BuildEvidence(root, evidencePath);
        Assert.StartsWith("predeclared-unseen:pass|", built, StringComparison.Ordinal);
        Assert.True(File.Exists(evidencePath));

        var fit = TemperatureCalibrator.Fit(
            Enumerable.Repeat(d, 400).ToArray(),
            Enumerable.Repeat((1, 0), 400).ToArray(),
            CandidateProtocol.AlphaInnerDefault);
        var bind = EvidenceWindowOffline.BindCheck(root, evidencePath, evidenceId, fit);
        Assert.Contains("mechanism=bound|", bind, StringComparison.Ordinal);
        Assert.Contains("production=bound-evidence-id-empty|", bind, StringComparison.Ordinal);

        // 篡改训练名册（不重叠 ID）后绑定拒绝，且原因不是 empty BoundEvidenceId。
        Assert.StartsWith("train-roster:ok|", CandidateArchive.WriteTrainRoster(root, new[]
        {
            new TrainRosterRow { MatchId = 90001, At = "2026-10-20 12:00:00", OddsAt = "2026-10-20 11:00:00", Kickoff = "2026-10-20 18:00:00" },
            new TrainRosterRow { MatchId = 90003, At = "2026-10-22 12:00:00", OddsAt = "2026-10-22 11:00:00", Kickoff = "2026-10-22 18:00:00" }
        }), StringComparison.Ordinal);
        Assert.True(EvidenceWindowOffline.TryReadEvidenceFile(evidencePath, out var evidence, out _));
        var after = CalibratorBinding.BindVerified(fit, evidence, evidenceId, root);
        Assert.Equal("evidence-train-roster-hash-mismatch", after.Status);
        Assert.NotEqual("bound-evidence-id-empty", after.Status);
    }

    [Fact]
    public void Offline_rejects_train_time_missing_and_train_test_leak_via_import()
    {
        var root = Path.Combine(Path.GetTempPath(), "aifootball-offline-neg-" + Guid.NewGuid().ToString("N"));
        Assert.StartsWith("window-meta:frozen|", EvidenceWindowOffline.FreezeMeta(root, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["EvidenceId"] = "unseen-offline-neg",
            ["SampleRef"] = "neg-window",
            ["ModelVersion"] = "m",
            ["CodeHash"] = "c",
            ["PolicyVersion"] = CandidateProtocol.RuleVersion,
            ["TrainCutoff"] = "2026-10-31 00:00:00"
        }), StringComparison.Ordinal);

        var badTrain = Path.Combine(root, "bad-train.tsv");
        File.WriteAllText(badTrain, "matchId\tat\toddsAt\tkickoff\n1\t2026-10-01 12:00:00\t\t2026-10-01 18:00:00\n");
        Assert.Equal("blocked:train-times-missing", EvidenceWindowOffline.ImportTrainRoster(root, badTrain));

        var okTrain = Path.Combine(root, "ok-train.tsv");
        File.WriteAllText(okTrain, "matchId\tat\toddsAt\tkickoff\n1\t2026-10-20 12:00:00\t2026-10-20 11:00:00\t2026-10-20 18:00:00\n");
        Assert.StartsWith("train-roster:ok|", EvidenceWindowOffline.ImportTrainRoster(root, okTrain), StringComparison.Ordinal);

        var declare = Path.Combine(root, "one.tsv");
        File.WriteAllText(declare,
            "matchId\tlambdaH\tlambdaA\trho\ttemp\tpFull\tplay\toption\todds\toddsSnapshot\tdeclaredAt\toddsCapturedAt\tasOf\tkickoff\n" +
            "1\t1.2\t1.0\t0.010\t1\t0.1\tJQ\t2\t4.000\t0=3;1=3;2=3;3=4;4=5;5=7;6=9;7=12\t2026-11-01 10:00:00\t2026-11-01 11:00:00\t2026-11-01 12:00:00\t2026-11-01 18:00:00\n");
        Assert.Equal("declares:imported|1", EvidenceWindowOffline.ImportDeclares(root, declare));
        Assert.Equal("blocked:train-test-match-leak", CandidateArchive.EvaluateWindow(root));
    }
}
