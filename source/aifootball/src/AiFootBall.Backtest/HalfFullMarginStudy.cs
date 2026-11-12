using AiFootBall.Core.Models;
using AiFootBall.Data;
using AiFootBall.Sources;
using Dapper;

namespace AiFootBall.Backtest;

/// <summary>
/// 「半全场联合分布对半场求和」与「全场网格」差多少（2026-11-12 加，第三方校验方案的 P1-6）。
///
/// 这是整份校验方案里**唯一一条我们从来不敢说「通过」的自洽项**：
/// 半全场用的是「上半场 λ×0.45 + 下半场 λ×0.55」两段独立泊松再合并（见
/// <see cref="MilkPickEngine.HalfFullJoint"/>），而全场胜负来自另一个对象（比分网格）。
/// 两者本该在**同一个量上给出一致的数**——「主胜的概率」。
/// 若联合分布求边际后系统性偏离网格，说明 0.45/0.55 这个切分（或两段独立这个假设）不对，
/// 半全场的 9 档概率整体被带偏，而它正是大奶榜最常出票的玩法。
///
/// 判定线（方案里的口径）：L1 距离中位数 > 5 个百分点 ⇒ 切分需要按联赛重估。
///
/// 为什么之前没量过：想出这个数必须同时拿到 joint 与 grid，而 joint 一直是 private。
/// 2026-11-11 为回测台改成 public 之后才第一次可测；这里不复制任何算式，直接调线上同一份实现，
/// 免得「测的是另一套代码」。
///
/// 用法：dotnet run --project src/AiFootBall.Backtest -c Release -- hf-margin [训练占比]
/// 只读库，不改任何业务表。
/// </summary>
public static class HalfFullMarginStudy
{
    sealed class SRow
    {
        public long MatchId { get; set; }
        public string? LeagueName { get; set; }
        public string HomeTeam { get; set; } = "";
        public string AwayTeam { get; set; } = "";
        public DateTime MatchTime { get; set; }
        public int HomeScore { get; set; }
        public int AwayScore { get; set; }
    }

