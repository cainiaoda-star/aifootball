using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

public class BdHandicapTests
{
    [Fact]
    public void ZeroHandicap_MatchesDevigOdds()
    {
        var devig = HitRateMath.Devig(1.20m, 5.99m, 10.35m)!.Value;
        var p = HandicapMath.FromOdds(1.20m, 5.99m, 10.35m, 3.0m, 0m)!.Value;
        Assert.InRange(p.Win, devig.Home - 0.03m, devig.Home + 0.03m);
        Assert.InRange(p.Loss, devig.Away - 0.03m, devig.Away + 0.03m);
    }

    [Fact]
    public void DeeperHandicap_LowersWinRaisesLoss()
    {
        var p0 = HandicapMath.FromOdds(1.20m, 5.99m, 10.35m, 3.0m, 0m)!.Value;
        var p2 = HandicapMath.FromOdds(1.20m, 5.99m, 10.35m, 3.0m, -2m)!.Value;
        Assert.True(p2.Win < p0.Win);
        Assert.True(p2.Loss > p0.Loss);
        Assert.Equal(1m, Math.Round(p2.Win + p2.Draw + p2.Loss, 6));
    }

    [Theory]
    [InlineData(4, 1, -2, '3')]
    [InlineData(3, 1, -2, '1')]
    [InlineData(2, 1, -2, '0')]
    [InlineData(1, 1, 1, '3')]
    [InlineData(0, 1, 1, '1')]
    public void Result_UsesHandicap(int home, int away, int hcp, char expected)
        => Assert.Equal(expected, HandicapMath.Result(home, away, hcp));

    [Fact]
    public void Partick_MinusTwo_PicksHandicapOption_NotPlainHomeWin()
    {
        // 2026-10-31 北单 157 帕尔蒂克蓟花 vs 格里诺克摩顿：让 -2。
        // 平均欧赔 1.20 / 5.99 / 10.35 是不让球价，北单 SP 让胜 9.55 / 让平 14.70 / 让负 1.20。
        var rec = new MatchRecommendation
        {
            Board = Boards.Bd, MatchNo = "157", Handicap = -2m, RecResult = '3',
            SignalDesc = "主胜水位 1.20 低水热门，照发"
        };
        RecommendationSelection.ApplyBdHandicap(rec, 1.20m, 5.99m, 10.35m, 3.0m, 9.55m, 14.70m, 1.20m);

        Assert.NotNull(rec.RecHandicap);
        Assert.NotNull(rec.HandicapProbability);
        Assert.StartsWith("北单让-2：", rec.SignalDesc);
        Assert.Contains("不让球参考（北单不卖）", rec.SignalDesc);
        Assert.DoesNotContain("不进精选", rec.SignalDesc);
        var sp = rec.RecHandicap switch { '3' => 9.55m, '1' => 14.70m, _ => 1.20m };
        Assert.Equal(sp, rec.HandicapOdds);
    }

    [Fact]
    public void ParseBdSpf_ReadsHandicapSp_NotAverageEuro()
    {
        const string html = """
            <table><tr class="vs_lines" value="{index:'157',rangqiuNum:'-2',disabled:'no'}" fid="1421080">
            <td value="{'index':157,'disabled':0}">
              <span class="sp_w35 eng pjoz">1.20</span><span class="sp_w35 eng pjoz">5.99</span><span class="sp_w35 eng pjoz">10.35</span>
            </td>
            <td class="h_br"><label class="label_n vs_options">&nbsp;<input type="checkbox" class="chbox" value="胜" /><span class="sp_value eng">9.55</span></label></td>
            <td class="h_br"><label class="label_n">&nbsp;<input type="checkbox" class="chbox" value="平" /><span class="sp_value eng">14.70</span></label></td>
            <td class="h_br"><label class="label_n">&nbsp;<input type="checkbox" class="chbox" value="负" /><span class="sp_value eng">1.20</span></label></td>
            </tr></table>
            """;
        var map = new Dictionary<long, BdMarketOdds>();
        BettingCardSource.ParseBdSpf(html, map);
        Assert.True(map.TryGetValue(1421080, out var sp));
        Assert.Equal(9.55m, sp!.Home);
        Assert.Equal(14.70m, sp.Draw);
        Assert.Equal(1.20m, sp.Away);
    }

