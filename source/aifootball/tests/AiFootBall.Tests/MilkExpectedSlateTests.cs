using AiFootBall.Core.Models;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 发布前核对的「独立预期」（2026-11-13 第五轮）。
///
/// 上一版的预期取自引擎输出（`build.ProbSnapshots`），只能证明「算出来的都写进去了」。
/// 这里钉住的是另一半：**该算的都算了没有**。所以每个用例都从「输入里有哪些档」出发，
/// 再故意让引擎少生成一场、少生成一档，核对必须失败。
/// </summary>
public class MilkExpectedSlateTests
{
    private static MilkInputMatch Match(long id, params (string Play, string Code)[] options)
        => new()
        {
            MatchId = id,
            MatchNo = $"周{id}",
            LeagueName = "测试联赛",
            HomeTeam = $"主{id}",
            AwayTeam = $"客{id}",
            Options = options
                .Select(o => new MilkInputOption { PlayType = o.Play, OptionCode = o.Code, Odds = 3m })
                .ToList()
        };

    private static readonly (string, string)[] ThreeWay =
        { (JcPlayTypes.Spf, "3"), (JcPlayTypes.Spf, "1"), (JcPlayTypes.Spf, "0") };

    private static readonly (string, string)[] NineHalfFull = MilkExpectedSlate.Domains.HalfFull
        .Select(c => (JcPlayTypes.Bq, c)).ToArray();

    private static readonly (string, string)[] EightGoals = Enumerable.Range(0, 8)
        .Select(n => (JcPlayTypes.Jq, n.ToString())).ToArray();

    private static List<MilkLedgerRow> ActualFrom(MilkSlateBuild slate)
        => slate.Rows.Select(r => new MilkLedgerRow { Key = r.Key }).ToList();

    [Fact]
    public void Build_每档一条_与输入档位数一致()
    {
        var slate = MilkExpectedSlate.Build(new[]
        {
            Match(1, ThreeWay.Concat(NineHalfFull).Concat(EightGoals).ToArray())
        });

        Assert.Equal(3 + 9 + 8, slate.Rows.Count);
        Assert.Empty(slate.Exemptions);
        Assert.Empty(slate.IgnoreKeys);
    }

    [Fact]
    public void Build_同一档重复出现_只算一档()
    {
        // 采集端偶发同档两行；存档侧在 CollectSnapshots 里按档去重，预期必须同口径，
        // 否则「预期 2、实际 1」会被当成漏写。
        var slate = MilkExpectedSlate.Build(new[]
        {
            Match(1, (JcPlayTypes.Spf, "3"), (JcPlayTypes.Spf, "3"), (JcPlayTypes.Spf, "3"))
        });

        Assert.Single(slate.Rows);
    }

    [Fact]
    public void Build_整场没有赔率档_记no_odds而不是当作漏写()
    {
        var slate = MilkExpectedSlate.Build(new[] { Match(9) });

        Assert.Empty(slate.Rows);
        var e = Assert.Single(slate.Exemptions);
        Assert.Equal(MilkSlateExemption.ReasonNoOdds, e.Reason);
        Assert.True(e.IsKnown);
    }

    [Fact]
    public void Build_档位代码超出玩法域_记原因且不进预期()
    {
        var slate = MilkExpectedSlate.Build(new[]
        {
            Match(1,
                (JcPlayTypes.Jq, "8"),      // 总进球只到 7+
                (JcPlayTypes.Bq, "2-1"),    // 半全场九个组合之外的脏代码
                (JcPlayTypes.Spf, "X"))     // 三向之外的脏代码
        });

        Assert.Empty(slate.Rows);
        Assert.Equal(3, slate.Exemptions.Count);
        Assert.All(slate.Exemptions, e => Assert.Equal(MilkSlateExemption.ReasonCodeOutsideDomain, e.Reason));
        Assert.All(slate.Exemptions, e => Assert.True(e.IsKnown));
    }

    [Fact]
    public void Build_玩法名不认识_记unknown_play()
    {
        var slate = MilkExpectedSlate.Build(new[] { Match(1, ("XYZ", "1")) });

        Assert.Empty(slate.Rows);
        Assert.Equal(MilkSlateExemption.ReasonUnknownPlay, Assert.Single(slate.Exemptions).Reason);
    }

    [Fact]
    public void 引擎少生成一整场_必须失败()
    {
        var m1 = Match(1, ThreeWay);
        var m2 = Match(2, ThreeWay);
        var slate = MilkExpectedSlate.Build(new[] { m1, m2 });

        // 引擎只写了 1 号场，2 号场一行都没写。
        var actual = slate.Rows
            .Where(r => r.Key.StartsWith("1|", StringComparison.Ordinal))
            .Select(r => new MilkLedgerRow { Key = r.Key })
            .ToList();

        var report = MilkRunLedger.Verify(new[]
        {
            new MilkLedgerPair { Table = "snapshot", Expected = slate.Rows, Actual = actual, Ignore = slate.IgnoreKeys }
        });

        Assert.False(report.Ok);
        Assert.Equal(3, report.Missing);   // 2 号场 3 个档全缺
    }

