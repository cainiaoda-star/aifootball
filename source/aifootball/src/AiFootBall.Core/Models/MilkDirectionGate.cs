namespace AiFootBall.Core.Models;

/// <summary>
/// 方向闸的三种结论。
///
/// 2026-11-05 加。起因是 11-04 晚三场全反：朗斯被科莫打 1:9、费内巴切反赢利物浦、波尔图没平。
/// 事后用库里 1873 场已完赛样本回测三层信号（亚盘水位 / 欧赔隐含 / 必发大额）的方向命中率：
///   · 亚盘水位方向 47.7%——等于抛硬币；
///   · 欧赔隐含方向 61.0%、必发大额方向 74.4%；
///   · 三层一致 84.4%，三层背离时跟亚盘只有 26.3%（亚盘在背离时几乎是反向指标）。
/// 结论：方向这件事不能听模型的，也不能听亚盘的，只能听市场（欧赔/竞彩去水）。
/// 于是加这道闸——模型方向和市场方向背离的场次，**标记并降权诊断**。
///
/// 注意（2026-11-08 修正）：`EnforceDirectionVeto=false`，这道闸实际**不砍注**，只标注。
/// 原因是硬砍回测为负 alpha（444 场 3745 注，前四档「反向」命中/回报优于「同向」）。
/// 原文档写「整场不发」与真实行为不符，已改。
/// </summary>
public static class MilkGateVerdicts
{
    /// <summary>模型方向与市场方向一致（或市场无明确方向但差距极小），放行。</summary>
    public const string Pass = "pass";

    /// <summary>模型方向与市场方向背离。默认只标注不砍注（见 EnforceDirectionVeto）。</summary>
    public const string Diverge = "diverge";

    /// <summary>市场方向算不出来（快照不完整），闸不生效——宁可不管，也不用残缺数据瞎判。</summary>
    public const string Unknown = "unknown";
}

/// <summary>诱盘告警代码。亚盘和必发跟市场对着干时记下来，但不否决。</summary>
public static class MilkGateAlerts
{
    /// <summary>亚盘在往一边走，市场在另一边——盘口造势的典型形态。</summary>
    public const string TrapAsian = "trap-asian";

    /// <summary>必发大额与市场方向相反。</summary>
    public const string TrapExchange = "trap-exchange";

    /// <summary>两层同时背离。朗斯那场就是这个形态。</summary>
    public const string TrapBoth = "trap-both";

    public static string NameOf(string? code) => code switch
    {
        TrapAsian => "亚盘与市场背离（疑似诱盘）",
        TrapExchange => "必发与市场背离",
        TrapBoth => "亚盘+必发同时背离（典型诱盘）",
        _ => code ?? ""
    };
}

/// <summary>
/// 一场比赛的方向闸诊断。落库到 dbo.FbMilkGate，是「这注为什么没发」的唯一凭证。
///
/// 为什么不只写日志：11-04 那次三场全反，事后想复盘「当时模型和市场各指哪儿」，
/// 数据已经跟着新一轮快照滚掉了。诊断必须与出票同时落库，赛后才有得对。
/// </summary>
public sealed class MilkDirectionDiagnosis
{
    public long? MatchId { get; set; }
    public string MatchNo { get; set; } = "";
    public string? LeagueName { get; set; }
    public string? HomeTeam { get; set; }
    public string? AwayTeam { get; set; }
    public DateTime? MatchTime { get; set; }

    /// <summary>模型（Dixon-Coles 网格）最强方向：3 主胜 / 1 平 / 0 客胜。</summary>
    public char? ModelDirection { get; set; }
    /// <summary>模型三项概率（%）。</summary>
    public decimal ModelHomePct { get; set; }
    public decimal ModelDrawPct { get; set; }
    public decimal ModelAwayPct { get; set; }

