using System.Globalization;
using AiFootBall.Core.Models;
using AiFootBall.Core.Models.Candidate;
using Microsoft.Data.SqlClient;

// 研究离线入口：只读导出/检查、赛前预测归档、附果、status、评价、建证。
// 夹具≠真实合格；不写生产库（export 仅 SELECT）；不启候选。

if (args.Length == 0)
{
    PrintHelp();
    return 2;
}

var dryRun = args.Any(a => a == "--dry-run");
var allowSql = args.Any(a => a == "--allow-sql");
var cmdArgs = args.Where(a => a is not ("--dry-run" or "--allow-sql")).ToArray();
var cmd = cmdArgs[0];

try
{
    return cmd switch
    {
        "compat-check" => CmdCompat(cmdArgs),
        "export-check" => CmdExport(cmdArgs, dryRun, allowSql),
        "predict-archive" => CmdPredict(cmdArgs, dryRun),
        "attach-results" => CmdAttach(cmdArgs, dryRun),
        "status" => CmdStatus(cmdArgs),
        "evaluate" => CmdEvaluate(cmdArgs),
        "build-evidence" => CmdBuild(cmdArgs),
        "new-window-cmd" => CmdNewWindow(),
        "fingerprints" => CmdFingerprints(),
        _ => Fail("blocked:unknown-command|" + cmd)
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine("blocked:exception|" + ex.Message);
    return 1;
}

static int CmdCompat(string[] args)
{
    if (args.Length < 2) return Fail("blocked:usage|compat-check <window-dir>");
    var r = ResearchEvidenceBridge.CompatCheck(args[1]);
    Console.WriteLine(r.Status);
    Console.WriteLine("expectedModel=" + r.ExpectedModel);
    Console.WriteLine("expectedCode=" + r.ExpectedCode);
    Console.WriteLine("windowModel=" + r.WindowModel);
    Console.WriteLine("windowCode=" + r.WindowCode);
    Console.WriteLine("evidenceId=" + r.EvidenceId);
    EvidenceWindowOffline.WriteStatus(args[1], r.Status);
    return r.Compatible ? 0 : 1;
}

static int CmdNewWindow()
{
    Console.WriteLine(ResearchEvidenceBridge.NewWindowFreezeCommandTemplate());
    Console.WriteLine("note=1825-incompatible-keep-readonly|do-not-auto-freeze|await-confirmation");
    return 0;
}

static int CmdFingerprints()
{
    Console.WriteLine("ModelVersion=" + DecisionDelivery.RuntimeModel());
    Console.WriteLine("CodeHash=" + BuildInfo.Version);
    Console.WriteLine("PolicyVersion=" + CandidateProtocol.RuleVersion);
    Console.WriteLine("label=" + ResearchEvidenceBridge.UncalibratedLabel);
    return 0;
}

static int CmdExport(string[] args, bool dryRun, bool allowSql)
{
    // export-check <out-dir> [--from=] [--to=] [--conn=] [--allow-sql]
    if (args.Length < 2) return Fail("blocked:usage|export-check <out-dir> [--from=ISO] [--to=ISO] [--conn=...] --allow-sql");
    var outDir = args[1];
    Directory.CreateDirectory(outDir);
    string? conn = null;
    var from = DateTime.Today.AddDays(-30);
    var to = DateTime.Today.AddDays(7);
    for (var i = 2; i < args.Length; i++)
    {
        if (args[i].StartsWith("--conn=", StringComparison.Ordinal)) conn = args[i]["--conn=".Length..];
        else if (args[i].StartsWith("--from=", StringComparison.Ordinal)
                 && ResearchEvidenceBridge.TryParseTime(args[i]["--from=".Length..], out var f)) from = f;
        else if (args[i].StartsWith("--to=", StringComparison.Ordinal)
                 && ResearchEvidenceBridge.TryParseTime(args[i]["--to=".Length..], out var t)) to = t;
    }

    if (!allowSql)
    {
        Console.WriteLine("export-check|blocked:sql-not-allowed|hint=pass --allow-sql after authorization");
        Console.WriteLine(ResearchEvidenceBridge.ResearchSqlQueries.ConnectionHint);
        WriteSqlTemplate(outDir);
        return dryRun ? 0 : 1;
    }
    if (string.IsNullOrWhiteSpace(conn))
        return Fail("blocked:conn-missing");

    // 强制只读意图
    if (!conn.Contains("ApplicationIntent=ReadOnly", StringComparison.OrdinalIgnoreCase))
        conn = conn.TrimEnd(';') + ";ApplicationIntent=ReadOnly";

    if (dryRun)
    {
        Console.WriteLine("export-check|dry-run|from=" + from.ToString("o") + "|to=" + to.ToString("o"));
        WriteSqlTemplate(outDir);
        return 0;
    }

    var matches = new List<ResearchEvidenceBridge.MatchClockRow>();
    using (var db = new SqlConnection(conn))
    {
        db.Open();
        using (var cmd = new SqlCommand(ResearchEvidenceBridge.ResearchSqlQueries.MatchSelect, db))
        {
            cmd.Parameters.AddWithValue("@from", from);
            cmd.Parameters.AddWithValue("@to", to);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                matches.Add(new ResearchEvidenceBridge.MatchClockRow
                {
                    MatchId = r.GetInt64(0),
                    League = r.IsDBNull(1) ? "" : r.GetString(1),
                    Home = r.IsDBNull(2) ? "" : r.GetString(2),
                    Away = r.IsDBNull(3) ? "" : r.GetString(3),
                    Kickoff = r.GetDateTime(4),
                    OuLine = r.IsDBNull(5) ? null : r.GetDecimal(5),
                    HomeScore = r.IsDBNull(6) ? null : Convert.ToInt32(r.GetValue(6)),
                    AwayScore = r.IsDBNull(7) ? null : Convert.ToInt32(r.GetValue(7)),
                    Status = r.IsDBNull(8) ? 0 : r.GetInt32(8),
                    SettledAt = r.IsDBNull(9) ? null : r.GetDateTime(9)
                });
            }
        }
    }

    var matchPath = Path.Combine(outDir, "matches.tsv");
    using (var sw = new StreamWriter(matchPath))
    {
        sw.WriteLine("# fixture-or-export|kickoff=FbMatch.MatchTime");
        sw.WriteLine("matchId\tleague\thome\taway\tkickoff\touLine\thomeScore\tawayScore\tstatus\tsettledAt");
        foreach (var m in matches)
        {
            sw.WriteLine(string.Join('\t',
                m.MatchId.ToString(CultureInfo.InvariantCulture),
                m.League, m.Home, m.Away,
                ResearchEvidenceBridge.FormatTime(m.Kickoff),
                m.OuLine?.ToString(CultureInfo.InvariantCulture) ?? "",
                m.HomeScore?.ToString(CultureInfo.InvariantCulture) ?? "",
                m.AwayScore?.ToString(CultureInfo.InvariantCulture) ?? "",
                m.Status.ToString(CultureInfo.InvariantCulture),
                m.SettledAt is DateTime s ? ResearchEvidenceBridge.FormatTime(s) : ""));
        }
    }

    Console.WriteLine("export-check|ok|matches=" + matches.Count.ToString(CultureInfo.InvariantCulture)
                      + "|file=" + matchPath
                      + "|note=odds/history-export-via-predict-inputs|no-current-price-backfill");
    WriteSqlTemplate(outDir);
    return 0;
}

