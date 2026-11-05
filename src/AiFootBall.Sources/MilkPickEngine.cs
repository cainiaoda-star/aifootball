using AiFootBall.Core.Models;

namespace AiFootBall.Sources;

/// <summary>
/// 竞彩大奶推荐引擎（2026-11-04 改向）。
///
/// 用户指示：胜负彩、北单推荐下线，每日只推荐竞彩大奶——
///   · 比分（BF）：官方固定奖金全部比分档，赔率 4.0~<see cref="MaxScoreOdds"/>；
///   · 半全场（BQ）：只发 胜胜 / 平平 / 负负（2026-11-04 晚实测逆转档净亏，见 <see cref="HalfFullCombos"/>）；
///   · 总进球（JQ）：官方 0~7+ 档，5 球档不发。
///
/// 概率全部来自 Dixon-Coles 比分网格（<see cref="ScorelineModel"/>）：
///   · 比分 = 单格概率；
///   · 总进球 = 对角线合计，7+ = 1 减累计；
///   · 半全场 = 上半场（约 45% 进球份额）× 全场，两个泊松近似独立。
/// 半全场是近似值，比让球盘的去水粗糙一个量级，只用来排序，不当命中率承诺。
///
/// 排序按「模型概率」，不是 EV（2026-11-04 用户定调「目的是提高中奖率」后改的口径）。
/// EV = 概率 × 赔率，赔率越离谱越占便宜，于是 1.9% 的 3:0@80 会压过 17.8% 的 胜胜@9.5；
/// 命中的次数远比单注金额重要，所以榜单先按概率降序，EV 只当参考列。
/// 大奶的返还率天然低于 1，列出 EV 不是说这注该买，是把「哪一档相对最不亏」摆在明面上。
/// </summary>
public static class MilkPickEngine
{
    /// <summary>
    /// 方向闸是否「否决出注」。默认 false —— 只记诊断、不砍注。
    ///
    /// 2026-11-05 上线时是 true，当天就被自己的大样本检验推翻，改回 false：
    /// 用全库 444 场、3745 注半全场（赔率 ≥3，收盘价、开赛前快照）分组，
    /// 按「注的方向是否等于竞彩 SPF 去水最强侧」拆开——
    ///
    ///   赔率档   同向命中（回报）      反向命中（回报）
    ///   3-5      18.2%（-32.0%）      21.8%（-6.1%）
    ///   5-8       9.6%（-48.2%）      14.9%（-7.2%）
    ///   12-20     1.8%（-65.5%）       3.1%（-53.3%）
    ///   20-40     3.6%（-13.3%）       1.2%（-69.3%）
    ///
    /// 前四档（占注量 95%）里反向都比同向好，只有 20-40 档反过来，那一档样本 332 注。
    /// 也就是说：这道闸挑中的恰是市场系统性高估的那一类注，它是负 alpha，不是过滤器。
    ///
    /// 那 11-04 三场为什么看起来有效？因为 6 场里市场方向蒙对 5 场（期望约 56%），
    /// 是运气；用运气当验证，等于把随机波动写成规则。
    ///
    /// 保留这个开关是因为「结论可能再变」：等样本翻倍、或加上亚盘/必发后分组重测，
    /// 万一某一档出现稳定正 alpha，再按档开、而不是全开全关。在那之前，别拿它砍注。
    /// </summary>
    /// 用 static readonly 而不是 const：const false 会让编译器把整段判定当死代码消掉，
    /// 连漏斗计数都编不进去，将来按档开闸时还得再改一遍。
    public static readonly bool EnforceDirectionVeto = false;

    /// <summary>
    /// 出注门槛：主客队各自在训练集里的历史场次（2026-11-05 加）。
    ///
    /// 这是目前唯一一条「有回测量支撑」的过滤器，依据见 src/AiFootBall.Backtest：
    /// 2405 场无泄漏样本上，模型 Brier 0.6270 vs 市场 0.5769；但把场次拆开看，
    ///   两队都认识 → 模型 0.6232 / 市场 0.5857（差 0.037）
    ///   至少一队不认识 → 模型 0.6479 / 市场 0.5289（差 0.119，3.2 倍）
    /// 也就是说模型「认识但样本稀」时给的概率接近「联赛均值 + 主场优势」，不是这两队的对阵。
    /// 周四011（阿尔克马 vs 尤文）两队各只有 2 场欧罗巴样本，就是这么被判「胜胜 27%」的。
    ///
    /// 取 3 而不是 5：2026-11-05 用户定调「放宽门槛，先让欧战出注」。
    /// 欧战球队（欧冠/欧罗巴/欧协联/亚冠）在本赛事一年只踢 10~15 场，5 场门槛会把
    /// 周四这种欧战日整盘熔断（当天 13 场 12 场样本不足）。降到 3 能救活 8/13 场。
    /// 代价：回测分层显示 3-5 场组 Brier 0.6239，仍比市场差（0.5857），
    /// 比 0-2 场组（0.6372）好、比瞎猜基线（0.6478）好，但谈不上准。
    /// 这是「先出注、标风险」的产品取舍，不是「变准了」。样本积累到 10+ 场时应回调到 5。
    /// </summary>
    public const int MinTeamGames = 3;

    /// <summary>半全场大奶线。用户 2026-11-04 指定：4.0 以下不发。</summary>
    public const decimal MinHalfFullOdds = 4.0m;

    /// <summary>比分 / 总进球的大奶线。与半全场共用同一条线，要动一起动。</summary>
    public const decimal MinOptionOdds = 4.0m;

    /// <summary>
    /// 比分赔率上限（2026-11-04 晚按实测加的，当夜从 100 收紧到 40）。
    ///
    /// 竞彩 453~519 场实测命中率：4~6 倍 6.2% / 6~10 倍 11.4% / 10~20 倍 8.4% /
    /// 20~40 倍 6.0% / 40~60 倍 6.1% / 60~100 倍 5.3% / 100 倍以上 4.4%。
    /// 40 倍以上命中率开始塌，且 100 倍以上档被几个千倍冷门单点拉爆，样本极不稳。
    /// 用户 2026-11-04 定调「目的是提高中奖率」，所以砍到 40：3:0@80、3:1@50 这类不再上榜。
    /// </summary>
    public const decimal MaxScoreOdds = 40m;

    /// <summary>
    /// 不发档：总进球 5 球。竞彩 453 场实测这一档平注回报 0.591，是八档里最差的，
    /// 明显差于相邻的 4 球（0.757）和 6 球（0.737），属于庄家摆明了要多收的档。
    /// </summary>
    public static readonly string[] BannedGoalBuckets = { "5" };

