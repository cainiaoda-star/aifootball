using System;
using System.Collections.Generic;
using System.Linq;
using AiFootBall.Core.Models;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 发布前的逐档核对（2026-11-13 第四轮）。
///
/// 要钉住的是用户当天指出的一件事：**发布条件不能只看「input &gt; 0 且 snapshot &gt; 0」**。
/// 那两个数字只证明「有东西写进去了」，证明不了「该写的都写全了、没写重复」。
/// 一次只写进去一半档位，榜面上就是少了几注；同一档写了两行，同一注会出现在两张牌上。
/// 这两种形态都必须判 failed（不发布），而不是「看起来有数据就算成功」。
/// </summary>
public class MilkRunLedgerTests
{
    private static readonly DateTime Day = new(2026, 11, 13);

    private static MilkLedgerRow Row(string key, int count = 1) => new() { Key = key, Count = count };

    private static MilkLedgerPair Pair(string table, IEnumerable<string> expected, IEnumerable<string> actual)
        => new()
        {
            Table = table,
            Expected = expected.Select(k => Row(k)).ToList(),
            Actual = actual.Select(k => Row(k)).ToList()
        };

    [Fact]
    public void 四表全齐时通过()
    {
        var report = MilkRunLedger.Verify(new[]
        {
            Pair("pick", new[] { "a", "b" }, new[] { "a", "b" }),
            Pair("gate", new[] { "1" }, new[] { "1" }),
            Pair("snapshot", new[] { "1|BQ|3-3", "1|BQ|1-1" }, new[] { "1|BQ|3-3", "1|BQ|1-1" }),
            Pair("input", new[] { "1" }, new[] { "1" })
        });

        Assert.True(report.Ok);
        Assert.Empty(report.Issues);
        Assert.Equal("pick 2/2、gate 1/1、snapshot 2/2、input 1/1", report.Summary);
    }

    [Fact]
    public void 少写一行判缺失()
    {
        var report = MilkRunLedger.Verify(new[]
        {
            Pair("pick", new[] { "a", "b" }, new[] { "a" })
        });

        Assert.False(report.Ok);
        var issue = Assert.Single(report.Issues);
        Assert.Equal("missing", issue.Kind);
        Assert.Equal("b", issue.Key);
        Assert.Equal(1, report.Missing);
    }

    [Fact]
    public void 同一档写了两行判重复()
    {
        var report = MilkRunLedger.Verify(new[]
        {
            new MilkLedgerPair
            {
                Table = "pick",
                Expected = new[] { Row("a") },
                Actual = new[] { Row("a", 2) }
            }
        });

        Assert.False(report.Ok);
        Assert.Equal("duplicate", Assert.Single(report.Issues).Kind);
        Assert.Equal(1, report.Duplicated);
    }

    [Fact]
    public void 多写预期外的档位判多写()
    {
        var report = MilkRunLedger.Verify(new[]
        {
            Pair("snapshot", new[] { "1|BQ|3-3" }, new[] { "1|BQ|3-3", "1|BQ|9-9" })
        });

        Assert.False(report.Ok);
        var issue = Assert.Single(report.Issues);
        Assert.Equal("unexpected", issue.Kind);
        Assert.Equal("1|BQ|9-9", issue.Key);
    }

    [Fact]
    public void 引擎自己出了重复档位要单独报()
    {
        // 同一场同玩法出了两注同档位：落库的 `WHERE NOT EXISTS` 只会写进去一条，
        // 于是「预期 2、实际 1」看起来像落库缺失。真正的问题在上游，必须分开报。
        var report = MilkRunLedger.Verify(new[]
        {
            new MilkLedgerPair
            {
                Table = "pick",
                Expected = new[] { Row("a"), Row("a") },
                Actual = new[] { Row("a") }
            }
        });

        Assert.False(report.Ok);
        var issue = Assert.Single(report.Issues);
        Assert.Equal("duplicate-expected", issue.Kind);
        Assert.Equal(2, issue.Expected);
    }

    [Fact]
    public void 空对空通过()
    {
        // 0 注的一轮是合法形态（全部被过滤）。核对不能把它判成失败——
        // 否则「撤掉失效旧票」这条唯一正确的修法会被自己的闸门挡住。
        var report = MilkRunLedger.Verify(new[]
        {
            Pair("pick", Array.Empty<string>(), Array.Empty<string>()),
            Pair("gate", new[] { "1" }, new[] { "1" })
        });

        Assert.True(report.Ok);
    }

