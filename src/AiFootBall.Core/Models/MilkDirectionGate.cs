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
/// 于是加这道闸——模型方向和市场方向背离的场次，整场不发。
/// </summary>
public static class MilkGateVerdicts
{
    /// <summary>模型方向与市场方向一致（或市场无明确方向但差距极小），放行。</summary>
    public const string Pass = "pass";

    /// <summary>模型方向与市场方向背离，整场不发。</summary>
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
/// 背景：大奶榜有 MinTeamGames=5 的样本熔断，欧战球队一个月在单一赛事只踢 2-3 场，
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

    /// <summary>被方向闸挡下的场次数。</summary>
    public int DivergedMatches => Gates.Count(g => g.Verdict == MilkGateVerdicts.Diverge);

    /// <summary>被方向闸挡下的注数。</summary>
    public int BlockedPicks => Gates.Sum(g => g.BlockedPicks);
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