    /// <summary>
    /// EV 上限。竞彩整体返还率约 85%，任何一注的真实期望都不该接近 2；
    /// 算出 3 倍、5 倍的，是模型在冷门格子上给了不切实际的概率，不是市场送钱。
    ///
    /// 为什么**没有** EV 下限（2026-11-05 特意写明，别再加）：
    /// 概率是模型 70% + 盘口去水 30% 掺出来的，EV = 0.7·p模型·赔率 + 0.3/水位。
    /// 水位是官方常数，于是 EV 的理论天花板就是该玩法的返奖率：比分 0.71、半全场/总进球 0.80。
    /// 想让 EV ≥ 1，模型得认为市场低估了 1.4 倍以上——而实测模型 MAE 1.53 球、比市场的 1.33 还差。
    /// 加 EV ≥ 1 的闸不是修 bug，是把榜永久清空（11-04 那 6 条最高才 0.951）。
    ///
    /// EV 在这张榜上的正确读法只有一句：它等于「这注的长期回报」，最高也就是返奖率，
    /// 所以买竞彩永远是负期望，能选的只是「亏得慢一点的玩法」。要少亏就避开水位高的玩法
    /// （比分 71%），别串关（水位相乘：0.71×0.80×0.80 ≈ 0.45）。
    /// </summary>
    public const decimal MaxEv = 2.5m;

    /// <summary>
    /// 市场权重：最终使用的概率 = (1 - 本值) × 模型概率 + 本值 × 盘口去水隐含概率。
    ///
    /// 为什么要掺盘口（2026-11-04 晚加）：当天实测到模型和盘口直接对着干——
    /// 费内巴切 vs 利物浦，模型给「胜胜」17.8%（等于说主胜至少 21%），
    /// 而盘口把这场的客胜比分全压在 6.7~9.5 倍（客胜合计约 40%、主胜不到 15%）。
    /// 模型是联赛历史拟合出来的，主队主场优势被反复高估；盘口是几百万人真金白银压出来的。
    /// 目标是中奖率，那就该按「谁更准」分配权重，而不是无条件相信自己的模型。
    ///
    /// 0.6 → 0.3（2026-11-05 调，依据是大奶盘回测：模型 vs 市场的完整对比）——
    /// 注意：这只针对大奶盘（比分/总进球/半全场）。方向盘（胜平负）上模型明确打不过市场
    /// （北单 1.8 万场标尺：模型 Brier 0.667 vs 市场 0.642），所以方向仍应信市场。
    /// 但大奶盘是另一回事，回测（约 500 场竞彩收盘赔率）三个玩法模型全面压过市场：
    ///   比分   Top-1 8.2% vs 6.4%，Brier 0.9474 vs 0.9833（模型更准）
    ///   总进球 Top-1 24.4% vs 19.7%，Brier 0.8564 vs 0.8504（基本持平）
    ///   半全场 Top-1 34.5% vs 22.7%，Brier 0.8010 vs 0.8672（模型显著更准）
    /// 结论：小众盘上盘口没有碾压性优势，反而模型排序更优。所以权重从「市场 60%」倒向
    /// 「模型 70%」。样本约 500 场仍偏小，0.3 是「信模型、但不全信」的折中，不是 0。
    /// 等样本攒到 3000+ 场再决定要不要继续往模型倾斜。
    /// </summary>
    public const decimal MarketWeight = 0.3m;

    /// <summary>一个玩法所有选项的 (1/赔率) 之和（观测值）。只用于诊断快照完整性，不参与去水。</summary>
    public static decimal ObservedOverRound(IEnumerable<JcOddsHistoryPoint> points, string playType)
        => points.Where(p => p.PlayType == playType && p.Odds > 1m).Sum(p => 1m / p.Odds);

    /// <summary>
    /// 各玩法的固定水位（Σ1/赔率），也就是「返奖率」的倒数。
    ///
    /// 2026-11-05 实测同一批次 13 场，逐场水位几乎一模一样：
    ///   比分 31 档 1.409~1.410（返奖 71%）· 半全场 9 档 1.253~1.255（80%）
    ///   总进球 8 档 1.253~1.255（80%）· 胜平负 3 档 1.138~1.140（88%）
    /// 也就是说水位是玩法级常数，不是每场现算出来的东西。
    ///
    /// 为什么必须用常数去水，而不是拿快照的 Σ1/赔率（2026-11-05 修的真 bug）：
    /// 11-04 那 6 条推荐，用 EV 反解出来的水位是 1.05 / 1.12 / 1.26，全部低于常数——
    /// 因为生成推荐时盘口还没开全，快照里只有部分档位，Σ1/赔率 被做小，去水后每个档的
    /// 市场概率被凭空放大 10~20%。掺进模型等于给「模型 + 假盘口」抬轿：概率虚高、EV 虚高、
    /// 烂注被顶到榜首。改用常数就与快照抓没抓全无关。
    /// </summary>
    public static decimal StandardOverRound(string playType) => playType switch
    {
        JcPlayTypes.Bf => 1.410m,
        JcPlayTypes.Bq => 1.254m,
        JcPlayTypes.Jq => 1.254m,
        _ => 1.139m
    };

    /// <summary>
    /// 该玩法的返奖率 = 1/水位。这是竞彩硬编码的抽水比例，也是任何一注长期回报的天花板：
    /// 比分 71%、半全场 80%、总进球 80%、胜平负 88%。榜上直接标给用户，别让人误以为能算到 1 以上。
    /// </summary>
    public static decimal Payout(string playType) => Math.Round(1m / StandardOverRound(playType), 3);

    /// <summary>
    /// 竞彩每个玩法的档数是固定的（比分 31 / 半全场 9 / 总进球 8 / 胜平负 3）。
    /// 快照抓到的档数不足，说明生成推荐时盘口还没开全：这时拿「全档的水位」去水会把
    /// 仅有的几档隐含概率放大（分母是全档，分子只有几档的 1/赔率），必须退回纯模型概率。
    /// </summary>
    public static bool SnapshotComplete(IEnumerable<JcOddsHistoryPoint> points, string playType)
    {
        var expected = playType switch
        {
            JcPlayTypes.Bf => 31,
            JcPlayTypes.Bq => 9,
            JcPlayTypes.Jq => 8,
            JcPlayTypes.Spf => 3,
            JcPlayTypes.Nspf => 3,
            _ => 0
        };
        if (expected == 0) return false;
        return DistinctOptionCount(points, playType) >= expected;
    }

    private static int DistinctOptionCount(IEnumerable<JcOddsHistoryPoint> points, string playType)
        => points.Where(p => p.PlayType == playType && p.Odds > 1m)
                 .Select(p => p.OptionCode)
                 .Distinct(StringComparer.Ordinal)
                 .Count();

    /// <summary>
    /// 把模型概率（0-1 比例）和盘口隐含概率加权。overRound 必须是该玩法的官方固定水位
    /// （见 <see cref="StandardOverRound"/>），不要传本场快照的观测值——盘口没开全时会失真。
    /// </summary>
    private static decimal Blend(decimal modelFraction, decimal odds, decimal overRound)
    {
        if (odds <= 1m) return modelFraction;
        if (overRound <= 0m) return modelFraction;
        var marketFraction = 1m / odds / overRound;
        if (marketFraction <= 0m) return modelFraction;
        return (1m - MarketWeight) * modelFraction + MarketWeight * marketFraction;
    }

    /// <summary>全榜上限 = 三个玩法名额之和（比分 5 + 半全场 4 + 总进球 3）。</summary>
    public const int MaxPicks = 12;

