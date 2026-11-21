using System.Globalization;
using System.Text;
using AiFootBall.Core.Models;

namespace AiFootBall.Core.Models.Candidate;

/// <summary>
/// 研究链路：只读数据适配 → 候选 FullSupport+T（真实 Fit）预测 → 1810 未见窗归档/附果/资格摘要。
/// T 须等于 <see cref="DecisionDelivery.RuntimeTemperature"/>；不得与生产 milk 截断网格混用。
/// 校准完成 ≠ 算法合格；独立未见验证未达 MinTestMatches 前 qualified=false。
/// </summary>
public static class ResearchEvidenceBridge
{
    /// <summary>实际 Apply/归档温度；与 DecisionDelivery.RuntimeTemperature 同值（1942 Fit T=1.25）。</summary>
    public const double ResearchFrozenTemperature = DecisionDelivery.RuntimeTemperature;
    public const string CheckpointFileName = "research-checkpoint.tsv";
    public const string RejectLogFileName = "research-rejects.log";
    public const string UncalibratedLabel = "calibrated-awaiting-unseen-T1.25";

    public static readonly string[] JqOptionCodes = { "0", "1", "2", "3", "4", "5", "6", "7" };

    /* ---------- DTOs / sources ---------- */

    public sealed class OddsSnapshotRow
    {
        public long MatchId { get; init; }
        public string PlayType { get; init; } = "";
        public string OptionCode { get; init; } = "";
        public decimal Odds { get; init; }
        /// <summary>盘口捕获时刻（来自 FbJcOddsHistory.CapturedAt；不得用「当前最新价」回填缺失历史）。</summary>
        public DateTime CapturedAt { get; init; }
    }

    public sealed class MatchClockRow
    {
        public long MatchId { get; init; }
        public string League { get; init; } = "";
        public string Home { get; init; } = "";
        public string Away { get; init; } = "";
        /// <summary>开赛 = FbMatch.MatchTime。</summary>
        public DateTime Kickoff { get; init; }
        public decimal? OuLine { get; init; }
        public int? HomeScore { get; init; }
        public int? AwayScore { get; init; }
        public int Status { get; init; }
        public DateTime? SettledAt { get; init; }
    }

    public interface IResearchDataSource
    {
        IReadOnlyList<ScoreSample> LoadScoreSamples();
        IReadOnlyList<MatchClockRow> LoadMatches();
        IReadOnlyList<OddsSnapshotRow> LoadOdds();
    }

    /// <summary>文件主路径。TSV 跳过空行/#/表头。</summary>
    public sealed class FileResearchDataSource : IResearchDataSource
    {
        readonly string _historyPath;
        readonly string _matchesPath;
        readonly string _oddsPath;

        public FileResearchDataSource(string historyPath, string matchesPath, string oddsPath)
        {
            _historyPath = historyPath;
            _matchesPath = matchesPath;
            _oddsPath = oddsPath;
        }

        public IReadOnlyList<ScoreSample> LoadScoreSamples()
        {
            var list = new List<ScoreSample>();
            foreach (var line in ReadDataLines(_historyPath, "matchId"))
            {
                var p = line.Split('\t');
                if (p.Length < 7) throw new InvalidDataException("blocked:history-incomplete|" + line);
                if (!long.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mid))
                    throw new InvalidDataException("blocked:history-matchId|" + p[0]);
                if (!int.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hs)
                    || !int.TryParse(p[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var aw))
                    throw new InvalidDataException("blocked:history-score|" + line);
                if (!TryParseTime(p[6], out var mt))
                    throw new InvalidDataException("blocked:history-time|" + p[6]);
                list.Add(new ScoreSample
                {
                    MatchId = mid,
                    LeagueName = p[1],
                    HomeTeam = p[2],
                    AwayTeam = p[3],
                    HomeScore = hs,
                    AwayScore = aw,
                    MatchTime = mt
                });
            }
            return list;
        }

