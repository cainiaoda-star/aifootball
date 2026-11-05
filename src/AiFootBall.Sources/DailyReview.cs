using System.Text;
using AiFootBall.Core.Models;
using AiFootBall.Data;

namespace AiFootBall.Sources;

/// <summary>
/// 每天一份复盘。竞彩、胜负彩全部场次都写，北单 A、B、C 级都写。没进精选的也写。
/// 精选变少不能把这篇写短。胜负彩受注卡没有开赛时间，按比赛表的开球时间归到当天。
/// </summary>
public static class DailyReview
{
    public static string ReviewRoot() => DeployPaths.Under("reviews");

    public static async Task<string> WriteAsync(
        RecommendationService svc, BettingRepository repo, DateTime day, CancellationToken ct = default,
        MilkScanService? milk = null, TicketRepository? tickets = null)
    {
        var jcCards = await repo.GetCardsByKickoffAsync(Boards.Jc, day, ct);
        var bdCards = await repo.GetCardsByKickoffAsync(Boards.Bd, day, ct);
        var sfcCards = await repo.GetCardsByKickoffAsync(Boards.Sfc, day, ct);
        var jc = await svc.ScoreForReviewAsync(Boards.Jc, jcCards, ct);
        var bd = await svc.ScoreForReviewAsync(Boards.Bd, bdCards, ct);
        var sfc = await svc.ScoreForReviewAsync(Boards.Sfc, sfcCards, ct);
        var saved = await repo.GetRecommendationsByDateAsync(day, ct);

        var ids = jc.Concat(bd).Concat(sfc).Where(r => r.MatchId is not null).Select(r => r.MatchId!.Value).Distinct().ToArray();
        var scores = await repo.GetReviewScoresAsync(ids, ct);
        var exchange = await repo.GetExchangeSpanAsync(ids, ct);

        var rows = new List<ReviewRow>();
        rows.AddRange(ToRows(Boards.Jc, jc, scores, exchange, saved));
        rows.AddRange(ToRows(Boards.Bd, bd, scores, exchange, saved));
        rows.AddRange(ToRows(Boards.Sfc, sfc, scores, exchange, saved));

        string? milkBody = null;
        if (milk is not null)
            milkBody = await milk.ReviewBodyAsync(jcCards.Concat(bdCards).ToList(), scores, ct);

        var dir = ReviewRoot();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, day.ToString("yyyy-MM-dd") + ".md");
        var notePath = Path.Combine(dir, day.ToString("yyyy-MM-dd") + ".note.md");
        var note = File.Exists(notePath) ? await File.ReadAllTextAsync(notePath, ct) : null;

        // 复盘只写「今天哪场中没中」，看不出「模型说 55% 的那些场到底是不是 55%」。
        // 把近 30 天校准接在后面，才能判断门槛该不该动。
        var calib = await CalibrationService.BuildAsync(repo, 30, ct);
        var body = Render(day, DateTime.Now, rows, milkBody) + Environment.NewLine + CalibrationService.ToMarkdown(calib);

        // 校准回答「方向准不准」，票面 KPI 回答「这样买票回不回本」。
        // 串关可以经常中奖却长期亏钱，两者必须分开看。
        if (tickets is not null)
        {
            try
            {
                var kpi = await tickets.GetKpiAsync(90, ct);
                body += TicketKpiReport.ToMarkdown(kpi);
            }
            catch (Exception) { /* 台账不可用时不拖垮复盘 */ }
        }

        // 否决台账回答「没发的那些场，挡得对不对」。
        // 只记录挡掉的场次不打紧，关键是让「阈值」第一次有了可被检验的数据。
        try
        {
            var jcVeto = VetoLog.Report(30, await repo.GetVetoStatsAsync(30, ct, Boards.Jc));
            var bdVeto = VetoLog.Report(30, await repo.GetVetoStatsAsync(30, ct, Boards.Bd));
            body += VetoLog.ToMarkdown(jcVeto, bdVeto);
        }
        catch (Exception) { /* 台账表未建时不影响复盘 */ }

