using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 受注日进 MERGE 键（2026-11-10 加）。
///
/// 病根：jc / bd 的 Expect 从来不填，只有 sfc 填，于是
///   MERGE dbo.BettingCard ON t.Board = s.Board AND ISNULL(t.Expect,'') = ISNULL(s.Expect,'') AND t.MatchNo = s.MatchNo
/// 退化成 (Board, MatchNo)。而「周二001」每周都重号 —— 下周二同一编号的卡片会把上周二那行
/// 原地 UPDATE 掉，历史卡片凭空消失。2026-11-05 回测 11-04 六场欧冠只剩一场，就是这么丢的。
///
/// 附带：表上那条 UQ_BettingCard(Board, Expect, MatchNo) 以前因为 Expect 全 NULL 而形同虚设
/// （SQL Server 认为多个 NULL 互不相同），现在才开始真正拦重复。
/// </summary>
public class CardExpectKeyTests
{
    private const string JcRowA = """
        <table><tr data-fixtureid="1491201" data-matchnum="周二001" data-homesxname="伯顿" data-awaysxname="诺茨郡"
            data-matchdate="2026-11-11" data-matchtime="03:00" data-rangqiu="-1" data-simpleleague="英锦标赛"
            data-processdate="2026-11-10">
          <td class="td td-betbtn"><p class="betbtn" data-type="nspf" data-value="3" data-sp="2.10"></p>
          <p class="betbtn" data-type="nspf" data-value="1" data-sp="3.20"></p>
          <p class="betbtn" data-type="nspf" data-value="0" data-sp="3.05"></p></td>
        </tr></table>
        """;

    // 同一编号，下一周的受注日。修复前这两行 MERGE 到同一行，第一行被覆盖。
    private const string JcRowBSameNo = """
        <table><tr data-fixtureid="1500001" data-matchnum="周二001" data-homesxname="另一队" data-awaysxname="再一队"
            data-matchdate="2026-11-18" data-matchtime="03:00" data-rangqiu="0" data-simpleleague="英锦标赛"
            data-processdate="2026-11-17">
          <td class="td td-betbtn"><p class="betbtn" data-type="nspf" data-value="3" data-sp="1.80"></p>
          <p class="betbtn" data-type="nspf" data-value="1" data-sp="3.50"></p>
          <p class="betbtn" data-type="nspf" data-value="0" data-sp="3.80"></p></td>
        </tr></table>
        """;

    [Fact]
    public void Jc_Card_Carries_ProcessDate_As_Expect()
    {
        var card = Assert.Single(BettingCardSource.ParseJc(JcRowA));
        Assert.Equal("2026-11-10", card.Expect);
    }

    [Fact]
    public void Jc_Same_MatchNo_Different_Week_Gets_Different_Expect()
    {
        // 两页各自解析，再合起来看：必须仍是两张能区分开的卡。
        var week1 = Assert.Single(BettingCardSource.ParseJc(JcRowA));
        var week2 = Assert.Single(BettingCardSource.ParseJc(JcRowBSameNo));

        Assert.Equal(week1.MatchNo, week2.MatchNo);
        Assert.NotEqual(week1.Expect, week2.Expect);
        Assert.NotEqual(week1.MatchId, week2.MatchId);
    }

    [Fact]
    public void Jc_Without_ProcessDate_Leaves_Expect_Null_Not_Empty()
    {
        // 空串与 NULL 在 MERGE 的 ISNULL(Expect,'') 里等价，但唯一索引不这么看
        // （NULL 互不相同、空串会互撞）。必须统一成 NULL，否则同一张卡会变成两种东西。
        const string row = """
            <table><tr data-fixtureid="1491202" data-matchnum="周二002" data-homesxname="A" data-awaysxname="B"
                data-matchdate="2026-11-11" data-matchtime="03:00" data-rangqiu="0" data-simpleleague="英锦标赛">
              <td><p class="betbtn" data-type="nspf" data-value="3" data-sp="2.00"></p></td>
            </tr></table>
            """;
        var card = Assert.Single(BettingCardSource.ParseJc(row));
        Assert.Null(card.Expect);
    }

    [Fact]
    public void Bd_Card_Carries_ScheduleDate_As_Expect()
    {
        // 北单没有 data-processdate，受注日在 value 的 scheduleDate 里。
        const string html = """
            <table><tr class="vs_lines" value="{index:'1',leagueName:'英锦赛',homeTeam:'巴尼特',guestTeam:'莱顿东方',endTime:'2026-11-11 02:50',rangqiuNum:'0',scheduleDate:'2026-11-10',disabled:'no'}" fid="1491190">
            <td><span class="sp_w35 eng pjoz">2.40</span><span class="sp_w35 eng pjoz">3.30</span><span class="sp_w35 eng pjoz">2.70</span></td>
            </tr></table>
            """;
        var card = Assert.Single(BettingCardSource.ParseBd(html));
        Assert.Equal("2026-11-10", card.Expect);
        Assert.Equal("1", card.MatchNo);
    }

    [Fact]
    public void Bd_Same_MatchNo_Different_Day_Gets_Different_Expect()
    {
        const string html = """
            <table>
            <tr class="vs_lines" value="{index:'1',homeTeam:'A',guestTeam:'B',endTime:'2026-11-11 02:50',rangqiuNum:'0',scheduleDate:'2026-11-10',disabled:'no'}" fid="1491190"></tr>
            <tr class="vs_lines" value="{index:'1',homeTeam:'C',guestTeam:'D',endTime:'2026-11-18 02:50',rangqiuNum:'0',scheduleDate:'2026-11-17',disabled:'no'}" fid="1500002"></tr>
            </table>
            """;
        var cards = BettingCardSource.ParseBd(html);

        Assert.Equal(2, cards.Count);
        Assert.All(cards, c => Assert.Equal("1", c.MatchNo));
        Assert.Equal(new[] { "2026-11-10", "2026-11-17" }, cards.Select(c => c.Expect!).ToArray());
    }
}
