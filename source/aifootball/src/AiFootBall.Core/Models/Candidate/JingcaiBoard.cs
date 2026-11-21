using AiFootBall.Core.Models;

namespace AiFootBall.Core.Models.Candidate;

/// <summary>
/// 三条竞彩链共用的保存口。正式适配器和隔离验收都把已经备好的提交交给它。
/// 榜面只认已发布的轮次。写到一半不得把半榜标成成功。
/// </summary>
public interface IJingcaiBoard
{
    Task<JingcaiCommitOutcome> CommitAsync(JingcaiCommit commit, CancellationToken ct = default);

    Task<JingcaiBoardView> ReadAsync(DateTime salesDate, string runId, CancellationToken ct = default);

    Task<string> SettleAsync(JingcaiSettle settle, CancellationToken ct = default);
}

public sealed class JingcaiCommit
{
    public DateTime SalesDate { get; init; }
    public DateTime DecisionAt { get; init; }
    public DateTime Kickoff { get; init; }
    public DateTime SettleAt { get; init; }
    public string RunId { get; init; } = "";
    public string ModelVersion { get; init; } = "";
    public string EvidenceId { get; init; } = "";
    public long MatchId { get; init; }
    public string MatchNo { get; init; } = "";
    public RecommendationRecord? Recommendation { get; init; }
    public IReadOnlyList<MilkPick> Milk { get; init; } = Array.Empty<MilkPick>();
    public IReadOnlyList<JingcaiSavedRow> Rows { get; init; } = Array.Empty<JingcaiSavedRow>();
    public bool Publish { get; init; }
    public string Fault { get; init; } = "";
    public string DesiredStatus { get; init; } = "published";
}

public sealed class JingcaiSavedRow
{
    public string Play { get; init; } = "";
    public string Option { get; init; } = "";
    public double Probability { get; init; }
    public decimal Odds { get; init; }
    public decimal Ev { get; init; }
    public decimal ModelEv { get; init; }
    public decimal? Handicap { get; init; }
    public string HandicapSource { get; init; } = "";
    public long MatchId { get; init; }
    public string RunId { get; init; } = "";
    public DateTime SalesDate { get; init; }
    public DateTime DecisionAt { get; init; }
    public DateTime Kickoff { get; init; }
    public string ModelVersion { get; init; } = "";
    public string EvidenceId { get; init; } = "";
    public string Settle { get; set; } = "";
}

public sealed class JingcaiBoardView
{
    public bool Published { get; init; }
    public string RunId { get; init; } = "";
    public DateTime SalesDate { get; init; }
    public IReadOnlyList<JingcaiSavedRow> Rows { get; init; } = Array.Empty<JingcaiSavedRow>();
}

public sealed class JingcaiCommitOutcome
{
    public string Status { get; init; } = "";
    public JingcaiBoardView View { get; init; } = new();
}

public sealed class JingcaiSettle
{
    public DateTime SalesDate { get; init; }
    public string RunId { get; init; } = "";
    public string Play { get; init; } = "";
    public int? Home { get; init; }
    public int? Away { get; init; }
    public decimal? Handicap { get; init; }
    public string State { get; init; } = "final";
}

public sealed class JingcaiSaveInterruptException : InvalidOperationException
{
    public JingcaiSaveInterruptException() : base("blocked:save-interrupted") { }
}
