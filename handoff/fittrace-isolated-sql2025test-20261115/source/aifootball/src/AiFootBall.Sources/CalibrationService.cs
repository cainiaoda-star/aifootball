using AiFootBall.Core.Models;
using AiFootBall.Data;

namespace AiFootBall.Sources;

/// <summary>
/// 命中率校准报表。回答三个此前无法回答的问题：
/// 1. 模型给的 55%/60%/70%，实际打出来是不是这个数；
/// 2. 模型比市场（去水概率）更准，还是只是复述市场；
/// 3. 命中率偏低是因为模型差，还是因为喂进去的是过期盘。
/// </summary>
public static class CalibrationService
{
    public static async Task<CalibrationReport> BuildAsync(
        BettingRepository repo, int days, CancellationToken ct = default)
    {
        var to = DateTime.Today;
        var from = to.AddDays(-Math.Max(1, days) + 1);
        var rows = await repo.GetCalibrationRowsAsync(from, to, ct);
        return Build(rows, from, to);
    }

    public static CalibrationReport Build(List<CalibrationRow> rows, DateTime from, DateTime to)
    {
        var report = new CalibrationReport { From = from, To = to, Total = rows.Count };

        if (rows.Count == 0)
        {
            report.Verdict = "区间内没有已结算的样本：校准无法进行。先把推荐跑起来并结算，至少积累 200 条再看。";
            return report;
        }

        report.Hits = rows.Count(r => r.Hit);
        report.Actual = Pct(report.Hits, rows.Count);
        report.Predicted = decimal.Round(rows.Average(r => r.HitProbability), 1);

        report.Brier = Math.Round(rows.Average(r => Brier(r.HitProbability, r.Hit)), 4);

        var priced = rows.Where(r => r.MarketProb is > 0).ToList();
        report.BrierBaseline = priced.Count > 0
            ? Math.Round(priced.Average(r => Brier(r.MarketProb!.Value, r.Hit)), 4)
            : 0;

        report.LogLoss = Math.Round(rows.Average(r => LogLoss(r.HitProbability, r.Hit)), 4);

        var withOdds = rows.Where(r => r.PickOdds is > 1m).ToList();
        report.PricedCount = withOdds.Count;
        report.Roi = withOdds.Count > 0 ? Roi(withOdds) : null;

        report.Bands = rows
            .GroupBy(r => (int)(r.HitProbability / 5))
            .OrderBy(g => g.Key)
            .Select(g => MakeBand(g.ToList(), $"{g.Key * 5}–{g.Key * 5 + 5}%", g.Key * 5, g.Key * 5 + 5))
            .ToList();

        report.ByModel = rows
            .GroupBy(r => string.IsNullOrWhiteSpace(r.ModelTag) ? "(未标注)" : r.ModelTag!)
            .OrderByDescending(g => g.Count())
            .Select(g => MakeBand(g.ToList(), g.Key, 0, 0))
            .ToList();

        report.ByDataAge = BuildDataAge(rows);

        report.Verdict = Verdict(report, rows, priced);
        return report;
    }

    private static List<CalibrationBand> BuildDataAge(List<CalibrationRow> rows)
    {
        var buckets = new (string Label, Func<double, bool> Match)[]
        {
            ("≤1 小时", h => h <= 1),
            ("1–3 小时", h => h > 1 && h <= 3),
            ("3–6 小时", h => h > 3 && h <= 6),
            ("6–12 小时", h => h > 6 && h <= 12),
            ("12–24 小时", h => h > 12 && h <= 24),
            (">24 小时", h => h > 24)
        };

        var list = new List<CalibrationBand>();
        foreach (var b in buckets)
        {
            var subset = rows.Where(r => r.DataAgeHours is double h && b.Match(h)).ToList();
            if (subset.Count == 0) continue;
            list.Add(MakeBand(subset, b.Label, 0, 0));
        }

        var unknown = rows.Where(r => r.DataAgeHours is null).ToList();
        if (unknown.Count > 0) list.Add(MakeBand(unknown, "无采集时间", 0, 0));
        return list;
    }

    private static CalibrationBand MakeBand(List<CalibrationRow> rows, string label, decimal low, decimal high)
    {
        var hits = rows.Count(r => r.Hit);
        var priced = rows.Where(r => r.PickOdds is > 1m).ToList();
        return new CalibrationBand
        {
            Label = label,
            Low = low,
            High = high,
            Count = rows.Count,
            Hits = hits,
            Actual = Pct(hits, rows.Count),
            Predicted = decimal.Round(rows.Average(r => r.HitProbability), 1),
            Gap = decimal.Round(Pct(hits, rows.Count) - rows.Average(r => r.HitProbability), 1),
            PricedCount = priced.Count,
            Roi = priced.Count > 0 ? Roi(priced) : null
        };
    }

