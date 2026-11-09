using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

public class SfcTicketTests
{
    private static HashSet<char> One(char bit) => new() { bit };

    private static List<(int MatchNo, HashSet<char> Options)> Singles(string result)
    {
        return result.Select((ch, i) => (i + 1, One(ch))).ToList();
    }

    [Fact]
    public void Fourteen_single_is_one_bet_at_two_yuan()
    {
        var counts = Enumerable.Repeat(1, 14).ToList();
        Assert.Equal(1, SfcTicketMath.FourteenBets(counts));

        var req = Ticket("胜负彩", "26155", counts.Select(_ => "胜").ToList());
        Assert.Null(SfcTicketMath.Normalize(req));
        Assert.Equal(1, req.BetCount);
        Assert.Equal(2m, req.BetAmount);
        Assert.Null(req.PassType);
    }

    [Fact]
    public void One_match_with_two_options_doubles_the_fourteen_stake()
    {
        var opts = Enumerable.Repeat("胜", 14).ToList();
        opts[3] = "胜,平";
        var req = Ticket("胜负彩", "26155", opts);
        Assert.Null(SfcTicketMath.Normalize(req));
        Assert.Equal(2, req.BetCount);
        Assert.Equal(4m, req.BetAmount);
    }

    [Fact]
    public void Renjiu_ten_singles_is_ten_bets()
    {
        var req = Ticket("任选九", "26155", Enumerable.Repeat("负", 10).ToList());
        Assert.Null(SfcTicketMath.Normalize(req));
        Assert.Equal(10, req.BetCount);
        Assert.Equal(20m, req.BetAmount);
    }

    [Fact]
    public void Renjiu_ten_with_one_double_is_nineteen_bets()
    {
        var counts = Enumerable.Repeat(1, 10).ToList();
        counts[0] = 2;
        Assert.Equal(19, SfcTicketMath.RenjiuBets(counts));
    }

    [Fact]
    public void Renjiu_rejects_fewer_than_nine()
    {
        var req = Ticket("任选九", "26155", Enumerable.Repeat("胜", 8).ToList());
        Assert.Equal("任选九要 9 到 14 场", SfcTicketMath.Normalize(req));
    }

    [Fact]
    public void All_correct_single_wins_first_prize_only()
    {
        const string result = "33300030301303";
        var wins = SfcTicketMath.CountWins(Singles(result), result);
        Assert.Equal(1, wins.Fourteen);
        Assert.Equal(0, wins.Thirteen);
        Assert.Equal(1059984m, SfcTicketMath.PrizeYuan(false, wins, 1059984m, 7854m, 5498m, 1));
    }

    [Fact]
    public void Exactly_one_wrong_wins_second_prize()
    {
        const string result = "33300030301303";
        var lines = Singles(result);
        lines[4] = (5, One('3'));
        var wins = SfcTicketMath.CountWins(lines, result);
        Assert.Equal(0, wins.Fourteen);
        Assert.Equal(1, wins.Thirteen);
        Assert.Equal(7854m, SfcTicketMath.PrizeYuan(false, wins, 1059984m, 7854m, 5498m, 1));
    }

    [Fact]
    public void Two_wrongs_miss_both_fourteen_prizes()
    {
        const string result = "33300030301303";
        var lines = Singles(result);
        lines[0] = (1, One('0'));
        lines[1] = (2, One('0'));
        var wins = SfcTicketMath.CountWins(lines, result);
        Assert.Equal(0, wins.Fourteen);
        Assert.Equal(0, wins.Thirteen);
    }

    [Fact]
    public void Ten_correct_renjiu_wins_ten_bets()
    {
        const string result = "33300030301303";
        var lines = Singles(result).Take(10).ToList();
        var wins = SfcTicketMath.CountWins(lines, result);
        Assert.Equal(10, wins.Renjiu);
        Assert.Equal(54980m, SfcTicketMath.PrizeYuan(true, wins, 0, 0, 5498m, 1));
    }

    [Fact]
    public void Parser_reads_26153_style_page()
    {
        var spans = string.Concat("33300030301303".Select(c => $"<span class=\"cfont5 \">{c}</span>"));
        var html = $"""
            <div>{spans}</div>
            <table>
            <tr><td>一等奖</td><td>6</td><td>1,059,984</td></tr>
            <tr><td> 二等奖</td><td>347</td><td>7,854</td></tr>
            <tr><td>任九</td><td>1954</td><td>5,498</td></tr>
            </table>
            """;
        var draw = SfcDrawParser.Parse(html, "26153");
        Assert.NotNull(draw);
        Assert.Equal("33300030301303", draw!.Result);
        Assert.Equal(1059984m, draw.FirstPrize);
        Assert.Equal(7854m, draw.SecondPrize);
        Assert.Equal(5498m, draw.RenjiuPrize);
        Assert.True(draw.Complete);
    }

    [Fact]
    public void Parser_waits_when_the_draw_is_short()
    {
        var html = """<span class="cfont5 ">3</span><td>一等奖</td><td>0</td><td>0</td>""";
        Assert.Null(SfcDrawParser.Parse(html, "26155"));
    }

    private static PurchaseUpsertRequest Ticket(string play, string expect, List<string> options) => new()
    {
        Board = Boards.Sfc,
        Expect = expect,
        PlayType = play,
        Multiple = 1,
        Lines = options.Select((opt, i) => new PurchaseLineRecord
        {
            MatchNo = (i + 1).ToString(),
            Option1 = opt
        }).ToList()
    };
}
