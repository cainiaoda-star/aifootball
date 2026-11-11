namespace AiFootBall.Core.Models;

/// <summary>
/// 联赛名对齐：竞彩官方名 → 数据源（FbMatch）里的名字。
///
/// 为什么必须有这张表（2026-11-08 实测，用户拍板修）：模型、联赛场均、半全场联赛基线
/// 全部按 <c>FbMatch.LeagueName</c> 分键，而竞彩受注卡用的是另一套叫法。名字对不上，
/// 整类联赛的球队就「模型从没训练过」——不是算不出，是查不到：
///   韩职 3 张卡 → 比分库 0 场（真名 K1联赛 1351 场）
///   日乙 1 张卡 → 0 场（日职乙 2304 场）
///   瑞超 4 张卡 → 0 场（瑞典超 1414 场）
///   美职足 2 张卡 → 0 场（美职联 3022 场）
///   芬兰超级联赛 2 张卡 → 0 场（芬超 1015 场）
///   亚冠 5 张卡 → 0 场（亚冠杯 744 场）
///   亚洲冠军乙级联赛 1 张卡 → 0 场（亚冠联2 275 场）
///   英联赛杯 1 张卡 → 0 场（英联杯）
///   日联赛杯 4 张卡 → 0 场（日联杯 407 场）—— 2026-11-11 用户确认同一赛事
/// 2026-11-08 当天 002 首尔FC vs 蔚山现代、005 江原FC vs 全北现代 就是这样整场被判「样本 0/0」的。
///
/// 入表标准（严格，别当成同义词字典往里塞）：
/// ① 只有「同一项赛事、两套叫法」才收；联赛层级不同（韩职 vs K2联赛、亚冠 vs 亚冠联2）不收。
/// ② 每条都要在库里核实过：用当天那几张卡的主客队名去目标联赛里能查到同一场比赛。
///    只靠字面相似（「瑞超」猜「瑞典超」）不入表，猜错会把整场挂到别的联赛均值上。
/// ③ 表里没有的名字原样返回，再由 <see cref="ScorelineModel.ResolveLeague"/> 用队名反查兜底，
///    所以新联赛出现时不用急着改这里。
/// </summary>
public static class LeagueNames
{
    /// <summary>竞彩名 → 数据源名。键用 OrdinalIgnoreCase，值必须是 FbMatch 里真实存在的写法。</summary>
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["韩职"] = "K1联赛",
        ["日乙"] = "日职乙",
        ["瑞超"] = "瑞典超",
        ["美职足"] = "美职联",
        ["芬兰超级联赛"] = "芬超",
        ["亚冠"] = "亚冠杯",
        ["亚洲冠军乙级联赛"] = "亚冠联2",
        ["英联赛杯"] = "英联杯",
        // 2026-11-11 用户确认同一赛事。竞彩卡写「日联赛杯」，FbMatch 里 407 场全叫「日联杯」。
        // 漏了这一条时，这个赛事的模型侧只能靠 ScorelineModel 的「两队共同出场联赛」反查，
        // 而那一步对这几支队会猜成「日职」（交手次数远多于杯赛）——等于拿联赛均值算杯赛。
        // 注意：只收「日联赛杯」这一种精确写法，不能模糊到「女日联杯」（那是另一项赛事）。
        ["日联赛杯"] = "日联杯",
    };

    /// <summary>
    /// 对齐后的联赛名。不在表里就返回原名（去掉首尾空白），不做任何模糊匹配——
    /// 模糊匹配猜错的代价是整场按错误联赛的场均出注，比查不到更糟。
    /// </summary>
    public static string Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var key = name.Trim();
        return Map.TryGetValue(key, out var mapped) ? mapped : key;
    }

    /// <summary>这个竞彩联赛名是否需要改名（诊断/页面提示用）。</summary>
    public static bool IsMapped(string? name)
        => !string.IsNullOrWhiteSpace(name) && Map.ContainsKey(name.Trim());

    /// <summary>全表快照，只读。给诊断输出、单测和运维核对用。</summary>
    public static IReadOnlyDictionary<string, string> All => Map;
}
