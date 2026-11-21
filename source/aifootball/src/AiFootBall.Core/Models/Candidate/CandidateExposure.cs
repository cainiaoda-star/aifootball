namespace AiFootBall.Core.Models.Candidate;

/// <summary>
/// 日用额和本笔预占放在同一次版本校验里。两个账本对象可以指向同一份状态，
/// 所以单对象上的 lock 不是跨实例控制。SQL 文本是多实例时的同一条契约。
/// </summary>
public sealed class VersionedExposureStore
{
    readonly object _gate = new();
    readonly Dictionary<string, DayRow> _days = new(StringComparer.Ordinal);
    readonly Dictionary<string, Hold> _holds = new(StringComparer.Ordinal);
    readonly string? _journal;
    readonly Mutex? _mutex;
    string _fault = "";
    string _tail = "";

    public const string SqlText =
        "BEGIN TRAN; SELECT UsedAmount, VersionNo FROM dbo.CandidateDayExposure WITH (UPDLOCK, HOLDLOCK) "
        + "WHERE DayKey=@day; IF @@ROWCOUNT=0 INSERT dbo.CandidateDayExposure(DayKey,UsedAmount,VersionNo) VALUES(@day,0,0); "
        + "SELECT State, Fingerprint FROM dbo.CandidateReservation WITH (UPDLOCK, HOLDLOCK) WHERE ReservationId=@id; "
        + "IF @@ROWCOUNT=1 AND (SELECT State FROM dbo.CandidateReservation WHERE ReservationId=@id)<>N'aborted' "
        + "AND (SELECT Fingerprint FROM dbo.CandidateReservation WHERE ReservationId=@id)<>@fingerprint ROLLBACK TRAN; "
        + "IF (SELECT VersionNo FROM dbo.CandidateDayExposure WHERE DayKey=@day)<>@version ROLLBACK TRAN; "
        + "IF (SELECT UsedAmount FROM dbo.CandidateDayExposure WHERE DayKey=@day)+@stake>@limit ROLLBACK TRAN; "
        + "UPDATE dbo.CandidateDayExposure SET UsedAmount=UsedAmount+@stake, VersionNo=VersionNo+1 "
        + "WHERE DayKey=@day AND VersionNo=@version; IF @@ROWCOUNT<>1 ROLLBACK TRAN; "
        + "UPDATE dbo.CandidateReservation SET State=N'reserved', Fingerprint=@fingerprint, Stake=@stake, "
        + "Odds=@odds, Probability=@probability, Handicap=@handicap, ModelVersion=@model, CodeHash=@code, "
        + "RuleVersion=@rule, EvidenceId=@evidence, VersionNo=@version+1 WHERE ReservationId=@id; "
        + "IF @@ROWCOUNT=0 INSERT dbo.CandidateReservation(ReservationId,DayKey,MatchKey,Stake,State,Fingerprint,VersionNo,"
        + "Odds,Probability,Handicap,ModelVersion,CodeHash,RuleVersion,EvidenceId) "
        + "VALUES(@id,@day,@match,@stake,N'reserved',@fingerprint,@version+1,@odds,@probability,@handicap,@model,@code,@rule,@evidence); COMMIT TRAN;";

    public VersionedExposureStore() { }