        public IReadOnlyList<MatchClockRow> LoadMatches()
        {
            var list = new List<MatchClockRow>();
            foreach (var line in ReadDataLines(_matchesPath, "matchId"))
            {
                var p = line.Split('\t');
                if (p.Length < 5) throw new InvalidDataException("blocked:matches-incomplete|" + line);
                if (!long.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mid))
                    throw new InvalidDataException("blocked:matches-matchId|" + p[0]);
                if (!TryParseTime(p[4], out var ko))
                    throw new InvalidDataException("blocked:matches-kickoff|" + p[4]);
                decimal? ou = null;
                if (p.Length > 5 && p[5].Length > 0
                    && decimal.TryParse(p[5], NumberStyles.Number, CultureInfo.InvariantCulture, out var ouV))
                    ou = ouV;
                int? hs = null, ascore = null;
                var status = 0;
                DateTime? settled = null;
                if (p.Length > 6 && int.TryParse(p[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hsv))
                    hs = hsv;
                if (p.Length > 7 && int.TryParse(p[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var asv))
                    ascore = asv;
                if (p.Length > 8 && int.TryParse(p[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var st))
                    status = st;
                if (p.Length > 9 && p[9].Length > 0 && TryParseTime(p[9], out var stt))
                    settled = stt;
                list.Add(new MatchClockRow
                {
                    MatchId = mid,
                    League = p[1],
                    Home = p[2],
                    Away = p[3],
                    Kickoff = ko,
                    OuLine = ou,
                    HomeScore = hs,
                    AwayScore = ascore,
                    Status = status,
                    SettledAt = settled
                });
            }
            return list;
        }

        public IReadOnlyList<OddsSnapshotRow> LoadOdds()
        {
            var list = new List<OddsSnapshotRow>();
            foreach (var line in ReadDataLines(_oddsPath, "matchId"))
            {
                var p = line.Split('\t');
                if (p.Length < 5) throw new InvalidDataException("blocked:odds-incomplete|" + line);
                if (!long.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mid))
                    throw new InvalidDataException("blocked:odds-matchId|" + p[0]);
                if (!decimal.TryParse(p[3], NumberStyles.Number, CultureInfo.InvariantCulture, out var odds))
                    throw new InvalidDataException("blocked:odds-price|" + p[3]);
                if (!TryParseTime(p[4], out var at))
                    throw new InvalidDataException("blocked:odds-capturedAt|" + p[4]);
                list.Add(new OddsSnapshotRow
                {
                    MatchId = mid,
                    PlayType = p[1],
                    OptionCode = p[2],
                    Odds = odds,
                    CapturedAt = at
                });
            }
            return list;
        }
    }

    /// <summary>只读 SQL 文本（CLI 注入连接串执行；Core 不引用 SqlClient）。禁止用当前价回填历史。</summary>
    public static class ResearchSqlQueries
    {
        public const string MatchSelect = """
            SELECT m.MatchId, ISNULL(m.LeagueName, '') AS League, ISNULL(m.HomeTeamName, '') AS Home, ISNULL(m.AwayTeamName, '') AS Away,
                   m.MatchTime AS Kickoff, CAST(NULL AS decimal(8,2)) AS OuLine,
                   m.HomeScore, m.AwayScore, m.Status,
                   CASE WHEN m.Status = 3 OR (m.HomeScore IS NOT NULL AND m.AwayScore IS NOT NULL AND m.MatchTime <= DATEADD(minute, -150, SYSUTCDATETIME()))
                        THEN COALESCE(m.UpdatedAt, m.MatchTime) ELSE NULL END AS SettledAt
            FROM dbo.FbMatch m WITH (READUNCOMMITTED)
            WHERE m.MatchTime >= @from AND m.MatchTime < @to
            """;