    [Fact]
    public void ParseBdHistory_SettledRowKeepsWinningSp_LiveRowHasNoResult()
    {
        const string html = """
            <table>
            <tr class="vs_lines" value="{index:'1',leagueName:'亚冠精英联赛',homeTeam:'大田韩亚市民',guestTeam:'神户胜利船',endTime:'2026-10-27 17:50',rangqiuNum:'0',disabled:'yes'}" fid="1473992">
            <td><span class="sp_w35 eng pjoz">2.27</span><span class="sp_w35 eng pjoz">3.31</span><span class="sp_w35 eng pjoz">2.88</span>
                <span class="asianhand">平手/半球</span></td>
            <td class="h_br"><a href="#" title="析" class="red eng">0:1</a></td>
            <td><label><input type="checkbox" value="胜" disabled/>--</label></td>
            <td><label><input type="checkbox" value="平" disabled/>--</label></td>
            <td><label><input type="checkbox" value="负" disabled/><span class="sp_value eng red">2.69</span></label></td>
            </tr>
            <tr class="vs_lines" value="{index:'157',endTime:'2026-10-31 22:40',rangqiuNum:'-2',disabled:'no'}" fid="1421080">
            <td><span class="sp_w35 eng pjoz">1.20</span><span class="sp_w35 eng pjoz">5.99</span><span class="sp_w35 eng pjoz">10.35</span></td>
            <td class="h_br"><a href="#" title="析" class="red eng">VS</a></td>
            <td><label><input type="checkbox" value="胜" /><span class="sp_value eng">9.55</span></label></td>
            <td><label><input type="checkbox" value="平" /><span class="sp_value eng">14.70</span></label></td>
            <td><label><input type="checkbox" value="负" /><span class="sp_value eng">1.20</span></label></td>
            </tr>
            </table>
            """;
        var rows = BettingCardSource.ParseBdHistory(html, 26108);

        Assert.Equal(2, rows.Count);
        var done = rows[0];
        Assert.Equal((26108, 1, 1473992L), (done.Expect, done.MatchNo, done.MatchId!.Value));
        Assert.Equal(0m, done.Handicap);
        Assert.Equal((2.27m, 3.31m, 2.88m), (done.AvgHome!.Value, done.AvgDraw!.Value, done.AvgAway!.Value));
        Assert.Equal("平手/半球", done.AsianHand);
        Assert.Equal((0, 1), (done.HomeScore!.Value, done.AwayScore!.Value));
        Assert.Equal("0", done.ResultCode);
        Assert.Equal(2.69m, done.ResultSp);

        var live = rows[1];
        Assert.Equal(-2m, live.Handicap);
        Assert.Null(live.HomeScore);
        Assert.Null(live.ResultCode);
        Assert.Null(live.ResultSp);
    }

    [Fact]
    public void Bd_Featured_UsesHandicapProbability()
    {
        var rec = new MatchRecommendation
        {
            Board = Boards.Bd, MatchNo = "1", RecResult = '3', SignalLevel = 3, HitProbability = 80m,
            RecHandicap = '3', HandicapProbability = 40m
        };
        Assert.False(RecommendationSelection.QualifiesForFeatured(rec));
        rec.HandicapProbability = 60m;
        Assert.True(RecommendationSelection.QualifiesForFeatured(rec));

        rec.Handicap = -1m;
        Assert.True(RecommendationSelection.QualifiesForFeatured(rec));

        rec.Handicap = -2m;
        Assert.False(RecommendationSelection.QualifiesForFeatured(rec));
        rec.Handicap = 1.5m;
        Assert.False(RecommendationSelection.QualifiesForFeatured(rec));

        rec.Handicap = -2m;
        rec.TotalLine = 3.0m;
        Assert.True(RecommendationSelection.QualifiesForFeatured(rec));
    }

    [Fact]
    public void DeepHandicap_WithoutTotalLine_SaysItIsHeld()
    {
        var rec = new MatchRecommendation
        {
            Board = Boards.Bd, MatchNo = "157", Handicap = -2m, SignalDesc = "主胜"
        };
        RecommendationSelection.ApplyBdHandicap(rec, 1.20m, 5.99m, 10.35m, null, 9.55m, 14.70m, 1.20m);
        Assert.Contains("不进精选", rec.SignalDesc);
        Assert.False(RecommendationSelection.QualifiesForFeatured(rec));
    }

    [Fact]
    public void Jc_Handicap_List_Keeps_55_And_Drops_A_Deep_Line_Without_A_Total()
    {
        var low = Hcp("周日001", '3', 54.9m, -1m, 2.5m);
        var ok = Hcp("周日002", '1', 55m, -1m, 2.5m);
        var deep = Hcp("周日003", '3', 70m, -2m, null);
        var deepWithLine = Hcp("周日004", '0', 61m, -2m, 3.25m);

        var shown = RecommendationSelection.ForJcHandicap(new[] { low, ok, deep, deepWithLine });

        Assert.Equal(new[] { "周日001", "周日002", "周日004" }, shown.Select(x => x.MatchNo).ToArray());
        Assert.Equal('1', shown.Single(x => x.MatchNo == "周日002").RecHandicap);
    }

    [Fact]
    public void Jc_Handicap_Does_Not_Publish_An_ALeague_Away_Cover()
    {
        var aleague = new MatchRecommendation
        {
            Board = Boards.Jc, MatchNo = "周日010", LeagueName = "澳超", Handicap = 0m
        };
        RecommendationSelection.ApplyJcHandicap(aleague, 6.0m, 4.2m, 1.40m, 2.5m, 5.5m, 3.8m, 1.45m);
        Assert.Null(aleague.RecHandicap);

        var epl = new MatchRecommendation
        {
            Board = Boards.Jc, MatchNo = "周日011", LeagueName = "英超", Handicap = 0m
        };
        RecommendationSelection.ApplyJcHandicap(epl, 6.0m, 4.2m, 1.40m, 2.5m, 5.5m, 3.8m, 1.45m);
        Assert.Equal('0', epl.RecHandicap);
        Assert.Equal(1.45m, epl.HandicapOdds);
        Assert.True(epl.HandicapProbability >= 55m);
        Assert.Single(RecommendationSelection.ForJcHandicap(new[] { epl }));
    }

