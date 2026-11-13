using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 审计层三件套（2026-11-12 加，对应第三方校验方案的 P0 清单）。
///
/// 这组测试守的是「事后能不能解释这条注」：
/// 1) 无未来信息：给了 asOf 就必须只用那之前的比赛 —— 旧实现把 asOf 只当衰减原点，
///    未来场次会以权重 1.0 混进拟合，跑 A/B 时测出来的是泄漏后的成绩；
/// 2) 总进球 = 比分网格对角线求和 —— 候选与分布存档必须走同一份算式；
/// 3) 分布存档覆盖全档 —— 不只是进榜那几注，且被闸掉的档也要在。
/// </summary>
public class AuditSnapshotTests
{
    const decimal BqOver = 1.254m;

    // ------------------------------------------------------------------ 无未来信息

    /// <summary>造一段「甲乙两队、每 3 天一场」的联赛样本。</summary>
    static List<ScoreSample> MakeSamples(
        string home, string away, DateTime first, int count, int homeGoals = 2, int awayGoals = 1)
        => Enumerable.Range(0, count).Select(i => new ScoreSample
        {
            LeagueName = "测试联赛",
            HomeTeam = home,
            AwayTeam = away,
            HomeScore = homeGoals,
            AwayScore = awayGoals,
            MatchTime = first.AddDays(i * 3)
        }).ToList();

    [Fact]
    public void Fit_DropsSamplesAfterAsOf()
    {
        var cutoff = new DateTime(2026, 6, 1, 12, 0, 0);
        var before = MakeSamples("甲队", "乙队", cutoff.AddDays(-120), 40);
        // 截止日之后才出现的两支球队：它们在场上的比分绝不能影响截止日之前的模型。
        var after = MakeSamples("丙队", "丁队", cutoff.AddDays(1), 20, 0, 2);

        var asOfOnly = ScorelineModel.Fit(before, asOf: cutoff);
        var withFuture = ScorelineModel.Fit(before.Concat(after).ToList(), asOf: cutoff);

        Assert.True(asOfOnly.Ready);
        Assert.Equal(40, asOfOnly.FitGames);
        // 核心断言：多喂 20 场未来比赛，参与拟合的场次一场都不该变。
        Assert.Equal(40, withFuture.FitGames);
        Assert.True(withFuture.FitTo <= cutoff, $"FitTo={withFuture.FitTo:O} 晚于截止日 {cutoff:O}");
        Assert.False(withFuture.KnowsTeam("测试联赛", "丙队"),
            "截止日之后才出现的球队必须不认识——认识它就说明未来样本混进了拟合。");

        // 不只看计数：同一场对阵的 36 格概率必须逐格相同。
        var a = asOfOnly.Distribution("测试联赛", "甲队", "乙队", null);
        var b = withFuture.Distribution("测试联赛", "甲队", "乙队", null);
        Assert.Equal(a.Count, b.Count);
        Assert.NotEmpty(a);
        for (var i = 0; i < a.Count; i++)
            Assert.Equal(a[i].P, b[i].P, 12);
    }

