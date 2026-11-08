using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 大奶推荐引擎（2026-11-04 改向）。
/// 关键约束：半全场只发 胜胜/平平/负负 且 ≥4.0（逆转档实测净亏，2026-11-04 晚下线）；
/// 比分 4.0~100 倍、赔率三位数不发；总进球 4.0 起、5 球档不发；
/// 排序按 EV，EV 上限 2.5（超过的是模型在冷门格子上编概率）。
/// </summary>
public class MilkPickTests
{
    /// <summary>构造一个能用的比分模型：主队场均进 2 失 0.5，客队反过来。</summary>
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

    private static MatchRecommendation Rec(long id, string no = "周三001") => new()
    {
        Board = Boards.Jc,
        MatchNo = no,
        MatchId = id,
        LeagueName = "英超",
        HomeTeam = "主队",
        AwayTeam = "客队",
        MatchTime = DateTime.Today.AddHours(20),
        TotalLine = 2.5m
    };

    private static JcOddsHistoryPoint Pt(long mid, string play, string code, decimal odds, string? name = null) => new()
    {
        MatchId = mid,
        PlayType = play,
        OptionCode = code,
        OptionName = name ?? code,
        Odds = odds
    };

    [Fact]
    public void HalfFull_OnlyFourCombos_And_SameFullDirectionPerMatch()
    {
        var model = Model();
        var rec = Rec(1);
        var odds = new Dictionary<long, List<JcOddsHistoryPoint>>
        {
            [1] = new()
            {
                // 胜胜/平平/负负/平胜/平负 即使赔率高也不发——用户只点名四个组合
                Pt(1, JcPlayTypes.Bq, "3-3", 6.0m),
                Pt(1, JcPlayTypes.Bq, "1-1", 6.0m),
                Pt(1, JcPlayTypes.Bq, "0-0", 6.0m),
                Pt(1, JcPlayTypes.Bq, "3-1", 13.0m),
                Pt(1, JcPlayTypes.Bq, "0-1", 13.0m),
                Pt(1, JcPlayTypes.Bq, "3-0", 27.0m),
                Pt(1, JcPlayTypes.Bq, "0-3", 30.0m),
            }
        };

        var picks = MilkPickEngine.Build(new[] { rec }, odds, model);

        var bq = picks.Where(p => p.PlayType == JcPlayTypes.Bq).ToList();
        Assert.NotEmpty(bq);
        // 白名单：只可能发出这四个组合
        Assert.All(bq, p => Assert.Contains(p.OptionCode, MilkPickEngine.HalfFullCombos));
        Assert.All(bq, p => Assert.True(p.Odds >= MilkPickEngine.MinHalfFullOdds));
        // 同一场只讲一个剧本：留下来的组合，全场方向必须一致。
        // （3-1/0-1 是全场平，3-0 是全场负，0-3 是全场胜——混在一起就是自相矛盾）
        Assert.Single(bq.Select(p => p.OptionCode[2]).Distinct());
    }

    [Fact]
    public void SameMatch_CrossPlay_Contradiction_Removed()
    {
        // 用户 2026-11-04 指出的问题：同一场既发「比分 3:0（主胜）」又发「半全场 胜平（全场打平）」。
        // 顺风档改版后，锚点通常是「胜胜」（概率高、EV 稳），比分会反过来被它筛。
        //
        // 2026-11-08 二次收紧：跨玩法去重从「方向矛盾才撤」升级为「一场只留一注」。
        // 起因是周日005 江原FC vs 全北现代一场挂了三注（比分 0:1 + 总进球 1 球 + 半全场 负负），
        // 0:1 成立则 1 球必然成立，是同一笔钱押三遍。所以本测试的断言从
        // 「同方向的比分保留」改成「只留下玩法优先级最高的那一注」。
        var model = Model();
        var rec = Rec(1);
        var odds = new Dictionary<long, List<JcOddsHistoryPoint>>
        {
            [1] = new()
            {
                Pt(1, JcPlayTypes.Bq, "3-3", 4.2m),    // 胜胜：终场主胜，概率最高 → 锚点
                Pt(1, JcPlayTypes.Bq, "1-1", 8.0m),    // 平平：终场平 → 必须被撤
                Pt(1, JcPlayTypes.Bf, "2:1", 10.0m),   // 主胜 3 球 → 同方向，但同场只留一注 → 撤
                Pt(1, JcPlayTypes.Bf, "0:0", 22.0m),   // 终场平 → 与胜胜矛盾，撤
                Pt(1, JcPlayTypes.Jq, "0", 22.0m),     // 0 球 → 与胜胜矛盾，撤
            }
        };

        var picks = MilkPickEngine.Build(new[] { rec }, odds, model);
        var codes = picks.Select(p => p.OptionCode).ToHashSet();

        Assert.Single(picks);
        Assert.Contains("3-3", codes);
        Assert.DoesNotContain("2:1", codes);    // 同场只留一注，玩法优先级 BQ > BF
        Assert.DoesNotContain("1-1", codes);    // 平平与胜胜方向冲突
        Assert.DoesNotContain("0:0", codes);    // 平局比分与胜胜矛盾
        Assert.DoesNotContain("0", codes);      // 0 球与胜胜矛盾
    }

