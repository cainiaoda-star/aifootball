using AiFootBall.Core.Models;
using AiFootBall.Data;

namespace AiFootBall.Sources;

/// <summary>
/// 临场转向哨兵（2026-11-05 加）。
///
/// 为什么需要它：方向闸（MilkDirectionGate）只在出注那一刻跑一次，存的是静态快照——
/// 它回答的是「当时模型和市场各指哪儿」，回答不了「之后市场有没有偷偷转向」。
/// 而市场转向恰恰是时间序列上的漂移：让胜赔率一路爬升、净胜球期望持续下修、
/// 客队深档整批降价——净胜球诊断（FbScoreDiag）每 2 分钟在刷新，却没有一条代码
/// 把「已出注的场次临场发生了市场转向」主动推出来。这就是「早干啥去了」的答案：
/// 数据在采、信号在算，告警闭环断在最后一公里。
///
/// 本类补上这最后一公里：开赛前每 10 分钟扫一次当天已发出的未开赛大奶注，
/// 对比净胜球诊断的转向信号与注的方向，命中的落一条告警到 dbo.FbTurnAlert。
///
/// 判定是纯函数（<see cref="Judge"/>），数据获取由调用方（RecommendationService）注入，
/// 便于单测，也便于事后拿历史盘口回测「当时的告警该不该响」。
/// </summary>
public static class TurnGuard
{
    /// <summary>期望净胜球从开盘到现在的下修幅度超过这个值，才算「主队信心流失」（球）。</summary>
    public const decimal GdShrinkThreshold = 0.10m;

    /// <summary>客队深档（净负 ≥3）里赔率下降 ≥5% 的档数达到这个数，才算「整批降价」。</summary>
    public const int DeepDropThreshold = 3;

    /// <summary>让胜赔率从出注时到临场的拉升幅度超过这个比例，才算「主队赢盘被抛弃」。</summary>
    public const decimal RangShengRisePct = 0.08m;

    /// <summary>客胜盈亏指数（PnlIdx）深负阈值：钱在净流入客胜，主胜热门被客队爆冷的前兆。</summary>
    public const decimal AwayPnlDeepNegative = -60m;

    /// <summary>客胜必发占比异常走高阈值：低水热门场客胜占比通常 &lt;25%，超过即异常。</summary>
    public const decimal AwayRatioAbnormal = 30m;

    // 信号码（2026-11-08 加）：告警去重与「对注有利/不利」分流都靠它。
    // 以前拿整段 Reason 当去重键，而 Reason 里带实时数值（让胜 5.40→6.10），
    // 每 10 分钟数值一变就是一条新记录——2026-11-08 一天刷出 57 条，实际只有 11 场。
    public const string SignalGdShrink = "gdShrink";       // 期望净胜球下修
    public const string SignalDeepDrop = "deepDrop";       // 客队深档整批降价
    public const string SignalRangSheng = "rangSheng";     // 让胜赔率单边拉升
    public const string SignalAwayPnl = "awayPnl";         // 客胜盈亏指数深负
    public const string SignalAwayRatio = "awayRatio";     // 客胜必发占比异常
    public const string SignalFakeHotAway = "fakeHotAway"; // 客胜被竞彩单边砸成假热
    public const string SignalFakeHotHome = "fakeHotHome"; // 主胜被竞彩单边砸成假热

