using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 北单「单一联赛集中度」上限（2026-11-10 崩盘后加）。
///
/// 事故：11-10 期（26114）可用 20 场 = 英锦赛 14 + 日联杯 4 + 巴西乙 1 + 捷克杯 1。
/// 按让球概率降序取前 10，**10 注全部落在英锦赛**，日联杯一场没进。
/// 当晚 10 注只中 2（巴尼特 4:2 让胜、普利茅斯 2:1 让平），回报 0.669，净亏 33%。
/// 前四天单一联赛最多 2 注，回报 1.471 / 1.025 / 0.913 / 0.900。
///
/// 英锦赛是形态最特殊的英格兰赛事（538 场：平局 17.3%、场均 3.13 球；
/// 英甲英乙 25~27% 平局、2.5 球），模型按全库进球分布算出的让球概率在这里天然失真。
/// 集中 10 注 = 把一个赛事的形态风险放大 10 倍。
/// </summary>
public class BdLeagueCapTests
{
    private static BdSpfPick Pick(string league, decimal prob, string no)
        => new()
        {
            MatchId = no.GetHashCode(),
            MatchNo = no,
            LeagueName = league,
            HomeTeam = "H" + no,
            AwayTeam = "A" + no,
            Handicap = 0m,
            HandicapSide = '3',
            HandicapSideName = "让胜",
            HandicapProbability = prob,
            HandicapOdds = 2.5m,
            Odds = 2.5m,
            Kind = "steady"
        };

    [Fact]
    public void 十一零事故复现_十四场同联赛只留三注()
    {
        // 11-10 真实让球概率（降序）：50.6 / 46.3 / 45.9 / 44.0 / 43.9 / 42.8 / 42.6 / 40.4 / 39.7 / ...
        var ordered = new[]
        {
            Pick("英锦赛", 50.6m, "2"),
            Pick("英锦赛", 46.3m, "7"),
            Pick("英锦赛", 45.9m, "8"),
            Pick("英锦赛", 44.0m, "6"),
            Pick("英锦赛", 43.9m, "12"),
            Pick("英锦赛", 42.8m, "4"),
            Pick("英锦赛", 42.6m, "11"),
            Pick("英锦赛", 40.4m, "1"),
            Pick("英锦赛", 39.7m, "3"),
            Pick("英锦赛", 38.5m, "5"),
            Pick("英锦赛", 37.2m, "9"),
            Pick("英锦赛", 36.1m, "10"),
            Pick("英锦赛", 35.0m, "13"),
            Pick("英锦赛", 34.2m, "14")
        };

        var kept = BdSpfPickService.CapPerLeague(ordered, 10, 3);

        Assert.Equal(3, kept.Count);
        Assert.All(kept, p => Assert.Equal("英锦赛", p.LeagueName));
        // 保序：留下的必须是概率最高的三注
        Assert.Equal(new[] { "2", "7", "8" }, kept.Select(p => p.MatchNo));
    }

    [Fact]
    public void 有空位时其他联赛能补进来()
    {
        // 英锦赛占满 3 个格后，日联杯的注应当补上 —— 这正是 11-10 缺失的行为。
        var ordered = new[]
        {
            Pick("英锦赛", 50.6m, "1"),
            Pick("英锦赛", 46.3m, "2"),
            Pick("英锦赛", 45.9m, "3"),
            Pick("英锦赛", 44.0m, "4"),
            Pick("日联杯", 41.0m, "5"),
            Pick("日联杯", 40.0m, "6"),
            Pick("英锦赛", 39.0m, "7"),
            Pick("巴西乙", 38.0m, "8")
        };

        var kept = BdSpfPickService.CapPerLeague(ordered, 10, 3);

        Assert.Equal(6, kept.Count);
        Assert.Equal(3, kept.Count(p => p.LeagueName == "英锦赛"));
        Assert.Equal(2, kept.Count(p => p.LeagueName == "日联杯"));
        Assert.Equal(1, kept.Count(p => p.LeagueName == "巴西乙"));
        // 顺序仍按传入的概率降序
        Assert.Equal(new[] { "1", "2", "3", "5", "6", "8" }, kept.Select(p => p.MatchNo));
    }

    [Fact]
    public void 分散期次不被误伤()
    {
        // 11-06 实况：14 注、10 个联赛、单一联赛最多 2 注 → 上限是 3，一注都不该被砍。
        var ordered = new[]
        {
            Pick("英超", 48.0m, "1"),
            Pick("英超", 47.0m, "2"),
            Pick("西甲", 46.0m, "3"),
            Pick("意甲", 45.0m, "4"),
            Pick("德乙", 44.0m, "5"),
            Pick("法乙", 43.0m, "6"),
            Pick("荷乙", 42.0m, "7"),
            Pick("苏超", 41.0m, "8"),
            Pick("比乙", 40.0m, "9"),
            Pick("丹超", 39.0m, "10")
        };

        var kept = BdSpfPickService.CapPerLeague(ordered, 10, 3);

        Assert.Equal(10, kept.Count);
        Assert.Equal(ordered.Select(p => p.MatchNo), kept.Select(p => p.MatchNo));
    }

    [Fact]
    public void 总数上限仍然生效()
    {
        // 每个联赛各 1 注，共 20 注 → 只能取 10
        var ordered = Enumerable.Range(1, 20)
            .Select(i => Pick("L" + i, 50m - i, i.ToString()))
            .ToArray();

        var kept = BdSpfPickService.CapPerLeague(ordered, 10, 3);

        Assert.Equal(10, kept.Count);
        Assert.Equal("1", kept[0].MatchNo);
        Assert.Equal("10", kept[^1].MatchNo);
    }

    [Fact]
    public void 联赛名为空的不占用别人的额度()
    {
        // 脏数据（联赛名缺失）不能因为都叫 null 就被当成同一联赛掐掉。
        var ordered = new[]
        {
            Pick(null!, 50m, "1"),
            Pick("  ", 49m, "2"),
            Pick("", 48m, "3"),
            Pick("英锦赛", 47m, "4"),
            Pick("英锦赛", 46m, "5")
        };

        var kept = BdSpfPickService.CapPerLeague(ordered, 10, 3);

        Assert.Equal(5, kept.Count);
    }

    [Fact]
    public void 两个联赛的期次仍能出满十注()
    {
        // 上限 3 时，两个联赛最多 6 注 —— 这是刻意的：北单串关宁缺毋滥。
        // 若将来要放宽，必须同时改 MaxPerLeague 与本断言，不允许悄悄回退。
        var ordered = Enumerable.Range(1, 10)
            .SelectMany(i => new[] { Pick("英锦赛", 50m - i, "A" + i), Pick("日联杯", 49m - i, "B" + i) })
            .OrderByDescending(p => p.HandicapProbability)
            .ToArray();

        var kept = BdSpfPickService.CapPerLeague(ordered, 10, 3);

        Assert.Equal(6, kept.Count);
        Assert.Equal(3, kept.Count(p => p.LeagueName == "英锦赛"));
        Assert.Equal(3, kept.Count(p => p.LeagueName == "日联杯"));
    }

    [Fact]
    public void 上限常数被钉死()
    {
        Assert.Equal(3, BdSpfPickService.MaxPerLeague);
        Assert.Equal(10, BdSpfPickService.MaxSerialPicks);
    }
}