    [Fact]
    public void Jc_Draw_Is_Issued_Without_Joining_The_55_List()
    {
        var shaped = new MatchRecommendation
        {
            Board = Boards.Jc,
            MatchNo = "周日020",
            RecResult = '1',
            SignalLevel = 1,
            Confidence = 45m,
            HitProbability = 37.2m,
            SignalDesc = "均势看平：主客均≥2.20，平局 3.10（平局回测偏低，不进精选）"
        };
        var model = new MatchRecommendation
        {
            Board = Boards.Jc,
            MatchNo = "周日021",
            RecResult = '1',
            SignalLevel = 1,
            Confidence = 41m,
            SignalDesc = "无强信号；模型看平，不进精选"
        };
        var fallback = new MatchRecommendation
        {
            Board = Boards.Jc,
            MatchNo = "周日022",
            RecResult = '1',
            IsOddsFallback = true,
            SignalDesc = "赔率兜底：平局赔率最低(3.05)，无强信号"
        };

        Assert.True(RecommendationSelection.IsIssuedDraw(shaped));
        Assert.True(RecommendationSelection.IsIssuedDraw(model));
        Assert.False(RecommendationSelection.IsIssuedDraw(fallback));
        Assert.False(RecommendationSelection.QualifiesForFeatured(shaped));
        Assert.Equal(new[] { "周日020", "周日021" },
            RecommendationSelection.ForJcDraw(new[] { fallback, model, shaped }).Select(x => x.MatchNo).ToArray());
    }

    [Fact]
    public void Jc_Calculated_Publishes_Spf_Below_55_And_Skips_ALeague_Away()
    {
        var home = new MatchRecommendation
        {
            Board = Boards.Jc, MatchNo = "周日030", RecResult = '3',
            SignalLevel = 1, HitProbability = 48.2m
        };
        var draw = new MatchRecommendation
        {
            Board = Boards.Jc, MatchNo = "周日031", RecResult = '1',
            SignalLevel = 1, HitProbability = 37m, IsOddsFallback = true
        };
        var away = new MatchRecommendation
        {
            Board = Boards.Jc, MatchNo = "周日032", LeagueName = "澳超", RecResult = '0',
            SignalLevel = 1, HitProbability = 62m
        };
        var featured = new MatchRecommendation
        {
            Board = Boards.Jc, MatchNo = "周日033", RecResult = '3',
            SignalLevel = 2, HitProbability = 58m, IsFeatured = true
        };

        Assert.Empty(RecommendationSelection.ForDisplay(Boards.Jc, new[] { home }));
        var shown = RecommendationSelection.ForJcCalculated(new[] { away, draw, home, featured });
        Assert.Equal(new[] { "周日030", "周日033" }, shown.Select(x => x.MatchNo).ToArray());
        Assert.DoesNotContain(shown, x => x.IsOddsFallback);
        Assert.False(shown.Single(x => x.MatchNo == "周日030").IsFeatured);
        Assert.True(shown.Single(x => x.MatchNo == "周日033").IsFeatured);
    }

    [Fact]
    public void Bd_Calculated_Publishes_Below_55_Including_Handicap_Draw()
    {
        var low = Hcp("1", '1', 42m, -1m, 2.5m);
        low.RecResult = '3';
        low.HitProbability = 40m;
        var ok = Hcp("2", '0', 61m, -1m, 2.5m);
        ok.RecResult = '0';
        ok.HitProbability = 58m;
        var deep = Hcp("3", '3', 70m, -2m, null);
        deep.RecResult = '3';
        var away = new MatchRecommendation
        {
            Board = Boards.Bd, MatchNo = "4", LeagueName = "澳超", RecResult = '0',
            HitProbability = 70m, RecHandicap = '3', HandicapProbability = 48m,
            Handicap = 0m, TotalLine = 2.5m
        };

        var shown = RecommendationSelection.ForBdCalculated(new[] { low, ok, deep, away });
        Assert.Equal(new[] { "1", "2", "4" }, shown.Select(x => x.MatchNo).ToArray());
        Assert.Equal('1', shown.Single(x => x.MatchNo == "1").RecHandicap);
        Assert.False(shown.Single(x => x.MatchNo == "1").IsFeatured);
        Assert.True(shown.Single(x => x.MatchNo == "2").IsFeatured);

        var plain = RecommendationSelection.ForBdPlain(new[] { low, ok, away });
        Assert.Equal(new[] { "1", "2" }, plain.Select(x => x.MatchNo).ToArray());
    }

    static MatchRecommendation Hcp(string no, char side, decimal prob, decimal line, decimal? total) => new()
    {
        Board = Boards.Jc,
        MatchNo = no,
        RecHandicap = side,
        HandicapProbability = prob,
        Handicap = line,
        TotalLine = total
    };
}
