using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AiFootBall.Core.Models;

/// <summary>账本里的一行：业务键 + 该键的行数。</summary>
public sealed class MilkLedgerRow
{
    public string Key { get; init; } = string.Empty;
    public int Count { get; init; } = 1;
}

/// <summary>一条核对问题。</summary>
public sealed class MilkLedgerIssue
{
    /// <summary>表：pick / gate / snapshot / input。</summary>
    public string Table { get; init; } = string.Empty;

    /// <summary>
    /// missing           预期有、实际没有（含「只写进去一部分」）
    /// duplicate         预期一个、实际多个（重复写入）
    /// unexpected        实际有、预期没有（多写的档位）
    /// duplicate-expected 预期里本身就重复（引擎同一场同玩法出了两注同档位）
    /// unlandable        预期里有一条根本写不进去（MatchId 为空）
    /// </summary>
    public string Kind { get; init; } = string.Empty;

    public string Key { get; init; } = string.Empty;
    public int Expected { get; init; }
    public int Actual { get; init; }

    public override string ToString()
        => $"{Table}:{Kind} [{Key}] 预期 {Expected} 实际 {Actual}";
}

/// <summary>
/// 一轮落库的核对结论（2026-11-13 第四轮，用户明确要求）。
///
/// 为什么「input &gt; 0 且 snapshot &gt; 0」不够：那只是两个总数。
/// 一次只写进去一半档位、或者同一档写了两行，总数照样可能「看起来正常」，
/// 而榜面读的是重写后的行——少的那几档当场从榜上消失，多的那几行让同一注出现在两张牌上。
/// 所以发布判据改成**逐场逐玩法逐档位**核对：
///   预期从本轮引擎输出（`MilkBoardPlan.Writes` + `build.Gates/Snapshots/Inputs`）构建，
///   实际从库里按 `RunId` 读回，两边按键比对，缺失、重复、多写一律判失败。
/// </summary>
public sealed class MilkLedgerReport
{
    public IReadOnlyList<MilkLedgerIssue> Issues { get; init; } = Array.Empty<MilkLedgerIssue>();

    /// <summary>每张表的（预期, 实际）总行数，写日志用。</summary>
    public IReadOnlyList<(string Table, int Expected, int Actual)> Totals { get; init; }
        = Array.Empty<(string, int, int)>();

    public bool Ok => Issues.Count == 0;

    /// <summary>合法缺项摘要（2026-11-13 第五轮）：写日志用，说明「为什么预期比输入少几档」。</summary>
    public string ExemptSummary { get; init; } = string.Empty;

    /// <summary>形如 `pick 6/6、gate 11/11、snapshot 105/105、input 3/3`。</summary>
    public string Summary => string.Join("、", Totals.Select(t => $"{t.Table} {t.Actual}/{t.Expected}"));

    /// <summary>最多 8 条问题明细，够定位又不会把日志刷爆。</summary>
    public string Detail
    {
        get
        {
            if (Issues.Count == 0) return string.Empty;
            var head = Issues.Take(8).Select(i => i.ToString());
            var more = Issues.Count > 8 ? $"；另有 {Issues.Count - 8} 条" : "";
            return string.Join("；", head) + more;
        }
    }

    public int Missing => Issues.Count(i => i.Kind is "missing" or "unlandable");
    public int Duplicated => Issues.Count(i => i.Kind is "duplicate" or "duplicate-expected");
    public int Unexpected => Issues.Count(i => i.Kind == "unexpected");
}

/// <summary>一张表的「预期 vs 实际」。</summary>
public sealed class MilkLedgerPair
{
    public string Table { get; init; } = string.Empty;
    public IReadOnlyList<MilkLedgerRow> Expected { get; init; } = Array.Empty<MilkLedgerRow>();
    public IReadOnlyList<MilkLedgerRow> Actual { get; init; } = Array.Empty<MilkLedgerRow>();

