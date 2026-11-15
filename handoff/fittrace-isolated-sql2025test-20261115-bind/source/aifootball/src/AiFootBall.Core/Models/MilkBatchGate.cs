namespace AiFootBall.Core.Models;

/// <summary>出榜前批次时效闸的档位（降级关闭后四档）。</summary>
public enum MilkGateBand
{
    /// <summary>放行发布（仅 fresh；本轮 no-sale 不走此档）。</summary>
    Allow,
    /// <summary>告警且不发布（partial / 本轮 no-sale）。</summary>
    AlertNoPublish,
    /// <summary>拒绝，榜面不动。</summary>
    Reject,
    /// <summary>跨日跳过，不告警不发布。</summary>
    Skip
}

/// <summary>闸输入：全部由调用方读库后传入，判定本身是纯函数。</summary>
public sealed class MilkGateInput
{
    public DateTime RunAt { get; init; }
    public DateTime? Cap { get; init; }
    public CardsSyncRec? Rec { get; init; }
    public bool Complete { get; init; }

    /// <summary>
    /// no-sale 旁证：cap 是否仍等于「rec 之前那次成功采集」的批次时刻（±120s）。
    /// 本轮 NoSale 不写 BettingCard，故 cap 不应贴近 rec.FinishedAt。
    /// </summary>
    public bool CapUnchangedSinceRec { get; init; }

    /// <summary>读 BettingCard 采集状态失败时的真实错误；非空则优先报 capture-read-error，不得吞成 no-batch。</summary>
    public string? CaptureError { get; init; }

    /// <summary>读 SyncLog 失败时的真实错误；非空则优先报 synclog-read-error。</summary>
    public string? SyncError { get; init; }
}

/// <summary>SyncLog 里一条成功的 cards 采集摘要（闸只认字段，不持连接）。</summary>
public sealed class CardsSyncRec
{
    public string TaskName { get; init; } = "";
    public DateTime FinishedAt { get; init; }
    public int RowsAffected { get; init; }
    public string Message { get; init; } = "";

    public bool IsDaily =>
        string.Equals(TaskName, "job:cards-daily", StringComparison.OrdinalIgnoreCase);

    public bool IsManual =>
        string.Equals(TaskName, "cards:manual", StringComparison.OrdinalIgnoreCase);

    public bool HasNoSalePrefix =>
        Message.StartsWith("[no-sale]", StringComparison.OrdinalIgnoreCase);

    public bool HasFetchFailedPrefix =>
        Message.StartsWith("[fetch-failed]", StringComparison.OrdinalIgnoreCase);

    public bool HasPartialPrefix =>
        Message.StartsWith("[partial]", StringComparison.OrdinalIgnoreCase);

