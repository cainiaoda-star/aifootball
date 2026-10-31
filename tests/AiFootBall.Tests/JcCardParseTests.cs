using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

public class JcCardParseTests
{
    // 取自 500 竞彩页：周三001 大阪钢巴 vs 布里兰，主让 1 球。
    // 第一行 data-type="nspf" 是不让球 1.52/3.95/4.60，第二行 data-type="spf" 是让 -1 的 2.65/3.50/2.15。
    private const string Row = """
        <table><tr data-fixtureid="1473930" data-matchnum="周三001" data-homesxname="大阪钢巴" data-awaysxname="布里兰"
            data-matchdate="2026-10-28" data-matchtime="18:00" data-rangqiu="-1" data-simpleleague="亚冠">
          <td class="td td-rang"><p class="itm-rangA1" title="不让球">0</p><p class="itm-rangA2" title="主队让1球"> -1</p></td>
          <td class="td td-betbtn">
            <div class="betbtn-row itm-rangB1">
              <p class="betbtn" data-type="nspf" data-value="3" data-sp="1.52"></p>
              <p class="betbtn" data-type="nspf" data-value="1" data-sp="3.95"></p>
              <p class="betbtn" data-type="nspf" data-value="0" data-sp="4.60"></p>
            </div>
            <div class="betbtn-row itm-rangB2">
              <p class="betbtn" data-type="spf" data-value="3" data-sp="2.65"></p>
              <p class="betbtn" data-type="spf" data-value="1" data-sp="3.50"></p>
              <p class="betbtn" data-type="spf" data-value="0" data-sp="2.15"></p>
            </div>
          </td>
        </tr></table>
        """;

    [Fact]
    public void PageNspf_Is_PlainSpf_And_PageSpf_Is_Handicap()
    {
        var card = Assert.Single(BettingCardSource.ParseJcCards(Row));

        Assert.Equal(-1m, card.Handicap);
        Assert.Equal(1.52m, card.SpfWin);
        Assert.Equal(4.60m, card.SpfLoss);
        Assert.Equal(2.65m, card.NspfWin);
        Assert.Equal(2.15m, card.NspfLoss);
    }

    [Fact]
    public void BettingCard_Uses_Plain_Spf()
    {
        var rec = Assert.Single(BettingCardSource.ParseJc(Row));

        Assert.Equal(1.52m, rec.SpfHome);
        Assert.Equal(3.95m, rec.SpfDraw);
        Assert.Equal(4.60m, rec.SpfAway);
    }
}