    /// <summary>
    /// 不参与判定的键（2026-11-13 第五轮）：合法缺项，原因已单独登记。
    /// 两边都按它过滤——豁免项既不算「少写」，也不算「多写」。
    /// 这不是「放宽判据」：豁免必须来自 <see cref="MilkExpectedSlate"/> 的固定原因枚举，
    /// 未知原因照样判失败。
    /// </summary>
    public IReadOnlyCollection<string> Ignore { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 本轮落库的逐档核对（纯函数，2026-11-13 第四轮）。
///
/// 键的格式必须与 `MilkPickRepository.ReadRunLedgerAsync` 里的 SQL 完全一致，
/// 否则核对会全表报 missing——这类「两边各写一份格式」的错，本项目已经踩过
/// （出票闸与撤注闸各写一份返奖率常数，结果系统撤销自己刚出的票）。
/// 统一由 <see cref="Keys"/> 生成，SQL 侧照抄。
/// </summary>
public static class MilkRunLedger
{
    /// <summary>业务键的构造口径，SQL 侧必须逐字对齐。</summary>
    public static class Keys
    {
        public const string DateFormat = "yyyy-MM-dd";

        public static string Pick(DateTime salesDate, long matchId, string playType, string optionCode)
            => $"{salesDate.ToString(DateFormat, CultureInfo.InvariantCulture)}|{matchId}|{playType}|{optionCode}";

        public static string Gate(long matchId) => matchId.ToString(CultureInfo.InvariantCulture);

        public static string Snapshot(long matchId, string playType, string optionCode)
            => $"{matchId}|{playType}|{optionCode}";

        public static string Input(long matchId) => matchId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 构建预期账本时要报的「写不进去」的行：`SaveDayAsync` 遇到 `MatchId` 为空的注是 `continue`，
    /// 不报错、不留痕——那正是「只写成功一部分」最隐蔽的一种。这里把它显式记成问题。
    /// </summary>
    public static List<MilkLedgerIssue> UnlandablePicks(IEnumerable<MilkPick>? picks)
        => (picks ?? Array.Empty<MilkPick>())
            .Where(p => p.MatchId is not long || p.MatchId <= 0)
            .Select(p => new MilkLedgerIssue
            {
                Table = "pick",
                Kind = "unlandable",
                Key = $"{p.MatchNo ?? "?"}|{p.PlayType}|{p.OptionCode}",
                Expected = 1,
                Actual = 0
            })
            .ToList();

    public static List<MilkLedgerRow> FromPicks(DateTime salesDate, IEnumerable<MilkPick>? picks)
        => (picks ?? Array.Empty<MilkPick>())
            .Where(p => p.MatchId is long mid && mid > 0)
            .Select(p => new MilkLedgerRow
            {
                Key = Keys.Pick(salesDate, p.MatchId!.Value, p.PlayType, p.OptionCode)
            })
            .ToList();

    public static List<MilkLedgerRow> FromGates(IEnumerable<MilkDirectionDiagnosis>? gates)
        => (gates ?? Array.Empty<MilkDirectionDiagnosis>())
            .Where(g => g.MatchId is long mid && mid > 0)
            .Select(g => new MilkLedgerRow { Key = Keys.Gate(g.MatchId!.Value) })
            .ToList();

    public static List<MilkLedgerRow> FromSnapshots(IEnumerable<MilkProbSnapshot>? rows)
        => (rows ?? Array.Empty<MilkProbSnapshot>())
            .Where(r => r.MatchId > 0)
            .Select(r => new MilkLedgerRow { Key = Keys.Snapshot(r.MatchId, r.PlayType, r.OptionCode) })
            .ToList();

    /// <summary>
    /// 输入凭证的预期：`SaveInputsAsync` 内部按 MatchId 去重（一场只记一行），
    /// 所以预期也必须先去重——否则核对会把「同场多个来源各报了一次」当成预期重复判失败。
    /// </summary>
    public static List<MilkLedgerRow> FromInputs(IEnumerable<MilkInputMatch>? rows)
        => (rows ?? Array.Empty<MilkInputMatch>())
            .Where(r => r.MatchId > 0)
            .GroupBy(r => r.MatchId)
            .Select(g => new MilkLedgerRow { Key = Keys.Input(g.Key) })
            .ToList();

    /// <summary>
    /// 逐档核对。任何一张表出现缺失 / 重复 / 多写，结论就是「不许发布」。
    ///
    /// 刻意不设「允许少量误差」的阈值：这几张表的行数完全由本轮输入决定，
    /// 少一行就是少一档存档，多一行就是同一注出现两次，没有「可接受的偏差」这种说法。
    /// </summary>
    public static MilkLedgerReport Verify(
        IReadOnlyList<MilkLedgerPair>? pairs,
        IReadOnlyList<MilkLedgerIssue>? preIssues = null,
        string exemptSummary = "")
    {
        var issues = new List<MilkLedgerIssue>(preIssues ?? Array.Empty<MilkLedgerIssue>());
        var totals = new List<(string, int, int)>();

        foreach (var pair in pairs ?? Array.Empty<MilkLedgerPair>())
        {
            var ignore = pair.Ignore is { Count: > 0 }
                ? new HashSet<string>(pair.Ignore, StringComparer.Ordinal)
                : null;

            var expList = (pair.Expected ?? Array.Empty<MilkLedgerRow>())
                .Where(r => ignore is null || !ignore.Contains(r.Key))
                .ToList();
            var actList = (pair.Actual ?? Array.Empty<MilkLedgerRow>())
                .Where(a => ignore is null || !ignore.Contains(a.Key))
                .ToList();
            totals.Add((pair.Table, expList.Count, actList.Sum(a => a.Count)));

            // 预期重复 = 引擎自己在同一场同玩法出了两注同档位。
            // 落库时 `INSERT ... WHERE NOT EXISTS` 只会写进去一条，于是「预期 2、实际 1」
            // 看起来像缺一行——真正的问题在上游，必须单独报出来，不能被当成落库缺失去查。
            // 报了这一条就不再对该键报 missing：否则同一个病因会说两遍，读日志的人会去查落库。
            var dupKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dup in expList.GroupBy(e => e.Key).Where(g => g.Count() > 1))
            {
                dupKeys.Add(dup.Key);
                issues.Add(new MilkLedgerIssue
                {
                    Table = pair.Table, Kind = "duplicate-expected", Key = dup.Key,
                    Expected = dup.Count(), Actual = 0
                });
            }

            var exp = expList.GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.Count());
            var act = actList.GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.Sum(x => x.Count));

            foreach (var (key, n) in exp)
            {
                if (dupKeys.Contains(key)) continue;
                if (!act.TryGetValue(key, out var got) || got == 0)
                {
                    issues.Add(new MilkLedgerIssue
                    {
                        Table = pair.Table, Kind = "missing", Key = key, Expected = n, Actual = 0
                    });
                }
                else if (got < n)
                {
                    // 预期 1 实际 1 之外的「部分写入」：同一键写了但少了几行。
                    issues.Add(new MilkLedgerIssue
                    {
                        Table = pair.Table, Kind = "missing", Key = key, Expected = n, Actual = got
                    });
                }
                else if (got > n)
                {
                    issues.Add(new MilkLedgerIssue
                    {
                        Table = pair.Table, Kind = "duplicate", Key = key, Expected = n, Actual = got
                    });
                }
            }

            foreach (var (key, got) in act)
            {
                if (got <= 0) continue;
                if (!exp.ContainsKey(key))
                    issues.Add(new MilkLedgerIssue
                    {
                        Table = pair.Table, Kind = "unexpected", Key = key, Expected = 0, Actual = got
                    });
            }
        }

        return new MilkLedgerReport { Issues = issues, Totals = totals, ExemptSummary = exemptSummary };
    }
}