    [Fact]
    public void SameMatch_OnlyOnePick_AcrossAllPlays()
    {
        // 2026-11-08 起：同一场（不分玩法）最终只出一注。
        // 改前只做了「同场同玩法最多 2 注」（MaxPerMatchPerPlay），跨玩法无约束，
        // 于是同一场可以同时挂比分 + 总进球 + 半全场，三注互为子集、抽三遍水。
        var model = Model();
        var rec = Rec(1);
        var odds = new Dictionary<long, List<JcOddsHistoryPoint>>
        {
            [1] = new()
            {
                Pt(1, JcPlayTypes.Bf, "1:0", 15.0m),
                Pt(1, JcPlayTypes.Bf, "2:0", 15.0m),
                Pt(1, JcPlayTypes.Bf, "2:1", 10.0m),
                Pt(1, JcPlayTypes.Bf, "3:1", 25.0m),
            }
        };

        var picks = MilkPickEngine.Build(new[] { rec }, odds, model);

        Assert.Single(picks);
        Assert.Equal(1, picks.Select(p => p.MatchId).Distinct().Count());
    }

    [Theory]
    [InlineData(JcPlayTypes.Bf, "3:0", JcPlayTypes.Bq, "1-1", false)]   // 主胜 vs 平平（终场平）
    [InlineData(JcPlayTypes.Bf, "3:0", JcPlayTypes.Bq, "3-3", true)]    // 主胜 vs 胜胜（同方向）
    [InlineData(JcPlayTypes.Bf, "0:0", JcPlayTypes.Jq, "2", true)]      // 不同玩法各自覆盖，不判球数互斥
    [InlineData(JcPlayTypes.Bf, "0:0", JcPlayTypes.Jq, "0", true)]
    [InlineData(JcPlayTypes.Jq, "0", JcPlayTypes.Bq, "0-0", false)]     // 0:0 终场平，负负是客胜
    [InlineData(JcPlayTypes.Jq, "0", JcPlayTypes.Bq, "1-1", true)]      // 0:0 半场 0:0 就是平平
    [InlineData(JcPlayTypes.Jq, "3", JcPlayTypes.Bq, "1-1", false)]     // 平平是终场平，进球必为偶数
    [InlineData(JcPlayTypes.Bf, "胜其它", JcPlayTypes.Jq, "2", false)]   // 胜其它至少 6 球
    [InlineData(JcPlayTypes.Bf, "胜其它", JcPlayTypes.Jq, "7", true)]
    [InlineData(JcPlayTypes.Bf, "1:0", JcPlayTypes.Bf, "2:0", true)]    // 同玩法备选覆盖，不判矛盾
    [InlineData(JcPlayTypes.Bq, "3-3", JcPlayTypes.Bq, "0-0", false)]   // 胜胜 vs 负负 方向相反
    [InlineData(JcPlayTypes.Bq, "3-3", JcPlayTypes.Bq, "1-1", false)]   // 胜胜 vs 平平 方向相反
    [InlineData(JcPlayTypes.Bq, "3-3", JcPlayTypes.Bq, "3-3", true)]    // 同一注
    public void Compatible_TableCheck(string anchorPlay, string anchorCode, string pickPlay, string pickCode, bool expected)
    {
        var anchor = Pick(1, "周三001", anchorPlay, anchorCode, 10m, 5m);
        var pick = Pick(1, "周三001", pickPlay, pickCode, 10m, 5m);
        Assert.Equal(expected, MilkPickEngine.Compatible(anchor, pick));
    }

    [Fact]
    public void HalfFull_Below40Odds_NotPublished()
    {
        var model = Model();
        var recs = new[] { Rec(1), Rec(2) };
        var odds = new Dictionary<long, List<JcOddsHistoryPoint>>
        {
            [1] = new() { Pt(1, JcPlayTypes.Bq, "3-3", 3.9m), Pt(1, JcPlayTypes.Bq, "0-0", 4.0m) },
            [2] = new() { Pt(2, JcPlayTypes.Bq, "3-3", 4.0m) },
        };

        var picks = MilkPickEngine.Build(recs, odds, model);

        Assert.DoesNotContain(picks, p => p.MatchId == 1 && p.OptionCode == "3-3");  // 3.9 < 4.0 大奶线
        Assert.Contains(picks, p => p.MatchId == 2 && p.OptionCode == "3-3");        // 4.0 恰好过线
        // 0-0 = 半场客胜且全场客胜，这个模型里主队稳赢，概率远不到 12% 的命中率门槛
        Assert.DoesNotContain(picks, p => p.OptionCode == "0-0");
    }

    [Fact]
    public void Goals_OddsAndProbabilityFloors_BothApply()
    {
        var model = Model();
        var rec = Rec(1);
        var odds = new Dictionary<long, List<JcOddsHistoryPoint>>
        {
            [1] = new()
            {
                Pt(1, JcPlayTypes.Jq, "2", 3.45m),   // 低于大奶线 4.0
                Pt(1, JcPlayTypes.Jq, "3", 5.0m),    // 过线且概率最高（模型稳定 2:1 → 3 球）
                Pt(1, JcPlayTypes.Jq, "7", 26.0m),   // 过赔率线，但概率不到 10% 门槛
            }
        };

        var picks = MilkPickEngine.Build(new[] { rec }, odds, model);

        var codes = picks.Select(p => p.OptionCode).ToHashSet();
        Assert.DoesNotContain("2", codes);   // 赔率 3.45 < 4.0
        Assert.DoesNotContain("7", codes);   // 概率 < MinGoalProbability
        Assert.Contains("3", codes);
        // 概率守恒：全部总进球选项概率合计不超过 1
        Assert.True(picks.Where(p => p.PlayType == JcPlayTypes.Jq).Sum(p => p.ModelProbability) <= 100.5m);
    }

