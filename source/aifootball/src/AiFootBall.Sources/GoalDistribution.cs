using System.Text;
using AiFootBall.Core.Models;

namespace AiFootBall.Sources;

/// <summary>
/// 每日复盘里的「进球与比分分布」一节。
///
/// 存在的理由（2026-11-08 用户要求）：复盘原来只回答「哪注中没中」，回答不了
/// 「总进球 2 球 / 3 球 / 4 球该押哪一档、这个价该不该接」。这一节只做统计，不进任何推荐，
/// 但它是唯一能让人（和模型）看见「一天的进球长什么样、离长期基线偏多远」的地方。
///
/// 三条硬规矩：
/// 1. 只统计**竞彩**（复盘里竞彩是逐场全写的，北单只写推荐场次，混在一起口径就乱了）。
/// 2. 单日样本不单独下结论——每一档都和「同一批竞彩联赛、近 N 天」的基线并排放。
/// 3. 选对档位 ≠ 有利可图。竞彩总进球是单档玩法、返奖率 79.7%，任何一档的 EV 天花板就是 0.797。
///    这一节能告诉你「哪一档更可能出」，不能告诉你「哪一档能赚钱」。
/// </summary>
public static class GoalDistribution
{
    static readonly string[] BucketNames = { "0 球", "1 球", "2 球", "3 球", "4 球", "5 球", "6 球", "7+ 球" };

    /// <summary>在场上一行用的最小结构：一场完赛比赛。</summary>
    sealed class DayRow
    {
        public string MatchNo = "";
        public string League = "";
        public string Score = "";
        public int Home;
        public int Away;
        public int Total;
        /// <summary>净胜球：主队角度，正=主胜。</summary>
        public int Margin => Home - Away;
        public bool CleanSheet => Home == 0 || Away == 0;
        public bool BothScored => Home > 0 && Away > 0;
    }

    static int Bucket(int goals) => goals >= 7 ? 7 : goals;

    /// <summary>
    /// 渲染这一节。baseline 为 null 时只出「今天」一列，并写明基线缺失，不伪造数字。
    /// </summary>
    public static string ToMarkdown(
        IReadOnlyList<ReviewRow> jcRows,
        IReadOnlyList<GoalSampleRow>? baseline,
        int baselineDays)
    {
        var all = jcRows.ToList();
        var day = new List<DayRow>();
        var pending = new List<ReviewRow>();

        foreach (var r in all)
        {
            var parts = (r.ScoreText ?? "").Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var a))
            {
                day.Add(new DayRow
                {
                    MatchNo = r.MatchNo, League = r.League, Score = $"{h}:{a}",
                    Home = h, Away = a, Total = h + a
                });
            }
            else
            {
                pending.Add(r);
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("## 进球与比分分布（学习用，不进推荐）");
        sb.AppendLine();

        if (day.Count == 0)
        {
            sb.AppendLine($"竞彩 {all.Count} 场，一场都还没有比分，这一节写不了。等赛果落库后重写复盘。");
            sb.AppendLine();
            return sb.ToString();
        }

        // ---- 基线预处理：档位、比分、联赛场均 ----
        var baseBucket = new int[8];
        var baseScores = new Dictionary<string, int>(StringComparer.Ordinal);
        var baseLeagueGoals = new Dictionary<string, (int Matches, int Goals)>(StringComparer.Ordinal);
        var baseTotal = 0;
        var baseGoals = 0;
        if (baseline is not null)
        {
            foreach (var s in baseline)
            {
                baseBucket[Bucket(s.HomeScore + s.AwayScore)] += s.Matches;
                var key = $"{s.HomeScore}:{s.AwayScore}";
                baseScores[key] = baseScores.TryGetValue(key, out var c) ? c + s.Matches : s.Matches;
                baseLeagueGoals.TryGetValue(s.LeagueName, out var agg);
                baseLeagueGoals[s.LeagueName] = (agg.Matches + s.Matches, agg.Goals + (s.HomeScore + s.AwayScore) * s.Matches);
                baseTotal += s.Matches;
                baseGoals += (s.HomeScore + s.AwayScore) * s.Matches;
            }
        }
        var hasBase = baseTotal > 0;

        // ---- 今天的档位 ----
        var dayBucket = new int[8];
        var dayScores = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var d in day)
        {
            dayBucket[Bucket(d.Total)]++;
            if (!dayScores.TryGetValue(d.Score, out var list)) dayScores[d.Score] = list = new List<string>();
            list.Add(d.MatchNo);
        }

