using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 大奶方向闸（2026-11-05 加）。
///
/// 起因：11-04 晚三场全反（朗斯 1:9 科莫、费内巴切 3:2 利物浦、波尔图没平）。
/// 回测 1873 场后定下的规则：方向只认市场，模型与市场背离整场不发。
/// 这里把四种判定钉死——pass / diverge / unknown，以及「分歧太小不拦」的边界。
/// 改阈值（MinDirectionGap）会直接改变每天出不出票，必须有测试兜着。
/// </summary>
public class MilkDirectionGateTests
{
    private static MatchRecommendation Rec(decimal? ratioHome = null, decimal? ratioAway = null) => new()
    {
        Board = Boards.Jc,
        MatchNo = "周四013",
        MatchId = 1,
        LeagueName = "英超",
        HomeTeam = "主队",
        AwayTeam = "客队",
        MatchTime = DateTime.Today.AddHours(20),
        RatioHome = ratioHome,
        RatioAway = ratioAway
    };

    private static JcOddsHistoryPoint Spf(string code, decimal odds) => new()
    {
        MatchId = 1,
        PlayType = JcPlayTypes.Spf,
        OptionCode = code,
        OptionName = code,
        Odds = odds
    };

    /// <summary>一套完整的胜平负快照。去水分母 = 1.0798，落在合理区间内。</summary>
    private static List<JcOddsHistoryPoint> SpfBook(decimal home, decimal draw, decimal away)
        => new() { Spf("3", home), Spf("1", draw), Spf("0", away) };

    [Fact]
    public void SpfDevig_PicksStrongestSide()
    {
        // 主队 1.60 热门 → 去水后主胜约 58%，市场方向必须是主胜
        var market = MilkDirectionGate.MarketDirection(SpfBook(1.60m, 3.80m, 5.50m));

        Assert.NotNull(market);
        Assert.Equal('3', market!.Value.Dir);
        Assert.Equal("spf", market.Value.Source);
        // 三项去水概率合计必须是 1
        Assert.Equal(1.0m, Math.Round(market.Value.H + market.Value.D + market.Value.A, 4));
    }

    [Fact]
    public void ModelAgreesWithMarket_Pass()
    {
        // 模型 55%，市场主胜 58% —— 同向，放行
        var diag = MilkDirectionGate.Judge(Rec(), SpfBook(1.60m, 3.80m, 5.50m), 0.55m, 0.25m, 0.20m);

        Assert.Equal(MilkGateVerdicts.Pass, diag.Verdict);
        Assert.Equal('3', diag.ModelDirection);
        Assert.Equal('3', diag.MarketDirection);
        Assert.Null(diag.AlertCode);
    }

    [Fact]
    public void ModelAgainstMarket_Diverge()
    {
        // 模型说主胜 60%，市场把客胜压到 2.00（隐含 46%）——朗斯那场的形态
        var diag = MilkDirectionGate.Judge(Rec(), SpfBook(3.50m, 3.40m, 2.00m), 0.60m, 0.25m, 0.15m);

        Assert.Equal(MilkGateVerdicts.Diverge, diag.Verdict);
        Assert.Equal('3', diag.ModelDirection);
        Assert.Equal('0', diag.MarketDirection);
        Assert.Contains("整场不发", diag.AlertNote);
    }

    [Fact]
    public void TinyGap_NotBlocked()
    {
        // 市场自己也没站定：主 33.5% / 平 34.2%，模型指主胜但与市场主胜只差 1 个点以内 → 不拦
        var diag = MilkDirectionGate.Judge(Rec(), SpfBook(2.60m, 2.55m, 2.70m), 0.40m, 0.33m, 0.27m);

        Assert.Equal(MilkGateVerdicts.Pass, diag.Verdict);
        Assert.Equal('3', diag.ModelDirection);
        Assert.Equal('1', diag.MarketDirection);
        Assert.Contains("市场无明确方向", diag.AlertNote);
    }

    [Fact]
    public void IncompleteSnapshot_GateDisabled()
    {
        // 只有两档胜平负 = 快照不完整。用残缺快照判方向比不判更危险，闸必须不生效。
        var points = new List<JcOddsHistoryPoint> { Spf("3", 1.60m), Spf("0", 5.50m) };

        var diag = MilkDirectionGate.Judge(Rec(), points, 0.60m, 0.25m, 0.15m);

        Assert.Equal(MilkGateVerdicts.Unknown, diag.Verdict);
        Assert.Null(diag.MarketDirection);
        Assert.Equal("none", diag.MarketSource);
    }

