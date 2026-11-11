using AiFootBall.Core.Models;
using AiFootBall.Data;
using AiFootBall.Sources;
using Dapper;

namespace AiFootBall.Backtest;

/// <summary>
/// 半全场（BQ）上「模型 vs 盘口」的 Brier 对照，用来检验 <c>MarketWeight = 0.3</c> 的根基
/// （2026-11-11 加）。
///
/// 为什么必须单独测：那个常量的注释写着「大奶盘回测（约 500 场竞彩收盘赔率）三个玩法模型全面压过市场，
/// 所以把市场权重从 0.6 降到 0.3」。但那组数字（比分 0.9474 vs 0.9833 等）在代码库里**找不到对应实现**，
/// 是一次性脚本的产物。而这个项目的铁律是「判据必须能重跑」——
/// <c>CounterHighModelVsMarketMin = 0.70</c> 就是同样的病，已经被标注「无统计支撑」。
///
/// 更值得怀疑的一点：回测台全目录里**没有任何一处过滤占位赔率**（Odds = 1.00），
/// 而这条过滤是本项目已经用真实事故换来的（1.00 会被算成「100% 概率」，把市场 Brier 拉爆）。
/// 若当时那份脚本也没过滤，0.9833 这种「比模型差 0.036」的数字就有了解释。
///
/// 同时这个 study 也给出「模型对半全场这一层到底准不准」的可重跑基线，
/// 顺带验证 11-11 修掉的半场数据事故之后，<see cref="MilkPickEngine.HalfFullJoint"/> 的分布是否还对得上。
///
/// 用法：dotnet run --project src/AiFootBall.Backtest -c Release -- exotic-bq
/// 只读库，不改任何业务表。
/// </summary>
public static class ExoticWeightStudy
{
    sealed class PRow
    {
        public long MatchId { get; set; }
        public string OptionCode { get; set; } = "";
        public decimal Odds { get; set; }
    }

    sealed class MRow
    {
        public long MatchId { get; set; }
        public string? LeagueName { get; set; }
        public string HomeTeam { get; set; } = "";
        public string AwayTeam { get; set; } = "";
        public DateTime MatchTime { get; set; }
        public int HomeScore { get; set; }
        public int AwayScore { get; set; }
        public int? HalfHomeScore { get; set; }
        public int? HalfAwayScore { get; set; }
    }

    /// <summary>竞彩半全场的 9 个选项码，格式「半场-全场」。</summary>
    static readonly string[] Codes = { "3-3", "3-1", "3-0", "1-3", "1-1", "1-0", "0-3", "0-1", "0-0" };
    static readonly string[] Names = { "胜胜", "胜平", "胜负", "平胜", "平平", "平负", "负胜", "负平", "负负" };

    static readonly decimal[] Weights = { 0m, 0.3m, 0.5m, 0.7m, 1.0m };

    const double MeaningfulGain = 0.002;

    static int Side(char c) => c == '3' ? 0 : c == '1' ? 1 : 2;
    static char CharOf(int side) => side == 0 ? '3' : side == 1 ? '1' : '0';