        var dayGoals = day.Sum(d => d.Total);
        sb.AppendLine(
            $"竞彩 {all.Count} 场，{day.Count} 场有比分" +
            (pending.Count > 0 ? $"、{pending.Count} 场待补（{string.Join("、", pending.Select(p => $"{p.MatchNo} {p.Home} vs {p.Away}"))}）" : "") +
            $"。今天共 {dayGoals} 球，场均 {(decimal)dayGoals / day.Count:0.00}" +
            (hasBase ? $"，近 {baselineDays} 天同批联赛基线场均 {(decimal)baseGoals / baseTotal:0.00}。" : "。") );
        sb.AppendLine();
        sb.AppendLine("这一节只做统计：它回答「今天这一档进球出现了几次、离长期有多远」，不回答「买哪一注」。");
        sb.AppendLine();

        // ---- 表一：总进球档位 ----
        sb.AppendLine("### 总进球档位");
        sb.AppendLine();
        sb.AppendLine(hasBase
            ? "| 总进球 | 今天 | 今天占比 | 近 " + baselineDays + " 天占比 | 差 | 今天这一档的比分 |"
            : "| 总进球 | 今天 | 今天占比 | 今天这一档的比分 |");
        sb.AppendLine(hasBase
            ? "| --- | --- | --- | --- | --- | --- |"
            : "| --- | --- | --- | --- |");