    /// <summary>jc 板块是否真取到页面（含 NoSale 容器在）。</summary>
    public bool JcOk
    {
        get
        {
            if (HasFetchFailedPrefix) return false;
            if (HasPartialPrefix)
            {
                // KindTag 形如 jc:fetch-failed:… / jc:no-sale / jc:12
                // 不能用裸 jc:0——会误撞 containers=jc:0
                if (Message.Contains("jc:fetch-failed", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (Message.Contains("jc:no-sale", StringComparison.OrdinalIgnoreCase))
                    return true;
                return System.Text.RegularExpressions.Regex.IsMatch(
                    Message, @"(^|[\s;])jc:\d+",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            if (HasNoSalePrefix)
                return Message.Contains("containers=jc:1", StringComparison.OrdinalIgnoreCase);
            // 普通成功文案
            return true;
        }
    }

    public bool NoSaleEvidenceComplete =>
        HasNoSalePrefix
        && System.Text.RegularExpressions.Regex.IsMatch(Message, @"\bjc=0\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        && Message.Contains("containers=jc:1", StringComparison.OrdinalIgnoreCase);
}

/// <summary>闸输出。</summary>
public sealed class MilkGateResult
{
    public MilkGateBand Band { get; init; }
    public string Verdict { get; init; } = "";
    public string FailReason { get; init; } = "";
    public DateTime? Slot { get; init; }
    public DateTime? Cap { get; init; }

    public bool Published => Band == MilkGateBand.Allow;
    public bool Alerts => Band is MilkGateBand.Reject or MilkGateBand.AlertNoPublish;

    public static MilkGateResult Allow(string verdict, DateTime? cap, DateTime? slot)
        => new() { Band = MilkGateBand.Allow, Verdict = verdict, Cap = cap, Slot = slot };

    public static MilkGateResult Alert(string verdict, DateTime? cap, DateTime? slot, string failReason)
        => new()
        {
            Band = MilkGateBand.AlertNoPublish,
            Verdict = verdict,
            Cap = cap,
            Slot = slot,
            FailReason = failReason
        };

    public static MilkGateResult Reject(string verdict, DateTime? cap, DateTime? slot, string failReason)
        => new()
        {
            Band = MilkGateBand.Reject,
            Verdict = verdict,
            Cap = cap,
            Slot = slot,
            FailReason = failReason
        };

    public static MilkGateResult Skip(string verdict, DateTime? cap, string note)
        => new()
        {
            Band = MilkGateBand.Skip,
            Verdict = verdict,
            Cap = cap,
            FailReason = note
        };
}

/// <summary>
/// 出榜前批次时效闸（纯判定）。<see cref="DegradeEnabled"/> 显式关闭；
/// MaxBatchAge 保留但不参与判定。±120s 只是时间启发式，不是来源证明。
/// </summary>
public static class MilkBatchGate
{
    /// <summary>显式开关（非 const，避免「改数字悄悄恢复降级」；测试可读当前值）。</summary>
    public static readonly bool DegradeEnabled = false;

    /// <summary>保留字段；关闭降级时不读、不比较（测试钉死）。</summary>
    public static readonly TimeSpan MaxBatchAge = TimeSpan.FromHours(6);

    public static readonly TimeSpan PairTolerance = TimeSpan.FromSeconds(120);

    public static readonly TimeOnly[] CardSlots =
    {
        new(8, 30), new(11, 30), new(16, 30), new(20, 30)
    };

    public const string DailyTask = "job:cards-daily";
    public const string ManualTask = "cards:manual";

    /// <summary>当日 ≤ runAt 的最近一个 cards 时点。</summary>
    public static DateTime SlotOf(DateTime runAt)
    {
        var day = runAt.Date;
        DateTime? best = null;
        foreach (var t in CardSlots)
        {
            var at = day.Add(t.ToTimeSpan());
            if (at <= runAt && (best is null || at > best))
                best = at;
        }
        // 早于当日第一个 slot：退到昨日 20:30
        return best ?? day.AddDays(-1).Add(new TimeOnly(20, 30).ToTimeSpan());
    }

    public static bool TimesClose(DateTime a, DateTime b)
        => Math.Abs((a - b).TotalSeconds) <= PairTolerance.TotalSeconds;

    /// <summary>
    /// 有序判定：先中者胜。
    /// 本轮 no-sale：即使证据齐也只 AlertNoPublish（不发布、不清榜、不撤票）——已废止旧 N3 绿空榜。
    /// </summary>
    public static MilkGateResult Check(MilkGateInput input)
    {
        var runAt = input.RunAt;
        var slot = SlotOf(runAt);
        var cap = input.Cap;
        var rec = input.Rec;

        string Red(string verdict)
        {
            var capText = cap?.ToString("HH:mm:ss") ?? "null";
            var slotText = slot.ToString("HH:mm");
            return $"{verdict}:{capText}:{slotText}:degrade=off";
        }

        // 读库异常必须原样上报，不得先落成 no-batch / batch-mismatch
        if (!string.IsNullOrWhiteSpace(input.CaptureError))
            return MilkGateResult.Reject(
                "capture-read-error", cap, slot,
                $"capture-read-error:{input.CaptureError}");
        if (!string.IsNullOrWhiteSpace(input.SyncError))
            return MilkGateResult.Reject(
                "synclog-read-error", cap, slot,
                $"synclog-read-error:{input.SyncError}");

        // #1
        if (cap is null)
            return MilkGateResult.Reject("no-batch", null, slot, Red("no-batch"));

        // #2
        if (cap > runAt)
            return MilkGateResult.Reject("future-batch", cap, slot, Red("future-batch"));

        // #10 提前：跨受注日不发布、不告警（隔夜重算）
        if (cap.Value.Date != runAt.Date)
            return MilkGateResult.Skip("cross-day-skip", cap, $"cross-day@{cap:yyyy-MM-dd HH:mm:ss}");

        // #3 rec 空
        if (rec is null)
            return MilkGateResult.Reject("batch-mismatch", cap, slot, Red("batch-mismatch"));

        // NoSale 单列通道（在 L1 时间配对之前）：本轮不写 BettingCard，不能要求 |cap−rec|≤120
        if (rec.HasNoSalePrefix || (rec.RowsAffected == 0 && rec.NoSaleEvidenceComplete))
        {
            if (!rec.IsDaily)
                return MilkGateResult.Reject("manual-only", cap, slot, Red("manual-only"));
            if (rec.FinishedAt < slot || rec.FinishedAt > runAt)
            {
                if (!DegradeEnabled)
                    return MilkGateResult.Reject("no-capture-this-slot", cap, slot, Red("no-capture-this-slot"));
                _ = MaxBatchAge;
                return MilkGateResult.Reject("no-capture-this-slot", cap, slot, Red("no-capture-this-slot"));
            }
            if (!rec.JcOk || !rec.NoSaleEvidenceComplete)
                return MilkGateResult.Reject("jc-fetch-failed", cap, slot, Red("jc-fetch-failed"));
            // cap 在 rec 之后被改过 → 顺序矛盾
            if (!input.CapUnchangedSinceRec || TimesClose(cap.Value, rec.FinishedAt))
                return MilkGateResult.Reject("batch-mismatch", cap, slot, Red("batch-mismatch"));

            // ★ 本轮修订：证据齐也不发布、不清榜、不撤票
            return MilkGateResult.Alert(
                "no-sale", cap, slot,
                $"no-sale:{cap:HH:mm:ss}:{slot:HH:mm}:degrade=off");
        }

        // #3 L1：|cap − rec.FinishedAt| ≤ 120s（启发式时间配对，非来源证明）
        if (!TimesClose(cap.Value, rec.FinishedAt))
            return MilkGateResult.Reject("batch-mismatch", cap, slot, Red("batch-mismatch"));

        // #4
        if (!rec.IsDaily)
            return MilkGateResult.Reject("manual-only", cap, slot, Red("manual-only"));

        // #5
        if (rec.FinishedAt < slot || rec.FinishedAt > runAt)
        {
            // 显式关闭分支：不得靠 MaxBatchAge=0 隐式生效（字段保留，关闭时不读、不比较）。
            if (!DegradeEnabled)
                return MilkGateResult.Reject("no-capture-this-slot", cap, slot, Red("no-capture-this-slot"));
            // 降级若将来打开：仍拒绝本轮自动顶旧批（本轮无 stale-bounded 绿路径）
            _ = MaxBatchAge;
            return MilkGateResult.Reject("no-capture-this-slot", cap, slot, Red("no-capture-this-slot"));
        }

        // #6
        if (!rec.JcOk)
            return MilkGateResult.Reject("jc-fetch-failed", cap, slot, Red("jc-fetch-failed"));

        // #7 fresh
        if (rec.RowsAffected > 0 && input.Complete)
            return MilkGateResult.Allow("fresh", cap, slot);

        // #8 已被上方 no-sale 通道处理

        // #9 partial：达标且 jcOk，但 complete=false —— 与批次新不新无关
        if (rec.RowsAffected > 0 && !input.Complete)
            return MilkGateResult.Alert(
                "partial", cap, slot,
                $"partial:{cap:HH:mm:ss}:{slot:HH:mm}");

        // RowsAffected=0 但无 no-sale 证据
        return MilkGateResult.Reject("jc-fetch-failed", cap, slot, Red("jc-fetch-failed"));
    }
}
