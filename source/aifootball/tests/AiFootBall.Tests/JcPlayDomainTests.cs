using AiFootBall.Core.Models;
using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 标准玩法域（2026-11-14 第十轮）。
///
/// 用户点名的问题：域必须用于**完整性判断、归一化、计算入口**三处，
/// 且**域外脏码不得影响合法档的概率**。生产上半全场确实出现过
/// `平/胜`/`平/负`/`平/平` 三条脏代码（3 场），它们不是「多一档」，是「多一条没有对应
/// 真实选项的报价」。如果它进了 Σ1/赔率，归一因子会变小，**所有合法档的混合概率被一起压低**——
/// 榜上每一注的 EV 都跟着偏低，而且没有任何一处会报错。
///
/// 这里不靠推理，直接跑引擎真实写库路径（`CollectSnapshots`）对比「加脏码前后」：
///   · 合法档的三列（纯模型 / 市场 / 混合）必须逐项相等；
///   · 脏码本身不得出现在存档里；
///   · 完整性判断不能被脏码顶数。
/// </summary>
public class JcPlayDomainTests
{
    private static MatchRecommendation Rec() => new()
    {
        Board = Boards.Jc,
        MatchNo = "周六001",
        MatchId = 910001,
        LeagueName = "测试联赛",
        HomeTeam = "甲队",
        AwayTeam = "乙队",
        MatchTime = DateTime.Today.AddHours(20)
    };

    private static JcOddsHistoryPoint Pt(string play, string code, decimal odds) => new()
    {
        MatchId = 910001,
        MatchNo = "周六001",
        PlayType = play,
        OptionCode = code,
        OptionName = code,
        Odds = odds,
        CapturedAt = DateTime.Now
    };

    /// <summary>半全场 9 档，赔率各不相同（便于看出归一因子变化）。</summary>
    private static List<JcOddsHistoryPoint> BqPoints()
        => JcPlayDomain.Bq
            .Select((code, i) => Pt(JcPlayTypes.Bq, code, 3.0m + i))
            .ToList();

    private static List<JcOddsHistoryPoint> BfPoints()
        => JcPlayDomain.Bf
            .Select((code, i) => Pt(JcPlayTypes.Bf, code, 8.0m + i * 0.5m))
            .ToList();

    private static decimal[,] Joint()
    {
        // 行列都归一（Σ=1），与实际 3×3 联合分布同形：对角 3×0.2333 + 非对角 6×0.05 = 1。
        var j = new decimal[3, 3];
        for (var i = 0; i < 3; i++)
            for (var k = 0; k < 3; k++)
                j[i, k] = i == k ? 0.2333m : 0.05m;
        return j;
    }

    // ─────────────────────────── ① 完整性判断 ───────────────────────────

    [Fact]
    public void SnapshotComplete_RequiresEveryDomainCode_NotJustTheCount()
    {
        var points = BqPoints();
        Assert.True(MilkPickEngine.SnapshotComplete(points, JcPlayTypes.Bq));

        // 一条脏码**顶替**一条合法档：档数仍是 9，按档数会判「齐」。
        var swapped = BqPoints().Where(p => p.OptionCode != "3-1").ToList();
        swapped.Add(Pt(JcPlayTypes.Bq, "平/胜", 3.2m));
        Assert.Equal(9, swapped.Select(p => p.OptionCode).Distinct().Count());
        Assert.False(MilkPickEngine.SnapshotComplete(swapped, JcPlayTypes.Bq));

        // 9 档齐全时多一条脏码：不能因此判不完整（脏码是数据卫生问题，不是缺档）。
        var extra = BqPoints();
        extra.Add(Pt(JcPlayTypes.Bq, "平/胜", 3.2m));
        Assert.True(MilkPickEngine.SnapshotComplete(extra, JcPlayTypes.Bq));
    }

    [Fact]
    public void SnapshotComplete_AcceptsBothSpellingsOfOtherScores()
    {
        // 采集端两种写法都出现过（500 用「胜其它」，个别源用「胜其他」）。
        // 域判定必须认作同一档，否则整场比分会被判「不全」而退回纯模型。
        var points = BfPoints()
            .Select(p => p.OptionCode == "胜其它" ? Pt(JcPlayTypes.Bf, "胜其他", p.Odds) : p)
            .ToList();

        Assert.True(JcPlayDomain.Contains(JcPlayTypes.Bf, "胜其他"));
        Assert.True(MilkPickEngine.SnapshotComplete(points, JcPlayTypes.Bf));
    }

    // ─────────────────────────── ② 归一化 ───────────────────────────

    [Fact]
    public void BlendNormalizer_IgnoresCodesOutsideDomain()
    {
        var clean = BqPoints();
        var dirty = BqPoints();
        dirty.Add(Pt(JcPlayTypes.Bq, "平/胜", 2.0m));   // 1/2.0 = 0.50，足以把因子拉低约 40%

        var over = MilkPickEngine.StandardOverRound(JcPlayTypes.Bq);
        Assert.Equal(
            MilkPickEngine.BlendNormalizer(clean, JcPlayTypes.Bq, over),
            MilkPickEngine.BlendNormalizer(dirty, JcPlayTypes.Bq, over));
        Assert.Equal(
            MilkPickEngine.ObservedOverRound(clean, JcPlayTypes.Bq),
            MilkPickEngine.ObservedOverRound(dirty, JcPlayTypes.Bq));
    }

