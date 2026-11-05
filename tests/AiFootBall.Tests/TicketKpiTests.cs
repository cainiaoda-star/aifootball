using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 票面台账的结算验证。票不等于单场推荐：一张票里哪几腿中了、
/// 展开成几注、奖金够不够本金，是另一个数学模型，必须单独锁住。
/// 复用 PurchaseSettleMath.CalculatePrize，所以这里同时也在验证
/// 「系统算的奖」与「手工购票算的奖」是同一套口径。
/// </summary>
public sealed class TicketKpiTests
{
    private static TicketRecord Ticket(string pass, decimal[] odds, bool?[] hits, decimal bet)
        => new()
        {
            PassType = pass,
            BetAmount = bet,
            Multiple = 1,
            Legs = odds.Select((o, i) => new TicketLegRecord
            {
                Seq = i + 1,
                Odds = o,
                LineHit = hits[i]
            }).ToList()
        };

    [Fact]
    public void 单关全中_奖金等于两元乘水位()
    {
        var t = Ticket("单关", new[] { 2.00m }, new bool?[] { true }, 2m);
        var r = TicketPlanBuilder.Settle(t);

        Assert.NotNull(r);
        Assert.True(r!.Value.Won);
        Assert.Equal(4.00m, r.Value.Prize);
        Assert.True(t.Backed == false); // 未结算前不臆造，回本要靠 PrizeAmount 回填
    }

    [Fact]
    public void 单关未中_零奖金且不算中奖()
    {
        var r = TicketPlanBuilder.Settle(Ticket("单关", new[] { 2.00m }, new bool?[] { false }, 2m));

        Assert.NotNull(r);
        Assert.False(r!.Value.Won);
        Assert.Equal(0m, r.Value.Prize);
    }

    [Fact]
    public void 有腿尚未判定_拒绝结算半张票()
    {
        var t = Ticket("2串1", new[] { 2.00m, 2.00m }, new bool?[] { true, null }, 2m);
        Assert.Null(TicketPlanBuilder.Settle(t));
    }

    [Fact]
    public void 二串一全包三腿_中两腿只中一注()
    {
        // 3 腿全包 2串1 = C(3,2) = 3 注，本金 6 元
        var r = TicketPlanBuilder.Settle(Ticket("2串1",
            new[] { 2.00m, 2.00m, 2.00m }, new bool?[] { true, true, false }, 6m));

        Assert.NotNull(r);
        Assert.True(r!.Value.Won);
        Assert.Equal(2, r.Value.HitCount);
        Assert.Equal(8.00m, r.Value.Prize);   // 2 元 × 2.0 × 2.0
    }

    [Fact]
    public void 二串一全包三腿_全中三注()
    {
        var r = TicketPlanBuilder.Settle(Ticket("2串1",
            new[] { 2.00m, 2.00m, 2.00m }, new bool?[] { true, true, true }, 6m));

        Assert.NotNull(r);
        Assert.Equal(24.00m, r!.Value.Prize);  // 3 注 × 8 元
    }

    [Fact]
    public void 八腿六七八串一_全中按28加8加1注展开()
    {
        var odds = Enumerable.Repeat(2.00m, 8).ToArray();
        var hits = Enumerable.Repeat((bool?)true, 8).ToArray();
        // 本金：C(8,6)+C(8,7)+C(8,8) = 28+8+1 = 37 注 → 74 元
        var r = TicketPlanBuilder.Settle(Ticket("6串1#7串1#8串1", odds, hits, 74m));

        Assert.NotNull(r);
        Assert.True(r!.Value.Won);
        // 6串1: 28 注 × 2×2^6 = 3584；7串1: 8 注 × 2×2^7 = 2048；8串1: 1 注 × 2×2^8 = 512
        Assert.Equal(6144.00m, r.Value.Prize);
    }

    [Fact]
    public void 八腿票中六腿_有奖但回不了本()
    {
        // 8 腿只中 6 腿：仅 1 注 6串1 中奖。这正是 8 腿票的问题——
        // 「中奖」很常见，「回本」很难，两者必须分开统计。
        var odds = Enumerable.Repeat(1.50m, 8).ToArray();
        var hits = new bool?[] { true, true, true, true, true, true, false, false };
        var r = TicketPlanBuilder.Settle(Ticket("6串1#7串1#8串1", odds, hits, 74m));

        Assert.NotNull(r);
        Assert.True(r!.Value.Won, "中六腿应当有奖");
        Assert.Equal(22.78m, r.Value.Prize);       // 2 × 1.5^6
        Assert.True(r.Value.Prize < 74m, "奖金 22.78 元不够 74 元本金，属于中奖不回本");
    }
}
