using System.Text;
using AiFootBall.Core.Models;

namespace AiFootBall.Sources;

/// <summary>
/// 「排名靠后的一方先进球」专题（2026-11-08 用户提出，要求进复盘长期对照）。
///
/// 起因：11-07 三场——周六007 朴次茅斯（英冠第 10/24）、周六008 马拉加（西甲第 20/20 垫底）
/// 都是主场先进球后 1:2 输（半全场「胜负」）；周六027 卡萨皮亚（葡超第 17/18）客场先进球后 1:1（「负平」）。
/// 用户判断这三场有共同点，要求查库验证并长期记录。
///
/// 实测结论（2024-08 起 2.8 万场、42 个半场数据可信的联赛）：
/// 1. 现象成立且单调——领先方排名越靠后，越守不住。但**弱队领先仍能守住 65%~75%**，
///    所以它是「别把弱队领先当赢」的提醒，不是「押反转」的理由。
/// 2. 客队领先比主队领先更难守：客队弱档守住 65.1%，被逆转 14.4%（主队弱档 74.6% / 8.1%）。
/// 3. 关键限制：**赛前不知道谁先进球**。把「该组合半场领先的基线概率」乘上「领先后被翻的条件概率」
///    才是半全场反转档的真实概率，这样算出来三场分别是 1.85% / 2.84% / 4.70%，
///    对应 32 / 26 / 17 倍赔率 → EV 0.59 / 0.74 / 0.80，全是负期望或打平。
///    这条比现象本身更重要：**赛后能复盘出的规律，赛前赔率往往已经吃掉。**
/// </summary>
public static class LeadStandingStudy
{
    /// <summary>分位分档的展示名。</summary>
    private static readonly string[] BandNames = { "强 (0-25%)", "中上 (25-50%)", "中下 (50-75%)", "弱 (75-100%)" };

    /// <summary>
    /// 基线一：赛前「谁先进球」。行=主队分位档，列=客队分位档，值=主队 / 客队半场领先的概率(%)。
    /// 2026-11-08 实测，2.8 万场（42 个半场可信联赛，2024-08 起）。半场平约 34.6%~41.9%，扣掉即可。
    /// </summary>
    private static readonly decimal[,] HomeLeadBase =
    {
        { 34.9m, 42.6m, 45.6m, 50.5m },
        { 27.7m, 35.3m, 37.7m, 41.8m },
        { 24.3m, 30.6m, 35.6m, 37.1m },
        { 22.0m, 27.3m, 29.8m, 35.0m },
    };

    private static readonly decimal[,] AwayLeadBase =
    {
        { 24.9m, 18.8m, 16.4m, 14.8m },
        { 30.9m, 22.8m, 21.3m, 19.9m },
        { 35.1m, 28.4m, 24.2m, 22.8m },
        { 38.7m, 31.7m, 28.7m, 23.7m },
    };

    /// <summary>
    /// 基线二：半场领先后能不能守住。守住/被追平/被逆转（%），按领先方与分位分档。
    /// 行：0=主队领先、1=客队领先；列：4 个分位档。
    /// </summary>
    private static readonly decimal[,] HoldPct =
    {
        { 85.6m, 79.4m, 76.2m, 74.6m },
        { 78.4m, 71.9m, 67.8m, 65.1m },
    };

    private static readonly decimal[,] DrawPct =
    {
        { 10.6m, 14.4m, 16.2m, 17.3m },
        { 15.2m, 18.3m, 19.9m, 20.6m },
    };

    private static readonly decimal[,] LosePct =
    {
        { 3.8m, 6.2m, 7.6m, 8.1m },
        { 6.4m, 9.8m, 12.3m, 14.4m },
    };

    private static readonly int[,] BaseN =
    {
        { 7495, 5624, 4975, 3968 },
        { 5751, 4116, 3477, 2734 },
    };

