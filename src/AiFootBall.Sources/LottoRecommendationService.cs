using AiFootBall.Core.Models;
using AiFootBall.Data;

namespace AiFootBall.Sources;

public sealed class LottoRecommendationService
{
    private readonly LotteryRepository _lotto;
    private readonly LottoRecommendationRepository _recs;

    public LottoRecommendationService(LotteryRepository lotto, LottoRecommendationRepository recs)
    {
        _lotto = lotto;
        _recs = recs;
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
                boards.Add(ToDtoFromRecords(code, list, draw));
            }
        }

        return new
        {
            date = DateTime.Today.ToString("yyyy-MM-dd"),
            source = rows.Count == 0 ? "live" : "saved",
            boards
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
        return ToDtoFromRecords(code, rows, draw);
    }

    private async Task<object?> LatestDrawDtoAsync(string code, CancellationToken ct)
    {
        var draws = await _lotto.GetDrawsAsync(code, 1, ct: ct);
        var d = draws.FirstOrDefault();
        if (d is null) return null;
        return new
        {
            issue = d.Issue,
            drawDate = d.DrawDate.ToString("yyyy-MM-dd"),
            reds = d.Reds,
            blues = d.Blues,
            redText = FormatReds(d.Reds.ToList()),
            blueText = d.Blues.Count == 0 ? "" : string.Join(" ", d.Blues.Select(b => b.ToString("D2")))
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
        picks = plan.Picks.Select(p => new
        {
            p.RankNo, p.IsPrimary, p.PlayType,
            reds = p.Reds, blues = p.Blues,
            redText = FormatReds(p.Reds),
            blueText = p.Blues.Count == 0 ? "" : string.Join(" ", p.Blues.Select(b => b.ToString("D2"))),
            p.Score, p.SignalDesc
        })
    };

    private static object ToDtoFromRecords(string code, List<LottoRecommendationRecord> rows, object? lastDraw)
    {
        var first = rows.FirstOrDefault();
        return new
        {
            lotteryCode = code,
            lotteryName = LottoCodes.DisplayName(code),
            targetIssue = first?.TargetIssue ?? "",
            basedOnIssue = first?.BasedOnIssue,
            recDate = first?.RecDate.ToString("yyyy-MM-dd"),
            summary = first is null ? "暂无方案" : $"第 {first.TargetIssue} 期参考选号（主推 + 备选）",
            lastDraw,
            picks = rows.Select(r =>
            {
                var reds = ParseCsv(r.RedNumbers);
                var blues = ParseCsv(r.BlueNumbers);
                return new
                {
                    r.RankNo, r.IsPrimary, r.PlayType,
                    reds, blues,
                    redText = FormatReds(reds),
                    blueText = blues.Count == 0 ? "" : string.Join(" ", blues.Select(b => b.ToString("D2"))),
                    score = r.Score,
                    signalDesc = r.SignalDesc
                };
            })
        };
    }

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
