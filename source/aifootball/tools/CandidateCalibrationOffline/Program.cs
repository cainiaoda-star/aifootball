using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AiFootBall.Core.Models;
using AiFootBall.Core.Models.Candidate;
using Microsoft.Data.SqlClient;

// 只读校准提取：SELECT only。不用 IN @ids。不写库。T=1.10 夹具 Fit 不得冒充真实校准。

if (args.Length == 0) { Help(); return 2; }
var cmd = args[0];
try
{
    return cmd switch
    {
        "run" => CmdRun(args),
        "help" => Help(),
        _ => Fail("blocked:unknown-command|" + cmd)
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine("blocked:exception|" + ex.Message);
    return 1;
}

static int Help()
{
    Console.WriteLine("""
CandidateCalibrationOffline — 只读提取 + 校准资格审计 + 可选真实 Temperature Fit
  run --out=<dir> --conn=<cs> [--from=ISO] [--to=ISO]
要求：语句仅 SELECT；ApplicationIntent=ReadOnly 不能替代该约束。
盘口：CapturedAt < MatchTime（可证明赛前）；缺失不得用当前价回填。
去重：matchId。MinCalibrationMatches=400 才跑真实 Fit。
""");
    return 0;
}

static int Fail(string m) { Console.Error.WriteLine(m); return 1; }

static int CmdRun(string[] args)
{
    string? outDir = null, conn = null;
    DateTime? from = null, to = null;
    for (var i = 1; i < args.Length; i++)
    {
        if (args[i].StartsWith("--out=", StringComparison.Ordinal)) outDir = args[i][6..];
        else if (args[i].StartsWith("--conn=", StringComparison.Ordinal)) conn = args[i][7..];
        else if (args[i].StartsWith("--from=", StringComparison.Ordinal)
                 && DateTime.TryParse(args[i][7..], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var f))
            from = f;
        else if (args[i].StartsWith("--to=", StringComparison.Ordinal)
                 && DateTime.TryParse(args[i][5..], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t))
            to = t;
    }
    if (string.IsNullOrWhiteSpace(outDir) || string.IsNullOrWhiteSpace(conn))
        return Fail("blocked:usage|run --out= --conn=");

    if (!conn.Contains("ApplicationIntent=ReadOnly", StringComparison.OrdinalIgnoreCase))
        conn = conn.TrimEnd(';') + ";ApplicationIntent=ReadOnly";

    Directory.CreateDirectory(outDir);
    var exportDir = Path.Combine(outDir, "export");
    var fitDir = Path.Combine(outDir, "fit");
    var notesDir = Path.Combine(outDir, "notes");
    Directory.CreateDirectory(exportDir);
    Directory.CreateDirectory(fitDir);
    Directory.CreateDirectory(notesDir);

    // 默认：盘口史起点起至「现在」（库内 JcOdds 自 2026-10-05）
    from ??= new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Local);
    to ??= DateTime.Now.AddDays(1);

    var cmdLog = new StringBuilder();
    void Log(string s)
    {
        Console.WriteLine(s);
        cmdLog.AppendLine(s);
    }

    Log("phase=schema-ok|readonly=SELECT-only|appIntent=ReadOnly-not-sufficient-alone");
    Log("range|from=" + from.Value.ToString("o") + "|to=" + to.Value.ToString("o"));

    // ---- 1) 完场比赛（窗口内开赛）----
    // 本库 FbMatch 已含 LeagueName/HomeTeamName/AwayTeamName；无 FbLeague.NameCN / MatchStatus。
    var matchesSql = """
        SELECT m.MatchId,
               ISNULL(m.LeagueName, '') AS League,
               ISNULL(m.HomeTeamName, '') AS Home,
               ISNULL(m.AwayTeamName, '') AS Away,
               m.MatchTime,
               m.HomeScore,
               m.AwayScore,
               m.Status
        FROM dbo.FbMatch m WITH (READUNCOMMITTED)
        WHERE m.MatchTime >= @from AND m.MatchTime < @to
          AND m.HomeScore IS NOT NULL AND m.AwayScore IS NOT NULL
          AND (m.Status = 3 OR m.MatchTime <= DATEADD(minute, -150, SYSDATETIME()))
        ORDER BY m.MatchTime, m.MatchId
        """;
    File.WriteAllText(Path.Combine(notesDir, "01-matches.sql"), matchesSql);

    var finished = new List<FinMatch>();
    using (var db = Open(conn))
    using (var cmd = new SqlCommand(matchesSql, db))
    {
        cmd.CommandTimeout = 180;
        cmd.Parameters.AddWithValue("@from", from.Value);
        cmd.Parameters.AddWithValue("@to", to.Value);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            finished.Add(new FinMatch(
                r.GetInt64(0),
                r.IsDBNull(1) ? "" : r.GetString(1),
                r.IsDBNull(2) ? "" : r.GetString(2),
                r.IsDBNull(3) ? "" : r.GetString(3),
                r.GetDateTime(4),
                Convert.ToInt32(r.GetValue(5)),
                Convert.ToInt32(r.GetValue(6)),
                Convert.ToInt32(r.GetValue(7))));
        }
    }
    Log("export|finishedMatches=" + finished.Count.ToString(CultureInfo.InvariantCulture));

    var matchesPath = Path.Combine(exportDir, "finished-matches.tsv");
    using (var sw = new StreamWriter(matchesPath, false, Encoding.UTF8))
    {
        sw.WriteLine("# SELECT-only|kickoff=FbMatch.MatchTime|finished=scores+status3_or_kickoff+150");
        sw.WriteLine("matchId\tleague\thome\taway\tkickoff\thomeScore\tawayScore\tstatus");
        foreach (var m in finished)
            sw.WriteLine(string.Join('\t',
                m.Id.ToString(CultureInfo.InvariantCulture), m.League, m.Home, m.Away,
                Fmt(m.Kickoff), m.HomeScore.ToString(CultureInfo.InvariantCulture),
                m.AwayScore.ToString(CultureInfo.InvariantCulture),
                m.Status.ToString(CultureInfo.InvariantCulture)));
    }

    // ---- 2) 赛前 JQ 盘口（CapturedAt < MatchTime；窗口连接，不用 IN @ids）----
    var oddsSql = """
        SELECT h.MatchId, h.OptionCode, h.Odds, h.CapturedAt, m.MatchTime
        FROM dbo.FbJcOddsHistory h WITH (READUNCOMMITTED)
        INNER JOIN dbo.FbMatch m WITH (READUNCOMMITTED) ON m.MatchId = h.MatchId
        WHERE m.MatchTime >= @from AND m.MatchTime < @to
          AND m.HomeScore IS NOT NULL AND m.AwayScore IS NOT NULL
          AND (m.Status = 3 OR m.MatchTime <= DATEADD(minute, -150, SYSDATETIME()))
          AND h.PlayType = 'JQ'
          AND h.Odds > 1
          AND h.CapturedAt < m.MatchTime
        ORDER BY h.MatchId, h.CapturedAt, h.OptionCode
        """;
    File.WriteAllText(Path.Combine(notesDir, "02-prekick-jq-odds.sql"), oddsSql
        + "\n-- NOTE: 禁止 WHERE MatchId IN @ids；本查询用时间窗 JOIN，参数仅 @from/@to。\n");

    var oddsRows = new List<OddsRow>();
    using (var db = Open(conn))
    using (var cmd = new SqlCommand(oddsSql, db))
    {
        cmd.CommandTimeout = 600;
        cmd.Parameters.AddWithValue("@from", from.Value);
        cmd.Parameters.AddWithValue("@to", to.Value);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            oddsRows.Add(new OddsRow(
                r.GetInt64(0),
                r.GetString(1),
                r.GetDecimal(2),
                r.GetDateTime(3),
                r.GetDateTime(4)));
        }
    }
    Log("export|prekickJqOddsRows=" + oddsRows.Count.ToString(CultureInfo.InvariantCulture));

    var oddsPath = Path.Combine(exportDir, "prekick-jq-odds.tsv");
    using (var sw = new StreamWriter(oddsPath, false, Encoding.UTF8))
    {
        sw.WriteLine("# SELECT-only|CapturedAt<MatchTime|no-current-price-backfill|no-IN-@ids");
        sw.WriteLine("matchId\toptionCode\todds\tcapturedAt\tkickoff");
        foreach (var o in oddsRows)
            sw.WriteLine(string.Join('\t',
                o.MatchId.ToString(CultureInfo.InvariantCulture), o.Code,
                o.Odds.ToString("0.000", CultureInfo.InvariantCulture),
                Fmt(o.CapturedAt), Fmt(o.Kickoff)));
    }

    // 每场选「赛前最近、且八档齐全」的 CapturedAt
    var jqNeed = ResearchEvidenceBridge.JqOptionCodes;
    var snapshotByMatch = new Dictionary<long, Snap>();
    foreach (var g in oddsRows.GroupBy(x => x.MatchId))
    {
        var byCap = g.GroupBy(x => x.CapturedAt).OrderByDescending(x => x.Key);
        Snap? chosen = null;
        foreach (var cap in byCap)
        {
            var map = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var row in cap)
                map[row.Code] = row.Odds; // 同刻同码后者覆盖
            if (jqNeed.All(c => map.ContainsKey(c) && map[c] > 1m))
            {
                chosen = new Snap(g.Key, cap.Key, map, cap.First().Kickoff);
                break;
            }
        }
        if (chosen != null)
            snapshotByMatch[g.Key] = chosen;
    }
    Log("export|matchesWithFullPrekickJq8=" + snapshotByMatch.Count.ToString(CultureInfo.InvariantCulture));

    // ---- 3) 训练样本（拟合用；18 个月完场 + 150 分钟闸）----
    var histSql = """
        SELECT m.MatchId,
               ISNULL(m.LeagueName, '') AS League,
               ISNULL(m.HomeTeamName, '') AS Home,
               ISNULL(m.AwayTeamName, '') AS Away,
               m.HomeScore, m.AwayScore, m.MatchTime
        FROM dbo.FbMatch m WITH (READUNCOMMITTED)
        WHERE m.HomeScore IS NOT NULL AND m.AwayScore IS NOT NULL
          AND m.Status = 3
          AND m.MatchTime >= DATEADD(month, -18, SYSDATETIME())
          AND m.MatchTime <= DATEADD(minute, -150, SYSDATETIME())
        ORDER BY m.MatchTime, m.MatchId
        """;
    File.WriteAllText(Path.Combine(notesDir, "03-history-samples.sql"), histSql);

    var history = new List<ScoreSample>();
    using (var db = Open(conn))
    using (var cmd = new SqlCommand(histSql, db))
    {
        cmd.CommandTimeout = 300;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            history.Add(new ScoreSample
            {
                MatchId = r.GetInt64(0),
                LeagueName = r.IsDBNull(1) ? "" : r.GetString(1),
                HomeTeam = r.IsDBNull(2) ? "" : r.GetString(2),
                AwayTeam = r.IsDBNull(3) ? "" : r.GetString(3),
                HomeScore = Convert.ToInt32(r.GetValue(4)),
                AwayScore = Convert.ToInt32(r.GetValue(5)),
                MatchTime = r.GetDateTime(6)
            });
        }
    }
    Log("export|historySamples=" + history.Count.ToString(CultureInfo.InvariantCulture));
    var histPath = Path.Combine(exportDir, "history-samples.tsv");
    using (var sw = new StreamWriter(histPath, false, Encoding.UTF8))
    {
        sw.WriteLine("# SELECT-only|Status=3|MatchTime+150<=now|18m");
        sw.WriteLine("matchId\tleague\thome\taway\thomeScore\tawayScore\tmatchTime");
        foreach (var s in history)
            sw.WriteLine(string.Join('\t',
                s.MatchId.ToString(CultureInfo.InvariantCulture),
                s.LeagueName ?? "", s.HomeTeam, s.AwayTeam,
                s.HomeScore.ToString(CultureInfo.InvariantCulture),
                s.AwayScore.ToString(CultureInfo.InvariantCulture),
                Fmt(s.MatchTime!.Value)));
    }

    // ---- 4) 逐场资格审计（校准集）----
    var rejectCounts = new Dictionary<string, int>(StringComparer.Ordinal);
    void Reject(string reason)
    {
        rejectCounts[reason] = rejectCounts.TryGetValue(reason, out var c) ? c + 1 : 1;
    }

    var predicted = new List<SupportDistribution>();
    var actual = new List<(int Home, int Away)>();
    var qualifiedRows = new List<string>();
    var rejectRows = new List<string>();
    var seen = new HashSet<long>();

    // 缓存：按日 Fit 降频（同一日历日 asOf 复用模型）
    var modelCache = new Dictionary<DateTime, ScorelineModel>();

    foreach (var m in finished.OrderBy(x => x.Kickoff).ThenBy(x => x.Id))
    {
        if (!seen.Add(m.Id))
        {
            Reject("duplicate-matchId");
            rejectRows.Add(m.Id + "\tduplicate-matchId");
            continue;
        }
        if (!snapshotByMatch.TryGetValue(m.Id, out var snap))
        {
            Reject("no-full-prekick-jq8");
            rejectRows.Add(m.Id + "\tno-full-prekick-jq8");
            continue;
        }
        if (snap.CapturedAt >= m.Kickoff)
        {
            Reject("odds-not-before-kickoff");
            rejectRows.Add(m.Id + "\todds-not-before-kickoff");
            continue;
        }

        // asOf = 盘口捕获时刻（赛前）；训练集 Fit(asOf) 要求样本 MatchTime+150 <= asOf
        var asOf = snap.CapturedAt;
        var dayKey = asOf.Date;
        if (!modelCache.TryGetValue(dayKey, out var model))
        {
            model = ScorelineModel.Fit(history, asOf: asOf);
            modelCache[dayKey] = model;
        }
        if (!model.Ready)
        {
            Reject("scoreline-fit-not-ready");
            rejectRows.Add(m.Id + "\tscoreline-fit-not-ready|asOf=" + Fmt(asOf));
            continue;
        }

        var goals = model.ExpectedGoals(m.League, m.Home, m.Away, null);
        if (goals is null)
        {
            Reject("expected-goals-unavailable");
            rejectRows.Add(m.Id + "\texpected-goals-unavailable");
            continue;
        }

        var dist = FullSupportDixonColes.Create(goals.Value.Home, goals.Value.Away, model.Rho);
        if (!dist.Ok)
        {
            Reject("full-support-fail");
            rejectRows.Add(m.Id + "\tfull-support-fail|" + dist.Status);
            continue;
        }

        // 实际比分不得超出网格时 DistributionScore 会失败；Fit 侧只需 Ok 分布 + 比分对
        if (m.HomeScore < 0 || m.AwayScore < 0)
        {
            Reject("score-invalid");
            rejectRows.Add(m.Id + "\tscore-invalid");
            continue;
        }

        predicted.Add(dist);
        actual.Add((m.HomeScore, m.AwayScore));
        qualifiedRows.Add(string.Join('\t',
            m.Id.ToString(CultureInfo.InvariantCulture),
            Fmt(m.Kickoff), Fmt(asOf),
            goals.Value.Home.ToString("0.00000000", CultureInfo.InvariantCulture),
            goals.Value.Away.ToString("0.00000000", CultureInfo.InvariantCulture),
            model.Rho.ToString("0.000", CultureInfo.InvariantCulture),
            m.HomeScore.ToString(CultureInfo.InvariantCulture),
            m.AwayScore.ToString(CultureInfo.InvariantCulture),
            DecisionDelivery.MarketText(snap.Odds.Select(kv => (kv.Key, kv.Value)))));
    }

    var qualPath = Path.Combine(exportDir, "calibration-qualified.tsv");
    File.WriteAllText(qualPath,
        "# matchId\tkickoff\tasOf(=prekickCapturedAt)\tlambdaH\tlambdaA\trho\thome\taway\toddsSnapshot\n"
        + string.Join('\n', qualifiedRows) + "\n", Encoding.UTF8);
    var rejPath = Path.Combine(exportDir, "calibration-rejects.tsv");
    File.WriteAllText(rejPath,
        "# matchId\treason\n" + string.Join('\n', rejectRows) + "\n", Encoding.UTF8);

    var rejectSummary = string.Join(';', rejectCounts.OrderByDescending(kv => kv.Value)
        .Select(kv => kv.Key + "=" + kv.Value.ToString(CultureInfo.InvariantCulture)));
    Log("audit|uniqueFinished=" + seen.Count.ToString(CultureInfo.InvariantCulture)
        + "|qualifiedCal=" + predicted.Count.ToString(CultureInfo.InvariantCulture)
        + "|minCal=" + CandidateProtocol.MinCalibrationMatches.ToString(CultureInfo.InvariantCulture)
        + "|gapCal=" + Math.Max(0, CandidateProtocol.MinCalibrationMatches - predicted.Count).ToString(CultureInfo.InvariantCulture)
        + "|rejects=" + rejectSummary);

    // 未来独立验证窗：开赛仍在未来、且有赛前盘口的场 — 只统计，不冒充未见声明已归档
    var futureSql = """
        SELECT COUNT(DISTINCT m.MatchId)
        FROM dbo.FbMatch m WITH (READUNCOMMITTED)
        INNER JOIN dbo.FbJcOddsHistory h WITH (READUNCOMMITTED) ON h.MatchId = m.MatchId
        WHERE m.MatchTime > SYSDATETIME()
          AND h.PlayType = 'JQ' AND h.Odds > 1 AND h.CapturedAt < m.MatchTime
        """;
    int futureWithOdds;
    using (var db = Open(conn))
    using (var cmd = new SqlCommand(futureSql, db))
        futureWithOdds = Convert.ToInt32(cmd.ExecuteScalar());
    Log("future|matchesWithPrekickJq=" + futureWithOdds.ToString(CultureInfo.InvariantCulture)
        + "|minTest=" + CandidateProtocol.MinTestMatches.ToString(CultureInfo.InvariantCulture)
        + "|note=not-archived-declares|not-unseen-evidence");

    // ---- 5) 真实 Fit 或停止 ----
    var modelVersion = DecisionDelivery.RuntimeModel();
    var codeHash = BuildInfo.Version;
    var alpha = CandidateProtocol.AlphaInnerDefault;
    File.WriteAllText(Path.Combine(fitDir, "fingerprints.txt"),
        "ModelVersion=" + modelVersion + "\nCodeHash=" + codeHash
        + "\nPolicyVersion=" + CandidateProtocol.RuleVersion
        + "\nAlphaInnerDefault=" + alpha.ToString(CultureInfo.InvariantCulture)
        + "\nlabel=real-TemperatureFit-candidate|not-T1.10-fixture\n");

    if (predicted.Count < CandidateProtocol.MinCalibrationMatches)
    {
        Log("fit|skipped|reason=insufficient-matches|have="
            + predicted.Count.ToString(CultureInfo.InvariantCulture)
            + "|need=" + CandidateProtocol.MinCalibrationMatches.ToString(CultureInfo.InvariantCulture));
        Log("qualification|calibrated=false|algorithmQualified=false");
        Log("nextAction=continue-prekick-odds-sampling-and-wait-until-qualifiedCal>=400-then-rerun-this-tool");
        WriteHashes(outDir, exportDir, fitDir, notesDir);
        File.WriteAllText(Path.Combine(notesDir, "run-log.txt"), cmdLog.ToString());
        return 0; // 数据不足是如实结果，不是工具失败
    }

    var fit = TemperatureCalibrator.Fit(predicted, actual, alpha);
    Log("fit|status=" + fit.Status
        + "|T=" + fit.Temperature.ToString("0.00", CultureInfo.InvariantCulture)
        + "|alpha=" + fit.Alpha.ToString(CultureInfo.InvariantCulture)
        + "|objective=" + fit.Objective.ToString("0.00000000", CultureInfo.InvariantCulture)
        + "|matches=" + fit.Matches.ToString(CultureInfo.InvariantCulture));

    if (!fit.Ok)
    {
        Log("qualification|calibrated=false|fitFailed=" + fit.Status);
        Log("nextAction=inspect-fit-failure-then-rerun");
        File.WriteAllText(Path.Combine(notesDir, "run-log.txt"), cmdLog.ToString());
        WriteHashes(outDir, exportDir, fitDir, notesDir);
        return 1;
    }

    var fitOut = new StringBuilder();
    fitOut.AppendLine("status=" + fit.Status);
    fitOut.AppendLine("temperature=" + fit.Temperature.ToString("0.00", CultureInfo.InvariantCulture));
    fitOut.AppendLine("alpha=" + fit.Alpha.ToString(CultureInfo.InvariantCulture));
    fitOut.AppendLine("objective=" + fit.Objective.ToString("0.00000000", CultureInfo.InvariantCulture));
    fitOut.AppendLine("matches=" + fit.Matches.ToString(CultureInfo.InvariantCulture));
    fitOut.AppendLine("ModelVersion=" + modelVersion);
    fitOut.AppendLine("CodeHash=" + codeHash);
    fitOut.AppendLine("PolicyVersion=" + CandidateProtocol.RuleVersion);
    fitOut.AppendLine("ModelFingerprint=" + CandidateArchive.Hash(modelVersion));
    fitOut.AppendLine("CodeFingerprint=" + CandidateArchive.Hash(codeHash));
    fitOut.AppendLine("RuleFingerprint=" + CandidateArchive.Hash(CandidateProtocol.RuleVersion));
    fitOut.AppendLine("note=real-TemperatureCalibrator.Fit|not-fixture|not-fixed-T-1.10");
    fitOut.AppendLine("calSet=finished+fullPrekickJq8+scorelineReady|dedupe=matchId");
    fitOut.AppendLine("NOT=independent-unseen-validation|BoundEvidenceId-still-empty|Enabled-still-false");
    File.WriteAllText(Path.Combine(fitDir, "temperature-fit.txt"), fitOut.ToString());

    // 新验证窗冻结命令（不自动执行）
    var freezeCmd =
        "CandidateEvidenceOffline.exe freeze-meta <NEW_WINDOW_DIR>"
        + " EvidenceId=ev-real-unseen-<yyyyMMdd-HHmmss>-<uniq>"
        + " SampleRef=sample-real-unseen-<yyyyMMdd-HHmmss>-<uniq>"
        + " ModelVersion=\"" + modelVersion + "\""
        + " CodeHash=\"" + codeHash + "\""
        + " PolicyVersion=" + CandidateProtocol.RuleVersion
        + " TrainCutoff=<实际执行时刻ISO>"
        + " Note=post-real-fit|T=" + fit.Temperature.ToString("0.00", CultureInfo.InvariantCulture)
        + "|calMatches=" + fit.Matches.ToString(CultureInfo.InvariantCulture)
        + "|await-independent-unseen-400|do-not-move-old-declares";
    File.WriteAllText(Path.Combine(fitDir, "new-window-freeze-cmd.txt"),
        freezeCmd + "\n# 本轮不自动 freeze；旧模型声明不得搬入新窗。\n");

    Log("qualification|calibrated=true|algorithmQualified=false|needIndependentUnseen>=400");
    Log("nextAction=confirm-and-freeze-new-window-then-archive-prematch-declares-until-test>=400");
    File.WriteAllText(Path.Combine(notesDir, "run-log.txt"), cmdLog.ToString());
    WriteHashes(outDir, exportDir, fitDir, notesDir);
    return 0;
}