static void WriteSqlTemplate(string outDir)
{
    File.WriteAllText(Path.Combine(outDir, "sql-readonly-template.txt"),
        ResearchEvidenceBridge.ResearchSqlQueries.ConnectionHint + "\n\n"
        + ResearchEvidenceBridge.ResearchSqlQueries.MatchSelect + "\n\n"
        + ResearchEvidenceBridge.ResearchSqlQueries.OddsHistorySelect + "\n\n"
        + ResearchEvidenceBridge.ResearchSqlQueries.ScoreSamplesSelect + "\n");
}

static int CmdPredict(string[] args, bool dryRun)
{
    // predict-archive <window> <history.tsv> <matches.tsv> <odds.tsv> [--asOf=]
    if (args.Length < 5)
        return Fail("blocked:usage|predict-archive <window> <history.tsv> <matches.tsv> <odds.tsv> [--asOf=ISO] [--dry-run]");
    var asOf = DateTime.Now;
    for (var i = 5; i < args.Length; i++)
        if (args[i].StartsWith("--asOf=", StringComparison.Ordinal)
            && ResearchEvidenceBridge.TryParseTime(args[i]["--asOf=".Length..], out var a))
            asOf = a;

    var src = new ResearchEvidenceBridge.FileResearchDataSource(args[2], args[3], args[4]);
    var batch = ResearchEvidenceBridge.PredictAndArchive(new ResearchEvidenceBridge.PredictOptions
    {
        WindowDirectory = args[1],
        Source = src,
        AsOf = asOf,
        DryRun = dryRun,
        Play = "JQ"
    });
    Console.WriteLine(batch.Status);
    foreach (var r in batch.RejectReasons.Take(20))
        Console.WriteLine("reject|" + r);
    EvidenceWindowOffline.WriteStatus(args[1], batch.Status);
    return batch.Status.StartsWith("predict-archive|", StringComparison.Ordinal) ? 0 : 1;
}

