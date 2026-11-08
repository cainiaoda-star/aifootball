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
    public void 胜平负平_现在能真正出注_且标记为方向模型()
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
                Pt(7, JcPlayTypes.Bq, "3-1", 13.0m),
                Pt(7, JcPlayTypes.Bq, "0-1", 13.0m),
            }
        };

        var picks = MilkPickEngine.Build(new[] { rec }, odds, model);

        var refined = picks.Where(p => p.HalfDrawRefined).ToList();
        Assert.NotEmpty(refined);
        Assert.All(refined, p => Assert.Contains(p.OptionCode, new[] { "3-1", "0-1" }));

        // 概率必须来自 HalfDrawModel（总进球反推 × 实测方向率），量级在 4%~9%；
        // 泊松联合分布给这两档的数值通常也是这个区间，所以这里同时锁「不是 0、也不是盘口隐含」。
        foreach (var p in refined)
        {
            Assert.InRange(p.ModelProbability, 3.0m, 10.0m);
            Assert.Equal(Math.Round(p.ModelProbability / 100m * p.Odds, 3), p.Ev);
        }
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