    public VersionedExposureStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _journal = Path.Combine(directory, "exposure.journal");
        _mutex = new Mutex(false, "Local\\AiFbExposure-" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(_journal)))).Substring(0, 16));
        WithLock(Replay);
    }

    public static string Identity(string dayKey, string matchKey, decimal stake, DecisionBinding binding)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return string.Join("|", dayKey, matchKey, stake.ToString("0.000", inv), binding.Play, binding.Option,
            binding.Odds.ToString("0.000", inv), binding.Probability.ToString("0.00000000", inv),
            binding.Handicap.ToString("0.000", inv), binding.ModelVersion, binding.CodeHash,
            binding.RuleVersion, binding.EvidenceId);
    }

    public string ReadJournalStatus()
    {
        var status = "";
        WithLock(() => status = _fault.Length > 0 ? _fault : _tail.Length > 0 ? _tail : "ok");
        return status;
    }

    public void Seed(string dayKey, decimal used, long version)
    {
        WithLock(() =>
        {
            _days[dayKey] = new DayRow { Used = used, Version = version };
            Append("seed\t" + dayKey + "\t" + used.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "\t" + version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        });
    }

    public decimal DayUsed(string dayKey)
    {
        decimal used = 0;
        WithLock(() =>
        {
            if (_fault.Length > 0) throw new InvalidOperationException(_fault);
            used = _days.TryGetValue(dayKey, out var row) ? row.Used : 0m;
        });
        return used;
    }

    public long Version(string dayKey)
    {
        long version = 0;
        WithLock(() =>
        {
            if (_fault.Length > 0) throw new InvalidOperationException(_fault);
            version = _days.TryGetValue(dayKey, out var row) ? row.Version : 0L;
        });
        return version;
    }

    public string State(string reservationId)
    {
        var state = "";
        WithLock(() =>
        {
            if (_fault.Length > 0) throw new InvalidOperationException(_fault);
            state = _holds.TryGetValue(reservationId, out var hold) ? hold.State : "";
        });
        return state;
    }

    public ReserveOutcome TryReserve(
        string reservationId, string dayKey, string matchKey,
        long expectedVersion, decimal expectedUsed,
        decimal stake, decimal dayLimit, decimal matchLoss, decimal matchLimit,
        DecisionBinding? binding = null)
    {
        ReserveOutcome outcome = null!;
        WithLock(() => outcome = ReserveUnlocked(reservationId, dayKey, matchKey, expectedVersion, expectedUsed, stake, dayLimit, matchLoss, matchLimit, binding ?? DecisionBinding.Empty));
        return outcome;
    }

    ReserveOutcome ReserveUnlocked(
        string reservationId, string dayKey, string matchKey,
        long expectedVersion, decimal expectedUsed,
        decimal stake, decimal dayLimit, decimal matchLoss, decimal matchLimit,
        DecisionBinding binding)
    {
        if (_fault.Length > 0)
            return new ReserveOutcome("journal-corrupt", 0, 0, false);
        var identity = Identity(dayKey, matchKey, stake, binding);
        if (_holds.TryGetValue(reservationId, out var existing))
        {
            if (existing.State is "reserved" or "saved")
            {
                if (!string.Equals(existing.Identity, identity, StringComparison.Ordinal) || existing.BoundVersion != expectedVersion)
                    return new ReserveOutcome("reservation-conflict", existing.Version, DayUsedUnlocked(dayKey), false);
                return new ReserveOutcome("idempotent", existing.Version, DayUsedUnlocked(dayKey), true);
            }
            if (!string.Equals(existing.Identity, identity, StringComparison.Ordinal))
                return new ReserveOutcome("reservation-conflict", existing.Version, DayUsedUnlocked(dayKey), false);
        }
        var row = _days.TryGetValue(dayKey, out var found) ? found : new DayRow();
        if (row.Version != expectedVersion || row.Used != expectedUsed)
            return new ReserveOutcome("concurrent-exposure", row.Version, row.Used, false);
        var matchUsed = 0m;
        foreach (var hold in _holds.Values)
            if (hold.State is "reserved" or "saved" && hold.MatchKey == matchKey) matchUsed += hold.Stake;
        if (matchUsed + stake > matchLimit || row.Used + stake > dayLimit)
            return new ReserveOutcome("budget-exceeded", row.Version, row.Used, false);
        row.Used += stake;
        row.Version++;
        _days[dayKey] = row;
        _holds[reservationId] = new Hold
        {
            DayKey = dayKey, MatchKey = matchKey, Stake = stake, MatchLoss = matchLoss,
            Version = row.Version, BoundVersion = expectedVersion, State = "reserved", Identity = identity
        };
        Append("reserve\t" + reservationId + "\t" + dayKey + "\t" + matchKey + "\t"
            + stake.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\t"
            + matchLoss.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\t"
            + expectedVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\t" + identity);
        return new ReserveOutcome("reserved", row.Version, row.Used, true);
    }

    public bool Abort(string reservationId, AppendOnlyTicketLog? tickets = null)
    {
        var released = false;
        WithLock(() =>
        {
            if (_fault.Length > 0) return;
            if (tickets is not null && tickets.ContainsId(reservationId)) return;
            if (!_holds.TryGetValue(reservationId, out var hold) || hold.State != "reserved") return;
            if (_days.TryGetValue(hold.DayKey, out var row))
            {
                row.Used -= hold.Stake;
                if (row.Used < 0) row.Used = 0;
                row.Version++;
            }
            hold.State = "aborted";
            Append("abort\t" + reservationId);
            released = true;
        });
        return released;
    }

    public void MarkSaved(string reservationId)
    {
        WithLock(() =>
        {
            if (_fault.Length > 0) throw new InvalidOperationException(_fault);
            if (!_holds.TryGetValue(reservationId, out var hold) || hold.State != "reserved") return;
            Append("save\t" + reservationId);
            hold.State = "saved";
        });
    }

    decimal DayUsedUnlocked(string dayKey) => _days.TryGetValue(dayKey, out var row) ? row.Used : 0m;

    void WithLock(Action action)
    {
        if (_mutex is null)
        {
            lock (_gate) action();
            return;
        }
        _mutex.WaitOne();
        try
        {
            lock (_gate)
            {
                Replay();
                action();
            }
        }
        finally { _mutex.ReleaseMutex(); }
    }

    void Append(string line)
    {
        if (_journal is null) return;
        File.AppendAllText(_journal, line + Environment.NewLine);
    }

    void Replay()
    {
        _fault = "";
        _tail = "";
        if (_journal is null || !File.Exists(_journal)) return;
        if (DurableLog.QuarantineTornTail(_journal) || File.Exists(_journal + ".torn"))
            _tail = "recovered-torn-tail";
        var text = File.ReadAllText(_journal);
        var days = new Dictionary<string, DayRow>(StringComparer.Ordinal);
        var holds = new Dictionary<string, Hold>(StringComparer.Ordinal);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0 || line == "\r") continue;
            var part = line.TrimEnd('\r').Split('\t');
            if (part.Length == 4 && part[0] == "seed"
                && decimal.TryParse(part[2], System.Globalization.NumberStyles.Number, inv, out var used)
                && long.TryParse(part[3], System.Globalization.NumberStyles.Integer, inv, out var version))
            {
                days[part[1]] = new DayRow { Used = used, Version = version };
            }
            else if (part.Length == 8 && part[0] == "reserve"
                && decimal.TryParse(part[4], System.Globalization.NumberStyles.Number, inv, out var stake)
                && decimal.TryParse(part[5], System.Globalization.NumberStyles.Number, inv, out var loss)
                && long.TryParse(part[6], System.Globalization.NumberStyles.Integer, inv, out var bound)
                && part[7].Length > 0)
            {
                var row = days.TryGetValue(part[2], out var found) ? found : new DayRow();
                row.Used += stake;
                row.Version++;
                days[part[2]] = row;
                holds[part[1]] = new Hold
                {
                    DayKey = part[2], MatchKey = part[3], Stake = stake, MatchLoss = loss,
                    Version = row.Version, BoundVersion = bound, State = "reserved", Identity = part[7]
                };
            }
            else if (part.Length == 2 && part[0] == "abort" && holds.TryGetValue(part[1], out var aborted) && aborted.State == "reserved")
            {
                if (days.TryGetValue(aborted.DayKey, out var row))
                {
                    row.Used -= aborted.Stake;
                    if (row.Used < 0) row.Used = 0;
                    row.Version++;
                }
                aborted.State = "aborted";
            }
            else if (part.Length == 2 && part[0] == "save" && holds.TryGetValue(part[1], out var saved) && saved.State == "reserved")
                saved.State = "saved";
            else
            {
                _fault = "journal-corrupt";
                _days.Clear();
                _holds.Clear();
                return;
            }
        }
        _days.Clear();
        _holds.Clear();
        foreach (var pair in days) _days[pair.Key] = pair.Value;
        foreach (var pair in holds) _holds[pair.Key] = pair.Value;
    }

    sealed class DayRow
    {
        public decimal Used;
        public long Version;
    }

    sealed class Hold
    {
        public string DayKey = "";
        public string MatchKey = "";
        public decimal Stake;
        public decimal MatchLoss;
        public long Version;
        public long BoundVersion;
        public string State = "";
        public string Identity = "";
    }
}

