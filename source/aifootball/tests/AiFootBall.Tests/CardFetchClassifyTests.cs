using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>2026-11-14 D：本地 HTML 样本三态；不抓外站。</summary>
public class CardFetchClassifyTests
{
    // 与 JcCardParseTests 同源结构
    private const string JcRow = """
        <table><tr data-fixtureid="1473930" data-matchnum="周三001" data-homesxname="大阪钢巴" data-awaysxname="布里兰"
            data-matchdate="2026-10-28" data-matchtime="18:00" data-rangqiu="-1" data-simpleleague="亚冠"
            data-processdate="2026-10-28">
          <td class="td td-rang"><p class="itm-rangA1" title="不让球">0</p></td>
          <td class="td td-betbtn">
            <div class="betbtn-row itm-rangB1">
              <p class="betbtn" data-type="nspf" data-value="3" data-sp="1.52"></p>
              <p class="betbtn" data-type="nspf" data-value="1" data-sp="3.95"></p>
              <p class="betbtn" data-type="nspf" data-value="0" data-sp="4.60"></p>
            </div>
          </td>
        </tr></table>
        """;

    private const string JcEmptyShell = """
        <html><body>
          <table class="bet-tb"><thead><tr><th>场次</th></tr></thead>
            <tbody><!-- 无 tr[data-matchnum] --></tbody>
          </table>
          <div class="td-betbtn"><div class="betbtn-row"></div></div>
        </body></html>
        """;

    private const string ChallengePage = """
        <html><body><h1>请完成安全验证</h1><p>Tencent EdgeOne</p></body></html>
        """;

    [Fact]
    public void D_Ok_When_Rows_Present()
    {
        var r = BettingCardSource.ClassifyJc(JcRow);
        Assert.Equal(CardFetchKind.Ok, r.Kind);
        Assert.True(r.ContainerPresent);
        Assert.True(r.Count >= 1);
    }

    [Fact]
    public void D2_NoSale_When_Container_Present_Zero_Rows()
    {
        Assert.True(BettingCardSource.HasJcContainer(JcEmptyShell));
        var r = BettingCardSource.ClassifyJc(JcEmptyShell);
        Assert.Equal(CardFetchKind.NoSale, r.Kind);
        Assert.Equal(0, r.Count);
        Assert.True(r.ContainerPresent);
    }

    [Fact]
    public void D3_FetchFailed_When_No_Container()
    {
        Assert.False(BettingCardSource.HasJcContainer(ChallengePage));
        var r = BettingCardSource.ClassifyJc(ChallengePage, "challenge");
        Assert.Equal(CardFetchKind.FetchFailed, r.Kind);
        Assert.False(r.ContainerPresent);
        Assert.NotEqual(CardFetchKind.NoSale, r.Kind);
    }

    [Fact]
    public void D3_EmptyHtml_Is_FetchFailed_Not_NoSale()
    {
        var r = BettingCardSource.ClassifyJc(null, "timeout");
        Assert.Equal(CardFetchKind.FetchFailed, r.Kind);
    }

    [Fact]
    public void D4_Partial_Message_When_Jc_Failed_Others_Ok()
    {
        var cards = new CardsCollectResult
        {
            Jc = BoardFetchResult.Failed("no-jc-container"),
            Bd = BoardFetchResult.Ok(new[] { new BettingCardRecord { Board = Boards.Bd, MatchNo = "1" } }),
            Sfc = BoardFetchResult.Ok(new[] { new BettingCardRecord { Board = Boards.Sfc, MatchNo = "01" } })
        };
        Assert.True(cards.SyncSuccess); // partial 仍 SUCCESS + 前缀
        Assert.StartsWith("[partial]", cards.SyncMessage);
        Assert.DoesNotContain("[no-sale]", cards.SyncMessage);
        Assert.Contains("containers=jc:0", cards.SyncMessage);
    }

    [Fact]
    public void D1_All_FetchFailed_Is_Failed_Status()
    {
        var cards = new CardsCollectResult
        {
            Jc = BoardFetchResult.Failed("a"),
            Bd = BoardFetchResult.Failed("b"),
            Sfc = BoardFetchResult.Failed("c")
        };
        Assert.False(cards.SyncSuccess);
        Assert.StartsWith("[fetch-failed]", cards.SyncMessage);
    }

    [Fact]
    public void All_NoSale_Message_Prefix()
    {
        var cards = new CardsCollectResult
        {
            Jc = BoardFetchResult.NoSale("jc-container-empty"),
            Bd = BoardFetchResult.NoSale("bd-container-empty"),
            Sfc = BoardFetchResult.NoSale("sfc-container-empty")
        };
        Assert.True(cards.SyncSuccess);
        Assert.StartsWith("[no-sale]", cards.SyncMessage);
        Assert.Contains("containers=jc:1;bd:1;sfc:1", cards.SyncMessage);
        Assert.Contains("jc=0", cards.SyncMessage);
    }

    [Fact]
    public void Bd_Container_From_Fid_Rows()
    {
        const string html = """
            <table><tr fid="123" value="index:'1',homeTeam:'A',guestTeam:'B',scheduleDate:'2026-11-14',disabled:'no'">
              <span class="pjoz">1.5</span><span class="pjoz">3.2</span><span class="pjoz">5.0</span>
            </tr></table>
            """;
        Assert.True(BettingCardSource.HasBdContainer(html));
        var r = BettingCardSource.ClassifyBd(html);
        Assert.Equal(CardFetchKind.Ok, r.Kind);
    }

    [Fact]
    public void Bd_EmptyShell_Is_NoSale()
    {
        const string html = """<div id="bjdc"><table class="bet-tb"><tbody></tbody></table></div>""";
        Assert.True(BettingCardSource.HasBdContainer(html));
        Assert.Equal(CardFetchKind.NoSale, BettingCardSource.ClassifyBd(html).Kind);
    }
}
