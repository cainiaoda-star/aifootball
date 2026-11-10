using AiFootBall.Core.Models;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 胜平负 / 让球胜平负的判定（2026-11-10 补）。
///
/// 背景：<see cref="MilkPickMath.IsHit"/> 原来只认 BF / BQ / JQ，SPF 与 NSPF 一律落到
/// `default: return false`——不管打成什么比分都算「没中」。这种错法是静默的：
/// 台账里不会出现任何异常，只是命中率被系统性压低，而恰好判对时（不中就是不中）
/// 也完全看不出来。11-07 罗马 3:0 萨索洛 让平 @4.40 就是这种情况。
///
/// 这批测试锁三件事：
///   1. SPF 三档按比分判，不看让球；
///   2. NSPF 三档按「净胜 + 让球数」判，走盘 = 让平；
///   3. 缺让球数时**不许判**（返回 false 只是兜底，调用方必须先跳过）。
/// </summary>
public class MilkPickHandicapTests
{
    /* ---------------- 胜平负（不让球） ---------------- */

    [Theory]
    [InlineData("3", 2, 0, true)]     // 主胜
    [InlineData("1", 2, 0, false)]    // 2:0 不是平
    [InlineData("0", 2, 0, false)]
    [InlineData("1", 1, 1, true)]     // 平
    [InlineData("3", 1, 1, false)]
    [InlineData("0", 0, 3, true)]     // 客胜
    [InlineData("3", 0, 3, false)]
    [InlineData("3", 0, 0, false)]    // 0:0 是平不是主胜
    public void IsHit_Spf_JudgeByScoreOnly(string code, int home, int away, bool expected)
        => Assert.Equal(expected, MilkPickMath.IsHit(JcPlayTypes.Spf, code, home, away, null, null));

    [Fact]
    public void IsHit_Spf_IgnoresHandicapEvenIfPassed()
    {
        // SPF 是不让球玩法。就算调用方把让球数传进来，也不许影响结论——
        // 否则「主胜 @1.5」在让 -1 的场上会被判成没中，等于把玩法搞混。
        Assert.True(MilkPickMath.IsHit(JcPlayTypes.Spf, "3", 1, 0, null, null, -1m));
        Assert.True(MilkPickMath.IsHit(JcPlayTypes.Spf, "3", 1, 0, null, null, -2m));
    }

    /* ---------------- 让球胜平负 ---------------- */

    [Theory]
    // 主让 1 球（handicap = -1）：净胜 = 主 − 客 − 1
    [InlineData("3", 2, 0, -1, true)]    // 净胜 1 → 让胜
    [InlineData("1", 2, 0, -1, false)]
    [InlineData("1", 1, 0, -1, true)]    // 净胜 0 → 让平（走盘）
    [InlineData("3", 1, 0, -1, false)]   // 只赢 1 球不是让胜
    [InlineData("0", 0, 0, -1, true)]    // 净胜 -1 → 让负
    [InlineData("1", 0, 0, -1, false)]
    // 主让 2 球（handicap = -2）
    [InlineData("1", 2, 0, -2, true)]    // 净胜 0 → 让平
    [InlineData("3", 3, 0, -2, true)]    // 净胜 1 → 让胜
    [InlineData("0", 1, 0, -2, true)]    // 净胜 -1 → 让负
    // 主受让 1 球（handicap = +1）
    [InlineData("3", 0, 0, 1, true)]     // 净胜 1 → 让胜
    [InlineData("1", 0, 1, 1, true)]     // 净胜 0 → 让平
    [InlineData("0", 0, 2, 1, true)]     // 净胜 -1 → 让负
    [InlineData("3", 0, 1, 1, false)]    // 输球但受让后走盘，不是让胜
    // 平手盘（handicap = 0）等价于不让球
    [InlineData("3", 1, 0, 0, true)]
    [InlineData("1", 1, 1, 0, true)]
    [InlineData("0", 0, 1, 0, true)]
    public void IsHit_Nspf_JudgeByNetAfterHandicap(string code, int home, int away, decimal hcp, bool expected)
        => Assert.Equal(expected, MilkPickMath.IsHit(JcPlayTypes.Nspf, code, home, away, null, null, hcp));

    [Fact]
    public void IsHit_Nspf_WithoutHandicap_ReturnsFalseAsLastResort()
    {
        // 兜底行为：缺让球数时只能返回 false。**调用方必须先跳过**（见两个 SettleAsync），
        // 否则这注会被记成「没中」，命中率被污染。这里锁的是「别改成 true」。
        Assert.False(MilkPickMath.IsHit(JcPlayTypes.Nspf, "3", 3, 0, null, null));
        Assert.False(MilkPickMath.IsHit(JcPlayTypes.Nspf, "1", 1, 0, null, null));
        Assert.False(MilkPickMath.IsHit(JcPlayTypes.Nspf, "0", 0, 1, null, null));
    }

    [Fact]
    public void IsHit_Nspf_RealCase_RomaSassuolo_LetDrawMisses()
    {
        // 11-07 罗马 3:0 萨索洛，台账那注是「让球胜平负 让平 @4.40」。
        // 主让 1 球后净胜 2 球 = 让胜，所以让平不中。
        // 修复前它也是 false，但那是 default 兜出来的；这条测试保证以后是「算」出来的。
        Assert.False(MilkPickMath.IsHit(JcPlayTypes.Nspf, "1", 3, 0, null, null, -1m));
        Assert.True(MilkPickMath.IsHit(JcPlayTypes.Nspf, "3", 3, 0, null, null, -1m));
    }

    /* ---------------- 显示名 ---------------- */

    [Fact]
    public void DisplayName_Spf_ShowsWordsNotRawCodes()
    {
        // 缺这一支时页面会把选项列直接印成 "3"——玩家不可能知道那是主胜。
        Assert.Equal("胜平负 主胜", MilkPickMath.DisplayName(JcPlayTypes.Spf, "3"));
        Assert.Equal("胜平负 平", MilkPickMath.DisplayName(JcPlayTypes.Spf, "1"));
        Assert.Equal("胜平负 客胜", MilkPickMath.DisplayName(JcPlayTypes.Spf, "0"));
        Assert.Equal("主胜", MilkPickMath.ShortName(JcPlayTypes.Spf, "3"));
        Assert.Equal("客胜", MilkPickMath.ShortName(JcPlayTypes.Spf, "0"));
    }

    [Fact]
    public void ExistingPlays_UnaffectedByNewBranches()
    {
        // 回归：加了两支之后，原三种玩法的结论一个都不许变。
        Assert.True(MilkPickMath.IsHit(JcPlayTypes.Bf, "3:1", 3, 1, null, null));
        Assert.True(MilkPickMath.IsHit(JcPlayTypes.Jq, "4", 3, 1, null, null));
        Assert.True(MilkPickMath.IsHit(JcPlayTypes.Bq, "3-0", 0, 2, 1, 0, null));
        Assert.False(MilkPickMath.IsHit(JcPlayTypes.Bq, "3-0", 3, 1, 1, null, null));
        Assert.False(MilkPickMath.IsHit("UNKNOWN", "3", 1, 0, null, null));
    }
}