    [Fact]
    public void Fit_RequiresSampleFinishedBeforeAsOf()
    {
        // 2026-11-13 二次收紧（用户令）：asOf 是「拟合时刻」，样本必须**在该时刻已经踢完**。
        // 判据 = 开赛 + MatchDurationMinutes(150) ≤ asOf，不再只判「开赛 ≤ asOf」。
        // 旧口径在历史回测里是泄漏：截止 10-14 23:00 拟合时，10-14 22:30 开赛的那场当时才踢半小时，
        // 却因为「今天库里有比分」被算成已知样本。
        var cutoff = new DateTime(2026, 6, 1, 12, 0, 0);
        var lastKickoff = cutoff.AddMinutes(-ScorelineModel.MatchDurationMinutes);
        var samples = MakeSamples("甲队", "乙队", lastKickoff.AddDays(-3 * 39), 40);

        var model = ScorelineModel.Fit(samples, asOf: cutoff);

        Assert.Equal(40, model.FitGames);
        Assert.Equal(lastKickoff, model.FitTo);

        // 再塞一场「开赛早于 cutoff、但还没有踢完」的：必须被剔掉，且概率不受影响。
        var stillPlaying = samples.Append(new ScoreSample
        {
            LeagueName = "测试联赛",
            HomeTeam = "甲队",
            AwayTeam = "乙队",
            HomeScore = 9,
            AwayScore = 0,
            MatchTime = cutoff.AddMinutes(-30)
        }).ToList();

        var m2 = ScorelineModel.Fit(stillPlaying, asOf: cutoff);

        Assert.Equal(40, m2.FitGames);
        Assert.Equal(lastKickoff, m2.FitTo);
        var a = model.Distribution("测试联赛", "甲队", "乙队", null);
        var b = m2.Distribution("测试联赛", "甲队", "乙队", null);
        for (var i = 0; i < a.Count; i++)
            Assert.Equal(a[i].P, b[i].P, 12);
    }

    [Fact]
    public void Fit_WithoutAsOf_StillUsesEverything()
    {
        // 不给 asOf 时不额外过滤：这是回测/研究口径（那些工具自己带 MatchTime 闸）。
        // 线上从 2026-11-13 起一律显式传 asOf（HitProbabilityService、BacktestMilkAsync），
        // 所以「线上恰好走这条路径」这句话已经不成立了——别拿它当线上口径的证明。
        var cutoff = new DateTime(2026, 6, 1);
        var samples = MakeSamples("甲队", "乙队", cutoff.AddDays(-120), 40)
            .Concat(MakeSamples("丙队", "丁队", cutoff.AddDays(1), 20)).ToList();

        var model = ScorelineModel.Fit(samples);

        Assert.Equal(60, model.FitGames);
        Assert.True(model.KnowsTeam("测试联赛", "丙队"));
    }

    // ------------------------------------------------------------------ 总进球 = 对角线

    [Fact]
    public void GoalBands_EqualDiagonalSums_AndSumToOne()
    {
        // 手写一份网格，故意让 total mass ≠ 1（0.97），用来验证归一化也一起钉住。
        var grid = new List<(int H, int A, double P)>
        {
            (0, 0, 0.10),
            (1, 0, 0.20),
            (0, 1, 0.15),
            (1, 1, 0.22),
            (2, 1, 0.18),
            (2, 2, 0.12)
        };
        var mass = grid.Sum(c => c.P);

        var bands = MilkPickEngine.GoalBands(grid, mass);

        // 逐档 = 该总进球的所有格子之和 ÷ 网格总质量。
        Assert.Equal(0.10 / mass, (double)bands[0], 10);   // 0:0
        Assert.Equal(0.35 / mass, (double)bands[1], 10);   // 1:0 + 0:1
        Assert.Equal(0.22 / mass, (double)bands[2], 10);   // 1:1
        Assert.Equal(0.18 / mass, (double)bands[3], 10);   // 2:1
        Assert.Equal(0.12 / mass, (double)bands[4], 10);   // 2:2
        Assert.Equal(0m, bands[5]);
        Assert.Equal(0m, bands[6]);
        // 7+ 是余量：网格截断到 6 球，这里全部质量都在 0~4，所以余量必须是 0，不是负数。
        Assert.Equal(0m, bands[7]);

        Assert.Equal(1m, bands.Sum(), 10);
    }

    [Fact]
    public void GoalBands_SevenPlus_IsTheRemainder()
    {
        // 网格只覆盖到 2 球（模拟极端截断），3 球以上必须由 7+ 档的余量接住。
        var grid = new List<(int H, int A, double P)> { (0, 0, 0.40), (1, 0, 0.35), (1, 1, 0.25) };
        var bands = MilkPickEngine.GoalBands(grid, grid.Sum(c => c.P));

        Assert.Equal(0.40, (double)bands[0], 10);
        Assert.Equal(0.35, (double)bands[1], 10);
        Assert.Equal(0.25, (double)bands[2], 10);
        Assert.Equal(0m, bands[7]);            // 质量已全在 0~2 球
        Assert.Equal(1m, bands.Sum(), 10);
    }

