using AiFootBall.Core.Models;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 空跑台账口径测试（2026-11-08 加）。
///
/// 这套口径是「两周后要不要真的跟单」的唯一依据，改错一次整份结论就废了，
/// 而且错得很安静——报表照样出，只是数字骗人。所以三条硬口径各钉一条：
/// 撤注不算没中、回报用平注、撤注单独计数。
/// </summary>
public class PaperTradeTests
{
    private static PaperTradeRow Row(string play, decimal odds, bool? hit,
        bool voided = false, bool counterHigh = false, bool overConfident = false, bool lowConf = false)
        => new()
        {
            PlayType = play,
            OptionCode = "X",
            Odds = odds,
            Hit = hit,
            Voided = voided,
            CounterHigh = counterHigh,
            ModelOverconfident = overConfident,
            ModelLowConfidence = lowConf
        };

    [Fact]
    public void 撤注的注不进分母_也不算没中()
    {
        var rows = new List<PaperTradeRow>
        {
            Row("BQ", 8.0m, true),                 // 中
            Row("BQ", 5.0m, false),                // 未中
            Row("BQ", 6.0m, null, voided: true)    // 撤注：既不是中，也不是未中
        };

        var s = PaperTradeMath.Slice("BQ", rows);

        Assert.Equal(3, s.Total);
        Assert.Equal(1, s.Voided);
        Assert.Equal(2, s.Settled);        // 分母 2，不是 3
        Assert.Equal(1, s.Hits);
        Assert.Equal(50.00m, s.HitPct);
        Assert.Equal(2m, s.Stake);
        Assert.Equal(8.0m, s.Return);      // 撤注那注的赔率不进回报
        Assert.Equal(4.0m, s.Roi);         // 8.0 / 2
    }

    [Fact]
    public void 未结算的注不进分母()
    {
        var rows = new List<PaperTradeRow>
        {
            Row("BF", 10.5m, null),   // 赛果还没到
            Row("BF", 7.0m, false)
        };

        var s = PaperTradeMath.Slice("BF", rows);

        Assert.Equal(2, s.Total);
        Assert.Equal(1, s.Settled);
        Assert.Equal(0m, s.Return);
        Assert.Equal(0m, s.Roi);
    }

    [Fact]
    public void 全部撤注时回报为0而不是除零()
    {
        var rows = new List<PaperTradeRow> { Row("JQ", 4.2m, null, voided: true) };

        var s = PaperTradeMath.Slice("JQ", rows);

        Assert.Equal(1, s.Total);
        Assert.Equal(1, s.Voided);
        Assert.Equal(0, s.Settled);
        Assert.Equal(0m, s.HitPct);
        Assert.Equal(0m, s.Roi);
    }

    [Theory]
    [InlineData(10.5, true, 9.5)]     // 命中：赔率 − 1
    [InlineData(10.5, false, -1.0)]   // 未中：亏 1 个平注
    public void 平注盈亏(double odds, bool hit, double expect)
    {
        var pnl = PaperTradeMath.PnlOf(false, hit, (decimal)odds);
        Assert.Equal((decimal)expect, pnl!.Value);
    }

    [Fact]
    public void 撤注的盈亏必须是空_不能记成亏一注()
    {
        // 记成 -1 会让「撤注判得对不对」永远算不清：撤对了本来该省一笔，账上却记亏损。
        Assert.Null(PaperTradeMath.PnlOf(true, false, 6.0m));
        Assert.Null(PaperTradeMath.PnlOf(true, true, 6.0m));
    }

    [Fact]
    public void 未结算时盈亏也是空()
    {
        Assert.Null(PaperTradeMath.PnlOf(false, null, 6.0m));
    }

    [Fact]
    public void 样本不足150注时Enough为false()
    {
        var rows = Enumerable.Range(0, 149).Select(_ => Row("BQ", 5m, false)).ToList();
        var report = new PaperReport(14, DateTime.Today.AddDays(-13), DateTime.Today, 1,
            PaperTradeMath.Slice("全部", rows), Array.Empty<PaperSlice>(),
            Array.Empty<PaperSlice>(), Array.Empty<PaperSlice>());

        Assert.False(report.Enough);
        Assert.Equal(0m, report.Overall.Roi);   // 149 注全不中，回报 0
    }

    [Fact]
    public void 样本到150注才算够()
    {
        var rows = Enumerable.Range(0, 150).Select(_ => Row("BQ", 5m, false)).ToList();
        var report = new PaperReport(14, DateTime.Today.AddDays(-13), DateTime.Today, 1,
            PaperTradeMath.Slice("全部", rows), Array.Empty<PaperSlice>(),
            Array.Empty<PaperSlice>(), Array.Empty<PaperSlice>());

        Assert.True(report.Enough);
    }
}
