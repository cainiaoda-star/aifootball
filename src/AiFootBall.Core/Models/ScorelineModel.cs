namespace AiFootBall.Core.Models;

/// <summary>完场比分，用来估计球队进球和失球。</summary>
public sealed class ScoreSample
{
    public string? LeagueName { get; init; }
    public string HomeTeam { get; init; } = "";
    public string AwayTeam { get; init; } = "";
    public int HomeScore { get; init; }
    public int AwayScore { get; init; }
}

/// <summary>
/// 比分：球队进球、失球做攻防，Dixon-Coles 修正 0-0、1-0、0-1、1-1，再按大小球盘口缩放。
/// 主受让时不抬净胜球。主让时，只有净胜能盖住让球、且概率还够看，才用大比分。
/// </summary>
public sealed class ScorelineModel
{
    private const double PriorGames = 8;
    private const int MaxGoals = 6;

    /// <summary>本联赛样本达到这个数就不跨联赛取（与出注门槛 MinTeamGames 一致）。</summary>
    private const int MinOwnGames = 5;

    private readonly double _globalHome;
    private readonly double _globalAway;
    private readonly Dictionary<string, (double HomeSum, double AwaySum, int N)> _leagues;
    private readonly Dictionary<string, (double For, double Against, int Games)> _teams;

    /// <summary>
    /// 联赛强度系数（方案 1：联赛强度校准，2026-11-05 加）。键=联赛名，值=该联赛相对全局的
    /// 进球强度倍率（1.0 = 全局平均，&gt;1 表示强联赛）。估计方法见 <see cref="EstimateLeagueStrength"/>。
    /// 跨联赛累计样本时用它把「进球」折算成「跨联赛可比的攻防倍率」：强联赛的进球更有含金量。
    /// 空字典 = 不校准（线上当前口径）。
    /// </summary>
    private readonly Dictionary<string, double> _leagueStrength;

    /// <summary>
    /// 球队跨联赛累计的攻防倍率（1.0 = 该场所属联赛的平均水平）。
    ///
    /// 2026-11-05 加。原来 _teams 的键是「联赛+队名」，欧战球队被切碎：阿尔克马尔在荷甲有 100+ 场，
    /// 在欧罗巴只有 2 场，模型只认后面这 2 场，于是「样本不足」把整场挡掉——今天 9 场受注全灭就是这个原因。
    /// 一支队踢联赛、杯赛、欧战是同一支队，样本必须累加，但不能直接相加进球数：
    /// 荷甲场均 1.6 球的队和意甲场均 1.2 球的队直接比，会把「联赛进球多」当成「球队强」，
    /// 11-04 朗斯 1:9 那一注就是这么来的。所以每场先按该场所属联赛的场均归一（进球/联赛场均），
    /// 累加的是倍率而不是进球数，出场时再乘回目标联赛的场均。
    /// </summary>
    private readonly Dictionary<string, (double AttSum, double DefSum, int Games)> _teamTotals;

    /// <summary>全库队名集合（与 _teamTotals 同键）。联赛名对不上时用它兜底认队。</summary>
    private readonly HashSet<string> _globalNames;

    /// <summary>全局子序列匹配结果缓存（名字 → 全称，空串表示确认认不出）。全局匹配与联赛无关，可缓存。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _globalAlias = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _namesByLeague;
    private readonly double _rho;

    /// <summary>
    /// 本联赛样本不足时，是否允许退到跨联赛累计。true = 混合口径（默认）；
    /// false = 旧口径（只在同联赛找，不够就用先验），仅保留给回测台做 A/B 对照——
    /// 同一个进程、同一份数据里跑两遍，才能证明改动到底是好是坏。
    /// </summary>
    private readonly bool _crossLeague;

    private ScorelineModel(
        bool ready,
        double globalHome,
        double globalAway,
        Dictionary<string, (double HomeSum, double AwaySum, int N)> leagues,
        Dictionary<string, (double For, double Against, int Games)> teams,
        Dictionary<string, (double AttSum, double DefSum, int Games)> teamTotals,
        Dictionary<string, string[]> namesByLeague,
        double rho,
        bool crossLeague = true,
        Dictionary<string, double>? leagueStrength = null)
    {
        Ready = ready;
        _globalHome = globalHome;
        _globalAway = globalAway;
        _leagues = leagues;
        _teams = teams;
        _teamTotals = teamTotals;
        _globalNames = new HashSet<string>(teamTotals.Keys, StringComparer.Ordinal);
        _namesByLeague = namesByLeague;
        _rho = rho;
        _crossLeague = crossLeague;
        _leagueStrength = leagueStrength ?? new Dictionary<string, double>(StringComparer.Ordinal);
    }

