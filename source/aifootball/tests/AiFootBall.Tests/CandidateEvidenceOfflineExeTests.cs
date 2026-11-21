using System.Diagnostics;
using System.Globalization;
using System.Text;
using AiFootBall.Core.Models.Candidate;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 进程级端到端：启动封存/Release CandidateEvidenceOffline，不用库 API 冒充 EXE 已执行。
/// 样例为机制夹具；bind-check 内夹具 Fit ≠ 真实校准 / 生产可绑定。
/// </summary>
public class CandidateEvidenceOfflineExeTests
{
    static string? ResolveExe()
    {
        var env = Environment.GetEnvironmentVariable("CANDIDATE_EVIDENCE_OFFLINE_EXE");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "tools", "CandidateEvidenceOffline", "bin", "Release", "net8.0", "CandidateEvidenceOffline.exe")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "tools", "CandidateEvidenceOffline", "bin", "Release", "net8.0", "CandidateEvidenceOffline.dll")),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return null;
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
        else
        {
            psi.FileName = exe;
        }
        foreach (var part in args)
            psi.ArgumentList.Add(part);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("process-null");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(180_000);
        return (p.ExitCode, stdout.Trim(), stderr.Trim());
    }

    [Fact]
    public void Sealed_exe_pipeline_with_commented_samples_step_by_step()
    {
        var exe = ResolveExe();
        Assert.True(exe != null, "未找到 CandidateEvidenceOffline Release 产物；请先 Release 构建或设 CANDIDATE_EVIDENCE_OFFLINE_EXE");

        var work = Path.Combine(Path.GetTempPath(), "aifootball-exe-e2e-" + Guid.NewGuid().ToString("N"));
        var win = Path.Combine(work, "window");
        Directory.CreateDirectory(win);
        var logPath = Path.Combine(work, "exe-e2e-step.log");
        var log = new StringBuilder();
        void Log(string step, int code, string stdout, string stderr)
        {
            log.AppendLine("=== " + step + " exit=" + code);
            log.AppendLine("stdout: " + stdout);
            if (stderr.Length > 0) log.AppendLine("stderr: " + stderr);
            File.WriteAllText(logPath, log.ToString());
        }

        var evidenceId = "unseen-exe-fixture-400";
        var (c0, o0, e0) = RunFreeze(exe!, win, evidenceId);
        Log("freeze-meta", c0, o0, e0);
        Assert.Equal(0, c0);
        Assert.StartsWith("window-meta:frozen|", o0, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(win, CandidateArchive.WindowMetaFileName)));
        Assert.True(File.Exists(Path.Combine(win, EvidenceWindowOffline.WindowMetaIntegrityFileName)));

        var train = Path.Combine(work, "train.import.tsv");
        File.WriteAllText(train,
            "# 机制夹具训练名册 — 非真实未见证据\n" +
            "matchId\tat\toddsAt\tkickoff\n" +
            "90001\t2026-10-20 12:00:00\t2026-10-20 11:00:00\t2026-10-20 18:00:00\n", Encoding.UTF8);
        var (c1, o1, e1) = Run(exe!, work, "import-train", win, train);
        Log("import-train", c1, o1, e1);
        Assert.Equal(0, c1);
        Assert.StartsWith("train-roster:ok|", o1, StringComparison.Ordinal);

        var d = FullSupportDixonColes.Create(1.2, 1.0, 0.010);
        var pFull = d.Prob(1, 0).ToString("0.00000000", CultureInfo.InvariantCulture);
        var declares = Path.Combine(work, "declares.import.tsv");
        using (var sw = new StreamWriter(declares, false, Encoding.UTF8))
        {
            sw.WriteLine("# 机制夹具赛前声明 — 含注释与表头，导入器须跳过");
            sw.WriteLine("matchId\tlambdaH\tlambdaA\trho\ttemp\tpFull\tplay\toption\todds\toddsSnapshot\tdeclaredAt\toddsCapturedAt\tasOf\tkickoff");
            for (var i = 1; i <= 400; i++)
                sw.WriteLine($"{i}\t1.2\t1.0\t0.010\t1\t{pFull}\tJQ\t2\t4.000\t0=3;1=3;2=3;3=4;4=5;5=7;6=9;7=12\t2026-11-01 10:00:00\t2026-11-01 11:00:00\t2026-11-01 12:00:00\t2026-11-01 18:00:00");
        }
        var (c2, o2, e2) = Run(exe!, work, "import-declares", win, declares);
        Log("import-declares", c2, o2, e2);
        Assert.Equal(0, c2);
        Assert.Equal("declares:imported|400", o2);

        var results = Path.Combine(work, "results.import.tsv");
        using (var sw = new StreamWriter(results, false, Encoding.UTF8))
        {
            sw.WriteLine("# 机制夹具赛果");
            sw.WriteLine("matchId\thome\taway\tsettledAt");
            for (var i = 1; i <= 400; i++)
                sw.WriteLine($"{i}\t1\t0\t2026-11-01 20:00:00");
        }
        var (c3, o3, e3) = Run(exe!, work, "import-results", win, results);
        Log("import-results", c3, o3, e3);
        Assert.Equal(0, c3);
        Assert.Equal("results:imported|400", o3);

        var (c4, o4, e4) = Run(exe!, work, "evaluate", win);
        Log("evaluate", c4, o4, e4);
        Assert.Equal(0, c4);
        Assert.Contains("predeclared-unseen:pass|", o4, StringComparison.Ordinal);
        Assert.Contains("results:ok|", o4, StringComparison.Ordinal);

        var evidenceOut = Path.Combine(win, "evidence.out.txt");
        var (c5, o5, e5) = Run(exe!, work, "build-evidence", win, evidenceOut);
        Log("build-evidence", c5, o5, e5);
        Assert.Equal(0, c5);
        Assert.True(File.Exists(evidenceOut));

        var (c6, o6, e6) = Run(exe!, work, "verify", win, evidenceOut);
        Log("verify", c6, o6, e6);
        Assert.Equal(0, c6);
        Assert.Equal("evidence:window-ok", o6);

        var (c7, o7, e7) = Run(exe!, work, "bind-check", win, evidenceOut, evidenceId);
        Log("bind-check", c7, o7, e7);
        Assert.Equal(0, c7);
        Assert.Contains("mechanism=bound|", o7, StringComparison.Ordinal);
        Assert.Contains("production=bound-evidence-id-empty|", o7, StringComparison.Ordinal);
        Assert.Contains("fixtureFit=true", o7, StringComparison.Ordinal);
        Assert.Contains("notRealCalibration=true", o7, StringComparison.Ordinal);

        // 非法数据行不得被注释规则吞掉
        var bad = Path.Combine(work, "bad-train.tsv");
        File.WriteAllText(bad, "# ok\nmatchId\tat\toddsAt\tkickoff\nNOT_A_NUMBER\t2026-10-20 12:00:00\t2026-10-20 11:00:00\t2026-10-20 18:00:00\n");
        var badDir = Path.Combine(work, "bad-win");
        Directory.CreateDirectory(badDir);
        var (cb, ob, eb) = RunFreeze(exe!, badDir, "unseen-exe-bad");
        Assert.Equal(0, cb);
        var (cBad, oBad, eBad) = Run(exe!, work, "import-train", badDir, bad);
        Log("import-train-illegal", cBad, oBad.Length > 0 ? oBad : eBad, eBad);
        Assert.NotEqual(0, cBad);
        Assert.Contains("blocked:train-roster-invalid", (oBad + eBad), StringComparison.Ordinal);

        // 把逐步日志拷到测试输出目录旁（若环境变量指定）
        var outLog = Environment.GetEnvironmentVariable("CANDIDATE_EVIDENCE_EXE_E2E_LOG");
        if (!string.IsNullOrWhiteSpace(outLog))
            File.Copy(logPath, outLog, true);
        else
            File.Copy(logPath, Path.Combine(Path.GetTempPath(), "CandidateEvidenceOffline-exe-e2e.log"), true);
    }

    static (int Exit, string StdOut, string StdErr) RunFreeze(string exe, string win, string evidenceId)
    {
        var psi = new ProcessStartInfo
        {
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
        psi.ArgumentList.Add("freeze-meta");
        psi.ArgumentList.Add(win);
        psi.ArgumentList.Add("EvidenceId=" + evidenceId);
        psi.ArgumentList.Add("SampleRef=exe-fixture-window");
        psi.ArgumentList.Add("ModelVersion=model-exe-v1");
        psi.ArgumentList.Add("CodeHash=code-exe-v1");
        psi.ArgumentList.Add("PolicyVersion=" + CandidateProtocol.RuleVersion);
        psi.ArgumentList.Add("TrainCutoff=2026-10-31 00:00:00");
        psi.ArgumentList.Add("Note=mechanism-fixture-only");
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("process-null");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        return (p.ExitCode, stdout.Trim(), stderr.Trim());
    }
}
