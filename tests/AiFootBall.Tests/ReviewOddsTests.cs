using AiFootBall.Core.Models;
using Xunit;
using AiFootBall.Data;
using AiFootBall.Sources;

namespace AiFootBall.Tests;

public class ReviewOddsTests
{
    [Fact]
    public void Beijing_pages_keep_the_settled_price()
    {
        var map = new Dictionary<long, BdMarketOdds>();
        BettingCardSource.ParseBdSpf("""
            <tr fid="1454383">
              <label><input value="胜" /><span class="sp_value">2.69</span></label>
              <label><input value="平" />--</label>
              <label><input value="负" />--</label>
            </tr>
            """, map);
        BettingCardSource.ParseBdScores("""
            <tr fid="1454383"></tr>
            <tr class="hide_b">
              <label><input value="2:0" /><span class="f_bf">2:0</span><span class="sp_value">9.41</span></label>
              <label><input value="1:0" />(--)</label>
            </tr>
            """, map);
        BettingCardSource.ParseBdHalf("""
            <tr fid="1454383">
              <label><input value="胜-胜" /><span class="sp_value">2.90</span></label>
              <label><input value="平-胜" />--</label>
            </tr>
            """, map);

        var row = map[1454383];
        Assert.Equal(2.69m, row.Home);
        Assert.Null(row.Draw);
        Assert.Equal(9.41m, row.Scores["2:0"]);
        Assert.Equal(2.90m, row.HalfFull["胜/胜"]);
    }

    [Fact]
    public void Review_line_includes_pick_half_and_final_score()
    {
        var item = new FinishedSummaryItem
        {
            RecResult = '3',
            IsCoverAll = false,
            FullHome = 2,
            FullAway = 0,
            RecScore1 = "1:0"
        };
        var odds = new Dictionary<string, decimal>
        {
            ["SPF|胜"] = 2.69m,
            ["BQ|胜/胜"] = 2.90m,
            ["BF|2:0"] = 9.41m,
            ["BF|1:0"] = 6.50m
        };

        var notes = ReviewOdds.Build(item, odds);

        Assert.Equal(new[] { "胜：2.69", "胜/胜：2.90", "比分 1:0：6.50", "最终比分 2:0：9.41" }, notes);
    }

    [Fact]
    public void Actual_half_full_odds_are_kept_when_the_pick_was_the_other_side()
    {
        var item = new FinishedSummaryItem
        {
            RecResult = '0',
            IsCoverAll = false,
            HalfFull = "平/平",
            FullHome = 1,
            FullAway = 1
        };
        var odds = new Dictionary<string, decimal>
        {
            ["BQ|平/平"] = 5.70m,
            ["BF|1:1"] = 8.38m
        };

        var notes = ReviewOdds.Build(item, odds);

        Assert.Contains("平/平：5.70", notes);
        Assert.Contains("最终比分 1:1：8.38", notes);
    }
}