    [Fact]
    public void ScoreBook_FallbackWhenSpfMissing()
    {
        // 没有胜平负时用比分盘反推。档数要够（≥15），否则还是算不出。
        var points = new List<JcOddsHistoryPoint>
        {
            Bf("1:0", 7.0m), Bf("2:0", 9.0m), Bf("2:1", 10.0m), Bf("3:0", 20.0m), Bf("3:1", 22.0m),
            Bf("3:2", 35.0m), Bf("4:0", 40.0m), Bf("4:1", 45.0m), Bf("4:2", 60.0m), Bf("5:0", 80.0m),
            Bf("0:0", 9.0m), Bf("1:1", 6.0m), Bf("2:2", 14.0m), Bf("3:3", 40.0m),
            Bf("0:1", 12.0m), Bf("0:2", 22.0m), Bf("1:2", 15.0m), Bf("0:3", 45.0m), Bf("胜其它", 60.0m)
        };

        var market = MilkDirectionGate.MarketDirection(points);

        Assert.NotNull(market);
        Assert.Equal("score", market!.Value.Source);
        Assert.Equal('3', market.Value.Dir);
    }

    private static JcOddsHistoryPoint Bf(string code, decimal odds) => new()
    {
        MatchId = 1,
        PlayType = JcPlayTypes.Bf,
        OptionCode = code,
        OptionName = code,
        Odds = odds
    };

    // 盘口用「百分之一盘位」的整数传（-25 = 主让 0.25），避免 InlineData 的
    // decimal 字面量被 xUnit 折成 double 再往 decimal? 里塞时抛 ArgumentException。
    [Theory]
    // 盘口从主让 0.25（-25）退到平手 = 往客队移 0.25 个盘位，算客向
    [InlineData(-25, 0, '0')]
    // 从平手加深到主让 0.5 = 主向
    [InlineData(0, -50, '3')]
    // 动 0.1 个盘位不算方向
    [InlineData(-25, -35, null)]
    // 没数据
    [InlineData(null, null, null)]
    public void AsianDirection_Threshold(int? open, int? close, char? expected)
        => Assert.Equal(expected, MilkDirectionGate.AsianDirection(
            open is int o ? o / 100m : null,
            close is int c ? c / 100m : null));

    [Theory]
    [InlineData(30000, 8000, '3')]     // 主队大额是客队 3.75 倍
    [InlineData(8000, 30000, '0')]
    [InlineData(10000, 9000, null)]    // 差距不到 1.5 倍，没有方向
    [InlineData(null, 9000, null)]
    public void ExchangeDirection_Threshold(int? home, int? away, char? expected)
        => Assert.Equal(expected, MilkDirectionGate.ExchangeDirection(
            home is int h ? h : null,
            away is int a ? a : null));

    [Fact]
    public void AsianTrap_AlertedButNotBlocked()
    {
        // 市场主胜、亚盘往客队移 —— 盘口在演，但闸只报警不否决（亚盘方向历史命中率 47.7%）
        var diag = MilkDirectionGate.Judge(
            Rec(), SpfBook(1.60m, 3.80m, 5.50m), 0.55m, 0.25m, 0.20m, (0.0m, 0.5m));

        Assert.Equal(MilkGateVerdicts.Pass, diag.Verdict);
        Assert.Equal(MilkGateAlerts.TrapAsian, diag.AlertCode);
        Assert.Equal('0', diag.AsianDirection);
    }

    [Fact]
    public void Lens2026_TrapAsianOnly()
    {
        // 朗斯 1:9 科莫那场的真实形态：市场压客胜、亚盘往主队移、必发大额也在客队。
        // 只有亚盘在演 —— 所以告警是 trap-asian，而不是 trap-both。
        var diag = MilkDirectionGate.Judge(
            Rec(ratioHome: 3408m, ratioAway: 11191m),
            SpfBook(3.50m, 3.40m, 2.00m), 0.60m, 0.25m, 0.15m, (0.0m, -0.5m));

        Assert.Equal(MilkGateVerdicts.Diverge, diag.Verdict);
        Assert.Equal(MilkGateAlerts.TrapAsian, diag.AlertCode);
        Assert.Equal('3', diag.AsianDirection);     // 亚盘加深主让
        Assert.Equal('0', diag.ExchangeDirection);  // 必发大额在客队
        Assert.Equal('0', diag.MarketDirection);    // 市场也在客队 → 必发不算背离
    }

