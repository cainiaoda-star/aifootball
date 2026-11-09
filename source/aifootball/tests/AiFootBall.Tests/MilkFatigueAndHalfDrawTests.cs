using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 2026-11-08 两条「此前只做了一半」的落地验证：
///
/// 1) 胜平(3-1)/负平(0-1) 的概率改由 <see cref="HalfDrawModel"/> 给出并**真的能出注**。
///    上一版只挂在参考接口上，选注链从未调用，历史台账里 3-1 一次都没出过。
/// 2) 热门方体能/状态只做标注、不做概率惩罚。回测方向是反的（见 <see cref="TeamForm"/>），
///    这里锁住「标注文案」和「不扣概率」这两件事，避免以后有人顺手把惩罚加回去。
/// </summary>
public class MilkFatigueAndHalfDrawTests
{
    /// <summary>主客完全对等的模型：场均各进 1 失 1，平局是最强方向。</summary>
    private static ScorelineModel DrawishModel()
    {
        var s = new List<ScoreSample>();
        for (var i = 0; i < 20; i++)
        {
            s.Add(new ScoreSample { LeagueName = "英超", HomeTeam = "主队", AwayTeam = "客" + i, HomeScore = 1, AwayScore = 1 });
            s.Add(new ScoreSample { LeagueName = "英超", HomeTeam = "客队", AwayTeam = "主" + i, HomeScore = 1, AwayScore = 1 });
        }
        return ScorelineModel.Fit(s);
    }

    private static MatchRecommendation Rec(long id) => new()
    {
        Board = Boards.Jc,
        MatchNo = "周日001",
        MatchId = id,
        LeagueName = "英超",
        HomeTeam = "主队",
        AwayTeam = "客队",
        MatchTime = DateTime.Today.AddHours(20),
        TotalLine = 2.5m
    };

    private static JcOddsHistoryPoint Pt(long mid, string play, string code, decimal odds) => new()
    {
        MatchId = mid,
        PlayType = play,
        OptionCode = code,
        OptionName = code,
        Odds = odds
    };

    [Fact]
    public void 胜平负平_上限降到8后整类不再出注_两道闸都过不去()
    {
        var model = DrawishModel();
        var rec = Rec(7);

        // 总进球报价集中在 2 球 → 平局占比高，3-1/0-1 才有概率基础。
        // SPF 三项必须齐，否则 RefineHalfDraw 直接返回 null，两档会回落联合概率。
        var odds = new Dictionary<long, List<JcOddsHistoryPoint>>
        {
            [7] = new()
            {
                Pt(7, JcPlayTypes.Spf, "3", 2.60m),
                Pt(7, JcPlayTypes.Spf, "1", 3.30m),
                Pt(7, JcPlayTypes.Spf, "0", 2.80m),
                Pt(7, JcPlayTypes.Jq, "0", 9.0m),
                Pt(7, JcPlayTypes.Jq, "1", 5.0m),
                Pt(7, JcPlayTypes.Jq, "2", 1.8m),
                Pt(7, JcPlayTypes.Jq, "3", 5.0m),
                Pt(7, JcPlayTypes.Jq, "4", 12.0m),
                Pt(7, JcPlayTypes.Jq, "5", 15.0m),
                Pt(7, JcPlayTypes.Jq, "6", 20.0m),
                Pt(7, JcPlayTypes.Jq, "7", 25.0m),
                // 只给反转档，不给胜胜/平平，确保这一注就是该场的剧本锚点。
                // 2026-11-09：真实胜平/负平均价 15.43，被 MaxHalfFullOdds=8 整类拦掉
                // （台账 3 注 13/17/30 倍全黑、421 场回放 8 倍以上回报 0.711，两个独立样本同向）。
                // 本测试验证的是「概率来自 HalfDrawModel」这条路径本身，与赔率高低无关，
                // 所以载体赔率取 7.5 让它能过闸；「超上限时不发」由下面那条测试单独锁。
                Pt(7, JcPlayTypes.Bq, "3-1", 7.5m),
                Pt(7, JcPlayTypes.Bq, "0-1", 7.5m),
            }
        };

        var picks = MilkPickEngine.Build(new[] { rec }, odds, model);

        var refined = picks.Where(p => p.HalfDrawRefined).ToList();

        // 2026-11-09：MaxHalfFullOdds 15 → 8 之后，胜平/负平被两道闸同时挡住，整类退出。
        //   ① 赔率闸：真实均价 15.43 > 8，根本进不到选注环节；
        //   ② 即使把赔率压到 8 以内（本测试用的 7.5），HalfDrawModel 给的 4~9% 概率
        //      算出的 EV 只有 0.30~0.68，过不了 BQ 的基线 EV 闸（要求 ≥1.00 倍返奖率，
        //      即赔率需 ≥ 8.9 才可能放行）。
        // 也就是说「把上限放宽回 15」救不回这两档——放宽赔率闸仍会被 EV 闸拦，
        // 唯一能救它们的做法是放宽 EV 闸，那是另一个决策。
        // HalfDrawModel 的代码保留在引擎里，将来重启时需要重新验证。
        Assert.Empty(refined);

        // 同一场也不该出现胜平/负平（换档逻辑不能把它们塞回来）。
        Assert.DoesNotContain(picks, p => p.OptionCode is "3-1" or "0-1");
    }