static SqlConnection Open(string conn)
{
    var db = new SqlConnection(conn);
    db.Open();
    using var gate = new SqlCommand("""
        IF EXISTS (
          SELECT 1 FROM sys.dm_exec_requests r
          CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t
          WHERE r.session_id = @@SPID AND (
            t.text LIKE '%INSERT %' OR t.text LIKE '%UPDATE %' OR t.text LIKE '%DELETE %'
            OR t.text LIKE '%MERGE %' OR t.text LIKE '%TRUNCATE %' OR t.text LIKE '%DROP %'
            OR t.text LIKE '%ALTER %' OR t.text LIKE '%CREATE %'
          )
        ) SELECT 1 ELSE SELECT 0
        """, db);
    // 会话级只读意图已在连接串；此处再拒绝非 SELECT 不现实（自身探测）。保持语句清单全为 SELECT。
    return db;
}

static string Fmt(DateTime dt)
    => dt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);

static void WriteHashes(string outDir, params string[] dirs)
{
    var lines = new List<string>();
    foreach (var dir in dirs.Prepend(outDir).Distinct())
    {
        if (!Directory.Exists(dir)) continue;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                     .Where(p => !p.EndsWith("SHA256.txt", StringComparison.OrdinalIgnoreCase)
                                 && !p.EndsWith("conn.readonly.env.txt", StringComparison.OrdinalIgnoreCase)
                                 && !p.EndsWith("console.out.txt", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var rel = Path.GetRelativePath(outDir, f).Replace('\\', '/');
            try
            {
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)));
                lines.Add(hash + "  " + rel);
            }
            catch (IOException)
            {
                // 跳过被外部占用的文件（如 Tee 中的 console.out），不阻断封存
            }
        }
    }
    File.WriteAllLines(Path.Combine(outDir, "SHA256.txt"), lines);
    Console.WriteLine("sha256|files=" + lines.Count.ToString(CultureInfo.InvariantCulture));
}

sealed record FinMatch(long Id, string League, string Home, string Away, DateTime Kickoff, int HomeScore, int AwayScore, int Status);
sealed record OddsRow(long MatchId, string Code, decimal Odds, DateTime CapturedAt, DateTime Kickoff);
sealed record Snap(long MatchId, DateTime CapturedAt, Dictionary<string, decimal> Odds, DateTime Kickoff);