    /// <summary>
    /// 各玩法的「单注概率下限」，也就是这条榜的门槛命中率。
    ///
    /// 2026-11-04 用户定调「目的是提高中奖率」后由 1.5% 大幅收紧：
    ///   · 比分 5%：实测 1:1 11.5% / 2:1 9.6% / 1:0 7.9% / 2:0 7.2% / 0:0 6.6% 是主力档，
    ///     3:1 4.0%、3:2 3.8%、4:0 2.8% 全部出局。原先进过 1.9% 的 3:0，纯属噪声。
    ///   · 半全场 12%：三个顺风档的实测命中率是 胜胜 36.6% / 负负 23.6% / 平平 17.7%，
    ///     这条线等于只留「真正站得住的」，模型算不到 12% 的场次不发。
    ///   · 总进球 10%：实测 3 球 23.8% / 2 球 21.2% / 1 球 13.0% / 4 球 15.9% 合格，
    ///     0 球 6.0% / 6 球 6.2% / 7+ 5.7% / 5 球 8.2%（另已整档停发）全部出局。
    ///
    /// 代价说清楚：门槛越高，赔率越低、上榜越少。要的不是「天天有票」，是「推的那注真的会中」。
    /// </summary>
    public const decimal MinScoreProbability = 5.0m;

    /// <inheritdoc cref="MinScoreProbability"/>
    public const decimal MinHalfFullProbability = 12.0m;

    /// <inheritdoc cref="MinScoreProbability"/>
    public const decimal MinGoalProbability = 10.0m;

    /// <summary>按玩法取门槛命中率。新增玩法忘了配会返回 0（不设闸），编译期不报错，别漏。</summary>
    public static decimal ProbabilityFloor(string playType) => playType switch
    {
        JcPlayTypes.Bf => MinScoreProbability,
        JcPlayTypes.Bq => MinHalfFullProbability,
        JcPlayTypes.Jq => MinGoalProbability,
        _ => 0m
    };

    /// <summary>
    /// 同一场、同一玩法最多留几注（2026-11-04 用户拍板加的上限）。
    /// 费内巴切-利物浦那场一口气占了 6 注，榜看着像只推了一场；
    /// 同一场堆四五个比分，本质还是把钱压在同一个剧本上，不叫分散。
    /// </summary>
    public const int MaxPerMatchPerPlay = 2;

    /// <summary>票腿概率下限。2026-11-04 晚从 4% 提到 8%：既然目标是中奖率，就别让 5% 的比分腿进票。</summary>
    public const decimal MinTicketLegProbability = 8m;

    /// <summary>整票中奖率下限。2串1 两腿各 4% 只有 0.16%，那是捐献不是投注。</summary>
    public const decimal MinTicketProbability = 2.5m;

    /// <summary>上半场进球约占总进球的份额。泊松近似的标准取值。</summary>
    public const double HalfGoalShare = 0.45;

    /// <summary>
    /// 半全场只发 胜胜 / 平平 / 负负 三个顺风组合（半-全，3胜 1平 0负）。
    ///
    /// 2026-11-04 晚改：原先是「胜负/负胜/胜平/负平」四个逆转组合，实测必须废掉——
    /// 竞彩 453 场完场样本、1812 个逆转选项只命中 41 次（2.26%），平均赔率 24.10，
    /// 平注回报率 0.451，分项 0.38~0.53，赔率给得再高也填不平真实概率。
    /// 相反胜胜/平平/负负 的回报率是 1.04~1.11（赔率 4.46/6.98/7.19，仍在大奶线上）：
    /// 竞彩半全场的钱几乎全压在逆转冷门上，顺风档反而被低估。
    ///
    /// 顺带解决用户指出的「比分 3:0 配半全场负胜」矛盾——负胜要求客队半场进过球，
    /// 3:0 是零封，两个剧本压根不可能同场发生。换成顺风档后方向天然一致。
    /// </summary>
    public static readonly string[] HalfFullCombos = { "3-3", "1-1", "0-0" };

    /// <summary>
    /// 生成当日大奶榜（只要榜，不要诊断）。保持旧签名，页面上那两条只读接口还在用。
    /// 需要「哪场被方向闸挡了、为什么」的调用方走 <see cref="BuildDetailed"/>。
    /// </summary>
    public static List<MilkPick> Build(
        IReadOnlyList<MatchRecommendation> recs,
        IReadOnlyDictionary<long, List<JcOddsHistoryPoint>> odds,
        ScorelineModel scores)
        => BuildDetailed(recs, odds, scores).Picks;

