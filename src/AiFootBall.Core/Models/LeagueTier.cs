namespace AiFootBall.Core.Models;

/// <summary>
/// 北单联赛分级。竞彩复盘不看这张表，北单只把 A、B、C 写进每天的复盘。
/// 名字按受注卡上的写法，竞彩和北单同一联赛会用不同简称。
/// </summary>
public static class LeagueTier
{
    public const string A = "A";
    public const string B = "B";
    public const string C = "C";

    static readonly Dictionary<string, string> Map = Build();

    public static string? Of(string? leagueName)
    {
        if (string.IsNullOrWhiteSpace(leagueName)) return null;
        var name = leagueName.Trim();
        return Map.TryGetValue(name, out var tier) ? tier : null;
    }

    static Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        Add(map, A,
            "英超", "西甲", "德甲", "意甲", "法甲",
            "荷甲", "葡超", "比甲", "俄超", "土超", "苏超",
            "巴甲", "巴西甲", "阿甲", "阿职联", "美职", "美职足", "墨超",
            "日职", "日职联", "J1联赛", "韩职", "韩K联", "K1联赛", "中超", "澳超",
            "欧冠", "欧冠杯", "欧联", "欧罗巴", "欧联杯", "欧会杯", "欧协联", "亚冠");
        Add(map, B,
            "英冠", "德乙", "意乙", "西乙", "法乙",
            "荷乙", "葡甲", "比乙", "巴乙", "巴西乙", "阿乙",
            "日乙", "J2联赛", "韩K2", "K2联赛",
            "瑞典超", "瑞超", "挪超", "丹超", "奥甲", "瑞士超", "希腊超", "希腊甲",
            "波兰甲", "捷克甲", "捷甲", "罗甲", "乌超", "塞超", "以超", "沙特联", "智利甲",
            "英甲", "德丙",
            "英联赛杯", "德国杯", "联赛杯", "社区盾", "国王杯", "意大利杯", "法国杯", "荷兰杯", "日联赛杯");
        Add(map, C,
            "英乙", "瑞典甲", "瑞甲", "挪甲", "丹甲", "奥乙", "瑞士甲", "苏冠",
            "爱超", "爱甲", "芬超", "芬兰超级联赛", "波兰乙", "中甲",
            "泰超", "埃及超", "南非超", "苏联赛杯", "亚洲冠军乙级联赛",
            "智利乙", "哥伦甲", "哥伦比亚甲", "秘鲁甲", "巴丙", "阿丙", "中乙");
        return map;
    }

    static void Add(Dictionary<string, string> map, string tier, params string[] names)
    {
        foreach (var name in names)
            map[name] = tier;
    }
}

/// <summary>复盘时从 FbMatch 读出的比分。未完场也留着。</summary>
public sealed class ReviewScore
{
    public long MatchId { get; set; }
    public int? HomeScore { get; set; }
    public int? AwayScore { get; set; }
    public int? HalfHomeScore { get; set; }
    public int? HalfAwayScore { get; set; }
    public int Status { get; set; }

    public bool Finished => Status == 3 && HomeScore is not null && AwayScore is not null;
}