    public static int BandOf(decimal pct) => pct < 0.25m ? 0 : pct < 0.5m ? 1 : pct < 0.75m ? 2 : 3;

    /// <summary>半全场结果类型文本（半-全，3胜 1平 0负）。</summary>
    public static string HalfFullName(LeadOutcomeRow r)
    {
        if (!r.Finished || !r.HasHalf) return "—";
        var ht = r.HalfHome > r.HalfAway ? "3" : r.HalfHome == r.HalfAway ? "1" : "0";
        var ft = r.HomeScore > r.AwayScore ? "3" : r.HomeScore == r.AwayScore ? "1" : "0";
        return (ht, ft) switch
        {
            ("3", "3") => "胜胜", ("3", "1") => "胜平", ("3", "0") => "胜负",
            ("1", "3") => "平胜", ("1", "1") => "平平", ("1", "0") => "平负",
            ("0", "3") => "负胜", ("0", "1") => "负平", _ => "负负",
        };
    }

    /// <summary>生成复盘章节。当天样本不足（没排名或没半场）时如实说明，不编。</summary>
    public static string ToMarkdown(DateTime day, IReadOnlyList<LeadOutcomeRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## 排名与先进球（学习，{day:MM-dd}）");
        sb.AppendLine();
        sb.AppendLine("> 排名靠后的一方先进球，**大多数时候还是守得住**（弱队主场 74.6%、客场仅 65.1%）。");
        sb.AppendLine("> 这不是「押反转」的理由，而是「别把弱队先进球当成会赢」的提醒——三场 11-07 的大奶就是这么来的。");
        sb.AppendLine();

        var withHalf = rows.Where(r => r.HasHalf && r.LeadSide != 0).ToList();
        var underdogs = withHalf.Where(r => r.LeadIsUnderdog).ToList();

        sb.AppendLine($"### 今天的场次（共 {rows.Count} 场，半场分出领先 {withHalf.Count} 场）");
        sb.AppendLine();
        sb.AppendLine("「排名靠后」= 半场领先那一方的赛前名次比对手低（不要求它在联赛后半区）。");
        sb.AppendLine();

        if (withHalf.Count == 0)
        {
            sb.AppendLine("今天没有可判断「谁先进球」的场次（半场比分缺失，或全部半场 0:0）。");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("| 场次 | 联赛 | 对阵 | 赛前排名 | 半场 | 全场 | 半全场 | 领先方 | 结局 |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
            foreach (var r in withHalf.OrderByDescending(r => r.LeadIsUnderdog).ThenBy(r => r.MatchNo))
            {
                var rank = r.HasRank ? $"{r.HomeRankPre}/{r.TeamCount} vs {r.AwayRankPre}/{r.TeamCount}" : "无";
                var lead = r.LeadSide == 1 ? "主队" : "客队";
                var who = r.LeadIsUnderdog ? $"**{lead}（靠后）**" : lead;
                var ending = r.Finished
                    ? r.LeadWon == true ? "守住" : r.LeadDrawn == true ? "被追平" : "被逆转"
                    : "待补";
                sb.AppendLine($"| {r.MatchNo} | {r.LeagueName} | {r.HomeTeam} vs {r.AwayTeam} | {rank} | " +
                              $"{r.HalfHome}:{r.HalfAway} | {(r.Finished ? $"{r.HomeScore}:{r.AwayScore}" : "—")} | " +
                              $"{HalfFullName(r)} | {who} | {ending} |");
            }
            sb.AppendLine();

            var decided = underdogs.Where(r => r.Finished).ToList();
            if (decided.Count > 0)
            {
                var hold = decided.Count(r => r.LeadWon == true);
                var draw = decided.Count(r => r.LeadDrawn == true);
                var lose = decided.Count(r => r.LeadLost == true);
                sb.AppendLine($"**排名靠后一方先进球：{decided.Count} 场** —— 守住 {hold}、被追平 {draw}、被逆转 {lose}。");
                sb.AppendLine();
            }
        }

        sb.AppendLine("### 基线一：赛前「谁先进球」（按双方排名分位，2.8 万场）");
        sb.AppendLine();
        sb.AppendLine("| 主队分位 \\ 客队分位 | " + string.Join(" | ", BandNames) + " |");
        sb.AppendLine("|---|---|---|---|---|");
        for (var i = 0; i < 4; i++)
        {
            var cells = Enumerable.Range(0, 4).Select(j => $"主 {HomeLeadBase[i, j]:0.0}% · 客 {AwayLeadBase[i, j]:0.0}%");
            sb.AppendLine($"| {BandNames[i]} | " + string.Join(" | ", cells) + " |");
        }
        sb.AppendLine();
        sb.AppendLine("> 每格是「主队半场领先 / 客队半场领先」的概率，剩下的约 35%~42% 是半场平。");
        sb.AppendLine("> 实战意义：弱主队打强客队，主队先进球只有 22%、客队有 38.7%——所以押主队「胜胜」本身就先输一截。");
        sb.AppendLine();

        sb.AppendLine("### 基线二：半场领先后能不能守住（2.8 万场）");
        sb.AppendLine();
        sb.AppendLine("| 领先方 \\ 排名分位 | 样本 | 守住 | 被追平 | 被逆转 |");
        sb.AppendLine("|---|---|---|---|---|");
        for (var side = 0; side < 2; side++)
        for (var b = 0; b < 4; b++)
        {
            var name = (side == 0 ? "主队" : "客队") + " · " + BandNames[b];
            sb.AppendLine($"| {name} | {BaseN[side, b]} | {HoldPct[side, b]:0.0}% | {DrawPct[side, b]:0.0}% | {LosePct[side, b]:0.0}% |");
        }
        sb.AppendLine();
        sb.AppendLine("> 单调关系成立：排名越靠后越守不住，客队比主队更脆弱（弱档被逆转 14.4% vs 8.1%）。");
        sb.AppendLine("> 但注意绝对值：**最脆弱的一档也有 65% 守住**。反转是少数剧本，赔率高是应该的。");
        sb.AppendLine();

        sb.AppendLine("### 怎么用（2026-11-08 实测，11-07 那三场做样本）");
        sb.AppendLine();
        sb.AppendLine("| 场次 | 结果类型 | 赔率 | 赛前真实概率 | EV |");
        sb.AppendLine("|---|---|---|---|---|");
        sb.AppendLine("| 周六007 朴次茅斯 10/24 打 5/24 | 胜负 3-0 | 32.00 | 27.7% × 6.2% = 1.72% | 0.55 |");
        sb.AppendLine("| 周六008 马拉加 20/20 打 16/20 | 胜负 3-0 | 26.00 | 35.0% × 8.1% = 2.84% | 0.74 |");
        sb.AppendLine("| 周六027 阿尔维卡 11/18 打 17/18 | 负平 0-1 | 17.00 | 22.8% × 20.6% = 4.70% | 0.80 |");
        sb.AppendLine();
        sb.AppendLine("（前两个数分别是「该方半场领先的基线概率」和「领先后被逆转/追平的条件概率」，都来自上面的基线表。）");
        sb.AppendLine();
        sb.AppendLine("三注全是负期望或打平（返奖率 0.797）。**结论：这个现象看得见、吃不着。**");        sb.AppendLine("真要用，只有两条：");
        sb.AppendLine("1. **不要**把「弱队/客队先进球」当成会赢——它在弱档有 8%~14% 会被直接逆转，半全场顺风档（胜胜/负负）要打折。");
        sb.AppendLine("2. 反转档里「负平」比「胜负」定价更合理（17 倍对 4.7% 几乎打平），「胜负」最贵（32 倍对 1.72%）。要碰只碰负平。");
        sb.AppendLine();
        return sb.ToString();
    }
}
