using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

public class DailyReviewTests
{
    [Fact]
    public void Tiers_Map_Jingcai_And_Beidan_Names()
    {
        Assert.Equal("A", LeagueTier.Of("英超"));
        Assert.Equal("A", LeagueTier.Of("J1联赛"));
        Assert.Equal("A", LeagueTier.Of("日职"));
        Assert.Equal("B", LeagueTier.Of("英冠"));
        Assert.Equal("B", LeagueTier.Of("瑞超"));
        Assert.Equal("C", LeagueTier.Of("英乙"));
        Assert.Equal("C", LeagueTier.Of("芬兰超级联赛"));
        Assert.Null(LeagueTier.Of("某青年联赛"));
    }

    [Fact]
    public void Render_Writes_Matches_That_Were_Not_Published()
    {
        var rows = new[]
        {
            Row("jc", "", "周六010", "勒沃库森", "斯图加特", published: true, verdict: "未中"),
            Row("jc", "", "周六011", "科莫", "威尼斯", published: false, verdict: "没有方向"),
            Row("bd", "A", "100", "马尔默", "哈马比", published: true, verdict: "命中"),
            Row("bd", "C", "200", "某队", "客队", published: false, verdict: "未中"),
            Row("sfc", "", "26153-6", "勒阿弗尔", "巴黎圣日耳曼", published: false, verdict: "未中")
        };

        var text = DailyReview.Render(new DateTime(2026, 10, 31), new DateTime(2026, 11, 1, 8, 20, 0), rows);

        Assert.Contains("周六011", text);
        Assert.Contains("没进精选", text);
        Assert.Contains("## 北单 C 级", text);
        Assert.Contains("某队", text);
        Assert.Contains("不改 55%", text);
        Assert.Contains("页面会发布但未中", text);
        Assert.Contains("勒沃库森", text);
        Assert.Contains("| 板块 | 场次 | 有方向 | 命中 | 命中率 | 会发布 |", text);
        Assert.Contains("| 竞彩 | 2 | 1 | 0 | 0% | 1 中 0 |", text);
        Assert.DoesNotContain("| 北单未分级 |", text);
        Assert.Contains("## 胜负彩（全部场次）", text);
        Assert.Contains("### 26153 ", text);
        Assert.Contains("#### 26153-6", text);
        Assert.Contains("巴黎圣日耳曼", text);
    }

    [Fact]
    public void Sfc_puts_the_latest_issue_above_one_already_finished()
    {
        var rows = new[]
        {
            Row("sfc", "", "26153-1", "谢菲联", "雷克斯", published: false, verdict: "命中"),
            Row("sfc", "", "26154-1", "巴黎圣日耳曼", "朗斯", published: false, verdict: "未中"),
            Row("sfc", "", "26155-1", "切尔西", "曼联", published: false, verdict: "未完场", finished: false)
        };

        var text = DailyReview.Render(new DateTime(2026, 10, 31), new DateTime(2026, 11, 1, 8, 20, 0), rows);
        var i55 = text.IndexOf("### 26155 ", StringComparison.Ordinal);
        var i54 = text.IndexOf("### 26154 ", StringComparison.Ordinal);
        var i53 = text.IndexOf("### 26153 ", StringComparison.Ordinal);

        Assert.True(i55 >= 0 && i54 > i55 && i53 > i54);
    }

    [Fact]
    public void Totals_and_milk_sit_in_the_review()
    {
        var rows = new[]
        {
            Row("jc", "", "周六001", "切尔西", "曼联", published: false, verdict: "没有方向",
                ouSide: "大", ouLine: 2.5m, ouProb: 54.2m, ouVerdict: "赢"),
            Row("bd", "A", "100", "马尔默", "哈马比", published: false, verdict: "未中",
                ouSide: "小", ouLine: 2.75m, ouProb: 51m, ouVerdict: "输半")
        };

        var milk = MilkScanService.FormatReview(new[]
        {
            new MilkScanService.WatchItem
            {
                Board = "jc",
                MatchNo = "周六010",
                MatchId = 7,
                League = "德甲",
                Home = "勒沃库森",
                Away = "斯图加特",
                Focus = "高球观察",
                Tags = new() { new MilkEventTaxonomy.MilkTagHit { Tag = MilkEventTaxonomy.Tags.Goals7pWatch, Label = "高球观察", Note = "大小球 3.25" } },
                EuHome = 1.40m
            }
        }, new Dictionary<long, ReviewScore>
        {
            [7] = new ReviewScore { MatchId = 7, Status = 3, HomeScore = 2, AwayScore = 4, HalfHomeScore = 1, HalfAwayScore = 2 }
        });

        var text = DailyReview.Render(new DateTime(2026, 10, 31), new DateTime(2026, 11, 1, 8, 20, 0), rows, milk);

        Assert.Contains("## 大小球（只作参考）", text);
        Assert.Contains("不进精选，不购买", text);
        Assert.Contains("竞彩 1 场，完场 1，赢 1，赢半 0，走盘 0，输半 0，输 0。", text);
        Assert.Contains("北单 1 场，完场 1，赢 0，赢半 0，走盘 0，输半 1，输 0。", text);
        Assert.Contains("周六001 德甲 切尔西 vs 曼联 盘口 2.5 看大 54.2%，进球 4，赢", text);
        Assert.Contains("## 冷门参考", text);
        Assert.Contains("只对照，不购买", text);
        Assert.Contains("高球 1 场打出 0", text);
        Assert.Contains("勒沃库森", text);
        Assert.Contains("对照：没打出", text);
        var milkAt = text.IndexOf("## 冷门参考", StringComparison.Ordinal);
        var ouAt = text.IndexOf("## 大小球", StringComparison.Ordinal);
        var jcAt = text.IndexOf("## 竞彩（全部场次）", StringComparison.Ordinal);
        Assert.True(milkAt >= 0 && ouAt > milkAt && jcAt > ouAt);
    }

    static ReviewRow Row(
        string board, string tier, string no, string home, string away, bool published, string verdict,
        bool finished = true, string? ouSide = null, decimal? ouLine = null, decimal? ouProb = null, string? ouVerdict = null) => new()
    {
        Board = board,
        Tier = tier,
        MatchNo = no,
        League = "德甲",
        Home = home,
        Away = away,
        Finished = finished,
        ScoreText = "1:3",
        DirectionText = verdict == "没有方向" ? "没有方向" : "主胜",
        Published = published,
        Verdict = verdict,
        SavedText = "库里没有这场。",
        Signal = "测试依据",
        OuSide = ouSide,
        OuLine = ouLine,
        OuProbability = ouProb,
        OuVerdict = ouVerdict
    };
}