    [Fact]
    public void BlendNormalizer_DoesNotLeakOtherPlaysSharingOptionCodes()
    {
        // 域判定单独用不够：SPF / NSPF 的代码也是 `0/1/3`，JQ 的域恰好是 `0..7`。
        // 只按「代码在 JQ 域内」过滤，会把 SPF 的三条报价算进 JQ 的分母——
        // 探针实测过：总进球 ΣBlended 从 100 掉到 40.745，而三列合计看着「都有值」。
        var jq = JcPlayDomain.Jq.Select((c, i) => Pt(JcPlayTypes.Jq, c, 4.0m + i)).ToList();
        var withSpf = jq.ToList();
        withSpf.Add(Pt(JcPlayTypes.Spf, "3", 1.80m));
        withSpf.Add(Pt(JcPlayTypes.Spf, "1", 3.40m));
        withSpf.Add(Pt(JcPlayTypes.Spf, "0", 4.20m));

        var over = MilkPickEngine.StandardOverRound(JcPlayTypes.Jq);
        Assert.Equal(
            MilkPickEngine.BlendNormalizer(jq, JcPlayTypes.Jq, over),
            MilkPickEngine.BlendNormalizer(withSpf, JcPlayTypes.Jq, over));
        Assert.Equal(
            MilkPickEngine.ObservedOverRound(jq, JcPlayTypes.Jq),
            MilkPickEngine.ObservedOverRound(withSpf, JcPlayTypes.Jq));
    }

    // ───────────────── ③ 真实写入路径：加脏码前后合法档结果不变 ─────────────────

    [Fact]
    public void CollectSnapshots_DirtyCodeLeavesLegitProbabilitiesUntouched()
    {
        var grid = new List<(int H, int A, double P)> { (1, 0, 0.2), (2, 1, 0.3), (0, 0, 0.25), (1, 1, 0.25) };
        var mass = grid.Sum(c => c.P);
        var bands = MilkPickEngine.GoalBands(grid, mass);
        var bfPure = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["2:1"] = 0.30m, ["胜其它"] = 0.05m };
        var over = MilkPickEngine.StandardOverRound(JcPlayTypes.Bq);

        List<MilkProbSnapshot> Run(List<JcOddsHistoryPoint> points)
        {
            // 归一因子按引擎的算法现算，不写死——否则这条测试根本测不到「分母被脏码污染」。
            var norm = MilkPickEngine.BlendNormalizer(points, JcPlayTypes.Bq, over);
            return MilkPickEngine.CollectSnapshots(
                Rec(), 910001, points, grid, mass,
                bfPure, 1.410m, 1m, bands, 1.254m, 1m, Joint(), over, norm,
                "dc1|domain-test", null);
        }

        var clean = Run(BqPoints());
        var dirtyPoints = BqPoints();
        dirtyPoints.Add(Pt(JcPlayTypes.Bq, "平/胜", 2.0m));
        dirtyPoints.Add(Pt(JcPlayTypes.Bq, "平/负", 2.1m));
        var dirty = Run(dirtyPoints);

        // 存档不因多两条脏码而多出行。
        Assert.Equal(clean.Count, dirty.Count);
        Assert.DoesNotContain(dirty, x => x.OptionCode is "平/胜" or "平/负");

        // 每一档的三列都逐项相等。
        foreach (var row in clean.Where(x => x.PlayType == JcPlayTypes.Bq))
        {
            var other = dirty.Single(x => x.PlayType == JcPlayTypes.Bq && x.OptionCode == row.OptionCode);
            Assert.Equal(row.ModelPctPure, other.ModelPctPure);
            Assert.Equal(row.MarketPct, other.MarketPct);
            Assert.Equal(row.BlendedPct, other.BlendedPct);
        }

        // 且整组合计仍是 100（脏码没有把质量带偏）。
        Assert.InRange(dirty.Where(x => x.PlayType == JcPlayTypes.Bq).Sum(x => x.BlendedPct!.Value),
            99.5m, 100.5m);
    }

    [Fact]
    public void CollectSnapshots_IncompleteDomain_FallsBackToPureModel()
    {
        // 少一档 → SnapshotComplete 为 false → 引擎传 over=0 → 不掺盘口（三列只填纯模型）。
        var points = BqPoints().Where(p => p.OptionCode != "0-0").ToList();
        Assert.False(MilkPickEngine.SnapshotComplete(points, JcPlayTypes.Bq));

        var grid = new List<(int H, int A, double P)> { (1, 0, 0.5), (0, 0, 0.5) };
        var bands = MilkPickEngine.GoalBands(grid, 1.0);
        var rows = MilkPickEngine.CollectSnapshots(
            Rec(), 910001, points, grid, 1.0,
            new Dictionary<string, decimal>(), 0m, 1m, bands, 0m, 1m, Joint(), 0m, 1m,
            "dc1|domain-test", null);

        Assert.All(rows, x => Assert.False(x.MarketPriced));
    }
}
