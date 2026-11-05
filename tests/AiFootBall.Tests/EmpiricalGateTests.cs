using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 经验命中率门槛的样本分级：12 场的点估计区间约 ±28 点，
/// 不能拿它否决 944 场验证出来的 55 门槛。
/// </summary>
public class EmpiricalGateTests
{
    private static MatchRecommendation Rec() => new()
    {
        Board = Boards.Jc,
        MatchNo = "周二001",
        MatchId = 1,
        RecResult = '3',
        SignalLevel = 2,
        Confidence = 70m,
        IsOddsFallback = false,
        SignalDesc = "",
        Handicap = -0.5m,
        RecHandicap = '3',
        HandicapProbability = 60m
    };

    [Fact]
    public void SmallSample_12_DoesNotDemote()
    {
        var rec = Rec();

        HitProbabilityService.ApplyEmpiricalGate(rec, hits: 4, total: 12, pickOdds: 2.10m);

        Assert.Equal(2, rec.SignalLevel);
        Assert.Equal(70m, rec.Confidence);
        Assert.DoesNotContain("降权不进精选", rec.SignalDesc);
    }

    [Fact]
    public void MediumSample_30_MovesConfidenceOnly()
    {
        var rec = Rec();

        HitProbabilityService.ApplyEmpiricalGate(rec, hits: 10, total: 30, pickOdds: 2.10m);

        Assert.Equal(2, rec.SignalLevel);
        Assert.True(rec.Confidence < 70m);
        Assert.DoesNotContain("降权不进精选", rec.SignalDesc);
    }

    [Fact]
    public void LargeSample_60_LowRate_Demotes()
    {
        var rec = Rec();

        HitProbabilityService.ApplyEmpiricalGate(rec, hits: 20, total: 60, pickOdds: 2.10m);

        Assert.Equal(1, rec.SignalLevel);
        Assert.Contains("降权不进精选", rec.SignalDesc);
    }

    [Fact]
    public void LargeSample_60_GoodRate_KeepsLevel()
    {
        var rec = Rec();

        HitProbabilityService.ApplyEmpiricalGate(rec, hits: 40, total: 60, pickOdds: 2.10m);

        Assert.Equal(2, rec.SignalLevel);
        Assert.DoesNotContain("降权不进精选", rec.SignalDesc);
    }

    [Fact]
    public void SuperLowOdds_IsNeverGated()
    {
        var rec = Rec();

        HitProbabilityService.ApplyEmpiricalGate(rec, hits: 10, total: 80, pickOdds: 1.25m);

        Assert.Equal(2, rec.SignalLevel);
    }

    [Fact]
    public void TicketLegs_KeepHighOdds_LowProbability_And_Drop_Junk()
    {
        // 2026-11-04 起票腿赔率 ≥1.80 当第一道闸，概率降到 45% 垃圾线
        var high = Rec();
        high.MatchNo = "周二002";
        high.HandicapProbability = 48m;       // 低概率但过垃圾线，水位未知的保留
        high.HandicapOdds = 1.95m;

        var lowOdds = Rec();
        lowOdds.MatchNo = "周二003";
        lowOdds.HandicapProbability = 61m;    // 概率再高，低赔也不组票
        lowOdds.HandicapOdds = 1.47m;

        var junk = Rec();
        junk.MatchNo = "周二004";
        junk.HandicapProbability = 44m;       // 垃圾线以下
        junk.HandicapOdds = 2.30m;

        var legs = RecommendationSelection.ForJcTicketLegs(new[] { high, lowOdds, junk });

        Assert.Single(legs);
        Assert.Equal("周二002", legs[0].MatchNo);
    }

    [Fact]
    public void TicketLegs_EmptyWhenNothingQualifies()
    {
        var weak = Rec();
        weak.HandicapProbability = 44m;       // 52% 在新规则下是合格腿，用垃圾线以下验证空

        Assert.Empty(RecommendationSelection.ForJcTicketLegs(new[] { weak }));
    }
}
