using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 「哪些场进了 Milk 引擎」的输入凭证（2026-11-13 加）。
///
/// 起因：验收要问「该日所有进入计算的场次，全档存档是否覆盖」，而当时没有任何地方
/// 记得住输入集，只能拿 `Recommendation` / `FbMilkGate` 去凑分母——
/// 前者是胜平负链路的展示结果（还经过 ForDisplay 过滤），后者只对走到方向闸的场次写行。
/// 11-13 实测三者数量完全不同：Recommendation 1 行、闸表 3 行、真正算过的 3 场。
///
/// 这组测试钉三件事：
///   1. 输入集在**方向闸之前**记录——被 MarketStrongReverse 整场撤掉的场次仍然在集里；
///   2. 禁推名单里的联赛**根本不进集**（11-13「国际赛」漏网就是这条没钉住）；
///   3. Inputs 与 ProbSnapshots 的场次集合一一对应（这是覆盖率的可验证形式）。
/// </summary>
public class MilkInputLedgerTests
{
    private static ScorelineModel Model()
    {
        var samples = new List<ScoreSample>();
        for (var i = 0; i < 20; i++)
        {
            samples.Add(new ScoreSample { LeagueName = "英超", HomeTeam = "主队", AwayTeam = "客" + i, HomeScore = 2, AwayScore = 1 });
            samples.Add(new ScoreSample { LeagueName = "英超", HomeTeam = "客队", AwayTeam = "主" + i, HomeScore = 1, AwayScore = 2 });
        }
        return ScorelineModel.Fit(samples);
    }

    private static MatchRecommendation Rec(long id, string league = "英超", string no = "周三001") => new()
    {
        Board = Boards.Jc,
        MatchNo = no,
        MatchId = id,
        LeagueName = league,
        HomeTeam = "主队",
        AwayTeam = "客队",
        MatchTime = DateTime.Today.AddHours(20),
        TotalLine = 2.5m
    };

    private static JcOddsHistoryPoint Pt(long mid, string play, string code, decimal odds) => new()
    {
        MatchId = mid,
        PlayType = play,
        OptionCode = code,
        OptionName = code,
        Odds = odds
    };

    /// <summary>一套能出票的正常赔率（主胜为主）。</summary>
    private static Dictionary<long, List<JcOddsHistoryPoint>> Odds(long mid) => new()
    {
        [mid] = new()
        {
            Pt(mid, JcPlayTypes.Spf, "3", 1.80m),
            Pt(mid, JcPlayTypes.Spf, "1", 3.60m),
            Pt(mid, JcPlayTypes.Spf, "0", 4.20m),
            Pt(mid, JcPlayTypes.Bq, "3-3", 4.20m),
            Pt(mid, JcPlayTypes.Bq, "1-1", 6.50m),
            Pt(mid, JcPlayTypes.Bq, "0-0", 9.00m),
            Pt(mid, JcPlayTypes.Jq, "2", 4.20m),
            Pt(mid, JcPlayTypes.Jq, "3", 4.00m),
            Pt(mid, JcPlayTypes.Bf, "2:1", 8.00m),
            Pt(mid, JcPlayTypes.Bf, "1:0", 9.00m)
        }
    };

    [Fact]
    public void Normal_Match_Is_Recorded_As_Input_With_Coverage()
    {
        var build = MilkPickEngine.BuildDetailed(new[] { Rec(1) }, Odds(1), Model());

        var input = Assert.Single(build.Inputs);
        Assert.Equal(1, input.MatchId);
        Assert.Equal("周三001", input.MatchNo);
        Assert.False(string.IsNullOrWhiteSpace(input.VersionTag));

        // 覆盖率：每个输入场次都必须至少有一行存档，且存档场次不得超出输入集。
        var snapMatches = build.ProbSnapshots.Select(s => s.MatchId).Distinct().ToList();
        Assert.Equal(input.MatchId, Assert.Single(snapMatches));
    }