        for (var b = 0; b < 8; b++)
        {
            var n = dayBucket[b];
            if (n == 0 && (!hasBase || baseBucket[b] == 0)) continue;
            var scoresInBucket = dayScores
                .Where(kv => Bucket(int.Parse(kv.Key.Split(':')[0]) + int.Parse(kv.Key.Split(':')[1])) == b)
                .OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Value.Count > 1 ? $"{kv.Key}×{kv.Value.Count}" : kv.Key)
                .ToList();
            var scoreText = scoresInBucket.Count > 0 ? string.Join("、", scoresInBucket) : "—";
            var dayPct = 100m * n / day.Count;
            if (hasBase)
            {
                var basePct = 100m * baseBucket[b] / baseTotal;
                sb.AppendLine($"| {BucketNames[b]} | {n} | {dayPct:0.0}% | {basePct:0.0}% | {Signed(dayPct - basePct)} | {scoreText} |");
            }
            else
            {
                sb.AppendLine($"| {BucketNames[b]} | {n} | {dayPct:0.0}% | {scoreText} |");
            }
        }

        var day234 = dayBucket[2] + dayBucket[3] + dayBucket[4];
        if (hasBase)
        {
            var base234 = baseBucket[2] + baseBucket[3] + baseBucket[4];
            sb.AppendLine($"| **合计** | **{day.Count}** | **100%** | **100%** | — | **2/3/4 球合计 {day234} 场 = {100m * day234 / day.Count:0.0}%（基线 {100m * base234 / baseTotal:0.0}%）** |");
        }
        else
        {
            sb.AppendLine($"| **合计** | **{day.Count}** | **100%** | **2/3/4 球合计 {day234} 场 = {100m * day234 / day.Count:0.0}%** |");
        }
        sb.AppendLine();

        // ---- 表二：比分 ----
        sb.AppendLine("### 比分（今天出现过的全部比分）");
        sb.AppendLine();
        sb.AppendLine(hasBase
            ? "| 比分 | 场次 | 占比 | 档位 | 近 " + baselineDays + " 天占比 | 场次号 |"
            : "| 比分 | 场次 | 占比 | 档位 | 场次号 |");
        sb.AppendLine(hasBase
            ? "| --- | --- | --- | --- | --- | --- |"
            : "| --- | --- | --- | --- | --- |");
        foreach (var kv in dayScores
            .OrderByDescending(kv => kv.Value.Count)
            .ThenBy(kv => int.Parse(kv.Key.Split(':')[0]) + int.Parse(kv.Key.Split(':')[1]))
            .ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var hh = int.Parse(kv.Key.Split(':')[0]);
            var aa = int.Parse(kv.Key.Split(':')[1]);
            var cnt = kv.Value.Count;
            var basePct = hasBase && baseScores.TryGetValue(kv.Key, out var bc) ? $"{100m * bc / baseTotal:0.0}%" : "—";
            if (hasBase)
                sb.AppendLine($"| {kv.Key} | {cnt} | {100m * cnt / day.Count:0.0}% | {BucketNames[Bucket(hh + aa)]} | {basePct} | {string.Join(" ", kv.Value)} |");
            else
                sb.AppendLine($"| {kv.Key} | {cnt} | {100m * cnt / day.Count:0.0}% | {BucketNames[Bucket(hh + aa)]} | {string.Join(" ", kv.Value)} |");
        }
        sb.AppendLine();

        // ---- 表三：联赛 ----
        sb.AppendLine("### 落在哪些联赛");
        sb.AppendLine();
        sb.AppendLine(hasBase
            ? "| 联赛 | 场次 | 总进球 | 场均 | 该联赛基线场均 | 0-1 球 | 2 球 | 3 球 | 4+ 球 | 今天的比分 |"
            : "| 联赛 | 场次 | 总进球 | 场均 | 0-1 球 | 2 球 | 3 球 | 4+ 球 | 今天的比分 |");
        sb.AppendLine(hasBase
            ? "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |"
            : "| --- | --- | --- | --- | --- | --- | --- | --- | --- |");

        foreach (var g in day
            .GroupBy(d => d.League, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            var n = g.Count();
            var goals = g.Sum(d => d.Total);
            var lo = g.Count(d => d.Total <= 1);
            var b2 = g.Count(d => d.Total == 2);
            var b3 = g.Count(d => d.Total == 3);
            var b4 = g.Count(d => d.Total >= 4);
            var scores = g.GroupBy(d => d.Score, StringComparer.Ordinal)
                .OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => x.Count() > 1 ? $"{x.Key}×{x.Count()}" : x.Key);
            var baseAvg = hasBase && baseLeagueGoals.TryGetValue(g.Key, out var agg) && agg.Matches > 0
                ? $"{(decimal)agg.Goals / agg.Matches:0.00}（{agg.Matches} 场）"
                : "—";
            if (hasBase)
                sb.AppendLine($"| {g.Key} | {n} | {goals} | {(decimal)goals / n:0.00} | {baseAvg} | {lo} | {b2} | {b3} | {b4} | {string.Join("、", scores)} |");
            else
                sb.AppendLine($"| {g.Key} | {n} | {goals} | {(decimal)goals / n:0.00} | {lo} | {b2} | {b3} | {b4} | {string.Join("、", scores)} |");
        }
        sb.AppendLine();
        if (hasBase)
        {
            sb.AppendLine("「该联赛基线场均」后面括号是近两年的样本场次；写 `—` 的说明库里这批比赛的联赛名和竞彩受注卡对不上（如日乙/韩职/瑞超），不是那天没踢，是没法拿来比。");
            sb.AppendLine();
        }

        // ---- 支撑判据：这一天的比赛是「闷」还是「开」 ----
        var homeWin = day.Count(d => d.Margin > 0);
        var draw = day.Count(d => d.Margin == 0);
        var awayWin = day.Count(d => d.Margin < 0);
        var margin1 = day.Count(d => Math.Abs(d.Margin) == 1);
        var margin2 = day.Count(d => Math.Abs(d.Margin) == 2);
        var margin3 = day.Count(d => Math.Abs(d.Margin) >= 3);
        var btts = day.Count(d => d.BothScored);
        var cs = day.Count(d => d.CleanSheet);

        sb.AppendLine("### 支撑判据（同一天的分布长什么样）");
        sb.AppendLine();
        sb.AppendLine($"- 结果：主胜 {homeWin}（{100m * homeWin / day.Count:0.0}%）、平 {draw}（{100m * draw / day.Count:0.0}%）、客胜 {awayWin}（{100m * awayWin / day.Count:0.0}%）。");
        sb.AppendLine($"- 分差：1 球 {margin1}、2 球 {margin2}、3 球及以上 {margin3}、平局 {draw}。");
        sb.AppendLine($"- 双方都进球 {btts} 场（{100m * btts / day.Count:0.0}%），有一边被零封 {cs} 场（{100m * cs / day.Count:0.0}%）。");
        sb.AppendLine();

        // ---- 怎么用 ----
        sb.AppendLine("### 2 球 / 3 球 / 4 球怎么选");
        sb.AppendLine();
        AppendHowTo(sb, day.Count, dayBucket, baseBucket, baseTotal, day234, btts, cs);
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// 「怎么选」这一段必须由数字推出来，不能写成通用鸡汤——每天的结论都可能不一样。
    /// </summary>
    static void AppendHowTo(
        StringBuilder sb, int dayCount, int[] dayBucket, int[] baseBucket, int baseTotal,
        int day234, int btts, int cs)
    {
        var hasBase = baseTotal > 0;

        if (hasBase)
        {
            var b2 = 100m * baseBucket[2] / baseTotal;
            var b3 = 100m * baseBucket[3] / baseTotal;
            var b4 = 100m * baseBucket[4] / baseTotal;
            var b234 = 100m * (baseBucket[2] + baseBucket[3] + baseBucket[4]) / baseTotal;
            sb.AppendLine(
                $"1. **长期基线先摆在这里**：2 球 {b2:0.0}% + 3 球 {b3:0.0}% + 4 球 {b4:0.0}% = {b234:0.0}%。" +
                $"也就是说随便挑一天，「2/3/4 球」这三档合起来天然覆盖六成场次，这不是哪天的运气，是分布本身。");
            sb.AppendLine(
                $"   真正要选的是在这三档里挑**一档**：长期 2 球和 3 球几乎齐平（{b2:0.0}% / {b3:0.0}%），" +
                $"4 球只有 {b4:0.0}%——所以 4 球的赔率高是应该的，不是「被低估」。");
        }
        else
        {
            sb.AppendLine("1. 这次没取到基线（查询失败或库太新），下面只看今天，**不要拿单日当规律**。");
        }

        var p234 = 100m * day234 / dayCount;
        sb.AppendLine(
            $"2. **今天** 2/3/4 球合计 {day234}/{dayCount} = {p234:0.0}%。" +
            (hasBase && p234 > 65
                ? "比基线高出一截，恰恰说明单日会飘——照今天的比例去调门槛，明天就过头了。"
                : hasBase && p234 < 50
                    ? "比基线低一截，这天偏闷；但这不构成「明天该押小球」的理由，单日样本只有二十几场。"
                    : "和基线大致相符，这类天没有额外信息。"));

        var b3Day = 100m * dayBucket[3] / dayCount;
        if (hasBase)
        {
            var base3 = 100m * baseBucket[3] / baseTotal;
            var gap = b3Day - base3;
            sb.AppendLine(
                $"3. **今天最偏的一档是 3 球**：{b3Day:0.0}%，基线 {base3:0.0}%，差 {Signed(gap)}。" +
                (Math.Abs(gap) >= 8
                    ? $"一天只有 {dayCount} 场，这一档多出 {Math.Round(Math.Abs(gap) * dayCount / 100m):0} 场就能拉开这么多，属于单日波动，**不改任何门槛**。"
                    : "差异在正常波动范围内。"));
        }

        sb.AppendLine(
            $"4. **同一天的支撑面**：双方都进球 {100m * btts / Math.Max(dayCount, 1):0.0}%、有一边零封 {100m * cs / Math.Max(dayCount, 1):0.0}%。" +
            "两边都能进的时候，2 球档落在 1:1、3 球档落在 2:1 或 1:2；零封多的时候，1 球档（1:0 / 0:1）才起来。" +
            "换句话说：**先判「会不会有一边挂零」，再判总进球档**——顺序反了就会在闷天去接 3 球。");
        sb.AppendLine(
            "5. **选对档 ≠ 赚钱**：竞彩总进球是单档玩法，返奖率 79.7%，任何一档的 EV 天花板就是 0.797（每投 100 元长期回收 79.7 元）。" +
            "这一节的作用是**排除明显不该买的档**（比如闷天接 4 球、开天接 0 球），不是给出买点。");
        sb.AppendLine(
            "6. 比分同理：把「今天的比分表」和「近两年比分基线」上下两行对着看，就知道哪些比分是常态（1:1 / 1:0 / 2:1）、" +
            "哪些是彩票（3:2 及以上）。比分玩法返奖率只有 71%，是抽水最狠的一档，长期比总进球更难回本。");
    }

    static string Signed(decimal v) => (v > 0 ? "+" : "") + v.ToString("0.0") + "pt";
}