        await File.WriteAllTextAsync(path, WithFieldNote(body, note), Encoding.UTF8, ct);
        return path;
    }

    public static string Render(DateTime day, DateTime writtenAt, IReadOnlyList<ReviewRow> rows, string? milkBody = null)
    {
        var jc = rows.Where(r => r.Board == Boards.Jc).ToList();
        var bd = rows.Where(r => r.Board == Boards.Bd).ToList();
        var sfc = rows.Where(r => r.Board == Boards.Sfc).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"# {day:yyyy-MM-dd} 复盘");
        sb.AppendLine();
        sb.AppendLine($"写于 {writtenAt:yyyy-MM-dd HH:mm}。用终盘赔率和**开赛前最后一次**必发重算，不是保存那一刻的页面。");
        sb.AppendLine("必发会一直记到赛中，取最后一条等于把比分喂进信号，所以复盘卡在开赛时刻。");
        sb.AppendLine("竞彩每场都写。胜负彩按开赛日每场都写。北单 A、B、C 级每场都写。没进精选、没有方向的也写。精选少了，这篇不能跟着短。");
        sb.AppendLine("胜负彩页面会把受注场次都列出来。这篇里「页面会发布」仍只算过了 55% 的场。");
        sb.AppendLine();
        sb.AppendLine("> **2026-11-04 起，胜平负 / 让球 / 大小球 / 胜负彩 / 北单全部停止对外发布，只发竞彩大奶。**");
        sb.AppendLine("> 下面的「页面会发布」是当天按旧口径算出来的方向，只作校准样本，不代表那天真的发了；");
        sb.AppendLine("> 那天真正发出去的只有大奶，见「大奶」一节和票面台账。");
        sb.AppendLine();
        sb.AppendLine("这一篇不改 55% 门槛，不改必发 75% 不追热，不够不凑。单日样本不拿来调规则，近 60 日完场优先。");
        sb.AppendLine();
        sb.AppendLine("## 总账");
        sb.AppendLine();
        AppendTally(sb, new (string Name, IReadOnlyList<ReviewRow> Rows)[]
        {
            ("竞彩", jc),
            ("胜负彩", sfc),
            ("北单 A 级", bd.Where(r => r.Tier == LeagueTier.A).ToList()),
            ("北单 B 级", bd.Where(r => r.Tier == LeagueTier.B).ToList()),
            ("北单 C 级", bd.Where(r => r.Tier == LeagueTier.C).ToList()),
            ("北单未分级", bd.Where(r => r.Tier == "未分级").ToList())
        });
        sb.AppendLine();
        sb.AppendLine("「页面会发布」只算终盘重算仍然过 55% 的场次。开赛前进过精选、终盘被撤掉的，不在这行里。那种场次写在下面，依据里会有「极端大热，不追热」。库里没有，不能当成没推荐过。");
        sb.AppendLine();
        sb.AppendLine("## 成功");
        sb.AppendLine();
        AppendNamed(sb, "页面会发布并且命中", rows.Where(r => r.Published && r.Verdict == "命中"));
        sb.AppendLine();
        sb.AppendLine("## 失败");
        sb.AppendLine();
        AppendNamed(sb, "页面会发布但未中", rows.Where(r => r.Published && r.Verdict == "未中"));
        AppendNamed(sb, "有方向、没发布、未中", rows.Where(r => !r.Published && r.Verdict == "未中"));
        AppendNamed(sb, "偏热方向未中", rows.Where(r => r.Verdict == "未中" && r.HotSide));
        sb.AppendLine();
        sb.AppendLine("## 不改的");
        sb.AppendLine();
        sb.AppendLine("- 精选门槛停在 55%。发布场次少，是不够，不是漏写复盘。");
        sb.AppendLine("- 必发一侧 ≥75% 不追这一侧。60% 到 75% 仍可作偏热方向，未中就记在上面，不因一场把线挪走。");
        sb.AppendLine("- 库里没有，不代表当时没精选。保存有 08:40、11:40、16:40、20:40，终版在停售前 8 分钟（工作日 21:50、周末 22:50）。中间改口看 `evidence/recommendations` 里的图片。");
        sb.AppendLine();
        AppendMilk(sb, milkBody);
        AppendOu(sb, rows);
        AppendBoard(sb, "竞彩（全部场次）", jc);
        AppendSfc(sb, sfc);
        AppendBoard(sb, "北单 A 级", bd.Where(r => r.Tier == LeagueTier.A).ToList());
        AppendBoard(sb, "北单 B 级", bd.Where(r => r.Tier == LeagueTier.B).ToList());
        AppendBoard(sb, "北单 C 级", bd.Where(r => r.Tier == LeagueTier.C).ToList());
        AppendBoard(sb, "北单未分级", bd.Where(r => r.Tier == "未分级").ToList());
        return sb.ToString();
    }

    /// <summary>早上重写全天复盘时，把当天手记接在后面。手记文件本身不被覆盖。</summary>
    public static string WithFieldNote(string review, string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return review;
        return review.TrimEnd() + Environment.NewLine + Environment.NewLine
            + "## 当场记下" + Environment.NewLine + Environment.NewLine
            + note.Trim() + Environment.NewLine;
    }

    static List<ReviewRow> ToRows(
        string board,
        List<MatchRecommendation> recs,
        Dictionary<long, ReviewScore> scores,
        Dictionary<long, (decimal? LastHome, decimal? LastDraw, decimal? LastAway, decimal? MaxHome, decimal? MaxAway)> exchange,
        List<RecommendationRecord> saved)
    {
        var qualified = recs.Where(RecommendationSelection.QualifiesForFeatured)
            .Select(ReviewKey)
            .ToHashSet(StringComparer.Ordinal);
        // 胜负彩页面列出全部受注场。复盘的「页面会发布」仍按 55%，不把 14 场都算成精选。
        var published = board == Boards.Sfc
            ? recs.Where(RecommendationSelection.QualifiesForFeatured).ToList()
            : RecommendationSelection.ForDisplay(board, recs);
        var publishedNos = published.Select(ReviewKey).ToHashSet(StringComparer.Ordinal);

        var rows = new List<ReviewRow>();
        foreach (var rec in recs
            .OrderBy(r => board == Boards.Sfc ? r.Expect : "")
            .ThenBy(r => RecommendationSelection.OfficialMatchOrder(r.MatchNo))
            .ThenBy(r => r.MatchNo))
        {
            ReviewScore? score = null;
            if (rec.MatchId is long id) scores.TryGetValue(id, out score);
            var finished = score?.Finished == true;
            var bd = board == Boards.Bd;
            char? actual = null;
            string? hcpActual = null;
            if (finished)
            {
                actual = Side(score!.HomeScore!.Value, score.AwayScore!.Value, 0);
                if (rec.Handicap is decimal hcp)
                    hcpActual = HcpName(Side(score.HomeScore.Value, score.AwayScore.Value, hcp));
            }

            var pick = bd ? rec.RecHandicap : rec.RecResult;
            var direction = pick is null
                ? "没有方向"
                : bd ? HcpName(pick) : SpfName(pick);
            decimal? prob = bd ? rec.HandicapProbability : rec.HitProbability;
            string verdict;
            if (!finished) verdict = "未完场";
            else if (pick is null) verdict = "没有方向";
            else if (bd && rec.Handicap is null) verdict = "没有让球数";
            else
            {
                var got = bd
                    ? Side(score!.HomeScore!.Value, score.AwayScore!.Value, rec.Handicap!.Value)
                    : actual;
                verdict = got == pick ? "命中" : "未中";
            }

            var hot = false;
            if (pick == '3' && rec.RatioHome is >= 60 and < 75) hot = true;
            if (pick == '0' && rec.RatioAway is >= 60 and < 75) hot = true;

            string exchangeText = "";
            if (rec.MatchId is long mid && exchange.TryGetValue(mid, out var ex))
            {
                exchangeText = $"终盘必发 主 {Pct(ex.LastHome)} / 平 {Pct(ex.LastDraw)} / 客 {Pct(ex.LastAway)}";
                if (ex.MaxHome is decimal mh && ex.LastHome is decimal lh && mh > lh)
                    exchangeText += $"；主胜采样最高 {Pct(mh)}";
                if (ex.MaxAway is decimal ma && ex.LastAway is decimal la && ma > la)
                    exchangeText += $"；客胜采样最高 {Pct(ma)}";
            }

            var kept = saved.FirstOrDefault(s =>
                string.Equals(s.Board, board, StringComparison.OrdinalIgnoreCase)
                && ((rec.MatchId is long rid && s.MatchId == rid) || s.MatchNo == rec.MatchNo));
            var savedText = kept is null
                ? "库里没有这场。保存只有四个钟点，中间改口不会留下。"
                : $"库里存的是 {SpfName(kept.RecResult)}，置信 {kept.Confidence?.ToString("0.#") ?? "—"}，{(kept.Hit is true ? "已标命中" : kept.Hit is false ? "已标未中" : "还没回填")}。";

            var tier = board == Boards.Bd ? LeagueTier.Of(rec.LeagueName) ?? "未分级" : "";
            rows.Add(new ReviewRow
            {
                Board = board,
                Tier = tier,
                MatchNo = board == Boards.Sfc && !string.IsNullOrWhiteSpace(rec.Expect)
                    ? $"{rec.Expect}-{rec.MatchNo}"
                    : rec.MatchNo,
                League = rec.LeagueName ?? "",
                Home = rec.HomeTeam,
                Away = rec.AwayTeam,
                Kickoff = rec.MatchTime,
                Finished = finished,
                ScoreText = finished ? $"{score!.HomeScore}:{score.AwayScore}" : "未完场",
                HalfText = score?.HalfHomeScore is int hh && score.HalfAwayScore is int ha ? $"{hh}:{ha}" : null,
                ActualText = finished ? SpfName(actual) : "",
                HandicapActualText = hcpActual,
                DirectionText = direction,
                Probability = prob,
                Level = rec.SignalLevel,
                Published = publishedNos.Contains(ReviewKey(rec)),
                Strong = rec.IsFeatured,
                Qualified = qualified.Contains(ReviewKey(rec)),
                SavedText = savedText,
                Signal = rec.SignalDesc ?? "",
                Tags = rec.Tags,
                ExchangeText = exchangeText,
                HotSide = hot,
                Verdict = verdict,
                HomeOddsDelta = rec.HomeOddsDelta,
                OuLine = board is Boards.Jc or Boards.Bd ? rec.TotalLine : null,
                OuSide = board is Boards.Jc or Boards.Bd ? rec.OuSide : null,
                OuProbability = board is Boards.Jc or Boards.Bd ? rec.OuProbability : null,
                OuVerdict = OuVerdictOf(board, rec, finished, score)
            });
        }
        return rows;
    }

    static string? OuVerdictOf(string board, MatchRecommendation rec, bool finished, ReviewScore? score)
    {
        if (board is not (Boards.Jc or Boards.Bd)) return null;
        if (rec.OuSide is null || rec.TotalLine is not decimal line) return null;
        if (!finished || score?.HomeScore is not int h || score.AwayScore is not int a) return "未完场";
        return TotalLineMath.Grade(h + a, line, rec.OuSide);
    }

    static string ReviewKey(MatchRecommendation rec) =>
        rec.Board == Boards.Sfc && !string.IsNullOrWhiteSpace(rec.Expect)
            ? $"{rec.Expect}-{rec.MatchNo}"
            : rec.MatchNo ?? "";

    readonly record struct TallyStats(int Total, int Done, int Aimed, int Hit, int Published, int PublishedHit);

    static TallyStats CountTally(IReadOnlyList<ReviewRow> rows)
    {
        var done = rows.Where(r => r.Finished).ToList();
        var aimed = done.Where(r => r.Verdict is "命中" or "未中").ToList();
        var published = done.Where(r => r.Published).ToList();
        return new TallyStats(
            rows.Count,
            done.Count,
            aimed.Count,
            aimed.Count(r => r.Verdict == "命中"),
            published.Count,
            published.Count(r => r.Verdict == "命中"));
    }

    static string RateText(int hit, int total) =>
        total == 0 ? "—" : $"{Math.Round(100.0 * hit / total, MidpointRounding.AwayFromZero):0}%";

    static void AppendTally(StringBuilder sb, IReadOnlyList<(string Name, IReadOnlyList<ReviewRow> Rows)> lines)
    {
        var items = lines
            .Select(x => (x.Name, Stats: CountTally(x.Rows)))
            .Where(x => x.Stats.Total > 0)
            .ToList();
        if (items.Count == 0)
        {
            sb.AppendLine("这天没有场次。");
            return;
        }

        var showDone = items.Any(x => x.Stats.Done != x.Stats.Total);
        sb.AppendLine(showDone
            ? "| 板块 | 场次 | 完场 | 有方向 | 命中 | 命中率 | 会发布 |"
            : "| 板块 | 场次 | 有方向 | 命中 | 命中率 | 会发布 |");
        sb.AppendLine(showDone
            ? "| --- | ---: | ---: | ---: | ---: | ---: | ---: |"
            : "| --- | ---: | ---: | ---: | ---: | ---: |");
        foreach (var (name, s) in items)
        {
            var pub = s.Published == 0 ? "—" : $"{s.Published} 中 {s.PublishedHit}";
            sb.AppendLine(showDone
                ? $"| {name} | {s.Total} | {s.Done} | {s.Aimed} | {s.Hit} | {RateText(s.Hit, s.Aimed)} | {pub} |"
                : $"| {name} | {s.Total} | {s.Aimed} | {s.Hit} | {RateText(s.Hit, s.Aimed)} | {pub} |");
        }
    }

    static void AppendNamed(StringBuilder sb, string title, IEnumerable<ReviewRow> rows)
    {
        var list = rows.ToList();
        sb.AppendLine($"- {title}：{(list.Count == 0 ? "无" : list.Count + " 场")}");
        foreach (var r in list)
            sb.AppendLine($"  - {r.MatchNo} {r.League} {r.Home} vs {r.Away} {r.ScoreText} 方向 {r.DirectionText} {Pct(r.Probability)}");
    }

    static void AppendSfc(StringBuilder sb, IReadOnlyList<ReviewRow> rows)
    {
        sb.AppendLine("## 胜负彩（全部场次）");
        sb.AppendLine();
        if (rows.Count == 0)
        {
            sb.AppendLine("这天没有场次。");
            sb.AppendLine();
            return;
        }

        // 没开完的在上，同一档里期号大的在上。开过的整期沉下去。
        foreach (var group in rows.GroupBy(IssueOf)
                     .OrderBy(g => g.All(r => r.Finished) ? 1 : 0)
                     .ThenByDescending(g => IssueNumber(g.Key)))
        {
            var list = group.OrderBy(MatchIndex).ToList();
            sb.AppendLine($"### {group.Key} {IssueBrief(list)}");
            sb.AppendLine();
            foreach (var r in list)
                AppendMatch(sb, r, 4);
        }
    }

    static int IssueNumber(string issue) => int.TryParse(issue, out var n) ? n : -1;

    static string IssueOf(ReviewRow row)
    {
        var no = row.MatchNo ?? "";
        var dash = no.IndexOf('-');
        if (dash > 0 && no[..dash].All(char.IsDigit)) return no[..dash];
        return "期号未知";
    }

    static int MatchIndex(ReviewRow row)
    {
        var no = row.MatchNo ?? "";
        var dash = no.IndexOf('-');
        var tail = dash >= 0 ? no[(dash + 1)..] : no;
        return RecommendationSelection.OfficialMatchOrder(tail);
    }

    static string IssueBrief(IReadOnlyList<ReviewRow> rows)
    {
        var done = rows.Where(r => r.Finished).ToList();
        var aimed = done.Where(r => r.Verdict is "命中" or "未中").ToList();
        var hit = aimed.Count(r => r.Verdict == "命中");
        var published = done.Where(r => r.Published).ToList();
        var publishedHit = published.Count(r => r.Verdict == "命中");
        return $"{rows.Count} 场，完场 {done.Count}，有方向 {aimed.Count} 中 {hit}，页面会发布 {published.Count} 中 {publishedHit}。";
    }

    static void AppendMilk(StringBuilder sb, string? body)
    {
        if (body is null) return;
        sb.AppendLine("## 冷门参考");
        sb.AppendLine();
        sb.AppendLine("最多 6 场，只对照，不购买。爆冷、高球、半全场（胜负、负胜、胜平、负平）、非常规比分打出来才算对照上，不是胜平负命中。");
        sb.AppendLine();
        sb.AppendLine(body.TrimEnd());
        sb.AppendLine();
    }

    static void AppendOu(StringBuilder sb, IReadOnlyList<ReviewRow> rows)
    {
        sb.AppendLine("## 大小球（只作参考）");
        sb.AppendLine();
        sb.AppendLine("不进精选，不购买。只写竞彩和北单上有四分之一盘的场。整数盘进球等于盘口是走盘。四分之一盘赢一半、输一半分开计。");
        sb.AppendLine();
        foreach (var (name, board) in new[] { ("竞彩", Boards.Jc), ("北单", Boards.Bd) })
        {
            var list = OuRows(rows, board);
            sb.AppendLine($"- {name} {OuBrief(list)}");
        }
        sb.AppendLine();
        foreach (var (name, board) in new[] { ("竞彩", Boards.Jc), ("北单", Boards.Bd) })
        {
            var list = OuRows(rows, board);
            if (list.Count == 0) continue;
            sb.AppendLine($"### {name}");
            sb.AppendLine();
            foreach (var r in list)
            {
                var prob = r.OuProbability is decimal p ? $"{p:0.0}%" : "—";
                var result = r.OuVerdict == "未完场"
                    ? "还没有完场比分"
                    : $"进球 {GoalsOf(r)}，{r.OuVerdict}";
                sb.AppendLine($"- {r.MatchNo} {r.League} {r.Home} vs {r.Away} 盘口 {r.OuLine:0.##} 看{r.OuSide} {prob}，{result}");
            }
            sb.AppendLine();
        }
    }

    static List<ReviewRow> OuRows(IReadOnlyList<ReviewRow> rows, string board)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<ReviewRow>();
        foreach (var r in rows
            .Where(r => r.Board == board && r.OuSide is not null && r.OuVerdict is not null)
            .OrderByDescending(r => r.OuProbability ?? 0)
            .ThenBy(r => RecommendationSelection.OfficialMatchOrder(r.MatchNo)))
        {
            var key = $"{r.Home}|{r.Away}";
            if (!seen.Add(key)) continue;
            list.Add(r);
        }
        return list;
    }

    static string OuBrief(IReadOnlyList<ReviewRow> list)
    {
        if (list.Count == 0) return "这天没有盘。";
        var done = list.Where(r => r.OuVerdict is "赢" or "赢半" or "走盘" or "输半" or "输").ToList();
        return $"{list.Count} 场，完场 {done.Count}，赢 {done.Count(r => r.OuVerdict == "赢")}，赢半 {done.Count(r => r.OuVerdict == "赢半")}，走盘 {done.Count(r => r.OuVerdict == "走盘")}，输半 {done.Count(r => r.OuVerdict == "输半")}，输 {done.Count(r => r.OuVerdict == "输")}。";
    }

    static int GoalsOf(ReviewRow r)
    {
        var parts = (r.ScoreText ?? "").Split(':');
        if (parts.Length == 2 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var a))
            return h + a;
        return 0;
    }

    static void AppendBoard(StringBuilder sb, string title, IReadOnlyList<ReviewRow> rows)
    {
        sb.AppendLine($"## {title}");
        sb.AppendLine();
        if (rows.Count == 0)
        {
            sb.AppendLine("这天没有场次。");
            sb.AppendLine();
            return;
        }

        foreach (var r in rows)
            AppendMatch(sb, r, 3);
    }

    static void AppendMatch(StringBuilder sb, ReviewRow r, int heading)
    {
        var when = r.Kickoff?.ToString("HH:mm") ?? "";
        sb.AppendLine($"{new string('#', heading)} {r.MatchNo} {r.League} {r.Home} vs {r.Away} {when}");
        sb.AppendLine();
        var half = r.HalfText is null ? "" : $"，半场 {r.HalfText}";
        var actual = r.Finished ? $"{r.ActualText}{half}" : "还没有完场比分";
        var hcp = r.HandicapActualText is null ? "" : $"，让球结果 {r.HandicapActualText}";
        sb.AppendLine($"- 赛果：{r.ScoreText}，{actual}{hcp}");
        var mark = r.Published
            ? (r.Strong ? "页面会发布，标了强推荐" : "页面会发布")
            : r.Qualified ? "够 55%，发布名额没排上" : "没进精选";
        var prob = r.Probability is decimal p ? $"，预计 {p:0.0}%" : "";
        var delta = r.HomeOddsDelta is decimal d ? $"，主胜水位变化 {d:+0.00;-0.00;0}" : "";
        sb.AppendLine($"- 终盘重算：{r.DirectionText}{prob}，信号 {r.Level}，{mark}{delta}");
        if (!string.IsNullOrWhiteSpace(r.Signal))
            sb.AppendLine($"- 依据：{r.Signal}");
        if (r.DirectionText == "没有方向" && (r.Tags & SignalTag.ExchangeExtremeHot) != 0)
            sb.AppendLine("- 终盘这一侧已经到 75%，方向被撤掉。撤掉之前若还在 75% 以下，当时可以是精选。空库不能当成没推荐过。");
        if (!string.IsNullOrWhiteSpace(r.ExchangeText))
            sb.AppendLine($"- {r.ExchangeText}");
        sb.AppendLine($"- {r.SavedText}");
        sb.AppendLine($"- 对照：{r.Verdict}");
        sb.AppendLine();
    }

    static char Side(int home, int away, decimal handicap)
    {
        var diff = home + handicap - away;
        if (diff > 0) return '3';
        if (diff == 0) return '1';
        return '0';
    }

    static string SpfName(char? side) => side switch
    {
        '3' => "主胜",
        '1' => "平局",
        '0' => "客胜",
        _ => "没有方向"
    };

    static string HcpName(char? side) => side switch
    {
        '3' => "让胜",
        '1' => "让平",
        '0' => "让负",
        _ => "没有方向"
    };

    static string Pct(decimal? value) => value is decimal v ? $"{v:0.0}%" : "—";
}

