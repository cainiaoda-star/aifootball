using AiFootBall.Core.Models;
using AiFootBall.Data;

namespace AiFootBall.Sources;

/// <summary>
/// 北单「让球胜平负」推荐（2026-11-06 重写为让球盘）。
///
/// 关键纠正：北单主玩法是「让球胜平负」（带让球数 rangqiuNum），不是不让球。
/// 用户实盘看到的是「主队 -1 胜 @2.77」这种让球盘，不是不让球「主胜」。
/// 之前版本错把不让球方向当推荐方向、把平均欧赔当赔率，跟实盘对不上。
///
/// 现在直接复用引擎已算好的让球盘结果：
///   · RecHandicap：让球盘方向（3=让胜 / 1=让平 / 0=让负）；
///   · HandicapProbability：该让球方向打出的概率（引擎用让球盘去水或欧赔反推）；
///   · HandicapOdds：让球盘 SP（开奖前会变，参考值）。
/// 串关正路按 HandicapProbability 降序取前 10（宁缺毋滥）；
/// 必发反向信号场次（主队热门但钱跑客队）单独列「爆冷参考」，不进串关。
/// </summary>
public sealed class BdSpfPickService
{
    /// <summary>串关正路最多取多少场（10串1 需要 10 场）。</summary>
    public const int MaxSerialPicks = 10;

    /// <summary>客胜盈亏指数（PnlIdxAway）深负阈值：钱净流入客胜，主胜热门被爆冷的前兆。</summary>
    public const decimal AwayPnlDeepNegative = -60m;

    /// <summary>主胜盈亏指数（PnlIdxHome）深负阈值：钱在逃离主胜，说明主胜热门是「假热门」。
    /// 2026-11-06 从 -60 收紧到 -45：场5/6 这种「主胜必发≥70% 但主胜盈亏 -45~-52」的中等强度假热门，
    /// 之前卡在 -60 阈值外漏进串关正路，实际是教训1的翻车画像。</summary>
    public const decimal HomePnlDeepNegative = -45m;

    private readonly RecommendationService _svc;
    private readonly BettingRepository _cards;

    public BdSpfPickService(RecommendationService svc, BettingRepository cards)
    {
        _svc = svc;
        _cards = cards;
    }

    /// <summary>
    /// 生成当天北单让球盘推荐（串关用途）。返回 null 表示北单今天没有可选的场次。
    /// </summary>
    public async Task<BdSpfPickDay?> BuildAsync(CancellationToken ct = default)
    {
        var recs = await _svc.RecommendAsync(Boards.Bd, null, ct);

        // 北单一期（scheduleDate=今天）的场次会跨到次日凌晨开赛（最晚约 08:50）。
        // BettingCard 没存 scheduleDate/期号，只能用开赛时间界定「今晚这一期」：
        // 取 [今天 00:00, 明天 12:00) 开赛的场次，排除提前预售的未来几天场次。
        var today = DateTime.Today;
        var cutoff = today.AddDays(1).AddHours(12);
        var live = recs
            .Where(r => r.MatchId is > 0 && r.MatchTime is not null
                        && r.MatchTime >= today && r.MatchTime < cutoff
                        && r.MatchTime > DateTime.Now)
            .ToList();

        if (live.Count == 0) return null;

        var steady = new List<BdSpfPick>();
        var reverse = new List<BdSpfPick>();

        foreach (var r in live)
        {
            // 没有让球盘方向（引擎没算出让球结果）的场次跳过。
            if (r.RecHandicap is not char hside) continue;

            // 必发反向信号：主队被买成大热，但钱在逃离主胜/流入客胜 → 让球盘方向可能翻车，单独列不进串关。
            if (IsReverseSignal(r, hside))
            {
                reverse.Add(BuildPick(r, hside, reversed: true));
                continue;
            }

            // 串关正路：直接采用引擎的让球盘方向，按让球概率排序。
            steady.Add(BuildPick(r, hside, reversed: false));
        }

        // 串关正路：按让球盘概率降序取前 10（宁缺毋滥），再按场次号升序展示。
        steady = steady
            .OrderByDescending(p => p.HandicapProbability ?? 0)
            .Take(MaxSerialPicks)
            .OrderBy(p => MatchNoKey(p.MatchNo))
            .ToList();

        // 爆冷参考：按「钱往客胜跑」的坚决度（客胜盈亏深负优先）排序。
        reverse = reverse
            .OrderBy(p => p.PnlIdxAway ?? 999)
            .ToList();

        if (steady.Count == 0 && reverse.Count == 0) return null;

        return new BdSpfPickDay
        {
            Day = DateTime.Today,
            Steady = steady,
            Reverse = reverse
        };
    }