    /// <summary>
    /// 实证 EV 跌破本玩法返奖率（2026-11-08 加）。
    ///
    /// 这是**唯一一条与方向无关、与市场转不转向无关**的判据：它说的是「这个价本身就不值得买」。
    /// 触发场景是 2026-11-08 被用户抓出来的真事——
    ///   江原FC vs 全北现代 半全场负负 @5.30，基线概率 14.37%，实证 EV 0.762，
    ///   比半全场返奖率 0.797 还低（即：买入的期望回报比在同一玩法里随便挑一注更差）。
    ///   市场从头到尾没转向（赔率 5.25→5.30），于是转向哨兵一路报「无告警」，
    ///   而那一注从出票那一刻起就是负期望。哨兵只看「变了没有」，从不问「值不值」，这就是缺口。
    ///
    /// 阈值取**出票闸同款下限**（<see cref="BaselineEvFloor"/>），不另拍数字（2026-11-08 二次修正）。
    /// 最初的写法是「跌破返奖率本身」，方向对但和出票闸不一致：出票闸对比分/总进球只要求
    /// 返奖率×0.85，所以 EV 0.65 的比分注出票照过、哨兵下一轮立刻撤，系统在撤销自己刚出的票。
    /// 现在两边共用同一个下限函数：**出票时过得了的，哨兵不会因为同一条判据撤它**。
    /// </summary>
    public const string SignalEvBroken = "evBroken";

    /// <summary>
    /// 各玩法返奖率。**必须委托给 <see cref="MilkPickEngine.Payout"/>**，不许在这里另写一份常数
    /// （2026-11-08 修）：两边各写一份的后果是门槛悄悄漂移——当时这里是 0.710/0.797，
    /// 出票闸那边是 Payout × 0.85，于是比分/总进球的注只要 EV 落在
    /// [0.85×返奖率, 返奖率) 就会被「出票放行 → 下一轮哨兵立刻撤掉」，自己撤自己。
    /// </summary>
    public static decimal PaybackOf(string playType) => MilkPickEngine.Payout(playType);

    /// <summary>
    /// 撤注用的 EV 下限，**与出票闸 <see cref="MilkPickEngine.PassesBaselineEv"/> 同源**（2026-11-08）。
    ///
    /// 哨兵只在「出票时过闸、之后跌破了」才该撤。若撤注门槛比出票门槛更严，
    /// 就等于每一轮都在撤销自己刚出的票，榜面永远在抖动 —— 那不是风控，是自相矛盾。
    /// 门槛的唯一真源留在 MilkPickEngine 里，这里只做取用。
    /// </summary>
    public static decimal BaselineEvFloor(string playType)
        => PaybackOf(playType) * FloorRatioOf(playType);

    /// <summary>出票闸对该玩法用的下限倍率（半全场 1.00、比分/总进球 0.85），供诊断文案展示。</summary>
    public static decimal FloorRatioOf(string playType)
        => playType == JcPlayTypes.Bq ? MilkPickEngine.MinBaselineEvRatioBq : MilkPickEngine.MinBaselineEvRatio;