    /// <summary>
    /// 生成当日大奶榜 + 每场的方向闸诊断。
    /// recs 用已过销售时段过滤的竞彩推荐（提供场次、联赛、大小球盘、必发占比）；
    /// odds 是每场四类玩法的最新固定奖金快照（FbJcOddsHistory，Odds>1）；
    /// asian 是亚盘初/终盘（FbOdds 的 AH Handicap，主让为负），只用于诱盘告警。
    ///
    /// 方向闸（2026-11-05 加）：模型自己算出来的胜负方向与市场去水方向背离时，整场不发。
    /// 见 <see cref="MilkDirectionGate"/>——亚盘水位方向的历史命中率只有 47.7%，
    /// 模型自己的方向偏差在 11-04 晚直接导致三场全反，方向这件事只能听市场。
    /// </summary>
    public static MilkBuildResult BuildDetailed(
        IReadOnlyList<MatchRecommendation> recs,
        IReadOnlyDictionary<long, List<JcOddsHistoryPoint>> odds,
        ScorelineModel scores,
        IReadOnlyDictionary<long, (decimal? Open, decimal? Close)>? asian = null)
    {
        var result = new List<MilkPick>();
        var gates = new List<MilkDirectionDiagnosis>();
        var showRows = new List<MilkShowRow>();
        // 漏斗：每层剩多少场。空榜时这是唯一能自动解释「卡在哪」的东西（2026-11-05 加）。
        var funnel = new MilkFunnel { Recs = recs.Count, ScoresReady = scores.Ready };
        if (recs.Count == 0 || odds.Count == 0)
            return new MilkBuildResult { Picks = result, Gates = gates, Funnel = funnel };

        foreach (var r in recs)
        {
            if (r.MatchId is not long mid)
            {
                if (funnel.Skipped.Count < 20) funnel.Skipped.Add($"{r.MatchNo} {r.HomeTeam}vs{r.AwayTeam}：无 MatchId");
                continue;
            }
            if (!odds.TryGetValue(mid, out var points))
            {
                if (funnel.Skipped.Count < 20) funnel.Skipped.Add($"{r.MatchNo} {r.HomeTeam}vs{r.AwayTeam}：没有竞彩固定奖金快照");
                continue;
            }
            funnel.WithOdds++;

            if (string.IsNullOrWhiteSpace(r.HomeTeam) || string.IsNullOrWhiteSpace(r.AwayTeam))
            {
                if (funnel.Skipped.Count < 20) funnel.Skipped.Add($"{r.MatchNo}（MatchId {mid}）：队名缺失");
                continue;
            }
            funnel.Named++;

            // 市场三边（方案 2 展示用）：有竞彩胜平负 / 比分快照就算，缺则 null。
            // 每场只算一次，unknown-team 和 thin-sample 两个分支共用，避免重复去水。
            var marketDir = MilkDirectionGate.MarketDirection(points);

            // 模型没见过的队回退全局均值，比分概率纯属噪声——EV 会把这种「无信息的错价」
            // 推到榜首（越冷门的比分错得越离谱）。没见过的队整场不发。
            if (!scores.Ready
                || !scores.KnowsTeam(r.LeagueName, r.HomeTeam!)
                || !scores.KnowsTeam(r.LeagueName, r.AwayTeam!))
            {
                if (funnel.Skipped.Count < 20)
                {
                    var why = !scores.Ready ? "比分模型未就绪"
                        : !scores.KnowsTeam(r.LeagueName, r.HomeTeam!) ? $"模型不认识 {r.LeagueName}/{r.HomeTeam}"
                        : $"模型不认识 {r.LeagueName}/{r.AwayTeam}";
                    funnel.Skipped.Add($"{r.MatchNo} {r.HomeTeam}vs{r.AwayTeam}：{why}");
                }
                // 方案 2：模型不认识也进对照表，模型三边留空、市场三边照给，让人看到「模型对这场没观点」。
                showRows.Add(ShowRowFor(r, mid, null, null, marketDir, "unknown-team", 0, 0));
                continue;
            }
            funnel.Known++;

            // 认识了不等于算得准（2026-11-05 加，见 MinTeamGames）。
            // 样本稀的时候模型退的是「联赛均值 × 主场优势」，两队谁强谁弱它其实不知道；
            // 这种场次的 EV 高低是噪声，不是价值。
            var homeGames = scores.TeamGames(r.LeagueName, r.HomeTeam!);
            var awayGames = scores.TeamGames(r.LeagueName, r.AwayTeam!);
            if (homeGames < MinTeamGames || awayGames < MinTeamGames)
            {
                funnel.ThinSample++;
                if (funnel.Skipped.Count < 20)
                    funnel.Skipped.Add(
                        $"{r.MatchNo} {r.HomeTeam}vs{r.AwayTeam}：样本不足（{homeGames}/{awayGames} 场，门槛 {MinTeamGames}）");
                // 方案 2：样本不足也把「模型 vs 市场」三边摊出来，标 thin-sample，不出注。
                // 模型三边照算（虽然退的是联赛均值 + 主场优势），让用户自己判断这概率有多「没信息」。
                var thinGrid = scores.Distribution(r.LeagueName, r.HomeTeam!, r.AwayTeam!, r.TotalLine);
                var thinMass = thinGrid.Count > 0 ? thinGrid.Sum(c => c.P) : 0;
                (decimal H, decimal D, decimal A)? thinModel = thinMass > 0
                    ? ((decimal)ResultProbability(thinGrid, thinMass, '3'),
                       (decimal)ResultProbability(thinGrid, thinMass, '1'),
                       (decimal)ResultProbability(thinGrid, thinMass, '0'))
                    : null;
                // 模型最强方向，供前端高亮「模型在跟市场对着干」的场次。
                char? thinDir = thinModel is { } tm
                    ? (tm.H >= tm.D && tm.H >= tm.A ? '3' : tm.D >= tm.A ? '1' : '0')
                    : null;
                showRows.Add(ShowRowFor(r, mid, thinModel, thinDir, marketDir, "thin-sample", homeGames, awayGames));
                continue;
            }

            var grid = scores.Distribution(r.LeagueName, r.HomeTeam!, r.AwayTeam!, r.TotalLine);
            if (grid.Count == 0) continue;

            var mass = grid.Sum(c => c.P);
            if (mass <= 0) continue;
            funnel.Distributed++;

            // 每场的候选注先攒在这里，等方向闸判完再决定要不要进榜（2026-11-05）。
            // 直接往 result 里塞的话，闸就没法「整场撤下」——它只能事后从榜上摘，
            // 而事后摘会把名额让给别的场次，排序就乱了。
            var matchPicks = new List<MilkPick>();

            // 去水分母用该玩法的官方固定水位（2026-11-05 改）：算式见 StandardOverRound。
            // 以前取本场快照的 Σ1/赔率，盘口没开全时会把分母做小、市场概率放大，烂注被顶上来。
            // 反过来，快照缺档时也不能硬套全档水位——那会把仅剩几档的隐含概率放大，
            // 所以先做档数校验，不合格（返回 0）就退回纯模型概率。
            var bfOver = SnapshotComplete(points, JcPlayTypes.Bf) ? StandardOverRound(JcPlayTypes.Bf) : 0m;
            var jqOver = SnapshotComplete(points, JcPlayTypes.Jq) ? StandardOverRound(JcPlayTypes.Jq) : 0m;
            var bqOver = SnapshotComplete(points, JcPlayTypes.Bq) ? StandardOverRound(JcPlayTypes.Bq) : 0m;

            // 比分：单格概率；「其它」= 该方向合计 − 已列具体比分的合计
            var others = new Dictionary<char, JcOddsHistoryPoint>();
            var listed = new Dictionary<char, decimal> { ['3'] = 0m, ['1'] = 0m, ['0'] = 0m };
            foreach (var pt in points.Where(p => p.PlayType == JcPlayTypes.Bf))
            {
                // 采集端的写法并不统一：500 是「胜其它」，别的源出现过「胜其他」。
                // 只认「其它」的话，另一种写法会掉进下面的具体比分分支、解析成 null 后被默默丢掉，
                // 整个方向的其他档就消失了——概率尾巴凭空多出来一截。
                var otherSide = pt.OptionCode.EndsWith("其它", StringComparison.Ordinal)
                                || pt.OptionCode.EndsWith("其他", StringComparison.Ordinal)
                    ? pt.OptionCode[0] switch { '胜' => '3', '平' => '1', '负' => '0', _ => '?' }
                    : '?';
                if (otherSide == '?')
                {
                    var p = ScoreProbability(grid, mass, pt.OptionCode);
                    if (p is null) continue;

                    // 「其它」= 方向合计 − 官方已列具体比分合计，所以这里的累计必须先于赔率闸。
                    // 早前把累计放在闸后面，低赔的具体比分（如 2:0 @3.5）被跳过、没进合计，
                    // 于是「胜其它」的概率被抬高一大截，EV 凭空变好看。
                    var split = SplitScore(pt.OptionCode);
                    if (split is not null)
                        listed[split.Value.H > split.Value.A ? '3' : split.Value.H == split.Value.A ? '1' : '0'] += (decimal)p.Value;

                    if (pt.Odds < MinOptionOdds) continue;
                    if (pt.Odds > MaxScoreOdds) continue;
                    matchPicks.Add(Option(pt, r, (double)Blend((decimal)p.Value, pt.Odds, bfOver)));
                }
                else
                {
                    others[otherSide] = pt;
                }
            }
            foreach (var (side, pt) in others)
            {
                var p = Math.Max(0m, (decimal)ResultProbability(grid, mass, side) - listed[side]);
                if (p <= 0 || pt.Odds < MinOptionOdds) continue;
                if (pt.Odds > MaxScoreOdds) continue;
                matchPicks.Add(WithProbability(Option(pt, r), Blend(p, pt.Odds, bfOver)));
            }

            // 总进球：对角线合计，7+ 取余量
            foreach (var pt in points.Where(p => p.PlayType == JcPlayTypes.Jq))
            {
                if (!int.TryParse(pt.OptionCode, out var n)) continue;
                if (pt.Odds < MinOptionOdds) continue;
                if (BannedGoalBuckets.Contains(pt.OptionCode, StringComparer.Ordinal)) continue;
                var p = n >= 7
                    ? Math.Max(0m, 1m - (decimal)Enumerable.Range(0, 7).Sum(k => GoalsProbability(grid, mass, k)))
                    : (decimal)GoalsProbability(grid, mass, n);
                if (p <= 0) continue;
                matchPicks.Add(WithProbability(Option(pt, r), Blend(p, pt.Odds, jqOver)));
            }

            // 半全场：上半场（λ×0.45）+ 下半场（λ×0.55）两段泊松合并，得到「半场结果 × 全场结果」的联合分布。
            // 早前把半场概率和全场概率直接相乘是错的——两者高度相关，那样算会把「负胜」这种
            // 落后再翻盘的组合高估三到四倍（模型 11% 对盘口 2.7%），EV 榜于是被它整片霸占。
            var joint = HalfFullJoint(scores, r.LeagueName, r.HomeTeam!, r.AwayTeam!, r.TotalLine);
            if (joint is not null)
            {
                foreach (var pt in points.Where(p => p.PlayType == JcPlayTypes.Bq))
                {
                    if (!HalfFullCombos.Contains(pt.OptionCode, StringComparer.Ordinal)) continue;
                    if (pt.Odds < MinHalfFullOdds) continue;
                    var p = joint[SideIndex(pt.OptionCode[0]), SideIndex(pt.OptionCode[2])];
                    if (p <= 0) continue;
                    matchPicks.Add(WithProbability(Option(pt, r), Blend((decimal)p, pt.Odds, bqOver)));
                }
            }

            // 快照缺档时，这一注的概率完全没有掺盘口（见 Blend 的 overRound<=0 分支），
            // 纯模型算出来的 EV 天生偏高——看着像「发现价值」，其实是没有参照物。
            // 标出来让前端提示，别让人拿它当证据。
            foreach (var p in matchPicks)
                p.MarketPriced = p.PlayType switch
                {
                    JcPlayTypes.Bf => bfOver > 0,
                    JcPlayTypes.Jq => jqOver > 0,
                    JcPlayTypes.Bq => bqOver > 0,
                    _ => true
                };

            // 方向闸（2026-11-05）：模型方向与市场去水方向背离 → 整场不发。
            // 半全场拿不到模型进球期望时只是少一个玩法，不该连着比分和总进球一起退出，
            // 所以上面用 if (joint is not null) 包起来，而不是 continue。
            //
            // 2026-11-05 当天降级：EnforceDirectionVeto=false 时只记诊断、照常出注。
            // 原因见该常量的注释——大样本分组后同向不如反向，硬砍是负 alpha。
            (decimal? Open, decimal? Close)? asianMove = null;
            if (asian is not null && asian.TryGetValue(mid, out var move)) asianMove = move;

            // 这场候选注实际指向的方向（经过一致性检查后同场只会有一个方向）。
            // 整场只有总进球时为 null，交给闸退回模型倾向判断。
            //
            // 2026-11-05 修：原来取 matchPicks 里「第一条能定方向」的注，而 matchPicks 是按
            // 快照顺序铺的、比分区从 0:0 开始，于是 9 场里有 9 场的「这一注」都是平局比分，
            // 判据全变成「这一注指平」——诊断文字和真实出票对不上，diverge 也被刷成 9/9。
            // 改成取概率最高的那条能定方向的注：那才是这场真正要讲的剧本。
            var pickDirection = matchPicks
                .OrderByDescending(x => x.ModelProbability)
                .Select(FullTimeDirection)
                .FirstOrDefault(d => d is not null);
            if (matchPicks.Count > 0) funnel.Candidates++;
            else if (funnel.Skipped.Count < 20) funnel.Skipped.Add($"{r.MatchNo} {r.HomeTeam}vs{r.AwayTeam}：模型有分布但没生成候选注（赔率越界/被禁档）");

            var diag = MilkDirectionGate.Judge(
                r, points,
                (decimal)ResultProbability(grid, mass, '3'),
                (decimal)ResultProbability(grid, mass, '1'),
                (decimal)ResultProbability(grid, mass, '0'),
                asianMove,
                pickDirection);

            if (diag.Verdict == MilkGateVerdicts.Diverge)
            {
                diag.BlockedPicks = matchPicks.Count;
                var best = matchPicks.OrderByDescending(x => x.ModelProbability).FirstOrDefault();
                if (best is not null)
                {
                    diag.BlockedBest = $"{best.OptionName} {best.ModelProbability:0.0}%";
                    diag.BlockedBestProbability = best.ModelProbability;
                }

                // 只提示、不砍注。真砍了就是拿一个负 alpha 的规则压住整个榜。
                if (EnforceDirectionVeto)
                {
                    funnel.BlockedByGate++;
                    continue;
                }
            }

            // 逐注标注与市场的方向关系（2026-11-05 加）。
            // 整场一道闸太粗：同一场里可能既发「胜胜」这种逆市场的注、又发同向的比分注，
            // 一起放行会把逆市场的注洗白，一起砍又会连累同向的注。所以按注判，
            // 标记留给前端画红标，强背离（市场方向过半）在排序前直接撤。
            var marketPct = MarketPctOf(diag, diag.MarketDirection);
            foreach (var p in matchPicks)
            {
                var dir = FullTimeDirection(p);
                p.MarketDirection = diag.MarketDirection;
                p.MarketDirectionPct = marketPct;
                p.DivergedFromMarket = dir is not null
                                       && diag.MarketDirection is not null
                                       && dir != diag.MarketDirection;
            }

            result.AddRange(matchPicks);
            gates.Add(diag);

            // 方案 2：正常出注的场次也进对照表（status=known），三边直接复用方向闸已经算好的值。
            showRows.Add(ShowRowFor(
                r, mid,
                ((decimal)ResultProbability(grid, mass, '3'),
                 (decimal)ResultProbability(grid, mass, '1'),
                 (decimal)ResultProbability(grid, mass, '0')),
                diag.ModelDirection,
                marketDir,
                "known",
                homeGames, awayGames));
        }

        funnel.AfterGate = result.Count;
        if (result.Count == 0 && funnel.Candidates == 0)
            funnel.Skipped.Add("已进入全部场次，但没有一场生成候选注");

        // 1) 门槛过滤：EV 上限 + 各玩法的命中率下限。
        var gated = result
            .Where(x => x.Ev <= MaxEv)
            .Where(x => x.ModelProbability >= ProbabilityFloor(x.PlayType))
            .ToList();
        funnel.AfterEv = gated.Count;

        // 2) 同一场只讲一个剧本（2026-11-04 用户指出「比分 3:0 和半全场 胜平 互相矛盾」）。
        //    以该场概率最高、且能定方向的一注为锚点，方向对不上的、总进球档对不上的全部撤掉。
        var coherent = new List<MilkPick>(gated.Count);
        foreach (var match in gated.GroupBy(x => x.MatchId))
        {
            var ordered = match
                .OrderByDescending(x => x.ModelProbability)
                .ThenByDescending(x => x.Ev)
                .ToList();

            // 锚点必须能定方向（比分 / 半全场）。总进球只有球数、没有胜负方向，
            // 拿它当锚点时方向闸整条失效——2026-11-04 实测就出过「胜胜」和「平平」
            // 同时挂在一场下面的情况：锚点是总进球 2 球（概率 19.9%，比 3-3 的 17.8% 高），
            // FullTimeDirection 返回 null，于是主胜和平局两个剧本都被放行。
            // 所以先在能定方向的注里挑概率最高的；整场只有总进球时才退回去用它。
            var anchor = ordered
                .Where(x => FullTimeDirection(x) is not null)
                .FirstOrDefault()
                ?? ordered[0];

            // 每一注都要和「已经接受的全部注」对得上，不能只跟锚点比一次。
            // 只比锚点会漏掉「两注各自都和锚点兼容、彼此却冲突」：比分 0:2（2 球）和
            // 总进球 1 球都能挂到「负负」底下，球数明显对不上。链式检查把这种也清掉。
            var accepted = new List<MilkPick> { anchor };
            foreach (var p in ordered.Where(x => !ReferenceEquals(x, anchor)))
                if (accepted.All(a => Compatible(a, p))) accepted.Add(p);
            coherent.AddRange(accepted);
        }

        // 2.5) 强背离撤注（2026-11-05 加）：这一注的方向和市场最强方向相反，且市场方向占比过半。
        //
        // 周四011 是样本：市场客胜 1.71（去水 51.4%），模型发「胜胜」，EV 1.106 排第一。
        // 这种高 EV 不是价值，是模型对跨联赛实力差的误判（荷甲球队 vs 意甲豪门，拿各自
        // 联赛的进球率直接相比）。市场把方向说得这么明白还反着买，长期就是送钱。
        // 阈值取 45 而不是 50：三方向均衡时最大那项也能到 38-40%，45% 起才算市场有明确倾向；
        // 周三002（主胜 49.9% vs 发「负负」）这种差 0.1 踩空的，用 50 会漏过去。
        // 先撤注、再分同场同玩法名额，被撤的名额留给同向的那注，而不是空着。
        var beforeDivergentDrop = coherent.Count;
        coherent = coherent
            .Where(x => !(x.DivergedFromMarket && (x.MarketDirectionPct ?? 0m) >= DivergentDropPct))
            .ToList();
        funnel.DivergentDropped = beforeDivergentDrop - coherent.Count;

        // 3) 同场同玩法最多两注。**必须在一致性之后做**（2026-11-04 晚修）：
        //    早前先卡名额、再判一致性，一场里概率最高的两个比分很可能恰好是被锚点否掉的
        //    方向（比如锚点是主胜，两个名额全给了 1:1 和 1:2），于是这两个被撤，
        //    而真正同方向的 2:1 因为排第三根本没进来——整个比分玩法在这场凭空消失。
        var capped = coherent
            .GroupBy(x => (x.MatchId, x.PlayType))
            .SelectMany(g => g
                .OrderByDescending(x => x.ModelProbability)
                .ThenByDescending(x => x.Ev)
                .Take(MaxPerMatchPerPlay))
            .ToList();

        // 4) 按玩法分组各取前几名，不混在一起排。
        // 混排时比分档位多、半全场只有三档，混着排会让半全场整片消失；前端也按玩法分组展示。
        // 组内同样按概率降序——这是「提高中奖率」这条指示的直接落点。
        var ranked = capped
            .GroupBy(x => x.PlayType)
            .OrderBy(g => Array.IndexOf(PlayOrder, g.Key))
            .SelectMany(g => g
                .OrderByDescending(x => x.ModelProbability)
                .ThenByDescending(x => x.Ev)
                .Take(MaxPerPlay.TryGetValue(g.Key, out var cap) ? cap : 4))
            .ToList();

        // 「总进球 0 球」就是 0:0，和比分的 0:0 是同一注。同一场两个都列等于把同一注卖两次，
        // 留着比分那一条（更具体，赔率口径也更直白）。
        var zeroZero = ranked
            .Where(x => x.PlayType == JcPlayTypes.Bf && x.OptionCode == "0:0")
            .Select(x => x.MatchId)
            .ToHashSet();
        var picks = ranked
            .Where(x => x.PlayType != JcPlayTypes.Jq || x.OptionCode != "0" || x.MatchId is not long m || !zeroZero.Contains(m))
            .ToList();

        funnel.Final = picks.Count;
        return new MilkBuildResult { Picks = picks, Gates = gates, ShowRows = showRows, Funnel = funnel };
    }

