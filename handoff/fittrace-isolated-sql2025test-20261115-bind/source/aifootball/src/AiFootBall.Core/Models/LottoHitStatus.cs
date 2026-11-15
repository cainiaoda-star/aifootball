namespace AiFootBall.Core.Models;

/// <summary>
/// 数字彩对奖结构化状态。前后端必须以本枚举判定，禁止用 Note 是否含「中」推断中奖。
/// 「号码匹配」与「奖级/中奖结论」分离：无可靠奖级规则时用 PrizeUndetermined。
/// </summary>
public enum LottoHitStatus
{
    /// <summary>尚未开奖，不对奖。</summary>
    Pending = 0,
    /// <summary>已开奖且按已核实玩法规则判定中奖（如数字型直选全中、组选全中）。</summary>
    Won = 1,
    /// <summary>已开奖且按已核实玩法规则判定未中。</summary>
    Lost = 2,
    /// <summary>玩法无法识别，不计入有效投注命中率。</summary>
    Undetermined = 3,
    /// <summary>投注形态不合法，不计入有效投注命中率。</summary>
    Illegal = 4,
    /// <summary>
    /// 已开奖、号码匹配信息可用，但奖级规则未可靠实现 → 奖级未判定。
    /// 保留命中数/球标；不计入中奖率；不得标为 Won/Lost。
    /// </summary>
    PrizeUndetermined = 5
}

public static class LottoHitStatusRules
{
    public static string ToApi(LottoHitStatus s) => s switch
    {
        LottoHitStatus.Pending => "pending",
        LottoHitStatus.Won => "won",
        LottoHitStatus.Lost => "lost",
        LottoHitStatus.Undetermined => "undetermined",
        LottoHitStatus.Illegal => "illegal",
        LottoHitStatus.PrizeUndetermined => "prize_undetermined",
        _ => "undetermined"
    };

    /// <summary>仅 Won/Lost 计入有效投注命中率。奖级未判定/未知/非法不计。</summary>
    public static bool CountsInHitRate(LottoHitStatus s) =>
        s is LottoHitStatus.Won or LottoHitStatus.Lost;

    /// <summary>前端高亮「中奖」样式仅当 status=won。</summary>
    public static bool ShowAsWin(LottoHitStatus s) => s == LottoHitStatus.Won;

    /// <summary>是否展示球命中标记（含奖级未判定）。</summary>
    public static bool ShowMatchMarks(LottoHitStatus s) =>
        s is LottoHitStatus.Won or LottoHitStatus.Lost or LottoHitStatus.PrizeUndetermined;
}
