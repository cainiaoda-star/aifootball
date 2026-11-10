using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 稳胆口径（2026-11-09 新增 save-steady-picks 时锁死）。
///
/// 这个栏目的全部价值就是「命中率」，所以它最容易在三个地方被改坏：
///   ① 掺进模型概率 —— 一旦有人把「模型也觉得行」当条件，命中率立刻从 77% 掉到 14% 那一档；
///   ② 排序改成按概率或按 EV —— 那就变成「选中价格被压最狠那档」，和主榜犯同一个病；
///   ③ 结算按让球判 —— 稳胆是竞彩胜平负（不让球），跟北单那套「净胜+让球数」完全不同。
/// 下面把这些约束全部写成断言。
/// </summary>
public class SteadyPickTests
{
    // ---------- 结算 ----------

    [Theory]
    [InlineData(2, 1, '3')]
    [InlineData(1, 1, '1')]
    [InlineData(0, 3, '0')]
    [InlineData(0, 0, '1')]
    public void Result_Of_Score(int h, int a, char expected)
        => Assert.Equal(expected, SteadySettlement.ResultOf(h, a));

    [Fact]
    public void Judge_HomeWin_Picks()
    {
        Assert.True(SteadySettlement.Judge('3', 2, 1));
        Assert.False(SteadySettlement.Judge('3', 1, 1));
        Assert.False(SteadySettlement.Judge('3', 0, 1));
    }

    [Fact]
    public void Judge_Draw_Only_When_Equal()
    {
        Assert.True(SteadySettlement.Judge('1', 1, 1));
        Assert.False(SteadySettlement.Judge('1', 2, 1));
    }

    [Fact]
    public void Judge_AwayWin_Picks()
    {
        Assert.True(SteadySettlement.Judge('0', 0, 2));
        Assert.False(SteadySettlement.Judge('0', 2, 0));
    }

    /// <summary>比分缺失必须是「未结算」而不是「没中」——腰斩/取消的场次不该进命中率分母。</summary>
    [Theory]
    [InlineData(null, 1)]
    [InlineData(1, null)]
    [InlineData(null, null)]
    public void Judge_MissingScore_IsNull(int? h, int? a)
        => Assert.Null(SteadySettlement.Judge('3', h, a));

    // ---------- 选注 ----------

    /// <summary>三边 1.50 / 4.00 / 6.00：主胜是市场最看好档，且赔率在上限内。</summary>
    [Fact]
    public void Build_PicksStrongestSide()
    {
        var built = SteadyPickEngine.Build(
            new[] { Rec(1, "周日001", "英超", "曼城", "伯恩利", 1) },
            Odds(1, (1.50m, 4.00m, 6.00m)));

        var p = Assert.Single(built.Picks);
        Assert.Equal('3', p.Side);
        Assert.Equal(1.50m, p.Odds);
        Assert.Equal(1, p.Rank);
        Assert.True(p.MarketPct > 60m);   // 1/1.5 ÷ 1.0833 ≈ 61.5%
    }

    /// <summary>客胜是最低赔时选客胜，不能因为「主队主场」就锁死主胜。</summary>
    [Fact]
    public void Build_PicksAwayWhenCheapest()
    {
        var built = SteadyPickEngine.Build(
            new[] { Rec(2, "周日002", "英超", "伯恩利", "曼城", 1) },
            Odds(2, (6.00m, 4.00m, 1.45m)));

        var p = Assert.Single(built.Picks);
        Assert.Equal('0', p.Side);
        Assert.Equal(1.45m, p.Odds);
    }

    /// <summary>赔率超过上限（1.75）不出。回测里 2.0~2.6 箱命中率只有 39~46%。</summary>
    [Fact]
    public void Build_SkipsOverMaxOdds()
    {
        var built = SteadyPickEngine.Build(
            new[] { Rec(3, "周日003", "英超", "甲", "乙", 1) },
            Odds(3, (2.10m, 3.30m, 3.40m)));

        Assert.Empty(built.Picks);
        Assert.Equal(1, built.OverOdds);
    }

    /// <summary>均衡局（最高概率不到 40%）不该出现在「稳胆」里。</summary>
    [Fact]
    public void Build_SkipsBalancedGame()
    {
        // 2.60 / 3.10 / 2.90 → 最高约 37%，虽然赔率没超上限，但不是稳胆。
        var built = SteadyPickEngine.Build(
            new[] { Rec(4, "周日004", "英超", "甲", "乙", 1) },
            Odds(4, (2.60m, 3.10m, 2.90m)));

        Assert.Empty(built.Picks);
    }

    /// <summary>三档缺一档就不算——两档凑出来的去水概率是假精度。</summary>
    [Fact]
    public void Build_SkipsIncompleteSnapshot()
    {
        var odds = new Dictionary<long, List<JcOddsHistoryPoint>>
        {
            [5] = new()
            {
                Point(5, "3", 1.50m),
                Point(5, "1", 4.00m)
                // 缺客胜
            }
        };
        var built = SteadyPickEngine.Build(
            new[] { Rec(5, "周日005", "英超", "甲", "乙", 1) }, odds);

        Assert.Empty(built.Picks);
    }

