using System.Text;
using AiFootBall.Core.Models;

namespace AiFootBall.Sources;

/// <summary>
/// 否决台账：把「有方向但被规则挡下」的场次记下来，赛后回填结果。
/// 目的是让阈值可被检验——现在「75% 极端大热不追」这类规则挡掉了什么、
/// 挡得对不对，只能靠手工 SQL，而且很容易被赛后数据污染。
/// </summary>
public static class VetoLog
{
    /// <summary>原因代码 → 中文名。新增代码必须在这里登记，否则报表里显示原文。</summary>
    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // 数值跟常量联动：改线的时候报表标题不会还写着旧的 75%
        ["heat-extreme"] = $"必发极端大热不追(≥{RecommendationScoring.ExtremeHotRatio:0}%)",
        ["conflict-drop-awayhot"] = "冲突：主降但客热",
        ["conflict-rise-homehot"] = "冲突：主升但主热",
        ["conflict-deep-home"] = "冲突：深主让 vs 强客向",
        ["trap-bigdrop"] = "主胜大降=诱盘",
        ["trap-bigdrop-hot"] = "主胜大降+主热=诱盘",
        ["small-drop-no-lead"] = "小降但必发没领先10点",
        ["no-signal"] = "无信号(仅趋势)",
        ["low-signal"] = "弱信号(等级<2)",
        ["low-prob"] = "概率未达发布线",
        ["no-prob"] = "没有命中率估计",
        ["odds-fallback"] = "仅赔率兜底",
        ["no-handicap"] = "无让球方向",
        ["deep-hcp"] = "深盘无大小球",
        ["cap"] = "够格但超出当日名额",
    };

    public static string NameOf(string code)
        => Names.TryGetValue(code, out var n) ? n : code;

    /// <summary>归类一条没发出去的推荐被什么挡住。</summary>
    public static string Classify(MatchRecommendation x)
    {
        // 引擎里的硬否决已经写了代码，优先用它
        if (!string.IsNullOrWhiteSpace(x.VetoCode)) return x.VetoCode!;

        if (x.IsOddsFallback) return "odds-fallback";

        if (x.Board == Boards.Bd)
        {
            if (x.RecHandicap is null) return "no-handicap";
            if (RecommendationSelection.DeepHandicapWithoutTotal(x)) return "deep-hcp";
            if (x.HandicapProbability is decimal hp && hp < RecommendationScoring.MinFeaturedHitProbability)
                return "low-prob";
            return "cap";
        }

        if (x.SignalLevel < 2) return "low-signal";
        if (x.HitProbability is null) return "no-prob";
        if (x.HitProbability < RecommendationScoring.MinFeaturedHitProbability) return "low-prob";
        return "cap";
    }

    /// <summary>
    /// 判定一条否决记录「照打会不会中」。
    /// 抽成纯函数是为了能测：真正的回填要等比赛开完，而判定规则（尤其北单让球那支）
    /// 出错的代价是整本台账的结论都反了。
    ///
    /// 竞彩卖胜平负，被挡的方向直接对赛果；
    /// 北单卖让球，被挡的是让球那一注，必须用让球结果判，不能用胜平负。
    /// </summary>
    /// <returns>两个都 null 表示没法判（未完场或缺比分），调用方留给下一轮。</returns>
    public static (bool? Hit, bool? HcpHit) Judge(
        string board, char? pick, char? hcpPick, decimal? handicap,
        char? resultSpf, int? home, int? away)
    {
        if (resultSpf is null) return (null, null);   // 还没完场

        bool? hcpHit = null;
        if (hcpPick is char hp && handicap is decimal h && home is int hs && away is int aws)
            hcpHit = HandicapMath.Result(hs, aws, h) == hp;

        // 北单优先看让球；让球判不出来（缺盘口/比分）才退回胜平负。
        bool? hit = board == Boards.Bd && hcpHit is not null
            ? hcpHit
            : pick is char p ? p == resultSpf : null;

        return (hit, hcpHit);
    }

    /// <summary>
    /// 把「没进发布名单」的场次整理成台账记录。
    /// 连方向都没有的场次跳过——没有方向就没法在赛后判定对错，记了也是噪音。
    /// </summary>
    public static List<VetoRecord> Build(
        string board, IReadOnlyList<MatchRecommendation> raw,
        IReadOnlyList<MatchRecommendation> published, DateTime day)
    {
        if (board == Boards.Sfc) return new List<VetoRecord>();   // 胜负彩全发，不存在否决

        var publishedNos = published
            .Select(x => x.MatchNo)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.Ordinal);

        var rows = new List<VetoRecord>();
        foreach (var r in raw)
        {
            if (string.IsNullOrWhiteSpace(r.MatchNo)) continue;
            if (publishedNos.Contains(r.MatchNo)) continue;

            var spfPick = r.ProposedResult ?? r.RecResult;
            // 北单卖的是让球，被挡的也是让球那一注，所以主方向取让球；
            // 竞彩卖的是胜平负，取胜平负方向。
            var pick = board == Boards.Bd ? (r.RecHandicap ?? spfPick) : spfPick;
            if (pick is null) continue;

            rows.Add(new VetoRecord
            {
                Board = board,
                RecDate = day.Date,
                MatchId = r.MatchId,
                MatchNo = r.MatchNo!,
                HomeTeam = r.HomeTeam,
                AwayTeam = r.AwayTeam,
                MatchTime = r.MatchTime,
                Pick = pick,
                HcpPick = board == Boards.Bd ? r.RecHandicap : null,
                Handicap = board == Boards.Bd ? r.Handicap : null,
                ReasonCode = Classify(r),
                Reason = Truncate(r.SignalDesc, 480),
                SignalLevel = r.SignalLevel,
                HitProbability = r.HitProbability,
                HcpProbability = r.HandicapProbability,
                // 北单被挡的是让球那一注，水位要用让球 SP；没有 SP 才退回胜平负水位。
                PickOdds = board == Boards.Bd && r.HandicapOdds is not null ? r.HandicapOdds : r.PickOdds,
                MarketProb = r.MarketProb,
                RatioHome = r.RatioHome,
                RatioAway = r.RatioAway,
                OddsDelta = r.HomeOddsDelta
            });
        }
        return rows;
    }

    /// <summary>
    /// 复核一份台账：按原因看「照打会怎样」。
    /// 样本不足时不给结论——12 场就下判断正是这次要修的病。
    /// </summary>
    public static VetoReport Report(int days, List<VetoStatRow> rows)
    {
        var report = new VetoReport
        {
            Days = days,
            Rows = rows,
            Total = rows.Sum(r => r.Total),
            Settled = rows.Sum(r => r.Settled)
        };

        if (report.Total == 0)
        {
            report.Verdict = "还没有被否决的场次记录，从下一次出票作业开始累积。";
            return report;
        }
        if (report.Settled == 0)
        {
            report.Verdict = $"已记录 {report.Total} 场否决，还没有一场开奖。"
                           + "这批比赛打完后每小时结算一次会自动回填，届时这里才出命中率。";
            return report;
        }
        if (report.Settled < 100)
        {
            report.Verdict = $"已记录 {report.Total} 场否决，其中 {report.Settled} 场已开奖。"
                           + "样本不足 100 场，先别看单条规则的好坏——这个量级下命中率波动有 ±10 个点。";
            return report;
        }

        var heat = rows.FirstOrDefault(r => r.ReasonCode == "heat-extreme");
        if (heat is { Settled: >= 30, Roi: decimal roi })
        {
            report.Verdict = roi >= 1.0m
                ? $"「必发极端大热不追」挡掉的 {heat.Settled} 场里，照打回报 {roi:0.000}——这条规则在赔钱，该放开。"
                : $"「必发极端大热不追」挡掉的 {heat.Settled} 场里，照打回报 {roi:0.000}——这条规则挡对了。";
        }
        else
        {
            var overall = report.Settled > 0 ? rows.Sum(r => (r.Roi ?? 0) * r.Settled) / report.Settled : 0m;
            report.Verdict = $"整体照打回报 {overall:0.000}（1.0 为不亏不赚）。单条规则样本还不够，继续积累。";
        }
        return report;
    }

    /// <summary>
    /// 复盘用：竞彩和北单分开列。北单一天近百场、否决原因以「概率未达线」为主，
    /// 混在一张表里会把竞彩那几行真正要看的关键否决盖住。
    /// </summary>
    public static string ToMarkdown(VetoReport jc, VetoReport bd)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"## 否决台账（近 {jc.Days} 天）");
        sb.AppendLine();
        sb.AppendLine("被规则挡下的场次，赛后按当时的赔率回填结果。回答的是「挡得对不对」，"
                    + "不改任何发出去的票，也不进命中率统计。");
        sb.AppendLine();

        AppendTable(sb, "竞彩", jc);
        AppendTable(sb, "北单", bd);

        if (jc.Total > 0) sb.AppendLine($"**竞彩**：{jc.Verdict}");
        if (bd.Total > 0) sb.AppendLine($"**北单**：{bd.Verdict}");
        return sb.ToString();
    }

    private static void AppendTable(StringBuilder sb, string label, VetoReport report)
    {
        sb.AppendLine($"**{label}**（{report.Total} 场，已开奖 {report.Settled}）");
        sb.AppendLine();
        if (report.Rows.Count == 0)
        {
            sb.AppendLine("（无记录）");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| 否决原因 | 场次 | 已开奖 | 照打命中 | 命中率 | 均水位 | 照打回报 |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var r in report.Rows.OrderByDescending(r => r.Total))
        {
            sb.AppendLine($"| {NameOf(r.ReasonCode)} | {r.Total} | {r.Settled} | {r.Hits} | "
                        + $"{(r.HitRate is decimal hr ? hr.ToString("0.0") + "%" : "-")} | "
                        + $"{(r.AvgOdds is decimal a ? a.ToString("0.00") : "-")} | "
                        + $"{(r.Roi is decimal v ? v.ToString("0.000") : "-")} |");
        }
        sb.AppendLine();
    }

    private static string Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);
}
