using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// SignalTag 与 SignalDesc 的一致性。
///
/// 这次重构把「靠文案 Contains 判定」换成了「看标签位」，前提是两边严格对应。
/// 下面用一批覆盖各分支的输入跑真引擎，逐条断言「文案里出现某个词」和「置了对应的位」
/// 完全一致——只要有一处置位漏了，这里就会红。
///
/// 注意：这组断言是给重构期对照用的。以后要是故意改了文案措辞，删掉对应的镜像断言即可，
/// 判定本身已经只看标签位。
/// </summary>
public class SignalTagTests
{
    public static IEnumerable<object[]> Grid()
    {
        decimal?[] deltas = { null, -0.30m, -0.12m, 0.02m, 0.30m };
        (decimal? Home, decimal? Away)[] ratios =
        {
            (null, null), (65m, 20m), (20m, 65m), (80m, 5m), (5m, 80m), (40m, 30m), (55m, 52m)
        };
        (decimal? H, decimal? D, decimal? A)[] spfs =
        {
            (1.85m, 3.40m, 4.20m), (3.80m, 3.40m, 1.95m), (2.50m, 3.05m, 2.60m), (null, null, null)
        };
        string[] leagues = { "英超", "澳超" };

        foreach (var delta in deltas)
            foreach (var ratio in ratios)
                foreach (var spf in spfs)
                    foreach (var league in leagues)
                        yield return new object[] { delta, ratio.Home, ratio.Away, spf.H, spf.D, spf.A, league };
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void 标签位与文案严格对应(
        decimal? delta, decimal? ratioHome, decimal? ratioAway,
        decimal? spfHome, decimal? spfDraw, decimal? spfAway, string league)
    {
        var rec = RecommendationEngine.Recommend(
            Boards.Jc, null, "001", 1, league, "主队", "客队",
            DateTime.Today.AddHours(20), 0m,
            delta, ratioHome, ratioAway, null,
            spfHome, spfDraw, spfAway);

        AssertMirror(rec);
    }

    [Theory]
    [InlineData(-0.75, -0.75)]
    [InlineData(0.25, 0.25)]
    [InlineData(-0.75, -0.25)]
    [InlineData(0.50, 0.25)]
    public void 深主让与亚盘冲突的标签也对得上(double open, double close)
    {
        var rec = RecommendationEngine.Recommend(
            Boards.Jc, null, "001", 1, "英超", "主队", "客队",
            DateTime.Today.AddHours(20), -0.75m,
            0m, 40m, 30m, null,
            1.85m, 3.40m, 4.20m,
            null, null,
            asianOpen: (decimal)open, asianClose: (decimal)close);

        AssertMirror(rec);
    }

    [Theory]
    [InlineData(0.10)]
    [InlineData(-0.10)]
    public void 尾盘反抽与近况否决的标签也对得上(double late)
    {
        var rec = RecommendationEngine.Recommend(
            Boards.Jc, null, "001", 1, "英超", "主队", "客队",
            DateTime.Today.AddHours(20), 0m,
            -0.12m, 65m, 20m, null,
            1.85m, 3.40m, 4.20m,
            null, null, null, null,
            lateHomeDelta: (decimal)late, homeFormPts: 0, awayFormPts: 9);

        AssertMirror(rec);
    }

    [Fact]
    public void 经验降权标了不进精选()
    {
        var rec = new MatchRecommendation
        {
            Board = Boards.Jc, MatchNo = "001", RecResult = '3',
            SignalLevel = 2, Confidence = 62m
        };

        HitProbabilityService.ApplyEmpiricalGate(rec, hits: 20, total: 60, pickOdds: 2.10m);

        Assert.Contains("不进精选", rec.SignalDesc);
        Assert.True(rec.Tags.HasFlag(SignalTag.NotFeatured));
    }

    [Fact]
    public void 模型改看平_标平局位并算不进精选()
    {
        var rec = new MatchRecommendation { Board = Boards.Jc, MatchNo = "001", SignalLevel = 0 };

        var changed = HitProbabilityService.TryApplyModelSide(rec, (0.30m, 0.45m, 0.25m), 3.20m, 3.30m, 3.20m);

        Assert.True(changed);
        Assert.Equal('1', rec.RecResult);
        Assert.Contains("模型看平", rec.SignalDesc);
        Assert.True(rec.Tags.HasFlag(SignalTag.DrawByModel));
        Assert.True(rec.Tags.HasFlag(SignalTag.NotFeatured));
    }

    [Fact]
    public void 模型改看客胜_置客向位_长水降权才认得出这一单()
    {
        var rec = new MatchRecommendation
        {
            Board = Boards.Jc, MatchNo = "001", RecResult = '3', SignalLevel = 2, Confidence = 61m
        };

        var changed = HitProbabilityService.TryApplyModelSide(rec, (0.22m, 0.26m, 0.52m), 4.10m, 3.50m, 2.00m);

        Assert.True(changed);
        Assert.Equal('0', rec.RecResult);
        Assert.Contains("模型改看客胜", rec.SignalDesc);
        // 文案里出现「看客胜」，长水降权原先靠这个子串认出它，现在靠这一位
        Assert.True(rec.Tags.HasFlag(SignalTag.AwayPick));
    }

    [Fact]
    public void 模型看平只补一次文案()
    {
        var rec = new MatchRecommendation { Board = Boards.Jc, MatchNo = "001", SignalLevel = 0 };

        HitProbabilityService.TryApplyModelSide(rec, (0.30m, 0.45m, 0.25m), 3.20m, 3.30m, 3.20m);
        HitProbabilityService.TryApplyModelSide(rec, (0.28m, 0.46m, 0.26m), 3.20m, 3.30m, 3.20m);

        var count = rec.SignalDesc.Split("模型看平", StringSplitOptions.None).Length - 1;
        Assert.Equal(1, count);
    }

    private static void AssertMirror(MatchRecommendation rec)
    {
        var desc = rec.SignalDesc ?? "";
        var ctx = $"delta/ratio 组合产生的文案：{desc} ｜ tags={rec.Tags}";

        Same(desc.Contains("偏热"), rec.Tags.HasFlag(SignalTag.ExchangeHot), "偏热", ctx);
        Same(desc.Contains("极端大热"), rec.Tags.HasFlag(SignalTag.ExchangeExtremeHot), "极端大热", ctx);
        Same(desc.Contains("不进精选"), rec.Tags.HasFlag(SignalTag.NotFeatured), "不进精选", ctx);
        Same(desc.Contains("大升"), rec.Tags.HasFlag(SignalTag.OddsBigRise), "大升", ctx);
        Same(desc.Contains("看客胜"), rec.Tags.HasFlag(SignalTag.AwayPick), "看客胜", ctx);
        Same(desc.Contains("均势看平"), rec.Tags.HasFlag(SignalTag.DrawEven), "均势看平", ctx);
        Same(desc.Contains("平局回测偏低"), rec.Tags.HasFlag(SignalTag.DrawLowBacktest), "平局回测偏低", ctx);
        Same(desc.Contains("澳超客胜不进精选"), rec.Tags.HasFlag(SignalTag.ALeagueAwayNotFeatured), "澳超客胜不进精选", ctx);
        Same(rec.IsOddsFallback, rec.Tags.HasFlag(SignalTag.OddsFallback), "欧赔兜底", ctx);
    }

    private static void Same(bool textSays, bool tagSays, string word, string ctx)
        => Assert.True(textSays == tagSays, $"「{word}」文案与标签不一致：文案 {textSays} / 标签 {tagSays}。{ctx}");
}