    [Fact]
    public void PickDirectionDecides_NotModelDirection()
    {
        // 11-04 波尔图的形态：模型整体指主胜（55%），但这一场真正要发的是「平平」。
        // 市场也指主胜（58%），所以「平平」是逆市场的，必须拦。
        // 如果判据用 ModelDirection（主胜），这注会被当成同向放过去——榜就发出去了。
        var diag = MilkDirectionGate.Judge(
            Rec(), SpfBook(1.60m, 3.80m, 5.50m), 0.55m, 0.25m, 0.20m,
            asian: null, pickDirection: '1');

        Assert.Equal(MilkGateVerdicts.Diverge, diag.Verdict);
        Assert.Equal('3', diag.ModelDirection);   // 模型整体看主胜
        Assert.Equal('1', diag.PickDirection);    // 但这一注是平
        Assert.Equal('3', diag.MarketDirection);
    }

    [Fact]
    public void BothTraps_Flagged()
    {
        // 市场压客胜，亚盘往主队移、必发大额也在主队 —— 两层同时跟市场对着干
        var diag = MilkDirectionGate.Judge(
            Rec(ratioHome: 12000m, ratioAway: 5000m),
            SpfBook(3.50m, 3.40m, 2.00m), 0.60m, 0.25m, 0.15m, (0.0m, -0.5m));

        Assert.Equal(MilkGateVerdicts.Diverge, diag.Verdict);
        Assert.Equal(MilkGateAlerts.TrapBoth, diag.AlertCode);
        Assert.Equal('3', diag.AsianDirection);
        Assert.Equal('3', diag.ExchangeDirection);
    }

    /// <summary>
    /// 直接钉住 11-04 那三场的判定——不依赖模型重训，只喂当晚真实的市场收盘价和当晚真实发的注方向。
    ///
    /// 为什么必须这样测：模型是拿全量样本重训的离散对象，回测接口算出来的「模型方向」和当晚线上
    /// 可能不一样（实测朗斯现在给出客胜，当晚给的是胜胜）。这三行输入全部来自可复原的事实：
    ///   市场方向 ← FbJcOddsHistory 的 SPF 收盘价去水；
    ///   注方向   ← FbMilkPick 当天实际发出去的那几注。
    /// 所以它回答的是「当晚就已经有这道闸，会不会拦」，而不是「今天的模型怎么看」。
    /// </summary>
    [Theory]
    // 赔率用「百分之一」的整数传，避开 InlineData 的 decimal→double 折损。
    // 朗斯 1:9：市场客胜 55.5%，当晚押的是半全场胜胜（主胜）→ 拦
    [InlineData(350, 340, 200, '3', true)]
    // 波尔图 4:1：市场主胜 56.2%，当晚押的是平平（+比分 1:1）→ 拦
    [InlineData(160, 380, 550, '1', true)]
    // 费内巴切 3:2：市场客胜 68.6%，当晚押的是比分 1:2（客胜）→ 放行。
    // 那注输在比分本身没打出来，不是方向押反，这道闸管不了也不该管。
    [InlineData(350, 340, 200, '0', false)]
    public void Nov4RealCases(int home, int draw, int away, char pickDir, bool blocked)
    {
        var diag = MilkDirectionGate.Judge(
            Rec(), SpfBook(home / 100m, draw / 100m, away / 100m),
            0.50m, 0.25m, 0.25m, asian: null, pickDirection: pickDir);

        Assert.Equal(blocked ? MilkGateVerdicts.Diverge : MilkGateVerdicts.Pass, diag.Verdict);
    }

    /* ---------------- 端到端：闸真的把整场从榜上撤掉了 ---------------- */

    /// <summary>主队场均进 2 失 0.5 的模型，主胜概率六成以上。</summary>
    private static ScorelineModel Model()
    {
        var samples = new List<ScoreSample>();
        for (var i = 0; i < 20; i++)
        {
            samples.Add(new ScoreSample { LeagueName = "英超", HomeTeam = "主队", AwayTeam = "客" + i, HomeScore = 2, AwayScore = 1 });
            samples.Add(new ScoreSample { LeagueName = "英超", HomeTeam = "客队", AwayTeam = "主" + i, HomeScore = 1, AwayScore = 2 });
        }
        return ScorelineModel.Fit(samples);
    }

    private static List<JcOddsHistoryPoint> PickBook(decimal home, decimal draw, decimal away) => new()
    {
        Spf("3", home), Spf("1", draw), Spf("0", away),
        Bf("2:1", 10.0m), Bf("1:0", 9.0m), Bf("2:0", 15.0m),
        new JcOddsHistoryPoint
        {
            MatchId = 1, PlayType = JcPlayTypes.Bq, OptionCode = "3-3", OptionName = "3-3", Odds = 5.0m
        }
    };