    /// <summary>
    /// 这场候选注实际指向的方向——**否决就是拿它跟市场比的**。
    ///
    /// 为什么不拿 ModelDirection 比（2026-11-05 改）：模型整体可能指主胜，
    /// 而这一场真正要发的是「平平」（11-04 波尔图就是），拿整场倾向去比会放过逆市场的注；
    /// 反过来模型整体偏平、但发的是主胜比分，也会被误判。榜是具体某一注，判据就得是那一注的方向。
    /// </summary>
    public char? PickDirection { get; set; }

    /// <summary>市场最强方向。取自竞彩胜平负去水，退而求其次用比分盘反推。</summary>
    public char? MarketDirection { get; set; }
    /// <summary>市场方向来源：spf / score / none。</summary>
    public string MarketSource { get; set; } = "none";
    public decimal? MarketHomePct { get; set; }
    public decimal? MarketDrawPct { get; set; }
    public decimal? MarketAwayPct { get; set; }

    /// <summary>亚盘方向（盘口移动方向）：3 往主队移，0 往客队移，null 没动或没数据。</summary>
    public char? AsianDirection { get; set; }
    public decimal? AsianOpen { get; set; }
    public decimal? AsianClose { get; set; }

    /// <summary>必发大额方向。</summary>
    public char? ExchangeDirection { get; set; }
    public decimal? RatioHome { get; set; }
    public decimal? RatioAway { get; set; }

    /// <summary><see cref="MilkGateVerdicts"/> 之一。</summary>
    public string Verdict { get; set; } = MilkGateVerdicts.Unknown;

    /// <summary><see cref="MilkGateAlerts"/> 之一，没有则为 null。</summary>
    public string? AlertCode { get; set; }
    /// <summary>给人看的一句说明。</summary>
    public string? AlertNote { get; set; }

    /// <summary>本次被这道闸挡下的注数。</summary>
    public int BlockedPicks { get; set; }
    /// <summary>被挡注里概率最高的一条，形如「半全场 胜胜 19.0%」。</summary>
    public string? BlockedBest { get; set; }
    public decimal? BlockedBestProbability { get; set; }

    /// <summary>方向的中文名。页面直接显示。</summary>
    public static string DirName(char? d) => d switch
    {
        '3' => "主胜",
        '1' => "平",
        '0' => "客胜",
        _ => "-"
    };

    /// <summary>一行摘要，前端和日志共用。</summary>
    public string Summary
    {
        get
        {
            var model = DirName(ModelDirection);
            var market = MarketDirection is null ? "未知" : DirName(MarketDirection);
            var text = $"模型 {model} / 市场 {market}";
            if (Verdict == MilkGateVerdicts.Diverge)
                text += $"；挡下 {BlockedPicks} 注";
            if (!string.IsNullOrEmpty(AlertCode))
                text += $"；{MilkGateAlerts.NameOf(AlertCode)}";
            return text;
        }
    }
}

/// <summary>
/// 「模型 vs 市场」对照展示行（2026-11-05 加，方案 2：只做展示、不出注）。
///
/// 背景：大奶榜有样本熔断（门槛只有一份：<see cref="MilkThresholds.MinTeamGames"/>，线上为 3），
/// 欧战球队一个月在单一赛事只踢 2-3 场，
/// 于是整天空榜。空榜不等于「算不出来」——模型对每一场其实都算了三边概率，
/// 只是样本不足不敢出注。与其让用户盯着空榜猜，不如把「模型怎么想 vs 市场怎么想」
/// 摊开给用户看：模型给的三边概率、市场去水的三边概率、以及这一场为什么不出注。
/// 这行只作展示，永远不参与出票。
/// </summary>
public sealed class MilkShowRow
{
    public long? MatchId { get; set; }
    public string MatchNo { get; set; } = "";
    public string? LeagueName { get; set; }
    public string HomeTeam { get; set; } = "";
    public string AwayTeam { get; set; } = "";
    public DateTime? MatchTime { get; set; }

    /// <summary>模型三边概率（%）。模型不认识这两队、或算不出分布时为 null。</summary>
    public decimal? ModelHomePct { get; set; }
    public decimal? ModelDrawPct { get; set; }
    public decimal? ModelAwayPct { get; set; }