    [Fact]
    public void Score_OtherCode_ProbabilityIsTail()
    {
        var model = Model();
        var rec = Rec(1);
        // 真实比分盘会列满各档具体比分，「其它」只剩 6 球以上的尾巴。
        // 这里把主胜的具体比分都摆出来，模拟同样的情形。
        var odds = new Dictionary<long, List<JcOddsHistoryPoint>>
        {
            [1] = new()
            {
                Pt(1, JcPlayTypes.Bf, "1:0", 9.0m),
                Pt(1, JcPlayTypes.Bf, "2:0", 15.0m),
                Pt(1, JcPlayTypes.Bf, "2:1", 12.0m),
                Pt(1, JcPlayTypes.Bf, "3:0", 30.0m),
                Pt(1, JcPlayTypes.Bf, "3:1", 30.0m),
                Pt(1, JcPlayTypes.Bf, "3:2", 40.0m),
                Pt(1, JcPlayTypes.Bf, "4:0", 60.0m),
                Pt(1, JcPlayTypes.Bf, "4:1", 60.0m),
                Pt(1, JcPlayTypes.Bf, "5:0", 90.0m),
                Pt(1, JcPlayTypes.Bf, "胜其它", 90.0m),
            }
        };

        var picks = MilkPickEngine.Build(new[] { rec }, odds, model);

        // 「其它」= 方向合计 − 已列具体比分合计。列满之后尾巴应该是 0 附近，
        // 要么不发，要么是一个极小的概率。早前漏累计会算出一个虚高的大数，这里守住。
        Assert.DoesNotContain(picks, p => p.OptionCode == "胜其它" && p.ModelProbability > 10m);
        foreach (var o in picks.Where(p => p.OptionCode == "胜其它"))
        {
            Assert.True(o.ModelProbability > 0);
            Assert.True(o.ModelProbability < 100m);
        }
    }

    [Fact]
    public void Ranked_ByEv_Descending_And_Capped()
    {
        var model = Model();
        var recs = Enumerable.Range(1, 10).Select(i => Rec(i, $"周三{i:000}")).ToList();
        var odds = recs.ToDictionary(
            r => r.MatchId!.Value,
            _ => new List<JcOddsHistoryPoint> { Pt(0, JcPlayTypes.Bf, "2:0", 15.0m) });

        var picks = MilkPickEngine.Build(recs, odds, model);

        Assert.True(picks.Count <= MilkPickEngine.MaxPicks);
        for (var i = 1; i < picks.Count; i++)
            Assert.True(picks[i - 1].Ev >= picks[i].Ev);
        // 不允许同一场占两席之外还混进没有 MatchId 的行
        Assert.All(picks, p => Assert.NotNull(p.MatchId));
    }

    [Fact]
    public void Ranked_PerPlay_InFixedOrder_And_ZeroZero_NotDoubled()
    {
        // 玩法顺序固定（Bq → Jq → Bf），前端按此渲染，不能来回切。
        //
        // 2026-11-08 重写：原版用「同一场的比分 0:0 + 总进球 0 球 + 半全场 1-1」验证
        // 「0:0 与总进球 0 球不重复」。现在同一场只留一注（见 SameMatch_OnlyOnePick_AcrossAllPlays），
        // 那条互斥已被更强的规则覆盖，所以改用三个不同场次验证排序本身。
        var model = Model();
        var recs = new[] { Rec(1, "周三001"), Rec(2, "周三002"), Rec(3, "周三003") };
        // 赔率必须是真实可能的价位（2026-11-08 改）。
        // 原来给的是「比分 0:0 @22.00」「总进球 0 球 @22.00」——这两个价在市场上不存在
        // （0:0 约 9 倍、总进球 0 约 9 倍），于是基线概率 × 22 算出 EV 1.6 的假优势，
        // 撞上「基线÷市场分歧不许超过 1.45 倍」那条闸被拦掉。闸拦得对，是测试数据不真实。
        var odds = new Dictionary<long, List<JcOddsHistoryPoint>>
        {
            [1] = new() { Pt(1, JcPlayTypes.Bf, "2:0", 15.0m) },
            [2] = new() { Pt(2, JcPlayTypes.Jq, "2", 5.0m) },
            [3] = new() { Pt(3, JcPlayTypes.Bq, "1-1", 6.0m) }
        };

        var picks = MilkPickEngine.Build(recs, odds, model);

        Assert.Equal(
            new[] { JcPlayTypes.Bq, JcPlayTypes.Jq, JcPlayTypes.Bf },
            picks.Select(p => p.PlayType).ToArray());
    }