        /// <summary>
        /// 赛前盘口导出：时间窗 JOIN，禁止 <c>WHERE MatchId IN @ids</c>（SqlCommand 无法绑定列表）。
        /// 资格：CapturedAt &lt; MatchTime；不得用当前价回填。
        /// </summary>
        public const string OddsHistorySelect = """
            SELECT h.MatchId, h.PlayType, h.OptionCode, h.Odds, h.CapturedAt, m.MatchTime
            FROM dbo.FbJcOddsHistory h WITH (READUNCOMMITTED)
            INNER JOIN dbo.FbMatch m WITH (READUNCOMMITTED) ON m.MatchId = h.MatchId
            WHERE m.MatchTime >= @from AND m.MatchTime < @to
              AND h.PlayType = 'JQ' AND h.Odds > 1
              AND h.CapturedAt < m.MatchTime
            ORDER BY h.MatchId, h.CapturedAt, h.OptionCode
            """;

        public const string ScoreSamplesSelect = """
            SELECT m.MatchId, ISNULL(m.LeagueName, '') AS League, ISNULL(m.HomeTeamName, '') AS Home, ISNULL(m.AwayTeamName, '') AS Away,
                   m.HomeScore, m.AwayScore, m.MatchTime
            FROM dbo.FbMatch m WITH (READUNCOMMITTED)
            WHERE m.HomeScore IS NOT NULL AND m.AwayScore IS NOT NULL
              AND m.MatchTime <= DATEADD(minute, -150, @trainCutoff)
            ORDER BY m.MatchTime
            """;

        public const string ConnectionHint =
            "ApplicationIntent=ReadOnly; 仅 SELECT；不得 UPDATE/INSERT；不得用当前盘口回填缺失 CapturedAt。";
    }

    /* ---------- compat ---------- */

    public sealed class CompatResult
    {
        public bool Compatible { get; init; }
        public string Status { get; init; } = "";
        public string ExpectedModel { get; init; } = "";
        public string ExpectedCode { get; init; } = "";
        public string WindowModel { get; init; } = "";
        public string WindowCode { get; init; } = "";
        public string EvidenceId { get; init; } = "";
    }

    public static CompatResult CompatCheck(string windowDirectory)
    {
        var expectedModel = DecisionDelivery.RuntimeModel();
        var expectedCode = BuildInfo.Version;
        var integ = EvidenceWindowOffline.CheckWindowMetaIntegrity(windowDirectory);
        if (!integ.StartsWith("window-meta:integrity-ok|", StringComparison.Ordinal)
            && integ != "window-meta:absent")
        {
            return new CompatResult
            {
                Compatible = false,
                Status = integ,
                ExpectedModel = expectedModel,
                ExpectedCode = expectedCode
            };
        }
        if (integ == "window-meta:absent")
            return new CompatResult
            {
                Compatible = false,
                Status = "blocked:window-meta-missing",
                ExpectedModel = expectedModel,
                ExpectedCode = expectedCode
            };

        var read = EvidenceWindowOffline.ReadMeta(windowDirectory, out var meta);
        if (!read.StartsWith("window-meta:ok", StringComparison.Ordinal))
            return new CompatResult { Compatible = false, Status = read, ExpectedModel = expectedModel, ExpectedCode = expectedCode };

        var wm = meta.GetValueOrDefault("ModelVersion") ?? "";
        var wc = meta.GetValueOrDefault("CodeHash") ?? "";
        var eid = meta.GetValueOrDefault("EvidenceId") ?? "";
        var ok = string.Equals(wm, expectedModel, StringComparison.Ordinal)
                 && string.Equals(wc, expectedCode, StringComparison.Ordinal)
                 && string.Equals(meta.GetValueOrDefault("PolicyVersion"), CandidateProtocol.RuleVersion, StringComparison.Ordinal);
        var reason = ok
            ? "compat:ok|uncalibrated=" + UncalibratedLabel
            : "compat:incompatible|reason=model-or-code-mismatch|hint=1825-prod-build-tag-not-candidate-runtime";
        return new CompatResult
        {
            Compatible = ok,
            Status = reason,
            ExpectedModel = expectedModel,
            ExpectedCode = expectedCode,
            WindowModel = wm,
            WindowCode = wc,
            EvidenceId = eid
        };
    }

