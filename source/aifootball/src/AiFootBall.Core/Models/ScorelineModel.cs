namespace AiFootBall.Core.Models;

/// <summary>
/// 构建标识（2026-11-12 加，为了「这条注是哪份代码算的」可审计）。
///
/// 记的是 <c>ManifestModule.ModuleVersionId</c>（MVID）——.NET 每次编译都会换一个新的 GUID，
/// 所以同一个 dll 永远是同一个 MVID，改一行代码重新编译就变。这比手写版本号可靠：
/// 手写的版本号一定会忘记改，而 MVID 想忘也忘不掉。
///
/// 用法：拿注单上的 MVID 去比对 <c>deploy-package\app\api\AiFootBall.Core.dll</c> 的 MVID
/// （`powershell -c "[Reflection.Assembly]::LoadFile('...').ManifestModule.ModuleVersionId"`），
/// 就能确认线上跑的到底是哪次构建。
///
/// 为什么不直接用 git 提交号：程序集里没有这个信息，要写进去得在构建时注入
/// （SourceLink / InformationalVersion），而发布脚本不经过 git、只拷 dll。
/// 这里选「能自动拿到、且确实唯一」的那个字段，而不是「看起来更像版本号」的那个。
/// </summary>
public static class BuildInfo
{
    /// <summary>形如 <c>1.0.0.0+3f2a9c17</c>；取不到时返回 <c>unknown</c>，绝不让它抛异常。</summary>
    public static string Version { get; } = Compute();

    private static string Compute()
    {
        try
        {
            var asm = typeof(BuildInfo).Assembly;
            var v = asm.GetName().Version?.ToString() ?? "?";
            var mvid = asm.ManifestModule.ModuleVersionId.ToString("N");
            return mvid.Length >= 8 ? $"{v}+{mvid[..8]}" : $"{v}+{mvid}";
        }
        catch
        {
            // 单文件裁剪/反射受限等场景下取不到，宁可写 unknown 也不让出榜因为「盖章」失败。
            return "unknown";
        }
    }
}

/// <summary>完场比分，用来估计球队进球和失球。</summary>
public sealed class ScoreSample
{
    public string? LeagueName { get; init; }
    public string HomeTeam { get; init; } = "";
    public string AwayTeam { get; init; } = "";
    public int HomeScore { get; init; }
    public int AwayScore { get; init; }

    /// <summary>
    /// 开赛时间，仅供「按时间衰减加权」的拟合使用（2026-11-11 加）。
    /// 为 null 时该场按等权参与，与加这个字段之前的行为完全一致；
    /// <see cref="ScorelineModel.Fit"/> 在 <c>halfLifeDays</c> 为 null 时也根本不看它。
    /// </summary>
    public DateTime? MatchTime { get; init; }

    /// <summary>完场主键。拟合公式不读它；留痕用它标出当时那一行。</summary>
    public long MatchId { get; init; }
}

/// <summary>
/// 比分：球队进球、失球做攻防，Dixon-Coles 修正 0-0、1-0、0-1、1-1，再按大小球盘口缩放。
/// 主受让时不抬净胜球。主让时，只有净胜能盖住让球、且概率还够看，才用大比分。
/// </summary>
public sealed class ScorelineModel
{
    private const double PriorGames = 8;
    private const int MaxGoals = 6;

    /// <summary>
    /// 从开赛到「可以认定这场已经结束」的分钟数（2026-11-13 加）。
    ///
    /// 训练集原来只按「开赛时间 ≤ 截止时刻」过滤，这在历史回测里是泄漏：截止到 10-14 23:00 拟合，
    /// 一场 10-14 22:30 开赛的比赛当时还在踢，却被当成已知样本（因为**今天**库里已经有比分）。
    /// 线上因为 fitMoment = now 且 Status=3，几乎碰不到；回测天天碰。
    /// 判据只认「开赛时间 + 150 分钟 ≤ 截止时刻」，不认「现在有比分」——有比分不代表当时有。
    /// 150 分钟与完场判据（`Status==3 || MatchTime<=now-150min`）同源。
    ///
    /// ★ 这是**代理**，不是「当时已完赛」的严格证据（用户 2026-11-13 指出）：
    ///   · 加时、点球、伤停补时超长、延期、中断都可能超过 150 分钟；
    ///   · 库里唯一与「入库时刻」有关的字段是 `FbMatch.UpdatedAt`，但它是**最后一次写入**时间，
    ///     重采会把老场的 UpdatedAt 全部刷成当前批次（实测 3 个月内 89.9% 的行 UpdatedAt 晚于开赛 3 天），
    ///     所以它只能证明「此刻已知」，不能证明「当时未知」；
    ///   · 真实赛果抓取作业 `football` 每小时跑一次（近 2 天实测间隔 60 分钟），
    ///     所以即使踢满 110 分钟，结果也要等到下一个整点才入库 —— 150 分钟只够覆盖「踢完 + 不到一小时的抓取延迟」。
    /// 结论：这条闸把「明显还在踢」的样本挡住了，**残余泄漏风险仍然存在，不许宣称已彻底消除**。
    /// 严格口径要等有「首次入库时间」的历史留痕才能做，`fit-probe` 第八节在量化这个残余。
    /// </summary>
    public const int MatchDurationMinutes = 150;

