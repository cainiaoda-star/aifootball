namespace AiFootBall.Core.Models;

/// <summary>
/// 受注页采集三态（2026-11-14）：空列表不再一律当「无在售」。
/// Ok = 解析到 ≥1 场；NoSale = 容器在且 0 行；FetchFailed = 打不开 / 无容器 / 无法确认。
/// </summary>
public enum CardFetchKind
{
    Ok,
    NoSale,
    FetchFailed
}

/// <summary>单板块采集结果。</summary>
public sealed class BoardFetchResult
{
    public CardFetchKind Kind { get; init; }
    public IReadOnlyList<BettingCardRecord> Records { get; init; } = Array.Empty<BettingCardRecord>();
    public bool ContainerPresent { get; init; }
    public string Evidence { get; init; } = "";

    public int Count => Records.Count;

    public static BoardFetchResult Ok(IReadOnlyList<BettingCardRecord> records, string evidence = "")
        => new()
        {
            Kind = CardFetchKind.Ok,
            Records = records,
            ContainerPresent = true,
            Evidence = evidence
        };

    public static BoardFetchResult NoSale(string evidence)
        => new()
        {
            Kind = CardFetchKind.NoSale,
            Records = Array.Empty<BettingCardRecord>(),
            ContainerPresent = true,
            Evidence = evidence
        };

    public static BoardFetchResult Failed(string evidence, bool containerPresent = false)
        => new()
        {
            Kind = CardFetchKind.FetchFailed,
            Records = Array.Empty<BettingCardRecord>(),
            ContainerPresent = containerPresent,
            Evidence = evidence
        };
}

/// <summary>三板块汇总，供 SyncLog 与闸判定共用同一套前缀口径。</summary>
public sealed class CardsCollectResult
{
    public BoardFetchResult Jc { get; init; } = BoardFetchResult.Failed("unset");
    public BoardFetchResult Bd { get; init; } = BoardFetchResult.Failed("unset");
    public BoardFetchResult Sfc { get; init; } = BoardFetchResult.Failed("unset");

    public int JcCount => Jc.Count;
    public int BdCount => Bd.Count;
    public int SfcCount => Sfc.Count;
    public int TotalRows => JcCount + BdCount + SfcCount;

    public string ContainersToken =>
        $"containers=jc:{(Jc.ContainerPresent ? 1 : 0)};bd:{(Bd.ContainerPresent ? 1 : 0)};sfc:{(Sfc.ContainerPresent ? 1 : 0)}";

    /// <summary>
    /// SyncLog Message：全失败 → <c>[fetch-failed]</c>；含失败 → <c>[partial]</c>；
    /// 全 NoSale → <c>[no-sale]</c>；否则普通成功文案。
    /// </summary>
    public string SyncMessage
    {
        get
        {
            var counts = $"jc={JcCount} bd={BdCount} sfc={SfcCount} {ContainersToken}";
            var kinds = new[] { Jc.Kind, Bd.Kind, Sfc.Kind };

            if (kinds.All(k => k == CardFetchKind.FetchFailed))
                return $"[fetch-failed] {counts} jc:{Jc.Evidence};bd:{Bd.Evidence};sfc:{Sfc.Evidence}";

            if (kinds.Any(k => k == CardFetchKind.FetchFailed))
                return $"[partial] {counts} jc:{KindTag(Jc)};bd:{KindTag(Bd)};sfc:{KindTag(Sfc)}";

            if (kinds.All(k => k == CardFetchKind.NoSale))
                return $"[no-sale] {counts}";

            // jc 无在售、其它板块有场：仍标 [no-sale]（闸只认 jc），并带上其它行数
            if (Jc.Kind == CardFetchKind.NoSale && kinds.Any(k => k == CardFetchKind.Ok))
                return $"[no-sale] {counts}";

            if (Jc.Kind == CardFetchKind.NoSale)
                return $"[no-sale] {counts}";

            return $"受注场次：竞彩 {JcCount} / 北单 {BdCount} / 胜负彩 {SfcCount}";
        }
    }

    /// <summary>
    /// 作业落库状态：三块全 FetchFailed → FAILED；其余（含 partial / no-sale / ok）→ SUCCESS。
    /// partial 用 message 前缀区分，Status 仍 SUCCESS（列宽不动）。
    /// </summary>
    public bool SyncSuccess =>
        !(Jc.Kind == CardFetchKind.FetchFailed
          && Bd.Kind == CardFetchKind.FetchFailed
          && Sfc.Kind == CardFetchKind.FetchFailed);

    private static string KindTag(BoardFetchResult b) => b.Kind switch
    {
        CardFetchKind.Ok => b.Count.ToString(),
        CardFetchKind.NoSale => "no-sale",
        _ => $"fetch-failed:{b.Evidence}"
    };
}
