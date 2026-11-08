using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 半场比分解析的回归测试（2026-11-08 半场数据事故）。
///
/// 事故本身：`FiveHundredMatchSource.ParseRow` 拿到结构化全场比分后，无条件把
/// 「第一个匹配比分正则的单元格」当半场。而装全场的 `div.pk` 那格 innerText 会被清成
/// "4-0"，同样匹配正则还排在最前面 —— 于是**半场永远是全场的副本**。
/// 全库 84% 的场次受害，半场场均进球 2.58（全场 2.90 的 89%，真实约 43%），
/// 所有半全场实证基线（九档、联赛反转表、HalfDrawModel）全部作废。
///
/// 第一版修法也是错的：改成「数值与全场不同才算半场」，结果把真实存在的
/// 「半场 = 全场」（约 20%）一起丢掉，2026-11-07 的同分率被压到 0%、样本少三成。
/// 所以这里的测试必须同时锁住两件事：
///   · 不能把全场格当半场；
///   · 不能因为「半场恰好等于全场」就丢掉它。
/// </summary>
public class HalfScoreParseTests
{
    /// <summary>真实行样例（500 完场页，阿森纳 vs 赫尔城 1:0，半场 0:0）。
    /// 注意第 5 格：pk 格的文本被盘口污染成 "1两球/两球半0"，不匹配比分正则。</summary>
    private static readonly string[] RealRow =
    {
        "英超", "第10轮", "11-07 23:00", "完", "[01]阿森纳",
        "1两球/两球半0",            // 5 = 装 div.pk 的全场格，文本被污染
        "赫尔城5[12]",
        "0 - 0",                    // 7 = 真正的半场格
        "",
        "析 亚 欧 情"
    };

    [Fact]
    public void PicksHalfCell_NotTheFullScoreCell_EvenWhenPkTextIsPolluted()
    {
        var half = FiveHundredMatchSource.PickHalfScore(RealRow, fullCellIdx: 5, fullHome: 1, fullAway: 0);

        Assert.NotNull(half);
        Assert.Equal(0, half!.Value.H);
        Assert.Equal(0, half.Value.A);
    }

    [Fact]
    public void KeepsLegitimateSameScore_WhenHalfEqualsFull()
    {
        // 真实足球约 20% 的场次半场就等于全场。这类样本必须保留，
        // 不能因为「和全场一样」就当作拷贝丢掉（第一版修法的错误）。
        var texts = new[] { "英超", "11-07 23:00", "完", "2-2", "2 - 2", "析" };
        var half = FiveHundredMatchSource.PickHalfScore(texts, fullCellIdx: 3, fullHome: 2, fullAway: 2);

        Assert.NotNull(half);
        Assert.Equal(2, half!.Value.H);
        Assert.Equal(2, half.Value.A);
    }

    [Fact]
    public void IgnoresHigherThanFullScore()
    {
        // 残行/别处数字：半场 3:0 不可能出现在全场 1:0 的比赛里。
        var texts = new[] { "英超", "1-0", "3 - 0", "析" };
        var half = FiveHundredMatchSource.PickHalfScore(texts, fullCellIdx: 1, fullHome: 1, fullAway: 0);

        Assert.Null(half);
    }

    [Fact]
    public void ReturnsNull_WhenRowHasNoHalfCell()
    {
        // 完场页有约 15~25% 的行没有半场格（低级别联赛尤甚）。宁可留空，不要编。
        var texts = new[] { "英超", "第10轮", "11-07 23:00", "完", "[01]阿森纳", "1-0", "赫尔城[12]", "析" };
        var half = FiveHundredMatchSource.PickHalfScore(texts, fullCellIdx: 5, fullHome: 1, fullAway: 0);

        Assert.Null(half);
    }

    [Fact]
    public void DoesNotMistakeTimeOrHandicapTextForScore()
    {
        var texts = new[] { "英超", "第10轮", "11-07 23:00", "完", "阿森纳", "1-0", "赫尔城", "析" };
        var half = FiveHundredMatchSource.PickHalfScore(texts, fullCellIdx: 5, fullHome: 1, fullAway: 0);

        Assert.Null(half);
    }

    [Fact]
    public void UsableCriterion_AcceptsOnlyLogicallyPossibleHalfScores()
    {
        Assert.True(HalfTimeQuality.IsUsable(1, 0, 2, 0));   // 半场 1:0 → 全场 2:0
        Assert.True(HalfTimeQuality.IsUsable(2, 2, 2, 2));    // 半场=全场，合法（真实约 20%）
        Assert.False(HalfTimeQuality.IsUsable(3, 0, 1, 0));   // 半场超过全场
        Assert.False(HalfTimeQuality.IsUsable(null, null, 1, 0));
    }
}