public sealed class DecisionBinding
{
    public DecisionBinding(string play, string option, decimal odds, double probability, decimal handicap,
        string modelVersion, string codeHash, string ruleVersion, string evidenceId)
    {
        Play = play;
        Option = option;
        Odds = odds;
        Probability = probability;
        Handicap = handicap;
        ModelVersion = modelVersion;
        CodeHash = codeHash;
        RuleVersion = ruleVersion;
        EvidenceId = evidenceId;
    }

    public static DecisionBinding Empty { get; } = new("", "", 0, 0, 0, "", "", "", "");
    public string Play { get; }
    public string Option { get; }
    public decimal Odds { get; }
    public double Probability { get; }
    public decimal Handicap { get; }
    public string ModelVersion { get; }
    public string CodeHash { get; }
    public string RuleVersion { get; }
    public string EvidenceId { get; }
}

public sealed class ReserveOutcome
{
    public ReserveOutcome(string status, long version, decimal dayUsed, bool accepted)
    {
        Status = status;
        Version = version;
        DayUsed = dayUsed;
        Accepted = accepted;
    }

    public string Status { get; }
    public long Version { get; }
    public decimal DayUsed { get; }
    public bool Accepted { get; }
}

public sealed class CandidateLedger
{
    public CandidateLedger() : this(new VersionedExposureStore()) { }

