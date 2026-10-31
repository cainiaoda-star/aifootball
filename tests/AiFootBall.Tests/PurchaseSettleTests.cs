using AiFootBall.Core.Models;
using Xunit;

namespace AiFootBall.Tests;

public class PurchaseSettleTests
{
    [Fact]
    public void Plain_spf_ticket_counts_rang_sheng_as_a_home_win()
    {
        var line = new PurchaseLineRecord { Option1 = "让胜", Handicap = -1m };
        var score = new PurchaseSettleMath.MatchScore { Home = 1, Away = 0, Handicap = -1m };

        var plain = PurchaseSettleMath.EvaluateLine("胜平负", line, score);
        var handicap = PurchaseSettleMath.EvaluateLine("让球胜平负", line, score);

        Assert.True(plain.Hit);
        Assert.False(handicap.Hit);
    }

    [Fact]
    public void Plain_spf_draw_does_not_count_as_a_home_win()
    {
        var line = new PurchaseLineRecord { Option1 = "让胜" };
        var score = new PurchaseSettleMath.MatchScore { Home = 1, Away = 1 };

        var plain = PurchaseSettleMath.EvaluateLine("胜平负", line, score);

        Assert.False(plain.Hit);
    }

    [Fact]
    public void Score_ticket_mislabeled_as_score_still_settles_rang_sheng_on_the_handicap()
    {
        var line = new PurchaseLineRecord { Option1 = "让胜", Handicap = -1m };
        var score = new PurchaseSettleMath.MatchScore { Home = 2, Away = 0, Handicap = -1m };

        var hit = PurchaseSettleMath.EvaluateLine("全场比分", line, score);

        Assert.True(hit.Hit);
    }
}