    /// <summary>
    /// 给一场已发出的注做转向诊断。返回要落库的告警，没有转向则返回 null。
    ///
    /// <paramref name="pickDir"/> 是这一注指向的胜负方向（3 主胜 / 1 平 / 0 客胜），
    /// 总进球没有方向，传 null（此时只看信号本身，不判「与注相反」）。
    /// <paramref name="awayMoney"/> 是必发静态资金快照（客胜盈亏指数 PnlIdxAway、客胜占比 RatioAway）。
    /// 前三个信号是「临场时间序列转向」，这个第四个是「静态资金异常」——
    /// 主胜大热反向/客胜资金涌入这种信号没有时间漂移，净胜球诊断不会报，
    /// 但它是欧战低水热门被爆冷（AC米兰/勒沃库森/桑德兰）最直接的铁证。
    /// </summary>
    public static TurnAlertRow? Judge(
        MilkPickRow pick,
        char? pickDir,
        ScoreDiagRow? diag,
        (decimal OddsAtIssue, decimal OddsNow)? rangSheng,
        (decimal? PnlIdxAway, decimal? RatioAway)? awayMoney = null,
        PriceDivergence.Result? divergence = null)
    {
        var payback = PaybackOf(pick.PlayType);

        var notes = new List<string>();
        var codes = new List<string>();
        var strong = new List<string>();   // 有实测支撑的强信号（诺丁汉/欧罗巴那批铁证）

        // 信号 0（绝对值判据）：实证 EV 已跌破**出票闸同款下限**。见 SignalEvBroken 注释。
        //
        // ★ 门槛必须与出票闸一致（2026-11-08 修）：原来这里拿「返奖率本身」当线，
        //   而出票闸对比分/总进球只要求「返奖率×0.85」，于是 EV 0.65 的比分注
        //   出票照过、下一轮哨兵立刻判 high 撤注 —— 系统在撤销自己刚出的票。
        var evFloor = BaselineEvFloor(pick.PlayType);
        var evBroken = pick.BaselineEv is decimal bev && bev > 0m && bev < evFloor;
        if (evBroken)
        {
            codes.Add(SignalEvBroken); strong.Add(SignalEvBroken);
            notes.Add($"实证 EV {pick.BaselineEv:0.000} 跌破出票下限 {evFloor:0.000}"
                      + $"（本玩法返奖率 {payback:0.000}）——出票时还没这么差，之后被市场压下去了");
        }

        // 无方向玩法（总进球）与平局方向不报「转向」类信号（2026-11-08 改）。
        //
        // 原来 pickDir 为 null 时照样报「客胜必发占比 76%」，于是 水晶宫vs利物浦「总进球 2 球」
        // 这类注被连报十几次——客胜占比高跟「总进球是不是 2 个」没有半毛钱关系，纯噪音。
        // 平局方向（平平/胜平/平负…）同理：库里没有任何「平局会被什么信号推翻」的实测证据，
        // 硬报就是拿猜测冒充告警。宁可不说，也不制造噪音。
        //
        // ★ 但信号 0 是绝对判据，跟方向无关：总进球、平局一样会被它拦下。所以它放在这道闸之前。
        var dir = pickDir;
        var directional = dir is char dd && dd != '1';

        // 定价背离与方向无关（总进球/平局也可能撞上假价），所以在块外先算好。
        var fakeHotAway = divergence is { AwayFakeHot: true };
        var fakeHotHome = divergence is { HomeFakeHot: true };

        if (diag is not null && directional)
        {

        // 信号 1：净胜球期望下修——市场对主队信心在流失。
        var gdShrunk = diag.ExpGdMove is decimal move && move <= -GdShrinkThreshold;
        if (gdShrunk)
        {
            codes.Add(SignalGdShrink); strong.Add(SignalGdShrink);
            notes.Add($"期望净胜球 {diag.ExpGdOpen:0.000}→{diag.ExpGd:0.000}（下修 {diag.ExpGdMove:0.000}）");
        }

        // 信号 2：客队深档整批降价——资金在往客队方向压。
        var deepDrop = diag.DeepNegDrop >= DeepDropThreshold
                       || (diag.TailTag?.Contains("客队深档", StringComparison.Ordinal) ?? false);
        if (deepDrop)
        {
            codes.Add(SignalDeepDrop); strong.Add(SignalDeepDrop);
            notes.Add($"客队深档整批降价（{diag.DeepNegDrop} 档），尾盘标签「{diag.TailTag}」");
        }

        // 信号 3：让胜赔率单边拉升——主队赢盘被市场抛弃。
        var rangShengUp = rangSheng is { } r && r.OddsAtIssue > 0
                          && r.OddsNow >= r.OddsAtIssue * (1m + RangShengRisePct);
        if (rangShengUp)
        {
            codes.Add(SignalRangSheng);
            notes.Add($"让胜赔率 {rangSheng!.Value.OddsAtIssue:0.00}→{rangSheng.Value.OddsNow:0.00}（单边拉升）");
        }

        // 信号 4（静态资金异常）：钱往客胜跑，主胜热门被爆冷的前兆。
        // 这个信号没有时间漂移——它是「主胜大热反向/客胜资金涌入」的静态铁证，
        // 净胜球诊断（每 2 分钟刷新的时间序列）报不出来，必须单独看必发静态快照。
        var awayPnlDeep = awayMoney is { } am && am.PnlIdxAway is <= AwayPnlDeepNegative;
        var awayRatioHot = awayMoney is { } am2 && am2.RatioAway is >= AwayRatioAbnormal;
        if (awayPnlDeep)
        {
            codes.Add(SignalAwayPnl); strong.Add(SignalAwayPnl);
            notes.Add($"客胜盈亏指数 {awayMoney!.Value.PnlIdxAway:0}（深负，钱净流入客胜）");
        }
        if (awayRatioHot)
        {
            codes.Add(SignalAwayRatio);
            notes.Add($"客胜必发占比 {awayMoney!.Value.RatioAway:0.#}%（异常走高）");
        }

        // 信号 5（定价背离，2026-11-08 加）：竞彩单边砸价、必发不认。
        // 与信号 1-4 不是一回事——那四条是「市场在转向」，这一条是「这个价本身就是假的」：
        // 2026-11-08 英超 诺丁汉森林 3:1 曼城，竞彩客胜 1.60→1.43（-10.6%），
        // 必发 PriceAway 只动 -1.1%。散户在竞彩砸热门，专业资金一个价都不接，结果直接翻车。
        // 回测见 PriceDivergence：客胜侧该形态 13 场实际 23.1% vs 隐含 40.1%（-17 点），
        // 主胜侧 9 场 +3.4 点（无偏离，所以主胜侧这里只报 mid，不报 high）。
        if (fakeHotAway)
        {
            codes.Add(SignalFakeHotAway); strong.Add(SignalFakeHotAway);
            notes.Add($"客胜被竞彩单边砸价（{divergence!.Away!.Value.JcPct:0.#}%），必发只动 {divergence.Away.Value.ExPct:0.#}%");
        }
        if (fakeHotHome)
        {
            codes.Add(SignalFakeHotHome); strong.Add(SignalFakeHotHome);
            notes.Add($"主胜被竞彩单边砸价（{divergence!.Home!.Value.JcPct:0.#}%），必发只动 {divergence.Home.Value.ExPct:0.#}%");
        }
        }   // ← if (diag is not null && directional) 到此结束；信号 0 不受它约束

        if (codes.Count == 0) return null;

        // ★ 方向性筛选（2026-11-08 重写）：只有「对注不利」的信号才报警。
        //
        // 旧逻辑的致命缺陷：让胜赔率拉升、客胜资金流入这些信号，只有在注押**主胜**时才是坏消息；
        // 注押**客胜**时它们恰恰是印证。旧代码却一律照报，于是
        //   江原FC vs 全北现代 半全场 负负（押客胜）被「让胜 5.40→6.10 拉升 + 客胜盈亏 -99」报警 6 次，
        // 而这三条信号全部指向客队走强——是这注的利好，不是风险。
        // 报警喊反了，比不喊更害人：用户看到「告警」去撤一注本来正确的单子。
        //
        // 现在按注的方向分流：
        //   押主胜(dir=3)：净胜球下修 / 客队深档降价 / 让胜拉升 / 客胜盈亏深负 / 客胜占比异常 / 主胜假热 → 不利
        //   押客胜(dir=0)：只有「客胜自己被砸成假热」才是坏消息，其余全是有利
        //   押平局 / 无方向：不报（见方法开头）
        List<string> againstCodes;
        var priceFakeAgainstPick = false;
        if (dir == '3')
        {
            againstCodes = codes.Where(c => c != SignalFakeHotAway).ToList();
        }
        else if (dir == '0')
        {
            // 押客胜：转向类信号全是印证，只留「EV 破线」与「客胜自己被砸成假热」。
            againstCodes = codes.Where(c => c == SignalEvBroken).ToList();
            if (fakeHotAway) { againstCodes.Add(SignalFakeHotAway); priceFakeAgainstPick = true; }
        }
        else
        {
            // 平局 / 无方向（总进球）：只剩信号 0 可报——它是绝对判据，跟方向无关。
            againstCodes = codes.Where(c => c == SignalEvBroken).ToList();
        }

        if (againstCodes.Count == 0) return null;

        // 至少要有一个「有实测支撑」的强信号才算告警。
        // 只靠「客胜必发占比 76%」这种单薄的静态占比，在强客队场次（利物浦/AC米兰做客）是常态，
        // 单独拿来报警会把主榜淹掉——2026-11-08 一天就刷了 57 条重复告警，其中大半是这一条。
        var againstStrong = againstCodes.Count(c => strong.Contains(c));
        if (againstStrong == 0) return null;

        // 高危判据：出现假热，或强信号与其它信号叠加（≥2 条同向）。
        // 与旧版的差别：旧版 high 只有「下修+深档降价」和「盈亏深负+其一」两种组合，
        // 诺丁汉森林 3:1 曼城 那场「深档降价 5 档 + 让胜 2.00→2.28 + 客胜 79.9%」反而只给了 mid。
        //
        // ★ evBroken 被移出 high（2026-11-08 晚，第二次修）：它不够格撤销一注。
        //
        // 算一下就清楚：江原 负负 @5.30 的基线 14.37%，而 5.30 这个竞彩价去水后隐含 15.04%，
        // 两者只差 4 个百分点——这 4 个点全是「分档均值 vs 单场概率」的估计误差
        // （同一档内平平率就能从 12% 摆到 22%），拿它判「这注该撤」等于拿噪声杀人。
        // 更要紧的是**职责重复**：出票闸 MinBaselineEvRatioBq=1.00 已经在源头要求
        // EV ≥ 返奖率，能上到榜上的注不可能是 0.762。同一条判据在出票和撤注各执行一次，
        // 不增加任何信息，只会在赔率小幅波动时把榜面搅得天天抖。
        //
        // 所以现在的分工是：**出票闸管「值不值」，撤注闸只管「市场明确反对」**。
        // evBroken 仍会报，但只作 mid 红标提示，不撤注。
        var high = againstCodes.Count >= 2 || priceFakeAgainstPick;

        // 只报了 EV 破线时，文案不能说「市场转向」——那是另一回事，混在一起会误导。
        var onlyEv = againstCodes.Count > 0 && againstCodes.All(c => c == SignalEvBroken);
        var reason = string.Join("；", notes);
        if (againstCodes.Count < codes.Count)
            reason += $"（另有 {codes.Count - againstCodes.Count} 条信号指向相反方向，已忽略）";
        reason = priceFakeAgainstPick
            ? $"这一注指{MilkDirectionDiagnosis.DirName(dir)}，但该侧价格是竞彩单边砸出来的、必发不认：" + reason
            : onlyEv
                ? $"这一注按实证基线偏低，但差距在估计误差内（只标红、不撤注）：" + reason
                : $"这一注指{MilkDirectionDiagnosis.DirName(dir)}，市场在临场反向转向：" + reason;

        return new TurnAlertRow
        {
            // PickId / PlayType / OptionCode / SignalCode 是闭环用的键（2026-11-08 加）。
            // 只存 PickId 不够：每次重算都是 DELETE + INSERT，PickId 会变，
            // 撤注标记就会在下一轮重算后凭空消失——所以用 (MatchId, PlayType, OptionCode) 这个稳定三角做关联。
            PickId = pick.PickId,
            PlayType = pick.PlayType,
            OptionCode = pick.OptionCode,
            SignalCode = string.Join(",", againstCodes.OrderBy(c => c, StringComparer.Ordinal)),
            MatchId = pick.MatchId,
            MatchNo = pick.MatchNo,
            LeagueName = pick.LeagueName,
            HomeTeam = pick.HomeTeam,
            AwayTeam = pick.AwayTeam,
            MatchTime = pick.MatchTime,
            PickOption = pick.PlayType switch
            {
                JcPlayTypes.Bq => $"半全场 {MilkPickMath.ShortName(pick.PlayType, pick.OptionCode)}",
                JcPlayTypes.Bf => $"比分 {pick.OptionCode}",
                JcPlayTypes.Jq => $"总进球 {MilkPickMath.ShortName(pick.PlayType, pick.OptionCode)}",
                _ => pick.OptionName
            },
            Reason = reason,
            Severity = high ? "high" : "mid",
            ScoreDiagTag = diag?.TailTag,
            ExpGdOpen = diag?.ExpGdOpen,
            ExpGdNow = diag?.ExpGd,
            ExpGdMove = diag?.ExpGdMove
        };
    }