    /// <summary>市场三边概率（%）。无竞彩快照或去水失败时为 null。</summary>
    public decimal? MarketHomePct { get; set; }
    public decimal? MarketDrawPct { get; set; }
    public decimal? MarketAwayPct { get; set; }
    public string MarketSource { get; set; } = "none";

    /// <summary>
    /// 这一场为什么（不）出注：
    ///   known=样本充足、正常出注；thin-sample=样本不足不出注；
    ///   unknown-team=模型不认识不出注；no-odds=无快照；no-name=队名缺失。
    /// </summary>
    public string Status { get; set; } = "known";

    /// <summary>主客队在本赛事口径下的训练样本场次。</summary>
    public int HomeGames { get; set; }
    public int AwayGames { get; set; }

    /// <summary>模型最强方向 vs 市场最强方向，供前端高亮「模型在跟市场对着干」的场次。</summary>
    public char? ModelDirection { get; set; }
    public char? MarketDirection { get; set; }
}

/// <summary>一次大奶计算的完整结果：发出去的榜 + 每场的闸门诊断。</summary>
public sealed class MilkBuildResult
{
    public List<MilkPick> Picks { get; init; } = new();
    public List<MilkDirectionDiagnosis> Gates { get; init; } = new();

    /// <summary>「模型 vs 市场」对照展示行（方案 2，只展示不出注）。覆盖所有受注场次。</summary>
    public List<MilkShowRow> ShowRows { get; init; } = new();

    /// <summary>构建漏斗，空榜时用它解释「卡在哪一层」。2026-11-05 加。</summary>
    public MilkFunnel Funnel { get; init; } = new();

    /// <summary>
    /// 完整分布存档（2026-11-12 加）：每场每玩法**每一个档位**的纯模型 / 市场 / 混合概率，
    /// 不只出票的那几档。落库 <c>dbo.FbMilkProbSnapshot</c>。
    ///
    /// 与 <see cref="Picks"/> 的关系：Picks ⊂ 这里的档位集合，但这里的档位**不是**候选注——
    /// 没有赔率闸、没有价值闸、没有名额限制。它回答的是「模型当时给这场每一档算了多少」，
    /// 有了它才能事后复盘「这道闸拦掉的档到底该不该拦」。
    /// </summary>
    public List<MilkProbSnapshot> ProbSnapshots { get; init; } = new();

    /// <summary>
    /// 30~40 倍反转档候选的引擎判定（2026-11-08 加）。
    /// 只做展示与追溯，不影响选注——即使某条被判「模型否决」，它也不会因此被重新放行。
    /// 落库表 FbMilkCounterCandidate，接口 /api/milk/counter-candidates 直接读。
    /// </summary>
    public List<MilkCounterCandidate> CounterCandidates { get; init; } = new();

    /// <summary>
    /// 本次真正进了 Milk 引擎计算的场次（2026-11-13 加）。落库 <c>dbo.FbMilkInput</c>。
    ///
    /// 为什么需要它：验收要问「该日所有进入计算的场次，存档是否全覆盖」，而此前没有任何
    /// 地方记得住「哪些场进了计算」——
    ///   · <c>FbMilkGate</c> 只对走到方向闸的场次写行，被禁推名单/无赔率/样本不足挡掉的没有；
    ///   · <c>Recommendation</c> 是**胜平负**那条链路的展示结果（经过 ForDisplay 过滤），
    ///     跟 Milk 的输入集既不同源也不同量（11-13 实测：Recommendation 1 行，Milk 侧受注 11 场、
    ///     禁推 8 场、真正计算 3 场）；
    ///   · 用「几张下游表的并集」当分母，等于拿结果反推输入，少算一场就永远查不出来。
    ///
    /// 记录点固定在**算出分布之后、方向闸之前**（`funnel.Distributed++` 处），
    /// 所以它既是「进了计算」的准确边界，也不会被后续撤注影响。
    /// </summary>
    public List<MilkInputMatch> Inputs { get; init; } = new();

