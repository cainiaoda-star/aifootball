using AiFootBall.Core.Models;
using Xunit;

namespace AiFootBall.Tests;

public class LottoBetValidatorTests
{
    [Fact]
    public void Default_Play_Follows_Contract_Not_Blind_Single()
    {
        Assert.True(LottoBetValidator.TryValidate(LottoCodes.Fc3d, null, [1, 2, 3], [], null, out var p3, out _));
        Assert.Equal("直选", p3);

        Assert.True(LottoBetValidator.TryValidate(LottoCodes.Kl8, null, Enumerable.Range(1, 10).ToList(), [], null, out var k, out _));
        Assert.Equal("选十", k);

        Assert.True(LottoBetValidator.TryValidate(LottoCodes.Ssq, null, [1, 2, 3, 4, 5, 6], [7], null, out var s, out _));
        Assert.Equal("单式", s);
    }

    [Fact]
    public void Normalize_Blank_Only_Does_Not_Rewrite_Unknown()
    {
        Assert.Equal("选十", LottoBetValidator.NormalizePlayForLottery(LottoCodes.Kl8, null));
        Assert.Equal("选十", LottoBetValidator.NormalizePlayForLottery(LottoCodes.Kl8, "  "));
        Assert.Equal("单式", LottoBetValidator.NormalizePlayForLottery(LottoCodes.Kl8, "单式")); // 原样保留，留给校验拒绝
        Assert.Equal("神秘", LottoBetValidator.NormalizePlayForLottery(LottoCodes.Ssq, "神秘"));
    }

    [Fact]
    public void Unknown_Play_Rejected_No_Loose_Pass()
    {
        Assert.False(LottoBetValidator.TryValidate(LottoCodes.Ssq, "神秘玩法", [1, 2, 3, 4, 5, 6], [7], null, out _, out var err));
        Assert.Contains("未知玩法", err);

        Assert.False(LottoBetValidator.TryValidate(LottoCodes.Kl8, "单式", Enumerable.Range(1, 10).ToList(), [], null, out _, out var kl8));
        Assert.Contains("选十", kl8);
    }

    [Fact]
    public void Illegal_Shape_Blocked_Before_Save()
    {
        Assert.False(LottoBetValidator.TryValidate(LottoCodes.Ssq, "单式", [1, 2, 3], [7], null, out _, out var err));
        Assert.Contains("6 个红球", err);

        Assert.False(LottoBetValidator.TryValidate(LottoCodes.Fc3d, "组选三", [1, 2], [], null, out _, out var g3));
        Assert.Contains("两位 AB", g3);

        Assert.False(LottoBetValidator.TryValidate(LottoCodes.Pl5, "组选六", [1, 2, 3, 4, 5], [], null, out _, out var pl));
        Assert.Contains("仅支持", pl);
    }

    [Fact]
    public void Fushi_And_DanTuo_Rejected_No_Silent_Drop()
    {
        Assert.False(LottoBetValidator.TryValidate(
            LottoCodes.Ssq, "复式",
            [1, 2, 3, 4, 5, 6, 7], [8], null,
            out _, out var fushi));
        Assert.Contains("不支持复式/胆拖", fushi);

        Assert.False(LottoBetValidator.TryValidate(
            LottoCodes.Dlt, "四胆全拖",
            [1, 2, 3, 4], [5, 6], Enumerable.Range(5, 31).ToList(),
            out _, out var dantuo));
        Assert.Contains("拖码无法落库", dantuo);

        // 即使玩法写「单式」，只要带了拖码也拒绝（禁止静默丢拖）
        Assert.False(LottoBetValidator.TryValidate(
            LottoCodes.Dlt, "单式",
            [1, 2, 3, 4, 5], [6, 7], [8, 9, 10],
            out _, out var withTuo));
        Assert.Equal(LottoBetValidator.UnsupportedMultiBetMessage, withTuo);
    }

    /// <summary>
    /// 页面实际请求形态 → 服务校验：选号器/手输均提交 playType=选十，合法 10 码可过；错玩法拒绝。
    /// </summary>
    [Fact]
    public void Kl8_Page_Request_Shape_Requires_XuanShi()
    {
        var ten = Enumerable.Range(1, 10).ToList();

        // 选号器 / 手输：{ playType: "选十", reds: 10 个, blues: [], betAmount: 2 }
        var pagePlay = LottoBetValidator.NormalizePlayForLottery(LottoCodes.Kl8, "选十");
        Assert.Equal("选十", pagePlay);
        Assert.True(LottoBetValidator.TryValidate(LottoCodes.Kl8, pagePlay, ten, [], null, out var ok, out _));
        Assert.Equal("选十", ok);

        // 空白玩法：规范化为选十后通过（兼容旧客户端漏传）
        Assert.True(LottoBetValidator.TryValidate(LottoCodes.Kl8, null, ten, [], null, out var def, out _));
        Assert.Equal("选十", def);

        // 错把快乐8写成单式：拒绝（不得宽松放行）
        Assert.False(LottoBetValidator.TryValidate(LottoCodes.Kl8, "单式", ten, [], null, out _, out var bad));
        Assert.Contains("选十", bad);

        // 9 个号：形态非法
        Assert.False(LottoBetValidator.TryValidate(LottoCodes.Kl8, "选十", Enumerable.Range(1, 9).ToList(), [], null, out _, out var shape));
        Assert.Contains("10 个号", shape);
    }
}
