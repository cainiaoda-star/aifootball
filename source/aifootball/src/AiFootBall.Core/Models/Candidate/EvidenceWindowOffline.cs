using System.Globalization;
using System.Text;

namespace AiFootBall.Core.Models.Candidate;

/// <summary>
/// 离线窗文件入口：冻结元数据 → 导入训练名册/赛前声明 → 附赛果 → 评价/建证/绑定校验。
/// 复用 CandidateArchive / CalibratorBinding 同一套守卫；不连接数据库、不部署。
/// 夹具与样例仅证明机制，不是真实未见证据。
/// 本地 MetaHash 仅为冻结后防篡改摘要，不等于可信时间戳，不能证明历史上已赛前声明。
/// </summary>
public static class EvidenceWindowOffline
{
    public const string DeclaresImportName = "declares.import.tsv";
    public const string ResultsImportName = "results.import.tsv";
    public const string TrainImportName = "train.import.tsv";
    public const string StatusFileName = "offline-status.txt";
    public const string WindowMetaIntegrityFileName = "window-meta.integrity";

    static readonly string[] MetaSealKeys =
    {
        "EvidenceId", "SampleRef", "ModelVersion", "CodeHash", "PolicyVersion", "TrainCutoff", "Note",
        "FrozenAt", "MechanismOnly"
    };