    /// <summary>
    /// 拼一行「模型 vs 市场」对照（方案 2）。modelProbs 为 (主,平,客) 的 0-1 比例，传 null 表示算不出。
    /// marketDir 为 <see cref="MilkDirectionGate.MarketDirection"/> 的结果（含三边与来源）。
    /// </summary>
    private static MilkShowRow ShowRowFor(
        MatchRecommendation r, long mid,
        (decimal H, decimal D, decimal A)? modelProbs,
        char? modelDirection,
        MilkDirectionGate.MarketDirectionResult? marketDir,
        string status, int homeGames, int awayGames)
    {
        var row = new MilkShowRow
        {
            MatchId = mid,
            MatchNo = r.MatchNo,
            LeagueName = r.LeagueName,
            HomeTeam = r.HomeTeam,
            AwayTeam = r.AwayTeam,
            MatchTime = r.MatchTime,
            Status = status,
            HomeGames = homeGames,
            AwayGames = awayGames,
            ModelDirection = modelDirection,
            MarketDirection = marketDir?.Dir
        };

        if (modelProbs is { } mp)
        {
            row.ModelHomePct = Math.Round(mp.H * 100m, 1);
            row.ModelDrawPct = Math.Round(mp.D * 100m, 1);
            row.ModelAwayPct = Math.Round(mp.A * 100m, 1);
        }

        if (marketDir is { } mk)
        {
            row.MarketHomePct = Math.Round(mk.H * 100m, 1);
            row.MarketDrawPct = Math.Round(mk.D * 100m, 1);
            row.MarketAwayPct = Math.Round(mk.A * 100m, 1);
            row.MarketSource = mk.Source;
        }

        return row;
    }