    /// <summary>平注 1 单位、按真实水位结算的回报率（%）。中则得 odds，不中亏 1。</summary>
    private static decimal Roi(List<CalibrationRow> priced)
    {
        var profit = priced.Sum(r => (r.Hit ? r.PickOdds!.Value : 0m) - 1m);
        return decimal.Round(profit / priced.Count * 100m, 1);
    }

    private static decimal Pct(int part, int total)
        => total == 0 ? 0 : decimal.Round(100m * part / total, 1);

    private static double Brier(decimal probPct, bool hit)
    {
        var p = (double)probPct / 100.0;
        var y = hit ? 1.0 : 0.0;
        return (p - y) * (p - y);
    }

    private static double LogLoss(decimal probPct, bool hit)
    {
        var p = Math.Clamp((double)probPct / 100.0, 0.001, 0.999);
        return -(hit ? Math.Log(p) : Math.Log(1 - p));
    }

    private static string Verdict(CalibrationReport report, List<CalibrationRow> rows, List<CalibrationRow> priced)
    {
        if (report.Total < 100)
            return $"样本仅 {report.Total} 条，误差范围过大（95% 区间约 ±{Math.Round(98 / Math.Sqrt(report.Total), 1)} 个点），还不能判断校准好坏。";

        var parts = new List<string>();

        var gap = report.Bias;
        parts.Add(gap switch
        {
            >= 5 => $"预测整体偏保守 {gap:0.0} 个点（实际 {report.Actual}% > 预测 {report.Predicted}%），水位门槛可能设得过严。",
            <= -5 => $"预测整体偏乐观 {Math.Abs(gap):0.0} 个点（实际 {report.Actual}% < 预测 {report.Predicted}%），高估了自身信号强度。",
            _ => $"整体校准良好，偏差仅 {gap:0.0} 个点。"
        });

        if (priced.Count >= 50)
        {
            if (report.Brier < report.BrierBaseline - 0.002)
                parts.Add($"模型优于市场（Brier {report.Brier:0.0000} < 市场 {report.BrierBaseline:0.0000}），存在可利用的 edge。");
            else if (report.Brier > report.BrierBaseline + 0.002)
                parts.Add($"模型不如直接采用市场去水概率（Brier {report.Brier:0.0000} > 市场 {report.BrierBaseline:0.0000}），特征与训练样本都需要重做。");
            else
                parts.Add($"模型与市场持平（Brier 差 {Math.Abs(report.Brier - report.BrierBaseline):0.0000}），没有可提取的 edge，此时提高中奖率只能靠票型结构而不是预测。");
        }

        if (report.Roi is decimal roi)
            parts.Add($"平注回报率 {roi:0.0}%（{priced.Count} 场）。");

        var stale = report.ByDataAge.Where(b => b.Label is ">24 小时" or "12–24 小时" or "无采集时间").ToList();
        if (stale.Count > 0 && stale.Sum(b => b.Count) >= rows.Count * 0.3)
            parts.Add("三成以上样本用的是 12 小时以前的盘，命中率受盘龄污染，先修采集频率再调模型。");

        return string.Join(" ", parts);
    }

    /// <summary>把校准结果渲染成复盘文档里的一节。没有样本时只写一句提示，不撑空表。</summary>
    public static string ToMarkdown(CalibrationReport r)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## 校准（近 30 天已结算）");
        sb.AppendLine();

        if (r.Total == 0)
        {
            sb.AppendLine("还没有带预测值的已结算样本。新字段从 2026-11-03 起才落库，需要几天积累。");
            sb.AppendLine();
            return sb.ToString();
        }

        sb.AppendLine($"样本 {r.Total} 条，实际命中 {r.Hits} 条（{r.Actual}%），平均预测 {r.Predicted}%，偏差 {r.Bias:+0.0;-0.0;0} 个点。");
        sb.AppendLine();
        sb.AppendLine("| 预测区间 | 场次 | 命中 | 实际 | 预测 | 偏差 | 回报率 |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var b in r.Bands)
        {
            var roi = b.Roi is decimal v ? v.ToString("0.0") + "%" : "—";
            sb.AppendLine($"| {b.Label} | {b.Count} | {b.Hits} | {b.Actual}% | {b.Predicted}% | {b.Gap:+0.0;-0.0;0} | {roi} |");
        }
        sb.AppendLine();
        sb.AppendLine($"Brier {r.Brier:0.0000}（市场基准 {r.BrierBaseline:0.0000}，越小越好；0.25 等于永远猜 50%），对数损失 {r.LogLoss:0.000}。");
        sb.AppendLine();
        sb.AppendLine("> " + r.Verdict);
        sb.AppendLine();
        return sb.ToString();
    }
}