    // ------------------------------------------------------------------ 分布存档

    static MatchRecommendation Rec() => new()
    {
        Board = Boards.Jc,
        MatchNo = "周四001",
        MatchId = 900001,
        LeagueName = "测试联赛",
        HomeTeam = "甲队",
        AwayTeam = "乙队",
        MatchTime = DateTime.Today.AddHours(20)
    };

    static JcOddsHistoryPoint Pt(string play, string code, decimal odds) => new()
    {
        MatchId = 900001,
        MatchNo = "周四001",
        PlayType = play,
        OptionCode = code,
        OptionName = code,
        Odds = odds,
        CapturedAt = DateTime.Now
    };

    static List<JcOddsHistoryPoint> Points() => new()
    {
        Pt(JcPlayTypes.Bf, "2:1", 8.0m),
        Pt(JcPlayTypes.Bf, "胜其它", 10.0m),
        Pt(JcPlayTypes.Jq, "2", 3.5m),
        Pt(JcPlayTypes.Jq, "7", 30.0m),
        Pt(JcPlayTypes.Bq, "3-3", 2.5m),
        Pt(JcPlayTypes.Bq, "1-1", 5.0m),
        // 非法档位：解析不出来，不该出现在存档里
        Pt(JcPlayTypes.Jq, "9", 50.0m)
    };

    static decimal[,] Joint()
    {
        var j = new decimal[3, 3];
        j[0, 0] = 0.30m; j[1, 1] = 0.20m; j[2, 2] = 0.10m;
        j[0, 1] = 0.10m; j[1, 0] = 0.05m; j[2, 1] = 0.10m;
        j[0, 2] = 0.05m; j[1, 2] = 0.05m; j[2, 0] = 0.05m;
        return j;
    }

    [Fact]
    public void CollectSnapshots_CoversEveryOption_NotJustPicks()
    {
        var grid = new List<(int H, int A, double P)> { (1, 0, 0.2), (2, 1, 0.3), (0, 0, 0.25), (1, 1, 0.25) };
        var mass = grid.Sum(c => c.P);
        var bands = MilkPickEngine.GoalBands(grid, mass);
        var bfPure = new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["2:1"] = 30m,
            ["胜其它"] = 5m
        };

        // picks 故意只放一注：存档的档位数必须和 picks 的条数无关。
        // 注意档位写法是「半场结果-全场结果」，3=胜 1=平 0=负：3-3 才是「胜胜」。
        var picks = new List<MilkPick> { new() { PlayType = JcPlayTypes.Bq, OptionCode = "3-3" } };

        var rows = MilkPickEngine.CollectSnapshots(
            Rec(), 900001, Points(), grid, mass,
            bfPure, 1.410m, 1m, bands, 1.253m, 1m, Joint(), 1.254m, 1m,
            "dc1|test", picks);

        // 7 条赔率里有 1 条非法（总进球 9），存档 = 6 行。
        Assert.Equal(6, rows.Count);
        Assert.Contains(rows, x => x.PlayType == JcPlayTypes.Jq && x.OptionCode == "7");
        Assert.DoesNotContain(rows, x => x.OptionCode == "9");

        // 总进球档必须与 GoalBands 完全一致（同一份算式，不是另算一遍）。
        var jq2 = rows.Single(x => x.PlayType == JcPlayTypes.Jq && x.OptionCode == "2");
        Assert.Equal(Math.Round(bands[2] * 100m, 3), jq2.ModelPctPure);