    /// <summary>从一条大奶注反推它指向的胜负方向（与 MilkPickEngine.FullTimeDirection 同口径）。</summary>
    public static char? PickDirection(string playType, string optionCode)
    {
        switch (playType)
        {
            case JcPlayTypes.Bq:
                return optionCode.Length == 3 ? optionCode[2] : null;

            case JcPlayTypes.Bf:
            {
                if (optionCode.EndsWith("其它", StringComparison.Ordinal))
                    return optionCode[0] switch { '胜' => '3', '平' => '1', '负' => '0', _ => null };
                var s = SplitScore(optionCode);
                if (s is null) return null;
                return s.Value.H > s.Value.A ? '3' : s.Value.H == s.Value.A ? '1' : '0';
            }

            default:
                return null;
        }
    }

    private static (int H, int A)? SplitScore(string code)
    {
        var parts = code.Split(':');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var a)) return null;
        return (h, a);
    }
}

/// <summary>
/// 一轮临场哨兵的执行结果（2026-11-08 加）。
/// 除了「喊了几声」，还要报「喊完之后榜面变了什么」——<see cref="Voided"/> 是
/// 已从主榜撤下的注数，<see cref="Recalculated"/> 表示本轮是否因新高危触发了全量重算。
/// </summary>
public sealed class TurnGuardRunResult
{
    public int Alerts { get; set; }
    public int High { get; set; }
    public int NewHigh { get; set; }
    /// <summary>被打上红标的注数（含 mid）。</summary>
    public int Marked { get; set; }
    /// <summary>当前处于「已撤注」状态的注数。</summary>
    public int Voided { get; set; }
    public bool Recalculated { get; set; }
}