    public static string NewWindowFreezeCommandTemplate()
    {
        var model = DecisionDelivery.RuntimeModel();
        var code = BuildInfo.Version;
        return "CandidateEvidenceOffline.exe freeze-meta <NEW_WINDOW_DIR>"
               + " EvidenceId=ev-real-unseen-<yyyyMMdd-HHmmss>-<uniq>"
               + " SampleRef=sample-real-unseen-<yyyyMMdd-HHmmss>-<uniq>"
               + " ModelVersion=" + QuoteArg(model)
               + " CodeHash=" + QuoteArg(code)
               + " PolicyVersion=" + CandidateProtocol.RuleVersion
               + " TrainCutoff=<实际执行时刻 ISO>"
               + " Note=candidate-research|" + UncalibratedLabel
               + "|T=" + ResearchFrozenTemperature.ToString("0.00", CultureInfo.InvariantCulture)
               + "|await-independent-unseen-400|not-qualified";
    }

    static string QuoteArg(string v)
        => v.Contains(' ') || v.Contains('|') ? "\"" + v.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : v;

    /* ---------- predict + archive ---------- */

    public sealed class PredictOptions
    {
        public string WindowDirectory { get; init; } = "";
        public IResearchDataSource Source { get; init; } = null!;
        public DateTime AsOf { get; init; }
        public string Play { get; init; } = "JQ";
        public bool DryRun { get; init; }
        public string RunIdPrefix { get; init; } = "research-declare";
    }

    public sealed class PredictBatchResult
    {
        public string Status { get; set; } = "";
        public int Attempted { get; set; }
        public int Archived { get; set; }
        public int IdempotentSkipped { get; set; }
        public int Rejected { get; set; }
        public List<string> RejectReasons { get; } = new();
    }

    public static PredictBatchResult PredictAndArchive(PredictOptions opt)
    {
        var result = new PredictBatchResult();
        var integ = EvidenceWindowOffline.RequireWindowMetaIntegrity(opt.WindowDirectory);
        if (!integ.StartsWith("window-meta:integrity-ok|", StringComparison.Ordinal))
        {
            result.Status = integ;
            return result;
        }
        var compat = CompatCheck(opt.WindowDirectory);
        if (!compat.Compatible)
        {
            result.Status = compat.Status;
            return result;
        }
        var read = EvidenceWindowOffline.ReadMeta(opt.WindowDirectory, out var meta);
        if (!read.StartsWith("window-meta:ok", StringComparison.Ordinal))
        {
            result.Status = read;
            return result;
        }
        if (!TryParseTime(meta["TrainCutoff"], out var trainCutoff))
        {
            result.Status = "blocked:train-cutoff-invalid";
            return result;
        }
        if (!File.Exists(Path.Combine(opt.WindowDirectory, CandidateArchive.TrainRosterFileName)))
        {
            result.Status = "blocked:train-roster-missing";
            return result;
        }

        var samples = opt.Source.LoadScoreSamples();
        var model = ScorelineModel.Fit(samples, asOf: trainCutoff);
        if (!model.Ready)
        {
            result.Status = "blocked:scoreline-fit-not-ready|samples=" + samples.Count.ToString(CultureInfo.InvariantCulture);
            return result;
        }

        var matches = opt.Source.LoadMatches();
        var oddsAll = opt.Source.LoadOdds();
        var existing = LoadExistingDeclareIndex(opt.WindowDirectory);
        var checkpoint = LoadCheckpoint(opt.WindowDirectory);

        foreach (var match in matches)
        {
            result.Attempted++;
            var one = TryPredictOne(opt, meta, trainCutoff, model, match, oddsAll, existing);
            if (one.StartsWith("declare:archived|", StringComparison.Ordinal))
            {
                result.Archived++;
                checkpoint[match.MatchId] = "archived";
            }
            else if (one.StartsWith("declare:idempotent|", StringComparison.Ordinal))
            {
                result.IdempotentSkipped++;
                checkpoint[match.MatchId] = "idempotent";
            }
            else
            {
                result.Rejected++;
                result.RejectReasons.Add(match.MatchId.ToString(CultureInfo.InvariantCulture) + "|" + one);
                AppendReject(opt.WindowDirectory, match.MatchId, one);
                checkpoint[match.MatchId] = one;
            }
        }

        if (!opt.DryRun)
            SaveCheckpoint(opt.WindowDirectory, checkpoint);

        result.Status = "predict-archive|"
                        + "attempted=" + result.Attempted.ToString(CultureInfo.InvariantCulture)
                        + "|archived=" + result.Archived.ToString(CultureInfo.InvariantCulture)
                        + "|idempotent=" + result.IdempotentSkipped.ToString(CultureInfo.InvariantCulture)
                        + "|rejected=" + result.Rejected.ToString(CultureInfo.InvariantCulture)
                        + "|label=" + UncalibratedLabel
                        + "|model=" + DecisionDelivery.RuntimeModel();
        return result;
    }

