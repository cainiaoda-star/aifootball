namespace AiFootBall.Core.Models;

/// <summary>
/// 完全不出注的联赛名单（2026-11-09 加，起因：用户问「欧国联能不能推」）。
///
/// ★ 与 <see cref="NoParlayLeagues"/> 不是一回事，不要合并：
///   · NoParlayLeagues = 可以单关发，只是不拿去组串（澳超）。
///   · NoPickLeagues   = 连单关都不发（国家队/友谊赛/青年赛）。
///
/// 欧国联的证据（`league-probe 2026-10-01`，只读回放，同一套 ScorelineModel + 竞彩真实九档）：
///   · 测试期 52 场完赛，15 场有完整半全场赔率，可评估 14 场；
///   · **Top-1 命中率：模型 14.3% vs 市场 42.9%** —— 模型挑最高概率档，只有市场的三分之一；
///   · 按线上出票口径（概率 ≥12%、赔率 4~15、每场一注）**13 注 0 中，平注回报 0.000**；
///   · 其中「模型概率 ≥ 市场去水概率」的 8 注（唯一可能过基线闸的那批）也是 0 中；
///   · 校准：模型给 ≥25% 的档实际只中 8.3%（高估 21 点），给 0-0(负负) 26.5% 而市场只给 5.9%
///     —— 模型对国家队比赛的顺风大档系统性高估 2~4 倍。
///   · 52/52 场都能过 MinTeamGames（队均 15.1 场），**门槛挡不住它**，只能靠这张名单。
/// 另外 `FbLeagueHalfFullBase` 里欧国联没有任何记录（只有球会友谊/友谊赛），
/// 也就是说连联赛分层基线都没有，出注等于用一个没有兜底的值去赌。
///
/// 样本只有 13 注，统计学上不足以断言「欧国联永远不能推」；但足以说明
/// **按现在这套模型推它会持续亏**，而用户要的是提高命中率，不是填满榜面。
///
/// ★ 2026-11-13 补「国际赛」：竞彩受注卡把国家队热身赛叫「国际赛」，FbMatch 记「友谊赛」，
/// 名单当时只认「友谊赛」→ 整类漏网。库内该叫法 3 场，3/3 映射 FbMatch 友谊赛，
/// 周五001 新加坡 vs 巴拉圭 就是这样进了引擎并出了注（本地 20:00 开赛）。
/// 所以除了加名字，还改成与 <see cref="LeagueCategory"/> 同源判定——名单里的字面值和
/// 引擎对「友谊赛」的理解从此只有一份，不会再出现「同一天两道闸各判一套」。
/// 判定顺序上正式国际赛事（世界杯/欧洲杯/欧国联/世预…）优先，不会被当成热身赛砍掉。
///
/// 恢复某类联赛：从 <see cref="Names"/> 删掉即可，不要在调用点硬编码。
/// </summary>
public static class NoPickLeagues
{
    /// <summary>完全不出注的联赛名（含子串匹配，「欧国联」也拦「女欧国联」）。</summary>
    /// <remarks>
    /// ★「国际赛」故意**不放进这张表**，靠 <see cref="LeagueCategory.IsFriendly"/> 生效。
    /// 放进来的话，子串匹配会把「国际赛-世界杯预选赛」这种拼接叫法一起砍掉——
    /// 正式国际赛事被当成热身赛，正是本名单最怕的「多拦一类」（测试钉住了这条）。
    /// 判定只能有一处：正式赛事优先，其次才是友谊赛类。
    /// </remarks>
    private static readonly string[] Names =
    {
        "欧国联",     // 实测 0/13，模型对顺风档高估 2~4 倍，且无联赛分层基线
        "友谊赛",     // 含「国际友谊赛」；球队动机不受约束，样本再大也不稳定
        "球会友谊",   // 季前热身，主力轮换不可预测
        "U17", "U19", "U20", "U21", "U23"   // 青年赛：竞彩 9 月以来零开售，属于纯盲区
    };

    /// <summary>该类联赛是否完全不出注。</summary>
    public static bool IsExcluded(string? leagueName)
    {
        if (string.IsNullOrWhiteSpace(leagueName)) return false;
        var lg = leagueName.Trim();

        // 与引擎同源：友谊赛类（含「国际赛」「热身赛」以及以后新增的叫法）一律不出。
        if (LeagueCategory.IsFriendly(lg)) return true;

        foreach (var n in Names)
        {
            if (lg.Equals(n, StringComparison.Ordinal)) return true;
            if (lg.Contains(n, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