public sealed class ReviewRow
{
    public string Board { get; init; } = "";
    public string Tier { get; init; } = "";
    public string MatchNo { get; init; } = "";
    public string League { get; init; } = "";
    public string Home { get; init; } = "";
    public string Away { get; init; } = "";
    public DateTime? Kickoff { get; init; }
    public bool Finished { get; init; }
    public string ScoreText { get; init; } = "";
    public string? HalfText { get; init; }
    public string ActualText { get; init; } = "";
    public string? HandicapActualText { get; init; }
    public string DirectionText { get; init; } = "";
    public decimal? Probability { get; init; }
    public int Level { get; init; }
    public bool Published { get; init; }
    public bool Strong { get; init; }
    public bool Qualified { get; init; }
    public string? SavedText { get; init; }
    public string Signal { get; init; } = "";
    /// <summary>Signal 的结构化版本。复盘说明按位判断，不靠 Signal 里的字。</summary>
    public SignalTag Tags { get; init; } = SignalTag.None;
    public string ExchangeText { get; init; } = "";
    public bool HotSide { get; init; }
    public string Verdict { get; init; } = "";
    public decimal? HomeOddsDelta { get; init; }
    public decimal? OuLine { get; init; }
    public string? OuSide { get; init; }
    public decimal? OuProbability { get; init; }
    public string? OuVerdict { get; init; }
}