    public static ScorelineModel Empty { get; } = new(false, 1.45, 1.15, new(), new(), new(), new(), -0.1);

    public bool Ready { get; }

    /// <summary>
    /// 模型是否见过这支队（有历史样本）。没见过的队回退全局均值，算出的比分概率是噪声，调用方据此跳过该场。
    ///
    /// 2026-11-05 加别名解析：竞彩官方给的是简称（布斯巴达 / 帕梅拉斯 / 阿尔克马 / 特拉布宗），
    /// 训练样本来自数据源全称（布拉格斯巴达 / 帕尔梅拉斯 / 阿尔克马尔 / 特拉布宗体育）。
    /// 原来只做精确字符串比对，当天 13 场受注卡只认出 4 场，其余 9 场全被当成「模型不认识的队」丢掉——
    /// 不是模型算不出来，是名字对不上。表现就是大奶榜莫名其妙只剩几场，甚至空榜。
    /// </summary>
    public bool KnowsTeam(string? league, string name) => Resolve(league, name) is not null;

    /// <summary>
    /// 该队在训练集里的样本场次（别名解析后）。认不出返回 0。
    ///
    /// 2026-11-05 加，依据是回测台（src/AiFootBall.Backtest）在 2405 场无泄漏样本上的结果：
    ///   · 「两队都认识」的场次 Brier 0.6232，市场 0.5857，差 0.037；
    ///   · 「至少一队不认识」的场次 Brier 0.6479，市场 0.5289，差 0.119 —— 差了 3.2 倍。
    /// 更关键的是模型主胜概率标准差只有 9.8pp（市场 17.3pp），也就是它把大多数比赛
    /// 看成同一场，靠的是先验而不是这两队。所以「认识」（>=1 场）根本不够，
    /// 必须样本够量才允许出注。阈值放在调用方（MilkPickEngine.MinTeamGames）。
    /// </summary>
    public int TeamGames(string? league, string name)
    {
        var resolved = Resolve(league, name);
        if (resolved is null) return 0;
        if (_crossLeague)
            return _teamTotals.TryGetValue(resolved, out var t) ? t.Games : 0;
        return _teams.TryGetValue(Key(league, resolved), out var g) ? g.Games : 0;
    }

    /// <summary>
    /// 把竞彩简称对到训练集里的全称。只认一种关系，且必须在同联赛内唯一：
    ///   简称的每个字按顺序出现在全称里（子序列），且长度差不超过 4 个字。
    /// 用子序列而不是子串，是因为竞彩缩的是中间：布斯巴达 ← 布拉格斯巴达、帕梅拉斯 ← 帕尔梅拉斯，
    /// 这两个都不是连续子串。长度差上限是防「米兰」把一堆队吸进来——何况它本来就有歧义，
    /// 命中两支以上直接判没认出来，宁可丢场也不猜错队。
    /// </summary>
    public string? Resolve(string? league, string name)
    {
        var lg = league?.Trim() ?? "";
        var n = name?.Trim() ?? "";
        if (n.Length == 0) return null;
        if (_teams.ContainsKey(Key(lg, n))) return n;

        // 联赛名对不上时的兜底（2026-11-05 加）：竞彩写「亚洲冠军乙级联赛」，数据源写「亚冠联2」，
        // 联赛维度一错，整场就被判成「模型不认识这支队」——可是町田泽维亚在库里明明有 85 场。
        // 队名精确相同就直接认，这一步跟联赛无关，也不会误配（要求完全相同）。
        if (_globalNames.Contains(n)) return n;

        // 人工确认过的别名：中文译名完全不同，规则推不出来（圣吉联合 = 圣吉罗斯）
        if (KnownAliases.TryGetValue(n, out var known) && _globalNames.Contains(known)) return known;

        // 联赛内子序列：竞彩缩的是中间字（布斯巴达 ← 布拉格斯巴达）
        if (_namesByLeague.TryGetValue(lg, out var all))
        {
            var (hit, ambiguous) = MatchSubsequence(n, all);
            if (hit is not null) return hit;
            // 同联赛里撞出两支以上，说明这批名单本身有歧义，交给调用方丢场，不跨联赛乱猜
            if (ambiguous) return null;
        }

        // 全局子序列：联赛名对不上时的最后一道兜底，仍要求全库唯一命中。
        if (!_globalAlias.TryGetValue(n, out var cached))
        {
            cached = MatchSubsequence(n, _globalNames).Hit ?? "";
            _globalAlias.TryAdd(n, cached);
        }
        return cached.Length == 0 ? null : cached;
    }