    [Fact]
    public void 胜平负平赔率超上限时整类不再出注()
    {
        // 设计后果锁定（2026-11-09 MaxHalfFullOdds 15 → 8）：
        // 胜平/负平真实均价 15.43，落到上限之外，两档会整类退出。
        // 这条测试的存在意义是：将来有人把上限放宽回 15 时，会立刻看到这里变红，
        // 从而必须回答「8~15 倍那批到底该不该发」——那批在 421 场回放里回报 0.711。
        var model = DrawishModel();
        var rec = Rec(7);
        var odds = new Dictionary<long, List<JcOddsHistoryPoint>>
        {
            [7] = new()
            {
                Pt(7, JcPlayTypes.Spf, "3", 2.60m),
                Pt(7, JcPlayTypes.Spf, "1", 3.30m),
                Pt(7, JcPlayTypes.Spf, "0", 2.80m),
                Pt(7, JcPlayTypes.Jq, "2", 1.8m),
                Pt(7, JcPlayTypes.Bq, "3-1", 15.43m),
                Pt(7, JcPlayTypes.Bq, "0-1", 15.43m),
            }
        };

        var picks = MilkPickEngine.Build(new[] { rec }, odds, model);

        Assert.DoesNotContain(picks, p => p.OptionCode is "3-1" or "0-1");
    }

    [Fact]
    public void 缺SPF报价时回落联合概率_不会标方向模型()
    {
        var model = DrawishModel();
        var rec = Rec(8);
        var odds = new Dictionary<long, List<JcOddsHistoryPoint>>
        {
            [8] = new()
            {
                // 故意不给 SPF：没有主客强弱，HalfDrawModel 无从下手
                Pt(8, JcPlayTypes.Jq, "2", 1.8m),
                Pt(8, JcPlayTypes.Bq, "3-1", 13.0m),
                Pt(8, JcPlayTypes.Bq, "0-1", 13.0m),
            }
        };

        var picks = MilkPickEngine.Build(new[] { rec }, odds, model);
        Assert.DoesNotContain(picks, p => p.HalfDrawRefined);
    }

    [Fact]
    public void 体能标注_欧战后短休加三连败()
    {
        var ko = new DateTime(2026, 11, 8, 20, 0, 0);
        var form = new TeamForm
        {
            TeamName = "曼城",
            LastMatchTime = ko.AddHours(-68),
            LastLeague = "欧冠",
            Last1 = -1,
            Last2 = -1,
            Last3 = -1
        };

        var tag = MilkPickEngine.DescribeFatigue(form, ko);
        Assert.NotNull(tag);
        Assert.Contains("连败3场", tag);
        Assert.Contains("欧战后68小时", tag);
    }

    [Fact]
    public void 体能标注_休息充足且无连败时为空()
    {
        var ko = new DateTime(2026, 11, 8, 20, 0, 0);
        var form = new TeamForm
        {
            TeamName = "曼城",
            LastMatchTime = ko.AddDays(-7),
            LastLeague = "英超",
            Last1 = 1,
            Last2 = 0,
            Last3 = -1
        };

        Assert.Null(MilkPickEngine.DescribeFatigue(form, ko));
    }

    [Fact]
    public void 体能标注_非欧战短休按72小时()
    {
        var ko = new DateTime(2026, 11, 8, 20, 0, 0);
        var form = new TeamForm
        {
            TeamName = "某队",
            LastMatchTime = ko.AddHours(-60),
            LastLeague = "英超",
            Last1 = 1,
            Last2 = 1,
            Last3 = 0
        };

        var tag = MilkPickEngine.DescribeFatigue(form, ko);
        Assert.NotNull(tag);
        Assert.Contains("休息60小时", tag);
        Assert.DoesNotContain("连败", tag);
    }

    [Fact]
    public void 体能标注不改概率()
    {
        var model = DrawishModel();
        var rec = Rec(9);
        var odds = new Dictionary<long, List<JcOddsHistoryPoint>>
        {
            [9] = new()
            {
                Pt(9, JcPlayTypes.Spf, "3", 2.60m),
                Pt(9, JcPlayTypes.Spf, "1", 3.30m),
                Pt(9, JcPlayTypes.Spf, "0", 2.80m),
                Pt(9, JcPlayTypes.Bq, "3-1", 13.0m),
                Pt(9, JcPlayTypes.Bq, "0-1", 13.0m),
            }
        };

        var without = MilkPickEngine.Build(new[] { rec }, odds, model);

        var forms = new Dictionary<string, TeamForm>(StringComparer.Ordinal)
        {
            ["主队"] = new TeamForm
            {
                TeamName = "主队",
                LastMatchTime = rec.MatchTime!.Value.AddHours(-50),
                LastLeague = "欧冠",
                Last1 = -1, Last2 = -1, Last3 = -1
            },
            ["客队"] = new TeamForm
            {
                TeamName = "客队",
                LastMatchTime = rec.MatchTime!.Value.AddHours(-50),
                LastLeague = "欧冠",
                Last1 = -1, Last2 = -1, Last3 = -1
            }
        };

        var with = MilkPickEngine.BuildDetailed(
            new[] { rec }, odds, model, teamForms: forms).Picks;

        // 概率/EV 必须逐注一致——这条断言就是「只标注、不扣概率」的守门人。
        Assert.Equal(without.Count, with.Count);
        foreach (var w in with)
        {
            var o = without.Single(x => x.PlayType == w.PlayType && x.OptionCode == w.OptionCode);
            Assert.Equal(o.ModelProbability, w.ModelProbability);
            Assert.Equal(o.Ev, w.Ev);
        }
    }
}