        // 半全场取联合分布对角：3-3 = joint[胜,胜]。
        var bq33 = rows.Single(x => x.PlayType == JcPlayTypes.Bq && x.OptionCode == "3-3");
        Assert.Equal(30.000m, bq33.ModelPctPure);

        // 比分：纯模型值来自 bfPure，不在这里重新解析。
        var bf21 = rows.Single(x => x.PlayType == JcPlayTypes.Bf && x.OptionCode == "2:1");
        Assert.Equal(30m, bf21.ModelPctPure);

        // 三列必须同时有值，且混合值落在纯模型与市场隐含之间（w 在 0~1 之间）。
        Assert.All(rows, x =>
        {
            Assert.True(x.MarketPriced);
            Assert.NotNull(x.MarketPct);
            Assert.NotNull(x.ModelPctPure);
            Assert.NotNull(x.BlendedPct);
            var pure = x.ModelPctPure!.Value;
            var mk = x.MarketPct!.Value;
            Assert.InRange(x.BlendedPct!.Value,
                Math.Min(pure, mk) - 0.5m, Math.Max(pure, mk) + 0.5m);
        });
    }

    [Fact]
    public void CollectSnapshots_IncompleteSnapshot_FallsBackToPureModel()
    {
        var grid = new List<(int H, int A, double P)> { (1, 0, 0.2), (2, 1, 0.3), (0, 0, 0.25), (1, 1, 0.25) };
        var mass = grid.Sum(c => c.P);
        var bands = MilkPickEngine.GoalBands(grid, mass);
        var bfPure = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["2:1"] = 30m, ["胜其它"] = 5m };

        // overRound = 0 表示该玩法快照不全 → 不掺盘口。
        var rows = MilkPickEngine.CollectSnapshots(
            Rec(), 900001, Points(), grid, mass,
            bfPure, 0m, 1m, bands, 0m, 1m, Joint(), 0m, 1m,
            "dc1|test", null);

        Assert.All(rows, x =>
        {
            Assert.False(x.MarketPriced);
            Assert.Null(x.MarketPct);
            // 快照不全时混合值必须等于纯模型值 —— 这是「没有参照物」的显式表达，不能留空。
            Assert.Equal(x.ModelPctPure, x.BlendedPct);
        });
    }

    [Fact]
    public void CollectSnapshots_DuplicateOptionCode_CollapsesToOne()
    {
        // 采集端偶发把同一档写进快照两次（两次采样都取到了）。存档若不自己去重，
        // 「覆盖了几档」这个数会虚高，档位覆盖率统计就错了。
        var grid = new List<(int H, int A, double P)> { (1, 0, 0.2), (2, 1, 0.3), (0, 0, 0.25), (1, 1, 0.25) };
        var mass = grid.Sum(c => c.P);
        var bands = MilkPickEngine.GoalBands(grid, mass);
        var bfPure = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["2:1"] = 30m, ["胜其它"] = 5m };

        var pts = Points();
        pts.Add(Pt(JcPlayTypes.Jq, "2", 3.8m));   // 同档第二次出现，价也变了

        var rows = MilkPickEngine.CollectSnapshots(
            Rec(), 900001, pts, grid, mass,
            bfPure, 1.410m, 1m, bands, 1.253m, 1m, Joint(), 1.254m, 1m,
            "dc1|test", null);

        var jq2 = rows.Where(x => x.PlayType == JcPlayTypes.Jq && x.OptionCode == "2").ToList();
        Assert.Single(jq2);
        Assert.Equal(3.8m, jq2[0].Odds);   // 保留后出现的那条（points 按采集时间排）

        // 总档数仍是 6（7 条赔率里 1 条非法档位），没有因为重复而变多。
        Assert.Equal(6, rows.Count);
    }

    [Fact]
    public void CollectSnapshots_CoversSpfAndNspf_NotJustThreePlays()
    {
        // 2026-11-13 补：此前只存 BF/BQ/JQ。而 SPF 正是方向闸判「模型 vs 市场」用的量，
        // 最该复盘的一列反而没存；NSPF 有 3 档赔率却一行不写。
        var grid = new List<(int H, int A, double P)> { (1, 0, 0.2), (2, 1, 0.3), (0, 0, 0.25), (1, 1, 0.25) };
        var mass = grid.Sum(c => c.P);   // = 1.0
        var bands = MilkPickEngine.GoalBands(grid, mass);
        var bfPure = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["2:1"] = 30m, ["胜其它"] = 5m };

        var pts = new List<JcOddsHistoryPoint>
        {
            Pt(JcPlayTypes.Spf, "3", 2.10m),
            Pt(JcPlayTypes.Spf, "1", 3.20m),
            Pt(JcPlayTypes.Spf, "0", 3.40m),
            Pt(JcPlayTypes.Nspf, "3", 3.10m),
            Pt(JcPlayTypes.Nspf, "1", 3.60m),
            Pt(JcPlayTypes.Nspf, "0", 2.00m)
        };

        var rec = Rec();
        rec.Handicap = -1m;   // 主让一球：让平 = 净胜恰 1 球

        var rows = MilkPickEngine.CollectSnapshots(
            rec, 900001, pts, grid, mass,
            bfPure, 0m, 1m, bands, 0m, 1m, null, 0m, 1m,
            "dc1|test", null);

        Assert.Equal(6, rows.Count);
        Assert.Equal(3, rows.Count(x => x.PlayType == JcPlayTypes.Spf));
        Assert.Equal(3, rows.Count(x => x.PlayType == JcPlayTypes.Nspf));

        // 胜平负：网格里主胜 0.5（1:0 + 2:1）、平 0.5（0:0 + 1:1）、客胜 0。
        Assert.Equal(50m, rows.Single(x => x.PlayType == JcPlayTypes.Spf && x.OptionCode == "3").ModelPctPure);
        Assert.Equal(50m, rows.Single(x => x.PlayType == JcPlayTypes.Spf && x.OptionCode == "1").ModelPctPure);
        Assert.Equal(0m, rows.Single(x => x.PlayType == JcPlayTypes.Spf && x.OptionCode == "0").ModelPctPure);

        // 让球 −1：净胜 1 的两格（1:0、2:1）合计 0.5 → 让平 50%；净胜 0 的两格合计 0.5 → 让负 50%；让胜 0。
        Assert.Equal(50m, rows.Single(x => x.PlayType == JcPlayTypes.Nspf && x.OptionCode == "1").ModelPctPure);
        Assert.Equal(50m, rows.Single(x => x.PlayType == JcPlayTypes.Nspf && x.OptionCode == "0").ModelPctPure);
        Assert.Equal(0m, rows.Single(x => x.PlayType == JcPlayTypes.Nspf && x.OptionCode == "3").ModelPctPure);

        // 三档齐全，说明水位/去水走的是同一套 StandardOverRound + SnapshotComplete。
        Assert.All(rows, x => Assert.True(x.MarketPriced));
    }

    [Fact]
    public void CollectSnapshots_Nspf_HandicapSignIsNotInverted()
    {
        // 让球数的符号是这套代码里最容易写反的地方（正=主受让、负=主让球）。
        // 同一条网格，把让球从 −1 翻到 +1，「让胜」「让负」必须互换。
        var grid = new List<(int H, int A, double P)> { (1, 0, 0.2), (2, 1, 0.3), (0, 0, 0.25), (1, 1, 0.25) };
        var mass = grid.Sum(c => c.P);
        var bands = MilkPickEngine.GoalBands(grid, mass);

        var pts = new List<JcOddsHistoryPoint>
        {
            Pt(JcPlayTypes.Nspf, "3", 3.10m),
            Pt(JcPlayTypes.Nspf, "1", 3.60m),
            Pt(JcPlayTypes.Nspf, "0", 2.00m)
        };

        decimal PureOf(decimal hcp, string code)
        {
            var r = Rec();
            r.Handicap = hcp;
            var rows = MilkPickEngine.CollectSnapshots(
                r, 900001, pts, grid, mass,
                new Dictionary<string, decimal>(), 0m, 1m, bands, 0m, 1m, null, 0m, 1m,
                "dc1|test", null);
            return rows.Single(x => x.OptionCode == code).ModelPctPure!.Value;
        }

        // 主让一球（−1）：净胜 ≥2 才让胜 → 这里没有净胜 2 的格 → 0。
        Assert.Equal(0m, PureOf(-1m, "3"));
        Assert.Equal(50m, PureOf(-1m, "1"));   // 净胜恰 1
        Assert.Equal(50m, PureOf(-1m, "0"));   // 净胜 ≤0

        // 主受让一球（+1）：净胜 ≥0 全算让胜 → 100%，让平/让负都是 0。
        Assert.Equal(100m, PureOf(1m, "3"));
        Assert.Equal(0m, PureOf(1m, "1"));
        Assert.Equal(0m, PureOf(1m, "0"));
    }

    [Fact]
    public void CollectSnapshots_Nspf_WithoutHandicapLine_RecordsOddsButNoPureModel()
    {
        // 拿不到让球数时不编概率：档位与价格照记，纯模型列留空（null ≠ 0）。
        var grid = new List<(int H, int A, double P)> { (1, 0, 0.5), (0, 0, 0.5) };
        var bands = MilkPickEngine.GoalBands(grid, 1.0);
        var pts = new List<JcOddsHistoryPoint>
        {
            Pt(JcPlayTypes.Nspf, "3", 3.10m),
            Pt(JcPlayTypes.Nspf, "1", 3.60m),
            Pt(JcPlayTypes.Nspf, "0", 2.00m)
        };

        var rec = Rec();
        rec.Handicap = null;

        var rows = MilkPickEngine.CollectSnapshots(
            rec, 900001, pts, grid, 1.0,
            new Dictionary<string, decimal>(), 0m, 1m, bands, 0m, 1m, null, 0m, 1m,
            "dc1|test", null);

        Assert.Equal(3, rows.Count);
        Assert.All(rows, x =>
        {
            Assert.Null(x.ModelPctPure);
            Assert.NotNull(x.Odds);
        });
    }

    // ------------------------------------------------------------------ 存档与方向闸的先后

    /// <summary>
    /// 被整场撤掉的场次也必须进存档（2026-11-13 加）。
    ///
    /// 起因：11-13 有 3 场进引擎（`FbMilkGate` 3 行），存档只有 1 场。原因是
    /// `CollectSnapshots` 放在方向闸之后，而 `MarketStrongReverse` / `EnforceDirectionVeto`
    /// 两条分支都是 `continue` —— 被撤的场次一行都不写。
    /// 而被撤的场次**恰恰是最该事后审计的**：「为什么没推这场」只能靠这时的分布回答。
    ///
    /// 这条测试的形状来自 11-08 埃尔切 vs 贝蒂斯：市场客胜大热、模型指主胜。
    /// </summary>
    [Fact]
    public void BuildDetailed_MatchBlockedByGate_StillArchived()
    {
        var model = StrongHomeModel();
        var rec = new MatchRecommendation
        {
            Board = Boards.Jc,
            MatchNo = "周三001",
            MatchId = 1,
            LeagueName = "英超",
            HomeTeam = "主队",
            AwayTeam = "客队",
            MatchTime = DateTime.Today.AddHours(20),
            TotalLine = 2.5m,
            Handicap = -1m
        };

        JcOddsHistoryPoint P(string play, string code, decimal odds) => new()
        {
            MatchId = 1, PlayType = play, OptionCode = code, OptionName = code, Odds = odds
        };

        var odds = new Dictionary<long, List<JcOddsHistoryPoint>>
        {
            [1] = new()
            {
                P(JcPlayTypes.Spf, "3", 3.62m),
                P(JcPlayTypes.Spf, "1", 3.60m),
                P(JcPlayTypes.Spf, "0", 1.71m),   // 客胜去水 51.4%，市场大热
                P(JcPlayTypes.Nspf, "3", 2.40m),
                P(JcPlayTypes.Nspf, "1", 3.60m),
                P(JcPlayTypes.Nspf, "0", 2.30m),
                P(JcPlayTypes.Bq, "3-3", 6.0m),
                P(JcPlayTypes.Bq, "1-1", 6.0m),
                P(JcPlayTypes.Bq, "0-0", 6.0m)
            }
        };

        var r = MilkPickEngine.BuildDetailed(new[] { rec }, odds, model);

        // 先确认这个 fixture 真的被撤了，否则测的就不是要测的东西。
        Assert.True(r.Funnel.BlockedByGate >= 1,
            $"这个 fixture 本应被方向闸整场撤掉，实测 BlockedByGate={r.Funnel.BlockedByGate}");
        Assert.DoesNotContain(r.Picks, x => x.MatchId == 1);

        // 核心断言：没出注，但分布存档必须在。
        var snap = r.ProbSnapshots.Where(x => x.MatchId == 1).ToList();
        Assert.NotEmpty(snap);
        Assert.Contains(snap, x => x.PlayType == JcPlayTypes.Spf);
        Assert.Contains(snap, x => x.PlayType == JcPlayTypes.Nspf);
        // 版本戳必须一路带到存档行（第二个独立来源）。
        // 这里只断言前缀：本 fixture 的样本没有 MatchTime，窗口段会是 ?..? —— 那是取样字段的问题，
        // 由 fit-probe 在真库上验，不在这个单测的职责里。
        Assert.All(snap, x => Assert.StartsWith("dc1|", x.ModelVersion));
    }

    /// <summary>主队强、客队弱的模型，用来复现「模型指主胜、市场指客胜」的背离形态。</summary>
    static ScorelineModel StrongHomeModel()
    {
        var samples = new List<ScoreSample>();
        for (var i = 0; i < 20; i++)
        {
            samples.Add(new ScoreSample { LeagueName = "英超", HomeTeam = "主队", AwayTeam = "客" + i, HomeScore = 2, AwayScore = 1 });
            samples.Add(new ScoreSample { LeagueName = "英超", HomeTeam = "客队", AwayTeam = "主" + i, HomeScore = 1, AwayScore = 2 });
        }
        return ScorelineModel.Fit(samples);
    }

    // ------------------------------------------------------------------ 版本戳

    [Fact]
    public void PickVersion_CarriesFrozenWeightsAndBuild()
    {
        var v = MilkPickEngine.PickVersion(ScorelineModel.Empty);

        Assert.StartsWith("dc-none|", v);
        // 五个玩法的权重按 SPF/NSPF/BQ/JQ/BF 排列，写成百分数整数，避开 culture 小数点。
        Assert.Contains("w=70/70/50/80/30", v);
        Assert.Contains(MilkPickEngine.EnableScorePicks ? "bf=on" : "bf=off", v);
        // 构建标识必须存在（取不到时是 unknown，也不能是空串）。
        Assert.DoesNotContain("||", v);
    }

    [Fact]
    public void Fit_StampsWindowAndRho()
    {
        var cutoff = new DateTime(2026, 6, 1);
        var samples = MakeSamples("甲队", "乙队", cutoff.AddDays(-120), 40);
        var model = ScorelineModel.Fit(samples, asOf: cutoff);

        Assert.Equal(40, model.FitGames);
        Assert.NotNull(model.FitFrom);
        Assert.NotNull(model.FitTo);
        Assert.Contains("n=40", model.VersionTag);
        Assert.Contains($"rho={model.Rho:0.000}", model.VersionTag);
        Assert.Contains(model.FitFrom!.Value.ToString("yyyy-MM-dd"), model.VersionTag);
    }
}