    /// <summary>
    /// 人工确认过的别名（竞彩简称 → 数据源全称）。只在子序列匹配不到时才用。
    ///
    /// 只收「中文译名完全不同、任何字符串规则都推不出来」的：圣吉联合 vs 圣吉罗斯（Union Saint-Gilloise）。
    /// 能从规则推出来的一律不入表——硬编码越多，越难发现规则本身失效。
    /// </summary>
    private static readonly Dictionary<string, string> KnownAliases = new(StringComparer.Ordinal)
    {
        ["圣吉联合"] = "圣吉罗斯",
        ["圣吉罗斯联合"] = "圣吉罗斯",

        // 北单译名 vs 数据源译名差异（2026-11-05 逐条人工核实，目标名在 FbMatch 里真实存在）。
        // 只收「字符串规则推不出来」的同音/形近译名差异；前后缀、子序列能推的一律不入表。
        ["上海上港"] = "上海海港",
        ["中岸水手"] = "中央海岸水手",
        ["云达不来梅"] = "云达不莱梅",
        ["伊韦尔东体育"] = "伊韦尔顿",
        ["休斯敦迪纳摩"] = "休斯顿迪纳摩",
        ["佩纳菲耶尔"] = "佩纳菲尔",
        ["兰斯克鲁纳"] = "兰斯科罗纳",
        ["兰黑姆"] = "兰赫姆",
        ["利文斯顿"] = "利云斯顿",
        ["北安普顿"] = "北安普敦",
        ["南安普顿"] = "南安普敦",
        ["卡坦扎罗"] = "卡坦萨罗",
        ["卡萨皮亚"] = "卡萨匹亚",
        ["吉尔维森特"] = "吉维森特",
        ["吉林汉姆"] = "吉灵汉姆",
        ["圣塔克拉拉"] = "圣克拉拉",
        ["圣特赖登"] = "圣图尔登",
        ["埃克塞特城"] = "埃克赛特城",
        ["埃格尔松"] = "埃格尔森德",
        ["基马诺克"] = "基尔马诺克",
        ["大田韩亚市民"] = "大田市民",
        ["奥利韦伦斯"] = "奥利维伦斯",
        ["奥洛穆茨"] = "奥洛莫茨",
        ["奥达克斯意大利人"] = "奥达科斯意大利人",
        ["富恩拉夫拉达"] = "富恩拉布雷达",
        ["布杰约维采迪纳摩"] = "布德约维茨迪纳摩",
        ["布莱顿"] = "布赖顿",
        ["帕尔杜比采"] = "帕尔杜比斯",
        ["弗罗西诺内"] = "弗洛西诺内",
        ["摩雷伦塞"] = "摩雷伦斯",
        ["斯托姆加斯特"] = "斯特罗姆加斯特",
        ["斯洛瓦茨科"] = "斯洛瓦科",
        ["斯蒂夫尼奇"] = "斯蒂文尼奇",
        ["格兰纳达"] = "格拉纳达CF",
        ["桑讷菲尤尔"] = "桑纳菲尤尔",
        ["法尔肯贝里"] = "法尔肯堡",
        ["波代诺内"] = "波德诺内",
        ["波尔蒂芒斯"] = "波尔蒂芒尼斯",
        ["波特兰伐木者"] = "波特兰伐木工",
        ["浦项铁人"] = "浦项制铁",
        ["海尔蒙德体育"] = "海尔蒙特",
        ["清水心跳"] = "清水鼓动",
        ["温哥华白浪"] = "温哥华白帽",
        ["湘南比马"] = "湘南海洋",
        ["特普利采"] = "特普利斯",
        ["磐田山叶"] = "磐田喜悦",
        ["福塔莱萨"] = "福塔雷萨",
        ["科切斯特联"] = "科尔切斯特联",
        ["科森扎"] = "科森察",
        ["米涅罗竞技"] = "米内罗竞技",
        ["米涅罗美洲"] = "米内罗美洲",
        ["索非亚中央陆军"] = "索菲亚中央陆军",
        ["索非亚列夫斯基"] = "索菲亚利夫斯基",
        ["纽布莱恩斯"] = "纽夫莱恩斯",
        ["聚尔特瓦勒海姆"] = "聚尔特瓦雷赫姆",
        ["莱斯特城"] = "莱切斯特城",
        ["蔚山HD"] = "蔚山现代",
        ["西布朗维奇"] = "西布罗姆维奇",
        ["西悉尼流浪者"] = "西悉尼漫步者",
        ["赫塔菲"] = "赫塔费",
        ["金泽萨维根"] = "金泽塞维根",
        ["防卫与正义"] = "防御与正义",
        ["阿尔梅利亚"] = "阿尔梅里亚",
        ["阿莫雷维耶塔"] = "阿莫雷别塔",
        ["韦斯特洛"] = "韦斯特鲁",
        ["马略卡"] = "马洛卡",
        ["鸟栖砂岩"] = "鸟栖沙岩",
    };

