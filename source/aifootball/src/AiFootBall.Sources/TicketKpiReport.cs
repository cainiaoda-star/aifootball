using System.Text;
using AiFootBall.Core.Models;

namespace AiFootBall.Sources;

/// <summary>
/// 票面 KPI 的 Markdown 输出，接在每日复盘末尾。
/// 与校准节分工：校准看「单场方向准不准」，这里看「这样买票到底回不回本」。
/// </summary>
public static class TicketKpiReport
{
    public static string ToMarkdown(TicketKpiSummary kpi)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"## 票面 KPI（{kpi.From:MM-dd} 至 {kpi.To:MM-dd} 已结算）");
        sb.AppendLine();

        if (kpi.Rows.Count == 0)
        {
            sb.AppendLine(kpi.Verdict);
            if (kpi.PendingTickets > 0)
                sb.AppendLine();
            if (kpi.PendingTickets > 0)
                sb.AppendLine($"另有 {kpi.PendingTickets} 张票在等赛果。");
            return sb.ToString();
        }

        sb.AppendLine("| 票型 | 腿数 | 票数 | 有奖 | 回本 | 本金 | 奖金 | 实际回报 | 模型中奖率 | 实际中奖率 |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var r in kpi.Rows.OrderByDescending(x => x.Tickets))
        {
            sb.AppendLine($"| {r.PassType} | {r.LegCount} | {r.Tickets} | {r.Won} | {r.Backed} "
                        + $"| {r.Bet:0.##} | {r.Prize:0.##} | {r.Roi:0.000} "
                        + $"| {(r.PredictedHit is null ? "-" : $"{r.PredictedHit:0.0}%")} | {r.ActualHit:0.0}% |");
        }

        sb.AppendLine();
        sb.AppendLine($"合计本金 {kpi.TotalBet:0.##} 元，奖金 {kpi.TotalPrize:0.##} 元，"
                    + $"盈亏 {kpi.TotalPrize - kpi.TotalBet:+0.##;-0.##;0} 元。");
        if (kpi.PendingTickets > 0)
            sb.AppendLine($"另有 {kpi.PendingTickets} 张在等赛果，未计入。");
        sb.AppendLine();
        sb.AppendLine(kpi.Verdict);
        sb.AppendLine();
        sb.AppendLine("> 「有奖」是至少一注中，「回本」是奖金盖过本金。串关这两个数会明显分叉，只看有奖会高估自己。");
        return sb.ToString();
    }
}