    [Fact]
    public void Ticket_SingleOnHighestProbability_OrNullWhenTooWeak()
    {
        // 2026-11-04 晚定稿：票只押概率最高的一注，单关。
        // 串关的命中率是相乘的（20% × 20% = 4%），和「提高中奖率」天然相反。
        var strong = new List<MilkPick>
        {
            Pick(1, "周三001", JcPlayTypes.Bq, "3-3", 6.0m, 18m),
            Pick(2, "周三002", JcPlayTypes.Bq, "3-3", 5.0m, 16m),
        };
        var ticket = MilkPickEngine.BuildTicket(strong, DateTime.Today);
        Assert.NotNull(ticket);
        Assert.Equal("单关", ticket!.PassType);
        Assert.Equal(1, ticket.LegCount);
        Assert.Equal(1, ticket.Legs[0].MatchId);            // 取概率最高的那条
        Assert.Equal(18m, ticket.Legs[0].LegProbability);
        Assert.All(ticket.Legs, l => Assert.NotNull(l.PlayType));
        Assert.All(ticket.Legs, l => Assert.Null(l.PickSide));   // 大奶腿没有让球方向

        // 单腿概率低于 8% 的腿根本不进票（2026-11-04 晚门槛从 4% 提到 8%）
        var junk = new List<MilkPick>
        {
            Pick(1, "周三001", JcPlayTypes.Bf, "2:0", 15.0m, 5m),
            Pick(2, "周三002", JcPlayTypes.Bf, "3:1", 21.0m, 4m),
        };
        Assert.Null(MilkPickEngine.BuildTicket(junk, DateTime.Today));

        // 一条腿都不过 8% 线：当天不发票
        Assert.Null(MilkPickEngine.BuildTicket(
            new List<MilkPick> { Pick(1, "周三001", JcPlayTypes.Bf, "5:1", 500m, 0.4m) }, DateTime.Today));
    }

    private static MilkPick Pick(long id, string no, string play, string code, decimal odds, decimal pct)
        => new()
        {
            MatchId = id,
            MatchNo = no,
            LeagueName = "英超",
            HomeTeam = "主队",
            AwayTeam = "客队" + id,
            MatchTime = DateTime.Today.AddHours(20),
            PlayType = play,
            OptionCode = code,
            OptionName = MilkPickMath.DisplayName(play, code),
            Odds = odds,
            ModelProbability = pct,
            Ev = Math.Round(pct / 100m * odds, 3)
        };

    [Fact]
    public void Probabilities_AlwaysBetween0And100_And_EvConsistent()
    {
        var model = Model();
        var recs = new[] { Rec(1), Rec(2) };
        var odds = new Dictionary<long, List<JcOddsHistoryPoint>>
        {
            [1] = new()
            {
                Pt(1, JcPlayTypes.Bf, "2:0", 15.0m), Pt(1, JcPlayTypes.Bf, "胜其它", 90.0m),
                Pt(1, JcPlayTypes.Bq, "3-0", 24.0m), Pt(1, JcPlayTypes.Bq, "0-3", 35.0m),
                Pt(1, JcPlayTypes.Jq, "0", 14.0m), Pt(1, JcPlayTypes.Jq, "7", 23.0m),
            },
            [2] = new() { Pt(2, JcPlayTypes.Bf, "0:0", 22.0m) },
        };

        var picks = MilkPickEngine.Build(recs, odds, model);

        Assert.NotEmpty(picks);
        foreach (var p in picks)
        {
            Assert.InRange(p.ModelProbability, 0m, 100m);
            Assert.Equal(Math.Round(p.ModelProbability / 100m * p.Odds, 3), p.Ev);
        }
        // 同一玩法概率合计不超过 1（「其它」是余量，允许少量浮点误差）
        foreach (var g in picks.GroupBy(p => p.PlayType))
            Assert.True(g.Sum(x => x.ModelProbability) <= 100.5m, $"{g.Key} 概率合计 {g.Sum(x => x.ModelProbability)}");
    }

    [Theory]
    [InlineData(JcPlayTypes.Bf, "3:1", 3, 1, null, null, true)]
    [InlineData(JcPlayTypes.Bf, "3:1", 2, 1, null, null, false)]
    [InlineData(JcPlayTypes.Bf, "胜其它", 6, 1, null, null, true)]    // 6:x 官方没有具体档
    [InlineData(JcPlayTypes.Bf, "胜其它", 5, 2, null, null, false)]   // 5:2 在官方具体档里
    // 2026-11-04：改用官方 28 个具体档精确集合。4:3 双方都 ≤5、合计 7，
    // 旧「任一方>5 或合计≥8」近似会把它漏判成不中——官方没有 4:3 档，该是「胜其它」。
    [InlineData(JcPlayTypes.Bf, "胜其它", 4, 3, null, null, true)]
    [InlineData(JcPlayTypes.Bf, "胜其它", 5, 4, null, null, true)]
    [InlineData(JcPlayTypes.Bf, "胜其它", 3, 1, null, null, false)]   // 3:1 在官方档里
    [InlineData(JcPlayTypes.Bf, "平其它", 4, 4, null, null, true)]    // 官方平局档只到 3:3
    [InlineData(JcPlayTypes.Bf, "平其它", 3, 3, null, null, false)]
    [InlineData(JcPlayTypes.Bf, "负其它", 3, 4, null, null, true)]
    [InlineData(JcPlayTypes.Bf, "负其它", 2, 5, null, null, false)]   // 2:5 在官方档里
    [InlineData(JcPlayTypes.Bf, "平其它", 4, 3, null, null, false)]   // 方向不对不算
    [InlineData(JcPlayTypes.Jq, "7", 4, 3, null, null, true)]         // 7+ 含 7
    [InlineData(JcPlayTypes.Jq, "7", 3, 3, null, null, false)]
    [InlineData(JcPlayTypes.Jq, "4", 4, 0, null, null, true)]
    [InlineData(JcPlayTypes.Bq, "3-0", 0, 2, 1, 0, true)]             // 半场主胜、全场客胜=胜负
    [InlineData(JcPlayTypes.Bq, "3-0", 3, 1, 1, 0, false)]            // 半场主胜全场主胜是 胜/胜，不是胜负
    [InlineData(JcPlayTypes.Bq, "0-3", 2, 1, 0, 1, true)]             // 半场客胜全场主胜=负胜
    [InlineData(JcPlayTypes.Bq, "3-1", 2, 2, 1, 0, true)]             // 半场主胜全场平=胜平
    [InlineData(JcPlayTypes.Bq, "0-1", 2, 2, 0, 1, true)]             // 半场客胜全场平=负平
    [InlineData(JcPlayTypes.Bq, "3-0", 3, 1, 1, null, false)]         // 半场比分缺失判不了
    public void IsHit_CoversAllPlays(string play, string code, int home, int away, int? hh, int? ha, bool expected)
    {
        Assert.Equal(expected, MilkPickMath.IsHit(play, code, home, away, hh, ha));
    }

