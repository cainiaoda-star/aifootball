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

    /// <summary>
    /// 2026-11-04 P1①：半全场手输「负负」对上 MatchScore 算出的「负/负」（带斜杠），
    /// 旧归一化不去斜杠 → 命中也判不中（票 #20023 的负负@5.8 / 胜胜@6.2）。
    /// </summary>
    [Fact]
    public void Half_full_without_slash_matches_slash_form()
    {
        var line = new PurchaseLineRecord { Option1 = "负负", Odds1 = 5.8m };
        var score = new PurchaseSettleMath.MatchScore { Home = 0, Away = 2, HalfHome = 0, HalfAway = 1 };

        var hit = PurchaseSettleMath.EvaluateLine("半全场", line, score);

        Assert.True(hit.Hit);
        Assert.Equal(5.8m, hit.Odds);
    }

    [Fact]
    public void Half_full_miss_when_full_time_direction_differs()
    {
        var line = new PurchaseLineRecord { Option1 = "胜胜", Odds1 = 6.2m };
        var score = new PurchaseSettleMath.MatchScore { Home = 2, Away = 1, HalfHome = 0, HalfAway = 0 };

        Assert.False(PurchaseSettleMath.EvaluateLine("半全场", line, score).Hit);
    }

    /// <summary>2026-11-04 P2④：「胜其它/平其它/负其它」此前被当普通比分字符串比对，永远不中。</summary>
    [Theory]
    [InlineData("胜其它", 4, 3, true)]   // 官方没有 4:3 档
    [InlineData("胜其它", 5, 4, true)]
    [InlineData("胜其它", 3, 1, false)]  // 3:1 在官方 28 档里
    [InlineData("胜其它", 4, 4, false)]  // 平局方向不对
    [InlineData("平其它", 4, 4, true)]
    [InlineData("平其它", 3, 3, false)]
    [InlineData("负其它", 3, 4, true)]
    [InlineData("负其它", 2, 5, false)]
    public void Other_score_options_hit_by_direction_and_official_list(string opt, int home, int away, bool expected)
    {
        var line = new PurchaseLineRecord { Option1 = opt, Odds1 = 90m };
        var score = new PurchaseSettleMath.MatchScore { Home = home, Away = away };

        var hit = PurchaseSettleMath.EvaluateLine("全场比分", line, score);

        Assert.Equal(expected, hit.Hit);
    }

    /// <summary>2026-11-04 P2⑤：总进球「7」档是 7 球及以上（8、9 球也算中），与 MilkPickMath 同口径。</summary>
    [Theory]
    [InlineData("7", 7, true)]
    [InlineData("7", 8, true)]
    [InlineData("7", 6, false)]
    [InlineData("7+", 9, true)]
    [InlineData("5", 5, true)]
    [InlineData("5", 6, false)]
    public void Goals_bucket_seven_means_seven_or_more(string opt, int goals, bool expected)
    {
        var line = new PurchaseLineRecord { Option1 = opt, Odds1 = 23m };
        var score = new PurchaseSettleMath.MatchScore { Home = goals, Away = 0 };

        Assert.Equal(expected, PurchaseSettleMath.EvaluateLine("总进球", line, score).Hit);
    }

    /// <summary>2026-11-04 P2⑥：过关留空 = 每行一注单关（前端口径），多行全中也得结出钱。</summary>
    [Fact]
    public void Empty_pass_type_settles_each_line_as_single()
    {
        var sizes = PurchaseSettleMath.ParsePassSizes(null, 3);
        Assert.Equal(new[] { 1 }, sizes);

        var legs = new List<(bool, decimal?)> { (true, 2.0m), (true, 3.0m), (false, 4.0m) };
        var prize = PurchaseSettleMath.CalculatePrize(legs, null, 1);

        Assert.Equal(10.0m, Math.Round(prize, 2)); // 2×2 + 2×3，未中的那注归零
    }
}
