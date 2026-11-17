using AiFootBall.Core.Models;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 存档三列概率的单位/归一校验（2026-11-14）。
///
/// 为什么值得单写一组：11-14 08:55 那轮 BF 逐场合计是 1.00 / 99.95 / 30.69，
/// 其余四个玩法都是 100.00 —— 一列用了 0~1，一列用了 0~100。这种错**不会让任何东西报错**，
/// 只会让按它算的汇总悄悄差 100 倍。所以判据是「合计必须 100」而不是「看起来像百分数」。
/// </summary>
public class MilkProbScaleTests
{
    private static MilkProbSnapshot Row(long mid, string play, string code,
        decimal? pure, decimal? market = null, decimal? blend = null)
        => new()
        {
            MatchId = mid, PlayType = play, OptionCode = code,
            ModelPctPure = pure, MarketPct = market, BlendedPct = blend
        };

    [Fact]
    public void Percentages_SummingTo100_Pass()
    {
        var rows = new[]
        {
            Row(1, JcPlayTypes.Spf, "3", 45.0m, 44.0m, 44.7m),
            Row(1, JcPlayTypes.Spf, "1", 27.0m, 28.0m, 27.3m),
            Row(1, JcPlayTypes.Spf, "0", 28.0m, 28.0m, 28.0m)
        };

        Assert.Empty(MilkProbScale.Verify(rows));
    }

    [Fact]
    public void FractionsWrittenIntoPercentColumn_AreCaught()
    {
        // BF 31 档：低赔几档 + 「其它」余量，合计 1.00 而不是 100。
        var rows = new List<MilkProbSnapshot>
        {
            Row(1, JcPlayTypes.Bf, "1:0", 0.079m, 8.1m, 8.3m),
            Row(1, JcPlayTypes.Bf, "2:1", 0.096m, 9.9m, 9.8m),
            Row(1, JcPlayTypes.Bf, "胜其它", 0.122m, 12.4m, 12.3m),
            Row(1, JcPlayTypes.Bf, "负其它", 0.700m, 69.6m, 69.6m)
        };

        var issues = MilkProbScale.Verify(rows);

        Assert.Contains(issues, i => i.Kind == "prob-not-normalized" && i.Key.Contains("ModelPctPure"));
        Assert.Contains(issues, i => i.Kind == "unit-mismatch");
        // BlendedPct 这一列在本例里是百分数，合计约 100 —— 不该被一起判错。
        Assert.DoesNotContain(issues, i => i.Key.Contains("BlendedPct"));
    }

    [Fact]
    public void OnePlayBroken_DoesNotAffectOtherPlays()
    {
        var rows = new[]
        {
            Row(1, JcPlayTypes.Bf, "1:0", 0.079m, 8.1m, 8.3m),
            Row(1, JcPlayTypes.Bq, "3-3", 36.0m, 35.0m, 35.7m),
            Row(1, JcPlayTypes.Bq, "1-1", 12.0m, 12.5m, 12.2m),
            Row(1, JcPlayTypes.Bq, "0-0", 52.0m, 52.5m, 52.1m)
        };

        var issues = MilkProbScale.Verify(rows);

        Assert.All(issues, i => Assert.Contains("|BF|", i.Key));
    }

    [Fact]
    public void UnpricedRows_AreSkippedForMarketColumns()
    {
        // 盘口没开全时 MarketPct/BlendedPct 为 null（null ≠ 0），不能因此判「合计不到 100」。
        var rows = new[]
        {
            Row(1, JcPlayTypes.Bf, "1:0", 8.0m),
            Row(1, JcPlayTypes.Bf, "2:1", 9.0m),
            Row(1, JcPlayTypes.Bf, "胜其它", 83.0m)
        };

        Assert.Empty(MilkProbScale.Verify(rows));
    }

    [Fact]
    public void NspfWithoutHandicap_AllPureNull_IsSkipped()
    {
        var rows = new[]
        {
            Row(1, JcPlayTypes.Nspf, "3", null, 33.0m),
            Row(1, JcPlayTypes.Nspf, "1", null, 33.0m),
            Row(1, JcPlayTypes.Nspf, "0", null, 34.0m)
        };

        Assert.Empty(MilkProbScale.Verify(rows));
    }

