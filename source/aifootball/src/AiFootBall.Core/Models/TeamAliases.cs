namespace AiFootBall.Core.Models;

/// <summary>
/// 队名别名表：同一个俱乐部在中超/竞彩/500 源里出现两套写法时，统一到一个「规范名」。
///
/// 为什么需要独立的类（2026-11-08）：原来这张表私藏在 <see cref="ScorelineModel"/> 里，
/// 只在「精确匹配失败」时才查。于是「索尔纳」和「AIK索尔纳」两个写法**都在库里有样本**时，
/// 精确匹配先命中，别名永远不生效——模型把一支队当成两支，各自只有 55 / 205 场。
/// 那支队叫 AIK 索尔纳（瑞典超），教练组、球探、赔率商都用同一个队，只是译名带了「AIK」。
///
/// 现在的用法：
///   · <see cref="Canonical"/> —— 拟合前把样本队名归一，攻防样本才会真正合并；
///   · <see cref="FormsOf"/>   —— 查库（近期战绩、俱乐部画像）时把写法全部展开，别漏历史；
///   · <see cref="Map"/>       —— <see cref="ScorelineModel.Resolve"/> 的兜底查表。
///
/// **入表标准**（和联赛名表一致）：只收人工确认过的同一支球队。字面相似一律不入表——
/// 猜错会把两支不同球队的攻防样本混在一起，比不合并危险得多。
/// </summary>
public static class TeamAliases
{
    /// <summary>别名 → 规范名。规范名必须是 FbMatch 里真实存在、且样本更多的那个写法。</summary>
    public static readonly IReadOnlyDictionary<string, string> Map =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["圣吉联合"] = "圣吉罗斯",
            ["圣吉罗斯联合"] = "圣吉罗斯",

            // 竞彩简称与数据源全称**用字不同**、子序列规则推不出来的（2026-11-08 加）。
            // 「巴萨」对「巴塞罗那」：巴命中、萨不在其中（塞≠萨），子序列直接失败；
            // 退到全局子序列又会撞上巴萨克圣星/巴萨拉兵库/巴萨诺/蒙巴萨 等一串，
            // 判成歧义 → 整场 unknown-team → 不出注。
            // 代价：西甲最热的一场（马竞 vs 巴萨，客胜 51%、赔率 1.63）被静默丢掉。
            // 这类「改字不缩写」的译名差异规则永远推不出来，只能人工收。
            ["巴萨"] = "巴塞罗那",

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

            // 2026-11-08 联赛名对齐时顺带补的两条：联赛名一修，场上就轮到队名了。
            // 洛城银河 vs 洛杉矶银河：「城」不在目标名里，子序列推不出来（洛杉矶银河 = 洛,杉,矶,银,河）。
            // 库奥皮奥 vs 库普斯：KuPS 的两个中文译名，任何一个字符串规则都推不出来。
            ["洛城银河"] = "洛杉矶银河",
            ["库奥皮奥"] = "库普斯",

            // 2026-11-08 用户拍板：同一支 AIK 索尔纳（瑞典超）。
            // 库里「AIK索尔纳」205 场（2021 起，最新 2026-11-08）、「索尔纳」55 场（2024-07~2025-08
            // 那段窗口的源换过写法）。规范名取样本更多的「AIK索尔纳」，合并后瑞典超 176 场。
            // 不合并的代价是模型把一支队当两支，各自攻防估计都偏薄（3 场门槛过得去，但噪声大）。
            ["索尔纳"] = "AIK索尔纳",
        };

    /// <summary>规范名 → 全部写法（含自身）。查库时用它展开，避免只查到一半历史。</summary>
    private static readonly Dictionary<string, string[]> Forms = BuildForms();

    private static Dictionary<string, string[]> BuildForms()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (alias, canonical) in Map)
        {
            if (!map.TryGetValue(canonical, out var list)) map[canonical] = list = new List<string>(2);
            if (!list.Contains(alias, StringComparer.Ordinal)) list.Add(alias);
        }

        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (canonical, aliases) in map)
        {
            var all = new List<string>(aliases.Count + 1) { canonical };
            all.AddRange(aliases);
            result[canonical] = all.ToArray();
        }
        return result;
    }

    /// <summary>归一到一个写法。不在表里的原样返回（去空白），保证调用方不必判空表。</summary>
    public static string Canonical(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var n = name.Trim();

        // 两跳保护：万一以后有人写了 A→B、B→C，也能收敛到 C，不会来回震荡
        for (var hop = 0; hop < 3 && Map.TryGetValue(n, out var next); hop++)
        {
            if (string.Equals(next, n, StringComparison.Ordinal)) break;
            n = next;
        }
        return n;
    }

    /// <summary>
    /// 这个写法在库里可能出现的所有名字（含规范名）。不在表里的返回单元素数组。
    /// 仓储拿它拼 SQL IN，才能把换写法之前的比赛也捞进来。
    /// </summary>
    public static IReadOnlyList<string> FormsOf(string? name)
    {
        var canonical = Canonical(name);
        if (canonical.Length == 0) return Array.Empty<string>();
        return Forms.TryGetValue(canonical, out var forms) ? forms : new[] { canonical };
    }

    /// <summary>是否登记在案（诊断/单测用）。</summary>
    public static bool IsKnown(string? name)
        => !string.IsNullOrWhiteSpace(name) && Map.ContainsKey(name.Trim());
}