    /// <summary>不推名单（欧国联/友谊赛/青年赛）与黑名单（澳超）都不出，和主榜共用同一份名单。</summary>
    [Theory]
    [InlineData("欧国联")]
    [InlineData("国际友谊赛")]
    [InlineData("澳超")]
    public void Build_SkipsExcludedLeagues(string league)
    {
        var built = SteadyPickEngine.Build(
            new[] { Rec(6, "周日006", league, "甲", "乙", 1) },
            Odds(6, (1.40m, 4.20m, 7.00m)));

        Assert.Empty(built.Picks);
        Assert.Equal(1, built.Excluded);
    }

    /// <summary>每日最多 3 注，按赔率升序；第 4 注起只计数不返回。</summary>
    [Fact]
    public void Build_CapsAtTwoPerDay()
    {
        var recs = new[]
        {
            Rec(11, "周日011", "英超", "A", "a", 1),
            Rec(12, "周日012", "英超", "B", "b", 2),
            Rec(13, "周日013", "英超", "C", "c", 3),
            Rec(14, "周日014", "英超", "D", "d", 4),
            Rec(15, "周日015", "英超", "E", "e", 5)
        };
        var odds = new Dictionary<long, List<JcOddsHistoryPoint>>();
        foreach (var r in recs)
        {
            var mid = r.MatchId!.Value;
            // 赔率故意乱序：1.60 / 1.30 / 1.70 / 1.20 / 1.50
            var h = mid switch { 11 => 1.60m, 12 => 1.30m, 13 => 1.70m, 14 => 1.20m, _ => 1.50m };
            // 平/客按「去水分母 1.15」反推，保证快照通过了 over-round 校验。
            // （第一版随手写 4.5 / 7.0，结果 1.70 那场 over-round 只有 0.953 被当成残缺快照跳过，
            //   测试因此报 Candidates=3 而不是 5 —— 校验本身是对的，是我的构造数据不真实。）
            var leg = (1.15m - 1m / h) / 2m;
            odds[mid] = new List<JcOddsHistoryPoint>
            {
                Point(mid, "3", h), Point(mid, "1", Math.Round(1m / leg, 2)), Point(mid, "0", Math.Round(1m / leg, 2))
            };
        }

        var built = SteadyPickEngine.Build(recs, odds);

        Assert.Equal(2, built.Picks.Count);
        Assert.Equal(5, built.Candidates);
        Assert.Equal(3, built.CutByRank);

        // 留下的是赔率最低的两注（1.20 的 14 号、1.30 的 12 号），按 Rank 升序。
        // 这条钉住「按赔率升序取前 N」，不是按开赛时间或场次号 —— 回测的 1.108 回报
        // 全部建立在这个排序上（见 SteadyPickRules.MaxPerDay 的注释）。
        Assert.Equal(14, built.Picks[0].MatchId);
        Assert.Equal(1, built.Picks[0].Rank);
        Assert.Equal(12, built.Picks[1].MatchId);
        Assert.Equal(2, built.Picks[1].Rank);
        // 按赔率升序：1.20 → 1.30（上限 2 注，1.50 那注被截掉）
        Assert.Equal(new[] { 1.20m, 1.30m }, built.Picks.Select(p => p.Odds).ToArray());
        Assert.Equal(new[] { 1, 2 }, built.Picks.Select(p => p.Rank).ToArray());
    }

    /// <summary>同赔率时按开赛时间再按场次号排序——否则每次重算顺序都可能变，用户看到注单乱跳。</summary>
    [Fact]
    public void Build_StableOrderOnEqualOdds()
    {
        var recs = new[]
        {
            Rec(21, "周日021", "英超", "A", "a", 2),
            Rec(22, "周日022", "英超", "B", "b", 1)
        };
        var built = SteadyPickEngine.Build(recs, new Dictionary<long, List<JcOddsHistoryPoint>>
        {
            [21] = new() { Point(21, "3", 1.50m), Point(21, "1", 4.2m), Point(21, "0", 6.8m) },
            [22] = new() { Point(22, "3", 1.50m), Point(22, "1", 4.2m), Point(22, "0", 6.8m) }
        });

        Assert.Equal(2, built.Picks.Count);
        Assert.Equal("周日022", built.Picks[0].MatchNo);   // 开赛更早（hour 1）
    }

    /// <summary>没有快照 / 没有场次时安静返回空，不抛异常（早跑时快照常常还没到）。</summary>
    [Fact]
    public void Build_EmptyInputs_NoThrow()
    {
        Assert.Empty(SteadyPickEngine.Build(
            Array.Empty<MatchRecommendation>(),
            new Dictionary<long, List<JcOddsHistoryPoint>>()).Picks);

        Assert.Empty(SteadyPickEngine.Build(
            new[] { Rec(31, "周日031", "英超", "A", "a", 1) },
            new Dictionary<long, List<JcOddsHistoryPoint>>()).Picks);
    }