    [Fact]
    public void MatchId为空的注要报写不进去()
    {
        var picks = new[]
        {
            new MilkPick { MatchId = null, MatchNo = "周五001", PlayType = JcPlayTypes.Bq, OptionCode = "3-3" },
            new MilkPick { MatchId = 0, MatchNo = "周五002", PlayType = JcPlayTypes.Bq, OptionCode = "3-3" }
        };

        var issues = MilkRunLedger.UnlandablePicks(picks);

        Assert.Equal(2, issues.Count);
        Assert.All(issues, i => Assert.Equal("unlandable", i.Kind));
        Assert.Empty(MilkRunLedger.FromPicks(Day, picks));   // 它们进不了预期集合
    }

    [Fact]
    public void 写不进去的注会让整轮判失败()
    {
        var report = MilkRunLedger.Verify(
            new[] { Pair("pick", Array.Empty<string>(), Array.Empty<string>()) },
            MilkRunLedger.UnlandablePicks(new[]
            {
                new MilkPick { MatchId = null, MatchNo = "周五001", PlayType = JcPlayTypes.Bq, OptionCode = "3-3" }
            }));

        Assert.False(report.Ok);
        Assert.Equal("unlandable", Assert.Single(report.Issues).Kind);
    }

    [Fact]
    public void 输入凭证按场次去重与落库口径一致()
    {
        // SaveInputsAsync 内部按 MatchId 去重。预期侧若不去重，
        // 「同场两个来源各报了一次」会被误判成 duplicate-expected。
        var rows = new[]
        {
            new MilkInputMatch { MatchId = 10 },
            new MilkInputMatch { MatchId = 10 },
            new MilkInputMatch { MatchId = 11 }
        };

        var expected = MilkRunLedger.FromInputs(rows);

        Assert.Equal(2, expected.Count);
        Assert.Contains(expected, r => r.Key == "10");
        Assert.Contains(expected, r => r.Key == "11");
    }

    [Fact]
    public void 键格式固定为可排序日期且不含本地化符号()
    {
        // 键必须和 ReadRunLedgerAsync 里的 SQL 逐字对齐：
        // SQL 用 CONVERT(varchar(10), PickDate, 23) = yyyy-mm-dd，C# 侧必须是同一个格式。
        // 两边各写一份格式，是本项目踩过的坑（出票闸与撤注闸各写一份返奖率常数）。
        Assert.Equal("2026-11-13|123|BQ|3-3",
            MilkRunLedger.Keys.Pick(new DateTime(2026, 11, 13), 123, "BQ", "3-3"));
        Assert.Equal("123", MilkRunLedger.Keys.Gate(123));
        Assert.Equal("123|BQ|3-3", MilkRunLedger.Keys.Snapshot(123, "BQ", "3-3"));
        Assert.Equal("123", MilkRunLedger.Keys.Input(123));
    }

    [Fact]
    public void 预期与实际的键大小写必须一致()
    {
        // PlayType/OptionCode 来自引擎常量，两侧同源，不该出现大小写差；
        // 这条测试是防止「核对用了 ToLower / ToUpper 归一再比」这种掩盖问题的写法。
        var expected = MilkRunLedger.FromSnapshots(new[]
        {
            new MilkProbSnapshot { MatchId = 1, PlayType = JcPlayTypes.Bq, OptionCode = "3-3" }
        });

        var report = MilkRunLedger.Verify(new[] { new MilkLedgerPair { Table = "snapshot", Expected = expected, Actual = expected } });
        Assert.True(report.Ok);
        Assert.Equal($"1|{JcPlayTypes.Bq}|3-3", expected[0].Key);
    }

    [Fact]
    public void 只有部分表参与时其余不影响结论()
    {
        var report = MilkRunLedger.Verify(new[]
        {
            new MilkLedgerPair { Table = "pick", Expected = new[] { Row("a") }, Actual = new[] { Row("a") } },
            new MilkLedgerPair { Table = "gate", Expected = new[] { Row("9") }, Actual = Array.Empty<MilkLedgerRow>() }
        });

        Assert.False(report.Ok);
        Assert.Equal("gate", Assert.Single(report.Issues).Table);
    }
}