    [Fact]
    public void 引擎少生成一档_必须失败()
    {
        var slate = MilkExpectedSlate.Build(new[] { Match(1, ThreeWay) });
        var actual = ActualFrom(slate);
        actual.RemoveAll(r => r.Key.EndsWith("|SPF|1", StringComparison.Ordinal));

        var report = MilkRunLedger.Verify(new[]
        {
            new MilkLedgerPair { Table = "snapshot", Expected = slate.Rows, Actual = actual, Ignore = slate.IgnoreKeys }
        });

        Assert.False(report.Ok);
        Assert.Equal(1, report.Missing);
        Assert.Contains("SPF|1", report.Detail);
    }

    [Fact]
    public void 同一档写了两行_报重复()
    {
        var slate = MilkExpectedSlate.Build(new[] { Match(1, ThreeWay) });
        var actual = ActualFrom(slate);
        actual.Add(new MilkLedgerRow { Key = actual[0].Key });

        var report = MilkRunLedger.Verify(new[]
        {
            new MilkLedgerPair { Table = "snapshot", Expected = slate.Rows, Actual = actual, Ignore = slate.IgnoreKeys }
        });

        Assert.False(report.Ok);
        Assert.Equal(1, report.Duplicated);
    }

    [Fact]
    public void 申报了半全场联合分布缺失_不算漏写且原因入库()
    {
        var m = Match(1, ThreeWay.Concat(NineHalfFull).ToArray());
        var exempts = NineHalfFull.Select(o => new MilkSlateExemption
        {
            MatchId = 1, MatchNo = "周1",
            PlayType = o.Item1, OptionCode = o.Item2,
            Reason = MilkSlateExemption.ReasonHalfFullJointMissing,
            Evidence = "points=9;joint=absent"
        }).ToList();

        var slate = MilkExpectedSlate.Build(new[] { m }, exempts);

        // 预期只剩胜平负三档，半全场九档被豁免
        Assert.Equal(3, slate.Rows.Count);
        Assert.Equal(9, slate.Exemptions.Count);
        Assert.Empty(slate.Rejected);

        var actual = ActualFrom(slate);  // 引擎实际也只写了三档
        var report = MilkRunLedger.Verify(new[]
        {
            new MilkLedgerPair { Table = "snapshot", Expected = slate.Rows, Actual = actual, Ignore = slate.IgnoreKeys }
        }, null, slate.Summary);

        Assert.True(report.Ok);
        Assert.Contains("bq-joint-unavailable 9", report.ExemptSummary);
    }

    [Fact]
    public void 豁免的档若真的写了_也不算多写()
    {
        // 同一档既在豁免清单里、又出现在实际里：一致，不该报 unexpected。
        var m = Match(1, (JcPlayTypes.Jq, "8"));
        var slate = MilkExpectedSlate.Build(new[] { m });
        var actual = new List<MilkLedgerRow> { new() { Key = MilkRunLedger.Keys.Snapshot(1, JcPlayTypes.Jq, "8") } };

        var report = MilkRunLedger.Verify(new[]
        {
            new MilkLedgerPair { Table = "snapshot", Expected = slate.Rows, Actual = actual, Ignore = slate.IgnoreKeys }
        });

        Assert.True(report.Ok);
    }

    [Fact]
    public void 未知原因的豁免_必须报出来()
    {
        // 原因只能是固定枚举；随便写一个「我觉得不该写」不能被当成合法缺项。
        var slate = MilkExpectedSlate.Build(new[] { Match(1, ThreeWay) }, new[]
        {
            new MilkSlateExemption
            {
                MatchId = 1, PlayType = JcPlayTypes.Spf, OptionCode = "3",
                Reason = "看着不顺眼", Evidence = "感觉=不对"
            }
        });

        Assert.Single(slate.Rejected);
        Assert.False(slate.Rejected[0].IsKnown);
        Assert.Contains("不在枚举内", slate.Rejected[0].Defect);
    }

    // ── 第六轮：豁免必须带证据，engine-skipped 不接受 ──────────────────────────

