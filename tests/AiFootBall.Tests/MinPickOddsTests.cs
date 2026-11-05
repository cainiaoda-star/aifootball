using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 2026-11-04 用户指示：低赔一律不推，宁可高赔、宁可空缺。
/// 边界钉死在 1.79/1.80：这条线挡住的是 11-03 那张 1.47/1.53 的 2串1。
/// </summary>
public class MinPickOddsTests
{
    [Fact]
    public void 组票咽喉_水位低于1_80_概率再高也不成腿()
    {
        // 概率 60%，水位 1.79：差一分钱也不行
        Assert.Null(RecommendationSelection.JcCoverTicket.TryHandicapLeg('3', 60m, 1.79m, 3.5m, 4.0m));
        // 水位正好 1.80：过
        Assert.NotNull(RecommendationSelection.JcCoverTicket.TryHandicapLeg('3', 60m, 1.80m, 3.5m, 4.0m));
        // 概率 45 垃圾线 + 水位 2.10：高赔低概率，组
        var leg = RecommendationSelection.JcCoverTicket.TryHandicapLeg('3', 45m, 2.10m, 3.5m, 4.0m);
        Assert.NotNull(leg);
        Assert.Equal(2.10m, leg!.Value.Odds);
    }

    [Fact]
    public void 组票咽喉_概率低于45_赔率再高也是垃圾()
    {
        Assert.Null(RecommendationSelection.JcCoverTicket.TryHandicapLeg('3', 44.9m, 3.50m, 4.0m, 4.5m));
        Assert.Null(RecommendationSelection.JcCoverTicket.TryHandicapLeg('3', null, 3.50m, 4.0m, 4.5m));
        Assert.NotNull(RecommendationSelection.JcCoverTicket.TryHandicapLeg('3', 45m, 3.50m, 4.0m, 4.5m));
    }

    [Fact]
    public void 让球表_已知低赔不展示_未知水位保留()
    {
        var low = Rec("周二001");
        low.HandicapOdds = 1.60m;

        var high = Rec("周二002");
        high.HandicapOdds = 1.85m;

        var unknown = Rec("周二003");   // 没抓到让球价，数据缺口不是低赔

        var shown = RecommendationSelection.ForJcHandicap(new[] { low, high, unknown });

        Assert.Equal(new[] { "周二002", "周二003" }, shown.Select(x => x.MatchNo).ToArray());
    }

    [Fact]
    public void 胜平负列表_已知低赔不推荐()
    {
        var low = Rec("周二001");
        low.RecHandicap = null;
        low.PickOdds = 1.35m;           // 深让低水「稳胆」，正是要清掉的东西

        var high = Rec("周二002");
        high.RecHandicap = null;
        high.PickOdds = 2.20m;

        var unknown = Rec("周二003");   // 没跑过模型、水位未知，保留
        unknown.RecHandicap = null;
        unknown.PickOdds = null;

        var shown = RecommendationSelection.ForJcCalculated(new[] { low, high, unknown });

        Assert.Equal(new[] { "周二002", "周二003" }, shown.Select(x => x.MatchNo).ToArray());
    }

    [Fact]
    public void 边界_1_79与1_80一念之差()
    {
        var below = Rec("周二001");
        below.HandicapOdds = 1.79m;
        var atLine = Rec("周二002");
        atLine.HandicapOdds = 1.80m;

        var shown = RecommendationSelection.ForJcHandicap(new[] { below, atLine });

        Assert.Single(shown);
        Assert.Equal("周二002", shown[0].MatchNo);
    }

    private static MatchRecommendation Rec(string no) => new()
    {
        Board = Boards.Jc,
        MatchNo = no,
        MatchId = no.GetHashCode(),
        RecResult = '3',
        SignalLevel = 2,
        Confidence = 70m,
        Handicap = -0.5m,
        RecHandicap = '3',
        HandicapProbability = 50m
    };
}