    /// <summary>榜单玩法的固定顺序：比分 → 半全场 → 总进球（用户点名的顺序）。</summary>
    private static readonly string[] PlayOrder = { JcPlayTypes.Bf, JcPlayTypes.Bq, JcPlayTypes.Jq };

    /// <summary>
    /// 市场方向占比达到这个数（%）就算「市场说得够明白」，此时与该方向相反的注直接撤（2026-11-05 加）。
    /// 低于它只打红标不撤：三方向均衡的比赛，模型跟市场分歧本来就是价值来源之一，不能一刀切。
    /// </summary>
    private const decimal DivergentDropPct = 45m;

    /// <summary>取该方向在市场去水里的占比（%）；方向未知返回 null。</summary>
    private static decimal? MarketPctOf(MilkDirectionDiagnosis diag, char? dir)
        => dir switch
        {
            '3' => diag.MarketHomePct,
            '1' => diag.MarketDrawPct,
            '0' => diag.MarketAwayPct,
            _ => null
        };

    /// <summary>每个玩法最多几条。2026-11-04 晚从 5/4/3 收紧到 2/2/2。</summary>
    private static readonly Dictionary<string, int> MaxPerPlay = new()
    {
        [JcPlayTypes.Bf] = 2,
        [JcPlayTypes.Bq] = 2,
        [JcPlayTypes.Jq] = 2
    };

