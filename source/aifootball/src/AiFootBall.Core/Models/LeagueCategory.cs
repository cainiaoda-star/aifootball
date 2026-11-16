namespace AiFootBall.Core.Models;

/// <summary>
/// 赛事类型分类（2026-11-06 加在 MilkPickEngine 里，2026-11-13 迁到 Core 并收口）。
///
/// 冷门是「赛事类型放大的」：杯赛反转档合计 15.1% > 联赛 11.1% > 友谊赛 2.7%（39.5 万场实测）。
/// 消费点两处，必须是同一份判定，否则同一天的两道闸会互相打架：
///   · <see cref="NoPickLeagues"/> —— 稳胆仍跳过友谊赛整类；竞彩大奶入漏斗前不再挡整场；
///   · MilkPickEngine —— 友谊赛不发半全场反转档、杯赛低水热门触发 CupHotFade。整场仍进入「能中」。
///
/// 为什么必须抽出来（2026-11-13 实据）：竞彩受注卡把国家队热身赛叫 **「国际赛」**，
/// 而 FbMatch 一律记「友谊赛」。名单里当时只有「友谊赛」，于是拿卡名做判定的路径整类漏网：
/// 周五001 新加坡 vs 巴拉圭（`BettingCard.LeagueName='国际赛'`，FbMatch 同场 `'友谊赛'`）
/// 因此进了引擎并出了注，`LeagueCategory("国际赛")` 也落到 "LEAGUE"，
/// 连「友谊赛不发反转档」这条规则都一起失效。库内该叫法 3 场，3/3 映射 FbMatch 友谊赛。
///
/// ★ 正式国际赛事优先判定：名称里带世界杯/欧洲杯/欧国联/世预…的一律 CUP，
/// 哪怕同时出现「国际赛」字样（「国际赛-世界杯预选赛」这种拼接叫法）。顺序不能反，
/// 反了就会把正式比赛当成热身赛砍掉——那是负 alpha，而且线上不报错，只是榜面悄悄少一块。
/// </summary>
public static class LeagueCategory
{
    /// <summary>杯赛/赛会制。</summary>
    public const string Cup = "CUP";

    /// <summary>友谊赛/热身赛（球队动机不受约束，反转档仅 2.7%）。</summary>
    public const string Friendly = "FRIENDLY";

    /// <summary>常规联赛。</summary>
    public const string League = "LEAGUE";

    /// <summary>正式国际赛事标记：命中即按杯赛处理，不参与友谊赛判定。</summary>
    private static readonly string[] MajorTournaments =
    {
        "世界杯", "欧洲杯", "美洲杯", "亚洲杯", "非洲杯", "欧国联", "世预", "联合会杯"
    };

    /// <summary>友谊赛标记。**「国际赛」是竞彩对国家队热身赛的叫法**，不是在说「国际比赛」。</summary>
    private static readonly string[] FriendlyMarkers =
    {
        "友谊", "热身", "国际赛"
    };

    /// <summary>杯赛/赛会制标记（与迁移前 MilkPickEngine.LeagueCategory 逐条一致）。</summary>
    private static readonly string[] CupMarkers =
    {
        "杯", "欧冠", "欧罗巴", "欧会", "欧协", "亚冠", "欧国联", "世预",
        "世界杯", "欧洲杯", "美洲杯", "亚洲杯", "非洲杯", "超级杯", "联赛杯",
        "足总", "国王杯", "总统杯", "冠军", "资格", "附加"
    };

    /// <summary>该联赛（按竞彩卡名或 FbMatch 名）属于哪一类。空名按联赛处理。</summary>
    public static string Of(string? leagueName)
    {
        if (string.IsNullOrWhiteSpace(leagueName)) return League;
        var lg = leagueName.Trim();

        if (Contains(lg, MajorTournaments)) return Cup;
        if (Contains(lg, FriendlyMarkers)) return Friendly;
        if (Contains(lg, CupMarkers)) return Cup;
        return League;
    }

    /// <summary>是否友谊赛类（决定整类不出注、不发反转档）。</summary>
    public static bool IsFriendly(string? leagueName) => Of(leagueName) == Friendly;

    /// <summary>是否正式国际赛事（世界杯/欧洲杯/欧国联/世预…）。</summary>
    public static bool IsMajorTournament(string? leagueName)
        => !string.IsNullOrWhiteSpace(leagueName) && Contains(leagueName.Trim(), MajorTournaments);

    private static bool Contains(string lg, string[] markers)
    {
        foreach (var m in markers)
            if (lg.Contains(m, StringComparison.Ordinal)) return true;
        return false;
    }
}
