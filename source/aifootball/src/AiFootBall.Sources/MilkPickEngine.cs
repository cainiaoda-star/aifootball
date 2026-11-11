using AiFootBall.Core.Models;
using AiFootBall.Data;

namespace AiFootBall.Sources;

/// <summary>
/// 竞彩大奶推荐引擎（2026-11-04 改向）。
///
/// 用户指示：胜负彩、北单推荐下线，每日只推荐竞彩大奶——
///   · 半全场（BQ）：只发 胜胜 / 平平 / 负负（2026-11-04 晚实测逆转档净亏，见 <see cref="HalfFullCombos"/>）；
///   · 总进球（JQ）：官方 0~7+ 档，5 球档不发；
///   · 比分（BF）：官方固定奖金全部比分档，赔率 4.0~<see cref="MaxScoreOdds"/>。
/// 玩法侧重点（2026-11-06 定调）：主攻半全场 → 总进球为辅 → 其次比分，见 <see cref="PlayOrder"/>。
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

    /// <summary>
    /// 半全场「模型 vs 市场」偏离上限（2026-11-07 新增，防 21.40%/EV 2.25 那类假 EV）。
    /// 判据来自页面自己的口径：EV 天花板 = 玩法返奖率（半全场 0.797），一注 EV 若显著超过它，
    /// 等价于「模型认为这注比市场平均好一大截」——我们没有这个 alpha 的证据，只能是概率算高了。
    /// 实测：米竞技负负 EV/返奖 = 2.82 倍、奥萨苏纳胜胜 1.80 倍，两注都是模型在低样本赛事上跑飞；
    /// 其余注 0.76~1.16 倍不受影响。阈值取 1.6 把这两注挡住，误杀为 0。
    /// </summary>
    public const decimal BqModelOverMarketMax = 1.6m;

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
    public const decimal MaxScoreOdds = 20m;

    /// <summary>
    /// 比分玩法总开关（2026-11-09 停发，用户「提高中奖率」第一段）。
    ///
    /// **停发理由不是「4 注 0 中」**——4 注全灭在 10% 真实命中率下概率高达 65.6%，是纯噪音，
    /// 拿它当依据和我自己反复批判的错法是同一个。真实理由有两条，都是结构性的、不依赖样本：
    ///   ① 比分返奖率 0.710，比半全场/总进球（0.797）低 8.7 个点。同样猜对率下，比分天生多亏一截，
    ///      这是税，不是能力问题，再准也补不回来。
    ///   ② 模型对进球分布的 MAE 1.53 球，比市场 1.33 还差。比分恰好是**唯一**只吃「进球分布精度」
    ///      的玩法（半全场还有半场/全场相关性可用、总进球只需合计），模型在最弱的能力上被要求最高。
    ///
    /// 保留开关而不是删代码：第二段校准检验若证明模型在进球分布上确实优于市场，这里改回 true 即可。
    /// 关掉后每天注数会减少 0~2 注，这是代价。
    ///
    /// ★ 2026-11-09 回测补证（Backtest 校准台，测试集 3107 场，切分 2026-10-29）：
    ///   比分 Top-1 命中率 **模型 9.8% vs 市场 12.8%**，平均 Brier 0.9346 vs 0.9333。
    ///   也就是说「选哪一个比分」这件事，模型比市场差 3 个点——这比返奖率论据更直接：
    ///   不光是税高，是连挑都挑不过。停发的决定因此从推断变成有实测支撑。
    /// </summary>
    public const bool EnableScorePicks = false;

    /// <summary>
    /// 半全场赔率上限（2026-11-09 由 15 降到 8）。
    ///
    /// 依据：`league-probe ALL 2026-10-01` 用 **421 场真实竞彩九档 + 真实赛果** 扫描
    /// 「赔率上限 × 概率下限」，每场一注（取过闸档里模型概率最高那档）：
    ///
    /// | 上限 | 注数 | 命中率 | 平均赔率 | 平注回报 |
    /// |---|---|---|---|---|
    /// | 6  | 372 | 16.4% | 4.72 | 0.763 |
    /// | 8  | 401 | 16.5% | 5.04 | **0.811** |
    /// | 10 | 415 | 15.2% | 5.30 | 0.743 |
    /// | 12 | 415 | 15.2% | 5.36 | 0.743 |
    /// | 15 | 415 | 14.5% | 5.51 | 0.711 |
    ///
    /// 15 倍是全部配置里最差的一档；10/12/15 三行几乎重合，说明 10 倍以上那批注是纯拖累。
    /// 8 倍比 15 倍多 2 个点命中、多 0.10 回报，注数只从 415 掉到 401。
    /// 根因：模型在半全场顺风档（胜胜/负负）与反转档上系统性高估 3~5 倍
    /// （逐注看：模型 26~30% vs 市场 5~7%），高赔档把这种高估放大成了 EV。
    ///
    /// 代价要说清：胜平/负平（13~15 倍）、胜负/负胜（25 倍以上）整类不再发。
    /// **这不是提高期望，是用赔率换命中率**——扫描里最好的一格（8 倍 + 模型≥市场）也只有 0.951。
    /// </summary>
    public const decimal MaxHalfFullOdds = 8m;

    /// <summary>
    /// 总进球赔率上限（2026-11-09 新增）。总进球 2/3/4 球档赔率约 3.5~5.5，
    /// 6 球以上才会到 10 倍以上，而 6 球以上全库占比不足 8%。设 10 只是拦住极端档，
    /// 正常出注（实测全部落在 4.0~4.2）不受影响。
    /// </summary>
    public const decimal MaxGoalOdds = 10m;

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
    /// 「市场明确大热」的门槛（2026-11-08 加）：市场最强一侧的去水概率 ≥ 本值时才算大热。
    /// 低于它属于「市场也没方向」，不适合拿大热逻辑去砍注。
    /// </summary>
    public const decimal MarketStrongPct = 50m;

    /// <summary>
    /// 大热逆向的背离幅度门槛（2026-11-08 加）：市场最强侧比模型侧高出 ≥ 本值（百分点）才砍。
    ///
    /// 依据只有一条，但很硬：竞彩 SPF 客胜 1.55~1.75 的 31 场已完赛，主胜 19.4% / 平 22.6% / 客胜 58.1%。
    /// 11-08 埃尔切 vs 贝蒂斯正是这一档（客胜 1.67），模型给主胜 37.13%——历史同类场次的 1.9 倍，
    /// 还据此发出「半全场胜胜 19.69% @5.85」并挂到榜上。
    ///
    /// 为什么定在 20 而不是 10：今天是 28.9 和 30.5 两个点，20 留了余量，
    /// 不至于把「市场 50%、模型 42%」这种正常分歧也砍掉（那只是模型略偏乐观）。
    /// 样本 31 场偏小，所以只砍最极端的形态，不做渐变降权。
    /// </summary>
    public const decimal MarketStrongReverseGap = 20m;

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
    /// EV 天花板的容差倍率（2026-11-08 加）：玩法返奖率 × 1.05。
    /// 一注的名义 EV 超过本玩法返奖率 5% 以上，在数学上只有一种解释——**模型概率给高了**，
    /// 因为市场把所有注的期望压在返奖率上，没有人能靠「算得准」把它抬到返奖率之上。
    /// </summary>
    public const decimal EvCapOverPaybackRatio = 1.05m;

    /// <summary>
    /// 该玩法的 EV 名义天花板。半全场/总进球 0.837、比分 0.745、胜平负 0.922。
    ///
    /// 用途只有一个：给「模型高估」打标，**不撤注**（2026-11-08 用户定的口径）。
    /// 撤注会把榜砍空，而且这些注并非必挂——它们的真实命中率约等于市场隐含概率，
    /// 只是「EV 显示 1.138」这件事本身是幻觉，不能挂在榜上让用户当成「这注不亏」。
    /// </summary>
    public static decimal EvCapFor(string playType) => Math.Round(Payout(playType) * EvCapOverPaybackRatio, 3);

    /// <summary>
    /// 给「名义 EV 超天花板」的注打标（2026-11-08 加）：只标不撤。
    ///
    /// 实测背景（2026-11-08 早班）：近 5 天榜上几乎每个玩法都有 EV 超返奖率的注，
    /// BQ 最高 1.221、今天周日007 埃尔切 半全场胜胜@5.75 报 1.138（模型 19.8%，
    /// 而按赔率反推市场只给 13.9%）——模型比市场高估 42%，却因为 EV 最高排在组内前面。
    /// 用户按 EV 挑注，正好挑到高估最狠的那一注。
    ///
    /// 所以必须把「名义 EV」和「可期收益」分开：标记为 true 的注，前端要把 EV 显示成
    /// 「模型高估」并给出本玩法天花板，不能让 1.138 这个数字裸奔。
    /// </summary>
    public static void MarkOverconfident(IEnumerable<MilkPick> picks)
    {
        foreach (var p in picks)
        {
            if (p.Ev <= EvCapFor(p.PlayType)) continue;
            p.ModelOverconfident = true;
        }
    }

    /// <summary>
    /// 真实期望下限（2026-11-08 加）：按联赛/全库实测概率算出的 EV 不得低于本玩法返奖率的 85%。
    ///
    /// 【为什么加】榜面上的 <see cref="MilkPick.Ev"/> = 模型概率 × 赔率，是**模型自证**。
    /// 而市场把所有注的期望压在返奖率上（半全场/总进球 0.797、比分 0.710），
    /// 一注真实期望低于返奖率 15% 以上，只剩一种解释：模型把概率算高了。
    /// 2026-11-08 十注复盘实锤：周日028 佛罗伦萨 vs 尤文 比分 0:1 @8.00 报 EV 1.066，
    /// 按意甲 0:1 实测 7.98% 算只有 0.638——全表最差的一注，却是模型最"看好"的一注。
    /// 周日025 塞尔塔 半全场平胜 @4.20 同理：模型 18.57% 与市场 18.98% 几乎重合，
    /// 而西甲平胜实测 15.52% → 0.652。模型没有独立判断，只是复述了市场的价。
    ///
    /// 【为什么是 0.85 而不是 1.00】基线是「该联赛该档的平均概率」，不区分具体场次的强弱。
    /// 一场强强对话的胜胜概率本来就高于联赛平均，用 1.00 会把「模型正确识别出强弱」的注也杀掉。
    /// 0.85 只拦「低得离谱」的那批（今天 0.638 和 0.652），放行正常波动。
    ///
    /// ★★★ 必须知道：这个 0.85 没有回测支撑 ★★★
    /// 和 <see cref="CounterHighModelVsMarketMin"/> 那个 0.70 一样，它是**风控约束**不是**统计优化**：
    /// 「真实期望比返奖率还低 15%，那就不该买」这个道理站得住，但能不能提高回报，现有数据证不了。
    /// 要动这个数之前，先跑 <c>counter-gate</c> 那类工具，并且先看样本量够不够。
    /// </summary>
    public const decimal MinBaselineEvRatio = 0.85m;

    /// <summary>
    /// 半全场的 EV 下限顶到返奖率本身（1.00），2026-11-08 加。
    ///
    /// ★ 2026-11-08 晚附注：这个 1.00 的**证据基础在同一天被推翻了**，暂时留着只是因为它方向保守。
    ///
    /// 当初立这个数的唯一实证是江原FC vs 全北现代 半全场负负 @5.30：
    /// 客胜欧赔 3.30 落 3.00-3.40 档，旧条件化表给 14.37% → EV 0.762（亏），所以「0.85 太松」。
    /// 但当晚用**可复现口径**重算同一档（2024-08 起、排除半场污染期、1743 场）得到 **22.58%**
    /// → EV 1.197（赚 20%）。同一个档位、同样的问题，两个口径差 8 个百分点（相对差 57%），
    /// 结论从「亏 4%」翻成「赚 20%」。
    ///
    /// 这说明的是：**基线估到 ±5~8 点，分辨不了 15% 和 22%**。所以任何落在
    /// 「返奖率 ± 8 点」区间内的 EV 判定，本质上是在挑口径，不是在做判断。
    ///   江原那注按新口径 edge = 1.197÷0.797 = 1.50 &gt; 1.45 → 现在是被**上限**拦下的
    ///   （基线说它好得离谱，同样不可信），而不是被下限判「亏」。
    ///
    /// 保留 1.00 的两个理由：① 方向保守（少出票）；② 没有回测能证明放宽更好。
    /// **要改成 0.85 或 0.70 之前，先攒够 BaselineEv 已结算样本**，别再来一次「拍个数字当证据」。
    /// 撤注端已经不再用这条线（见 TurnGuard.SignalEvBroken），那是最容易害人的一处。
    /// </summary>
    public const decimal MinBaselineEvRatioBq = 1.00m;

    /// <summary>
    /// 基线概率最多允许比市场隐含概率高多少倍（2026-11-08 加）。
    ///
    /// 注意这个判据**不能写成「EV 超过返奖率×1.05」**——那会误杀胜胜这类高概率档：
    /// 主胜 1.40 时胜胜基线 61.99%，EV 0.868，本身很正常，但已经超过 0.797×1.05。
    /// 真正异常的不是 EV 的绝对值，而是**基线与市场的分歧幅度**：
    ///     edge = 基线概率 ÷ 市场隐含概率 = BaselineEv ÷ 返奖率
    ///
    /// 实例：朗斯 vs 马赛 法甲 平平 @6.20，条件化表 22.41%（2026-11-08 重算口径）、市场隐含 12.85%
    /// → edge = 1.389 ÷ 0.797 = 1.74。市场在一个档上白送 74% 的期望是不存在的，
    /// 所以这是基线错了——那张条件化表 1743 场、每档 186~707 场，95% 区间 ±3~7 个点，
    /// 而它算出的值与市场隐含差了 74%，远超它的分辨能力。
    /// 45% 以内的分歧留给真实的联赛特性差异。
    /// </summary>
    public const decimal MaxBaselineEdgeRatio = 1.45m;

    /// <summary>
    /// 该注按实测基线算的 EV 是否过下限。**无基线的注一律放行**——
    /// 宁可让它上榜并标注「无基线」，也不要拿「查不到」当「不合格」。
    /// </summary>
    internal static bool PassesBaselineEv(MilkPick p, IReadOnlyDictionary<long, List<JcOddsHistoryPoint>>? odds = null)
    {
        var (h, a) = SpfOddsOf(odds, p.MatchId);
        var bp = OptionBaseline.Pct(p.LeagueName, p.PlayType, p.OptionCode, h, a);
        if (bp is not > 0m) return true;
        var realEv = bp.Value / 100m * p.Odds;
        var payback = Payout(p.PlayType);

        // ★ 基线概率与市场的分歧有上限（2026-11-08 加）：见 MaxBaselineEdgeRatio。
        //    edge = realEv ÷ 返奖率 = 基线概率 ÷ 市场隐含概率。
        //    「EV 高于返奖率×1.05 就拦」是错的写法——它会误杀胜胜这种高概率档。
        if (realEv > payback * MaxBaselineEdgeRatio) return false;

        // 半全场基线已条件化 → 门槛顶到返奖率本身；总进球/比分未条件化 → 仍留 15% 余量。
        var floor = p.PlayType == JcPlayTypes.Bq ? MinBaselineEvRatioBq : MinBaselineEvRatio;
        return realEv >= payback * floor;
    }

    /// <summary>
    /// 取该场 SPF 主胜/客胜赔率（竞彩价），用于半全场顺风三档的赔率条件化基线。
    /// 拿不到就返回 (null, null)，调用方会回退联赛分层值——**不许拿比分或半全场的赔率顶替**，
    /// 那是被同一份概率定价过的循环引用。
    /// </summary>
    private static (decimal? Home, decimal? Away) SpfOddsOf(
        IReadOnlyDictionary<long, List<JcOddsHistoryPoint>>? odds, long? matchId)
    {
        if (odds is null || matchId is not long mid || !odds.TryGetValue(mid, out var pts)) return (null, null);
        decimal? h = null, a = null;
        foreach (var p in pts)
        {
            if (p.PlayType != JcPlayTypes.Spf) continue;
            if (p.OptionCode == "3") h = p.Odds;
            else if (p.OptionCode == "0") a = p.Odds;
        }
        return (h, a);
    }

    /// <summary>
    /// 给榜面上**所有**注回填实测基线（2026-11-08 加）。
    ///
    /// 改前只有单列注带 <see cref="MilkPick.BaselineProbability"/>，主榜 9 注全是 NULL，
    /// 于是页面只能展示模型 EV，用户没有任何办法判断「这个 1.24 是真的还是模型吹的」。
    /// 自检脚本 <c>milk-check.ps1</c> 也因此永远 PASS——它查的是「低可信注必须带基线」，
    /// 而 <see cref="MilkPick.ModelLowConfidence"/> 只对反转档为 true，主榜顺风档全 false，
    /// 这个洞从设计上就抓不到。
    ///
    /// 现在改为：凡 <see cref="OptionBaseline"/> 能算出概率的档位，一律回填概率与基线 EV。
    /// 算不出来的（胜平负/让球、未知档位）保持 NULL，前端必须显示「无基线」，不许静默。
    /// </summary>
    public static void MarkBaseline(IEnumerable<MilkPick> picks,
                                    IReadOnlyDictionary<long, List<JcOddsHistoryPoint>>? odds = null)
    {
        foreach (var p in picks)
        {
            var (h, a) = SpfOddsOf(odds, p.MatchId);
            var bp = OptionBaseline.Pct(p.LeagueName, p.PlayType, p.OptionCode, h, a);
            if (bp is not > 0m) continue;
            p.BaselineProbability ??= bp;
            p.BaselineEv ??= decimal.Round(bp.Value / 100m * p.Odds, 3);
        }
    }

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

    /// <summary>
    /// 全榜上限 = 各玩法名额之和（半全场 2 + 让球胜平负 1 + 总进球 1 + 比分 0）。
    /// 2026-11-11 由 8 改 4：比分返奖率 .710 最低，名额归零。
    /// 注意 NSPF 那一份是名义值，引擎无生成器，实际出注数恒比这个小。
    /// </summary>
    public const int MaxPicks = 4;

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

    /// <summary>
    /// 半全场「反转档」（半场方向 ≠ 全场方向）的概率**上限**（百分数）。
    ///
    /// 2026-11-07 重做：这里原来是「固定下限 5%」，是错的。
    /// 反转 6 档的真实概率量级差 6 倍——技能13（96,703 场联赛平均）：
    ///   平胜 4.0% / 平负 3.1% / 胜平 1.4% / 负平 1.3% / 负胜 0.7% / **胜负 0.6%**。
    /// 固定 5% 高于其中 5 档的真实概率，等于「门开了但谁也进不来」：
    /// 32 倍的「胜负」永远出不来——2026-11-07 周六007 朴茨茅斯 1:2（半场主胜、全场客胜）实证，
    /// 系统一注不可能给到。且全库 0.6% 是被脏数据（78.7% 场次半场=全场）稀释的，
    /// 可信联赛（半场=全场占比<40%）的真实值：苏超 3.20 / 英超 2.68 / 欧冠 2.66 / 英冠 2.56%。
    ///
    /// 正确做法 = 按赔率反推（见 <see cref="ReverseFloor"/>）：门槛随赔率走，自动分层。
    /// 本常量退化为**上限**，与顺风档 12% 对齐，防止低赔反转档（如 3.9 倍的胜平）门槛倒挂。
    /// </summary>
    public const decimal MinHalfFullReverseProbability = 12.0m;

    /// <summary>
    /// 反转档动态门槛系数（2026-11-07 加）：门槛 = 返奖率 / 赔率 × 本值，
    /// 即「模型概率至少达到该档公平概率的 85%」。
    ///
    /// 为什么用赔率反推而不是拍一个固定数：赔率本身就是市场对「这个档有多难」的定价，
    /// 32 倍的档公平概率只有 2.5%，拿 5% 去要求它，等于要求模型比市场看准一倍——
    /// 而实测模型 MAE 1.53 球、比市场的 1.33 还差，不可能有这个精度。
    /// 分层效果：32 倍 → 2.12%；15 倍 → 4.52%；7.1 倍（平负）→ 9.54%。
    /// </summary>
    public const decimal ReverseFloorRatio = 0.85m;

    /// <summary>
    /// 反转档动态门槛的绝对下限（百分数）：极端高赔（100 倍以上）算出来的门槛会低到 0.7%，
    /// 那个量级模型根本区分不了信号和噪声，锁在 1.5%。
    /// </summary>
    public const decimal MinReverseAbsolute = 1.5m;

    /// <summary>
    /// 高赔反转单列的赔率区间：**30 ~ &lt;40 倍**（2026-11-08 修订，原为「12 倍以上、取最高」）。
    ///
    /// 为什么收窄到 30-39（35 天 10-05~11-08、620 场、1104 个反转档报价实测，见
    /// <see cref="CounterHighMaxOdds"/> 的注释）：
    /// 40 倍以上 197 注只中 2 条（1.02%），30-39 倍 201 注中 4 条（1.99%）——同一通道里
    /// 30-39 是命中率最高的一档，40 倍以上是最差的一档。「挑赔率最高」等于每天主动站在
    /// 期望最差的位置，而且回测里每日最高赔那条 35 天 0 命中（均赔 46.7）。
    ///
    /// 上限之所以不是 45/50：那两档同样在 1% 附近，且供给稀疏（45 倍以上 35 天仅 148 场）。
    /// 30-39 档 35 天 218 场、约 6.2 场/天，每天都有得挑，不会因为卡太紧而空榜。
    ///
    /// 12 倍下限作废的理由：12-19 与 20-29 两档本来就走主榜（半全场 3 个名额），
    /// 单列再从这里捞，等于和主榜抢同一批注，还占掉「大奶」的名分。
    /// </summary>
    public const decimal CounterHighMinOdds = 30.0m;

    /// <summary>
    /// 高赔反转单列的赔率上界（开区间，2026-11-08 加）：40 倍。
    ///
    /// 实测依据（35 天 / 1022 注 / 平注 1 元，每场每档取赛前最高赔率）：
    ///   12-20 倍  124 注 中 4 → 3.23%
    ///   20-29 倍  500 注 中 14 → 2.80%
    ///   30-39 倍  201 注 中 4 → 1.99%   ← 本通道选中档位
    ///   40 倍以上  197 注 中 2 → 1.02%   ← 旧策略每天站在这里
    ///
    /// 另一个必须知道的事实（同批 1104 个档位报价的分布统计）：竞彩半全场赔率是按档位走的，
    /// 主力价位是 40/42/45/46/50/55/60/65/70，**49.00 这个精确值 35 天只出现 1 次**
    /// （2026-11-08 德甲 弗赖堡 vs 柏林联合 胜负@49，纯属空档巧合）。
    /// 所以「挑赔率最高」挑到的价位本身就不稳定，今天 49 明天 55 后天 65，没有可复现性；
    /// 30-39 是唯一供给稳定、命中率又最好的一档。
    ///
    /// 附带结论：首末快照均价几乎不动（3-0 档 34.06 → 34.25），半全场冷门档不像比分盘
    /// 临场大跳水，看到 32 倍基本就买得到 32 倍。
    /// </summary>
    public const decimal CounterHighMaxOdds = 40.0m;

    /// <summary>
    /// 高赔单列的联赛基线准入（%，2026-11-07 加）：该联赛该反转档的实测概率必须 ≥ 本值。
    ///
    /// 为什么单列还要再过一道联赛闸：赔率反推的门槛只回答「模型敢不敢」，不回答「这事在这个联赛会不会发生」。
    /// 友谊赛胜负档实测 0.05%（23k 场），模型偶尔会给它 2.5%——那不是发现价值，是拿小样本噪声赌 32 倍。
    /// 取 1.2%：英冠 2.56 / 英超 2.68 / 苏超 3.20 全过，友谊赛 0.05 / 阿甲 1.23(胜负档) 边缘、
    /// 荷乙 2.62 全过；低反转联赛自动出局，不需要逐个维护名单。
    /// </summary>
    public const decimal MinCounterHighLeaguePct = 1.2m;

    /// <summary>
    /// 高赔单列的 EV 上限 = 半全场返奖率 × 本值（2026-11-07 加，复查时补的）。
    ///
    /// 第一版只用了全局 <see cref="MaxEv"/>=2.5，结果实测出一注「米竞技 3-0 @45.00 / 模型 2.15% / EV 0.968」——
    /// 而半全场返奖率只有 0.797，EV 0.968 意味着模型认为它比市场准 21%，前端会同时挂
    /// 「高赔单列」和「EV 超上限·概率存疑」两个标签，自相矛盾（正是用户 11-07 抓出的那条）。
    ///
    /// 单列票的定位是「小注买一个真实存在的高赔可能」，不是「模型发现了价值」。
    /// 所以它的 EV 必须落在返奖率附近（×1.05 容差），超了就说明概率被高估——宁可不发。
    /// </summary>
    public const decimal CounterHighEvToPaybackMax = 1.05m;

    /// <summary>
    /// 高赔单列「基线 EV / 返奖率」的下限（2026-11-07 夜二次修订）：0.95。
    ///
    /// 为什么单列的概率判据从「模型概率」换成「联赛基线」：模型在极端反转档上系统性低估。
    /// 实证（2026-11-07 周六007 英冠 朴茨茅斯 vs 斯托克城，半场主胜→全场 1:2，胜负@32.00）：
    /// 模型给 1.76%（由 ExpTotal 2.534 / ExpGd +0.24 拆出的泊松两段），
    /// 反推门槛是 0.797 ÷ 32 × 0.85 = 2.12% → 模型被自己的门槛挡死，一注没发；
    /// 而英冠实测 2.56%、市场去水隐含 2.50%，这一注其实是「市场定价公道、模型看错」。
    /// 一个 3248 场支撑的联赛基线，比一个 MAE 1.53 球的模型可靠一个量级。
    ///
    /// 判据：基线概率 × 赔率 ÷ 返奖率 ∈ [0.95, 1.35]。
    /// 下界 0.95 拦掉「这个联赛本来就不出、赔率也没补偿」的注；
    /// 上界拦「基线高估 / 赔率虚高」，但**不能收太紧**——见 <see cref="CounterHighBaseEvMaxRatio"/>。
    /// 校验：英冠 3-0 @32 → 2.56%×32÷0.797 = 1.028 ✓；西甲 0-1 @17 → 5.24%×17÷0.797 = 1.118 ✓。
    /// </summary>
    public const decimal CounterHighBaseEvMinRatio = 0.95m;

    /// <summary>
    /// 高赔单列「基线 EV / 返奖率」的上限（2026-11-08 早班修订：1.15 → 1.35）。
    ///
    /// 为什么必须放宽（2026-11-08 08:50 实测抓出，三条反转档全部卡在 1.15）：
    ///   周日012 伊普斯 vs 伯恩茅斯 负胜@32，英超负胜档基线 3.136% → 3.136%×32÷0.797 = 1.259 ✗
    ///   周日016 科隆 vs 勒沃库森   负胜@30，德甲负胜档基线 3.244% → 1.221 ✗
    ///   而今天入选的周日021 曼联 胜负@29，英超胜负档 2.682% → 0.976 ✓（恰好卡在区间内）
    /// 也就是说 1.15 这个上界**恰好只放行「赔率不够高」的那一注**，把真正 32 倍的挡在门外——
    /// 越是大奶、越是联赛历史上真会翻的，ratio 越高，反而越进不来。通道意图被这个数字反噬了。
    ///
    /// 取 1.35 的依据：实测需要放行的最紧一档是 1.259，留 7% 余量；同时仍拦掉
    /// 「基线×赔率」离谱到 35% 以上的注（那种是赔率录错或基线虚高）。
    /// 未知联赛不再由这个上界兜着——改用 <see cref="LeagueHalfFullBaseline.Known"/> 显式否决
    /// （米竞技 3-0@45 走兜底基线 2.08% 算出 1.174，当时靠 1.15 侥幸拦住；上限放宽后必须换闸）。
    /// </summary>
    public const decimal CounterHighBaseEvMaxRatio = 1.35m;

    /// <summary>
    /// 单列注的模型概率相对联赛基线的容忍下限（2026-11-07 夜二次修订）：0.40。
    ///
    /// 概率判据虽换成基线，但不能完全绕过模型——模型给 0.3%、基线给 2.56% 的场次，
    /// 说明模型连方向都看不上（比如主力伤停、阵型对位完全相反），这种「基线高但模型强烈否定」
    /// 的注不该占名额。取 0.40 而非 0.70：英冠 32 倍那注模型/基线 = 1.76/2.56 = 0.69，
    /// 若取 0.70 恰好把它挡掉，等于白改。0.40 只拦「模型强烈否定」，不拦「模型略保守」。
    /// </summary>
    public const decimal CounterHighModelVsBaseMin = 0.40m;

    /// <summary>
    /// 「模型低可信」标记阈值（2026-11-07 夜加）：模型概率 &lt; 联赛基线 × 0.70 就标记。
    ///
    /// 为什么是 0.70 而不是否决线 0.40：这两个数干的是完全不同的事。
    /// 0.40 是**否决权**——低于它就撤注（模型连方向都不认同）；
    /// 0.70 是**标记权**——低于它照样发注，但必须告诉用户「这注的模型概率不可信」。
    /// 中间这段 [0.40, 0.70) 恰恰是最需要解释的区间：模型保守 30% 以上、
    /// 但联赛历史上就是会发生（英冠胜负档模型 1.76% / 基线 2.56% = 0.69，正落在这里）。
    /// 以前这段被静默处理——要么按模型概率排到末尾被挤掉，要么发出来也不解释为什么。
    ///
    /// 前端看到这个标记要展示「模型低估·按联赛基线看」，不能装作模型也认同这注。
    /// </summary>
    public const decimal ModelLowConfidenceRatio = 0.70m;

    /// <summary>
    /// 单列注「模型纯值 / 市场去水隐含」的容忍下限（2026-11-08 用户当场抓错后加）：0.70。
    ///
    /// 这条闸补的窟窿，和 <see cref="CounterHighModelVsBaseMin"/> 那个 0.40 完全不是一回事，
    /// 必须分开看：
    ///
    /// 【错在哪】落库的 ModelProbability 是 Blend 之后的混合值：
    ///     blended = 0.7 × 模型 + 0.3 × 市场隐含（<see cref="MarketWeight"/>）
    /// 但 0.40 那道否决闸是拿 blended 去比联赛基线的。掺进来的 0.3 恰好就是市场自己的报价，
    /// 等于「用市场的乐观给模型担保」。2026-11-08 周日015 热那亚 vs AC米兰 负胜@36 就是活证：
    ///   · 纯模型 1.21%（λ 0.8/1.31 两段泊松，负胜 86% 来自「半场 0:1 → 下半场翻回来」）
    ///   · 市场隐含 2.22%（36 倍去水）
    ///   · Blend 后 1.51%  ← 落库、上页面、参与判闸的都是这个
    ///   · 用 1.51 / 2.64（意甲基线）= 0.57 → 越过 0.40 线，放行
    ///   · 用纯模型 1.21 / 2.64 = 0.46 → 仍然过线，但 1.21 / 2.22（市场）= 0.55 已经明显偏低
    /// 三个数字口径不同却混着比，谁也没错、谁也没查出来，这就是「不问就看不见」的根。
    ///
    /// 【判据】模型纯值 ≥ 市场隐含 × 0.70，即「模型认为市场高估不超过 43%」。
    /// 反过来读更清楚：模型自己都认为这个价高估 30% 以上，那就不该买——我们付的是市场价，
    /// 凭什么赌一个连自己模型都不看好的价。0.70 与 <see cref="ModelLowConfidenceRatio"/>
    /// 同值但用途不同：那个是「标记」，这个是「否决」。同值是为了口径统一，不是巧合。
    ///
    /// 为什么不用 0.85/0.95 这种更狠的：模型在长尾档有已知的系统性低估（35 天回放，
    /// 30~39 倍档实测 1.99% vs 模型 1.2%~1.5%，低估约 30%）。0.70 正好是「把这份已知低估
    /// 扣掉之后仍不看好」的分界；再往上会把「模型保守但联赛真会翻」的注（英冠 0.69）全杀。
    ///
    /// ★★★ 必须知道：这个 0.70 没有统计支撑，而且回测方向是反的 ★★★
    /// 2026-11-08 当天补跑校准（<c>counter-gate</c>，测试期 2026-07-01~11-08）：
    ///   152 注、7 中。比值 &lt;0.70 的 64 注 4 中（回报 0.968），≥0.70 的 88 注 2 中（回报 0.807）。
    ///   也就是说「放行区」的回报反而更低，看不出这条闸有 alpha。
    /// 但**这个对比本身不成立**：152 注只命中 7 次，任何分箱差都是 1~2 次命中的事
    /// （比值 &lt;0.60 的 30 注零命中，在真实概率 1.5% 下出现概率有 61%）。
    /// 样本上限也不是调参能突破的——竞彩半全场赔率库总共只有 620 场 / 34 天（2026-10-05 起），
    /// 20~34 倍档的反转报价全量就这 152 注。**要统计显著得攒 2 年以上。**
    ///
    /// 所以这条闸的定位必须说清楚：它是**风控约束**，不是**统计优化**——
    /// 「我们付的是市场价，没理由赌一个自己模型都不看好的价」这个道理站得住，
    /// 但它能不能提高回报，现有数据证不了。谁要拿它当「已验证的赚钱规则」，就是自欺欺人。
    /// 想动这个数之前先跑 <c>counter-gate</c>，并且先看样本量够不够。
    /// </summary>
    public const decimal CounterHighModelVsMarketMin = MilkPick.ModelDoubtRatio;

    /// <summary>
    /// 从落库口径（Blend 之后的百分数）反推纯模型百分数。Blend 是线性的，可以精确反解：
    ///     blended = (1 - w)·model + w·market，w = <see cref="MarketWeight"/>
    ///     → model = (blended - w·market) / (1 - w)
    /// 快照不全（overRound ≤ 0）时 Blend 直接返回模型值，这里也原样返回，不乱推。
    /// </summary>
    public static decimal ModelPctBeforeBlend(decimal blendedPct, decimal odds, decimal overRound)
    {
        if (odds <= 1m || overRound <= 0m) return blendedPct;
        var marketPct = 100m / odds / overRound;
        var pure = (blendedPct - MarketWeight * marketPct) / (1m - MarketWeight);
        return pure < 0m ? 0m : pure;
    }

    /// <summary>
    /// 零封倾向阈值（0-1 概率，2026-11-06 加）。某队全场零封对手的模型概率达到此线，
    /// 就把这场判为「零封型」，半全场选注时优先「胜胜/负负」方向一致档，而非硬套高赔反转档。
    /// 泊松进球期望下，强队对弱队（λ差约 2.0）零封概率约 13%~18%，弱队零封强队通常 <5%；
    /// 取 0.15 只认「明显强队零封弱队」的信号，避免误伤势均力敌的场次。
    /// </summary>
    public const decimal CleanSheetThreshold = 0.15m;

    /// <summary>
    /// 让胜极端低水阈值（2026-11-06 加）。主队让球盘的让胜(NSPF-3)水位被压到这个数以下，
    /// 说明市场资金一致看好主队赢 2 球以上；但强队往往「只赢球、不赢盘」——净胜球卡在 1 球，
    /// 让胜(需净胜≥2)走盘。铁证：2026-11-05 周四001 町田 2:1 上海申花（主让1球），让胜 2.00→1.66。
    /// 用于 <see cref="MilkPick.HotHomeTrap"/> 的判定，驱动半全场选注转向「让平+胜胜+1球小胜」。
    /// </summary>
    public const decimal MinNspfHotOdds = 1.66m;

    /// <summary>
    /// 净胜球卡 1 球的概率阈值（2026-11-06 加）。主队净胜正好 1 球的模型概率达到此线，
    /// 且让胜已到极端低水，就判「让胜陷阱」——主队大概率只赢 1 球、让球盘走盘。
    /// 泊松强队对弱队(λ差约1.5~2.0)净胜1球概率常 15%~22%，取 15% 作为显著线。
    /// </summary>
    public const decimal NetOneGoalThreshold = 0.15m;

    /// <summary>
    /// 「低水杯赛热门」的主胜水位上限（2026-11-07 加）。杯赛/欧战主胜 ≤ 此线 + 必发大热/钱涌客胜，
    /// 判「市场过度定价主队」，半全场平负(1-0)/负负(0-0)提级。依据（教训1/1b 回测）：
    /// 欧罗巴主胜&lt;1.5 翻车率 88.9%（9 场）、主胜&lt;1.35 的 6 场反转率 67%、仅 17% 主胜。
    /// 对联赛不适用——赛季初联赛低水热门常是真碾压（悉尼FC 半场2:0 全场3:0，教训1f/1j）。
    /// </summary>
    public const decimal CupHotFadeMaxOdds = 1.35m;

    /// <summary>「低水杯赛热门」要求的必发主胜占比下限（%）。教训1 的危险组合口径：必发主胜 ≥70%。</summary>
    public const decimal HotFadeRatioMin = 70m;

    /// <summary>必发盈亏指数深负线（教训1l，2026-11-07 定稿）。≤-60 才算「钱涌客胜」的硬信号；
    /// -37~-56 的中等强度「被买重」在小联赛实测 1/5，是噪音，不参与任何否决/反向。
    /// 语义注：PnlIdx 负数=该方向被买重（庄家在该结果亏钱），不是「钱逃离」，11-07 公式反推订正。</summary>
    public const decimal PnlDeepNegative = -60m;

    /// <summary>
    /// 主胜临场升水否决线（2026-11-07 加，技能11落地，%）。
    /// 竞彩 SPF 主胜「首轮→现价」升幅 ≥ 此线时，主胜方向注（胜胜/平胜/主胜比分）整类撤下。
    /// 依据（9758 场平均盘开盘vs临场回测）：升水≥12% 的主胜兑现率仅 26.3%、按终赔下注 ROI 恶化到 33
    /// （平盘基准 54，差 21 个点，全梯度单调）；降水方向只确认既有判断、无超额价值，故只否决不奖励。
    /// </summary>
    public const decimal WaterRiseVetoPct = 12m;

    /// <summary>平局倾向线（2026-11-07，模型概率口径 0-1）。模型全场平局概率 ≥ 30% 即视为
    /// 「平局剧本场」（全库基准约 26%，联赛平平档占 22.7%）——此时半全场「平平」提级。
    /// 这是引擎第一个平局方向哨兵：此前零封/杯热/画像全部围绕胜/负，平局没有任何信号。</summary>
    public const decimal DrawSetupThreshold = 0.30m;

    /// <summary>让平出注的大热门槛（2026-11-07，技能10）。主胜 SPF ≤ 1.5 + 深盘让-1，
    /// 对应回测里让平 26.4%、EV 0.92 的高价值区（FbBdHistory 7895 场）。</summary>
    public const decimal HcpDrawHotOdds = 1.5m;

    /// <summary>让平出注的盘口下限：主让 1 球或更深（整数盘）。半球盘（-0.5/-1.5）没有「走盘」，
    /// 让平语义退化为净胜恰卡盘口，回测口径不覆盖，不发。</summary>
    public const decimal HcpDrawMaxLine = -1m;

    /// <summary>让平出注的模型概率下限（0-1）。全库让-1 让平基准约 26%，低于 20% 说明这场
    /// 模型认为会打穿盘口，让平不值得占名额。</summary>
    public const decimal HcpDrawMinProbability = 0.20m;

    /// <summary>
    /// 「高反转热门」的反转率下限（2026-11-07 加，俱乐部画像落地，%）。
    /// 全库画像（db/21_team_profile.sql，39.5 万场）反转身位梯度：广岛三箭 43.2、尤文 41.4、
    /// 蔚山 41.4、马竞 41.0 在上；联赛均值约 36。取 38 = 显著高于平均的反转倾向队。
    /// 这类队做热门时「半场领先被翻/半场平后崩」是系统性现象 → 反向档提级。
    /// </summary>
    public const decimal ReversalFavoriteMinRate = 38m;

    /// <summary>「碾压型真强队」的反转率上限。拜仁 31.3 / 勒沃库森 31.4 / 巴黎 29.2。</summary>
    public const decimal CrushReversalMaxRate = 33m;

    /// <summary>「碾压型真强队」的零封率上限（巴黎 33.8 / 勒沃库森 33.0——进攻强，难零封对手也难被零封）。</summary>
    public const decimal CrushCleanSheetMaxRate = 36m;

    /// <summary>画像可信的最低样本（巴黎全历史仅 65 场，样本太小不做画像判断）。</summary>
    public const int ProfileMinGames = 150;

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
    /// 按「注」取门槛命中率。半全场反转档（半场≠全场）单独用更低的 <see cref="MinHalfFullReverseProbability"/>，
    /// 其余走 <see cref="ProbabilityFloor(string)"/>。2026-11-06 放开反转档时引入。
    /// </summary>
    private static decimal ProbabilityFloorFor(MilkPick pick)
        => pick.PlayType == JcPlayTypes.Bq && IsHalfFullReverse(pick)
            ? ReverseFloor(pick.Odds)
            : ProbabilityFloor(pick.PlayType);

    /// <summary>
    /// 反转档动态概率门槛（百分数，2026-11-07 加，替代原来的固定 5%）。
    /// 门槛 = 返奖率 / 赔率 × <see cref="ReverseFloorRatio"/>，再夹在
    /// [<see cref="MinReverseAbsolute"/>, <see cref="MinHalfFullReverseProbability"/>] 之间。
    ///
    /// 返奖率取半全场官方固定水位 <see cref="StandardOverRound"/> 的倒数（1/1.254 ≈ 0.797），
    /// 与 Blend 用的是同一个数，避免两套返奖率打架。
    ///
    /// 量纲（这里栽过一次，别再错）：返回的是**百分数**，因为 ModelProbability 是百分数；
    /// 而 Blend 返回的是 0~1 小数。两者不能直接比。
    /// </summary>
    private static decimal ReverseFloor(decimal odds)
    {
        if (odds <= 1m) return MinHalfFullReverseProbability;
        var payback = 1m / StandardOverRound(JcPlayTypes.Bq);   // ≈0.797
        var fairPct = payback / odds * 100m;                    // 公平概率（百分数）
        return Math.Clamp(fairPct * ReverseFloorRatio, MinReverseAbsolute, MinHalfFullReverseProbability);
    }

    /// <summary>半全场 OptionCode 形如 "X-Y"（X=半场方向，Y=全场方向，3胜/1平/0负），X != Y 即反转档。</summary>
    private static bool IsHalfFullReverse(MilkPick pick)
        => pick.PlayType == JcPlayTypes.Bq
           && pick.OptionCode.Length == 3
           && pick.OptionCode[1] == '-'
           && pick.OptionCode[0] != pick.OptionCode[2];

    /// <summary>
    /// 这注是不是「全场主胜」方向（2026-11-07 加，技能11升水否决的撤注范围）：
    /// 半全场全场主胜档（X-3）+ 主胜比分档（H:A 且 H&gt;A，含「胜其它」）。总进球与平/客胜方向不算。
    /// </summary>
    private static bool IsHomeWinDirection(MilkPick pick)
    {
        if (pick.PlayType == JcPlayTypes.Bq)
            return pick.OptionCode.Length == 3 && pick.OptionCode[1] == '-' && pick.OptionCode[2] == '3';

        if (pick.PlayType == JcPlayTypes.Bf)
        {
            // 「胜其它」两个写法都兜住（采集端 500 用「其它」、别的源出现过「其他」）。
            if (pick.OptionCode.EndsWith("其它", StringComparison.Ordinal)
                || pick.OptionCode.EndsWith("其他", StringComparison.Ordinal))
                return pick.OptionCode[0] == '胜';
            var split = SplitScore(pick.OptionCode);
            return split is { } s && s.H > s.A;
        }

        return false;
    }

    /// <summary>
    /// 这注是不是「该豁免强背离撤注」的反转档 / 零封方向一致档（2026-11-06 加）。
    ///
    /// 背景：强背离撤注（DivergentDropPct=45%）拿「这一注的方向」跟市场最强方向比，
    /// 方向相反且市场占比过半就撤。这对「顺风档」合理——市场都说得那么明白了还反着买，
    /// 大概率是模型跨联赛实力差误判。但对两类注是**结构性误杀**：
    ///   1) 半全场反转档（平负/负胜/胜平/胜负/平胜/平负）——它们的价值恰恰在于「跟市场反着买」，
    ///      市场指主胜、你押「负负/平负」本来就该方向相反，这不是误判、是这类注的本质；
    ///   2) 零封方向一致档（胜胜/负负，且 CleanSheetDirection 命中本注方向）——零封型剧本
    ///      天然逆市场（市场看好强队赢、模型判强队被零封），方向相反正是信号本身。
    /// 这两类注若被强背离一刀砍掉，等于把「假热门反向」这条核心规律整个废掉——它们本该上榜。
    /// 所以这里豁免：只保留红标（DivergedFromMarket），不撤注。
    /// </summary>
    private static bool IsReverseOrCleanSheet(MilkPick pick)
    {
        // 半全场反转档：半场方向 ≠ 全场方向，天然逆市场。
        if (IsHalfFullReverse(pick)) return true;

        // 零封方向一致档：胜胜(3-3)命中「主队零封对手」、负负(0-0)命中「客队零封主队」。
        if (pick.CleanSheetDirection is char cd)
        {
            if (pick.OptionCode == "3-3" && cd == '3') return true; // 主队零封客队 → 胜胜
            if (pick.OptionCode == "0-0" && cd == '0') return true; // 客队零封主队 → 负负
        }

        return false;
    }

    /// <summary>
    /// 半全场档位优先级（越小越先选）。2026-11-06 用户定调「主攻半全场、主攻高赔反转大奶」，
    /// 后又补齐零封/半场平转/让胜陷阱/低水杯热规律，统一成决策层：
    ///   0 = 高赔反转档（胜平/负平/负胜/胜负，12.5~16 倍起步）——翻盘大奶，最高优先；
    ///   1 = 零封方向一致档（胜胜/负负）——仅当这场零封倾向显著时提到这里，否则按概率自然竞争；
    ///   1 = 低水杯赛热门的「平负/负负」档（2026-11-07）——杯赛主胜≤1.35+必发大热/钱涌客胜时，
    ///       半场平/落后→全场崩是最高频剧本，与零封档同级；
    ///   2 = 让胜陷阱的「胜胜」档（2026-11-06）——主队让胜被买成极端低水、净胜卡1球时，
    ///       胜胜(3-3)是「只赢1球」的正确落点（半场领先、全场守住小胜），与零封档同级；
    ///   3 = 其他半全场档（顺风档平平 + 低赔反转档 平胜/平负）。
    ///
    /// perMatch 逐场跟进选注时，先看有没有高赔反转档；没有再判断零封倾向——
    /// 若这场大概率零封（CleanSheetDirection 命中本注方向），优先选方向一致的胜胜/负负；
    /// 再看低水杯热与让胜陷阱——命中则平负/负负、胜胜分别提级；最后退回平平/平胜/平负。
    /// </summary>
    private static int HalfFullTier(MilkPick pick)
    {
        if (pick.PlayType != JcPlayTypes.Bq) return int.MaxValue;

        // 0：高赔反转档（翻盘大奶）。
        if (HighOddsReverseCombos.Contains(pick.OptionCode, StringComparer.Ordinal))
            return 0;

        // 1：零封方向一致档。只有当这场零封倾向明确、且本注方向与零封方向一致时才提级。
        //    胜胜 = 主队零封对手（CleanSheetDirection=='3'）；负负 = 客队零封主队（'0'）。
        if (pick.CleanSheetDirection is char cd)
        {
            var isHomeCleanWin = pick.OptionCode == "3-3" && cd == '3'; // 主队零封客队 → 胜胜
            var isAwayCleanWin = pick.OptionCode == "0-0" && cd == '0'; // 客队零封主队 → 负负
            if (isHomeCleanWin || isAwayCleanWin) return 1;
        }

        // 1：低水杯赛热门的「平负/负负」档（2026-11-07，教训1/1b 落地）。
        //    杯赛/欧战主胜 ≤1.35 + 必发大热/钱涌客胜时，半场没领先、下半场崩盘是最高频剧本：
        //    AC米兰0:2(平负)、勒沃库森0:1(平负)、桑德兰0:2(负负)。与零封档同级，
        //    排在普通高赔反转档之后——后者赔率更高，前者命中率更高，同场兼有时反转档先出。
        if (pick.CupHotFade && pick.OptionCode is "1-0" or "0-0")
            return 1;

        // 1：高反转热门的「反向」低赔反转档（2026-11-07，俱乐部画像落地）。
        //    热门方反转率≥38%（广岛/尤文/蔚山/马竞档）→ 该队做热门时半场平后崩、领先被翻
        //    是系统性现象：主队热门被翻 = 平负(1-0)/负负(0-0)；客队热门被翻 = 平胜(1-3)/负胜(0-3)。
        if (pick.HighReversalFavorite && pick.FavoriteSide is char favS)
        {
            var isHomeFavFade = favS == '3' && pick.OptionCode is "1-0" or "0-0";
            var isAwayFavFade = favS == '0' && pick.OptionCode is "1-3" or "0-3";
            if (isHomeFavFade || isAwayFavFade) return 1;
        }

        // 2：让胜陷阱的「胜胜」档（2026-11-06）。主队让胜被买成极端低水、净胜卡1球，
        //    此时「只赢1球」的正确半全场是胜胜(3-3)——半场领先、全场守住小胜不扩大比分。
        if (pick.HotHomeTrap && pick.OptionCode == "3-3")
            return 2;

        // 2：平局倾向的「平平」档（2026-11-07）。模型平局概率 ≥30% 的场次（基准约26%），
        //    平平是该场的第一落点：双方谁也打不开、半场平全场平。与让胜陷阱的胜胜同级。
        if (pick.DrawSetup && pick.OptionCode == "1-1")
            return 2;

        return 3;
    }

    /// <summary>
    /// 同一场、同一玩法最多留几注（2026-11-04 用户拍板加的上限）。
    /// 费内巴切-利物浦那场一口气占了 6 注，榜看着像只推了一场；
    /// 同一场堆四五个比分，本质还是把钱压在同一个剧本上，不叫分散。
    /// </summary>
    public const int MaxPerMatchPerPlay = 2;

    /// <summary>
    /// 逐场跟进模式（<c>perMatch</c>）每场最多几注，2026-11-11 由 1 改 2（双选）。
    /// </summary>
    /// <remarks>
    /// 双选能让榜面命中率接近翻倍（总进球 23%→45%、半全场 35%→54%，423 场实测），
    /// 但**回报率不变**（.78~.83，仍亏 17~22%），因为两注的 EV 都是负的、加权平均仍是负的。
    /// 这是把「单场押一个剧本」换成「同一场押两个剧本」，降低单注波动、提高中签频次；
    /// 不是提高期望。跟单时本金要按两倍算，绝对亏损同比例放大。
    /// </remarks>
    public const int PerMatchMaxPicks = 2;

    /// <summary>票腿概率下限。2026-11-04 晚从 4% 提到 8%：既然目标是中奖率，就别让 5% 的比分腿进票。</summary>
    public const decimal MinTicketLegProbability = 8m;

    /// <summary>整票中奖率下限。2串1 两腿各 4% 只有 0.16%，那是捐献不是投注。</summary>
    public const decimal MinTicketProbability = 2.5m;

    /// <summary>上半场进球约占总进球的份额。泊松近似的标准取值。</summary>
    public const double HalfGoalShare = 0.45;

    /// <summary>
    /// 半全场发哪些组合（半-全，3胜 1平 0负）。
    ///
    /// 2026-11-06 用户定调「主攻半全场，反转档必须放开」：全部 9 个组合都发，不再只限顺风档。
    /// 顺风档（胜胜/平平/负负）起步赔率仅 1.19~3.40，负期望；高赔反转档（胜平/负平/负胜/胜负）
    /// 起步赔率 12.5~16 倍，才是大奶富矿。见 <see cref="HighOddsReverseCombos"/>。
    ///
    /// 历史注记（2026-11-04 晚曾改成只发顺风档，现按新规律推翻）：当时实测 453 场
    /// 逆转档平注回报率 0.451、顺风档 1.04~1.11，结论是「顺风档被低估」。但那是
    /// 全联赛混合样本；聚焦到「欧战低水热门」这个子集后，反转档才是真实价值洼地。
    /// 半全场概率来自 <see cref="HalfFullJoint"/> 的完整 3×3 联合分布，9 档本来就都算得出来。
    /// </summary>
    public static readonly string[] HalfFullCombos =
    {
        "3-3", "3-1", "3-0",
        "1-3", "1-1", "1-0",
        "0-3", "0-1", "0-0"
    };

    /// <summary>
    /// 高赔反转档（大奶）——半场与全场「方向翻转」的 4 个组合，起步赔率 12.5~16 倍。
    ///   3-1 胜平（半场胜→全场平）、0-1 负平（半场负→全场平）、
    ///   0-3 负胜（半场负→全场胜）、3-0 胜负（半场胜→全场负）。
    ///
    /// 区别于低赔反转档「平胜(1-3)」「平负(1-0)」——那两个起步才 3.1~3.2 倍，是
    /// 「半场平→全场分出胜负」的常见剧本，不是大奶。2026-11-06 用户实测纠正后厘清。
    /// </summary>
    public static readonly string[] HighOddsReverseCombos = { "3-1", "0-1", "0-3", "3-0" };

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
        IReadOnlyDictionary<long, (decimal? Open, decimal? Close)>? asian = null,
        IReadOnlyDictionary<long, ScoreDiagRow>? scoreDiags = null,
        IReadOnlyDictionary<long, (decimal? Open, decimal? Curr)>? spfWater = null,
        IReadOnlyDictionary<string, TeamProfile>? teamProfiles = null,
        IReadOnlyDictionary<long, PriceDivergence.PriceSpan>? priceSpans = null,
        IReadOnlyDictionary<string, TeamForm>? teamForms = null,
        bool perMatch = false)
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
            // 完全不出注的联赛（2026-11-09 加，起因「欧国联能不能推」）。
            // 与下面的 NoParlayLeagues 不是一回事：那张是「能单关、不进串」，这张是「连单关都不发」。
            // 证据见 NoPickLeagues 注释：欧国联回放 14 场 Top-1 模型 14.3% vs 市场 42.9%，出注口径 13 注 0 中。
            if (NoPickLeagues.IsExcluded(r.LeagueName))
            {
                if (funnel.Skipped.Count < 20)
                    funnel.Skipped.Add($"{r.MatchNo} {r.HomeTeam}vs{r.AwayTeam}：{r.LeagueName} 属不推名单（国际赛/友谊赛/青年赛，模型无 alpha）");
                continue;
            }

            // 联赛黑名单（2026-11-07 用户拍板）：澳超整类不发。大奶榜是组串的主要来源，
            // 澳超无升降级+高方差+判罚争议高发（11-07 珀斯光荣越位进球判有效，正是实购 4 串 1 的挂腿）。
            // 名单集中在 NoParlayLeagues，恢复某联赛只改那里。
            if (NoParlayLeagues.IsExcluded(r.LeagueName))
            {
                if (funnel.Skipped.Count < 20)
                    funnel.Skipped.Add($"{r.MatchNo} {r.HomeTeam}vs{r.AwayTeam}：{r.LeagueName} 属黑名单，不进串关");
                continue;
            }

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

            // 联赛名对齐（2026-11-08 加）：竞彩叫「韩职」，数据源叫「K1联赛」。
            // 名字对不上时，模型按「韩职」查队，一场都查不到 → 整类联赛永远样本 0/0、永不出注。
            // 解析一次，本场所有 scores.* 调用都用对齐后的名字；显示仍用竞彩原名（r.LeagueName）。
            // 解析规则三级见 ScorelineModel.ResolveLeague（人工表 → 原名兜底 → 按两队反查）。
            var lg = scores.ResolveLeague(r.LeagueName, r.HomeTeam, r.AwayTeam, MinTeamGames);
            if (!string.IsNullOrWhiteSpace(lg) && !string.Equals(lg, r.LeagueName, StringComparison.Ordinal))
            {
                funnel.LeagueAliased++;
                if (funnel.AliasNotes.Count < 20)
                    funnel.AliasNotes.Add($"{r.MatchNo} {r.LeagueName}→{lg}");
            }

            // 市场三边（方案 2 展示用）：有竞彩胜平负 / 比分快照就算，缺则 null。
            // 每场只算一次，unknown-team 和 thin-sample 两个分支共用，避免重复去水。
            var marketDir = MilkDirectionGate.MarketDirection(points);

            // 模型没见过的队回退全局均值，比分概率纯属噪声——EV 会把这种「无信息的错价」
            // 推到榜首（越冷门的比分错得越离谱）。没见过的队整场不发。
            if (!scores.Ready
                || !scores.KnowsTeam(lg, r.HomeTeam!)
                || !scores.KnowsTeam(lg, r.AwayTeam!))
            {
                if (funnel.Skipped.Count < 20)
                {
                    var why = !scores.Ready ? "比分模型未就绪"
                        : !scores.KnowsTeam(lg, r.HomeTeam!) ? $"模型不认识 {lg}/{r.HomeTeam}"
                        : $"模型不认识 {lg}/{r.AwayTeam}";
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
            var homeGames = scores.TeamGames(lg, r.HomeTeam!);
            var awayGames = scores.TeamGames(lg, r.AwayTeam!);
            if (homeGames < MinTeamGames || awayGames < MinTeamGames)
            {
                funnel.ThinSample++;
                if (funnel.Skipped.Count < 20)
                    funnel.Skipped.Add(
                        $"{r.MatchNo} {r.HomeTeam}vs{r.AwayTeam}：样本不足（{homeGames}/{awayGames} 场，门槛 {MinTeamGames}）");
                // 方案 2：样本不足也把「模型 vs 市场」三边摊出来，标 thin-sample，不出注。
                // 模型三边照算（虽然退的是联赛均值 + 主场优势），让用户自己判断这概率有多「没信息」。
                var thinGrid = scores.Distribution(lg, r.HomeTeam!, r.AwayTeam!, r.TotalLine);
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

            var grid = scores.Distribution(lg, r.HomeTeam!, r.AwayTeam!, r.TotalLine);
            if (grid.Count == 0) continue;

            var mass = grid.Sum(c => c.P);
            if (mass <= 0) continue;
            funnel.Distributed++;

            // 俱乐部画像（2026-11-07 落地）：模型热门方的历史反转倾向。
            // 队名先过 ScorelineModel.Resolve（竞彩简称→数据源全称），画像键是数据源全称。
            TeamProfile? favProfile = null;
            char favSide = '3';
            if (teamProfiles is { Count: > 0 })
            {
                var pH = ResultProbability(grid, mass, '3');
                var pA = ResultProbability(grid, mass, '0');
                favSide = pH >= pA ? '3' : '0';
                var favName = favSide == '3' ? r.HomeTeam! : r.AwayTeam!;
                var favKey = scores.Resolve(lg, favName);
                if (favKey is not null
                    && teamProfiles.TryGetValue(favKey, out var tp)
                    && tp.Games >= ProfileMinGames)
                    favProfile = tp;
            }
            var highRevFav = favProfile is { } hf && hf.ReversalRate >= ReversalFavoriteMinRate;
            var crushFav = favProfile is { } cf
                           && cf.ReversalRate <= CrushReversalMaxRate
                           && cf.CleanSheetRate <= CrushCleanSheetMaxRate;

            // 体能/状态标注（2026-11-08，曼城复盘落地）。**只标注、不扣概率**：
            // 回测（2025-08 起，热门赔率≤2.50）显示「上一场欧战 + 96 小时内再战」的热门方
            // 实际兑现 63.9% vs 市场隐含 59.1%（+4.8 点），三连败组 +6.3 点——方向是反的。
            // 扣分会变成负 alpha，所以这里只写标签，把判断留给人和后续样本。详见 TeamForm 注释。
            string? fatigueTag = null;
            if (teamForms is not null)
            {
                var favNameRaw = favSide == '3' ? r.HomeTeam : r.AwayTeam;
                var favKey2 = string.IsNullOrWhiteSpace(favNameRaw)
                    ? null
                    : scores.Resolve(lg, favNameRaw!);
                if (favKey2 is not null && teamForms.TryGetValue(favKey2, out var ff))
                    fatigueTag = DescribeFatigue(ff, r.MatchTime);
            }

            // 主胜临场升水（2026-11-07，技能11落地）：FbMatchTrend 的 SPF OpenWin→CurrWin。
            // 升水≥12% = 资金放弃主队，主胜方向注整类撤下（常量见 WaterRiseVetoPct）。
            decimal? waterRisePct = null;
            if (spfWater is not null && spfWater.TryGetValue(mid, out var wtr)
                && wtr.Open is > 1m && wtr.Curr is > 1m)
                waterRisePct = Math.Round((wtr.Curr.Value - wtr.Open.Value) / wtr.Open.Value * 100m, 1);
            var waterVeto = waterRisePct >= WaterRiseVetoPct;

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
                    // 停发比分期间不建候选，但上面的 listed[] 累计与下面的 others 收集必须保留：
                    // 它们只影响「胜其它/平其它/负其它」的余量计算，跳掉会让概率凭空变好看。
                    if (!EnableScorePicks) continue;
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
                if (!EnableScorePicks) continue;
                matchPicks.Add(WithProbability(Option(pt, r), Blend(p, pt.Odds, bfOver)));
            }

            // 总进球：对角线合计，7+ 取余量
            foreach (var pt in points.Where(p => p.PlayType == JcPlayTypes.Jq))
            {
                if (!int.TryParse(pt.OptionCode, out var n)) continue;
                if (pt.Odds < MinOptionOdds) continue;
                if (pt.Odds > MaxGoalOdds) continue;
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
            var joint = HalfFullJoint(scores, lg, r.HomeTeam!, r.AwayTeam!, r.TotalLine);
            if (joint is not null)
            {
                // 零封倾向（2026-11-06）：从全场进球分布算主队/客队零封对手的概率，
                // 谁显著高就倾向谁——用于半全场选注时优先「负负/胜胜」而非高赔反转档。
                var homeCleanSheet = grid.Where(c => c.Away == 0).Sum(c => c.P) / mass;
                var awayCleanSheet = grid.Where(c => c.Home == 0).Sum(c => c.P) / mass;
                var cleanDir = homeCleanSheet >= (double)CleanSheetThreshold && homeCleanSheet > awayCleanSheet
                    ? '3'
                    : awayCleanSheet >= (double)CleanSheetThreshold && awayCleanSheet > homeCleanSheet
                        ? '0'
                        : (char?)null;

                // 让胜陷阱（2026-11-06）：主队让胜(NSPF-3)被买到极端低水 + 净胜1球概率显著
                // → 主队「只赢球不赢盘」，让球盘走盘。驱动半全场选注转向「让平+胜胜+1球小胜」。
                var nspfHome = points.FirstOrDefault(p => p.PlayType == JcPlayTypes.Nspf && p.OptionCode == "3");
                var netOneGoal = grid.Where(c => c.Home - c.Away == 1).Sum(c => c.P) / mass;
                var hotHomeTrap = nspfHome is not null
                                  && nspfHome.Odds <= MinNspfHotOdds
                                  && netOneGoal >= (double)NetOneGoalThreshold;

                // 平局倾向（2026-11-07）：模型全场平局概率 ≥30%（基准约26%）→ 平局剧本场，
                // 半全场「平平」提级（见 HalfFullTier）。此前平局方向没有任何哨兵。
                var drawSetup = ResultProbability(grid, mass, '1') >= (double)DrawSetupThreshold;

                // 低水杯赛热门（2026-11-07，教训1/1b/1f/1g 落地）：杯赛/欧战 + 竞彩主胜 ≤1.35
                // + 必发主胜大热(≥70%)或钱涌客胜(PnlIdxAway≤-60，中等强度 -37~-56 是噪音不算)
                // → 市场过度定价主队（欧罗巴主胜<1.35 翻车率 81.8%、低水热门 6 场反转率 67%），
                // 半全场「平负(1-0)/负负(0-0)」是最高频落点，该档提到与零封同级。
                // 注意教训1f 的边界：这只对杯赛成立——联赛低水热门常是真碾压（悉尼FC 3:0），
                // 赛季初的联赛场次不适用反向。友谊赛则整类砍掉反转档（反转档仅 2.7%）。
                var leagueCat = LeagueCategory(r.LeagueName);
                var spfHome = points.FirstOrDefault(p => p.PlayType == JcPlayTypes.Spf && p.OptionCode == "3");
                // 碾压型真强队（画像：反转低+零封低）不做反向——假热门规律硬套真碾压就是负 alpha
                // （教训1f：悉尼FC 低水真碾压 3:0；教训1h：拜仁/巴黎/勒沃库森反转+零封双低）。
                var cupHotFade = !crushFav
                                 && leagueCat == "CUP"
                                 && spfHome is not null && spfHome.Odds <= CupHotFadeMaxOdds
                                 && ((r.RatioHome is decimal rh && rh >= HotFadeRatioMin)
                                     || (r.PnlIdxAway is decimal pa && pa <= PnlDeepNegative));

                // 胜平/负平专用概率源（2026-11-08 接入选注）。取不到就返回 null，两档回落原来的
                // 联合概率——不让一个可选模型把整场的半全场出注搞崩。
                var halfDraw = RefineHalfDraw(points);

                foreach (var pt in points.Where(p => p.PlayType == JcPlayTypes.Bq))
                {
                    if (!HalfFullCombos.Contains(pt.OptionCode, StringComparer.Ordinal)) continue;
                    if (pt.Odds < MinHalfFullOdds) continue;
                    if (pt.Odds > MaxHalfFullOdds) continue;
                    var p = joint[SideIndex(pt.OptionCode[0]), SideIndex(pt.OptionCode[2])];
                    var bqPick = Option(pt, r);
                    // 友谊赛反转档全库仅 2.7%（39.5万场实测，联赛 11.1%、杯赛 15.1%），
                    // 友谊赛「胜胜」畸形高（44.1%），押反转纯属买彩票——整类不发（教训1g）。
                    if (leagueCat == "FRIENDLY" && IsHalfFullReverse(bqPick)) continue;
                    bqPick.CleanSheetDirection = cleanDir;
                    bqPick.HotHomeTrap = hotHomeTrap;
                    bqPick.CupHotFade = cupHotFade;
                    bqPick.DrawSetup = drawSetup;
                    bqPick.FavoriteSide = favProfile is null ? null : favSide;
                    bqPick.FavoriteReversalPct = favProfile?.ReversalRate;
                    bqPick.HighReversalFavorite = highRevFav;
                    bqPick.CrushFavorite = crushFav;

                    // 胜平(3-1)/负平(0-1) 换用 HalfDrawModel 的概率（2026-11-08 接入选注）。
                    // 这两档竞彩报同一个价，价格不含方向信息，所以**不能掺盘口**——
                    // Blend 会把市场那份相同的隐含概率混回来，正好把模型算出的方向差抹平。
                    if (halfDraw is not null && (pt.OptionCode == "3-1" || pt.OptionCode == "0-1"))
                    {
                        var refinedPct = pt.OptionCode == "3-1"
                            ? halfDraw.ProbWinDraw
                            : halfDraw.ProbLoseDraw;
                        if (refinedPct <= 0) continue;
                        bqPick.HalfDrawRefined = true;
                        matchPicks.Add(WithProbability(bqPick, refinedPct / 100m));
                        continue;
                    }

                    if (p <= 0) continue;
                    // 市场一致性闸（2026-11-07）：模型概率超过市场隐含 2.5 倍即判「模型跑飞了」。
                    // 半全场的联合概率会把 λ 的偏差放大，不设这道闸，模型估偏的场次会靠虚高 EV 霸榜。
                    // 注意量纲：Blend 返回 0~1 的小数（ModelProbability 落库时才 ×100），
                    // 市场隐含也必须是小数，写成 100/odds/over 会恒假（当日已栽过一次）。
                    var blended = Blend((decimal)p, pt.Odds, bqOver);
                    if (bqOver > 0 && blended > 1m / pt.Odds / bqOver * BqModelOverMarketMax) continue;
                    // 模型纯值 ÷ 市场隐含（2026-11-08 加，落库 FbMilkPick.ModelVsMarketRatio）。
                    //
                    // 这是全榜唯一一个「模型自己怎么看这注」的数字。落库的 ModelProbability 是
                    // Blend 后的混合值，掺了 30% 市场——用它判断「模型信不信」等于让市场自证。
                    // 这里反过来把市场那 30% 剔掉，还原模型的原判：
                    //   blended = 0.7·model + 0.3·market  →  model / market = 下式
                    // < 0.70 即模型认为市场高估 30% 以上，前端打红标（主榜）/ 单列直接否决。
                    if (bqOver > 0m)
                    {
                        var marketFrac = 1m / pt.Odds / bqOver;
                        if (marketFrac > 0m)
                        {
                            var pureFrac = (blended - MarketWeight * marketFrac) / (1m - MarketWeight);
                            bqPick.ModelVsMarketRatio = decimal.Round(
                                (pureFrac < 0m ? 0m : pureFrac) / marketFrac, 4);
                        }
                    }
                    matchPicks.Add(WithProbability(bqPick, blended));
                }

                // 让平出注（2026-11-07，技能10落地）：NSPF 让平是全引擎此前从不出注的最大价值档。
                // 触发条件（满足其一）：①深盘(主让≥1球整数盘) + 主胜大热(≤1.5)——10.8万场回测
                // 让平 26.4%、EV 0.92 > 让胜 0.78；②让胜陷阱（让胜被买到极端低水 + 净胜卡1球）。
                // 概率 = 模型「主队净胜恰卡盘口」的格子合计（让-1 → 净胜1球）。半球盘不发（无走盘语义）。
                var nspfDraw = points.FirstOrDefault(p => p.PlayType == JcPlayTypes.Nspf && p.OptionCode == "1");
                if (nspfDraw is not null && nspfDraw.Odds >= MinOptionOdds
                    && r.Handicap is decimal hcpLine
                    && hcpLine <= HcpDrawMaxLine && hcpLine == Math.Floor(hcpLine))
                {
                    var hcpTarget = (int)(-hcpLine); // 让-1 → 主队净胜恰 1 球
                    var hcpDrawP = grid.Where(c => c.Home - c.Away == hcpTarget).Sum(c => c.P) / mass;
                    var hcpHot = spfHome is not null && spfHome.Odds <= HcpDrawHotOdds;
                    if (hcpDrawP >= (double)HcpDrawMinProbability && (hcpHot || hotHomeTrap))
                    {
                        var nspfOver = SnapshotComplete(points, JcPlayTypes.Nspf)
                            ? StandardOverRound(JcPlayTypes.Nspf)
                            : 0m;
                        var nd = Option(nspfDraw, r);
                        nd.HandicapDrawSetup = true;
                        nd.DrawSetup = drawSetup;
                        matchPicks.Add(WithProbability(nd, Blend((decimal)hcpDrawP, nspfDraw.Odds, nspfOver)));
                    }
                }
            }

            // 快照缺档时，这一注的概率完全没有掺盘口（见 Blend 的 overRound<=0 分支），
            // 纯模型算出来的 EV 天生偏高——看着像「发现价值」，其实是没有参照物。
            // 标出来让前端提示，别让人拿它当证据。
            foreach (var p in matchPicks)
            {
                p.MarketPriced = p.PlayType switch
                {
                    JcPlayTypes.Bf => bfOver > 0,
                    JcPlayTypes.Jq => jqOver > 0,
                    JcPlayTypes.Bq => bqOver > 0,
                    _ => true
                };
                p.WaterRisePct = waterRisePct;
            }

            // 升水否决（2026-11-07，技能11）：主胜临场升水≥12% → 撤掉全部主胜方向注
            // （半全场全场主胜档 + 主胜比分档）。总进球/平局/客胜方向不在这条规则管界内。
            if (waterVeto)
            {
                var before = matchPicks.Count;
                matchPicks.RemoveAll(IsHomeWinDirection);
                funnel.WaterRiseVeto += before - matchPicks.Count;
                if (before > matchPicks.Count && funnel.Skipped.Count < 20)
                    funnel.Skipped.Add(
                        $"{r.MatchNo} {r.HomeTeam}vs{r.AwayTeam}：主胜升水 {waterRisePct:0.#}%（≥{WaterRiseVetoPct:0}%），主胜方向注已撤");
            }

            // 定价背离闸（2026-11-08，曼城复盘落地）。
            //
            // 2026-11-08 01:30 英超 诺丁汉森林 3:1 曼城：竞彩客胜 1.60→1.43（-10.6%），
            // 必发 PriceAway 只动 -1.1%。两个市场在讲不同的事——竞彩散户在砸热门，
            // 专业资金一个价都不接。我们押的 0:1 比分 / 平负半全场全挂。
            //
            // 回测（见 PriceDivergence 类注释，完赛场次、收盘去水）：
            //   客胜 降≥10% + 必发动<3% → 13 场，实际 23.1% vs 隐含 40.1%，-17.0 点；
            //   主胜 同形态           →  9 场，实际 55.6% vs 隐含 52.2%， +3.4 点。
            // 所以只撤客胜侧。主胜侧撤了等于白扔注——这个不对称是数据给的，不是漏写，
            // 别「为了对称」把 IsHomeWinDirection 也加进来。
            //
            // 样本 13 场仍小（p≈0.21），所以开关是 PriceDivergence.VetoAwayFakeHot，
            // 关掉只停撤注，打标与漏斗计数照旧。
            var divergence = priceSpans is not null && priceSpans.TryGetValue(mid, out var span)
                ? PriceDivergence.Judge(span)
                : null;
            if (divergence is not null)
            {
                if (divergence.AwayFakeHot)
                {
                    var before = matchPicks.Count;
                    matchPicks.RemoveAll(x => PriceDivergence.ShouldVeto(FullTimeDirection(x), divergence));
                    funnel.DivergenceVeto += before - matchPicks.Count;
                    if (before > matchPicks.Count && funnel.Skipped.Count < 20)
                        funnel.Skipped.Add($"{r.MatchNo} {r.HomeTeam}vs{r.AwayTeam}：{divergence.Note}");
                }

                // 打标在撤注之后：留下的注（主胜/平局/总进球）带这个标签，
                // 是为了让人知道「这场客胜方向已经被背离闸排除了」，而不是凭空少了一注。
                foreach (var p in matchPicks)
                {
                    p.DivergenceTag = divergence.Tag;
                    p.DivergenceNote = divergence.Note;
                }
            }

            // 方向闸（2026-11-05）：模型方向与市场去水方向背离 → 标记 diverge。
            // **实际不砍注**（EnforceDirectionVeto=false，硬砍回测是负 alpha），诊断文案已按真实行为生成。
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
            if (fatigueTag is not null)
                foreach (var pk in matchPicks) pk.FatigueTag = fatigueTag;

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

                // 但「市场明确大热、模型却掉头」这一种形态要单独砍（2026-11-08 加）。
                // 这不是上面那条混合方向回测的翻案，是另一件事：那次回测把「模型逆市场 5 个点」
                // 和「模型逆一个 52% 的大热」当成同一类，其实不是。
                // 实证（竞彩 SPF 客胜 1.55~1.75，31 场）：主胜 19.4% / 平 22.6% / 客胜 58.1%。
                // 而 11-08 埃尔切 vs 贝蒂斯（客胜 1.67）模型给主胜 37.13%，是历史同类场次的 1.9 倍，
                // 还据此发出「半全场胜胜 19.69%」。
                if (MarketStrongReverse(diag, pickDirection))
                {
                    funnel.BlockedByGate++;
                    diag.BlockedPicks = matchPicks.Count;
                    diag.AlertNote = $"市场大热{DiagOf(diag.MarketDirection)} {MarketPctOf(diag, diag.MarketDirection):0.0}%，"
                        + $"模型却指{DiagOf(pickDirection ?? diag.ModelDirection)}——同类场次模型只对 19%，整场不发";
                    // 诊断必须落库，否则页面上这场会「凭空消失」，事后无从追查。
                    // continue 会跳过循环末尾的 gates.Add，所以这里补一次。
                    gates.Add(diag);
                    if (funnel.Skipped.Count < 20)
                        funnel.Skipped.Add($"{r.MatchNo} {r.HomeTeam}vs{r.AwayTeam}：{diag.AlertNote}");
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
        //    半全场反转档（半场≠全场）用更低的 MinHalfFullReverseProbability，顺风档仍走 12%。
        //
        //    2026-11-08 补上真实期望下限：改前只有 EV **上限**（MaxEv=2.5，形同虚设），
        //    从没有下限闸，所以「按联赛实测概率算只有 0.638 期望」的注照样上榜。
        //    判据与阈值见 <see cref="PassesBaselineEv"/>，无基线的注放行。
        var gated = result
            .Where(x => x.Ev <= MaxEv)
            .Where(x => x.ModelProbability >= ProbabilityFloorFor(x))
            .Where(x => PassesBaselineEv(x, odds))
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
            .Where(x => !(x.DivergedFromMarket
                         && (x.MarketDirectionPct ?? 0m) >= DivergentDropPct
                         && !IsReverseOrCleanSheet(x)))
            .ToList();
        funnel.DivergentDropped = beforeDivergentDrop - coherent.Count;

        // 2.6) 临场转向撤注（2026-11-05 加）：方向闸只认「SPF/比分去水的静态方向」，
        // 拦不住「SPF 还没动、但比分盘深档已经先转向」的场次——塞尔塔就是样本：
        // SPF 去水仍主胜 39% 最强（闸判 Pass），但净胜球期望已从 0.371 下修到 0.226、
        // 客队深档 6 档整批降价，市场真金白银在往客队压。这一注指主胜就是逆着市场走。
        // 所以独立读净胜球诊断（FbScoreDiag，每 2 分钟刷新），主胜方向注 + 转向客队 → 撤。
        if (scoreDiags is not null && scoreDiags.Count > 0)
        {
            var beforeTurnDrop = coherent.Count;
            coherent = coherent
                .Where(x => !(x.MatchId is long tMid
                              && scoreDiags.TryGetValue(tMid, out var sd)
                              && FullTimeDirection(x) == '3'
                              && (sd.ExpGdMove is decimal mv && mv <= -TurnGuard.GdShrinkThreshold
                                  || sd.DeepNegDrop >= TurnGuard.DeepDropThreshold
                                  || (sd.TailTag?.Contains("客队深档", StringComparison.Ordinal) ?? false))))
                .ToList();
            funnel.TurnDropped = beforeTurnDrop - coherent.Count;
        }

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

        // 3.5) 逐场跟进模式（2026-11-06 起）：每场只留概率最高的一注，不再全局砍单。
        //      这是「每日复盘」新口径——竞彩每场都跟一个大奶，赛后逐场写复盘。
        //      全局砍单（第 4 步）会把「没进榜的场次」整个丢掉，复盘就缺了那些场。
        //
        //      玩法侧重点（2026-11-06 用户定调）：主攻半全场 → 总进球为辅 → 其次比分。
        //      所以每场选注时先按玩法优先级排，同一玩法内再比概率。
        //      不再「纯按概率取第一」——那样半全场会被比分的高概率档挤掉。
        //
        //      半全场档位（2026-11-06 用户实测纠正）：主攻高赔反转大奶（胜平/负平/负胜/胜负，
        //      12.5~16 倍起步）。所以半全场内部先按档位——高赔反转档优先，同档再比概率。
        //      否则「按概率最高」会选出平胜(1-3)@3.1 这种低赔档，错过真正的大奶。
        if (perMatch)
        {
            // 2026-11-11 改双选（用户要求，每场最多 <see cref="PerMatchMaxPicks"/> 注）。
            //
            // 依据（423 场竞彩实测，市场概率排序代理，非引擎自身样本）：
            //   总进球  1 选 命中 23.2% 回报 .782 → 2 选 45.0% / .805 → 3 选 62.9% / .824（全押 8 档 .785）
            //   半全场  1 选 命中 34.8% 回报 .827 → 2 选 53.7% / .815
            // 也就是说双选**把命中率买上去**，回报率几乎不动（仍在返奖率 .797 附近），
            // 本金翻倍、绝对亏损同步翻倍。这是分散化，不是提期望——别把 45% 当成"变准了"。
            //
            // 实现口径：**同玩法优先取满两档**（真正的双选）——BQ 有两档合格就出 BQ 两档，
            // 只有一档才跨到下一个玩法。所以一场可能出现「BQ 胜胜 + BQ 平平」这种互斥对，
            // 也可能「BQ 平平 + JQ 2 球」这种相关对，两种都不重复扣水。
            var perMatchPicks = capped
                .GroupBy(x => x.MatchId)
                .SelectMany(g => g
                    .GroupBy(x => x.PlayType)
                    .OrderBy(pg => Array.IndexOf(PlayOrder, pg.Key))
                    .SelectMany(pg => pg
                        .OrderBy(x => HalfFullTier(x))
                        .ThenByDescending(x => x.ModelProbability)
                        .ThenByDescending(x => x.Ev)
                        .Take(MaxPerMatchPerPlay))
                    .Take(PerMatchMaxPicks))
                .ToList();

            funnel.Final = perMatchPicks.Count;
            MarkBaseline(perMatchPicks, odds);
            return new MilkBuildResult { Picks = perMatchPicks, Gates = gates, ShowRows = showRows, Funnel = funnel };
        }

        // 4) 按玩法分组各取前几名，不混在一起排。
        // 混排时比分档位多、半全场只有三档，混着排会让半全场整片消失；前端也按玩法分组展示。
        //
        // 排序口径分家（2026-11-07 修，用户实测反推）：
        //   · 半全场/总进球/让球：仍按模型概率降序——这是「提高中奖率」这条指示的直接落点。
        //   · **比分改按 EV 降序**。比分是 31 档分散玩法，单档概率天然只有 5~15%，按概率排
        //     必然永远选到「赔率被压得最狠」的那个热门档（阿森纳 vs 赫尔城 2:0 @5.30）。
        //     实测 204 场主胜热门（SPF<1.8）：2:1 命中 12.25% > 2:0 10.30% > 1:0 8.33%，
        //     而 Dixon-Coles 网格在强弱分明时峰值落在 2:0，模型概率与真实分布反向。
        //     同日 4 场落点（威尼斯2:1 / 柏太阳神2:1 / 珀斯2:1 / 鸟栖1:2）全部错过，
        //     根因就是「按模型概率取最高」把一个系统性低估的档位永远排在 2:0 后面。
        var ranked = capped
            .GroupBy(x => x.PlayType)
            .OrderBy(g => Array.IndexOf(PlayOrder, g.Key))
            .SelectMany(g => g
                .OrderByDescending(x => x.PlayType == JcPlayTypes.Bf ? x.Ev : x.ModelProbability)
                .ThenByDescending(x => x.ModelProbability)
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

        // 4.5) 同一场跨玩法只留一注（2026-11-08 加）。
        //
        // 改前只做了「同场同玩法最多 2 注」（<see cref="MaxPerMatchPerPlay"/>）和「总进球 0 球 vs 比分 0:0」，
        // 跨玩法完全没有约束。2026-11-08 周日005 江原FC vs 全北现代一场挂了三注：
        // 比分 0:1 @7.50 + 半全场 负负 @5.30 + 总进球 1 球 @4.20。
        // 三者是**同一笔钱押三遍**：0:1 成立则总进球 1 球必然成立，
        // 而 0:1 里约三分之二是「半场客队已领先 → 负负」。抽水抽了三遍，敞口却是同一个。
        //
        // 保留哪一注：<see cref="picks"/> 已按「玩法优先级 → 组内排序键」排好（半全场 > 让球 > 总进球 > 比分），
        // 每场保留**先出现的那个**，即玩法优先级最高的一注。不按概率或 EV 挑，
        // 是因为那两个数字都建立在「模型准」这个已经被证伪的前提上（见 <see cref="MinBaselineEvRatio"/>）。
        //
        // 后果：榜面注数会变少（今天 10 → 6）。这是对的——原来的 10 注里有 4 注是重复敞口。
        //
        // 2026-11-11 改双选（用户要求）：每场从 1 注放宽到 <see cref="PerMatchMaxPicks"/> 注。
        // 取的是「先出现的两注」——<see cref="picks"/> 已按玩法优先级排好，所以这两注是
        // 该场玩法优先级最高的两个档（BQ 有两档就是 BQ 双选，只有一档才跨到 JQ）。
        //
        // 上面那段「重复敞口」的担心依然成立，双选正是**主动**去买这份重复敞口：
        // 换的是命中率翻倍（423 场实测 总进球 23%→45%、半全场 35%→54%），
        // 代价是本金翻倍、绝对亏损翻倍，回报率仍卡在返奖率 .797 附近。
        var perMatchTaken = new Dictionary<long, int>();
        picks = picks
            .Where(x =>
            {
                if (x.MatchId is not long dedupId) return true;
                perMatchTaken.TryGetValue(dedupId, out var taken);
                if (taken >= PerMatchMaxPicks) return false;
                perMatchTaken[dedupId] = taken + 1;
                return true;
            })
            .ToList();

        // 5) 高赔反转单列（2026-11-07 加）。
        //
        //    主榜半全场只有 3 个名额、组内按模型概率排，而反转档概率天然只有 2%~5%，
        //    永远挤不过胜胜/负负/平平——结果是 30 倍以上的档位在主榜里「结构上不存在」。
        //    实证：2026-11-07 周六007 朴茨茅斯 vs 斯托克城，半场主胜、全场 1:2，胜负@32，
        //    系统全程一注没给；周六008 马拉加同型。用户买的就是这个量级。
        //
        //    这里单独开 1 个名额：不进主榜排序，只认「赔率够高 + 过动态门槛 + EV 不超上限」。
        //    每期最多 1 注，标记 CounterHigh，前端必须写明「高赔单列·长期负期望」——
        //    它的 EV 天花板仍是玩法返奖率 0.797，不能混进主榜装作能赚。
        //
        //    从 gated 而不是 coherent 里挑：coherent 已经做过「同一场只讲一个剧本」的
        //    连贯性剪枝——锚点通常是概率最高的胜胜/平平，方向与反转档天然冲突，
        //    反转档在那一关就被剪掉了，从 coherent 挑等于永远挑不到（改完实测证实：
        //    32 倍胜负档一注都进不来）。gated 已过概率门槛和 EV 上限，
        //    强背离撤注对反转档本来就有豁免（IsReverseOrCleanSheet），复活的风险可控。
        //    第二个坑（2026-11-07 夜二次修订，实测抓出）：从 gated 挑同样挑不到。
        //    gated 在 1045 行已经做过 ModelProbability >= ProbabilityFloorFor(x) 的过滤，
        //    反转档的门槛是「按赔率反推」的（32 倍 → 2.12%），而模型给 32 倍档的概率只有 1.76%，
        //    它连 gated 都进不去——在 gated 后面加任何判据都是死代码。
        //    所以候选池必须回到 result（未过滤全量），再在单列通道里自己做全套闸。
        //
        //    这样做的代价：result 里含被「强背离撤注」砍掉过的注，理论上会复活。
        //    风险由三道闸控住：赔率 30~<40、基线 EV/返奖率 ∈ [0.95,1.35]、模型 ≥ 基线×0.40。
        //    而且反转档在强背离撤注里本来就有豁免（IsReverseOrCleanSheet），不是新增漏洞。
        var bqPayback = 1m / StandardOverRound(JcPlayTypes.Bq);
        var counterHighRow = result
            .Where(x => x.PlayType == JcPlayTypes.Bq && IsHalfFullReverse(x))
            // 赔率区间 30 ~ <40（2026-11-08 修订，原为 ≥12 且取最高）：
            // 35 天实测 40 倍以上 197 注中 2 条（1.02%），30-39 倍 201 注中 4 条（1.99%）。
            // 「挑赔率最高」等于每天主动站在命中率最差的一档，且 40+ 的价位不稳定
            // （49.00 这个精确值 35 天只出现 1 次），没有可复现性。
            .Where(x => x.Odds >= CounterHighMinOdds && x.Odds < CounterHighMaxOdds)
            // 联赛基线闸（2026-11-07）：模型敢给不等于这个联赛会出。友谊赛胜负档实测 0.05%，
            // 模型给 2.5% 只是噪声；英冠 2.56%、英超 2.68%、苏超 3.20% 才是真的会翻。
            // 联赛必须在基线表里（2026-11-08 加，与上限放宽同时上线）。
            // 兜底均值 DefaultPct 是 14 个 A 级联赛的均值，拿来给未知联赛背书等于用别人的历史赌这一场。
            // 上次米竞技 3-0@45 走的就是兜底 2.08%，靠 ratio 上界 1.15 侥幸拦下；现在上界放到 1.35，
            // 必须换成显式闸，否则这类「查不到联赛、模型随便给个概率」的注会从缺口涌进来。
            .Where(x => LeagueHalfFullBaseline.Known(x.LeagueName))
            .Where(x => LeagueHalfFullBaseline.Passes(x.LeagueName, x.OptionCode, MinCounterHighLeaguePct))
            .Select(x => new
            {
                Pick = x,
                BasePct = LeagueHalfFullBaseline.Pct(x.LeagueName, x.OptionCode) ?? 0m
            })
            .Where(t => t.BasePct > 0m)
            // ★★★ 2026-11-08 删除「基线 EV / 返奖率 ∈ [0.95, 1.35]」这道闸 ★★★
            //
            // 删它的实证（Backtest/CounterGateStudy.cs，测试期 2026-07-01~11-08）：
            // 30~<40 档真实频率 2.63%（152 注 4 中），同期市场去水隐含均值 2.38%——
            // **这个价位基本是公允定价**。所以「赔率给够没给够补偿」这个问题，在这条通道里
            // 没有可利用的答案：无论怎么算，得到的都只是噪声。
            //
            // 更根本的问题是它口径混了：ratio = 联赛基线 ÷ 市场隐含，
            // 衡量的不是「这场球值不值」，而是「这个联赛平均比市场乐观多少」。
            // 拿它当准入门槛，等于用联赛均值给单场背书——热那亚 负胜@36 就是这么进来的
            // （意甲 2.64% ÷ 市场 2.22% = 1.19，漂亮地过闸，然后模型说它只有 1.21%）。
            //
            // 职责拆开，不再混在一个比值里：
            //   ① 这个联赛会不会出反转？ → 联赛基线 ≥ 1.2%（上一行 Passes，保留）
            //   ② 这个价配不配这个赔率？ → 模型纯值 ÷ 市场隐含 ≥ 0.70（下面那道，新增）
            // 上界 1.35 一并作废：它拦的是「基线虚高」，而那类注现在会被 ② 直接判死
            // （基线虚高 = 模型与市场差距大 = 比值低），不需要两道闸说同一件事。
            // 但模型不能强烈否定（模型概率 ≥ 基线×0.40）：模型给 0.3%、基线给 2.56%，
            // 说明模型连方向都看不上（伤停/对位反了），这种不该占名额。
            //
            // ★ 2026-11-08 修正：这一闸原来拿 ModelProbability（Blend 之后的混合值）来比，
            //   而 0.3 的市场权重掺的正是市场自己的报价——等于「用市场的乐观替模型担保」。
            //   热那亚 负胜@36：纯模型 1.21%、市场 2.22%、混合 1.51%，用 1.51 去比 2.64 的基线
            //   得到 0.57，干干净净过关。改用纯模型值判（<see cref="ModelPctBeforeBlend"/>）。
            .Where(t => ModelPctBeforeBlend(t.Pick.ModelProbability, t.Pick.Odds,
                           StandardOverRound(JcPlayTypes.Bq)) >= t.BasePct * CounterHighModelVsBaseMin)
            // 模型自己怎么看这个价（2026-11-08 新增，见 CounterHighModelVsMarketMin）：
            // 纯模型值必须 ≥ 市场去水隐含 × 0.70。模型认为市场高估 30% 以上就别买——
            // 我们付的是市场价，没理由赌一个自己模型都不看好的价。
            .Where(t =>
            {
                var over = StandardOverRound(JcPlayTypes.Bq);
                if (over <= 0m) return true;
                var marketPct = 100m / t.Pick.Odds / over;
                var purePct = ModelPctBeforeBlend(t.Pick.ModelProbability, t.Pick.Odds, over);
                return purePct >= marketPct * CounterHighModelVsMarketMin;
            })
            // 保留全局 EV 天花板（防模型爆表把 EV 撑到 2.5 以上）
            .Where(t => t.Pick.Ev <= MaxEv)
            .Where(t => !picks.Any(p => p.MatchId == t.Pick.MatchId))
            // 区间内排序：赔率降序（2026-11-08 保留）。
            // 区间已经锁死在 30~<40，这里只决定「39 还是 30」——同一档内取更高的赔率，
            // 不做 EV 排序（EV 排序会把 30 倍排在 39 倍前面，等于把上限收成 30）。
            // ThenBy 基线概率：同为 32 倍时，优先这个联赛历史上真会翻的那一注
            // （今天 伊普斯 英超负胜 3.136% 因此排在 库奥皮奥 芬超负胜 之前）。
            .OrderByDescending(t => t.Pick.Odds)
            .ThenByDescending(t => t.BasePct)
            .FirstOrDefault();

        // ── 单列候选诊断（2026-11-08 加）──────────────────────────────────────
        //    把 30~<40 档每一条候选的落点算出来，只用于展示与落库，**不参与选注**：
        //    即使某条被判「模型否决」，它也不会因此被重新放行，选注结果与不加这段完全一致。
        //
        //    起因：候选区接口在 API 层重跑三道闸，唯一漏掉的是「模型不得强烈否定」——
        //    那道闸要模型的 P(3-0)/P(0-3)，API 层拿不到。于是「被模型否决」和「被赔率挤出」
        //    在页面上长得一样（11-08 热那亚 负胜@36 就是被模型否决的，页面只显示「未上榜」）。
        //
        //    ★ 判定顺序与下面那条选注链严格一一对应，改任一边必须同步另一边：
        //      IsHalfFullReverse + 赔率区间 → Known → Passes(1.2%)
        //      → 模型纯值≥基线×0.40 → 模型纯值≥市场×0.70 → Ev≤MaxEv
        //      → 同场未上主榜 → 排序取第一。
        //      （ratio ∈[0.95,1.35] 那道闸已于 2026-11-08 删除，此处同步移除）
        var counterDiag = new List<MilkCounterCandidate>();
        foreach (var c in result.Where(x => x.PlayType == JcPlayTypes.Bq && IsHalfFullReverse(x)
                                            && x.Odds >= CounterHighMinOdds && x.Odds < CounterHighMaxOdds))
        {
            var bpv = LeagueHalfFullBaseline.Pct(c.LeagueName ?? "", c.OptionCode) ?? 0m;
            // 这个比值只做展示（页面上标「基线EV比」），**不再参与任何判定**（2026-11-08 删闸）。
            // 保留它是因为它能解释「联赛历史频率比市场定价乐观多少」，是给人看的参考量，
            // 而不是给闸门的判据——它衡量的从来不是「这场球值不值」。
            var ratio = bpv > 0m ? decimal.Round(bpv / 100m * c.Odds / bqPayback, 3) : (decimal?)null;
            string verdict;
            string why;

            if (!LeagueHalfFullBaseline.Known(c.LeagueName ?? "") || bpv <= 0m)
            {
                verdict = MilkCounterVerdicts.NoLeagueBaseline;
                why = $"联赛「{c.LeagueName}」不在基线表，拿不到该档实测频率，单列不敢用兜底均值背书";
            }
            else if (!LeagueHalfFullBaseline.Passes(c.LeagueName ?? "", c.OptionCode, MinCounterHighLeaguePct))
            {
                verdict = MilkCounterVerdicts.LeagueBelowFloor;
                why = $"该联赛该档实测 {bpv:F2}% < 准入门槛 {MinCounterHighLeaguePct:F2}%（友谊赛这类基本不出反转的联赛在此出局）";
            }
            else if (ModelPctBeforeBlend(c.ModelProbability, c.Odds, StandardOverRound(JcPlayTypes.Bq))
                     < bpv * CounterHighModelVsBaseMin)
            {
                verdict = MilkCounterVerdicts.ModelVeto;
                why = $"模型仅 {c.ModelProbability:F2}%，低于基线×{CounterHighModelVsBaseMin:F2}"
                    + $"（{bpv * CounterHighModelVsBaseMin:F2}%）——模型连方向都不认同";
            }
            else if (StandardOverRound(JcPlayTypes.Bq) > 0m
                     && ModelPctBeforeBlend(c.ModelProbability, c.Odds, StandardOverRound(JcPlayTypes.Bq))
                        < 100m / c.Odds / StandardOverRound(JcPlayTypes.Bq) * CounterHighModelVsMarketMin)
            {
                // 2026-11-08 新增：模型自己怎么看这个价。热那亚 负胜@36 就是死在这一条——
                // 纯模型 1.21% vs 市场隐含 2.22% = 0.55，低于 0.70，模型认为市场高估近一半。
                var over = StandardOverRound(JcPlayTypes.Bq);
                var marketPct = 100m / c.Odds / over;
                var purePct = ModelPctBeforeBlend(c.ModelProbability, c.Odds, over);
                verdict = MilkCounterVerdicts.ModelVsMarket;
                why = $"模型纯值 {purePct:F2}% 只有市场隐含 {marketPct:F2}% 的 "
                    + $"{purePct / marketPct:F2} 倍（门槛 {CounterHighModelVsMarketMin:F2}）"
                    + "——不是市场高估，是模型自己也不信这个价，不买";
            }
            else if (c.Ev > MaxEv)
            {
                verdict = MilkCounterVerdicts.EvTooHigh;
                why = $"名义 EV {c.Ev:F2} 超全局上限 {MaxEv:F2}，模型概率明显爆表";
            }
            else if (picks.Any(p => p.MatchId == c.MatchId))
            {
                verdict = MilkCounterVerdicts.SameMatchOnBoard;
                why = "该场已占主榜名额，同一场不重复出注";
            }
            else if (counterHighRow is not null && counterHighRow.Pick.MatchId == c.MatchId
                     && counterHighRow.Pick.PlayType == c.PlayType
                     && counterHighRow.Pick.OptionCode == c.OptionCode)
            {
                verdict = MilkCounterVerdicts.Admitted;
                why = "单列选中";
            }
            else
            {
                verdict = MilkCounterVerdicts.LostToRank;
                why = "过全部闸，但同档有赔率更优的注被选中（每天只留 1 注）";
            }

            counterDiag.Add(new MilkCounterCandidate
            {
                MatchId = c.MatchId ?? 0,
                MatchNo = c.MatchNo,
                LeagueName = c.LeagueName,
                HomeTeam = c.HomeTeam,
                AwayTeam = c.AwayTeam,
                MatchTime = c.MatchTime,
                OptionCode = c.OptionCode,
                Odds = c.Odds,
                ModelProbability = c.ModelProbability,
                BasePct = bpv > 0m ? bpv : null,
                Ratio = ratio,
                Verdict = verdict,
                Reason = why
            });
        }

        if (counterHighRow is not null)
        {
            var counterHigh = counterHighRow.Pick;
            counterHigh.CounterHigh = true;
            // 单列注展示用的期望值走基线口径，不走模型口径（模型在这种档位不可信）。
            counterHigh.BaselineProbability = counterHighRow.BasePct;
            counterHigh.BaselineEv = decimal.Round(counterHighRow.BasePct / 100m * counterHigh.Odds, 3);
            picks.Add(counterHigh);
        }

        // 「模型低可信」统一打标（2026-11-07 夜）：对所有最终入选的半全场反转档生效，
        // 不分主榜/单列。放在最后统一扫一遍而不是在选注时逐个算，理由有两个：
        // ① 主榜那 3 个半全场名额里也可能出反转档（平负/平胜 概率 11%~14%，能过 12% 门槛），
        //    它们同样存在「模型比联赛基线保守」的问题，不能只标单列注；
        // ② 单列注刚在上面才补了 BaselineProbability，在选注过程中扫描时它还读不到。
        // 只标不撤：用户要的是「把事实摆出来」，不是「用模型的不自信替用户做决定」。
        // 回填实测基线（2026-11-08 加）：让榜面每一注都能回答「按真实概率算，这注值多少」。
        // 必须排在 MarkLowConfidence 之前——那个方法靠 BaselineProbability 判断「模型是否比历史保守」。
        MarkBaseline(picks, odds);
        MarkLowConfidence(picks);
        // 名义 EV 超本玩法返奖率的注打「模型高估」标（2026-11-08 加）：只标不撤。
        // 榜单上出现 EV 1.138 这种数字，等于在暗示「这注不亏」，而返奖率 0.797 已经把上限写死了。
        MarkOverconfident(picks);

        funnel.Final = picks.Count;
        return new MilkBuildResult
        {
            Picks = picks,
            Gates = gates,
            ShowRows = showRows,
            Funnel = funnel,
            CounterCandidates = counterDiag
        };
    }

    /// <summary>
    /// 给半全场反转档打「模型低可信」标记（2026-11-07 夜，从 Build 里的内联循环抽出）。
    ///
    /// 判据：模型概率 &lt; 联赛基线 × <see cref="ModelLowConfidenceRatio"/>(0.70)，只标不撤。
    /// 顺风档（胜胜/平平/负负）不参与——那些档模型样本充足、概率本来就在 12% 以上，
    /// 不存在「模型比历史保守 30%」的问题，标了只会制造噪声。
    ///
    /// 独立成 public 方法而不是内联在 Build 里，是为了能单测：
    /// 内联时想验证它必须构造一整套能选中 12 倍反转档的赔率网格，而那种网格
    /// 对模型概率的约束是间接的（要经过 Blend），测挂了也说不清是哪一步在起作用。
    ///
    /// 侧效应：被标记的注会补上 <see cref="MilkPick.BaselineProbability"/> 与
    /// <see cref="MilkPick.BaselineEv"/>——前端要并排展示「模型 X% / 基线 Y%」，
    /// 只给布尔标记不给数字，等于让用户自己猜比例。
    /// </summary>
    public static void MarkLowConfidence(IEnumerable<MilkPick> picks)
    {
        foreach (var p in picks.Where(x => x.PlayType == JcPlayTypes.Bq && IsHalfFullReverse(x)))
        {
            var basePct = p.BaselineProbability ?? LeagueHalfFullBaseline.Pct(p.LeagueName, p.OptionCode);
            if (basePct is not > 0m) continue;
            if (p.ModelProbability >= basePct.Value * ModelLowConfidenceRatio) continue;

            p.ModelLowConfidence = true;
            p.BaselineProbability ??= basePct;
            p.BaselineEv ??= decimal.Round(basePct.Value / 100m * p.Odds, 3);
        }
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

    /// <summary>
    /// 榜单玩法的固定顺序。2026-11-06 用户定调侧重点：主攻半全场 → 总进球为辅 → 其次比分。
    /// 顺序即优先级，perMatch 逐场跟进按它选「每场一个大奶」，全局榜按它分玩法排序。
    /// </summary>
    private static readonly string[] PlayOrder = { JcPlayTypes.Bq, JcPlayTypes.Nspf, JcPlayTypes.Jq, JcPlayTypes.Bf };

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

    /// <summary>
    /// 方向码转文字，只用于诊断文案。
    /// </summary>
    private static string DiagOf(char? d) => MilkDirectionDiagnosis.DirName(d);

    /// <summary>
    /// 市场明确大热、模型却掉头——这一种形态单独砍掉（2026-11-08 加）。
    ///
    /// 实证口径：竞彩 SPF 客胜赔率 1.55~1.75 的 31 场已完赛，实际主胜 19.4% / 平 22.6% / 客胜 58.1%。
    /// 也就是说，当市场把某一侧定到 50% 以上、而模型指向反面且差得远时，
    /// 跟市场的胜率是 58%~82%，跟模型只有 19%。这不是阈值微调，是两个数量级的差距。
    ///
    /// 触发条件（三条同时成立）：
    ///   1. 市场最强侧 ≥ <see cref="MarketStrongPct"/>（=50%，明确大热，不是「市场没方向」）；
    ///   2. 本场注的方向与市场最强方向相反；
    ///   3. 市场最强侧比模型侧在该市场里的定价高出 ≥ <see cref="MarketStrongReverseGap"/>（=20 个点）。
    ///
    /// 与 `EnforceDirectionVeto` 的关系：那个常量是「混合方向背离」的回测裁决（444 场 3745 注，
    /// 反向注命中反而更好 → 不砍）。它把「逆 5 个点」和「逆一个 52% 的大热」混成一类，
    /// 而后者有独立的、方向相反的实证。两者不冲突：那个管弱背离，这个管大热逆向。
    /// </summary>
    internal static bool MarketStrongReverse(MilkDirectionDiagnosis diag, char? pickDir)
    {
        if (diag.MarketDirection is not char md) return false;
        var jd = pickDir ?? diag.ModelDirection;
        if (jd is not char mine || mine == md) return false;

        var strong = MarketPctOf(diag, md);
        var weak = MarketPctOf(diag, mine);
        if (strong is not decimal s || weak is not decimal w) return false;
        return s >= MarketStrongPct && s - w >= MarketStrongReverseGap;
    }

    /// <summary>
    /// 每个玩法最多几条（全局榜）。2026-11-06 用户定调侧重点「主攻半全场 → 总进球为辅 → 其次比分」：
    /// 半全场 3 条、总进球 2 条、比分 1 条，名额向半全场倾斜，比分降为点缀。
    /// </summary>
    /// <summary>
    /// 各玩法名额。**2026-11-11 按返奖率重排，砍掉比分**。
    ///
    /// 这是本榜唯一不依赖模型准确度的改动依据：返奖率是玩法自带的硬天花板，
    /// 模型再准也超不过它。官方返奖率：SPF/NSPF **0.878**、BQ/JQ **0.797**、BF **0.710**。
    ///
    /// 改前 BQ3 + NSPF1 + JQ2 + BF2 = 8 注，加权期望 **0.785** —— 也就是这个榜从产品定义
    /// 那天起，每 100 元期望回收 78.5 元。参数调得再细也改不了这个数。
    /// 砍 BF 的理由是双重的：返奖率最低（.710），且实盘 4 注 0 中、`BaselineEv` 全空。
    ///
    /// ★★ **NSPF 那条是死配置，别被它骗了**（2026-11-11 实测）：
    ///   引擎里只有 Bf / Jq / Bq 三条候选生成循环，**没有 NSPF 生成器**。
    ///   所以 `[Nspf]` 这个名额提到多少都不会多出一注票 —— 今天把 1 提到 3，
    ///   落库结果 NSPF 仍是 0 注，榜面加权期望只从 0.785 挪到 0.797。
    ///   要真正吃 0.878 的返奖率，得先写 NSPF 候选生成器（让球三档 + 去水 + 闸门），
    ///   那是新功能，不是调参数。
    ///
    /// ★ 必须承认的边界：即使 NSPF 出满，加权期望也只有 0.846，**仍然小于 1**。
    ///   换玩法只能把「必亏 21.5%」压到「必亏 15.4%」，不可能变成盈利 ——
    ///   要变正必须靠模型相对盘口的 alpha，而 Brier 实测模型 .5995 差于市场 .5812。
    ///   所以这是止损动作，不是盈利动作。真正有正向证据的只有稳胆（见 SteadyPickRules）。
    /// </summary>
    private static readonly Dictionary<string, int> MaxPerPlay = new()
    {
        // 2026-11-11 双选：2 → 4。每场最多两注（<see cref="PerMatchMaxPicks"/>），
        // 这个全局名额必须比「场数 × 2」大，否则同一场的第二档会被全局切片切掉、双选形同虚设。
        [JcPlayTypes.Bq] = 4,
        // 名义配额 1（历史值），实际恒为 0 注 —— 引擎无 NSPF 生成器，见上。
        [JcPlayTypes.Nspf] = 1,
        // 2026-11-11 由 2 降 1（只取最热那档）；同日晚因双选回到 2，允许同场两档并出。
        [JcPlayTypes.Jq] = 2,
        [JcPlayTypes.Bf] = 0
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

    /// <summary>
    /// 赛事类型分类（2026-11-06 加，2026-11-07 起被半全场选注消费）。
    /// 冷门是「赛事类型放大的」：杯赛反转档合计 15.1% > 联赛 11.1% > 友谊赛 2.7%（39.5万场实测）；
    /// 友谊赛反转档整类不发、杯赛低水热门触发平负/负负提级（见 CupHotFade）。
    /// 返回 "CUP"=杯赛 / "FRIENDLY"=友谊赛 / "LEAGUE"=联赛。
    /// </summary>
    private static string LeagueCategory(string? leagueName)
    {
        if (string.IsNullOrWhiteSpace(leagueName)) return "LEAGUE";
        if (leagueName.Contains("友谊", StringComparison.Ordinal) || leagueName.Contains("热身", StringComparison.Ordinal))
            return "FRIENDLY";
        if (leagueName.Contains("杯", StringComparison.Ordinal)
            || leagueName.Contains("欧冠", StringComparison.Ordinal)
            || leagueName.Contains("欧罗巴", StringComparison.Ordinal)
            || leagueName.Contains("欧会", StringComparison.Ordinal)
            || leagueName.Contains("欧协", StringComparison.Ordinal)
            || leagueName.Contains("亚冠", StringComparison.Ordinal)
            || leagueName.Contains("欧国联", StringComparison.Ordinal)
            || leagueName.Contains("世预", StringComparison.Ordinal)
            || leagueName.Contains("世界杯", StringComparison.Ordinal)
            || leagueName.Contains("欧洲杯", StringComparison.Ordinal)
            || leagueName.Contains("美洲杯", StringComparison.Ordinal)
            || leagueName.Contains("亚洲杯", StringComparison.Ordinal)
            || leagueName.Contains("非洲杯", StringComparison.Ordinal)
            || leagueName.Contains("超级杯", StringComparison.Ordinal)
            || leagueName.Contains("联赛杯", StringComparison.Ordinal)
            || leagueName.Contains("足总", StringComparison.Ordinal)
            || leagueName.Contains("国王杯", StringComparison.Ordinal)
            || leagueName.Contains("总统杯", StringComparison.Ordinal)
            || leagueName.Contains("冠军", StringComparison.Ordinal)
            || leagueName.Contains("资格", StringComparison.Ordinal)
            || leagueName.Contains("附加", StringComparison.Ordinal))
            return "CUP";
        return "LEAGUE";
    }

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

    /// <summary>
    /// 热门方体能/状态的人类可读标注（2026-11-08，曼城复盘）。返回 null = 无可标注项。
    ///
    /// **这里只产出文字，不做任何概率调整**——回测方向是反的（见 <see cref="TeamForm"/>）：
    /// 「欧战后 96 小时内」的热门方实际兑现 63.9% vs 市场隐含 59.1%，三连败组 +6.3 点。
    /// 拿它扣概率会亏得更多，所以把判断权交回给人，引擎只负责把事实摆出来。
    ///
    /// 门槛说明：欧战按 96 小时（跨国的舟车 + 时差，72 小时太紧会漏掉绝大多数场次）；
    /// 非欧战的短休按 72 小时。
    /// </summary>
    public static string? DescribeFatigue(TeamForm? form, DateTime? kickoff)
    {
        if (form is null || form.LastMatchTime is null || kickoff is not DateTime ko) return null;

        var rest = form.RestHours(ko);
        var bits = new List<string>();
        if (form.ThreeLossStreak) bits.Add("连败3场");
        if (form.PlayedContinentalLast && rest is > 0 and < 96) bits.Add($"欧战后{rest}小时");
        else if (rest is > 0 and < 72) bits.Add($"休息{rest}小时");

        return bits.Count == 0 ? null : "热门方" + string.Join("+", bits);
    }

    /// <summary>
    /// 从本场已采报价里拼出 <see cref="HalfDrawModel"/> 的输入：SPF 三项 + 总进球八档 + 胜平/负平两档。
    ///
    /// 为什么在引擎里就地拼、不再查一次库：这三组报价本来就在 <c>points</c> 里
    /// （<see cref="HalfDrawRepository"/> 走的是同一批表，只是给参考接口单独取了一次）。
    /// 选注链上多一次往返没有意义，还会让「引擎算的」和「面板显示的」有机会对不上。
    ///
    /// 缺 SPF 任一项就返回 null——没有主客强弱，模型无从下手，调用方回落联合概率。
    /// 总进球缺档时 <see cref="HalfDrawModel"/> 自己会回落泊松，不需要在这里拦。
    /// </summary>
    private static HalfDrawEstimate? RefineHalfDraw(IReadOnlyList<JcOddsHistoryPoint> points)
    {
        decimal? o3 = null, o1 = null, o0 = null, b31 = null, b01 = null;
        var jq = new decimal?[8];
        foreach (var pt in points)
        {
            if (pt.Odds <= 1m) continue;
            switch (pt.PlayType)
            {
                case JcPlayTypes.Spf:
                    if (pt.OptionCode == "3") o3 = pt.Odds;
                    else if (pt.OptionCode == "1") o1 = pt.Odds;
                    else if (pt.OptionCode == "0") o0 = pt.Odds;
                    break;
                case JcPlayTypes.Jq:
                    if (int.TryParse(pt.OptionCode, out var g) && g >= 0 && g <= 7) jq[g] = pt.Odds;
                    break;
                case JcPlayTypes.Bq:
                    if (pt.OptionCode == "3-1") b31 = pt.Odds;
                    else if (pt.OptionCode == "0-1") b01 = pt.Odds;
                    break;
            }
        }
        return HalfDrawModel.Estimate(o3, o1, o0, jq, b31, b01);
    }

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