    [Fact]
    public void BuildDetailed_MarketAgainstModel_FlagsAndDropsDivergentPicks()
    {
        // 模型稳主胜，市场把客胜压到 2.00（去水 46.3%，过半 45 的阈值）。
        //
        // 2026-11-05 两次改：先是「硬否决整场」被大样本推翻，改成「标注 + 照发」；
        // 当晚周四011（市场客胜 51.4%、模型发胜胜、EV 1.106）说明全放行也不行，
        // 于是收敛到按注判：弱背离只标注，强背离（市场方向 ≥DivergentDropPct）撤注。
        // 注意那次「大样本推翻硬否决」的分组依据是 PickDirection，而 PickDirection 当时
        // 取的是 matchPicks 第一条——比分区从 0:0 铺起，等于 9 场里 9 场都记成「指平」，
        // 那份统计本身是被污染的，不能拿来反对这次按注撤。
        var odds = new Dictionary<long, List<JcOddsHistoryPoint>> { [1] = PickBook(3.50m, 3.40m, 2.00m) };

        var result = MilkPickEngine.BuildDetailed(new[] { Rec() }, odds, Model());

        var gate = Assert.Single(result.Gates);
        Assert.Equal(MilkGateVerdicts.Diverge, gate.Verdict);
        Assert.True(gate.BlockedPicks > 0, "若开否决会挡掉几注，这个数必须留着，将来重测还要用");
        Assert.False(string.IsNullOrEmpty(gate.BlockedBest));
        Assert.Equal(1, result.DivergedMatches);

        // 2026-11-07 修：11-06 给「反转档 / 零封方向一致档」加了强背离豁免
        // （MilkPickEngine.IsReverseOrCleanSheet——假热门反向是核心规律，被强背离一刀砍掉就废了）。
        // 所以「榜上不许出现强背离注」这条断言不再成立，改成更精确的那条：
        // 强背离注可以留，但**只允许豁免类型留**，其它类型一律不许。
        var strongDiverged = result.Picks
            .Where(p => p.DivergedFromMarket && (p.MarketDirectionPct ?? 0m) >= 45m)
            .ToList();
        Assert.All(strongDiverged, p =>
        {
            var isReverseHalfFull = p.PlayType == JcPlayTypes.Bq
                                    && p.OptionCode.Length == 3
                                    && p.OptionCode[1] == '-'
                                    && p.OptionCode[0] != p.OptionCode[2];
            var isCleanSheetAligned = p.CleanSheetDirection is char cd
                                      && ((p.OptionCode == "3-3" && cd == '3')
                                          || (p.OptionCode == "0-0" && cd == '0'));
            Assert.True(isReverseHalfFull || isCleanSheetAligned,
                $"{p.OptionCode} 不属于豁免类型，强背离却留在榜上——方向闸被绕过了");
        });
    }

    [Fact]
    public void BuildDetailed_MarketAgreesWithModel_PicksSurvive()
    {
        // 同样的模型，市场这次也压主胜：注照发
        var odds = new Dictionary<long, List<JcOddsHistoryPoint>> { [1] = PickBook(1.60m, 3.80m, 5.50m) };

        var result = MilkPickEngine.BuildDetailed(new[] { Rec() }, odds, Model());

        Assert.Equal(MilkGateVerdicts.Pass, Assert.Single(result.Gates).Verdict);
        Assert.NotEmpty(result.Picks);
        Assert.All(result.Picks, p => Assert.Equal(1, p.MatchId));
    }

    [Fact]
    public void BuildDetailed_NoSpf_GateStaysOpen()
    {
        // 快照里没有胜平负（只有比分/半全场）时闸算不出方向，不能因此把榜清空
        var points = new List<JcOddsHistoryPoint>
        {
            Bf("2:1", 10.0m), Bf("1:0", 9.0m), Bf("2:0", 15.0m),
            new JcOddsHistoryPoint
            {
                MatchId = 1, PlayType = JcPlayTypes.Bq, OptionCode = "3-3", OptionName = "3-3", Odds = 5.0m
            }
        };

        var result = MilkPickEngine.BuildDetailed(
            new[] { Rec() }, new Dictionary<long, List<JcOddsHistoryPoint>> { [1] = points }, Model());

        Assert.Equal(MilkGateVerdicts.Unknown, Assert.Single(result.Gates).Verdict);
        Assert.NotEmpty(result.Picks);
    }
}