    /// <summary>
    /// 引擎自己申报的合法缺项（2026-11-13 第五轮）。
    ///
    /// 与 <see cref="MilkExpectedSlate"/> 的分工：预期档位由「输入 × 玩法规则」独立推出，
    /// 引擎只有在**规则内确实不该写**的时候才申报一条（例如半全场算不出联合分布、让球盘脏档）。
    /// 申报不等于放行——原因必须是固定枚举，未知原因照样判失败。
    /// 为什么必须由引擎报而不能让核对器去猜：这些分支的判据在引擎内部（joint 是否为 null、
    /// 让球数是否可用），核对器看不到；猜就等于把「漏写」和「正常跳过」混为一谈。
    /// </summary>
    public List<MilkSlateExemption> SlateExemptions { get; init; } = new();

    /// <summary>
    /// 引擎对源比赛给出的拒绝记录（2026-11-14 加）。落库 <c>dbo.FbMilkSourceMatch</c>。
    ///
    /// 每个「在写输入凭证之前就 continue」的分支都必须在这里留一条——否则那场球
    /// 在库里既没有输入凭证、也没有拒绝记录，与「本来就没有这场」长得一模一样。
    /// 11-14 的 1419319 就是这样消失的：`FbMilkInput/ProbSnapshot/Gate` 全 0 行。
    ///
    /// 申报不等于放行：原因必须是固定枚举、必须带证据前缀，`name-unresolved` 还要经
    /// 独立库检证明是真新队（见 <see cref="MilkSourceSlate"/>）。
    /// </summary>
    public List<MilkInputRejection> Rejections { get; init; } = new();

    /// <summary>被方向闸挡下的场次数。</summary>
    public int DivergedMatches => Gates.Count(g => g.Verdict == MilkGateVerdicts.Diverge);

    /// <summary>被方向闸挡下的注数。</summary>
    public int BlockedPicks => Gates.Sum(g => g.BlockedPicks);
}

/// <summary>
/// 一场「进了 Milk 引擎计算」的场次（2026-11-13 加，落库 <c>dbo.FbMilkInput</c>）。
/// 只做输入凭证，不含任何概率——概率在 <see cref="MilkProbSnapshot"/> 里。
/// </summary>
public sealed class MilkInputMatch
{
    public long MatchId { get; init; }
    public string? MatchNo { get; init; }
    public string? LeagueName { get; init; }
    public string? HomeTeam { get; init; }
    public string? AwayTeam { get; init; }
    public DateTime? MatchTime { get; init; }
    /// <summary>整批共用的版本戳，与注单/存档同源。</summary>
    public string? VersionTag { get; init; }

    /// <summary>
    /// 进计算时这一场的竞彩赔率档位清单（2026-11-13 第五轮）。
    ///
    /// 为什么把它挂在这里：发布前核对需要一份**不来自引擎输出**的「该有几档」依据。
    /// 之前那份预期取自 `build.ProbSnapshots`（引擎自己的输出），只能证明「算出来的都写了」，
    /// 证明不了「该算的都算了」。这个清单在 `Distributed++` 处与输入凭证同时落，
    /// 之后引擎怎么生成都不影响它，于是「漏一场 / 漏一档」才验得出来。
    /// 只存内存，不落库（`FbMilkInput` 仍是场级一行）。
    /// </summary>
    public IReadOnlyList<MilkInputOption> Options { get; init; } = Array.Empty<MilkInputOption>();
}

/// <summary>
/// 大奶榜构建漏斗：每过一层还剩几场。
///
/// 为什么需要它（2026-11-05 加）：榜空的时候，接口只回一句 scanned=0，
/// 于是「没有赔率」「模型不认识这两支队」「模型算不出分布」「被方向闸整场撤下」
/// 四种完全不同的原因长得一模一样，只能靠翻库猜。猜错一次就要多花半小时，
/// 而且对用户来说这都是「又没出东西」。漏斗把这条链摊开，空榜自带解释。
/// </summary>
public sealed class MilkFunnel
{
    /// <summary>当日受注场次（已过销售时段过滤）。</summary>
    public int Recs { get; set; }