    public CandidateLedger(VersionedExposureStore store) => Store = store;

    public VersionedExposureStore Store { get; }
}

public sealed class TicketRecord
{
    public string Id { get; init; } = "";
    public string Play { get; init; } = "";
    public string Option { get; init; } = "";
    public long MatchId { get; init; }
    public decimal Odds { get; init; }
    public decimal Stake { get; init; }
    public string Lane { get; init; } = "research";
    public DateTime DecisionAt { get; init; }
    public DateTime Kickoff { get; init; }
    public string Settlement { get; set; } = "unsettled";
    public double Probability { get; init; }
    public decimal Handicap { get; init; }
    public string ModelVersion { get; init; } = "";
    public string CodeHash { get; init; } = "";
    public string RuleVersion { get; init; } = "";
    public string EvidenceId { get; init; } = "";
}

public sealed class SaveReceipt
{
    public SaveReceipt(string status, string id)
    {
        Status = status;
        Id = id;
    }

    public string Status { get; }
    public string Id { get; }
    public bool Saved => Status == "saved";
}

/// <summary>
/// 把未写完的尾部留在备份里。主日志只在临时文件核对通过、并且替换成功之后才换成完整前缀。
/// 替换前的任何失败都留下原始字节。替换后的证据写入失败则抛出，不返回部分票数。
/// </summary>
public static class DurableLog
{
    public static bool QuarantineTornTail(string path)
    {
        if (!File.Exists(path)) return false;
        var text = File.ReadAllText(path);
        if (text.Length == 0 || text[^1] == '\n') return false;
        var cut = text.LastIndexOf('\n');
        var torn = cut < 0 ? text : text[(cut + 1)..];
        var keep = cut < 0 ? "" : text[..(cut + 1)];
        var unique = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N");
        var temp = path + ".keep-" + unique;
        var backup = path + ".bak-" + unique;
        var replaced = false;
        try
        {
            File.WriteAllText(temp, keep);
            if (!string.Equals(File.ReadAllText(temp), keep, StringComparison.Ordinal))
                throw new IOException("recovered prefix does not match the bytes just written");
            File.Replace(temp, path, backup, ignoreMetadataErrors: true);
            replaced = true;
            File.AppendAllText(path + ".torn",
                DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture)
                + "\t" + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(torn))
                + "\tbackup=" + Path.GetFileName(backup) + Environment.NewLine);
            return true;
        }
        catch (Exception)
        {
            if (!replaced)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            }
            throw;
        }
    }
}

/// <summary>追加写入。同一路径的多个进程用同一把命名互斥锁，物理文件里每个编号只有一行。</summary>
public sealed class AppendOnlyTicketLog
{
    readonly Mutex _mutex;
    readonly string _path;