    /* ---------------- 同一场的一致性 ---------------- */

    /// <summary>
    /// 同一场的两注算不算「同一个剧本」。只对齐两件一眼能看穿的事：
    /// 全场胜负方向、以及能算出确切球数时的总进球档。
    ///
    /// 半场的路径不判互斥。半全场只有四个组合，如果按「比分 3:0 ⇒ 半场不可能落后」硬卡，
    /// 比分永远会当锚点，半全场就整片消失了。所以「3:0」和「负胜」算同一方向下的两条路径，
    /// 允许并存；真正让人看着别扭的「主胜 + 全场打平」会被这道闸挡住。
    /// </summary>
    public static bool Compatible(MilkPick anchor, MilkPick pick)
    {
        if (ReferenceEquals(anchor, pick)) return true;

        var aDir = FullTimeDirection(anchor);
        var pDir = FullTimeDirection(pick);
        if (aDir is not null && pDir is not null && aDir != pDir) return false;

        // 比分和总进球之间不判球数互斥（2026-11-04 晚改）。
        // 早前要求「比分 3:0」与「总进球 3 球」对得上，结果每场只要有一个比分推荐，
        // 总进球整片消失——因为总进球档和任何一个具体比分都不可能同时命中，
        // 两个玩法本身就是「这场比赛可能的两种结果」，不是同一个剧本。
        // 真正一眼假的是方向打架（主胜 vs 全场平），那个由上面那道闸管。

        // 「胜其它」意味着任一方 6 球、或合计 8 球以上，总进球只可能落在 6 球或 7+ 两档。
        if (IsOtherScoreCode(anchor) && pick.PlayType == JcPlayTypes.Jq
            && int.TryParse(pick.OptionCode, out var otherBucket) && otherBucket < 6) return false;
        if (IsOtherScoreCode(pick) && anchor.PlayType == JcPlayTypes.Jq
            && int.TryParse(anchor.OptionCode, out var bkt) && bkt < 6) return false;

        // 0 球档就是 0:0，半场也必然是 0:0，所以只可能与「平平」同场；
        // 胜胜 / 负负 要求终场分出胜负，和 0 球互斥。总进球没方向，方向闸管不到这里，单独挡。
        if (IsZeroGoalBucket(anchor) && pick.PlayType == JcPlayTypes.Bq
            && pick.OptionCode is "3-3" or "0-0") return false;
        if (IsZeroGoalBucket(pick) && anchor.PlayType == JcPlayTypes.Bq
            && anchor.OptionCode is "3-3" or "0-0") return false;

        // 半全场「平平」= 终场平局，总进球只能落在 0 / 2 / 4 / 6（平局比分必为偶数）。
        // 榜上同时出现「平平」和「总进球 3 球」，就是用户说的那种一眼假的矛盾。
        if (pick.PlayType == JcPlayTypes.Bq && pick.OptionCode == "1-1"
            && anchor.PlayType == JcPlayTypes.Jq && !IsEvenGoalBucket(anchor.OptionCode)) return false;
        if (anchor.PlayType == JcPlayTypes.Bq && anchor.OptionCode == "1-1"
            && pick.PlayType == JcPlayTypes.Jq && !IsEvenGoalBucket(pick.OptionCode)) return false;

        return true;
    }

    private static bool IsZeroGoalBucket(MilkPick pick)
        => pick.PlayType == JcPlayTypes.Jq && pick.OptionCode == "0";

    /// <summary>总进球档能不能是平局：0/2/4/6 可以，1/3/5 和 7+（7 球以上必有胜负）不行。</summary>
    private static bool IsEvenGoalBucket(string code)
        => int.TryParse(code, out var n) && n is 0 or 2 or 4 or 6;

    /// <summary>全场胜负方向：3=主胜 / 1=平 / 0=客胜。总进球没有方向，返回 null 表示不参与方向判断。</summary>
    private static char? FullTimeDirection(MilkPick pick)
    {
        switch (pick.PlayType)
        {
            case JcPlayTypes.Bq:
                return pick.OptionCode.Length == 3 ? pick.OptionCode[2] : null;

            case JcPlayTypes.Bf:
            {
                if (IsOtherScoreCode(pick))
                    return pick.OptionCode[0] switch { '胜' => '3', '平' => '1', '负' => '0', _ => null };
                var s = SplitScore(pick.OptionCode);
                if (s is null) return null;
                return s.Value.H > s.Value.A ? '3' : s.Value.H == s.Value.A ? '1' : '0';
            }

            default:
                return null;
        }
    }

    private static bool IsOtherScoreCode(MilkPick pick)
        => pick.PlayType == JcPlayTypes.Bf && pick.OptionCode.EndsWith("其它", StringComparison.Ordinal);

    /* ---------------- 票 ---------------- */