    static string TryPredictOne(
        PredictOptions opt,
        Dictionary<string, string> meta,
        DateTime trainCutoff,
        ScorelineModel model,
        MatchClockRow match,
        IReadOnlyList<OddsSnapshotRow> oddsAll,
        Dictionary<long, string> existingIdentity)
    {
        if (match.Kickoff <= opt.AsOf)
            return "blocked:kickoff-not-future";
        if (match.Kickoff <= trainCutoff)
            return "blocked:kickoff-before-train-cutoff";

        var jqOdds = oddsAll
            .Where(o => o.MatchId == match.MatchId
                        && string.Equals(o.PlayType, opt.Play, StringComparison.OrdinalIgnoreCase)
                        && o.CapturedAt <= opt.AsOf
                        && o.Odds > 1m)
            .ToList();
        if (jqOdds.Count == 0)
            return "blocked:odds-missing";

        // 每个 option 取 asOf 前最近一次 CapturedAt（禁止用更晚「当前价」）。
        var latestByOption = jqOdds
            .GroupBy(o => o.OptionCode, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(x => x.CapturedAt).First())
            .ToList();
        var capturedAt = latestByOption.Max(o => o.CapturedAt);
        if (capturedAt > opt.AsOf)
            return "blocked:odds-after-predict";
        if (capturedAt > match.Kickoff)
            return "blocked:odds-after-kickoff";

        var snapshot = DecisionDelivery.MarketText(latestByOption.Select(o => (o.OptionCode, o.Odds)));
        var mstat = CandidateArchive.MarketStatus(opt.Play, snapshot);
        if (mstat != null)
            return mstat;

        var goals = model.ExpectedGoals(match.League, match.Home, match.Away, match.OuLine);
        if (goals is null)
            return "blocked:expected-goals-unavailable";

        var raw = FullSupportDixonColes.Create(goals.Value.Home, goals.Value.Away, model.Rho);
        if (!raw.Ok)
            return "blocked:full-support|" + raw.Status;
        var scaled = TemperatureCalibrator.Apply(raw, ResearchFrozenTemperature);
        if (!scaled.Ok)
            return "blocked:temperature-apply|" + scaled.Status;
        var margin = PlayMargins.AggregateGoals(scaled);
        if (!margin.Ok)
            return "blocked:margin|" + margin.Status;

        string chosen = "";
        double chosenP = -1;
        decimal chosenOdds = 0;
        foreach (var row in latestByOption)
        {
            var idx = Array.IndexOf(JqOptionCodes, row.OptionCode);
            if (idx < 0 || idx >= margin.Values.Length) continue;
            var p = margin.Values[idx];
            if (!double.IsFinite(p) || p <= 0) continue;
            if (p > chosenP)
            {
                chosen = row.OptionCode;
                chosenP = p;
                chosenOdds = row.Odds;
            }
        }
        if (chosen.Length == 0)
            return "blocked:selection-empty";

        var declaredAt = capturedAt <= opt.AsOf ? capturedAt : opt.AsOf;
        // declaredAt ≤ oddsCapturedAt ≤ asOf < kickoff
        if (declaredAt > capturedAt)
            declaredAt = capturedAt;

        var rowArchive = new ArchiveRow
        {
            RunId = opt.RunIdPrefix + "-" + match.MatchId.ToString(CultureInfo.InvariantCulture),
            MatchId = match.MatchId,
            LambdaHome = goals.Value.Home,
            LambdaAway = goals.Value.Away,
            Rho = model.Rho,
            Temperature = ResearchFrozenTemperature,
            StoredProbability = chosenP,
            Play = opt.Play,
            Option = chosen,
            Odds = chosenOdds,
            OddsSnapshot = snapshot,
            DeclaredAt = FormatTime(declaredAt),
            OddsCapturedAt = FormatTime(capturedAt),
            AsOf = FormatTime(opt.AsOf),
            Kickoff = FormatTime(match.Kickoff),
            TrainCutoff = meta["TrainCutoff"],
            EvidenceId = meta["EvidenceId"],
            SampleRef = meta["SampleRef"],
            ModelVersion = meta["ModelVersion"],
            CodeHash = meta["CodeHash"],
            RuleVersion = meta["PolicyVersion"],
            Lane = "research",
            Decision = "observe",
            Reason = UncalibratedLabel
        };

        string identity;
        try
        {
            var probeLine = CandidateArchive.FormatLine(rowArchive, CalibratorBinding.KindPredeclaredUnseen);
            identity = ExtractField(probeLine, "identityHash") ?? "";
        }
        catch (InvalidDataException ex)
        {
            return "blocked:" + ex.Message;
        }

        if (existingIdentity.TryGetValue(match.MatchId, out var prev))
        {
            if (string.Equals(prev, identity, StringComparison.Ordinal))
                return "declare:idempotent|matchId=" + match.MatchId.ToString(CultureInfo.InvariantCulture);
            return "blocked:declare-conflict|matchId=" + match.MatchId.ToString(CultureInfo.InvariantCulture);
        }

        // checkpoint 仅辅助进度；幂等以 archive.log identity 为准。若仅有 checkpoint 无 archive，允许重写以恢复中断。
        if (opt.DryRun)
            return "declare:archived|dry-run|matchId=" + match.MatchId.ToString(CultureInfo.InvariantCulture);

        try
        {
            CandidateArchive.WritePredeclaredUnseen(opt.WindowDirectory, rowArchive);
            existingIdentity[match.MatchId] = identity;
            return "declare:archived|matchId=" + match.MatchId.ToString(CultureInfo.InvariantCulture);
        }
        catch (InvalidDataException ex)
        {
            return "blocked:" + ex.Message;
        }
    }

