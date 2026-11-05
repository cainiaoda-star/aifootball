using AiFootBall.Core.Models;
using AiFootBall.Data;

namespace AiFootBall.Sources;

public sealed class LottoRecommendationService
{
    private readonly LotteryRepository _lotto;
    private readonly LottoRecommendationRepository _recs;
    private readonly LottoUserTicketRepository _tickets;

    public LottoRecommendationService(LotteryRepository lotto, LottoRecommendationRepository recs,
        LottoUserTicketRepository tickets)
    {
        _lotto = lotto;
        _recs = recs;
        _tickets = tickets;
    }

    public async Task<List<LottoPlanSet>> GenerateAllAndSaveAsync(CancellationToken ct = default)
    {
        var result = new List<LottoPlanSet>();
        foreach (var code in LottoCodes.All)
        {
            ct.ThrowIfCancellationRequested();
            var plan = await GenerateOneAsync(code, ct);
            if (plan.Picks.Count > 0)
                await _recs.ReplacePlanAsync(plan, ct);
            result.Add(plan);
        }
        return result;
    }

    public async Task<LottoPlanSet> GenerateOneAsync(string code, CancellationToken ct = default)
    {
        var lt = await _lotto.GetLotteryTypeAsync(code, ct)
                 ?? throw new InvalidOperationException($"未知彩种 {code}");
        var draws = await _lotto.GetDrawsAsync(code, 300, ct: ct);
        return LottoRecommendEngine.Recommend(lt, draws);
    }

    public async Task<object> GetLatestAsync(CancellationToken ct = default)
    {
        var rows = await _recs.GetLatestAllAsync(ct);
        var boards = new List<object>();
        foreach (var code in LottoCodes.All)
        {
            ct.ThrowIfCancellationRequested();
            var draw = await LatestDrawDtoAsync(code, ct);
            if (rows.Count == 0)
            {
                var plan = await GenerateOneAsync(code, ct);
                boards.Add(ToDto(plan, draw));
            }
            else
            {
                var list = rows.Where(r => r.LotteryCode.Equals(code, StringComparison.OrdinalIgnoreCase)).ToList();
                if (list.Count == 0)
                {
                    var plan = await GenerateOneAsync(code, ct);
                    boards.Add(ToDto(plan, draw));
                }
                else
                {
                    boards.Add(await ToDtoFromRecordsAsync(code, list, draw, ct));
                }
            }
        }

        return new
        {
            date = DateTime.Today.ToString("yyyy-MM-dd"),
            source = rows.Count == 0 ? "live" : "saved",
            boards
        };
    }

    public async Task<object> ReviewAsync(string code, CancellationToken ct = default)
    {
        var rows = await _recs.GetRecentIssuesAsync(code, 20, ct);
        var draws = await _lotto.GetDrawsAsync(code, 160, ct: ct);
        var byIssue = new Dictionary<string, LotteryDrawRecord>(StringComparer.Ordinal);
        foreach (var d in draws)
            byIssue.TryAdd(d.Issue, d);

        var issues = new List<object>();
        foreach (var group in rows.GroupBy(r => r.TargetIssue))
        {
            byIssue.TryGetValue(group.Key, out var draw);
            issues.Add(ReviewIssue(code, group.Key, group.OrderBy(r => r.RankNo).ToList(), draw));
        }

        return new
        {
            lotteryCode = code,
            lotteryName = LottoCodes.DisplayName(code),
            drawn = rows.Select(r => r.TargetIssue).Distinct()
                .Count(issue => byIssue.ContainsKey(issue)),
            issues
        };
    }

    public async Task<object> GetByCodeAsync(string code, CancellationToken ct = default)
    {
        var draw = await LatestDrawDtoAsync(code, ct);
        var rows = await _recs.GetByCodeAsync(code, null, ct);
        if (rows.Count == 0)
        {
            var plan = await GenerateOneAsync(code, ct);
            return ToDto(plan, draw);
        }
        return await ToDtoFromRecordsAsync(code, rows, draw, ct);
    }

