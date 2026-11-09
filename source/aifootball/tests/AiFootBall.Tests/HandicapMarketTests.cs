using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 让球概率的来源（C 项）和赔率变化阈值的换算（E 项）。
///
/// 两件事的共同点：都改的是「用哪个数做判断」，而不是阈值本身，
/// 所以最该防的错误是「两个来源被当成同一个来源」。
/// </summary>
public sealed class HandicapMarketTests
{
    private static MatchRecommendation Rec(string league = "英超", decimal hcp = -1m) => new()
    {
        Board = Boards.Jc,
        MatchNo = "周一001",
        LeagueName = league,
        Handicap = hcp
    };

    /// <summary>
    /// 让球盘和欧赔给出相反的方向时，必须按让球盘走。
    ///
    /// 欧赔链是「不让球三项 → 泊松 → 折算让球数」，中间夹着大小球盘和一个进球期望的拟合；
    /// 让球盘就是这一注的报价。两个市场对同一注的定价不同时，报价市场更准。
    /// </summary>
    [Fact]
    public void 让球方向按盘口挑_欧赔反推的方向不作为优先()
    {
        var rec = Rec();

        // 欧赔：主队偏强（1.80 / 3.60 / 4.20），反推出的让一球方向大概率是让胜。
        // 让球盘：让负极低（4.80 / 4.00 / 1.35），市场明摆着压客队。
        RecommendationSelection.ApplyJcHandicap(rec, 1.80m, 3.60m, 4.20m, 2.5m, 4.80m, 4.00m, 1.35m);

        Assert.Equal('0', rec.RecHandicap);
        Assert.Equal("market", rec.HandicapSource);
        Assert.Equal(1.35m, rec.HandicapOdds);

        // 两个值都留下来，事后才能算「模型和盘口谁更准」
        Assert.NotNull(rec.HandicapMarketProbability);
        Assert.NotNull(rec.HandicapModelProbability);
        Assert.True(rec.HandicapModelProbability < rec.HandicapMarketProbability,
            $"模型 {rec.HandicapModelProbability} 应低于盘口 {rec.HandicapMarketProbability}");

        // HandicapProbability 必须等于盘口值，不能混用
        Assert.Equal(rec.HandicapMarketProbability, rec.HandicapProbability);
    }

    /// <summary>没有让球盘时退回模型，并明确标出来源，不能让下游以为用的是盘口。</summary>
    [Fact]
    public void 缺让球盘时退回模型_来源标记为model()
    {
        var rec = Rec();

        RecommendationSelection.ApplyJcHandicap(rec, 1.80m, 3.60m, 4.20m, 2.5m, null, null, null);

        Assert.NotNull(rec.RecHandicap);
        Assert.Equal("model", rec.HandicapSource);
        Assert.Null(rec.HandicapMarketProbability);
        Assert.NotNull(rec.HandicapModelProbability);
        Assert.Equal(rec.HandicapModelProbability, rec.HandicapProbability);
    }

    /// <summary>盘口三项去水后必然归一，三项加起来是 100%。</summary>
    [Fact]
    public void 盘口概率是去水值_三项归一()
    {
        var rec = Rec(hcp: 0m);
        RecommendationSelection.ApplyJcHandicap(rec, 2.40m, 3.20m, 2.80m, 2.5m, 2.55m, 3.10m, 2.60m);

        var market = HitRateMath.Devig(2.55m, 3.10m, 2.60m);
        Assert.NotNull(market);
        var sum = market!.Value.Home + market.Value.Draw + market.Value.Away;
        Assert.True(Math.Abs(sum - 1m) < 0.0001m, $"去水后合计 {sum}");

        // 三项倒数和 1/2.55+1/3.10+1/2.60 ≈ 1.10，超过 1 的部分就是抽水，
        // 去水后每一项都会比「1 ÷ 水位」小
        Assert.True(market.Value.Home < 1m / 2.55m);
        Assert.True(market.Value.Away < 1m / 2.60m);
    }

    /// <summary>
    /// 低赔场次的赔率异动原来被漏掉：主胜 1.30 跌 0.04 是 −3.1%，
    /// 比 3.50 跌 0.05（−1.4%）更有信息量，但旧阈值（统一 −0.05）把它放过去了。
    /// </summary>
    [Fact]
    public void 低赔场次的小幅异动现在能触发() 
    {
        // 1.30 的主胜，跌 0.04：旧阈值 −0.05 判不出，新阈值（1.30 换算成 −0.0325）能判出
        var lowOdds = RecommendationEngine.Recommend(
            Boards.Jc, null, "001", 1, "英超", "主队", "客队",
            DateTime.Today.AddHours(20), -1m,
            -0.04m, 65m, 20m, null,
            1.34m, 4.20m, 8.50m,
            null, null);

        Assert.Equal('3', lowOdds.RecResult);
        Assert.Equal(3, lowOdds.SignalLevel);
    }

    /// <summary>
    /// 让球方向改按盘口挑之后，会更多地落在热门一侧，于是更容易撞上「必发极端大热（≥75%）不追」。
    /// 11-03 晚实测：周二009、周二010 就是因为这个从让球表里消失——旧规则挑的是冷门侧，
    /// 新规则挑热门侧，然后被大热规则挡掉。这是两条规则的正确叠加，不是丢场。
    /// </summary>
    [Fact]
    public void 让球挑到热门侧时_会被极端大热规则清掉()
    {
        var rec = Rec();
        RecommendationSelection.ApplyJcHandicap(rec, 1.80m, 3.60m, 4.20m, 2.5m, 4.80m, 4.00m, 1.35m);
        Assert.Equal('0', rec.RecHandicap);

        // 客胜占比 80%，达到极端大热
        rec.RatioAway = 80m;
        HotWatch.Mark(rec);

        Assert.Null(rec.RecHandicap);
        Assert.Null(rec.HandicapProbability);
        Assert.True(rec.HotWatch);
        Assert.Empty(RecommendationSelection.ForJcHandicap(new[] { rec }));
    }

    /// <summary>热门一侧没到 75% 时不该清，让球方向照常保留。水位 ≥1.80 才留（低赔不推，2026-11-04）。</summary>
    [Fact]
    public void 让球挑到热门侧但未达极端大热时_正常保留()
    {
        var rec = Rec();
        RecommendationSelection.ApplyJcHandicap(rec, 1.80m, 3.60m, 4.20m, 2.5m, 4.80m, 4.00m, 1.92m);

        rec.RatioAway = 68m;
        HotWatch.Mark(rec);

        Assert.Equal('0', rec.RecHandicap);
        Assert.Single(RecommendationSelection.ForJcHandicap(new[] { rec }));
    }

    /// <summary>
    /// 高赔一侧的门槛保持不变。收紧它会删掉一批原本触发的历史样本，
    /// 53% / 30% / 55% 这些回测命中率立刻失效——那不是今晚该动的事。
    /// </summary>
    [Fact]
    public void 高赔场次的门槛保持不变()
    {
        // 3.80 的主胜，涨 0.25：按绝对阈值算「大升」（+0.20），改完后仍是
        var highOdds = RecommendationEngine.Recommend(
            Boards.Jc, null, "002", 2, "英超", "主队", "客队",
            DateTime.Today.AddHours(20), -1m,
            0.25m, 20m, 65m, null,
            4.05m, 3.60m, 1.75m,
            null, null);

        Assert.Equal('0', highOdds.RecResult);
        Assert.Equal(3, highOdds.SignalLevel);
        Assert.Contains("大升", highOdds.SignalDesc);
    }
}