static int CmdAttach(string[] args, bool dryRun)
{
    // attach-results <window> <history.tsv> <matches.tsv> <odds.tsv>
    // 或 attach-results <window> --results-tsv=path
    if (args.Length < 2) return Fail("blocked:usage|attach-results <window> <history> <matches> <odds> | --results-tsv=");
    var window = args[1];
    string? resultsTsv = null;
    for (var i = 2; i < args.Length; i++)
        if (args[i].StartsWith("--results-tsv=", StringComparison.Ordinal))
            resultsTsv = args[i]["--results-tsv=".Length..];

    if (resultsTsv != null)
    {
        if (dryRun)
        {
            Console.WriteLine("attach-results|dry-run|tsv=" + resultsTsv);
            return 0;
        }
        var status = EvidenceWindowOffline.ImportResults(window, resultsTsv);
        Console.WriteLine(status);
        EvidenceWindowOffline.WriteStatus(window, status);
        return status.StartsWith("results:imported|", StringComparison.Ordinal) ? 0 : 1;
    }

    if (args.Length < 5) return Fail("blocked:usage|attach-results <window> <history> <matches> <odds>");
    var src = new ResearchEvidenceBridge.FileResearchDataSource(args[2], args[3], args[4]);
    var status2 = ResearchEvidenceBridge.AttachFinishedFromSource(window, src, dryRun);
    Console.WriteLine(status2);
    EvidenceWindowOffline.WriteStatus(window, status2);
    return status2.StartsWith("attach-results|", StringComparison.Ordinal) ? 0 : 1;
}

static int CmdStatus(string[] args)
{
    if (args.Length < 2) return Fail("blocked:usage|status <window>");
    var q = ResearchEvidenceBridge.Status(args[1]);
    Console.WriteLine(q.StatusLine);
    foreach (var g in q.VersionGroups)
        Console.WriteLine("versionGroup|" + g);
    foreach (var r in q.RejectSamples.Take(10))
        Console.WriteLine("rejectSample|" + r);
    EvidenceWindowOffline.WriteStatus(args[1], q.StatusLine);
    // status 永远 exit 0 以便脚本读取缺口；合格标志在 qualified=false
    return 0;
}

static int CmdEvaluate(string[] args)
{
    if (args.Length < 2) return Fail("blocked:usage|evaluate <window>");
    var status = EvidenceWindowOffline.EvaluateAll(args[1]);
    Console.WriteLine(status);
    EvidenceWindowOffline.WriteStatus(args[1], status);
    return status.Contains("predeclared-unseen:pass|", StringComparison.Ordinal)
           && status.Contains("results:ok|", StringComparison.Ordinal)
        ? 0 : 1;
}

static int CmdBuild(string[] args)
{
    if (args.Length < 3) return Fail("blocked:usage|build-evidence <window> <evidence.out.txt>");
    var status = EvidenceWindowOffline.BuildEvidence(args[1], args[2]);
    Console.WriteLine(status);
    EvidenceWindowOffline.WriteStatus(args[1], status);
    return status.Contains("evidenceFile=", StringComparison.Ordinal) ? 0 : 1;
}

static int Fail(string msg)
{
    Console.Error.WriteLine(msg);
    return 1;
}

static void PrintHelp()
{
    Console.WriteLine("""
CandidateResearchOffline — 真实采样→候选预测→未见窗归档（研究工具）
  compat-check <window>
  new-window-cmd
  export-check <out-dir> [--from=] [--to=] [--conn=] [--allow-sql] [--dry-run]
  predict-archive <window> <history.tsv> <matches.tsv> <odds.tsv> [--asOf=] [--dry-run]
  attach-results <window> <history> <matches> <odds> | --results-tsv=path [--dry-run]
  status <window>
  evaluate <window>
  build-evidence <window> <evidence.out.txt>
未校准研究预测 T=1.10 ≠ Fit 后待验证模型。夹具/EXE 通过 ≠ 真实 400/400 合格。
1825 窗与候选 RuntimeModel 不兼容：勿写入；见 new-window-cmd。
真实 SQL 导出默认拒绝，须另授权后加 --allow-sql。
""");
}