    /// <summary>
    /// 在候选名单里按子序列找唯一命中。长度差上限 4 个字，防止「米兰」把一堆队吸进来。
    ///
    /// 两条消歧规则（2026-11-05 加，都是被真实场次逼出来的）：
    /// 1. **梯队过滤**：候选是青年队/后备队/B 队/女足时，除非查询名自己也带这个标记，一律不认。
    ///    否则「波兹南」会撞上「波兹南莱赫青年队」「波兹南莱赫B队」，歧义一判就把整场丢了。
    /// 2. **前缀优先**：中文译名一般头词在前，「波兹南」既是「波兹南莱赫」的开头，也是「瓦塔波兹南」的结尾；
    ///    只按子序列找会判成歧义。前缀唯一命中就取它。
    /// 两条都不成立才算歧义——宁可丢场，也不猜错队。
    /// </summary>
    private static (string? Hit, bool Ambiguous) MatchSubsequence(string shortName, IEnumerable<string> candidates)
    {
        var hits = new List<string>(2);
        var shortIsReserve = IsReserveOrYouth(shortName);
        foreach (var cand in candidates)
        {
            if (cand.Length <= shortName.Length) continue;
            if (cand.Length - shortName.Length > 4) continue;
            if (!shortIsReserve && IsReserveOrYouth(cand)) continue;
            if (!IsSubsequence(shortName, cand)) continue;
            hits.Add(cand);
        }
        if (hits.Count == 0) return (null, false);
        if (hits.Count == 1) return (hits[0], false);

        string? prefix = null;
        foreach (var h in hits)
        {
            if (!h.StartsWith(shortName, StringComparison.Ordinal)) continue;
            if (prefix is not null) return (null, true);
            prefix = h;
        }
        return prefix is not null ? (prefix, false) : (null, true);
    }

    /// <summary>青年队 / 后备队 / B 队 / 女足这类附属队。它们和一线队是两套数据，不能混。</summary>
    private static bool IsReserveOrYouth(string name)
    {
        foreach (var mark in ReserveMarks)
            if (name.Contains(mark, StringComparison.Ordinal)) return true;
        return false;
    }

    private static readonly string[] ReserveMarks =
        { "青年队", "后备队", "预备队", "B队", "U19", "U20", "U21", "U23", "女足", "女子" };

    /// <summary>shortName 的每个字是否按顺序出现在 longName 里（不要求连续）。</summary>
    private static bool IsSubsequence(string shortName, string longName)
    {
        var i = 0;
        foreach (var ch in longName)
        {
            if (i < shortName.Length && ch == shortName[i]) i++;
            if (i == shortName.Length) return true;
        }
        return i == shortName.Length;
    }

    /// <summary>主客进球期望。有大小球盘时会把合计缩放到盘口的 0.65–1.55 倍。</summary>
    public (double Home, double Away)? ExpectedGoals(string? league, string home, string away, decimal? ouLine)
    {
        if (!Ready) return null;
        return Lambdas(league, home, away, ouLine);
    }