    public static string FreezeMeta(string directory, IReadOnlyDictionary<string, string> meta)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, CandidateArchive.WindowMetaFileName);
        var integrityPath = Path.Combine(directory, WindowMetaIntegrityFileName);
        if (File.Exists(path) || File.Exists(integrityPath))
            return "blocked:window-meta-already-frozen";
        foreach (var key in new[] { "EvidenceId", "SampleRef", "ModelVersion", "CodeHash", "PolicyVersion", "TrainCutoff" })
            if (!meta.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v))
                return "blocked:window-meta-incomplete|" + key;
        if (!string.Equals(meta["PolicyVersion"], CandidateProtocol.RuleVersion, StringComparison.Ordinal))
            return "blocked:rule-version-mismatch";
        if (CandidateArchive.IsUnqualifiedEvidenceIdPublic(meta["EvidenceId"]))
            return "blocked:evidence-id-unqualified";

        var sealedMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in new[] { "EvidenceId", "SampleRef", "ModelVersion", "CodeHash", "PolicyVersion", "TrainCutoff" })
            sealedMap[key] = meta[key];
        if (meta.TryGetValue("Note", out var note) && !string.IsNullOrWhiteSpace(note))
            sealedMap["Note"] = note;
        sealedMap["FrozenAt"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        sealedMap["MechanismOnly"] = "true";

        var canon = CanonicalMetaBody(sealedMap);
        var metaHash = CandidateArchive.Hash(canon);
        var sb = new StringBuilder();
        sb.Append(canon);
        // MetaHash 写入正文便于人工阅读；完整性以 integrity 文件为准，重算时排除本行。
        sb.Append("MetaHash=").Append(metaHash).Append('\n');
        File.WriteAllText(path, sb.ToString());
        File.WriteAllText(integrityPath, "metaHash=" + metaHash + "\n");
        return "window-meta:frozen|evidenceId=" + meta["EvidenceId"] + "|metaHash=" + metaHash;
    }

    static string CanonicalMetaBody(IReadOnlyDictionary<string, string> map)
    {
        var sb = new StringBuilder();
        foreach (var key in MetaSealKeys)
        {
            if (!map.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                continue;
            sb.Append(key).Append('=').Append(value).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// 校验冻结元数据未被改写。本地摘要 ≠ 可信时间戳。
    /// absent：无元数据（允许纯 archive 单测路径）。
    /// </summary>
    public static string CheckWindowMetaIntegrity(string directory)
    {
        var metaPath = Path.Combine(directory, CandidateArchive.WindowMetaFileName);
        var integrityPath = Path.Combine(directory, WindowMetaIntegrityFileName);
        var metaExists = File.Exists(metaPath);
        var integExists = File.Exists(integrityPath);
        if (!metaExists && !integExists)
            return "window-meta:absent";
        if (!metaExists) return "blocked:window-meta-missing";
        if (!integExists) return "blocked:window-meta-integrity-missing";

        var read = ReadMetaRaw(directory, out var map);
        if (!read.StartsWith("window-meta:ok", StringComparison.Ordinal))
            return read;
        var canon = CanonicalMetaBody(map);
        var live = CandidateArchive.Hash(canon);
        var stored = "";
        foreach (var line in File.ReadAllLines(integrityPath))
        {
            var t = line.Trim();
            if (t.StartsWith("metaHash=", StringComparison.OrdinalIgnoreCase))
                stored = t["metaHash=".Length..].Trim();
        }
        if (!CalibratorBinding.IsSha256Hex(stored))
            return "blocked:window-meta-integrity-invalid";
        if (!string.Equals(live, stored, StringComparison.OrdinalIgnoreCase))
            return "blocked:window-meta-integrity-mismatch";
        if (map.TryGetValue("MetaHash", out var embedded)
            && embedded.Length > 0
            && !string.Equals(embedded, stored, StringComparison.OrdinalIgnoreCase))
            return "blocked:window-meta-integrity-mismatch";
        return "window-meta:integrity-ok|metaHash=" + stored;
    }

    public static string RequireWindowMetaIntegrity(string directory)
    {
        var status = CheckWindowMetaIntegrity(directory);
        if (status.StartsWith("window-meta:integrity-ok|", StringComparison.Ordinal))
            return status;
        if (status == "window-meta:absent")
            return "blocked:window-meta-missing";
        return status;
    }

    public static string ReadMeta(string directory, out Dictionary<string, string> meta)
    {
        var integ = RequireWindowMetaIntegrity(directory);
        if (!integ.StartsWith("window-meta:integrity-ok|", StringComparison.Ordinal))
        {
            meta = new Dictionary<string, string>(StringComparer.Ordinal);
            return integ;
        }
        return ReadMetaRaw(directory, out meta);
    }

    static string ReadMetaRaw(string directory, out Dictionary<string, string> meta)
    {
        meta = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = Path.Combine(directory, CandidateArchive.WindowMetaFileName);
        if (!File.Exists(path)) return "blocked:window-meta-missing";
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            meta[line[..eq]] = line[(eq + 1)..];
        }
        foreach (var key in new[] { "EvidenceId", "SampleRef", "ModelVersion", "CodeHash", "PolicyVersion", "TrainCutoff" })
            if (!meta.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v))
                return "blocked:window-meta-incomplete|" + key;
        return "window-meta:ok";
    }

    /// <summary>空行、# 注释、表头跳过；其余数据行原样交给调用方。非法数据行不得静默吞掉。</summary>
    public static bool TryConsumeImportLine(string raw, string headerStartsWith, out string dataLine)
    {
        dataLine = (raw ?? "").Trim();
        if (dataLine.Length == 0) return false;
        if (dataLine[0] == '#') return false;
        if (dataLine.StartsWith(headerStartsWith, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    public static string ImportTrainRoster(string directory, string importPath)
    {
        var metaStatus = ReadMeta(directory, out _);
        if (!metaStatus.StartsWith("window-meta:ok", StringComparison.Ordinal))
            return metaStatus;
        if (!File.Exists(importPath)) return "blocked:train-import-missing";
        var rows = new List<TrainRosterRow>();
        foreach (var raw in File.ReadAllLines(importPath))
        {
            if (!TryConsumeImportLine(raw, "matchId", out var line)) continue;
            var p = line.Split('\t');
            if (p.Length < 4) return "blocked:train-times-missing";
            if (!long.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mid))
                return "blocked:train-roster-invalid";
            rows.Add(new TrainRosterRow { MatchId = mid, At = p[1], OddsAt = p[2], Kickoff = p[3] });
        }
        return CandidateArchive.WriteTrainRoster(directory, rows);
    }

    public static string ImportDeclares(string directory, string importPath)
    {
        var metaStatus = ReadMeta(directory, out var meta);
        if (!metaStatus.StartsWith("window-meta:ok", StringComparison.Ordinal))
            return metaStatus;
        if (!File.Exists(importPath)) return "blocked:declare-import-missing";
        if (!File.Exists(Path.Combine(directory, CandidateArchive.TrainRosterFileName)))
            return "blocked:train-roster-missing";
        var n = 0;
        foreach (var raw in File.ReadAllLines(importPath))
        {
            if (!TryConsumeImportLine(raw, "matchId", out var line)) continue;
            var p = line.Split('\t');
            if (p.Length < 14) return "blocked:declare-import-incomplete";
            if (!long.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mid))
                return "blocked:declare-import-invalid";
            if (!double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lh)
                || !double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var la)
                || !double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var rho)
                || !double.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var temp)
                || !double.TryParse(p[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var pFull)
                || !decimal.TryParse(p[8], NumberStyles.Number, CultureInfo.InvariantCulture, out var odds))
                return "blocked:declare-import-invalid";
            var row = new ArchiveRow
            {
                RunId = "offline-declare",
                MatchId = mid,
                LambdaHome = lh,
                LambdaAway = la,
                Rho = rho,
                Temperature = temp,
                StoredProbability = pFull,
                Play = p[6],
                Option = p[7],
                Odds = odds,
                OddsSnapshot = p[9],
                DeclaredAt = p[10],
                OddsCapturedAt = p[11],
                AsOf = p[12],
                Kickoff = p[13],
                TrainCutoff = meta["TrainCutoff"],
                EvidenceId = meta["EvidenceId"],
                SampleRef = meta["SampleRef"],
                ModelVersion = meta["ModelVersion"],
                CodeHash = meta["CodeHash"],
                RuleVersion = meta["PolicyVersion"],
                Lane = "research",
                Decision = "observe",
                Reason = "offline-import"
            };
            try { CandidateArchive.WritePredeclaredUnseen(directory, row); }
            catch (InvalidDataException ex) { return "blocked:" + ex.Message; }
            n++;
        }
        return "declares:imported|" + n.ToString(CultureInfo.InvariantCulture);
    }

    public static string ImportResults(string directory, string importPath)
    {
        var metaStatus = RequireWindowMetaIntegrity(directory);
        if (!metaStatus.StartsWith("window-meta:integrity-ok|", StringComparison.Ordinal))
            return metaStatus;
        if (!File.Exists(importPath)) return "blocked:results-import-missing";
        var n = 0;
        foreach (var raw in File.ReadAllLines(importPath))
        {
            if (!TryConsumeImportLine(raw, "matchId", out var line)) continue;
            var p = line.Split('\t');
            if (p.Length < 4) return "blocked:results-incomplete";
            if (!long.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mid)
                || !int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var home)
                || !int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var away))
                return "blocked:results-incomplete";
            var status = CandidateArchive.AttachResult(directory, mid, home, away, p[3]);
            if (status != "result:attached") return status;
            n++;
        }
        return "results:imported|" + n.ToString(CultureInfo.InvariantCulture);
    }

    public static string EvaluateAll(string directory)
    {
        var meta = RequireWindowMetaIntegrity(directory);
        if (!meta.StartsWith("window-meta:integrity-ok|", StringComparison.Ordinal))
            return meta;
        var win = CandidateArchive.EvaluateWindow(directory);
        var res = CandidateArchive.EvaluateResults(directory);
        return "evaluate|" + win + "||" + res + "||" + meta;
    }

    public static string BuildEvidence(string directory, string evidenceOutPath)
    {
        var meta = RequireWindowMetaIntegrity(directory);
        if (!meta.StartsWith("window-meta:integrity-ok|", StringComparison.Ordinal))
            return meta;
        if (!CandidateArchive.TryBuildEvidence(directory, out var evidence, out var status))
            return status;
        WriteEvidenceFile(evidenceOutPath, evidence);
        return status + "|evidenceFile=" + evidenceOutPath;
    }

    public static string BindCheck(string directory, string evidencePath, string requiredEvidenceId, TemperatureFit? fit = null)
    {
        var meta = RequireWindowMetaIntegrity(directory);
        if (!meta.StartsWith("window-meta:integrity-ok|", StringComparison.Ordinal))
            return meta;
        if (!TryReadEvidenceFile(evidencePath, out var evidence, out var err))
            return err;
        // 夹具 Fit 仅供机制绑定路径，不是真实校准证据，也不是生产可绑定证明。
        fit ??= TemperatureFit.Fail("calibrator-not-ready");
        if (fit.Status != "computed")
            return "blocked:calibrator-not-ready|fixture-fit-only-not-real-calibration";
        var bound = CalibratorBinding.BindVerified(fit, evidence, requiredEvidenceId, directory);
        var prod = CalibratorBinding.Bind(fit, evidence, directory);
        return "bind-check|mechanism=" + bound.Status
               + "|production=" + prod.Status
               + "|boundEvidenceIdEmpty=" + (CandidateProtocol.BoundEvidenceId.Length == 0).ToString(CultureInfo.InvariantCulture)
               + "|fixtureFit=true|notRealCalibration=true";
    }

    public static void WriteEvidenceFile(string path, CalibratorEvidence e)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var sb = new StringBuilder();
        void Put(string k, string v) => sb.Append(k).Append('=').Append(v).Append('\n');
        Put("EvidenceId", e.EvidenceId);
        Put("Kind", e.Kind);
        Put("ModelVersion", e.ModelVersion);
        Put("CodeHash", e.CodeHash);
        Put("PolicyVersion", e.PolicyVersion);
        Put("SampleWindow", e.SampleWindow);
        Put("FileHash", e.FileHash);
        Put("Matches", e.Matches.ToString(CultureInfo.InvariantCulture));
        Put("ModelFingerprint", e.ModelFingerprint);
        Put("CodeFingerprint", e.CodeFingerprint);
        Put("PolicyFingerprint", e.PolicyFingerprint);
        Put("ResultsHash", e.ResultsHash);
        Put("ResultsAttached", e.ResultsAttached.ToString(CultureInfo.InvariantCulture));
        Put("TrainRosterHash", e.TrainRosterHash);
        Put("TrainRosterCount", e.TrainRosterCount.ToString(CultureInfo.InvariantCulture));
        Put("WindowMetaHash", e.WindowMetaHash);
        Put("MechanismOnly", "true");
        File.WriteAllText(path, sb.ToString());
    }

    public static bool TryReadEvidenceFile(string path, out CalibratorEvidence evidence, out string error)
    {
        evidence = new CalibratorEvidence();
        error = "";
        if (!File.Exists(path))
        {
            error = "blocked:evidence-file-missing";
            return false;
        }
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            map[line[..eq]] = line[(eq + 1)..];
        }
        if (!int.TryParse(map.GetValueOrDefault("Matches"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var matches)
            || !int.TryParse(map.GetValueOrDefault("ResultsAttached"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var attached)
            || !int.TryParse(map.GetValueOrDefault("TrainRosterCount"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var trainCount))
        {
            error = "blocked:evidence-file-incomplete";
            return false;
        }
        evidence = new CalibratorEvidence
        {
            EvidenceId = map.GetValueOrDefault("EvidenceId") ?? "",
            Kind = map.GetValueOrDefault("Kind") ?? "",
            ModelVersion = map.GetValueOrDefault("ModelVersion") ?? "",
            CodeHash = map.GetValueOrDefault("CodeHash") ?? "",
            PolicyVersion = map.GetValueOrDefault("PolicyVersion") ?? "",
            SampleWindow = map.GetValueOrDefault("SampleWindow") ?? "",
            FileHash = map.GetValueOrDefault("FileHash") ?? "",
            Matches = matches,
            ModelFingerprint = map.GetValueOrDefault("ModelFingerprint") ?? "",
            CodeFingerprint = map.GetValueOrDefault("CodeFingerprint") ?? "",
            PolicyFingerprint = map.GetValueOrDefault("PolicyFingerprint") ?? "",
            ResultsHash = map.GetValueOrDefault("ResultsHash") ?? "",
            ResultsAttached = attached,
            TrainRosterHash = map.GetValueOrDefault("TrainRosterHash") ?? "",
            TrainRosterCount = trainCount,
            WindowMetaHash = map.GetValueOrDefault("WindowMetaHash") ?? ""
        };
        return true;
    }

    public static string WriteStatus(string directory, string status)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, StatusFileName);
        File.AppendAllText(path, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\t" + status + Environment.NewLine);
        return path;
    }

    public static string ParseMetaHashFromIntegrityStatus(string integrityOkStatus)
    {
        if (!integrityOkStatus.StartsWith("window-meta:integrity-ok|", StringComparison.Ordinal))
            return "";
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in integrityOkStatus.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            map[part[..eq]] = part[(eq + 1)..];
        }
        return map.GetValueOrDefault("metaHash") ?? "";
    }
}

public static partial class CandidateArchive
{
    public static bool IsUnqualifiedEvidenceIdPublic(string evidenceId) => IsUnqualifiedEvidenceId(evidenceId);
}