    /* ---------- attach results from source ---------- */

    public static string AttachFinishedFromSource(string windowDirectory, IResearchDataSource source, bool dryRun)
    {
        var integ = EvidenceWindowOffline.RequireWindowMetaIntegrity(windowDirectory);
        if (!integ.StartsWith("window-meta:integrity-ok|", StringComparison.Ordinal))
            return integ;
        var declares = LoadExistingDeclareIndex(windowDirectory);
        var matches = source.LoadMatches().ToDictionary(m => m.MatchId);
        var n = 0;
        var rejects = new List<string>();
        foreach (var mid in declares.Keys)
        {
            if (!matches.TryGetValue(mid, out var m))
            {
                rejects.Add(mid + "|blocked:match-missing");
                continue;
            }
            if (m.HomeScore is null || m.AwayScore is null)
            {
                rejects.Add(mid + "|blocked:result-not-ready");
                continue;
            }
            var settled = m.SettledAt ?? m.Kickoff.AddMinutes(ScorelineModel.MatchDurationMinutes);
            if (dryRun)
            {
                n++;
                continue;
            }
            var status = CandidateArchive.AttachResult(windowDirectory, mid, m.HomeScore.Value, m.AwayScore.Value, FormatTime(settled));
            if (status == "result:attached")
                n++;
            else if (status.StartsWith("blocked:results-duplicate", StringComparison.Ordinal)
                     || status.Contains("results-duplicate", StringComparison.Ordinal))
                n++; // idempotent attach
            else
                rejects.Add(mid + "|" + status);
        }
        return "attach-results|attached=" + n.ToString(CultureInfo.InvariantCulture)
               + "|rejected=" + rejects.Count.ToString(CultureInfo.InvariantCulture)
               + (rejects.Count > 0 ? "|first=" + rejects[0] : "");
    }