    /// <summary>
    /// 低位比分相关系数 ρ，**固定值**（2026-11-13 用户拍板，选项 B）。
    ///
    /// 原来由 <c>FitRho</c> 按 count/2500 步长抽 2500 个子样本、网格搜索 −0.20..+0.05 得到。
    /// 这条路径的问题不是「不够准」，而是**不可复现**：同一份 178177 场、同一份代码，
    /// 取样顺序一变就落过 +0.010 / 0.000 / −0.010；加了 ORDER BY 之后稳定落在 −0.060。
    ///
    /// 11-13 用前向测试集（53,488 场，切点 2026-07-12，测试集不参与 ρ 选择）对比过：
    ///   ρ=+0.010（基准） 1X2 Brier 0.61661 / 0:0 校准 −0.11pp
    ///   ρ=−0.060（新值） 1X2 Brier 0.61733（+0.00072，95% 区间 [+0.00056,+0.00089] 显著变差）
    ///                    0:0 预测 6.99%（实际 6.36%，过度修正 +0.63pp）、1:1 高估 +1.14pp
    ///   ρ=0 的 1X2 与 +0.010 差 0.00006，属噪声级；比分 LogLoss 各档差 ≤0.0004。
    /// 所以「可复现」不等于「更准」。用户裁定：**先恢复基准 +0.010，不借这次审计修复选新参数**；
    /// ρ=0 与「全量似然重估」留作下一轮的独立验证（换一个没看过的时段，不重复使用这批测试集）。
    ///
    /// 因此 <c>Fit</c> 不再估计 ρ：同代码 + 同数据 + 同窗口 ⇒ 恒定版本戳。
    /// 改这个常量等于改模型，必须带回测与用户批准，不要在别处写第二个值。
    /// </summary>
    public const double DefaultRho = 0.010;

    /// <summary>本联赛样本达到这个数就不跨联赛取（与出注门槛 MinTeamGames 一致）。</summary>
    private const int MinOwnGames = 5;

    private readonly double _globalHome;
    private readonly double _globalAway;
    private readonly Dictionary<string, (double HomeSum, double AwaySum, double Weight, int N)> _leagues;
    private readonly Dictionary<string, (double For, double Against, double Weight, int Games)> _teams;

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
    private readonly Dictionary<string, (double AttSum, double DefSum, double Weight, int Games)> _teamTotals;

    /// <summary>全库队名集合（与 _teamTotals 同键）。联赛名对不上时用它兜底认队。</summary>
    private readonly HashSet<string> _globalNames;

    /// <summary>全场唯一命中才返回；空串 = 有歧义或没有，都按「认不出」处理，绝不猜。</summary>
    private readonly Dictionary<string, string> _globalCore;

    /// <summary>全局子序列匹配结果缓存（名字 → 全称，空串表示确认认不出）。全局匹配与联赛无关，可缓存。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _globalAlias = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _namesByLeague;
    private readonly double _rho;

