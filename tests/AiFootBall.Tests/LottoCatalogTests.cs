using AiFootBall.Core.Models;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 彩种目录：哪些能选号、哪些只采开奖。
/// 这两份清单以前是脱钩的 —— LotteryType 表有 8 个彩种（IsActive 全为 1 却没人查），
/// LottoCodes.All 只有 6 个，靠人记得同步。这组测试把「代码白名单」钉死，
/// 数据库那一侧由 13_deactivate_unsupported_lottery.sql 对齐。
/// </summary>
public class LottoCatalogTests
{
    [Fact]
    public void 选号白名单恰好六个且不含七乐彩七星彩()
    {
        Assert.Equal(6, LottoCodes.All.Length);
        Assert.Equal(
            new[] { "ssq", "dlt", "fc3d", "kl8", "pl3", "pl5" }.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            LottoCodes.All.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("qlc", LottoCodes.All);
        Assert.DoesNotContain("qxc", LottoCodes.All);
    }

    [Theory]
    [InlineData("ssq")]
    [InlineData("dlt")]
    [InlineData("fc3d")]
    [InlineData("kl8")]
    [InlineData("pl3")]
    [InlineData("pl5")]
    public void 支持的彩种标记为可选号(string code)
    {
        Assert.True(TypeOf(code).SupportsRecommend);
        Assert.Equal(code, TypeOf(code).Code);
    }

    [Theory]
    [InlineData("qlc", "七乐彩")]
    [InlineData("qxc", "七星彩")]
    public void 只采开奖的彩种标记为不可选号(string code, string name)
    {
        var lt = new LotteryTypeInfo { Code = code, Name = name };
        Assert.False(lt.SupportsRecommend);
        // 名字照旧能展示：它们在「数据概览」里是有意义的（有几千期开奖）
        Assert.Equal(name, lt.Name);
    }

    [Fact]
    public void 彩种代码大小写不影响判定()
    {
        Assert.True(new LotteryTypeInfo { Code = "SSQ" }.SupportsRecommend);
        Assert.False(new LotteryTypeInfo { Code = "QLC" }.SupportsRecommend);
    }

    [Fact]
    public void 未知彩种的显示名回落到代码本身()
    {
        Assert.Equal("qlc", LottoCodes.DisplayName("qlc"));
        Assert.Equal("qxc", LottoCodes.DisplayName("qxc"));
        // 不抛异常很重要：老推荐记录里可能挂着这些 code，复盘时要能显示
        Assert.Equal("双色球", LottoCodes.DisplayName("ssq"));
    }

    private static LotteryTypeInfo TypeOf(string code) => new() { Code = code, Name = code };
}