    /// <summary>
    /// 拟合。crossLeague=false（默认，线上口径）：球队样本只在本联赛内取，不够 5 场就用先验。
    /// crossLeague=true：不够时退到跨联赛累计——**回测证明更差，默认关闭**，只留给 A/B 对照：
    ///   2033 场样本充足的比赛两边完全一样（Brier 0.6047 vs 0.6047），
    ///   339 场样本不足的比赛跨联赛兜底反而更差（0.6279 vs 0.6123）。
    /// 结论：一支队在别的赛事里的攻防，不能直接搬过来用，联赛之间的强度差比样本量更值钱。
    /// </summary>
    public static ScorelineModel Fit(
        IReadOnlyList<ScoreSample> samples, bool crossLeague = false,
        Dictionary<string, double>? leagueStrength = null)
    {
        var usable = samples.Where(s =>
            !string.IsNullOrWhiteSpace(s.HomeTeam)
            && !string.IsNullOrWhiteSpace(s.AwayTeam)
            && s.HomeScore >= 0 && s.AwayScore >= 0
            && s.HomeScore <= 12 && s.AwayScore <= 12).ToList();
        if (usable.Count < 30) return Empty;

        double homeSum = 0, awaySum = 0;
        var leagues = new Dictionary<string, (double HomeSum, double AwaySum, int N)>(StringComparer.Ordinal);
        var teams = new Dictionary<string, (double For, double Against, int Games)>(StringComparer.Ordinal);
        foreach (var s in usable)
        {
            homeSum += s.HomeScore;
            awaySum += s.AwayScore;
            var league = s.LeagueName?.Trim() ?? "";
            if (league.Length > 0)
            {
                leagues.TryGetValue(league, out var lg);
                lg.HomeSum += s.HomeScore;
                lg.AwaySum += s.AwayScore;
                lg.N++;
                leagues[league] = lg;
            }
            AddTeam(teams, league, s.HomeTeam, s.HomeScore, s.AwayScore);
            AddTeam(teams, league, s.AwayTeam, s.AwayScore, s.HomeScore);
        }

        var n = usable.Count;
        var globalHome = homeSum / n;
        var globalAway = awaySum / n;

        // 联赛场均。与 LeagueAvg 同一口径：样本 ≥12 场才用自己的均值，否则回退全局，
        // 并用 30 场先验收缩，避免小样本联赛把倍率放大成噪声。
        double LgHome(string lg) => leagues.TryGetValue(lg, out var x) && x.N >= 12
            ? (x.HomeSum + 30 * globalHome) / (x.N + 30) : globalHome;
        double LgAway(string lg) => leagues.TryGetValue(lg, out var x) && x.N >= 12
            ? (x.AwaySum + 30 * globalAway) / (x.N + 30) : globalAway;

        var strength = leagueStrength ?? new Dictionary<string, double>(StringComparer.Ordinal);

        // 第二遍：按本场所属联赛的场均归一，累加的是「倍率」而不是进球数。
        // 这样荷甲 1.6 球/场的队和意甲 1.2 球/场的队才可比，跨联赛累计不会把联赛风格当成球队实力。
        // 方案 1（2026-11-05）：有联赛强度系数时，把场均再除以强度——强联赛的进球更有含金量，
        // 归一后才能跨联赛比。荷甲场均 2.8 球（进攻性强）不等于荷甲队比意甲队强。
        var teamTotals = new Dictionary<string, (double AttSum, double DefSum, int Games)>(StringComparer.Ordinal);
        foreach (var s in usable)
        {
            var lg = s.LeagueName?.Trim() ?? "";
            var sHome = strength.TryGetValue(lg, out var sh) ? sh : 1.0;
            var sAway = strength.TryGetValue(lg, out var sa) ? sa : 1.0;
            var hAvg = Math.Max(0.4, LgHome(lg) * sHome);
            var aAvg = Math.Max(0.4, LgAway(lg) * sAway);
            AddTotal(teamTotals, s.HomeTeam.Trim(), s.HomeScore / hAvg, s.AwayScore / aAvg);
            AddTotal(teamTotals, s.AwayTeam.Trim(), s.AwayScore / aAvg, s.HomeScore / hAvg);
        }

        var namesByLeague = BuildNameIndex(teams);
        var draft = new ScorelineModel(true, globalHome, globalAway, leagues, teams, teamTotals, namesByLeague, -0.1, crossLeague, strength);
        var rho = draft.FitRho(usable);
        return new ScorelineModel(true, globalHome, globalAway, leagues, teams, teamTotals, namesByLeague, rho, crossLeague, strength);
    }

    /// <summary>
    /// 估计每个联赛的相对强度系数（方案 1：联赛强度校准，2026-11-05 加）。
    ///
    /// 用跨联赛交手（两队主联赛不同，典型是欧冠/欧联/亚冠）的净胜球反推：
    ///   净胜球 ≈ 主队联赛强度 − 客队联赛强度 + 主场优势。
    /// 对每个联赛解一个「对数强度」，使预测净胜球与真实净胜球的最小二乘误差最小。
    ///
    /// 只估计「样本够多」的联赛（跨联赛交手 ≥ minGames 场），其余回退 1.0（全局平均）。
    /// 返回键=联赛名、值=进球强度倍率（1.0=全局平均）。空样本或解不出返回空字典（=不校准）。
    /// </summary>
    public static Dictionary<string, double> EstimateLeagueStrength(
        IReadOnlyList<ScoreSample> samples, int minGames = 30)
    {
        // 第一步：判定每队的「主联赛」= 出现场次最多的联赛（剔除友谊赛/杯赛噪声）。
        // 一支队踢本国联赛 + 欧战，主联赛是本国联赛。
        var teamLeagueCount = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        void Count(string team, string lg)
        {
            if (string.IsNullOrWhiteSpace(lg) || string.IsNullOrWhiteSpace(team)) return;
            if (!teamLeagueCount.TryGetValue(team, out var m))
                teamLeagueCount[team] = m = new Dictionary<string, int>(StringComparer.Ordinal);
            m.TryGetValue(lg, out var c);
            m[lg] = c + 1;
        }
        foreach (var s in samples)
        {
            Count(s.HomeTeam, s.LeagueName ?? "");
            Count(s.AwayTeam, s.LeagueName ?? "");
        }
        var mainLeague = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (team, m) in teamLeagueCount)
        {
            string? best = null; var bestN = 0;
            foreach (var (lg, c) in m)
                if (c > bestN) { bestN = c; best = lg; }
            if (best is not null) mainLeague[team] = best;
        }