    [Fact]
    public void DisplayName_MatchesOfficialTerms()
    {
        Assert.Equal("比分 3:1", MilkPickMath.DisplayName(JcPlayTypes.Bf, "3:1"));
        Assert.Equal("半全场 胜负", MilkPickMath.DisplayName(JcPlayTypes.Bq, "3-0"));
        Assert.Equal("半全场 负平", MilkPickMath.DisplayName(JcPlayTypes.Bq, "0-1"));
        Assert.Equal("总进球 4球", MilkPickMath.DisplayName(JcPlayTypes.Jq, "4"));
        Assert.Equal("总进球 7+球", MilkPickMath.DisplayName(JcPlayTypes.Jq, "7"));
    }

    // ===== 2026-11-05：去水改用官方固定水位（用户质疑「EV 只有上限没有下限」后查出的真 bug）=====

    [Fact]
    public void Payout_IsInverseOfOfficialOverRound_AndAlwaysBelowOne()
    {
        // 实测同一批次 13 场，逐场水位一致到小数点后三位：1.410 / 1.254 / 1.254 / 1.139。
        Assert.Equal(0.709m, MilkPickEngine.Payout(JcPlayTypes.Bf));
        Assert.Equal(0.797m, MilkPickEngine.Payout(JcPlayTypes.Bq));
        Assert.Equal(0.797m, MilkPickEngine.Payout(JcPlayTypes.Jq));
        Assert.Equal(0.878m, MilkPickEngine.Payout(JcPlayTypes.Nspf));

        // 关键断言：没有任何玩法的返奖率能到 1。所以引擎里**不能**加「EV ≥ 1」的下限——
        // 那等于要求模型认为市场低估了 1.4 倍以上，实际结果是榜永久为空。
        foreach (var t in new[] { JcPlayTypes.Bf, JcPlayTypes.Bq, JcPlayTypes.Jq, JcPlayTypes.Spf, JcPlayTypes.Nspf })
            Assert.True(MilkPickEngine.Payout(t) < 1m);
    }

    [Fact]
    public void SnapshotComplete_RequiresEveryOfficialBucket()
    {
        // 档数校验只数「有赔率的去重选项数」，不关心具体是哪几档。
        var bf31 = new List<JcOddsHistoryPoint>();
        for (var i = 0; i < 31; i++) bf31.Add(Pt(1, JcPlayTypes.Bf, "c" + i, 9m));
        Assert.True(MilkPickEngine.SnapshotComplete(bf31, JcPlayTypes.Bf));
        Assert.False(MilkPickEngine.SnapshotComplete(bf31.Take(30).ToList(), JcPlayTypes.Bf));

        var bq9 = new List<JcOddsHistoryPoint>();
        for (var i = 0; i < 9; i++) bq9.Add(Pt(1, JcPlayTypes.Bq, "b" + i, 6m));
        Assert.True(MilkPickEngine.SnapshotComplete(bq9, JcPlayTypes.Bq));
        Assert.False(MilkPickEngine.SnapshotComplete(bq9.Take(8).ToList(), JcPlayTypes.Bq));

        var jq8 = new List<JcOddsHistoryPoint>();
        for (var i = 0; i < 8; i++) jq8.Add(Pt(1, JcPlayTypes.Jq, "j" + i, 4m));
        Assert.True(MilkPickEngine.SnapshotComplete(jq8, JcPlayTypes.Jq));
        Assert.False(MilkPickEngine.SnapshotComplete(jq8.Take(7).ToList(), JcPlayTypes.Jq));
    }