    /// <summary>
    /// 11-13 的原样复现：竞彩把国家队热身赛叫「国际赛」，当时名单只认「友谊赛」，
    /// 于是这场进了引擎、出了注、还写了 48 行存档。现在它必须一场都进不来。
    /// </summary>
    [Fact]
    public void Friendly_League_Never_Enters_Inputs()
    {
        var build = MilkPickEngine.BuildDetailed(
            new[] { Rec(1, league: "国际赛") }, Odds(1), Model());

        Assert.Empty(build.Inputs);
        Assert.Empty(build.ProbSnapshots);
        Assert.Empty(build.Picks);
        Assert.Equal(0, build.Funnel.Distributed);
    }

    /// <summary>
    /// 输入集必须在方向闸**之前**记录：市场大热客胜、模型却指主胜的场次会被整场撤掉，
    /// 但它确实算过——事后要能回答「这场为什么没发」，输入与分布都必须留痕。
    /// </summary>
    [Fact]
    public void Match_Blocked_By_Market_Strong_Reverse_Is_Still_Recorded()
    {
        var mid = 2L;
        var odds = new Dictionary<long, List<JcOddsHistoryPoint>>
        {
            [mid] = new()
            {
                Pt(mid, JcPlayTypes.Spf, "3", 6.00m),
                Pt(mid, JcPlayTypes.Spf, "1", 3.60m),
                Pt(mid, JcPlayTypes.Spf, "0", 1.35m),
                Pt(mid, JcPlayTypes.Bq, "3-3", 4.20m),
                Pt(mid, JcPlayTypes.Bq, "1-1", 6.50m),
                Pt(mid, JcPlayTypes.Bq, "0-0", 9.00m),
                Pt(mid, JcPlayTypes.Jq, "2", 4.20m),
                Pt(mid, JcPlayTypes.Bf, "2:1", 8.00m)
            }
        };

        var build = MilkPickEngine.BuildDetailed(new[] { Rec(mid) }, odds, Model());

        // 整场被撤：不出注，但输入与存档都在。
        Assert.Empty(build.Picks);
        Assert.Equal(1, build.Funnel.BlockedByGate);
        Assert.Equal(mid, Assert.Single(build.Inputs).MatchId);
        Assert.Contains(build.ProbSnapshots, s => s.MatchId == mid);
    }

    [Fact]
    public void Coverage_Holds_For_Multiple_Matches()
    {
        var odds = new Dictionary<long, List<JcOddsHistoryPoint>>();
        foreach (var kv in Odds(1)) odds[kv.Key] = kv.Value;
        foreach (var kv in Odds(3)) odds[kv.Key] = kv.Value;

        var build = MilkPickEngine.BuildDetailed(new[] { Rec(1), Rec(3, no: "周三002") }, odds, Model());

        var inputIds = build.Inputs.Select(i => i.MatchId).OrderBy(x => x).ToList();
        var snapIds = build.ProbSnapshots.Select(s => s.MatchId).Distinct().OrderBy(x => x).ToList();
        Assert.Equal(inputIds, snapIds);
    }

    /// <summary>没有输入（受注 0 场）时范围必须为空——此时绝不允许去清榜。</summary>
    [Fact]
    public void SalesDates_Empty_When_No_Input()
    {
        Assert.Empty(RecommendationService.SalesDatesFor(null, new DateTime(2026, 11, 13)));
        Assert.Empty(RecommendationService.SalesDatesFor(new List<MatchRecommendation>(), new DateTime(2026, 11, 13)));
    }

    /// <summary>范围来自受注场次，不是「今天」：22:00 之后算出次日期的场次时，重写的是那两期。</summary>
    [Fact]
    public void SalesDates_Follow_Sales_Date_Not_Today()
    {
        var today = new DateTime(2026, 11, 13);
        var recs = new List<MatchRecommendation>
        {
            Rec(1, no: "周五001"),
            Rec(2, no: "周六001")
        };
        recs[0].MatchTime = today.AddHours(20);
        recs[1].MatchTime = today.AddDays(1).AddHours(13);

        var dates = RecommendationService.SalesDatesFor(recs, today);

        Assert.Contains(today, dates);
        Assert.Contains(today.AddDays(1), dates);
        Assert.Equal(2, dates.Count);
    }
}