    /* ---------- status / qualification ---------- */

    public sealed class QualificationStatus
    {
        public string StatusLine { get; set; } = "";
        public int TrainRosterMatches { get; set; }
        public int DeclareMatches { get; set; }
        public int ResultsAttached { get; set; }
        public int MinCalibration { get; set; } = CandidateProtocol.MinCalibrationMatches;
        public int MinTest { get; set; } = CandidateProtocol.MinTestMatches;
        public int GapCalibration { get; set; }
        public int GapTest { get; set; }
        public int GapResults { get; set; }
        public bool CompatOk { get; set; }
        public string CalibrationNote { get; set; } = "";
        public List<string> VersionGroups { get; set; } = new();
        public List<string> RejectSamples { get; set; } = new();
    }

    public static QualificationStatus Status(string windowDirectory)
    {
        var q = new QualificationStatus();
        var compat = CompatCheck(windowDirectory);
        q.CompatOk = compat.Compatible;

        var trainPath = Path.Combine(windowDirectory, CandidateArchive.TrainRosterFileName);
        if (File.Exists(trainPath))
        {
            var ids = new HashSet<long>();
            foreach (var raw in File.ReadAllLines(trainPath))
            {
                if (!EvidenceWindowOffline.TryConsumeImportLine(raw, "matchId", out var line)) continue;
                var p = line.Split('\t');
                if (p.Length >= 1
                    && long.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mid))
                    ids.Add(mid);
            }
            q.TrainRosterMatches = ids.Count;
        }