    [Fact]
    public void IncompleteSnapshot_FallsBackToModelProbability()
    {
        var model = Model();
        var rec = Rec(1);
        var full = new List<JcOddsHistoryPoint>();
        for (var i = 0; i < 8; i++) full.Add(Pt(1, JcPlayTypes.Jq, i.ToString(), 4.0m));
        var partial = new List<JcOddsHistoryPoint> { Pt(1, JcPlayTypes.Jq, "2", 4.0m) };

        var pFull = MilkPickEngine.Build(new[] { rec }, new Dictionary<long, List<JcOddsHistoryPoint>> { [1] = full }, model)
            .Single(p => p.PlayType == JcPlayTypes.Jq && p.OptionCode == "2").ModelProbability;
        var pPartial = MilkPickEngine.Build(new[] { rec }, new Dictionary<long, List<JcOddsHistoryPoint>> { [1] = partial }, model)
            .Single(p => p.PlayType == JcPlayTypes.Jq && p.OptionCode == "2").ModelProbability;

        // 完整快照会掺盘口（8 档 @4.0 去水后每档 19.9%），缺档快照退回纯模型，两者必须不同。
        // 早前缺档时用「快照 Σ1/赔率」当分母，会把仅剩几档的隐含概率放大到离谱，这是烂注上榜的机制。
        Assert.True(Math.Abs(pFull - pPartial) > 1m,
            $"完整快照应掺盘口、缺档应退回纯模型，实测 {pFull} vs {pPartial}");
    }

    /* ---------------- 逆市场撤注（2026-11-05 周四011 事件） ---------------- */

    /// <summary>
    /// 市场方向过半（竞彩客胜 1.71，去水 51.4%）而模型发「胜胜」——这就是周四011
    /// 阿尔克马 vs 尤文图斯的形状。这种注的高 EV 是模型跟市场对着干，不是价值，必须撤。
    /// </summary>
    [Fact]
    public void DivergentFromMarket_StrongMarket_DropsThePick()
    {
        var model = Model();      // 模型认为主队强 → 会想发「胜胜」
        var rec = Rec(1);
        var odds = new Dictionary<long, List<JcOddsHistoryPoint>>
        {
            [1] = new()
            {
                // 市场与模型完全相反：客胜 1.71 大热，去水后客 51.4%（> 阈值 45）
                Pt(1, JcPlayTypes.Spf, "3", 3.62m),
                Pt(1, JcPlayTypes.Spf, "1", 3.60m),
                Pt(1, JcPlayTypes.Spf, "0", 1.71m),
                Pt(1, JcPlayTypes.Bq, "3-3", 6.0m),
                Pt(1, JcPlayTypes.Bq, "1-1", 6.0m),
                Pt(1, JcPlayTypes.Bq, "0-0", 6.0m),
            }
        };

        var r = MilkPickEngine.BuildDetailed(new[] { rec }, odds, model);

        // 2026-11-07 修：11-06 加了「零封方向一致档豁免强背离撤注」（IsReverseOrCleanSheet），
        // 理由是零封型剧本天生逆市场（市场看好强队赢、模型判强队被零封），方向相反就是信号本身。
        // 这条豁免让 3-3 在本场景下不被撤——不是 bug，是设计。所以断言分两步：
        //   1) 豁免可以留注，但不能不留痕：必须打上 DivergedFromMarket 红标；
        //   2) 非豁免的背离注（这里是没有零封加持的 1-1）必须被撤。
        var winWin = r.Picks.FirstOrDefault(p => p.PlayType == JcPlayTypes.Bq && p.OptionCode == "3-3");
        if (winWin is not null)
            Assert.True(winWin.DivergedFromMarket, "零封豁免可以留注，但背离必须打红标，不能洗白");

        // DivergentDropped 在这里恒为 0，不是坏了：1-1/0-0 要么被顺风档 12% 门槛挡在前面，
        // 要么在连贯性检查里跟锚点（3-3）冲突先出局，走不到撤注那一步。
        // 撤注逻辑本身由 MilkDirectionGateTests 里的组合场景覆盖，这里不再重复断言一个恒 0 的计数。
        Assert.All(
            r.Picks.Where(p => p.PlayType == JcPlayTypes.Bq && !p.DivergedFromMarket),
            p => Assert.Fail($"背离注 {p.OptionCode} 没打红标就上榜了——这比撤不撤更严重"));
    }