    /// <summary>保存用户选号票（选号器生成的或手输的）。期号缺省取最新开奖 +1。</summary>
    public async Task<object> SaveUserTicketsAsync(string code, List<LottoUserTicketIn> tickets,
        string? targetIssue, CancellationToken ct = default)
    {
        if (!LottoCodes.All.Contains(code, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"未知彩种 {code}");
        if (tickets.Count == 0 || tickets.Count > 100)
            throw new InvalidOperationException("一次保存 1~100 注");
        foreach (var t in tickets)
        {
            if (t.Reds.Count == 0) throw new InvalidOperationException("有注没有主号码");
            if (LottoCodes.IsKl8(code))
                t.PlayType = "选十"; // Mark() 靠这个词走选十判级
        }

        var issue = targetIssue?.Trim();
        if (string.IsNullOrEmpty(issue))
        {
            var last = (await _lotto.GetDrawsAsync(code, 1, ct: ct)).FirstOrDefault();
            issue = last is null
                ? DateTime.Today.ToString("yyyyMMdd")
                : LottoRecommendEngine.NextIssue(last.Issue);
        }

        var n = await _tickets.SaveAsync(code, issue, DateOnly.FromDateTime(DateTime.Now), tickets, ct);
        return new { saved = n, targetIssue = issue };
    }

    /// <summary>用户票列表 + 按期对奖。没开奖的期返回 settled=false。</summary>
    public async Task<object> GetUserTicketsAsync(string code, int days, CancellationToken ct = default)
    {
        var rows = await _tickets.GetRecentAsync(code, Math.Clamp(days, 1, 120), ct);
        var draws = await _lotto.GetDrawsAsync(code, 200, ct: ct);
        var byIssue = new Dictionary<string, LotteryDrawRecord>(StringComparer.Ordinal);
        foreach (var d in draws)
            byIssue.TryAdd(d.Issue, d);

        var issues = new List<object>();
        foreach (var group in rows.GroupBy(r => r.TargetIssue).OrderByDescending(g => g.Key))
        {
            byIssue.TryGetValue(group.Key, out var draw);
            var picks = new List<object>();
            foreach (var r in group)
            {
                var reds = ParseCsv(r.RedNumbers);
                var blues = ParseCsv(r.BlueNumbers);
                var grade = draw is null ? null : LottoHitMath.Mark(r.PlayType, reds, blues, draw.Reds, draw.Blues, code);
                picks.Add(new
                {
                    r.TicketId,
                    r.PlayType,
                    reds = BallObjects(reds, grade?.RedHit),
                    blues = BallObjects(blues, grade?.BlueHit),
                    note = grade?.Note ?? "还没开奖",
                    r.CoolScore, r.LevelName, r.Source, r.BetAmount
                });
            }

            object? drawn = null;
            if (draw is not null)
            {
                var reds = LottoHitMath.OfficialDigits(code, draw.Reds);
                var blues = LottoCodes.IsDigitGame(code) ? new List<int>() : draw.Blues.ToList();
                drawn = new
                {
                    issue = draw.Issue,
                    drawDate = draw.DrawDate.ToString("yyyy-MM-dd"),
                    reds,
                    blues
                };
            }

            issues.Add(new
            {
                issue = group.Key,
                settled = draw is not null,
                draw = drawn,
                tickets = picks,
                total = picks.Count,
                betTotal = group.Sum(r => r.BetAmount)
            });
        }

        return new
        {
            lotteryCode = code,
            lotteryName = LottoCodes.DisplayName(code),
            issues
        };
    }

    private static List<object> BallObjects(List<int> nums, bool[]? hits) =>
        nums.Select((n, i) => (object)new { n, hit = hits is not null && i < hits.Length && hits[i] }).ToList();

    private async Task<object?> LatestDrawDtoAsync(string code, CancellationToken ct)
    {
        var draws = await _lotto.GetDrawsAsync(code, 1, ct: ct);
        var d = draws.FirstOrDefault();
        if (d is null) return null;
        var reds = LottoHitMath.OfficialDigits(code, d.Reds);
        var blues = LottoCodes.IsDigitGame(code) ? new List<int>() : d.Blues.ToList();
        return new
        {
            issue = d.Issue,
            drawDate = d.DrawDate.ToString("yyyy-MM-dd"),
            places = DigitPlaces(code, reds.Count),
            reds,
            blues,
            redText = FormatReds(reds),
            blueText = blues.Count == 0 ? "" : string.Join(" ", blues.Select(b => b.ToString("D2")))
        };
    }

    private static object ToDto(LottoPlanSet plan, object? lastDraw) => new
    {
        lotteryCode = plan.LotteryCode,
        lotteryName = plan.LotteryName,
        targetIssue = plan.TargetIssue,
        basedOnIssue = plan.BasedOnIssue,
        recDate = plan.RecDate.ToString("yyyy-MM-dd"),
        summary = plan.Summary,
        lastDraw,
        picks = plan.Picks.Select(PickDto)
    };

    private async Task<object> ToDtoFromRecordsAsync(string code, List<LottoRecommendationRecord> rows, object? lastDraw, CancellationToken ct)
    {
        var first = rows.FirstOrDefault();
        var picks = new List<object>();
        foreach (var r in rows)
        {
            var reds = ParseCsv(r.RedNumbers);
            var blues = ParseCsv(r.BlueNumbers);
            var tuo = r.PlayType == "四胆全拖"
                ? Enumerable.Range(1, 35).Where(n => !reds.Contains(n)).ToList()
                : new List<int>();
            picks.Add(new
            {
                r.RankNo, r.IsPrimary, r.PlayType,
                reds, blues,
                tuoReds = tuo,
                betCount = r.PlayType == "四胆全拖" ? tuo.Count : (int?)null,
                redText = FormatReds(reds),
                blueText = blues.Count == 0 ? "" : string.Join(" ", blues.Select(b => b.ToString("D2"))),
                score = r.Score,
                signalDesc = r.SignalDesc
            });
        }

        var summary = first is null ? "暂无方案" : $"第 {first.TargetIssue} 期参考选号（主推 + 备选）";
        if (LottoCodes.IsDlt(code) && rows.All(r => r.PlayType != "四胆全拖"))
        {
            var extra = await BuildDltDanTuoAsync(ct);
            if (extra is not null)
            {
                extra.RankNo = picks.Count + 1;
                picks.Add(PickDto(extra));
                summary += $"，另附四胆全拖 {extra.BetCount} 注";
            }
        }

        return new
        {
            lotteryCode = code,
            lotteryName = LottoCodes.DisplayName(code),
            targetIssue = first?.TargetIssue ?? "",
            basedOnIssue = first?.BasedOnIssue,
            recDate = first?.RecDate.ToString("yyyy-MM-dd"),
            summary,
            lastDraw,
            picks
        };
    }

    private async Task<LottoPick?> BuildDltDanTuoAsync(CancellationToken ct)
    {
        var lt = await _lotto.GetLotteryTypeAsync(LottoCodes.Dlt, ct);
        if (lt is null) return null;
        var draws = await _lotto.GetDrawsAsync(LottoCodes.Dlt, 300, ct: ct);
        return LottoRecommendEngine.DltFourDanFullTuo(lt, draws);
    }

    private static object ReviewIssue(string code, string issue, List<LottoRecommendationRecord> rows, LotteryDrawRecord? draw)
    {
        var grades = new List<LottoHitMath.Grade>();
        var picks = new List<object>();
        foreach (var r in rows)
        {
            var reds = ParseCsv(r.RedNumbers);
            var blues = ParseCsv(r.BlueNumbers);
            var grade = draw is null
                ? null
                : LottoHitMath.Mark(r.PlayType, reds, blues, draw.Reds, draw.Blues, code);
            if (grade is not null) grades.Add(grade);
            picks.Add(new
            {
                r.RankNo, r.IsPrimary, r.PlayType,
                reds = Balls(reds, grade?.RedHit),
                blues = Balls(blues, grade?.BlueHit),
                note = grade?.Note ?? "还没开奖",
                redHits = grade?.RedHits,
                blueHits = grade?.BlueHits,
                score = r.Score
            });
        }

        object? drawn = null;
        if (draw is not null)
        {
            var reds = LottoHitMath.OfficialDigits(code, draw.Reds);
            var blues = LottoCodes.IsDigitGame(code) ? new List<int>() : draw.Blues.ToList();
            var redHit = OrFlags(reds.Count, grades.Select(g => g.DrawRedHit));
            var blueHit = OrFlags(blues.Count, grades.Select(g => g.DrawBlueHit));
            drawn = new
            {
                issue = draw.Issue,
                drawDate = draw.DrawDate.ToString("yyyy-MM-dd"),
                places = DigitPlaces(code, reds.Count),
                reds = Balls(reds, redHit),
                blues = Balls(blues, blueHit)
            };
        }

        return new
        {
            issue,
            settled = draw is not null,
            draw = drawn,
            picks
        };
    }

    private static string[]? DigitPlaces(string code, int count)
    {
        if (!LottoCodes.IsDigitGame(code)) return null;
        var names = count >= 5
            ? new[] { "万", "千", "百", "十", "个" }
            : new[] { "百", "十", "个" };
        return names.Take(count).ToArray();
    }

    private static bool[] OrFlags(int length, IEnumerable<bool[]> flags)
    {
        var hit = new bool[length];
        foreach (var row in flags)
        {
            for (var i = 0; i < length && i < row.Length; i++)
                if (row[i]) hit[i] = true;
        }
        return hit;
    }

    private static List<object> Balls(List<int> nums, bool[]? hits) =>
        nums.Select((n, i) => (object)new { n, hit = hits is not null && i < hits.Length && hits[i] }).ToList();

    private static object PickDto(LottoPick p) => new
    {
        p.RankNo, p.IsPrimary, p.PlayType,
        reds = p.Reds, blues = p.Blues,
        tuoReds = p.TuoReds,
        betCount = p.BetCount,
        redText = FormatReds(p.Reds),
        blueText = p.Blues.Count == 0 ? "" : string.Join(" ", p.Blues.Select(b => b.ToString("D2"))),
        p.Score, p.SignalDesc
    };

    private static List<int> ParseCsv(string? csv) =>
        string.IsNullOrWhiteSpace(csv) ? new()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.Parse(s.Trim())).ToList();

    private static string FormatReds(List<int> reds)
    {
        if (reds.Count == 0) return "";
        var pad = reds.Any(x => x >= 10);
        return string.Join(" ", reds.Select(n => pad ? n.ToString("D2") : n.ToString()));
    }
}
