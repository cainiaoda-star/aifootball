using System.Globalization;
using AiFootBall.Core.Models.Candidate;

// 离线证据窗 CLI。不连库、不部署。夹具/样例仅证机制。
// 用法见包内 notes/离线入口说明.md

if (args.Length == 0)
{
    PrintHelp();
    return 2;
}

var cmd = args[0];
try
{
    var code = cmd switch
    {
        "freeze-meta" => CmdFreezeMeta(args),
        "import-train" => CmdImport(args, EvidenceWindowOffline.ImportTrainRoster),
        "import-declares" => CmdImport(args, EvidenceWindowOffline.ImportDeclares),
        "import-results" => CmdImport(args, EvidenceWindowOffline.ImportResults),
        "evaluate" => CmdDir(args, EvidenceWindowOffline.EvaluateAll),
        "build-evidence" => CmdBuild(args),
        "bind-check" => CmdBind(args),
        "verify" => CmdVerify(args),
        _ => Fail("blocked:unknown-command|" + cmd)
    };
    return code;
}
catch (Exception ex)
{
    Console.Error.WriteLine("blocked:exception|" + ex.Message);
    return 1;
}

static int CmdFreezeMeta(string[] args)
{
    // freeze-meta <dir> key=value ...
    if (args.Length < 3) return Fail("blocked:usage|freeze-meta <dir> EvidenceId=.. SampleRef=.. ModelVersion=.. CodeHash=.. PolicyVersion=.. TrainCutoff=..");
    var dir = args[1];
    var meta = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 2; i < args.Length; i++)
    {
        var eq = args[i].IndexOf('=');
        if (eq <= 0) return Fail("blocked:meta-pair|" + args[i]);
        meta[args[i][..eq]] = args[i][(eq + 1)..];
    }
    return Emit(dir, EvidenceWindowOffline.FreezeMeta(dir, meta));
}

static int CmdImport(string[] args, Func<string, string, string> fn)
{
    if (args.Length < 3) return Fail("blocked:usage|" + args[0] + " <dir> <import.tsv>");
    return Emit(args[1], fn(args[1], args[2]));
}

static int CmdDir(string[] args, Func<string, string> fn)
{
    if (args.Length < 2) return Fail("blocked:usage|" + args[0] + " <dir>");
    return Emit(args[1], fn(args[1]));
}

static int CmdBuild(string[] args)
{
    if (args.Length < 3) return Fail("blocked:usage|build-evidence <dir> <evidence.out.txt>");
    return Emit(args[1], EvidenceWindowOffline.BuildEvidence(args[1], args[2]));
}

static int CmdBind(string[] args)
{
    // bind-check <dir> <evidence.txt> <requiredEvidenceId>
    if (args.Length < 4) return Fail("blocked:usage|bind-check <dir> <evidence.txt> <requiredEvidenceId>");
    // 夹具 Fit：仅供机制绑定路径；不得当作真实校准证据。
    var d = FullSupportDixonColes.Create(1.2, 1.0, 0.010);
    var fit = TemperatureCalibrator.Fit(
        Enumerable.Repeat(d, CandidateProtocol.MinCalibrationMatches).ToArray(),
        Enumerable.Repeat((1, 0), CandidateProtocol.MinCalibrationMatches).ToArray(),
        CandidateProtocol.AlphaInnerDefault);
    return Emit(args[1], EvidenceWindowOffline.BindCheck(args[1], args[2], args[3], fit));
}

static int CmdVerify(string[] args)
{
    if (args.Length < 3) return Fail("blocked:usage|verify <dir> <evidence.txt>");
    if (!EvidenceWindowOffline.TryReadEvidenceFile(args[2], out var evidence, out var err))
        return Fail(err);
    return Emit(args[1], CandidateArchive.VerifyEvidenceAgainstWindow(evidence, args[1]));
}

static int Emit(string dir, string status)
{
    Console.WriteLine(status);
    EvidenceWindowOffline.WriteStatus(dir, status);
    return IsSuccess(status) ? 0 : 1;
}

static bool IsSuccess(string status)
{
    if (status.StartsWith("window-meta:frozen", StringComparison.Ordinal)) return true;
    if (status.StartsWith("train-roster:ok", StringComparison.Ordinal)) return true;
    if (status.StartsWith("declares:imported", StringComparison.Ordinal)) return true;
    if (status.StartsWith("results:imported", StringComparison.Ordinal)) return true;
    if (status.StartsWith("predeclared-unseen:pass", StringComparison.Ordinal)) return true;
    if (status == "evidence:window-ok") return true;
    if (status.StartsWith("bind-check|", StringComparison.Ordinal)
        && status.Contains("mechanism=bound|", StringComparison.Ordinal)) return true;
    if (status.StartsWith("evaluate|", StringComparison.Ordinal)
        && status.Contains("predeclared-unseen:pass|", StringComparison.Ordinal)
        && status.Contains("results:ok|", StringComparison.Ordinal)) return true;
    return false;
}

static int Fail(string msg)
{
    Console.Error.WriteLine(msg);
    return 1;
}

static void PrintHelp()
{
    Console.WriteLine("""
CandidateEvidenceOffline — 机制夹具离线入口（非真实未见证据）
  freeze-meta <dir> EvidenceId=.. SampleRef=.. ModelVersion=.. CodeHash=.. PolicyVersion=candidate-rule-v1 TrainCutoff=..
  import-train <dir> train.import.tsv
  import-declares <dir> declares.import.tsv
  import-results <dir> results.import.tsv
  evaluate <dir>
  build-evidence <dir> evidence.out.txt
  verify <dir> evidence.out.txt
  bind-check <dir> evidence.out.txt <requiredEvidenceId>
生产 BoundEvidenceId 为空时 production=bound-evidence-id-empty；mechanism 走 BindVerified。
bind-check 使用夹具 Fit（fixtureFit=true），不是真实校准，也不是生产可绑定。
导入 TSV 跳过空行、# 注释与表头；非法数据行明确拒绝。
""");
}