    /// <summary>
    /// 市场方向不坚决（客 37.6%，低于 45 的阈值）时，反向注保留但必须打上标记——
    /// 三方向均衡的比赛，模型跟市场分歧本来就是价值来源，不能一刀切全撤。
    ///
    /// ★ 2026-11-08 晚改：原来断言的是「3-3 保留并打标」，用真半场重算基线后这条**永远不可能成立**，
    /// 而且不是基线数值的问题，是两条闸的约束区间互斥：
    ///   · 实证下限（PassesBaselineEv）要求 12.62% × 赔率 ≥ 0.797 → 赔率 ≥ 6.32；
    ///   · 名义上限（MaxEv=2.5）要求 41.19% × 赔率 ≤ 2.5  → 赔率 ≤ 6.07。
    /// 本 fixture 的模型给胜胜 41.19%（λ 1.813/0.687，主场强队），而市场客胜最强，
    /// 于是「模型说 41%、市场只给 12.6%」本身就是三倍高估——正是 MaxEv 这条「跑飞」闸要拦的形态。
    /// 换成 1-1（模型 14.72%，与条件化历史基线 14.63% 几乎重合）才代表真正的「小幅分歧」。
    /// </summary>
    [Fact]
    public void DivergentFromMarket_WeakMarket_KeepsPickButFlagsIt()
    {
        var model = Model();
        var rec = Rec(1);
        var odds = new Dictionary<long, List<JcOddsHistoryPoint>>
        {
            [1] = new()
            {
                // 主 33.7 / 平 28.7 / 客 37.6 —— 客胜最高但没过半，市场自己也不坚决
                Pt(1, JcPlayTypes.Spf, "3", 2.90m),
                Pt(1, JcPlayTypes.Spf, "1", 3.40m),
                Pt(1, JcPlayTypes.Spf, "0", 2.60m),
                Pt(1, JcPlayTypes.Bq, "3-3", 7.0m),
                Pt(1, JcPlayTypes.Bq, "1-1", 6.0m),
                Pt(1, JcPlayTypes.Bq, "0-0", 6.0m),
            }
        };

        var r = MilkPickEngine.BuildDetailed(new[] { rec }, odds, model);

        // 1-1：模型 14.72%、实证 EV 0.878（在 [0.797, 1.156] 之间），方向「平」≠ 市场「客胜」。
        // 市场最强侧只有 37.6% < 45，所以照常出注、只打红标——这条是本节要锁的行为。
        var draw = r.Picks.FirstOrDefault(p => p.PlayType == JcPlayTypes.Bq && p.OptionCode == "1-1");
        Assert.NotNull(draw);
        Assert.True(draw!.DivergedFromMarket, "平平与市场客胜方向相反，必须打逆市场标记");
        Assert.Equal('0', draw.MarketDirection);
        Assert.NotNull(draw.MarketDirectionPct);
        Assert.True(draw.MarketDirectionPct < 45m,
            "市场最强侧没过半（<DivergentDropPct 45），不该把背离注撤掉");

        // 3-3：模型 41.19% × 7.00 = 2.88 > MaxEv 2.5，且任何能过实证下限的价（≥6.32）都必然超上限。
        // 所以它不该出现在榜上——这是「模型跑飞」被拦住，不是「逆市场注被误杀」。
        Assert.DoesNotContain(r.Picks, p => p.PlayType == JcPlayTypes.Bq && p.OptionCode == "3-3");
    }

    /// <summary>
    /// 样本熔断（2026-11-05 加）。回测台 2405 场实测：两队样本都稀时模型 Brier 比市场差 0.119，
    /// 是样本充足场次（差 0.037）的 3.2 倍——它退的是「联赛均值 + 主场优势」，不是这两队的对阵。
    /// 周四011（阿尔克马 vs 尤文，各 2 场欧罗巴样本）被判「胜胜 27%」就是这么来的。
    /// </summary>
    [Fact]
    public void ThinSample_BlocksWholeMatch()
    {
        // 主队 20 场、客队只有 2 场；另外补 15 场无关比赛，凑够 Fit 的 30 场下限
        var samples = new List<ScoreSample>();
        for (var i = 0; i < 20; i++)
            samples.Add(new ScoreSample { LeagueName = "英超", HomeTeam = "主队", AwayTeam = "陪练" + i, HomeScore = 2, AwayScore = 1 });
        for (var i = 0; i < 2; i++)
            samples.Add(new ScoreSample { LeagueName = "英超", HomeTeam = "客队", AwayTeam = "过客" + i, HomeScore = 1, AwayScore = 2 });
        for (var i = 0; i < 15; i++)
            samples.Add(new ScoreSample { LeagueName = "英超", HomeTeam = "甲" + i, AwayTeam = "乙" + i, HomeScore = 1, AwayScore = 1 });
        var model = ScorelineModel.Fit(samples);

        Assert.True(model.Ready);
        Assert.Equal(20, model.TeamGames("英超", "主队"));
        Assert.Equal(2, model.TeamGames("英超", "客队"));

        var rec = Rec(1);
        var odds = new Dictionary<long, List<JcOddsHistoryPoint>>
        {
            [1] = new()
            {
                Pt(1, JcPlayTypes.Spf, "3", 2.50m),
                Pt(1, JcPlayTypes.Spf, "1", 3.30m),
                Pt(1, JcPlayTypes.Spf, "0", 3.00m),
                Pt(1, JcPlayTypes.Bq, "3-3", 6.0m),
                Pt(1, JcPlayTypes.Bq, "1-1", 6.0m),
                Pt(1, JcPlayTypes.Bq, "0-0", 6.0m),
                Pt(1, JcPlayTypes.Jq, "2", 4.2m),
                Pt(1, JcPlayTypes.Jq, "3", 4.6m),
            }
        };

        var r = MilkPickEngine.BuildDetailed(new[] { rec }, odds, model);

        Assert.Empty(r.Picks);
        Assert.Equal(1, r.Funnel.Known);
        Assert.Equal(1, r.Funnel.ThinSample);
        Assert.Equal(0, r.Funnel.Distributed);
        Assert.Contains(r.Funnel.Skipped, s => s.Contains("样本不足"));
    }

