using System.Diagnostics;
using System.Globalization;
using System.Text;
using AiFootBall.Core.Models;
using AiFootBall.Core.Models.Candidate;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 进程级：CandidateResearchOffline EXE。夹具标 fixtureOnly；通过 ≠ 真实 400 合格。
/// </summary>
public class CandidateResearchOfflineExeTests
{
    static string? ResolveExe()
    {
        var env = Environment.GetEnvironmentVariable("CANDIDATE_RESEARCH_OFFLINE_EXE");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "tools", "CandidateResearchOffline", "bin", "Release", "net8.0", "CandidateResearchOffline.exe")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "tools", "CandidateResearchOffline", "bin", "Release", "net8.0", "CandidateResearchOffline.dll")),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return null;
    }

    static string? ResolveEvidenceExe()
    {
        var env = Environment.GetEnvironmentVariable("CANDIDATE_EVIDENCE_OFFLINE_EXE");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        var c = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "tools", "CandidateEvidenceOffline", "bin", "Release", "net8.0", "CandidateEvidenceOffline.exe"));
        return File.Exists(c) ? c : null;
    }

    static (int Exit, string StdOut, string StdErr) Run(string exe, string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (exe.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = "dotnet";
            psi.ArgumentList.Add(exe);
        }
        else psi.FileName = exe;
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("process-null");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(180_000);
        return (p.ExitCode, stdout.Trim(), stderr.Trim());
    }

    [Fact]
    public void Sealed_research_exe_fixture_pipeline_step_by_step()
    {
        var exe = ResolveExe();
        Assert.True(exe != null, "未找到 CandidateResearchOffline Release；设 CANDIDATE_RESEARCH_OFFLINE_EXE 或先 Release 构建");
        var evExe = ResolveEvidenceExe();
        Assert.True(evExe != null, "需要 CandidateEvidenceOffline Release 做 freeze-meta");

        var work = Path.Combine(Path.GetTempPath(), "aifb-research-exe-" + Guid.NewGuid().ToString("N"));
        var win = Path.Combine(work, "window");
        Directory.CreateDirectory(win);
        var logPath = Path.Combine(work, "research-exe-e2e-step.log");
        var log = new StringBuilder();
        void Log(string step, int code, string stdout, string stderr)
        {
            log.AppendLine("=== " + step + " exit=" + code);
            log.AppendLine("stdout: " + stdout);
            if (stderr.Length > 0) log.AppendLine("stderr: " + stderr);
            File.WriteAllText(logPath, log.ToString());
        }

        // 1) new-window-cmd
        var (cCmd, oCmd, eCmd) = Run(exe!, work, "new-window-cmd");
        Log("new-window-cmd", cCmd, oCmd, eCmd);
        Assert.Equal(0, cCmd);
        Assert.Contains("freeze-meta", oCmd, StringComparison.Ordinal);

        // fingerprints 必须来自同一 Research Release Core，避免 Debug 测试程序集 BuildInfo 漂移
        var (cFp, oFp, eFp) = Run(exe!, work, "fingerprints");
        Log("fingerprints", cFp, oFp, eFp);
        Assert.Equal(0, cFp);
        var fp = oFp.Split('\n').Select(l => l.Trim()).Where(l => l.Contains('=')).ToDictionary(
            l => l[..l.IndexOf('=')], l => l[(l.IndexOf('=') + 1)..], StringComparer.Ordinal);

        // 2) freeze compatible window via 1810 EXE（指纹对齐 Research EXE）
        var evidenceId = "unseen-research-exe-fixture";
        var (c0, o0, e0) = Run(evExe!, work,
            "freeze-meta", win,
            "EvidenceId=" + evidenceId,
            "SampleRef=sample-research-exe-fixture",
            "ModelVersion=" + fp["ModelVersion"],
            "CodeHash=" + fp["CodeHash"],
            "PolicyVersion=" + fp["PolicyVersion"],
            "TrainCutoff=2026-11-01T12:00:00.0000000+08:00",
            "Note=fixtureOnly=true|not-real-qualification");
        Log("freeze-meta", c0, o0, e0);
        Assert.Equal(0, c0);

        // 3) compat-check ok
        var (c1, o1, e1) = Run(exe!, work, "compat-check", win);
        Log("compat-check", c1, o1, e1);
        Assert.Equal(0, c1);
        Assert.Contains("compat:ok", o1, StringComparison.Ordinal);

        // 4) train
        var train = Path.Combine(work, "train.import.tsv");
        File.WriteAllText(train, "# fixture\nmatchId\tat\toddsAt\tkickoff\n80001\t2026-10-20T12:00:00+08:00\t2026-10-20T11:00:00+08:00\t2026-10-20T18:00:00+08:00\n");
        var (cT, oT, eT) = Run(evExe!, work, "import-train", win, train);
        Log("import-train", cT, oT, eT);
        Assert.Equal(0, cT);

        // 5) inputs
        var hist = Path.Combine(work, "history.tsv");
        var sb = new StringBuilder();
        sb.Append("# fixtureOnly=true history\nmatchId\tleague\thome\taway\thomeScore\tawayScore\tmatchTime\n");
        for (var i = 0; i < 40; i++)
        {
            var day = new DateTime(2026, 9, 1).AddDays(i);
            sb.Append(70000 + i).Append("\tL1\tH").Append(i % 8).Append("\tA").Append((i + 3) % 8)
              .Append('\t').Append(i % 3).Append('\t').Append((i + 1) % 3).Append('\t')
              .Append(day.ToString("yyyy-MM-ddTHH:mm:ss+08:00", CultureInfo.InvariantCulture)).Append('\n');
        }
        File.WriteAllText(hist, sb.ToString());
        var matches = Path.Combine(work, "matches.tsv");
        var odds = Path.Combine(work, "odds.tsv");
        var kickoff = "2026-11-10T20:00:00.0000000+08:00";
        var captured = "2026-11-10T14:00:00.0000000+08:00";
        File.WriteAllText(matches,
            "# fixtureOnly=true\nmatchId\tleague\thome\taway\tkickoff\touLine\thomeScore\tawayScore\tstatus\tsettledAt\n"
            + "91001\tL1\tH0\tA3\t" + kickoff + "\t2.5\t\t\t0\t\n");
        var osb = new StringBuilder();
        osb.Append("# fixtureOnly=true odds\nmatchId\tplayType\toptionCode\todds\tcapturedAt\n");
        var prices = new[] { "3.000", "3.200", "3.400", "4.000", "5.000", "7.000", "9.000", "12.000" };
        for (var i = 0; i < 8; i++)
            osb.Append("91001\tJQ\t").Append(i).Append('\t').Append(prices[i]).Append('\t').Append(captured).Append('\n');
        File.WriteAllText(odds, osb.ToString());

        // 6) predict-archive
        var (c2, o2, e2) = Run(exe!, work, "predict-archive", win, hist, matches, odds,
            "--asOf=2026-11-10T15:00:00.0000000+08:00");
        Log("predict-archive", c2, o2, e2);
        Assert.Equal(0, c2);
        Assert.Contains("predict-archive|", o2, StringComparison.Ordinal);
        Assert.Contains("archived=1", o2, StringComparison.Ordinal);

        // 7) attach via results tsv
        var results = Path.Combine(work, "results.import.tsv");
        File.WriteAllText(results,
            "# fixtureOnly=true\nmatchId\thome\taway\tsettledAt\n91001\t2\t1\t2026-11-10T22:00:00.0000000+08:00\n");
        var (c3, o3, e3) = Run(exe!, work, "attach-results", win, "--results-tsv=" + results);
        Log("attach-results", c3, o3, e3);
        Assert.Equal(0, c3);

        // 8) status gaps (not qualified)
        var (c4, o4, e4) = Run(exe!, work, "status", win);
        Log("status", c4, o4, e4);
        Assert.Equal(0, c4);
        Assert.Contains("qualified=false", o4, StringComparison.Ordinal);
        Assert.Contains("gapTest=", o4, StringComparison.Ordinal);

        // 9) evaluate expect fail insufficient (1 < 400) — still exercises EXE
        var (c5, o5, e5) = Run(exe!, work, "evaluate", win);
        Log("evaluate", c5, o5, e5);
        Assert.NotEqual(0, c5);

        // copy step log if harness requests
        var envLog = Environment.GetEnvironmentVariable("CANDIDATE_RESEARCH_EXE_E2E_LOG");
        if (!string.IsNullOrWhiteSpace(envLog))
            File.Copy(logPath, envLog, true);

        log.AppendLine("fixtureOnly=true|not-real-qualification");
        File.WriteAllText(logPath, log.ToString());
        if (!string.IsNullOrWhiteSpace(envLog))
            File.Copy(logPath, envLog, true);
        Assert.Contains("fixtureOnly=true", File.ReadAllText(logPath), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("predict-archive|", File.ReadAllText(logPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Export_check_refuses_sql_without_allow_flag()
    {
        var exe = ResolveExe();
        Assert.True(exe != null);
        var work = Path.Combine(Path.GetTempPath(), "aifb-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var (c, o, e) = Run(exe!, work, "export-check", work);
        Assert.Equal(1, c);
        Assert.Contains("sql-not-allowed", o, StringComparison.Ordinal);
    }
}