    /// <summary>取到竞彩固定奖金快照的场次。</summary>
    public int WithOdds { get; set; }

    /// <summary>主客队名齐全的场次。</summary>
    public int Named { get; set; }

    /// <summary>
    /// 竞彩联赛名与数据源不一致、已自动对齐的场次（2026-11-08 加）。
    /// 典型：竞彩「韩职」→ 数据源「K1联赛」。不做这一步，整类联赛会被判成「模型从没见过的队」。
    /// </summary>
    public int LeagueAliased { get; set; }

    /// <summary>对齐明细（最多留 20 条，形如「周日002 韩职→K1联赛」），空了说明今天没有对不上的名字。</summary>
    public List<string> AliasNotes { get; set; } = new();

    /// <summary>模型认识的场次（模型 Ready 且两队都有历史样本）。</summary>
    public int Known { get; set; }

    /// <summary>
    /// 因「样本不够」被挡掉的场次（2026-11-05 加）。这是 Known 与 Distributed 之间的新一层：
    /// 认识了不等于算得准——回测台实测每队平均只有 3.9 场样本，主胜概率标准差只有市场 56%。
    /// 阈值见 MilkPickEngine.MinTeamGames。
    /// </summary>
    public int ThinSample { get; set; }

    /// <summary>算得出比分分布的场次。</summary>
    public int Distributed { get; set; }

    /// <summary>生成出候选注的场次。</summary>
    public int Candidates { get; set; }

    /// <summary>被方向闸整场撤下的场次（仅 EnforceDirectionVeto 打开时）。</summary>
    public int BlockedByGate { get; set; }

    /// <summary>
    /// 因「主胜临场升水 ≥12%」被撤下的主胜方向注数（2026-11-07，技能11落地）。
    /// 9758 场平均盘回测：升水≥12% 的主胜 ROI 恶化到 33（平盘基准 54，差 21 个点），
    /// 是全部水位分桶里唯一大幅恶化的信号——升水=硬利空，主胜方向的注直接撤。
    /// </summary>
    public int WaterRiseVeto { get; set; }

    /// <summary>
    /// 因「竞彩客胜被单边砸价、必发不认」被撤下的客胜方向注数（2026-11-08 加）。
    /// 回测：该形态 13 场，客胜实际 23.1% vs 收盘隐含 40.1%（低 17 个点）。
    /// 只撤客胜侧；主胜同形态 9 场是 +3.4 点，撤了等于白扔注。见 <see cref="PriceDivergence"/>。
    /// </summary>
    public int DivergenceVeto { get; set; }

    /// <summary>通过方向闸的注数。</summary>
    public int AfterGate { get; set; }

    /// <summary>通过 EV/命中率门槛的注数。</summary>
    public int AfterEv { get; set; }

    /// <summary>
    /// 因「与市场方向相反且市场方向占比过半」被撤掉的注数（2026-11-05 加）。
    /// 与 <see cref="AfterEv"/> 的差额就是这个数 + 一致性检查撤掉的那些。
    /// </summary>
    public int DivergentDropped { get; set; }

    /// <summary>
    /// 因「临场市场转向客队、这一注却指主胜」被撤掉的注数（2026-11-05 加）。
    /// 方向闸只认 SPF/比分去水的静态方向，拦不住比分盘深档先转向的场次，这里补读净胜球诊断。
    /// </summary>
    public int TurnDropped { get; set; }

    /// <summary>最终上榜注数。</summary>
    public int Final { get; set; }

    /// <summary>比分模型是否可用。false 时整张榜必然为空，与赔率无关。</summary>
    public bool ScoresReady { get; set; }

    /// <summary>比分模型不可用时的原因（拟合异常 / 未就绪）。空榜要能自己说出这句话。</summary>
    public string? ScoresNote { get; set; }

    /// <summary>被跳过的场次及原因（最多留 20 条，够定位是哪支队、哪个联赛对不上）。</summary>
    public List<string> Skipped { get; } = new();
}