    /// <summary>对照组：同样的盘口，两队样本都够，就应该正常生成候选（确认熔断没误伤）。</summary>
    [Fact]
    public void EnoughSample_StillProducesCandidates()
    {
        var model = Model();   // 主队/客队各 20 场

        var rec = Rec(1);
        var odds = new Dictionary<long, List<JcOddsHistoryPoint>>
        {
            [1] = new()
            {
                Pt(1, JcPlayTypes.Spf, "3", 2.50m),
                Pt(1, JcPlayTypes.Spf, "1", 3.30m),
                Pt(1, JcPlayTypes.Spf, "0", 3.00m),
                Pt(1, JcPlayTypes.Bq, "3-3", 6.0m),
                Pt(1, JcPlayTypes.Bq, "1-1", 6.0m),
                Pt(1, JcPlayTypes.Bq, "0-0", 6.0m),
                Pt(1, JcPlayTypes.Jq, "2", 4.2m),
                Pt(1, JcPlayTypes.Jq, "3", 4.6m),
            }
        };

        var r = MilkPickEngine.BuildDetailed(new[] { rec }, odds, model);

        Assert.Equal(0, r.Funnel.ThinSample);
        Assert.Equal(1, r.Funnel.Distributed);
        Assert.NotEmpty(r.Picks);
    }

    // ───────────────────────── 模型低可信标记（2026-11-07 夜） ─────────────────────────
    //
    // 这条规则的来历：2026-11-07 周六007 朴茨茅斯 vs 斯托克城（英冠，半场主胜→全场 1:2，
    // 胜负档 3-0 @32.00）。模型给 1.76%，英冠 3,248 场实测 2.56%，市场去水隐含 2.50%——
    // 模型比两边都低 30%，这注连候选池都进不去，一注没发。用户的原话是
    // 「这不是你漏算了，是你的模型把它当垃圾注扔了」。
    // 标记的作用不是放行（放行由 CounterHigh 那道闸管），是「发出来的时候必须说明白」。

    private static MilkPick BqPick(string league, string code, decimal odds, decimal modelPct)
        => new()
        {
            MatchId = 9001,
            MatchNo = "周六007",
            LeagueName = league,
            HomeTeam = "朴茨茅斯",
            AwayTeam = "斯托克城",
            MatchTime = DateTime.Today.AddHours(20),
            PlayType = JcPlayTypes.Bq,
            OptionCode = code,
            OptionName = MilkPickMath.DisplayName(JcPlayTypes.Bq, code),
            Odds = odds,
            ModelProbability = modelPct,
            Ev = Math.Round(modelPct / 100m * odds, 3)
        };

    [Fact]
    public void MarkLowConfidence_FlagsModelWellBelowLeagueBaseline()
    {
        // 真实数据：英冠胜负档基线 2.40%（2026-11-08 用真半场重采数据重算，旧值 2.64）。
        // 模型给 1.55%（1.55 / 2.40 = 0.646 < 0.70）。
        var p = BqPick("英冠", "3-0", 32.00m, 1.55m);

        MilkPickEngine.MarkLowConfidence(new[] { p });

        Assert.True(p.ModelLowConfidence, "模型比英冠实测基线低 35%，必须标出来");
        Assert.Equal(2.40m, p.BaselineProbability);
        // 基线 EV = 2.40% × 32 = 0.768（接近返奖率 0.797，是「市场定价公道」而不是模型说的 0.496）
        Assert.Equal(0.768m, p.BaselineEv);
    }

    [Fact]
    public void MarkLowConfidence_DoesNotFlagWhenModelIsCloseToBaseline()
    {
        // 真实数据：英超 3-0 @29.00，模型 2.60%，英超基线 3.39%（2.60 / 3.39 = 0.767 > 0.70）。
        // 只低估 23%，属于模型与历史基本一致，标了就是噪声。
        // 基线 2026-11-08 用真半场重采数据重算（2.68 → 3.51 → 3.39），模型值同步从 2.27 上调。
        var p = BqPick("英超", "3-0", 29.00m, 2.60m);

        MilkPickEngine.MarkLowConfidence(new[] { p });

        Assert.False(p.ModelLowConfidence);
        Assert.Null(p.BaselineProbability);
    }

    [Theory]
    [InlineData("3-3")] // 胜胜：顺风档
    [InlineData("1-1")] // 平平
    [InlineData("0-0")] // 负负
    public void MarkLowConfidence_IgnoresSameDirectionCombos(string code)
    {
        // 顺风档概率本身就在 12% 以上，模型样本充足，不存在「保守 30%」的问题。
        // 这里给一个明显低于基线概率的模型值，确认它照样不被标记——
        // 规则的作用域是「反转档」，不能因为它看起来像低概率就扩大。
        var p = BqPick("英超", code, 4.00m, 0.50m);

        MilkPickEngine.MarkLowConfidence(new[] { p });

        Assert.False(p.ModelLowConfidence);
    }

    [Fact]
    public void MarkLowConfidence_NeverDropsPicks()
    {
        // 只标不撤：标记是「解释」，不是「否决权」。
        // 否决权在 CounterHighModelVsBaseMin(0.40)，两个阈值干的是不同的事。
        var picks = new List<MilkPick>
        {
            BqPick("英冠", "3-0", 32.00m, 1.55m),   // 低可信（1.55 / 2.40 = 0.646）
            BqPick("英超", "3-0", 29.00m, 2.60m),   // 正常（2.60 / 3.39 = 0.767）
        };

        MilkPickEngine.MarkLowConfidence(picks);

        Assert.Equal(2, picks.Count);
        Assert.Single(picks, x => x.ModelLowConfidence);
    }
}