    public static async Task<int> RunAsync(string connStr)
    {
        var factory = new SqlConnectionFactory(connStr);
        await using var conn = await factory.OpenAsync();

        // 每场取「开赛前最后一个 BQ 快照」当收盘价。CapturedAt <= MatchTime 是硬条件，
        // 否则滚球阶段的价格会混进来（收盘欧赔那次就是栽在这上面）。
        const string lastSnap = @"
WITH last AS (
  SELECT h.MatchId, MAX(h.CapturedAt) AS ca
  FROM dbo.FbJcOddsHistory h
  JOIN dbo.FbMatch m ON m.MatchId = h.MatchId
  WHERE h.PlayType = 'BQ' AND h.CapturedAt <= m.MatchTime
  GROUP BY h.MatchId)";

        var matches = (await conn.QueryAsync<MRow>(
            lastSnap + @"
SELECT m.MatchId, m.LeagueName,
       m.HomeTeamName AS HomeTeam, m.AwayTeamName AS AwayTeam, m.MatchTime,
       m.HomeScore, m.AwayScore, m.HalfHomeScore, m.HalfAwayScore
FROM last l
JOIN dbo.FbMatch m ON m.MatchId = l.MatchId
WHERE m.Status = 3 AND m.HomeScore IS NOT NULL AND m.AwayScore IS NOT NULL
  AND m.HalfHomeScore IS NOT NULL AND m.HalfAwayScore IS NOT NULL
ORDER BY m.MatchTime")).ToList();

        var prices = (await conn.QueryAsync<PRow>(
            lastSnap + @"
SELECT h.MatchId, h.OptionCode, h.Odds
FROM dbo.FbJcOddsHistory h
JOIN last l ON l.MatchId = h.MatchId AND l.ca = h.CapturedAt
WHERE h.PlayType = 'BQ'")).ToList();

        var byMatch = prices.GroupBy(p => p.MatchId).ToDictionary(g => g.Key, g => g.ToList());

        var usable = new List<(MRow M, decimal[] Odds)>();
        int incomplete = 0, placeholder = 0;
        foreach (var m in matches)
        {
            if (!byMatch.TryGetValue(m.MatchId, out var ps) || ps.Count != 9) { incomplete++; continue; }
            var arr = new decimal[9];
            var ok = true;
            for (var i = 0; i < 9; i++)
            {
                var hit = ps.FirstOrDefault(p => p.OptionCode == Codes[i]);
                // Odds <= 1.01 一律当占位值丢掉：它会在去水时把这一档算成 100%，
                // 把整场的市场概率压垮。这条过滤是记忆里被真实事故血证过的。
                if (hit is null || hit.Odds <= 1.01m) { ok = false; break; }
                arr[i] = hit.Odds;
            }
            if (!ok) { placeholder++; continue; }
            usable.Add((m, arr));
        }

        if (usable.Count < 30)
        {
            Console.WriteLine($"可用场次只有 {usable.Count} 场（档位不全 {incomplete}、占位赔率 {placeholder}），比不出东西。");
            return 1;
        }

        var cutoff = usable.Min(x => x.M.MatchTime);
        var trainSamples = (await conn.QueryAsync<ScoreSample>(
            @"SELECT LeagueName, HomeTeamName AS HomeTeam, AwayTeamName AS AwayTeam, MatchTime,
                     HomeScore, AwayScore
              FROM dbo.FbMatch
              WHERE Status = 3 AND HomeScore IS NOT NULL AND AwayScore IS NOT NULL
                AND MatchTime < @cutoff
                AND MatchTime >= DATEADD(month, -18, @cutoff)",
            new { cutoff })).ToList();

        var model = ScorelineModel.Fit(trainSamples);

        Console.WriteLine($"# 半全场（BQ）模型 vs 盘口  {DateTime.Now:yyyy-MM-dd HH:mm}");
        Console.WriteLine();
        Console.WriteLine("| 项目 | 值 |");
        Console.WriteLine("|---|---|");
        Console.WriteLine($"| 有赛前 BQ 快照且已完赛 | {matches.Count} 场 |");
        Console.WriteLine($"| 档位不全 | {incomplete} 场 |");
        Console.WriteLine($"| 含占位赔率（≤1.01） | {placeholder} 场 |");
        Console.WriteLine($"| **可用** | **{usable.Count} 场** |");
        Console.WriteLine($"| 训练样本 | {trainSamples.Count} 场（< {cutoff:yyyy-MM-dd}） |");
        Console.WriteLine($"| 模型就绪 | {(model.Ready ? "是" : "**否**")} |");
        Console.WriteLine();

        if (!model.Ready)
        {
            Console.WriteLine("模型未就绪，后面算不了。");
            return 1;
        }

        // 逐场：市场概率（去水）、模型概率（HalfFullJoint）、实际档位
        var rows = new List<(double[] Model, double[] Market, int Actual, decimal Over)>();
        var noModel = 0;
        foreach (var (m, odds) in usable)
        {
            var joint = MilkPickEngine.HalfFullJoint(model, m.LeagueName, m.HomeTeam, m.AwayTeam, null);
            if (joint is null) { noModel++; continue; }

            var mp = new double[9];
            var sumInv = 0.0;
            for (var i = 0; i < 9; i++) sumInv += 1.0 / (double)odds[i];

            for (var i = 0; i < 9; i++)
            {
                var code = Codes[i];
                var hm = Side(code[0]);
                var fm = Side(code[2]);
                mp[i] = (double)joint[hm, fm];
            }
            // 联合分布按定义和为 1；若因截断略偏离，归一化一次，避免 Brier 被系统性抬高。
            var mSum = mp.Sum();
            if (mSum <= 0) { noModel++; continue; }
            for (var i = 0; i < 9; i++) mp[i] /= mSum;

            var mk = new double[9];
            for (var i = 0; i < 9; i++) mk[i] = (1.0 / (double)odds[i]) / sumInv;

            var hh = m.HalfHomeScore!.Value;
            var ha = m.HalfAwayScore!.Value;
            var ht = hh > ha ? 0 : hh == ha ? 1 : 2;
            var ft = m.HomeScore > m.AwayScore ? 0 : m.HomeScore == m.AwayScore ? 1 : 2;
            var codeActual = $"{CharOf(ht)}-{CharOf(ft)}";
            var actual = Array.IndexOf(Codes, codeActual);

            rows.Add((mp, mk, actual, (decimal)sumInv));
        }

        if (rows.Count == 0)
        {
            Console.WriteLine("一场都没算出来（模型缺 λ 或联合分布为空）。");
            return 1;
        }

        Console.WriteLine($"实际参与对比 **{rows.Count}** 场（模型算不出 λ 的 {noModel} 场已剔除）。");
        Console.WriteLine($"平均返奖率倒推：1/Σ(1/odds) = **{100.0 / rows.Average(r => (double)r.Over):F1}%**（竞彩半全场官方约 79.7%）。");
        Console.WriteLine();

        // ---- 一、逐档：实际频率 vs 模型 vs 盘口 ----
        Console.WriteLine("## 一、逐档概率（谁更接近实际）");
        Console.WriteLine();
        Console.WriteLine("| 档 | 实际 | 模型 | 盘口 | 模型偏差 | 盘口偏差 | 谁更近 |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---|");
        var modelB = 0.0;
        var marketB = 0.0;
        for (var i = 0; i < 9; i++)
        {
            var act = rows.Count(r => r.Actual == i) * 100.0 / rows.Count;
            var pm = rows.Average(r => r.Model[i]) * 100;
            var pk = rows.Average(r => r.Market[i]) * 100;
            var dm = pm - act;
            var dk = pk - act;
            Console.WriteLine($"| {Names[i]} | {act:F1}% | {pm:F1}% | {pk:F1}% | {dm:+0.0;-0.0;0.0} | {dk:+0.0;-0.0;0.0} | {(Math.Abs(dm) < Math.Abs(dk) ? "模型" : "盘口")} |");
        }
        Console.WriteLine();

        // ---- 二、Brier ----
        Console.WriteLine("## 二、9 档 Brier（越低越好，同一批场次）");
        Console.WriteLine();
        Console.WriteLine("| 权重（市场占比） | 说明 | Brier | 相对 0.3 |");
        Console.WriteLine("|---:|---|---:|---:|");
        var results = new List<(decimal W, double Brier)>();
        foreach (var w in Weights)
        {
            var bw = (double)w;
            var total = 0.0;
            foreach (var r in rows)
            {
                for (var i = 0; i < 9; i++)
                {
                    var p = r.Model[i] * (1 - bw) + r.Market[i] * bw;
                    var y = r.Actual == i ? 1.0 : 0.0;
                    total += (p - y) * (p - y);
                }
            }
            var b = total / rows.Count;
            results.Add((w, b));
            if (w == 0m) modelB = b;
            if (w == 1m) marketB = b;
        }
        var baseB = results.First(x => x.W == 0.3m).Brier;
        foreach (var (w, b) in results)
        {
            var note = w == 0m ? "纯模型" : w == 0.3m ? "**现状**" : w == 1m ? "纯盘口" : "—";
            Console.WriteLine($"| {w:0.0} | {note} | {b:F4} | {b - baseB:+0.0000;-0.0000;0.0000} |");
        }
        Console.WriteLine();

        // ---- 三、结论 ----
        Console.WriteLine("## 三、结论");
        Console.WriteLine();
        Console.WriteLine($"- 纯模型 Brier **{modelB:F4}**，纯盘口 **{marketB:F4}**，差 **{marketB - modelB:+0.0000;-0.0000}**（正=盘口更好）。");
        var best = results.OrderBy(x => x.Brier).First();
        var gain = baseB - best.Brier;
        Console.WriteLine($"- 现状（0.3）：{baseB:F4}；最优：权重 **{best.W:0.0}** → {best.Brier:F4}，改善 **{gain:+0.0000;-0.0000}**。");
        Console.WriteLine($"- 判定：{(gain >= MeaningfulGain ? $"**过门槛**（≥ {MeaningfulGain:F3}）。" : $"**未过门槛**（< {MeaningfulGain:F3}），不改。")}");
        Console.WriteLine();
        Console.WriteLine($"> 样本 {rows.Count} 场 × 9 档。以这个量级，单档 1 个点的差异基本是噪声，");
        Console.WriteLine("> 只有「纯模型 vs 纯盘口」这种全档合计的差才值得看。**不足以单独决定改配置。**");
        return 0;
    }
}