    [Fact]
    public void 引擎整体跳过_不接受作为豁免()
    {
        // 上一版把 engine-skipped 放进枚举 = 一条「整场什么都可以不写」的通用放行口子。
        // 现在即使带上了格式正确的证据，也必须判失败。
        var slate = MilkExpectedSlate.Build(new[] { Match(1, ThreeWay) }, new[]
        {
            new MilkSlateExemption
            {
                MatchId = 1, PlayType = "*", OptionCode = "*",
                Reason = MilkSlateExemption.ReasonEngineSkipped, Evidence = "options=0"
            }
        });

        var e = Assert.Single(slate.Rejected);
        Assert.True(e.IsKnown);            // 原因认得出来
        Assert.False(e.Accepted);          // 但不接受
        Assert.Contains("engine-skipped", e.Defect, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 引擎整体跳过的豁免_不把该场的档从预期里抹掉()
    {
        // 被拒的豁免不许顺手 ignore：否则「拒绝」和「放行」在账本上表现一样。
        var slate = MilkExpectedSlate.Build(new[] { Match(1, ThreeWay) }, new[]
        {
            new MilkSlateExemption
            {
                MatchId = 1, PlayType = "*", OptionCode = "*",
                Reason = MilkSlateExemption.ReasonEngineSkipped, Evidence = "options=0"
            }
        });

        Assert.Equal(3, slate.Rows.Count);
        Assert.Empty(slate.IgnoreKeys);

        var report = MilkRunLedger.Verify(new[]
        {
            new MilkLedgerPair { Table = "snapshot", Expected = slate.Rows, Actual = Array.Empty<MilkLedgerRow>(), Ignore = slate.IgnoreKeys }
        }, slate.Rejected.Select(r => new MilkLedgerIssue { Table = "snapshot", Kind = "rejected-exemption", Key = r.Key }).ToList());

        Assert.False(report.Ok);
        Assert.Equal(3, report.Missing);        // 三档仍然算缺
        Assert.Equal(1, report.ExemptDefects);  // 且豁免本身站不住
    }

    [Fact]
    public void 合法原因但没有证据_判失败()
    {
        var slate = MilkExpectedSlate.Build(new[] { Match(1, ThreeWay.Concat(NineHalfFull).ToArray()) },
            NineHalfFull.Select(o => new MilkSlateExemption
            {
                MatchId = 1, PlayType = o.Item1, OptionCode = o.Item2,
                Reason = MilkSlateExemption.ReasonHalfFullJointMissing   // 故意的：没写 Evidence
            }).ToList());

        Assert.Equal(9, slate.Rejected.Count);
        Assert.All(slate.Rejected, e => Assert.Contains("缺证据", e.Defect));
        Assert.Empty(slate.IgnoreKeys);   // 没证据就不享受豁免
    }

    [Fact]
    public void 证据与原因不符_判失败()
    {
        // 原因说「联合分布缺失」，证据却是「options=0」——对不上。
        var slate = MilkExpectedSlate.Build(new[] { Match(1, ThreeWay.Concat(NineHalfFull).ToArray()) },
            NineHalfFull.Select(o => new MilkSlateExemption
            {
                MatchId = 1, PlayType = o.Item1, OptionCode = o.Item2,
                Reason = MilkSlateExemption.ReasonHalfFullJointMissing, Evidence = "options=0"
            }).ToList());

        Assert.Equal(9, slate.Rejected.Count);
        Assert.All(slate.Rejected, e => Assert.Contains("证据与原因不符", e.Defect));
    }

    [Fact]
    public void 自动识别的豁免_自带证据()
    {
        var slate = MilkExpectedSlate.Build(new[]
        {
            Match(1),                                        // 整场无赔率
            Match(2, (JcPlayTypes.Jq, "8")),                 // 域外档
            Match(3, ("XYZ", "1"))                           // 未知玩法
        });

        Assert.Equal(3, slate.Exemptions.Count);
        Assert.Empty(slate.Rejected);                        // 自动识别的都带证据，全部成立
        Assert.Contains(slate.Exemptions, e => e.Evidence == "options=0");
        Assert.Contains(slate.Exemptions, e => e.Evidence == "code=JQ/8");
        Assert.Contains(slate.Exemptions, e => e.Evidence == "play=XYZ");
    }

    [Fact]
    public void 赔率缺失却出了票_判失败()
    {
        // no-odds 只说明「没有可存档的赔率档」，不能同时理解成「可以照常出票」。
        var slate = MilkExpectedSlate.Build(new[] { Match(9) });
        Assert.Single(slate.Exemptions);

        var picks = new List<MilkPick>
        {
            new() { MatchId = 9, MatchNo = "周9", PlayType = JcPlayTypes.Spf, OptionCode = "3" }
        };

        var issues = MilkExpectedSlate.CrossCheck(slate, picks);

        var one = Assert.Single(issues);
        Assert.Equal("exempt-violated", one.Kind);
        Assert.Contains("no-odds", one.Key);
    }

    [Fact]
    public void 赔率缺失且没有出票_不误报()
    {
        var slate = MilkExpectedSlate.Build(new[] { Match(9) });
        var picks = new List<MilkPick>
        {
            new() { MatchId = 10, MatchNo = "周10", PlayType = JcPlayTypes.Spf, OptionCode = "3" }
        };

        Assert.Empty(MilkExpectedSlate.CrossCheck(slate, picks));
    }

    [Fact]
    public void 输入为空_预期为空_不报失败()
    {
        var slate = MilkExpectedSlate.Build(Array.Empty<MilkInputMatch>());
        Assert.Empty(slate.Rows);

        var report = MilkRunLedger.Verify(new[]
        {
            new MilkLedgerPair { Table = "snapshot", Expected = slate.Rows, Actual = Array.Empty<MilkLedgerRow>(), Ignore = slate.IgnoreKeys }
        });

        Assert.True(report.Ok);
    }
}
