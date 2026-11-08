using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 「市场大热、模型掉头」这道闸的守门测试（2026-11-08 加）。
///
/// 起因：11-08 周日007 埃尔切 vs 贝蒂斯，客胜 1.67（去水 52.6%），
/// 模型给主胜 37.13%，还发出「半全场胜胜 19.69% @5.85」挂到榜上。
/// 实证口径：竞彩 SPF 客胜 1.55~1.75 的 31 场已完赛，主胜只 19.4%。
///
/// 这组测试锁三件事：
///   1. 触发条件就是那三条，不多不少（50% 门槛、方向相反、背离 20 点）；
///   2. 边界值不许漂——49.9% 不触发、20.0 点触发；
///   3. 方向一致时永不触发（哪怕市场 90%）。
/// </summary>
public class MarketStrongReverseTests
{
    private static MilkDirectionDiagnosis Diag(
        char? market, decimal homePct, decimal drawPct, decimal awayPct, char? model = '3')
        => new()
        {
            MarketDirection = market,
            MarketHomePct = homePct,
            MarketDrawPct = drawPct,
            MarketAwayPct = awayPct,
            ModelDirection = model
        };

    /// <summary>11-08 埃尔切 vs 贝蒂斯原样：市场客 52.6%，模型侧（主胜）在市场里只有 23.7%。</summary>
    [Fact]
    public void Case_Elche_Betis_Is_Blocked()
    {
        var d = Diag('0', 23.7m, 23.7m, 52.6m, '3');
        Assert.True(MilkPickEngine.MarketStrongReverse(d, '3'));
    }

    /// <summary>11-08 伊普斯 vs 伯恩茅斯：市场客 52.9%，模型指主胜。</summary>
    [Fact]
    public void Case_Ipswich_Bournemouth_Is_Blocked()
    {
        var d = Diag('0', 22.4m, 24.7m, 52.9m, '3');
        Assert.True(MilkPickEngine.MarketStrongReverse(d, '3'));
    }

    /// <summary>市场最强侧只有 49.9%——不到 50%，属于「市场也没方向」，不许砍。</summary>
    [Fact]
    public void Market_Below_Threshold_Is_Not_Blocked()
    {
        // 11-08 国际米兰 vs 科莫：市场主 49.99%
        var d = Diag('3', 49.99m, 22.4m, 27.7m, '0');
        Assert.False(MilkPickEngine.MarketStrongReverse(d, '0'));
    }

    /// <summary>背离正好 20.0 点必须触发（门槛写的是 ≥）。</summary>
    [Fact]
    public void Gap_Exactly_At_Threshold_Is_Blocked()
    {
        var d = Diag('0', 30m, 20m, 50m, '3');
        Assert.True(MilkPickEngine.MarketStrongReverse(d, '3'));
    }

    /// <summary>背离 19.9 点不触发。</summary>
    [Fact]
    public void Gap_Just_Under_Threshold_Is_Not_Blocked()
    {
        var d = Diag('0', 30.1m, 20m, 50m, '3');
        Assert.False(MilkPickEngine.MarketStrongReverse(d, '3'));
    }

    /// <summary>方向一致时，市场再热也不能砍。</summary>
    [Fact]
    public void Same_Direction_Is_Never_Blocked()
    {
        var d = Diag('0', 5m, 5m, 90m, '0');
        Assert.False(MilkPickEngine.MarketStrongReverse(d, '0'));
    }

    /// <summary>注没有方向（只有总进球）时退回模型方向判断。</summary>
    [Fact]
    public void NullPickDirection_Falls_Back_To_ModelDirection()
    {
        var d = Diag('0', 20m, 20m, 60m, '3');
        Assert.True(MilkPickEngine.MarketStrongReverse(d, null));
    }

    /// <summary>市场方向算不出来时一律不砍——宁可放过，不拿残缺数据判。</summary>
    [Fact]
    public void Unknown_Market_Is_Not_Blocked()
    {
        var d = Diag(null, 0m, 0m, 0m, '3');
        Assert.False(MilkPickEngine.MarketStrongReverse(d, '3'));
    }

    /// <summary>11-08 那不勒斯 vs 拉齐奥：市场主 38.2%，模型指平——不到大热门槛，不砍。</summary>
    [Fact]
    public void Napoli_Lazio_Is_Not_Blocked()
    {
        var d = Diag('3', 38.2m, 25.1m, 36.7m, '1');
        Assert.False(MilkPickEngine.MarketStrongReverse(d, '1'));
    }

    /// <summary>门槛常量本身不许偷偷改。改了这里会红，逼着改的人先去看注释里的 31 场实证。</summary>
    [Fact]
    public void Thresholds_Are_Pinned()
    {
        Assert.Equal(50m, MilkPickEngine.MarketStrongPct);
        Assert.Equal(20m, MilkPickEngine.MarketStrongReverseGap);
    }
}