        // 第二步：收集跨联赛交手（两队主联赛不同），记净胜球差 + 谁主场。
        // 一个联赛队形如 { 净胜球和, 场次 }，主场优势作为全局常数在解里一并估。
        var cross = new List<(string HomeLg, string AwayLg, double Diff)>();
        foreach (var s in samples)
        {
            if (!mainLeague.TryGetValue(s.HomeTeam, out var hl)) continue;
            if (!mainLeague.TryGetValue(s.AwayTeam, out var al)) continue;
            if (hl == al) continue;   // 同联赛不算跨联赛
            cross.Add((hl, al, s.HomeScore - s.AwayScore));
        }
        if (cross.Count < minGames) return new Dictionary<string, double>(StringComparer.Ordinal);

        // 只保留跨联赛交手够多的联赛，其余强度固定 0（对数域），避免小样本把强度吹成噪声。
        var lgCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (hl, al, _) in cross)
        {
            lgCount[hl] = lgCount.GetValueOrDefault(hl) + 1;
            lgCount[al] = lgCount.GetValueOrDefault(al) + 1;
        }
        var keep = lgCount.Where(kv => kv.Value >= minGames).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        if (keep.Count < 2) return new Dictionary<string, double>(StringComparer.Ordinal);

        // 第三步：最小二乘解 对数强度 s[lg] 和主场优势 h。
        // 方程：diff ≈ s[homeLg] - s[awayLg] + h
        // 用梯度下降（简单可靠，联赛数几十个，几万场，几十轮就收敛）。
        var ids = keep.OrderBy(x => x, StringComparer.Ordinal).ToList();
        var idx = ids.Select((lg, i) => (lg, i)).ToDictionary(x => x.lg, x => x.i, StringComparer.Ordinal);
        var sArr = new double[idx.Count];
        double home = 0.0;
        const double lr = 0.05;
        var usableCross = cross.Where(c => keep.Contains(c.HomeLg) && keep.Contains(c.AwayLg)).ToList();
        if (usableCross.Count < minGames) return new Dictionary<string, double>(StringComparer.Ordinal);

        for (var iter = 0; iter < 200; iter++)
        {
            double gHome = 0;
            var gS = new double[idx.Count];
            foreach (var (hl, al, diff) in usableCross)
            {
                var pred = sArr[idx[hl]] - sArr[idx[al]] + home;
                var err = pred - diff;   // 预测 - 真实
                gHome += err;
                gS[idx[hl]] += err;
                gS[idx[al]] -= err;
            }
            home -= lr * gHome / usableCross.Count;
            for (var i = 0; i < sArr.Length; i++)
                sArr[i] -= lr * gS[i] / usableCross.Count;
        }

        // 对数强度 → 进球强度倍率。中心化到 1.0（乘性基准），并夹到合理区间防爆。
        var mean = sArr.Average();
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var i = 0; i < ids.Count; i++)
            result[ids[i]] = Math.Clamp(Math.Exp(sArr[i] - mean), 0.5, 2.0);
        return result;
    }

    /// <summary>联赛 → 该联赛出现过的全部队名。别名解析只在这个范围内找，跨联赛不认。</summary>
    private static Dictionary<string, string[]> BuildNameIndex(
        Dictionary<string, (double For, double Against, int Games)> teams)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var key in teams.Keys)
        {
            var i = key.IndexOf('\n');
            if (i <= 0) continue;
            var lg = key[..i];
            var nm = key[(i + 1)..];
            if (!map.TryGetValue(lg, out var list)) map[lg] = list = new List<string>();
            list.Add(nm);
        }
        return map.ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.Ordinal);
    }

    public List<(int Home, int Away)> Top(
        string? league, string home, string away, char direction,
        decimal? ouLine, decimal? handicap, int take = 2, int minMargin = 1)
    {
        if (!Ready || direction is not ('3' or '1' or '0')) return new();
        var (lh, la) = Lambdas(league, home, away, ouLine);
        var margin = Math.Max(1, minMargin);
        var cells = new List<(int Home, int Away, double P)>();
        for (var h = 0; h <= MaxGoals; h++)
        {
            for (var a = 0; a <= MaxGoals; a++)
            {
                var keep = direction switch
                {
                    '3' => h - a >= margin,
                    '0' => a - h >= margin,
                    _ => h == a
                };
                if (!keep) continue;
                cells.Add((h, a, Tau(h, a, lh, la) * Poisson(h, lh) * Poisson(a, la)));
            }
        }
        if (cells.Count == 0) return new();

        var pool = cells;
        if (handicap is < 0 && direction == '3')
            pool = PreferCover(cells, handicap.Value, homeSide: true);
        else if (handicap is > 0 && direction == '0')
            pool = PreferCover(cells, handicap.Value, homeSide: false);

        return pool
            .OrderByDescending(c => c.P)
            .Take(take)
            .Select(c => (c.Home, c.Away))
            .ToList();
    }

    /// <summary>
    /// 全比分概率分布（0..6 × 0..6，Dixon-Coles 修正，含 tau，未归一化到 1——
    /// 7 球以上的尾部自然落在 1 减合计的余量里，调用方用余量算 7+ 和「其它」）。
    /// 大奶推荐（比分/总进球/半全场）的基础分布，Top 只给筛选后的前 N 格，不够用。
    /// </summary>
    public IReadOnlyList<(int Home, int Away, double P)> Distribution(
        string? league, string home, string away, decimal? ouLine)
    {
        if (!Ready) return Array.Empty<(int, int, double)>();
        var (lh, la) = Lambdas(league, home, away, ouLine);
        var cells = new List<(int Home, int Away, double P)>(49);
        for (var h = 0; h <= MaxGoals; h++)
        for (var a = 0; a <= MaxGoals; a++)
            cells.Add((h, a, Tau(h, a, lh, la) * Poisson(h, lh) * Poisson(a, la)));
        return cells;
    }

    private static List<(int Home, int Away, double P)> PreferCover(
        List<(int Home, int Away, double P)> cells, decimal handicap, bool homeSide)
    {
        var need = Math.Abs((int)Math.Round(handicap)) + 1;
        var best = cells.Max(c => c.P);
        var cover = cells.Where(c =>
        {
            var diff = homeSide ? c.Home - c.Away : c.Away - c.Home;
            return diff >= need;
        }).ToList();
        if (cover.Count > 0 && cover.Max(c => c.P) >= best * 0.55)
            return cover;
        return cells;
    }

    private (double Home, double Away) Lambdas(string? league, string home, string away, decimal? ouLine)
    {
        var (lgHome, lgAway) = LeagueAvg(league);
        var teamAvg = Math.Max(0.4, (lgHome + lgAway) / 2);
        var h = TeamRate(league, home, teamAvg);
        var a = TeamRate(league, away, teamAvg);
        var lh = lgHome * h.Attack * a.Defense;
        var la = lgAway * a.Attack * h.Defense;
        lh = Math.Clamp(lh, 0.15, 4.5);
        la = Math.Clamp(la, 0.15, 4.5);
        if (ouLine is > 0 && lh + la > 0)
        {
            var scale = Math.Clamp((double)ouLine.Value / (lh + la), 0.65, 1.55);
            lh *= scale;
            la *= scale;
        }
        return (lh, la);
    }

    private (double Home, double Away) LeagueAvg(string? league)
    {
        var name = league?.Trim();
        if (!string.IsNullOrEmpty(name) && _leagues.TryGetValue(name, out var lg) && lg.N >= 12)
        {
            var home = (lg.HomeSum + 30 * _globalHome) / (lg.N + 30);
            var away = (lg.AwaySum + 30 * _globalAway) / (lg.N + 30);
            return (home, away);
        }
        return (_globalHome, _globalAway);
    }

    /// <summary>
    /// 球队攻防倍率（1.0 = 目标联赛的平均水平）。先按联赛解析别名，再取跨联赛累计值。
    ///
    /// 2026-11-05 改：原来只在「本联赛」里找样本，欧战球队因此只剩 2 场可用，
    /// 明明在联赛里有上百场也帮不上忙。现在无论踢的是联赛、杯赛还是欧战，都累加到同一支队身上。
    /// </summary>
    private (double Attack, double Defense) TeamRate(string? league, string name, double teamAvg)
    {
        var resolved = Resolve(league, name);
        if (resolved is null) return (1, 1);

        // 先看本联赛自己的样本。够 5 场就用它——同联赛的攻防是同一个竞争环境里的数字，
        // 直接比较最可靠，别没事去掺别的赛事。
        if (_teams.TryGetValue(Key(league, resolved), out var own) && own.Games >= MinOwnGames)
        {
            var attack = (own.For + PriorGames * teamAvg) / (own.Games + PriorGames) / teamAvg;
            var defense = (own.Against + PriorGames * teamAvg) / (own.Games + PriorGames) / teamAvg;
            return (Math.Clamp(attack, 0.2, 4.5), Math.Clamp(defense, 0.2, 4.5));
        }

        if (!_crossLeague) return (1, 1);

        // 本联赛样本不足（典型：欧战球队在本赛事只有 2 场）才跨联赛兜底，
        // 用按联赛场均归一后的倍率。这里只是「没别的可用」时的替代，不是首选。
        // 2026-11-05 实测：无条件全跨联赛累计 Brier 0.6200，比只在同联赛取的 0.6062 更差——
        // 拿荷甲的进球率去估欧战表现，正是 11-04 朗斯 1:9 那类错误的来源。
        // 所以改成「够用就不跨，不够才跨」，覆盖率照样从 85.7% 提到 94.5%，但不牺牲精度。
        if (!_teamTotals.TryGetValue(resolved, out var total) || total.Games <= 0)
            return (1, 1);
        var att = (total.AttSum + PriorGames) / (total.Games + PriorGames);
        var def = (total.DefSum + PriorGames) / (total.Games + PriorGames);
        return (Math.Clamp(att, 0.2, 4.5), Math.Clamp(def, 0.2, 4.5));
    }

    private double FitRho(List<ScoreSample> samples)
    {
        var bestRho = -0.1;
        var best = double.NegativeInfinity;
        var step = samples.Count > 2500 ? samples.Count / 2500 : 1;
        for (var rho = -0.20; rho <= 0.05; rho += 0.01)
        {
            _ = rho;
            double ll = 0;
            var ok = true;
            for (var i = 0; i < samples.Count; i += step)
            {
                var s = samples[i];
                var (lh, la) = Lambdas(s.LeagueName, s.HomeTeam, s.AwayTeam, null);
                var tau = TauAt(s.HomeScore, s.AwayScore, lh, la, rho);
                if (tau <= 1e-6) { ok = false; break; }
                var p = tau * Poisson(Math.Min(s.HomeScore, MaxGoals), lh) * Poisson(Math.Min(s.AwayScore, MaxGoals), la);
                if (p <= 1e-12) p = 1e-12;
                ll += Math.Log(p);
            }
            if (ok && ll > best)
            {
                best = ll;
                bestRho = rho;
            }
        }
        return bestRho;
    }

    private double Tau(int x, int y, double lh, double la) => TauAt(x, y, lh, la, _rho);

    private static double TauAt(int x, int y, double lh, double la, double rho)
    {
        var t = (x, y) switch
        {
            (0, 0) => 1 - lh * la * rho,
            (1, 0) => 1 + la * rho,
            (0, 1) => 1 + lh * rho,
            (1, 1) => 1 - rho,
            _ => 1d
        };
        return t;
    }

    private static double Poisson(int k, double lambda)
    {
        if (k < 0) return 0;
        if (lambda < 1e-6) return k == 0 ? 1 : 0;
        var p = Math.Exp(-lambda);
        for (var i = 1; i <= k; i++) p *= lambda / i;
        return p;
    }

    private static void AddTeam(
        Dictionary<string, (double For, double Against, int Games)> teams,
        string league, string name, int scored, int conceded)
    {
        var key = Key(league, name);
        teams.TryGetValue(key, out var t);
        t.For += scored;
        t.Against += conceded;
        t.Games++;
        teams[key] = t;
    }

    private static void AddTotal(
        Dictionary<string, (double AttSum, double DefSum, int Games)> totals,
        string name, double attRatio, double defRatio)
    {
        if (name.Length == 0) return;
        totals.TryGetValue(name, out var t);
        t.AttSum += attRatio;
        t.DefSum += defRatio;
        t.Games++;
        totals[name] = t;
    }

    private static string Key(string? league, string name) =>
        (league?.Trim() ?? "") + "\n" + name.Trim();
}