    /// <summary>
    /// 口径常量不许被悄悄放宽。这两条数字来自 `steady-probe` 382 注回测：
    /// 赔率 ≤1.75、每日 ≤3 → 77.6% 命中 / 回报 0.993；放宽到 3.0 → 57.1% / 0.919。
    /// 要改必须同时改 SteadyPickRules 的注释与类头回测表，否则下一个人不知道为什么是这两个数。
    /// </summary>
    [Fact]
    public void Rules_ArePinned()
    {
        Assert.Equal(1.75m, SteadyPickRules.MaxOdds);
        // 2026-11-10 由 3 改 2：按「每日排名」切，回报随注数单调下降
        // （前 1 注 1.108 / 前 2 注 1.011 / 前 3 注 0.983）。改回 3 等于把第三注
        // 当天勉强算热门的场次放进来，回报掉到 1 以下。这个数不是随便调的。
        Assert.Equal(2, SteadyPickRules.MaxPerDay);
    }

    // ---------- 构造辅助 ----------

    private static MatchRecommendation Rec(long id, string no, string league, string home, string away, int hour)
        => new()
        {
            Board = Boards.Jc,
            MatchNo = no,
            MatchId = id,
            LeagueName = league,
            HomeTeam = home,
            AwayTeam = away,
            MatchTime = new DateTime(2026, 11, 9, hour, 0, 0)
        };

    private static JcOddsHistoryPoint Point(long mid, string code, decimal odds)
        => new() { MatchId = mid, MatchNo = "", PlayType = JcPlayTypes.Spf, OptionCode = code, Odds = odds };

    // ---------- 受注日（2026-11-10 修：不再一律用 DateTime.Today） ----------

    /// <summary>
    /// 「周三005」开球 11-12 03:00，属于 11-11 那一期。
    /// 修之前作业一律存 DateTime.Today，20:50 那轮会把它记成 11-10 —— 错一天，
    /// 而且 SaveDayAsync 会先删当天的未结算行，等于用次日的注覆盖当天的注。
    /// </summary>
    [Fact]
    public void SalesDate_Follows_MatchNo_Period_Not_Kickoff_Date()
    {
        var d = SteadyPickRules.SalesDate("周三005", new DateTime(2026, 11, 12, 3, 0, 0), new DateTime(2026, 11, 10));
        Assert.Equal(new DateTime(2026, 11, 11), d);
    }

    /// <summary>同一天的晚场（20:00 开球）仍属于当天那一期，不能往前推。</summary>
    [Fact]
    public void SalesDate_Keeps_Same_Day_For_Evening_Kickoff()
    {
        var d = SteadyPickRules.SalesDate("周三001", new DateTime(2026, 11, 11, 20, 0, 0), new DateTime(2026, 11, 10));
        Assert.Equal(new DateTime(2026, 11, 11), d);
    }

    /// <summary>凌晨 03:00 的周二场属于 11-10（周二）那一期。</summary>
    [Fact]
    public void SalesDate_Tuesday_PreDawn_Is_Monday_Period()
    {
        var d = SteadyPickRules.SalesDate("周二001", new DateTime(2026, 11, 11, 3, 0, 0), new DateTime(2026, 11, 11));
        Assert.Equal(new DateTime(2026, 11, 10), d);
    }

    /// <summary>北单纯数字场次号没有期号前缀，退回「12:00 前开球归前一日」。</summary>
    [Fact]
    public void SalesDate_No_Period_Prefix_Falls_Back_To_Kickoff_Rule()
    {
        Assert.Equal(new DateTime(2026, 11, 10),
            SteadyPickRules.SalesDate("123", new DateTime(2026, 11, 11, 3, 0, 0), new DateTime(2026, 11, 1)));
        Assert.Equal(new DateTime(2026, 11, 11),
            SteadyPickRules.SalesDate("123", new DateTime(2026, 11, 11, 20, 0, 0), new DateTime(2026, 11, 1)));
    }

    /// <summary>完全没有开赛时间时只能信 fallback，但绝不许抛。</summary>
    [Fact]
    public void SalesDate_Null_MatchTime_Uses_Fallback()
    {
        Assert.Equal(new DateTime(2026, 11, 10), SteadyPickRules.SalesDate("周三005", null, new DateTime(2026, 11, 10)));
    }

    [Theory]
    [InlineData("周一001", 1)]
    [InlineData("周日023", 7)]
    [InlineData("周三005", 3)]
    [InlineData("123", 0)]
    [InlineData("", 0)]
    public void WeekdayOfMatchNo_Parses_Prefix(string matchNo, int expected)
        => Assert.Equal(expected, SteadyPickRules.WeekdayOfMatchNo(matchNo));

    private static Dictionary<long, List<JcOddsHistoryPoint>> Odds(long mid, (decimal H, decimal D, decimal A) o)
        => new()
        {
            [mid] = new()
            {
                Point(mid, "3", o.H), Point(mid, "1", o.D), Point(mid, "0", o.A)
            }
        };
}