    /// <summary>场次号排序键：取末尾连续数字做数值排序（"周五001"→1、"25"→25），非数字兜底排最后。</summary>
    private static int MatchNoKey(string? matchNo)
    {
        if (string.IsNullOrWhiteSpace(matchNo)) return int.MaxValue;
        var digits = 0;
        for (var i = matchNo.Length - 1; i >= 0; i--)
        {
            if (!char.IsDigit(matchNo[i])) break;
            digits++;
        }
        if (digits == 0) return int.MaxValue;
        var tail = matchNo[^digits..];
        return int.TryParse(tail, out var n) ? n : int.MaxValue;
    }

    /// <summary>
    /// 反向价值：主队被买成大热，但钱在逃离主胜/流入客胜（教训 1 的「主胜大热反向」）。
    /// 前提是「主队本来是热门」——主胜必发占比得有一定热度（≥40%），
    /// 否则「钱在客胜」只是客队本来的强势，不是反向。
    ///
    /// 2026-11-06 补充方向维度：只有当「让球方向是让胜（hside='3'，追主队赢）」时，
    /// 「主胜大热 + 钱逃主胜」才是自相矛盾的翻车画像。若让球方向本来就是「让负」（已看衰主队），
    /// 那钱逃主胜与让负方向一致，是正确的注，不能误伤（例：场40让-1让负，PnlIdxHome=-52 反而印证让负）。
    /// </summary>
    private static bool IsReverseSignal(MatchRecommendation r, char hside)
    {
        if (r.RatioHome is not decimal rh || rh < 40m) return false;
        // 客胜盈亏深负（钱净流入客胜）——不管让球方向，主胜热门被客队爆冷的前兆。
        if (r.PnlIdxAway is <= AwayPnlDeepNegative) return true;
        // 主胜大热但钱在逃离主胜——只有「追主队的让胜」方向才自相矛盾，是假热门；
        // 「让负」方向本来就赌主队输盘，钱逃主胜反而印证它，不标反向。
        if (hside == '3' && rh >= 70m && r.PnlIdxHome is <= HomePnlDeepNegative) return true;
        return false;
    }

    private static BdSpfPick BuildPick(MatchRecommendation r, char hside, bool reversed)
    {
        // 让球盘方向的中文名。
        var hsideName = hside switch { '3' => "让胜", '1' => "让平", _ => "让负" };

        return new BdSpfPick
        {
            MatchId = r.MatchId ?? 0,
            MatchNo = r.MatchNo,
            LeagueName = r.LeagueName,
            HomeTeam = r.HomeTeam,
            AwayTeam = r.AwayTeam,
            MatchTime = r.MatchTime,
            // 不让球方向保留（仅参考，北单不卖），Side 仍按让球方向填以兼容旧前端，但真正投注看 HandicapSide。
            Side = hside,
            SideName = hsideName,
            Odds = r.HandicapOdds,
            Handicap = r.Handicap,
            HandicapSide = hside,
            HandicapSideName = hsideName,
            HandicapProbability = r.HandicapProbability,
            HandicapOdds = r.HandicapOdds,
            HitProbability = r.HitProbability,
            MarketProb = r.MarketProb,
            RatioHome = r.RatioHome,
            RatioAway = r.RatioAway,
            PnlIdxHome = r.PnlIdxHome,
            PnlIdxAway = r.PnlIdxAway,
            Kind = reversed ? "reverse" : "steady",
            Confidence = (int)Math.Round(r.HandicapProbability ?? 0),
            Reason = BuildReason(r, hside, reversed)
        };
    }

    private static string BuildReason(MatchRecommendation r, char hside, bool reversed)
    {
        var bits = new List<string>();
        var hcp = r.Handicap is decimal h ? (h > 0 ? $"+{h:0.##}" : $"{h:0.##}") : "?";
        bits.Add($"让{hcp}");
        if (r.HandicapProbability is decimal hp) bits.Add($"让球概率 {hp:0.#}%");
        if (r.RatioHome is decimal rh) bits.Add($"主胜必发 {rh:0.#}%");
        if (r.RatioAway is decimal ra) bits.Add($"客胜必发 {ra:0.#}%");
        if (r.PnlIdxAway is decimal pa && pa <= AwayPnlDeepNegative) bits.Add($"客胜盈亏 {pa:0}（钱入客胜）");
        if (reversed) bits.Add("主胜假热门反向，不进串关");
        return string.Join("；", bits);
    }
}
