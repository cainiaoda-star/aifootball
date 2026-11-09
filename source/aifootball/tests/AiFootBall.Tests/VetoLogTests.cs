using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 否决台账。它要回答的问题是「被规则挡掉的场次，当时照打会怎样」，
/// 所以两个地方不能错：被挡的方向必须留下来（否决会把 RecResult 清空），
/// 没被挡的不能混进来（否则统计直接失真）。
/// </summary>
public sealed class VetoLogTests
{
    private static MatchRecommendation Rec(string no, char? result, char? proposed,
        int level = 0, decimal? hitProb = null, string? vetoCode = null, bool fallback = false)
        => new()
        {
            Board = Boards.Jc,
            MatchNo = no,
            MatchId = 1,
            MatchTime = DateTime.Today.AddHours(20),
            RecResult = result,
            ProposedResult = proposed,
            SignalLevel = level,
            HitProbability = hitProb,
            VetoCode = vetoCode,
            IsOddsFallback = fallback,
            PickOdds = 2.10m,
            SignalDesc = "测试"
        };

    [Fact]
    public void 被否决的场次也要记_方向取否决前的意图()
    {
        var raw = new[] { Rec("周二001", null, '3', vetoCode: "heat-extreme") };
        var published = Array.Empty<MatchRecommendation>();

        var rows = VetoLog.Build(Boards.Jc, raw, published, DateTime.Today);

        var row = Assert.Single(rows);
        Assert.Equal('3', row.Pick);
        Assert.Equal("heat-extreme", row.ReasonCode);
    }

    [Fact]
    public void 已经发出去的场次不进台账()
    {
        var raw = new[] { Rec("周二002", '3', '3', level: 2, hitProb: 61m) };
        var published = raw;

        Assert.Empty(VetoLog.Build(Boards.Jc, raw, published, DateTime.Today));
    }

    [Fact]
    public void 连方向都没有的场次不记_记了也没法判对错()
    {
        var raw = new[] { Rec("周二003", null, null, vetoCode: "no-signal") };

        Assert.Empty(VetoLog.Build(Boards.Jc, raw, Array.Empty<MatchRecommendation>(), DateTime.Today));
    }

    [Fact]
    public void 胜负彩全发_不存在否决()
    {
        var raw = new[] { Rec("001", '3', '3') };

        Assert.Empty(VetoLog.Build(Boards.Sfc, raw, Array.Empty<MatchRecommendation>(), DateTime.Today));
    }

    [Fact]
    public void 有方向但等级不够_归为弱信号()
    {
        var rec = Rec("周二004", '3', '3', level: 1, hitProb: 50m);

        Assert.Equal("low-signal", VetoLog.Classify(rec));
    }

    [Fact]
    public void 等级够但概率不到发布线_归为概率不足()
    {
        var rec = Rec("周二005", '3', '3', level: 2, hitProb: 52m);

        Assert.Equal("low-prob", VetoLog.Classify(rec));
    }

    [Fact]
    public void 等级概率都够却被挤掉_归为名额()
    {
        var rec = Rec("周二006", '3', '3', level: 2, hitProb: 63m);

        Assert.Equal("cap", VetoLog.Classify(rec));
    }

    [Fact]
    public void 纯赔率兜底单独归类()
    {
        var rec = Rec("周二007", '3', '3', level: 1, hitProb: 40m, fallback: true);

        Assert.Equal("odds-fallback", VetoLog.Classify(rec));
    }

    [Fact]
    public void 样本不足时不给结论()
    {
        var rows = new List<VetoStatRow>
        {
            new() { ReasonCode = "heat-extreme", Total = 30, Settled = 30, Hits = 25, HitRate = 83.3m, AvgOdds = 1.5m, Roi = 1.25m }
        };

        var report = VetoLog.Report(30, rows);

        Assert.Contains("样本不足", report.Verdict);
    }

    [Fact]
    public void 样本够了就明确说规则挡错了()
    {
        var rows = new List<VetoStatRow>
        {
            new() { ReasonCode = "heat-extreme", Total = 120, Settled = 120, Hits = 90, HitRate = 75m, AvgOdds = 1.6m, Roi = 1.20m }
        };

        var report = VetoLog.Report(30, rows);

        Assert.Contains("该放开", report.Verdict);
    }

    [Fact]
    public void 空台账不说挡得对也不说挡错了()
    {
        var report = VetoLog.Report(30, new List<VetoStatRow>());

        Assert.Equal(0, report.Total);
        Assert.Contains("还没有", report.Verdict);
    }
}
