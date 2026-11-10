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
        bool voided = false, bool counterHigh = false, bool overConfident = false, bool lowConf = false,
        long matchId = 0, long tradeId = 0, DateTime? frozenAt = null)
        => new()
        {
            TradeId = tradeId,
            MatchId = matchId,
            FrozenAt = frozenAt ?? new DateTime(2026, 11, 8, 20, 40, 0),
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
    public void 同场同玩法换档只算最后发布的那一注()
    {
        // 11-08 曼联 vs 维拉 的真实形态：21:45 发「胜平 13.00」（未中），22:25 重算换成「平平 6.15」（命中）。
        // 用户 2026-11-09 拍板：前一条不算一注。算两注会让战绩凭空多记一个未中，
        // 这正是 8 注 1.294 与 7 注 1.479 两个数字的来历。
        var rows = new List<PaperTradeRow>
        {
            Row("BQ", 13.00m, false, matchId: 9001, tradeId: 19, frozenAt: new DateTime(2026, 11, 8, 21, 45, 52)),
            Row("BQ", 6.15m, true, matchId: 9001, tradeId: 22, frozenAt: new DateTime(2026, 11, 8, 22, 25, 36)),
            Row("BQ", 5.00m, false, matchId: 9002, tradeId: 30, frozenAt: new DateTime(2026, 11, 8, 22, 25, 40))
        };

        var final = PaperTradeMath.FinalVersion(rows);
        var s = PaperTradeMath.Slice("BQ", final);

        Assert.Equal(2, final.Count);                       // 两场，两注
        Assert.Equal(1, PaperTradeMath.SupersededCount(rows));
        Assert.Equal(2, s.Settled);
        Assert.Equal(1, s.Hits);
        Assert.Equal(6.15m, s.Return);                      // 旧档 13.00 那条不进来
        Assert.Equal(3.075m, s.Roi);                        // 6.15 / 2
    }

    [Fact]
    public void 换档后终版被撤注_仍按撤注计而不是回退到旧档()
    {
        // 终版那注被 turn-guard 撤掉时，旧档不能顶上来当一注继续算——那等于凭空复活一条已作废的票。
        var rows = new List<PaperTradeRow>
        {
            Row("BQ", 8.00m, false, matchId: 9100, tradeId: 1, frozenAt: new DateTime(2026, 11, 8, 21, 45, 0)),
            Row("BQ", 9.00m, null, voided: true, matchId: 9100, tradeId: 2, frozenAt: new DateTime(2026, 11, 8, 22, 25, 0))
        };

        var final = PaperTradeMath.FinalVersion(rows);
        var s = PaperTradeMath.Slice("BQ", final);

        Assert.Single(final);
        Assert.Equal(1, s.Voided);
        Assert.Equal(0, s.Settled);      // 分母 0，不是把旧档补回来变成 1
    }

    [Fact]
    public void 同场不同玩法不算换档()
    {
        // 同场跨玩法去重是引擎的事；到了台账里它们本来就是两注，不能被当成换档吃掉一条。
        var rows = new List<PaperTradeRow>
        {
            Row("BQ", 6.00m, true, matchId: 9200, tradeId: 1),
            Row("JQ", 3.50m, false, matchId: 9200, tradeId: 2)
        };

        var final = PaperTradeMath.FinalVersion(rows);

        Assert.Equal(2, final.Count);
        Assert.Equal(0, PaperTradeMath.SupersededCount(rows));
    }

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

    [Fact]
    public void 撤注的注照常算命中率作为对照但不算钱()
    {
        // 撤注判据从上线到今天一次都没被检验过，原因是报表把撤注行整个排除在分母外，
        // 「撤掉的注后来中没中」在任何页面上都看不到。
        // 这条测试钉住新增的对照组口径：撤注行不进主分母（不算钱），
        // 但要单独数出 VoidedSettled / VoidedHits，否则永远无法回答「撤对了没有」。
        var rows = new List<PaperTradeRow>
        {
            Row("BQ", 6.15m, true,  voided: true),   // 撤了，结果中了 → 撤错
            Row("BQ", 13.0m, false, voided: true),   // 撤了，没中 → 撤对
            Row("BQ", 5.75m, true),                  // 正常出注，中了
            Row("BQ", 6.20m, false)                  // 正常出注，没中
        };

        var s = PaperTradeMath.Slice("BQ", rows);

        // 主口径：只有未撤注的两注参与，撤注一注都不进
        Assert.Equal(4, s.Total);
        Assert.Equal(2, s.Voided);
        Assert.Equal(2, s.Settled);
        Assert.Equal(1, s.Hits);
        Assert.Equal(50m, s.HitPct);
        Assert.Equal(2m, s.Stake);

        // 对照组：撤注的两注都在，命中一注
        Assert.Equal(2, s.VoidedSettled);
        Assert.Equal(1, s.VoidedHits);
        Assert.Equal(50m, s.VoidedHitPct);
    }

    [Fact]
    public void 撤注但还没赛果的不进对照组()
    {
        // 撤注时比赛还没踢完是常态。拿「没有赛果」当「没有命中」会把对照命中率压低，
        // 于是看起来「撤掉的都是错的」——那是数据缺口造出来的结论。
        var rows = new List<PaperTradeRow>
        {
            Row("JQ", 4.2m, null, voided: true),
            Row("JQ", 4.5m, true, voided: true)
        };

        var s = PaperTradeMath.Slice("JQ", rows);

        Assert.Equal(2, s.Voided);
        Assert.Equal(1, s.VoidedSettled);
        Assert.Equal(1, s.VoidedHits);
        Assert.Equal(100m, s.VoidedHitPct);
    }

    [Fact]
    public void 没有撤注样本时对照组命中率为0且不除零()
    {
        var s = PaperTradeMath.Slice("BF", new List<PaperTradeRow> { Row("BF", 10.5m, true) });

        Assert.Equal(0, s.VoidedSettled);
        Assert.Equal(0m, s.VoidedHitPct);
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