    public AppendOnlyTicketLog(string path)
    {
        _path = path;
        var full = Path.GetFullPath(path);
        _mutex = new Mutex(false, "Local\\AiFbTickets-" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(full))).Substring(0, 16));
    }

    public bool ContainsId(string id)
    {
        var found = false;
        WithLock(() =>
        {
            if (!File.Exists(_path)) return;
            foreach (var line in File.ReadLines(_path))
                if (line.StartsWith("save\t" + id + "\t", StringComparison.Ordinal)) found = true;
        });
        return found;
    }

    public SaveReceipt Append(TicketRecord record)
    {
        if (record.Play == "BQ") return new SaveReceipt("blocked:half-full-joint-missing", "");
        var line = Line(record);
        SaveReceipt receipt = null!;
        WithLock(() =>
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            DurableLog.QuarantineTornTail(_path);
            if (File.Exists(_path))
            {
                foreach (var existing in File.ReadLines(_path))
                {
                    if (existing.Length == 0) continue;
                    var fields = existing.Split('\t');
                    if (fields[0] == "settle") continue;
                    if (fields[0] != "save" || fields.Length != 17)
                        throw new InvalidDataException("ticket-log-corrupt");
                }
                var same = File.ReadAllLines(_path).FirstOrDefault(x => x.StartsWith("save\t" + record.Id + "\t", StringComparison.Ordinal));
                if (same is not null)
                {
                    receipt = new SaveReceipt(same == line ? "idempotent" : "reservation-conflict", record.Id);
                    return;
                }
            }
            File.AppendAllText(_path, line + Environment.NewLine);
            receipt = new SaveReceipt("saved", record.Id);
        });
        return receipt;
    }

    public void Settle(string id, string settlement)
    {
        WithLock(() => File.AppendAllText(_path, "settle\t" + id + "\t" + settlement + Environment.NewLine));
    }

    public IReadOnlyList<TicketRecord> Replay()
    {
        IReadOnlyList<TicketRecord> rows = Array.Empty<TicketRecord>();
        WithLock(() =>
        {
            if (!File.Exists(_path)) return;
            DurableLog.QuarantineTornTail(_path);
            var map = new Dictionary<string, TicketRecord>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in File.ReadLines(_path))
            {
                if (line.Length == 0) continue;
                var part = line.Split('\t');
                if (part[0] == "save")
                {
                    if (part.Length != 17) throw new InvalidDataException("ticket-log-corrupt");
                    if (!seen.Add(part[1])) throw new InvalidDataException("ticket-log-duplicate");
                    map[part[1]] = Parse(part);
                }
                else if (part.Length == 3 && part[0] == "settle" && map.TryGetValue(part[1], out var row))
                    row.Settlement = part[2];
                else
                    throw new InvalidDataException("ticket-log-corrupt");
            }
            rows = map.Values.ToList();
        });
        return rows;
    }

    void WithLock(Action action)
    {
        var owned = false;
        try
        {
            try { owned = _mutex.WaitOne(); }
            catch (AbandonedMutexException) { owned = true; }
            action();
        }
        finally
        {
            if (owned) _mutex.ReleaseMutex();
        }
    }

    static string Line(TicketRecord record)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return string.Join('\t', "save", record.Id, record.Play, record.Option, record.MatchId.ToString(inv),
            record.Odds.ToString("0.000", inv), record.Stake.ToString("0.000", inv),
            record.Lane, record.DecisionAt.ToString("yyyy-MM-dd HH:mm:ss", inv),
            record.Kickoff.ToString("yyyy-MM-dd HH:mm:ss", inv), "unsettled",
            record.Probability.ToString("0.00000000", inv), record.Handicap.ToString("0.000", inv),
            record.ModelVersion, record.CodeHash, record.RuleVersion, record.EvidenceId);
    }

    static TicketRecord Parse(string[] part)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return new TicketRecord
        {
            Id = part[1], Play = part[2], Option = part[3],
            MatchId = long.Parse(part[4], inv),
            Odds = decimal.Parse(part[5], inv),
            Stake = decimal.Parse(part[6], inv),
            Lane = part[7],
            DecisionAt = DateTime.Parse(part[8], inv),
            Kickoff = DateTime.Parse(part[9], inv),
            Settlement = part[10],
            Probability = double.Parse(part[11], inv),
            Handicap = decimal.Parse(part[12], inv),
            ModelVersion = part[13], CodeHash = part[14], RuleVersion = part[15], EvidenceId = part[16]
        };
    }
}