    /// <summary>
    /// 队名 → 它踢过的联赛及场次。用于「联赛名对不上时按队名反查联赛」，
    /// 见 <see cref="ResolveLeague"/>。2026-11-08 加：竞彩叫「韩职」、数据源叫「K1联赛」，
    /// 名字一错整场被判「样本 0/0」，而这两支队在库里明明各有一百多场。
    /// </summary>
    private readonly Dictionary<string, List<(string League, int Games)>> _leaguesByTeam;

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
        Dictionary<string, (double HomeSum, double AwaySum, double Weight, int N)> leagues,
        Dictionary<string, (double For, double Against, double Weight, int Games)> teams,
        Dictionary<string, (double AttSum, double DefSum, double Weight, int Games)> teamTotals,
        Dictionary<string, string[]> namesByLeague,
        double rho,
        bool crossLeague = true,
        Dictionary<string, double>? leagueStrength = null,
        DateTime? fitFrom = null,
        DateTime? fitTo = null,
        int fitGames = 0)
    {
        Ready = ready;
        _globalHome = globalHome;
        _globalAway = globalAway;
        _leagues = leagues;
        _teams = teams;
        _teamTotals = teamTotals;
        _globalNames = new HashSet<string>(teamTotals.Keys, StringComparer.Ordinal);
        _globalCore = BuildCoreIndex(_globalNames);
        _leaguesByTeam = BuildTeamLeagueIndex(teams);
        _namesByLeague = namesByLeague;
        _rho = rho;
        _crossLeague = crossLeague;
        _leagueStrength = leagueStrength ?? new Dictionary<string, double>(StringComparer.Ordinal);
        FitFrom = fitFrom;
        FitTo = fitTo;
        FitGames = fitGames;
    }

    public static ScorelineModel Empty { get; } = new(false, 1.45, 1.15, new(), new(), new(), new(), -0.1);

    public bool Ready { get; }

    /// <summary>实际参与拟合的最早一场开赛时间（2026-11-12 加，审计用）。null = 样本无时间戳。</summary>
    public DateTime? FitFrom { get; }

    /// <summary>实际参与拟合的最晚一场开赛时间。见 <see cref="VersionTag"/>。</summary>
    public DateTime? FitTo { get; }

    /// <summary>实际参与拟合的场次（已按 asOf 截断之后，不再是「传进来多少」）。</summary>
    public int FitGames { get; }

    /// <summary>
    /// 这次拟合实际用过的样本和已算字典。空模型没有。
    /// 字典是构造时那几份的副本，不是事后重算。
    /// </summary>
    public FitModelTrace? Trace { get; private set; }

    /// <summary>Dixon-Coles 的 ρ（低比分相关性修正）。审计时它一变，概率就会变。</summary>
    public double Rho => _rho;

    /// <summary>
    /// 模型版本快照（2026-11-12 加）。形如
    /// <c>dc1|2025-05-13..2026-10-28|n=169989|rho=-0.055|1.0.0.0+3f2a9c17</c>。
    ///
    /// 存在的理由：第三方校验方案点名「没有 model_version，事后无法回答这条注是哪个模型算的」。
    /// 这里只包含**模型自身**的东西（窗口、样本、ρ、构建），混合权重与玩法口令由
    /// <c>MilkPickEngine.PickVersion</c> 拼在后面——Core 不能反向引用 Sources。
    /// </summary>
    public string VersionTag => Ready
        ? $"dc1|{FitFrom?.ToString("yyyy-MM-dd") ?? "?"}..{FitTo?.ToString("yyyy-MM-dd") ?? "?"}"
          + $"|n={FitGames}|rho={_rho:0.000}|{BuildInfo.Version}"
        : $"dc-none|{BuildInfo.Version}";

    /// <summary>
    /// 把竞彩的联赛名对齐到训练集里的联赛名，返回「该用哪个联赛的场均和名单来算这场」。
    ///
    /// 三级判据（2026-11-08 加，起因：竞彩「韩职」vs 数据源「K1联赛」，整类联赛永不出注；
    /// 2026-11-11 补上 <paramref name="minGames"/> 这一层，起因见 <see cref="Enough"/>）：
    /// 1. <see cref="LeagueNames"/> 人工表命中、且该联赛**两队都有够量样本** → 用它；
    /// 2. 表里的映射在训练集里查不到（或两队在那没量）、而原名查得到 → 尊重原名；
    /// 3. 都不成立 → 用两队各自的「踢过哪些联赛」取交集，挑两队共同出场最多的那个联赛。
    ///    这一步是自愈的：以后新联赛再出现两套叫法，不用改表也能认出来。
    /// 任何一步都不成立就原样返回，行为和改动之前一致（宁可丢场，也不挂到别的联赛均值上）。
    /// </summary>
    public string ResolveLeague(string? league, string? home, string? away, int minGames = 0)
    {
        var raw = league?.Trim() ?? "";
        var mapped = LeagueNames.Resolve(raw);
        if (HasLeague(mapped) && Enough(mapped, home, away, minGames)) return mapped;
        if (mapped != raw && HasLeague(raw) && Enough(raw, home, away, minGames)) return raw;

        var byTeams = LeagueByTeams(raw, home, away);
        if (byTeams is not null && Enough(byTeams, home, away, minGames)) return byTeams;

        // 都够不上量：仍优先返回别名，让下游的「样本不足」诊断如实报出来，
        // 而不是在这里悄悄换成另一个联赛。
        return HasLeague(mapped) ? mapped : byTeams ?? mapped;
    }

    /// <summary>
    /// 这个联赛里两队是否都有 <paramref name="minGames"/> 场样本。
    ///
    /// 只看「联赛有样本」（<see cref="HasLeague"/>）是不够的，2026-11-11 用真实事故换来的：
    /// 加了「日联赛杯→日联杯」的别名后，解析结果从「日职」变成「日联杯」，
    /// 而近 18 个月训练窗口里 **鹿岛鹿角在日联杯 0 场、神户胜利船 2 场、町田泽维亚 2 场**
    /// （日职各 56~59 场）。于是 周二/周三 两场整场被判定「模型不认识」/「样本不足」，
    /// 直接从这个榜上消失——别名把一场能按联赛均值算的比赛，变成了算不出。
    ///
    /// 联赛级有样本 ≠ 这场算得动。别名只有在「两队都认识且有量」时才允许盖过队名反查；
    /// 不满足就退到 <see cref="LeagueByTeams"/>，让它落到两队真正交手多的那个联赛。
    /// 队名缺失时不做判断（返回 true），保持「没有队名信息就不额外否决」的旧行为。
    /// </summary>
    private bool Enough(string? league, string? home, string? away, int minGames)
    {
        if (minGames <= 0) return true;
        if (string.IsNullOrWhiteSpace(home) || string.IsNullOrWhiteSpace(away)) return true;
        return TeamGames(league, home) >= minGames && TeamGames(league, away) >= minGames;
    }

    /// <summary>训练集里是否存在这个联赛（有任何一场样本即可，不要求达到联赛均值门槛）。</summary>
    public bool HasLeague(string? league)
        => !string.IsNullOrWhiteSpace(league)
           && _leagues.TryGetValue(league.Trim(), out var x) && x.N > 0;

    /// <summary>
    /// 按两队共同出场的联赛反查。评分 = min(主队在该联赛的场次, 客队在该联赛的场次)，
    /// 同分取联赛总样本更大的那个（跨联赛杯赛里，两队可能既在各自联赛又在杯赛同场过，
    /// 取共同出场多的那个才是真正在打的那项赛事）。交集为空返回 null。
    /// </summary>
    private string? LeagueByTeams(string league, string? home, string? away)
    {
        if (string.IsNullOrWhiteSpace(home) || string.IsNullOrWhiteSpace(away)) return null;
        var h = Resolve(league, home);
        var a = Resolve(league, away);
        if (h is null || a is null) return null;
        if (!_leaguesByTeam.TryGetValue(h, out var hls) || !_leaguesByTeam.TryGetValue(a, out var als))
            return null;

        string? best = null;
        var bestScore = 0;
        var bestLeagueN = 0;
        foreach (var (lg, hg) in hls)
        {
            var ag = 0;
            foreach (var (lg2, g2) in als)
            {
                if (lg2 != lg) continue;
                ag = g2;
                break;
            }
            if (ag <= 0) continue;
            var score = Math.Min(hg, ag);
            var leagueN = _leagues.TryGetValue(lg, out var x) ? x.N : 0;
            if (score < bestScore) continue;
            if (score == bestScore && leagueN <= bestLeagueN) continue;
            best = lg;
            bestScore = score;
            bestLeagueN = leagueN;
        }
        return best;
    }

    /// <summary>
    /// 核心名索引：去掉赞助商/俱乐部英文标记后的名字 → 唯一对应的库内写法（有歧义则空串）。
    /// 只服务 <see cref="Resolve"/> 的「查询名比库内名更长」这一种形态（2026-11-14 加）。
    /// </summary>
    private static Dictionary<string, string> BuildCoreIndex(IEnumerable<string> names)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var n in names)
        {
            var core = TeamNames.CoreKey(n);
            if (core.Length == 0) continue;
            index[core] = index.TryGetValue(core, out var existing) && existing.Length > 0
                ? string.Empty          // 撞了两个以上 → 记成歧义，永远不返回
                : n;
        }
        return index;
    }

    /// <summary>队名 → 该队踢过的联赛（含场次），供 <see cref="LeagueByTeams"/> 使用。</summary>
    private static Dictionary<string, List<(string League, int Games)>> BuildTeamLeagueIndex(
        Dictionary<string, (double For, double Against, double Weight, int Games)> teams)
    {
        var map = new Dictionary<string, List<(string League, int Games)>>(StringComparer.Ordinal);
        foreach (var (key, val) in teams)
        {
            var i = key.IndexOf('\n');
            if (i <= 0) continue;
            var lg = key[..i];
            var nm = key[(i + 1)..];
            if (!map.TryGetValue(nm, out var list)) map[nm] = list = new List<(string, int)>(2);
            list.Add((lg, val.Games));
        }
        return map;
    }

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
        if (TeamAliases.Map.TryGetValue(n, out var known) && _globalNames.Contains(known)) return known;

        // 联赛内子序列：竞彩缩的是中间字（布斯巴达 ← 布拉格斯巴达）
        if (_namesByLeague.TryGetValue(lg, out var all))
        {
            var (hit, ambiguous) = MatchSubsequence(n, all);
            if (hit is not null) return hit;
            // 同联赛里撞出两支以上，说明这批名单本身有歧义，交给调用方丢场，不跨联赛乱猜
            if (ambiguous) return null;
        }

        // 全局子序列：联赛名对不上时的最后一道兜底，仍要求全库唯一命中。
        //
        // 2026-11-14 在它之前插一步「核心名」比对（1419319 大宫松鼠RB 事件）：
        // 子序列只允许**查询名比库内名短**（`cand.Length <= shortName.Length` 直接跳过），
        // 竞彩给队名加赞助商后缀时查询名反而更长，前面几步全不命中 → 整场丢失。
        // 核心名只做一件事：去掉 ASCII 标记后一字不差，且必须先在全库唯一。
        //
        // **全库唯一 ≠ 身份正确**（2026-11-14 第八轮，用户点名）：库里「不来梅」一线队没有样本时，
        // 唯一核心名是两个梯队「JFV不来梅U19」，于是「不来梅」被配成 U19；
        // 「谢周三」同样被配成「谢周三U21」。唯一性只保证不猜，不保证是同一支队。
        // 所以这里再加两道闸：
        //   ① 梯队层次必须**相等**（`TeamNames.SameIdentity`）：查询名不带青年/二队/女足标记时
        //      绝不接受梯队候选；查询名自己带标记时也只认同一个层次（谢周三U21 ≠ 谢周三U19）。
        //      实测能正确匹配到青年队靠的是上面的精确/别名步，不靠这条兜底。
        //   ② 候选不得引入新的 ASCII 记号（`AsciiMarks ⊆`）。这一条**只在这条路**上加：
        //      核心名比对绕过了下面的歧义判定，如果直接放行 `米兰 → AC米兰`，
        //      就把一个歧义问题悄悄变成答案。走上子序列那条路时歧义会被正常判出来。
        var qCore = TeamNames.CoreKey(n);
        if (qCore.Length > 0
            && _globalCore.TryGetValue(qCore, out var coreName)
            && coreName.Length > 0
            && TeamNames.SameIdentity(n, coreName)
            && TeamNames.AsciiMarks(coreName).IsSubsetOf(TeamNames.AsciiMarks(n)))
            return coreName;

        if (!_globalAlias.TryGetValue(n, out var cached))
        {
            cached = MatchSubsequence(n, _globalNames).Hit ?? "";
            _globalAlias.TryAdd(n, cached);
        }
        return cached.Length == 0 ? null : cached;
    }

    /// <summary>
    /// 人工确认过的队名别名表已搬到 <see cref="TeamAliases"/>（2026-11-08）。
    /// 搬家原因：原来只在「精确匹配失败」时才查这张表，而「索尔纳」「AIK索尔纳」两个写法都在库里有样本时
    /// 精确匹配先命中，别名永远不生效——模型把一支队当成两支。现在 Fit 会先归一，样本才真正合并。
    /// </summary>

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
        var shortCore = TeamNames.CoreKey(shortName);
        var identityConflict = false;
        foreach (var cand in candidates)
        {
            if (cand.Length <= shortName.Length) continue;
            if (cand.Length - shortName.Length > 4) continue;
            if (!shortIsReserve && IsReserveOrYouth(cand)) continue;

            // 同一核心名下的候选，身份由**同一个判据**决定（2026-11-14 第十轮）。
            // 核心名那一步（Resolve 里的 _globalCore）已经用 SameIdentity 拒过一遍，
            // 这里若不再判，`帕丘卡` 被拒之后会被子序列重新捡成 `帕丘卡III`、
            // `BKMA` 被捡成 `BKMA II` —— 「解析拒绝、下一条链路又接受」，
            // 与第九轮计数层的老问题同源。
            //
            // 只拦**梯队层次不同**的候选：`埃因霍温 → PSV埃因霍温`（库内 147 场）这类
            // 只多一个俱乐部前缀的写法，层次相同，仍然走子序列正常匹配。
            //
            // ★ 冲突要升级成「歧义」，不能简单跳过：`米兰` 同时对上 `国际米兰`（核心名不同）
            // 与 `AC米兰`（核心名相同、多了 AC 这个俱乐部记号）。跳过 AC 会得到
            // 「唯一命中 = 国际米兰」，把一个歧义问题悄悄变成错误答案。宁可判「认不出」。
            if (shortCore.Length >= 2
                && string.Equals(TeamNames.CoreKey(cand), shortCore, StringComparison.Ordinal)
                && !TeamNames.SameIdentity(shortName, cand))
            {
                identityConflict = true;
                continue;
            }

            if (!IsSubsequence(shortName, cand)) continue;
            hits.Add(cand);
        }
        if (identityConflict) return (null, true);
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

    /// <summary>
    /// 青年队 / 后备队 / B 队 / 女足这类附属队。它们和一线队是两套数据，不能混。
    ///
    /// 2026-11-14 第九轮：判定搬到 <see cref="TeamNames"/>，与历史样本计数共用一份实现。
    /// 之前解析在这里判、`GetRecentTeamGamesAsync` 只按核心名归并，两边口径不一致——
    /// 「不来梅」（解析层已拒绝 U19）在计数层仍把 `JFV不来梅U19` 的场次算进一线队。
    /// </summary>
    private static bool IsReserveOrYouth(string name) => TeamNames.IsReserveOrYouth(name);

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
    ///
    /// <paramref name="halfLifeDays"/>（2026-11-11 加）：按时间衰减加权，w = 0.5^(年龄/半衰期)。
    /// **实测不采用，默认 null（等权）**。工具 `weight-probe`，3558 场前向测试集：
    ///   等权 0.6030 / 540 天 0.6021 / 365 天 0.6020 / 240 天 0.6022 /
    ///   180 天 0.6026 / 120 天 0.6039 / 90 天 0.6055 / 60 天 0.6085
    /// 最好一档（365 天）只改善 0.0010，门槛 0.002 ⇒ **未过**。而且 180 天以下**单调变差**：
    /// 有效样本从 17 万降到 3.5 万，样本量的损失超过了「越近越重要」的收益。
    /// 这条轨道到这里为止——继续缩窗口不是在提高精度，只是在赌小样本。
    /// 参数保留是为了让 `weight-probe` 可重跑，不是留一个待调的门。
    /// </summary>
    public static ScorelineModel Fit(
        IReadOnlyList<ScoreSample> samples, bool crossLeague = false,
        Dictionary<string, double>? leagueStrength = null,
        double? halfLifeDays = null, DateTime? asOf = null)
    {
        // 留痕行与拟合行一起滤。拟合行的数值与原来的 Select 相同，并带上 MatchId（公式不读它）。
        var paired = samples.Where(s =>
            !string.IsNullOrWhiteSpace(s.HomeTeam)
            && !string.IsNullOrWhiteSpace(s.AwayTeam)
            && s.HomeScore >= 0 && s.AwayScore >= 0
            && s.HomeScore <= 12 && s.AwayScore <= 12)
            // 队名先归一，再统计（2026-11-08）。同一支球队在库里有两个写法时（索尔纳 / AIK索尔纳），
            // 不归一就会各算各的攻防样本：一支 176 场的瑞典超球队被拆成 55 + 121 场，
            // 两队样本都被噪声主导，λ 会随机抖动。归一放在这里而不是 Resolve 里，是因为
            // Resolve 只能解决「查询时找不到」，解决不了「训练时被拆开」。
            .Select(s => (
                trace: FitSampleRow.From(s),
                fit: new ScoreSample
                {
                    MatchId = s.MatchId,
                    LeagueName = s.LeagueName,
                    HomeTeam = TeamAliases.Canonical(s.HomeTeam),
                    AwayTeam = TeamAliases.Canonical(s.AwayTeam),
                    HomeScore = s.HomeScore,
                    AwayScore = s.AwayScore,
                    MatchTime = s.MatchTime
                }))
            .ToList();
        if (paired.Count < 30) return Empty;
        var usable = paired.Select(p => p.fit).ToList();

        // ---- 截止日过滤（2026-11-12 加，第三方校验方案 3.1 点名的「无未来信息」）----
        //
        // 改之前 asOf 只当衰减原点用，**不过滤样本**：传一批含未来场次的样本进来，
        // 那些场次会以权重 1.0 参与拟合（days ≤ 0 时 WeightOf 返回 1.0）。
        // 线上没暴露是因为 SQL 自己带了 Status=3 + 窗口；但那不是模型在兜底，
        // 是调用方碰巧守规矩——只要哪天有人拿全量样本 + asOf 跑 A/B，测出来的就是泄漏后的成绩。
        // 所以把这条约束放进 Fit 内部：给了 asOf 就是「只用这一天及以前的比赛」。
        //
        // 没有 MatchTime 的样本在给了 asOf 时一律排除：无法证明它在截止日之前，
        // 「不知道」不能当「安全」处理。线上不传 asOf，所以这条分支对线上零影响。
        //
        // ★ 2026-11-13 二次收紧：截止时刻之前**还必须已经踢完**。
        // 只判 `t <= cut` 时，回测到 10-14 23:00 会把 10-14 22:30 开赛（当时才踢了半小时）的场次算进训练集，
        // 依据是「今天库里有比分」——那是用未来的结果训练过去的模型。现在要求 `t + 150min <= cut`。
        // 线上 fitMoment = now 时只会剔掉刚完场的几场（本来 Status=3 也未必已写入），代价可忽略。
        if (asOf is DateTime cut)
        {
            var usableCut = cut.AddMinutes(-MatchDurationMinutes);
            paired = paired
                .Where(p => p.fit.MatchTime is DateTime t && t <= usableCut)
                .ToList();
            usable = paired.Select(p => p.fit).ToList();
        }
        if (usable.Count < 30) return Empty;

        // 拟合窗口快照（审计用）：过滤之后真实的起止与场次，不是调用方传进来的范围。
        DateTime? fitFrom = null, fitTo = null;
        foreach (var s in usable)
        {
            if (s.MatchTime is not DateTime t2) continue;
            if (fitFrom is null || t2 < fitFrom) fitFrom = t2;
            if (fitTo is null || t2 > fitTo) fitTo = t2;
        }
        var fitGames = usable.Count;

        // ---- 时间衰减（2026-11-11 加）----
        // w = 0.5 ^ (距原点天数 / 半衰期)。halfLifeDays 为 null 时全部 w = 1，
        // 与加这个参数之前逐位等价，所以线上默认路径零风险。
        //
        // 原点刻意不用 DateTime.Now：回测按「切分时刻」前向切分，若用 now，
        // 整段训练历史会被一起压扁成同一个权重，衰减到底有没有用就看不出来。
        // 因此默认取「训练集里最晚的一场」，即模型被要求预测的那一天。
        var anchor = asOf;
        if (anchor is null)
        {
            DateTime? latest = null;
            foreach (var s in usable)
                if (s.MatchTime is DateTime t && (latest is null || t > latest)) latest = t;
            anchor = latest;
        }
        var decaying = halfLifeDays is double hlDays && hlDays > 0 && anchor is DateTime;
        var anchorAt = anchor ?? default;

        double WeightOf(ScoreSample s)
        {
            if (!decaying || s.MatchTime is not DateTime t) return 1.0;
            var days = (anchorAt - t).TotalDays;
            return days <= 0 ? 1.0 : Math.Pow(0.5, days / halfLifeDays!.Value);
        }

        double homeSum = 0, awaySum = 0, totalWeight = 0;
        var leagues = new Dictionary<string, (double HomeSum, double AwaySum, double Weight, int N)>(StringComparer.Ordinal);
        var teams = new Dictionary<string, (double For, double Against, double Weight, int Games)>(StringComparer.Ordinal);
        foreach (var s in usable)
        {
            var w = WeightOf(s);
            totalWeight += w;
            homeSum += s.HomeScore * w;
            awaySum += s.AwayScore * w;
            var league = s.LeagueName?.Trim() ?? "";
            if (league.Length > 0)
            {
                leagues.TryGetValue(league, out var lg);
                lg.HomeSum += s.HomeScore * w;
                lg.AwaySum += s.AwayScore * w;
                lg.Weight += w;
                lg.N++;
                leagues[league] = lg;
            }
            AddTeam(teams, league, s.HomeTeam, s.HomeScore, s.AwayScore, w);
            AddTeam(teams, league, s.AwayTeam, s.AwayScore, s.HomeScore, w);
        }

        var n = usable.Count;
        var globalHome = homeSum / totalWeight;
        var globalAway = awaySum / totalWeight;

        // 联赛场均。门槛仍用**真实场次** N（够不够样本是另一回事），
        // 分母用加权有效样本 Weight——等权时 Weight == N，与旧口径逐位相同。
        double LgHome(string lg) => leagues.TryGetValue(lg, out var x) && x.N >= 12
            ? (x.HomeSum + 30 * globalHome) / (x.Weight + 30) : globalHome;
        double LgAway(string lg) => leagues.TryGetValue(lg, out var x) && x.N >= 12
            ? (x.AwaySum + 30 * globalAway) / (x.Weight + 30) : globalAway;

        var strength = leagueStrength ?? new Dictionary<string, double>(StringComparer.Ordinal);

        // 第二遍：按本场所属联赛的场均归一，累加的是「倍率」而不是进球数。
        // 这样荷甲 1.6 球/场的队和意甲 1.2 球/场的队才可比，跨联赛累计不会把联赛风格当成球队实力。
        // 方案 1（2026-11-05）：有联赛强度系数时，把场均再除以强度——强联赛的进球更有含金量，
        // 归一后才能跨联赛比。荷甲场均 2.8 球（进攻性强）不等于荷甲队比意甲队强。
        var teamTotals = new Dictionary<string, (double AttSum, double DefSum, double Weight, int Games)>(StringComparer.Ordinal);
        foreach (var s in usable)
        {
            var w = WeightOf(s);
            var lg = s.LeagueName?.Trim() ?? "";
            var sHome = strength.TryGetValue(lg, out var sh) ? sh : 1.0;
            var sAway = strength.TryGetValue(lg, out var sa) ? sa : 1.0;
            var hAvg = Math.Max(0.4, LgHome(lg) * sHome);
            var aAvg = Math.Max(0.4, LgAway(lg) * sAway);
            AddTotal(teamTotals, s.HomeTeam.Trim(), s.HomeScore / hAvg, s.AwayScore / aAvg, w);
            AddTotal(teamTotals, s.AwayTeam.Trim(), s.AwayScore / aAvg, s.HomeScore / hAvg, w);
        }

        var namesByLeague = BuildNameIndex(teams);
        // ρ 固定（2026-11-13 用户拍板选项 B，见 DefaultRho 的注释）：不再调 FitRho 估计。
        // 官方理由是可复现，不是「更准」——拟合出的 −0.060 在前向测试集上 1X2 显著更差。
        var model = new ScorelineModel(true, globalHome, globalAway, leagues, teams, teamTotals, namesByLeague,
            DefaultRho, crossLeague, strength, fitFrom, fitTo, fitGames);
        model.Trace = FitModelTrace.Capture(
            paired.Select(p => p.trace).ToList(),
            leagues, teams, teamTotals, globalHome, globalAway,
            DefaultRho, halfLifeDays, crossLeague);
        return model;
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
        // 队名先归一（2026-11-08）：同一队两个写法会被判成两支不同球队，各自算「主联赛」，
        // 跨联赛交手里就会出现「AIK索尔纳 vs 索尔纳」这种自己打自己的场次，强度解会被带偏。
        samples = samples
            .Select(s => new ScoreSample
            {
                LeagueName = s.LeagueName,
                HomeTeam = TeamAliases.Canonical(s.HomeTeam),
                AwayTeam = TeamAliases.Canonical(s.AwayTeam),
                HomeScore = s.HomeScore,
                AwayScore = s.AwayScore
            })
            .ToList();

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
        Dictionary<string, (double For, double Against, double Weight, int Games)> teams)
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
    /// 全比分概率分布（0..6 × 0..6，Dixon-Coles 修正，含 tau，未按全支撑归一）。
    /// 1 减这 49 格只是「任一队 ≥7 球」的尾部，不是总进球 7+，也不是胜其它：
    /// 总进球 7+ 还要加上网格内 h+a≥7；胜其它只收该方向没单列的比分。
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

    /// <summary>
    /// 只换 ρ、其余参数完全不变的副本（2026-11-13 加）。
    ///
    /// 存在的理由：回测要回答「ρ=a 与 ρ=b 哪个预测更准」，必须固定 λ、只动 ρ 各跑一遍。
    /// 线上 <see cref="Fit"/> 已固定 <see cref="DefaultRho"/>，所以这里只服务回测台
    /// （`rho-study` 的候选档、`fit-probe` 的对照），**没有第二条线上路径**。
    /// 下一轮验证 ρ=0 / 全量似然重估时会走这条入口，届时必须用没看过的时段。
    /// </summary>
    public ScorelineModel WithRho(double rho)
        => Ready
            ? new ScorelineModel(true, _globalHome, _globalAway, _leagues, _teams, _teamTotals,
                _namesByLeague, rho, _crossLeague, _leagueStrength, FitFrom, FitTo, FitGames)
            : Empty;

    /// <summary>
    /// 这批样本在本 ρ 下的平均逐场对数似然（2026-11-13 加，供 ρ 的全量似然重估）。
    ///
    /// 与回测台的关系：`rho-study` 用它做「全量似然重估」这一档——不抽子样本，全量遍历。
    /// 只给回测算一次，不进线上路径。
    /// 返回的 Games 是真正参与计算的场次，不是传入数——调用方要能看出剔掉了多少。
    /// </summary>
    public (double MeanLogLikelihood, int Games) Score(IReadOnlyList<ScoreSample> samples)
    {
        if (!Ready || samples.Count == 0) return (double.NegativeInfinity, 0);
        double sum = 0;
        var n = 0;
        foreach (var s in samples)
        {
            if (string.IsNullOrWhiteSpace(s.HomeTeam) || string.IsNullOrWhiteSpace(s.AwayTeam)) continue;
            if (s.HomeScore < 0 || s.AwayScore < 0 || s.HomeScore > 12 || s.AwayScore > 12) continue;
            var (lh, la) = Lambdas(s.LeagueName, s.HomeTeam, s.AwayTeam, null);
            var tau = Tau(s.HomeScore, s.AwayScore, lh, la);
            if (tau <= 1e-6) continue;
            var p = tau * Poisson(Math.Min(s.HomeScore, MaxGoals), lh) * Poisson(Math.Min(s.AwayScore, MaxGoals), la);
            if (p <= 1e-12) p = 1e-12;
            sum += Math.Log(p);
            n++;
        }
        return n == 0 ? (double.NegativeInfinity, 0) : (sum / n, n);
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
            var home = (lg.HomeSum + 30 * _globalHome) / (lg.Weight + 30);
            var away = (lg.AwaySum + 30 * _globalAway) / (lg.Weight + 30);
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
            var attack = (own.For + PriorGames * teamAvg) / (own.Weight + PriorGames) / teamAvg;
            var defense = (own.Against + PriorGames * teamAvg) / (own.Weight + PriorGames) / teamAvg;
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
        var att = (total.AttSum + PriorGames) / (total.Weight + PriorGames);
        var def = (total.DefSum + PriorGames) / (total.Weight + PriorGames);
        return (Math.Clamp(att, 0.2, 4.5), Math.Clamp(def, 0.2, 4.5));
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
        Dictionary<string, (double For, double Against, double Weight, int Games)> teams,
        string league, string name, int scored, int conceded, double weight)
    {
        var key = Key(league, name);
        teams.TryGetValue(key, out var t);
        t.For += scored * weight;
        t.Against += conceded * weight;
        t.Weight += weight;
        // Games 保持**真实场次**：它是 MinTeamGames / TeamGames 的门槛依据，
        // 「有没有足够样本」不该被衰减权重改写，否则老球队会凭空跌破门槛、
        // 把「模型变准了」和「覆盖率掉了」两件事混在一起，分不清哪个在起作用。
        t.Games++;
        teams[key] = t;
    }

    private static void AddTotal(
        Dictionary<string, (double AttSum, double DefSum, double Weight, int Games)> totals,
        string name, double attRatio, double defRatio, double weight)
    {
        if (name.Length == 0) return;
        totals.TryGetValue(name, out var t);
        t.AttSum += attRatio * weight;
        t.DefSum += defRatio * weight;
        t.Weight += weight;
        t.Games++;
        totals[name] = t;
    }

    private static string Key(string? league, string name) =>
        (league?.Trim() ?? "") + "\n" + name.Trim();
}