    [Fact]
    public void EmptySnapshot_Passes()
    {
        // 本轮 0 档（没有输入）不是「单位错了」，由源侧账本判，不在这里报。
        Assert.Empty(MilkProbScale.Verify(Array.Empty<MilkProbSnapshot>()));
    }

    /// <summary>
    /// 手算，不读任何已发布快照。
    /// 比分 31 档、总进球 8 档、半全场 9 格的纯模型和混合列各自合计为 1。
    /// 比分市场列用固定水位 1.410：31 档同赔 21.25 时，
    /// 100÷21.25÷1.410=3.3375045…，四舍五入到 3 位是 3.338，31 档合计 103.478。
    /// 这是水位常数和当场 Σ(1/赔率) 的差，不是分布没归一。
    /// </summary>
    [Fact]
    public void FixedMarginMarketSum_IsNotADistributionFailure()
    {
        const decimal odds = 21.25m;
        const decimal over = 1.410m;
        var marketEach = Math.Round(100m / odds / over, 3);
        Assert.Equal(3.338m, marketEach);

        var bfPure = Math.Round(100m / 31m, 3);
        Assert.Equal(3.226m, bfPure);
        Assert.Equal(100.006m, bfPure * 31m);
        Assert.Equal(103.478m, marketEach * 31m);

        var rows = new List<MilkProbSnapshot>();
        foreach (var code in JcPlayDomain.Bf)
            rows.Add(Row(1503020, JcPlayTypes.Bf, code, bfPure, marketEach, bfPure));

        var jq = Math.Round(100m / 8m, 3);
        Assert.Equal(12.500m, jq);
        for (var g = 0; g <= 7; g++)
            rows.Add(Row(1503020, JcPlayTypes.Jq, g.ToString(), jq, jq, jq));

        // 九格：前 8 格 11.111，最后一格补到 100.000−8×11.111=11.112。合计正好 100。
        decimal bqSum = 0m;
        for (var i = 0; i < JcPlayDomain.Bq.Count; i++)
        {
            var pct = i == JcPlayDomain.Bq.Count - 1 ? 100m - bqSum : 11.111m;
            bqSum += pct;
            rows.Add(Row(1503020, JcPlayTypes.Bq, JcPlayDomain.Bq[i], pct, pct, pct));
        }
        Assert.Equal(100m, bqSum);
        Assert.Equal(9, JcPlayDomain.Bq.Count);
        Assert.Equal(8, JcPlayDomain.Jq.Count);
        Assert.Equal(31, JcPlayDomain.Bf.Count);

        Assert.Empty(MilkProbScale.Verify(rows));
    }

    /// <summary>
    /// 把 3.500 再加进「胜其它」，31 档纯模型从 100.006 变成 103.506。
    /// 这才是尾部被重复计入。市场列仍是上面的 103.478，不能拿它当这条的原因。
    /// </summary>
    [Fact]
    public void DoubleCountedTail_FailsTheModelColumnOnly()
    {
        var bfPure = Math.Round(100m / 31m, 3);
        var marketEach = Math.Round(100m / 21.25m / 1.410m, 3);
        var rows = JcPlayDomain.Bf
            .Select(code => Row(1503020, JcPlayTypes.Bf, code, bfPure, marketEach, bfPure))
            .ToList();
        var other = rows.Single(r => r.OptionCode == "胜其它");
        other.ModelPctPure += 3.500m;

        var issues = MilkProbScale.Verify(rows);

        var hit = Assert.Single(issues);
        Assert.Equal("prob-not-normalized", hit.Kind);
        Assert.Contains("ModelPctPure", hit.Key);
        Assert.Contains("sum=103.506", hit.Key);
        Assert.Equal(100, hit.Expected);
        Assert.Equal(104, hit.Actual);
        Assert.DoesNotContain("MarketPct", hit.Key);
    }
}