        var groups = new Dictionary<string, int>(StringComparer.Ordinal);
        var declareIds = new HashSet<long>();
        var log = Path.Combine(windowDirectory, "archive.log");
        if (File.Exists(log))
        {
            foreach (var line in File.ReadAllLines(log))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var map = ParseFieldsPublic(line);
                if (map == null) continue;
                if (long.TryParse(map.GetValueOrDefault("matchId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mid))
                    declareIds.Add(mid);
                var key = (map.GetValueOrDefault("modelVersion") ?? "")
                          + "|" + (map.GetValueOrDefault("codeVersion") ?? "")
                          + "|" + (map.GetValueOrDefault("evidenceId") ?? "");
                groups[key] = groups.TryGetValue(key, out var c) ? c + 1 : 1;
            }
        }
        q.DeclareMatches = declareIds.Count;
        q.VersionGroups = groups.Select(kv => kv.Key + "=>" + kv.Value.ToString(CultureInfo.InvariantCulture)).ToList();

        var resultsPath = Path.Combine(windowDirectory, "results.log");
        var attached = new HashSet<long>();
        if (File.Exists(resultsPath))
        {
            foreach (var line in File.ReadAllLines(resultsPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var map = ParseFieldsPublic(line);
                if (map == null) continue;
                if (long.TryParse(map.GetValueOrDefault("matchId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mid))
                    attached.Add(mid);
            }
        }
        q.ResultsAttached = attached.Count(id => declareIds.Contains(id));

        q.GapCalibration = Math.Max(0, q.MinCalibration - q.TrainRosterMatches);
        q.GapTest = Math.Max(0, q.MinTest - q.DeclareMatches);
        q.GapResults = Math.Max(0, q.DeclareMatches - q.ResultsAttached);
        q.CalibrationNote = UncalibratedLabel
                            + "|real-TemperatureFit-T="
                            + ResearchFrozenTemperature.ToString("0.00", CultureInfo.InvariantCulture)
                            + "|fixtureFit-forbidden|algorithmQualified=false";

        var rejectPath = Path.Combine(windowDirectory, RejectLogFileName);
        if (File.Exists(rejectPath))
            q.RejectSamples = File.ReadAllLines(rejectPath).TakeLast(20).ToList();

        q.StatusLine = "status|"
                       + "compat=" + (q.CompatOk ? "ok" : "no")
                       + "|train=" + q.TrainRosterMatches.ToString(CultureInfo.InvariantCulture)
                       + "|declares=" + q.DeclareMatches.ToString(CultureInfo.InvariantCulture)
                       + "|results=" + q.ResultsAttached.ToString(CultureInfo.InvariantCulture)
                       + "|gapCal=" + q.GapCalibration.ToString(CultureInfo.InvariantCulture)
                       + "|gapTest=" + q.GapTest.ToString(CultureInfo.InvariantCulture)
                       + "|gapResults=" + q.GapResults.ToString(CultureInfo.InvariantCulture)
                       + "|minCal=" + q.MinCalibration.ToString(CultureInfo.InvariantCulture)
                       + "|minTest=" + q.MinTest.ToString(CultureInfo.InvariantCulture)
                       + "|qualified=false"
                       + "|note=" + q.CalibrationNote;
        return q;
    }

    /* ---------- helpers ---------- */

    public static IEnumerable<string> ReadDataLines(string path, string headerStartsWith)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(path);
        foreach (var raw in File.ReadAllLines(path))
        {
            if (!EvidenceWindowOffline.TryConsumeImportLine(raw, headerStartsWith, out var line))
                continue;
            yield return line;
        }
    }

    public static bool TryParseTime(string text, out DateTime value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value)
               || DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out value)
               || DateTime.TryParse(text, out value);
    }

    public static string FormatTime(DateTime dt)
        => dt.ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);

    static Dictionary<string, string>? ParseFieldsPublic(string line)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in line.Split('\t'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq];
            if (map.ContainsKey(key)) return null;
            map[key] = part[(eq + 1)..];
        }
        return map;
    }

    static string? ExtractField(string line, string key)
    {
        var map = ParseFieldsPublic(line);
        return map?.GetValueOrDefault(key);
    }

    static Dictionary<long, string> LoadExistingDeclareIndex(string directory)
    {
        var map = new Dictionary<long, string>();
        var log = Path.Combine(directory, "archive.log");
        if (!File.Exists(log)) return map;
        foreach (var line in File.ReadAllLines(log))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = ParseFieldsPublic(line);
            if (fields == null) continue;
            if (!long.TryParse(fields.GetValueOrDefault("matchId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mid))
                continue;
            map[mid] = fields.GetValueOrDefault("identityHash") ?? "";
        }
        return map;
    }

    static Dictionary<long, string> LoadCheckpoint(string directory)
    {
        var map = new Dictionary<long, string>();
        var path = Path.Combine(directory, CheckpointFileName);
        if (!File.Exists(path)) return map;
        foreach (var raw in File.ReadAllLines(path))
        {
            if (!EvidenceWindowOffline.TryConsumeImportLine(raw, "matchId", out var line)) continue;
            var p = line.Split('\t');
            if (p.Length < 2) continue;
            if (long.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mid))
                map[mid] = p[1];
        }
        return map;
    }

    static void SaveCheckpoint(string directory, Dictionary<long, string> map)
    {
        var sb = new StringBuilder();
        sb.Append("matchId\tstatus\n");
        foreach (var kv in map.OrderBy(x => x.Key))
            sb.Append(kv.Key.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(kv.Value).Append('\n');
        File.WriteAllText(Path.Combine(directory, CheckpointFileName), sb.ToString());
    }

    static void AppendReject(string directory, long matchId, string reason)
    {
        Directory.CreateDirectory(directory);
        File.AppendAllText(Path.Combine(directory, RejectLogFileName),
            FormatTime(DateTime.Now) + "\t" + matchId.ToString(CultureInfo.InvariantCulture) + "\t" + reason + "\n");
    }
}
