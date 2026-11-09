using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

public class LottoRecommendEngineTests
{
    [Fact]
    public void Dlt_ProducesPrimaryPlusThreeAlts()
    {
        var lt = new LotteryTypeInfo
        {
            Code = LottoCodes.Dlt, Name = "大乐透",
            RedCount = 5, RedMax = 35, BlueCount = 2, BlueMax = 12
        };
        var draws = FakeComboDraws(lt, 80);
        var plan = LottoRecommendEngine.Recommend(lt, draws);
        var singles = plan.Picks.Where(p => p.PlayType == "单式").ToList();
        Assert.Equal(4, singles.Count);
        Assert.Single(plan.Picks, p => p.IsPrimary);
        Assert.All(singles, p =>
        {
            Assert.Equal(5, p.Reds.Count);
            Assert.Equal(2, p.Blues.Count);
            Assert.Equal(p.Reds.Count, p.Reds.Distinct().Count());
        });

        var dan = Assert.Single(plan.Picks, p => p.PlayType == "四胆全拖");
        Assert.False(dan.IsPrimary);
        Assert.Equal(4, dan.Reds.Count);
        Assert.Equal(31, dan.TuoReds.Count);
        Assert.Equal(31, dan.BetCount);
        Assert.Equal(2, dan.Blues.Count);
        Assert.Empty(dan.Reds.Intersect(dan.TuoReds));
        Assert.Equal(Enumerable.Range(1, 35), dan.Reds.Concat(dan.TuoReds).OrderBy(x => x));
        Assert.All(dan.Blues, n => Assert.InRange(n, 1, 12));
        Assert.Contains("31 注", dan.SignalDesc);
        Assert.Contains("四胆全拖", plan.Summary);
    }

    [Fact]
    public void Fc3d_ProducesDirectPicks()
    {
        var lt = new LotteryTypeInfo
        {
            Code = LottoCodes.Fc3d, Name = "福彩3D",
            RedCount = 3, RedMax = 9, BlueCount = 0, BlueMax = 0
        };
        var draws = FakeDigitDraws(3, 60);
        var plan = LottoRecommendEngine.Recommend(lt, draws);
        Assert.True(plan.Picks.Count >= 2);
        Assert.Single(plan.Picks.Where(p => p.IsPrimary));
        Assert.All(plan.Picks, p =>
        {
            Assert.Equal(3, p.Reds.Count);
            Assert.All(p.Reds, d => Assert.InRange(d, 0, 9));
        });
    }

    [Fact]
    public void Kl8_ProducesTenNumberPicks()
    {
        var lt = new LotteryTypeInfo
        {
            Code = LottoCodes.Kl8, Name = "快乐8",
            RedCount = 20, RedMax = 80, BlueCount = 0, BlueMax = 0
        };
        var draws = FakeComboDraws(lt, 80);
        var plan = LottoRecommendEngine.Recommend(lt, draws);
        Assert.Equal(4, plan.Picks.Count);
        Assert.Single(plan.Picks.Where(p => p.IsPrimary));
        Assert.Contains("选十", plan.Summary);
        Assert.Equal("2026081", plan.TargetIssue);
        Assert.All(plan.Picks, p =>
        {
            Assert.Equal("选十", p.PlayType);
            Assert.Equal(10, p.Reds.Count);
            Assert.Equal(10, p.Reds.Distinct().Count());
            Assert.Empty(p.Blues);
            Assert.All(p.Reds, n => Assert.InRange(n, 1, 80));
            Assert.Equal(p.Reds.OrderBy(x => x), p.Reds);
        });
    }

    [Fact]
    public void Pl5_Produces_Only_Direct_Picks_No_Dantuo()
    {
        var lt = new LotteryTypeInfo
        {
            Code = LottoCodes.Pl5, Name = "排列五",
            RedCount = 5, RedMax = 9, BlueCount = 0, BlueMax = 0
        };
        var draws = FakePl5Draws(60);
        var plan = LottoRecommendEngine.Recommend(lt, draws);

        // 排列五只有直选单式，不搞胆全托（那是大乐透的玩法）
        Assert.All(plan.Picks, p => Assert.Equal("直选", p.PlayType));
        Assert.DoesNotContain(plan.Picks, p => p.Reds.Any(n => n < 0));
        Assert.DoesNotContain(plan.Summary, "四胆");
    }

    [Fact]
    public void NextIssue_Increments()
    {
        Assert.Equal("2026123", LottoRecommendEngine.NextIssue("2026122"));
    }

    private static List<LotteryDrawRecord> FakeComboDraws(LotteryTypeInfo lt, int n)
    {
        var rnd = new Random(42);
        var list = new List<LotteryDrawRecord>();
        for (var i = 0; i < n; i++)
        {
            var reds = Enumerable.Range(1, lt.RedMax).OrderBy(_ => rnd.Next()).Take(lt.RedCount).OrderBy(x => x).ToList();
            var blues = Enumerable.Range(1, lt.BlueMax).OrderBy(_ => rnd.Next()).Take(lt.BlueCount).OrderBy(x => x).ToList();
            list.Add(new LotteryDrawRecord
            {
                LotteryCode = lt.Code,
                Issue = (2026000 + n - i).ToString(),
                DrawDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-i)),
                Reds = reds,
                Blues = blues
            });
        }
        return list;
    }

    private static List<LotteryDrawRecord> FakeDigitDraws(int len, int n)
    {
        var rnd = new Random(7);
        var list = new List<LotteryDrawRecord>();
        for (var i = 0; i < n; i++)
        {
            list.Add(new LotteryDrawRecord
            {
                LotteryCode = LottoCodes.Fc3d,
                Issue = (2026000 + n - i).ToString(),
                DrawDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-i)),
                Reds = Enumerable.Range(0, len).Select(_ => rnd.Next(0, 10)).ToList()
            });
        }
        return list;
    }

    private static List<LotteryDrawRecord> FakePl5Draws(int n)
    {
        var rnd = new Random(11);
        var list = new List<LotteryDrawRecord>();
        for (var i = 0; i < n; i++)
        {
            list.Add(new LotteryDrawRecord
            {
                LotteryCode = LottoCodes.Pl5,
                Issue = (2026000 + n - i).ToString(),
                DrawDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-i)),
                Reds = Enumerable.Range(0, 5).Select(_ => rnd.Next(0, 10)).ToList()
            });
        }
        return list;
    }
}
