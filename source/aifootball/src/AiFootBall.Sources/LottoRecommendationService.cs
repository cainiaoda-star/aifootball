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

    public async Task<LottoGenerateSaveResult> GenerateAllAndSaveAsync(CancellationToken ct = default)
    {
        var plans = new List<LottoPlanSet>();
        var savedCodes = new List<string>();
        foreach (var code in LottoCodes.All)
        {
            ct.ThrowIfCancellationRequested();
            var plan = await GenerateOneAsync(code, ct);
            plans.Add(plan);
            if (plan.Picks.Count > 0 && plan.IssueBound)
            {
                await _recs.ReplacePlanAsync(plan, ct);
                savedCodes.Add(plan.LotteryCode);
            }
        }

        return LottoBoardPresentation.BuildSaveResult(plans, savedCodes);
    }

    public async Task<LottoPlanSet> GenerateOneAsync(string code, CancellationToken ct = default)
    {
        code = LottoCodes.Normalize(code);
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
            var list = rows.Where(r => r.LotteryCode.Equals(code, StringComparison.OrdinalIgnoreCase)).ToList();
            if (list.Count == 0)
            {
                var plan = await GenerateOneAsync(code, ct);
                boards.Add(ToDto(plan, draw, fromSaved: false));
            }
            else
            {
                boards.Add(ToDtoFromRecords(code, list, draw));
            }
        }

        return new
        {
            // 响应生成时刻，不是「这些板都是今天的当期推荐」
            responseAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            source = rows.Count == 0 ? "live-preview" : "saved-historical",
            boards
        };
    }

    public async Task<object> ReviewAsync(string code, CancellationToken ct = default)
    {
        code = LottoCodes.Normalize(code);
        var rows = await _recs.GetRecentIssuesAsync(code, 20, ct);
        var draws = await _lotto.GetDrawsAsync(code, 160, ct: ct);
        var byIssue = new Dictionary<string, LotteryDrawRecord>(StringComparer.Ordinal);
        foreach (var d in draws)
            byIssue.TryAdd(d.Issue, d);

        var issues = new List<object>();
        foreach (var group in rows.GroupBy(r => r.TargetIssue))
        {
            if (string.IsNullOrWhiteSpace(group.Key)) continue;
            byIssue.TryGetValue(group.Key, out var draw);
            issues.Add(ReviewIssue(code, group.Key, group.OrderBy(r => r.RankNo).ToList(), draw));
        }

        return new
        {
            lotteryCode = code,
            lotteryName = LottoCodes.DisplayName(code),
            drawn = rows.Select(r => r.TargetIssue).Distinct()
                .Count(issue => !string.IsNullOrWhiteSpace(issue) && byIssue.ContainsKey(issue)),
            issues
        };
    }

    public async Task<object> GetByCodeAsync(string code, CancellationToken ct = default)
    {
        code = LottoCodes.Normalize(code);
        var draw = await LatestDrawDtoAsync(code, ct);
        var rows = await _recs.GetByCodeAsync(code, null, ct);
        if (rows.Count == 0)
        {
            var plan = await GenerateOneAsync(code, ct);
            return ToDto(plan, draw, fromSaved: false);
        }
        return ToDtoFromRecords(code, rows, draw);
    }

    /// <summary>保存用户选号票。必须显式填写可校验的目标期号；禁止自动 +1 猜测。</summary>
    public async Task<object> SaveUserTicketsAsync(string code, List<LottoUserTicketIn> tickets,
        string? targetIssue, CancellationToken ct = default)
    {
        code = LottoCodes.Normalize(code);
        if (!LottoCodes.All.Contains(code, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"未知彩种 {code}");
        if (tickets.Count == 0 || tickets.Count > 100)
            throw new InvalidOperationException("一次保存 1~100 注");
        if (!LottoIssue.TryNormalizeUserIssue(targetIssue, out var issue, out var issueError))
            throw new InvalidOperationException(issueError);
        foreach (var t in tickets)
        {
            // 先按彩种规范化（仅空白→默认），再校验；快乐8 默认「选十」，其它未知玩法不放行。
            t.PlayType = LottoBetValidator.NormalizePlayForLottery(code, t.PlayType);
            if (!LottoBetValidator.TryValidate(code, t.PlayType, t.Reds, t.Blues, t.TuoReds,
                    out var normalizedPlay, out var betErr))
                throw new InvalidOperationException(betErr);
            t.PlayType = normalizedPlay;
            // 用户票本轮仅单注；金额必须为单注 2 元，禁止用固定 2 元冒充复式/胆拖多注。
            if (t.BetAmount <= 0) t.BetAmount = 2m;
            if (t.BetAmount != 2m)
                throw new InvalidOperationException(
                    "本轮用户票仅支持单注 2 元；复式/胆拖多注金额无法可靠保存，请拆成单式后再存。");
        }

        var n = await _tickets.SaveAsync(code, issue, DateOnly.FromDateTime(DateTime.Now), tickets, ct);
        return new { saved = n, targetIssue = issue };
    }

    public async Task<object> GetUserTicketsAsync(string code, int days, CancellationToken ct = default)
    {
        code = LottoCodes.Normalize(code);
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
                var grade = draw is null
                    ? LottoHitMath.Pending(reds.Count, blues.Count)
                    : LottoHitMath.Mark(r.PlayType, reds, blues, draw.Reds, draw.Blues, code);
                picks.Add(new
                {
                    r.TicketId,
                    r.PlayType,
                    reds = BallObjects(reds, grade.HasMatchDetail ? grade.RedHit : null),
                    blues = BallObjects(blues, grade.HasMatchDetail ? grade.BlueHit : null),
                    note = grade.Note,
                    status = LottoHitStatusRules.ToApi(grade.Status),
                    countsInHitRate = LottoHitStatusRules.CountsInHitRate(grade.Status),
                    showAsWin = LottoHitStatusRules.ShowAsWin(grade.Status),
                    showMatchMarks = LottoHitStatusRules.ShowMatchMarks(grade.Status),
                    determined = grade.Determined,
                    // 结构化命中数：与 prize_undetermined 一并返回，勿只从 note 解析
                    redHits = grade.HasMatchDetail ? grade.RedHits : (int?)null,
                    blueHits = grade.HasMatchDetail ? grade.BlueHits : (int?)null,
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
                    places = DigitPlaces(code, reds.Count),
                    reds,
                    blues
                };
            }

            issues.Add(new
            {
                issue = group.Key,
                settled = draw is not null,
                betTotal = group.Sum(x => x.BetAmount),
                tickets = picks,
                draw = drawn
            });
        }

        return new
        {
            lotteryCode = code,
            lotteryName = LottoCodes.DisplayName(code),
            issues
        };
    }

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

    private static object ToDto(LottoPlanSet plan, object? lastDraw, bool fromSaved)
    {
        var kind = LottoBoardPresentation.BoardKind(plan.IssueBound, fromSaved);
        var generatedAt = plan.RecDate.ToDateTime(TimeOnly.MinValue);
        return new
        {
            lotteryCode = plan.LotteryCode,
            lotteryName = plan.LotteryName,
            targetIssue = plan.IssueBound ? plan.TargetIssue : null,
            issueBound = plan.IssueBound,
            preview = kind == LottoBoardPresentation.KindPreview,
            boardKind = kind,
            boardStatusLabel = LottoBoardPresentation.StatusLabel(kind, plan.TargetIssue, generatedAt),
            basedOnIssue = plan.BasedOnIssue,
            recDate = plan.RecDate.ToString("yyyy-MM-dd"),
            generatedAt = generatedAt.ToString("yyyy-MM-dd"),
            summary = plan.Summary,
            lastDraw,
            picks = plan.Picks.Select(PickDto)
        };
    }

    private static object ToDtoFromRecords(string code, List<LottoRecommendationRecord> rows, object? lastDraw)
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

        var bound = !string.IsNullOrWhiteSpace(first?.TargetIssue);
        var generatedAt = first?.CreatedAt != default && first?.CreatedAt != null
            ? first.CreatedAt
            : first?.RecDate ?? DateTime.MinValue;
        var kind = LottoBoardPresentation.BoardKind(bound, fromSavedRecords: true);
        var summary = first is null
            ? "暂无方案"
            : LottoBoardPresentation.StatusLabel(kind, first.TargetIssue, generatedAt);

        return new
        {
            lotteryCode = code,
            lotteryName = LottoCodes.DisplayName(code),
            targetIssue = bound ? first!.TargetIssue : null,
            issueBound = bound,
            preview = false,
            boardKind = kind,
            boardStatusLabel = summary,
            basedOnIssue = first?.BasedOnIssue,
            recDate = first?.RecDate.ToString("yyyy-MM-dd"),
            generatedAt = generatedAt == DateTime.MinValue ? null : generatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            summary,
            lastDraw,
            picks
        };
    }

    /// <summary>复盘单期（可供边界测试直接调用）。</summary>
    public static object ReviewIssue(string code, string issue, List<LottoRecommendationRecord> rows, LotteryDrawRecord? draw)
    {
        code = LottoCodes.Normalize(code);
        var grades = new List<LottoHitMath.Grade>();
        var picks = new List<object>();
        foreach (var r in rows)
        {
            var reds = ParseCsv(r.RedNumbers);
            var blues = ParseCsv(r.BlueNumbers);
            var grade = draw is null
                ? LottoHitMath.Pending(reds.Count, blues.Count)
                : LottoHitMath.Mark(r.PlayType, reds, blues, draw.Reds, draw.Blues, code);
            if (grade.HasMatchDetail) grades.Add(grade);
            picks.Add(new
            {
                r.RankNo, r.IsPrimary, r.PlayType,
                reds = Balls(reds, grade.HasMatchDetail ? grade.RedHit : null),
                blues = Balls(blues, grade.HasMatchDetail ? grade.BlueHit : null),
                note = grade.Note,
                status = LottoHitStatusRules.ToApi(grade.Status),
                countsInHitRate = LottoHitStatusRules.CountsInHitRate(grade.Status),
                showAsWin = LottoHitStatusRules.ShowAsWin(grade.Status),
                showMatchMarks = LottoHitStatusRules.ShowMatchMarks(grade.Status),
                determined = grade.Determined,
                redHits = grade.HasMatchDetail ? grade.RedHits : (int?)null,
                blueHits = grade.HasMatchDetail ? grade.BlueHits : (int?)null,
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
        if (count >= 7)
            return Enumerable.Range(1, count).Select(i => $"第{i}位").ToArray();
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

    private static List<object> BallObjects(List<int> nums, bool[]? hits) => Balls(nums, hits);

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