/// <summary>
/// 哨兵的单注诊断（2026-11-08 加，只读）。
/// 目的：让「这注为什么没被告警」变成可查的结论，而不是一句「本轮无告警」。
/// </summary>
public sealed class TurnGuardScanRow
{
    public long PickId { get; set; }
    public long MatchId { get; set; }
    public string? MatchNo { get; set; }
    public string? HomeTeam { get; set; }
    public string? AwayTeam { get; set; }
    public DateTime? MatchTime { get; set; }
    public string PlayType { get; set; } = "";
    public string OptionCode { get; set; } = "";
    public string? OptionName { get; set; }
    public decimal Odds { get; set; }
    /// <summary>这一注的实证 EV（基线概率×赔率）。null = 当时没落基线值。</summary>
    public decimal? BaselineEv { get; set; }
    /// <summary>本玩法返奖率，用来判断 EV 破没破线。</summary>
    public decimal Payback { get; set; }
    /// <summary>实证 EV 是否已跌破返奖率（唯一与方向无关的判据）。</summary>
    public bool EvBroken { get; set; }
    /// <summary>这一注指向的胜负方向（主胜/平/客胜/无方向）。</summary>
    public string Direction { get; set; } = "";
    public bool Alerted { get; set; }
    public string? Severity { get; set; }
    public string? Reason { get; set; }
    public string? SignalCode { get; set; }
    /// <summary>没报警时的原因说明（前端直接显示，用户不用猜）。</summary>
    public string Verdict { get; set; } = "";
}
