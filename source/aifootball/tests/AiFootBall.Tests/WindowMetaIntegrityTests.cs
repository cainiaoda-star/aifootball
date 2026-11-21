using AiFootBall.Core.Models.Candidate;
using Xunit;

namespace AiFootBall.Tests;

public class WindowMetaIntegrityTests
{
    static Dictionary<string, string> FixtureMeta(string evidenceId = "unseen-meta-fixture") => new(StringComparer.Ordinal)
    {
        ["EvidenceId"] = evidenceId,
        ["SampleRef"] = "meta-fixture-window",
        ["ModelVersion"] = "model-meta-v1",
        ["CodeHash"] = "code-meta-v1",
        ["PolicyVersion"] = CandidateProtocol.RuleVersion,
        ["TrainCutoff"] = "2026-10-31 00:00:00",
        ["Note"] = "mechanism-only-local-hash-not-trusted-timestamp"
    };

    [Fact]
    public void Freeze_meta_integrity_rejects_second_freeze_edit_and_deleted_integrity()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aifootball-meta-" + Guid.NewGuid().ToString("N"));
        var frozen = EvidenceWindowOffline.FreezeMeta(dir, FixtureMeta());
        Assert.StartsWith("window-meta:frozen|", frozen, StringComparison.Ordinal);
        Assert.Contains("|metaHash=", frozen, StringComparison.Ordinal);
        Assert.Equal("blocked:window-meta-already-frozen", EvidenceWindowOffline.FreezeMeta(dir, FixtureMeta("other")));

        Assert.StartsWith("window-meta:integrity-ok|", EvidenceWindowOffline.CheckWindowMetaIntegrity(dir), StringComparison.Ordinal);

        // 冻结后改字段
        var metaPath = Path.Combine(dir, CandidateArchive.WindowMetaFileName);
        var text = File.ReadAllText(metaPath).Replace("SampleRef=meta-fixture-window", "SampleRef=tampered-window", StringComparison.Ordinal);
        File.WriteAllText(metaPath, text);
        Assert.Equal("blocked:window-meta-integrity-mismatch", EvidenceWindowOffline.CheckWindowMetaIntegrity(dir));
        Assert.Equal("blocked:window-meta-integrity-mismatch", EvidenceWindowOffline.ImportTrainRoster(dir, Path.Combine(dir, "no.tsv")));

        // 恢复正文但删完整性文件
        EvidenceWindowOffline.FreezeMeta(
            Path.Combine(Path.GetTempPath(), "aifootball-meta2-" + Guid.NewGuid().ToString("N")), FixtureMeta()); // 无关
        var dir2 = Path.Combine(Path.GetTempPath(), "aifootball-meta-del-" + Guid.NewGuid().ToString("N"));
        Assert.StartsWith("window-meta:frozen|", EvidenceWindowOffline.FreezeMeta(dir2, FixtureMeta("unseen-meta-del")), StringComparison.Ordinal);
        File.Delete(Path.Combine(dir2, EvidenceWindowOffline.WindowMetaIntegrityFileName));
        Assert.Equal("blocked:window-meta-integrity-missing", EvidenceWindowOffline.CheckWindowMetaIntegrity(dir2));
        Assert.Equal("blocked:window-meta-integrity-missing", EvidenceWindowOffline.EvaluateAll(dir2));
    }

    [Fact]
    public void Build_then_tamper_meta_rejects_bind_with_specific_guard()
    {
        Assert.Equal("", CandidateProtocol.BoundEvidenceId);
        var root = Path.Combine(Path.GetTempPath(), "aifootball-meta-bind-" + Guid.NewGuid().ToString("N"));
        var evidenceId = "unseen-meta-bind-400";
        Assert.StartsWith("window-meta:frozen|", EvidenceWindowOffline.FreezeMeta(root, FixtureMeta(evidenceId)), StringComparison.Ordinal);

        var train = Path.Combine(root, "train.import.tsv");
        File.WriteAllText(train, "# mechanism fixture\nmatchId\tat\toddsAt\tkickoff\n90001\t2026-10-20 12:00:00\t2026-10-20 11:00:00\t2026-10-20 18:00:00\n");
        Assert.StartsWith("train-roster:ok|", EvidenceWindowOffline.ImportTrainRoster(root, train), StringComparison.Ordinal);

        var d = FullSupportDixonColes.Create(1.2, 1.0, 0.010);
        var pFull = d.Prob(1, 0).ToString("0.00000000", System.Globalization.CultureInfo.InvariantCulture);
        var dec = Path.Combine(root, "declares.import.tsv");
        using (var sw = new StreamWriter(dec))
        {
            sw.WriteLine("# declares");
            sw.WriteLine("matchId\tlambdaH\tlambdaA\trho\ttemp\tpFull\tplay\toption\todds\toddsSnapshot\tdeclaredAt\toddsCapturedAt\tasOf\tkickoff");
            for (var i = 1; i <= 400; i++)
                sw.WriteLine($"{i}\t1.2\t1.0\t0.010\t1\t{pFull}\tJQ\t2\t4.000\t0=3;1=3;2=3;3=4;4=5;5=7;6=9;7=12\t2026-11-01 10:00:00\t2026-11-01 11:00:00\t2026-11-01 12:00:00\t2026-11-01 18:00:00");
        }
        Assert.Equal("declares:imported|400", EvidenceWindowOffline.ImportDeclares(root, dec));
        var res = Path.Combine(root, "results.import.tsv");
        using (var sw = new StreamWriter(res))
        {
            sw.WriteLine("# results");
            sw.WriteLine("matchId\thome\taway\tsettledAt");
            for (var i = 1; i <= 400; i++)
                sw.WriteLine($"{i}\t1\t0\t2026-11-01 20:00:00");
        }
        Assert.Equal("results:imported|400", EvidenceWindowOffline.ImportResults(root, res));

        var evidencePath = Path.Combine(root, "evidence.out.txt");
        Assert.StartsWith("predeclared-unseen:pass|", EvidenceWindowOffline.BuildEvidence(root, evidencePath), StringComparison.Ordinal);
        Assert.True(EvidenceWindowOffline.TryReadEvidenceFile(evidencePath, out var evidence, out _));
        Assert.True(CalibratorBinding.IsSha256Hex(evidence.WindowMetaHash));

        var fit = TemperatureCalibrator.Fit(
            Enumerable.Repeat(d, 400).ToArray(),
            Enumerable.Repeat((1, 0), 400).ToArray(),
            CandidateProtocol.AlphaInnerDefault);
        Assert.Equal("bound", CalibratorBinding.BindVerified(fit, evidence, evidenceId, root).Status);

        File.WriteAllText(Path.Combine(root, CandidateArchive.WindowMetaFileName),
            File.ReadAllText(Path.Combine(root, CandidateArchive.WindowMetaFileName))
                .Replace("ModelVersion=model-meta-v1", "ModelVersion=tampered-model", StringComparison.Ordinal));
        Assert.Equal("blocked:window-meta-integrity-mismatch",
            CalibratorBinding.BindVerified(fit, evidence, evidenceId, root).Status);
        Assert.NotEqual("bound-evidence-id-empty",
            CalibratorBinding.BindVerified(fit, evidence, evidenceId, root).Status);
    }
}