    /// <summary>
    /// 大奶票：直接押「概率最高的那一注」，单关。
    ///
    /// 2026-11-04 晚改过两次口径，最后定在单关：
    ///   · 最早按 EV 挑腿，票面是「3:0@50 + 3:0@80」，中奖率不到 0.1%，等于每天固定捐一注；
    ///   · 改成按概率挑腿后组 2串1，两条 20% 的腿串起来只剩 3.9%——串关的命中率是**相乘**的，
    ///     和「提高中奖率」这个目标天然相反；
    ///   · 所以票只留一条腿：概率最高的那注，单关。20% 命中率的单关，比 3.9% 的 2串1 稳五倍。
    ///
    /// 想要高赔率的串关随时可以恢复，但不该是默认值——那等于用中奖率换奖金。
    /// </summary>
    public static TicketDraft? BuildTicket(IReadOnlyList<MilkPick> picks, DateTime planDate)
    {
        if (picks.Count == 0) return null;

        // 跨场去重后取概率最高的一条腿（同一场押两注等于加倍押一个剧本，不算分散）。
        var best = picks
            .Where(p => p.ModelProbability >= MinTicketLegProbability)
            .OrderByDescending(p => p.ModelProbability)
            .ThenByDescending(p => p.Odds)
            .FirstOrDefault();
        if (best is null) return null;   // 一条腿都不够格：当天不发大奶票

        var legs = new List<(MilkPick Pick, decimal P)> { (best, best.ModelProbability / 100m) };

        const string pass = "单关";
        var plan = RecommendationSelection.JcCoverTicket.Plan(
            legs.Select(l => (l.Pick.Odds, l.P)).ToList(), pass);

        return new TicketDraft
        {
            PlanDate = planDate.Date,
            Board = Boards.Jc,
            PassType = pass,
            LegCount = legs.Count,
            BetCount = RecommendationSelection.JcCoverTicket.BetCountOf(pass, legs.Count),
            Multiple = RecommendationSelection.JcCoverTicket.Multiple,
            BetAmount = RecommendationSelection.JcCoverTicket.StakeOf(pass, legs.Count),
            HitProbability = Math.Round(plan.AnyPrize * 100m, 1),
            BackProbability = Math.Round(plan.BackStake * 100m, 1),
            ExpectedReturn = Math.Round(plan.Expected, 3),
            Legs = legs.Select((l, i) => new TicketLegDraft
            {
                Seq = i + 1,
                MatchId = l.Pick.MatchId,
                MatchNo = l.Pick.MatchNo,
                HomeTeam = l.Pick.HomeTeam,
                AwayTeam = l.Pick.AwayTeam,
                PickOption = l.Pick.OptionName,
                PlayType = l.Pick.PlayType,
                PickCode = l.Pick.OptionCode,
                Odds = l.Pick.Odds,
                LegProbability = Math.Round(l.P * 100m, 1),
                ModelProbability = l.Pick.ModelProbability,
                MarketProbability = null
            }).ToList()
        };
    }

    /* ---------------- 判定 ---------------- */

    // 判定与显示的纯函数在 Core 的 MilkPickMath（Data 层结算也要用，不能放 Sources）。
    // 这里只保留引擎自己的概率近似。

    /* ---------------- 概率近似 ---------------- */

    private static (int H, int A)? SplitScore(string code)
    {
        var parts = code.Split(':');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var a)) return null;
        return (h, a);
    }

    private static double? ScoreProbability(IReadOnlyList<(int H, int A, double P)> grid, double mass, string code)
    {
        var s = SplitScore(code);
        if (s is null) return null;
        if (s.Value.H > 6 || s.Value.A > 6) return null;
        var cell = grid.FirstOrDefault(c => c.H == s.Value.H && c.A == s.Value.A);
        return cell.P / mass;
    }

    private static double GoalsProbability(IReadOnlyList<(int H, int A, double P)> grid, double mass, int goals)
        => grid.Where(c => c.H + c.A == goals).Sum(c => c.P) / mass;

    private static double ResultProbability(IReadOnlyList<(int H, int A, double P)> grid, double mass, char result)
        => result switch
        {
            '3' => grid.Where(c => c.H > c.A).Sum(c => c.P) / mass,
            '1' => grid.Where(c => c.H == c.A).Sum(c => c.P) / mass,
            _ => grid.Where(c => c.H < c.A).Sum(c => c.P) / mass
        };

    /// <summary>
    /// 半全场联合概率矩阵：[半场结果, 全场结果]，行列为 3=主胜 / 1=平 / 0=客胜。
    /// 上半场按 λ×<see cref="HalfGoalShare"/>、下半场按其余份额各跑一次独立泊松，
    /// 再合并全场比分——这样「领先到终场」和「落后再翻盘」的差距就出来了，
    /// 而不是简单相乘把两者算成同一个数。模型缺进球期望时返回 null，该场不发半全场。
    /// </summary>
    private static decimal[,]? HalfFullJoint(
        ScorelineModel scores, string? league, string home, string away, decimal? ouLine)
    {
        var eg = scores.ExpectedGoals(league, home, away, ouLine);
        if (eg is null) return null;
        var (lh, la) = eg.Value;

        var first = GoalGrid(lh * HalfGoalShare, la * HalfGoalShare);
        var second = GoalGrid(lh * (1 - HalfGoalShare), la * (1 - HalfGoalShare));
        var massFirst = first.Sum(p => p.P);
        var massSecond = second.Sum(p => p.P);
        if (massFirst <= 0 || massSecond <= 0) return null;

        var joint = new double[3, 3];
        foreach (var h1 in first)
        foreach (var h2 in second)
        {
            var ht = h1.H > h1.A ? 0 : h1.H == h1.A ? 1 : 2;
            var fh = h1.H + h2.H;
            var fa = h1.A + h2.A;
            var ft = fh > fa ? 0 : fh == fa ? 1 : 2;
            joint[ht, ft] += h1.P * h2.P;
        }

        var result = new decimal[3, 3];
        for (var i = 0; i < 3; i++)
        for (var j = 0; j < 3; j++)
            result[i, j] = (decimal)(joint[i, j] / (massFirst * massSecond));
        return result;
    }

    /// <summary>单段（半场/下半场）进球分布，截断到 0..<see cref="MaxHalfGoals"/> 并返回未归一化的格子。</summary>
    private static List<(int H, int A, double P)> GoalGrid(double lh, double la)
    {
        var cells = new List<(int H, int A, double P)>((MaxHalfGoals + 1) * (MaxHalfGoals + 1));
        for (var h = 0; h <= MaxHalfGoals; h++)
        for (var a = 0; a <= MaxHalfGoals; a++)
            cells.Add((h, a, Poisson(h, lh) * Poisson(a, la)));
        return cells;
    }

    private static int SideIndex(char c) => c switch { '3' => 0, '1' => 1, _ => 2 };

    private const int MaxHalfGoals = 6;

    private static double Poisson(int k, double lambda)
    {
        if (k < 0) return 0;
        if (lambda < 1e-6) return k == 0 ? 1 : 0;
        var p = Math.Exp(-lambda);
        for (var i = 1; i <= k; i++) p *= lambda / i;
        return p;
    }

    /* ---------------- 工具 ---------------- */

    private static MilkPick Option(JcOddsHistoryPoint pt, MatchRecommendation r, double? probability = null)
        => WithProbability(new MilkPick
        {
            MatchNo = r.MatchNo,
            MatchId = r.MatchId,
            LeagueName = r.LeagueName,
            HomeTeam = r.HomeTeam,
            AwayTeam = r.AwayTeam,
            MatchTime = r.MatchTime,
            PlayType = pt.PlayType,
            OptionCode = pt.OptionCode,
            OptionName = MilkPickMath.DisplayName(pt.PlayType, pt.OptionCode),
            Odds = pt.Odds
        }, probability is null ? null : (decimal)probability.Value);

    private static MilkPick WithProbability(MilkPick pick, decimal? probabilityFraction)
    {
        if (probabilityFraction is decimal p)
        {
            // 统一口径：传进来的是 0-1 的比例，这里统一换算成百分比。
            // EV 用四舍五入后的百分比算，页面上「概率 × 赔率」才能对得上，
            // 否则 43.826 显示成 43.83，用户一乘就发现对不上。
            var pct = Math.Round(p * 100m, 2);
            pick.ModelProbability = pct;
            pick.Ev = Math.Round(pct / 100m * pick.Odds, 3);
        }
        return pick;
    }
}