    public static async Task<int> RunAsync(string connStr, double trainRatio = 0.67)
    {
        var factory = new SqlConnectionFactory(connStr);
        await using var conn = await factory.OpenAsync();

        var samples = (await conn.QueryAsync<SRow>(
            @"SELECT MatchId, LeagueName, HomeTeamName AS HomeTeam, AwayTeamName AS AwayTeam, MatchTime,
                     HomeScore, AwayScore
              FROM dbo.FbMatch
              WHERE Status = 3 AND HomeScore IS NOT NULL AND AwayScore IS NOT NULL
                AND MatchTime >= DATEADD(month, -18, GETDATE())")).ToList();

        if (samples.Count < 500)
        {
            Console.WriteLine($"样本不足（完赛 {samples.Count}），跑不了。");
            return 1;
        }

        var times = samples.Select(s => s.MatchTime).OrderBy(t => t).ToList();
        var cutIndex = Math.Max(1, Math.Min(times.Count - 1, (int)(times.Count * trainRatio)));
        var cutoff = times[cutIndex];

        var trainSamples = samples.Where(s => s.MatchTime < cutoff).ToList();
        var testAll = samples.Where(s => s.MatchTime >= cutoff).ToList();

        var games = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in trainSamples)
        {
            var h = TeamAliases.Canonical(s.HomeTeam);
            var a = TeamAliases.Canonical(s.AwayTeam);
            games[h] = games.GetValueOrDefault(h) + 1;
            games[a] = games.GetValueOrDefault(a) + 1;
        }
        int Known(string n) => games.GetValueOrDefault(TeamAliases.Canonical(n));
        var test = testAll.Where(s => Math.Min(Known(s.HomeTeam), Known(s.AwayTeam)) >= 5).ToList();

        var model = ScorelineModel.Fit(
            trainSamples.Select(s => new ScoreSample
            {
                LeagueName = s.LeagueName,
                HomeTeam = s.HomeTeam,
                AwayTeam = s.AwayTeam,
                HomeScore = s.HomeScore,
                AwayScore = s.AwayScore,
                MatchTime = s.MatchTime
            }).ToList(),
            asOf: cutoff);
        if (!model.Ready)
        {
            Console.WriteLine("模型未就绪，跑不了。");
            return 1;
        }

        Console.WriteLine($"# 半全场边际 vs 全场模型  {DateTime.Now:yyyy-MM-dd HH:mm}");
        Console.WriteLine();
        Console.WriteLine("| 项目 | 值 |");
        Console.WriteLine("|---|---|");
        Console.WriteLine($"| 切分时刻 | {cutoff:yyyy-MM-dd HH:mm} |");
        Console.WriteLine($"| 训练窗口（模型自报） | {model.FitFrom:yyyy-MM-dd} .. {model.FitTo:yyyy-MM-dd}（n={model.FitGames}） |");
        Console.WriteLine($"| 测试场次 | {test.Count} |");
        Console.WriteLine($"| 模型版本 | `{model.VersionTag}` |");
        Console.WriteLine();
        Console.WriteLine("口径：两段式半场切分固定为 " + MilkPickEngine.HalfGoalShare + " / "
            + (1 - MilkPickEngine.HalfGoalShare) + "；比赛不做大小球校正（历史场次没有当时的盘口），");
        Console.WriteLine("两边都吃同一个 λ，所以差异只来自「两段独立泊松 + 截断」这一层假设。");
        Console.WriteLine();

        var rows = new List<(string League, double L1, double Dh, double Dd, double Da)>();
        foreach (var s in test)
        {
            var lg = model.ResolveLeague(s.LeagueName, s.HomeTeam, s.AwayTeam, 5);

            var grid = model.Distribution(lg, s.HomeTeam, s.AwayTeam, null);
            if (grid.Count == 0) continue;
            var mass = grid.Sum(c => c.P);
            if (mass <= 0) continue;
            double gHome = grid.Where(c => c.Home > c.Away).Sum(c => c.P) / mass;
            double gDraw = grid.Where(c => c.Home == c.Away).Sum(c => c.P) / mass;
            double gAway = grid.Where(c => c.Home < c.Away).Sum(c => c.P) / mass;

            var joint = MilkPickEngine.HalfFullJoint(model, lg, s.HomeTeam, s.AwayTeam, null);
            if (joint is null) continue;

            // 联合分布 [半场结果, 全场结果]，行/列 0=主胜 1=平 2=客胜。
            // 对半场那一维求和 = 「不管半场怎样，全场是主胜/平/客胜」的概率，正是可比的那个量。
            double mHome = (double)(joint[0, 0] + joint[1, 0] + joint[2, 0]);
            double mDraw = (double)(joint[0, 1] + joint[1, 1] + joint[2, 1]);
            double mAway = (double)(joint[0, 2] + joint[1, 2] + joint[2, 2]);

            var dh = mHome - gHome;
            var dd = mDraw - gDraw;
            var da = mAway - gAway;
            rows.Add((s.LeagueName ?? "", Math.Abs(dh) + Math.Abs(dd) + Math.Abs(da), dh, dd, da));
        }

        if (rows.Count == 0)
        {
            Console.WriteLine("没有可比较的场次。");
            return 1;
        }

        var l1s = rows.Select(r => r.L1).OrderBy(x => x).ToList();
        double Pct(double q) => l1s[Math.Clamp((int)(q * (l1s.Count - 1)), 0, l1s.Count - 1)] * 100;
        var median = Pct(0.5);

        Console.WriteLine("## 一、L1 距离分布（单位：百分点；三向绝对差之和在完美一致时为 0，上限 200）");
        Console.WriteLine();
        Console.WriteLine("| 分位 | L1（点） |");
        Console.WriteLine("|---|---:|");
        Console.WriteLine($"| 中位数 | **{median:F2}** |");
        Console.WriteLine($"| 均值 | {rows.Average(r => r.L1) * 100:F2} |");
        Console.WriteLine($"| P75 | {Pct(0.75):F2} |");
        Console.WriteLine($"| P90 | {Pct(0.90):F2} |");
        Console.WriteLine($"| P99 | {Pct(0.99):F2} |");
        Console.WriteLine($"| 最大 | {l1s[^1] * 100:F2} |");
        Console.WriteLine();

        Console.WriteLine("## 二、方向性偏差（联合边际 − 全场网格，正=半全场那边给得更高）");
        Console.WriteLine();
        Console.WriteLine("| 结果 | 平均偏差（点） | 中位偏差（点） | 说明 |");
        Console.WriteLine("|---|---:|---:|---|");
        static double Med(IEnumerable<double> xs)
        {
            var a = xs.OrderBy(x => x).ToList();
            return a.Count == 0 ? 0 : a[a.Count / 2];
        }
        Console.WriteLine($"| 主胜 | {rows.Average(r => r.Dh) * 100:+0.00;-0.00} | {Med(rows.Select(r => r.Dh)) * 100:+0.00;-0.00} | "
            + "系统性为正 ⇒ 半全场高估主胜（反转档会被连带压低） |");
        Console.WriteLine($"| 平局 | {rows.Average(r => r.Dd) * 100:+0.00;-0.00} | {Med(rows.Select(r => r.Dd)) * 100:+0.00;-0.00} | "
            + "为正 ⇒ 平平/胜平/负平 被高估 |");
        Console.WriteLine($"| 客胜 | {rows.Average(r => r.Da) * 100:+0.00;-0.00} | {Med(rows.Select(r => r.Da)) * 100:+0.00;-0.00} | "
            + "为正 ⇒ 负负被高估 |");
        Console.WriteLine();
        Console.WriteLine($"平均 |偏差| = 主 {rows.Average(r => Math.Abs(r.Dh)) * 100:F2} / "
            + $"平 {rows.Average(r => Math.Abs(r.Dd)) * 100:F2} / "
            + $"客 {rows.Average(r => Math.Abs(r.Da)) * 100:F2} 点。");
        Console.WriteLine();

        // ---- 分联赛：哪些联赛的切分假设最不成立 ----
        var byLeague = rows.GroupBy(r => r.League)
            .Where(g => g.Count() >= 30)
            .Select(g => (League: g.Key, N: g.Count(), Med: Med(g.Select(x => x.L1)) * 100))
            .OrderByDescending(x => x.Med)
            .Take(12)
            .ToList();

        Console.WriteLine("## 三、最不一致的联赛（样本 ≥30 场，按中位 L1 降序）");
        Console.WriteLine();
        Console.WriteLine("| 联赛 | 场次 | 中位 L1（点） |");
        Console.WriteLine("|---|---:|---:|");
        foreach (var x in byLeague)
            Console.WriteLine($"| {(string.IsNullOrWhiteSpace(x.League) ? "(空)" : x.League)} | {x.N} | {x.Med:F2} |");
        Console.WriteLine();

        Console.WriteLine("## 四、判定");
        Console.WriteLine();
        Console.WriteLine(median > 5
            ? $"- 中位 L1 = {median:F2} 点 **> 5 点门槛** ⇒ 0.45/0.55 这个固定切分站不住，"
              + "半全场的 9 档概率整体被带偏，应按联赛重估（或改用半场实际进球率）。"
            : $"- 中位 L1 = {median:F2} 点 **≤ 5 点门槛** ⇒ 两段独立泊松这个近似在这批场次上够用，"
              + "半全场与全场不构成互相矛盾的两套概率。");
        Console.WriteLine("- 注意：这是**一致性**检验，不是准确率检验。一致说明「两边没打架」，"
            + "不说明半全场猜得准——后者要看 weight-replay 里的 Brier/ROI。");
        Console.WriteLine("- 口径边界：历史场次没有当时的盘口，所以这里不做大小球校正；"
            + "线上有 `TotalLine` 时会先缩放 λ，实际偏差可能比这里略小。");
        Console.WriteLine();
        Console.WriteLine("> 若将来改切分：改的是 `MilkPickEngine.HalfGoalShare`，一处常量；"
            + "改完必须重跑本工具 + `weight-replay`，两条一起看。");
        return 0;
    }
}
