using AiFootBall.Core.Models;
using AiFootBall.Data;

namespace AiFootBall.Sources;

/// <summary>
/// 推荐编排：采集官方受注场次 → 关联 fid → 计算赔率/必发信号 → 生成推荐。
/// </summary>
public sealed class RecommendationService
{
    private readonly BettingCardSource _cardSource;
    private readonly BettingRepository _repo;
    private readonly JcOddsRepository _jcOdds;
    private readonly HitProbabilityService? _hit;

    public RecommendationService(BettingCardSource cardSource, BettingRepository repo, JcOddsRepository jcOdds,
        HitProbabilityService? hit = null)
    {
        _cardSource = cardSource;
        _repo = repo;
        _jcOdds = jcOdds;
        _hit = hit;
    }

    /// <summary>采样竞彩四类玩法固定奖金，写入 FbJcOddsHistory 时间序列。</summary>
    public async Task<CollectionResult> SampleJcOddsAsync(CancellationToken ct = default)
    {
        var cards = await _cardSource.FetchJcAllPlaysAsync(ct);
        if (cards.Count == 0) return CollectionResult.Ok(0, "竞彩无受注场次");

        var withBf = cards.Count(c => c.BfOdds.Count > 0);
        var withJq = cards.Count(c => c.JqOdds.Count > 0);
        var withBq = cards.Count(c => c.BqOdds.Count > 0);

        var snapshots = BettingCardSource.ToSnapshots(cards);
        var now = DateTime.Now;
        var sampleAt = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);
        var rows = await _jcOdds.SaveSnapshotAsync(snapshots, sampleAt, ct);

        return CollectionResult.Ok(rows,
            $"竞彩固定奖金采样 {cards.Count} 场（比分 {withBf} / 总进球 {withJq} / 半全场 {withBq}），写入 {rows} 个选项");
    }

    /// <summary>采集三个板块的受注场次并入库，返回各板块场次数</summary>
    public async Task<(int Jc, int Bd, int Sfc)> CollectCardsAsync(CancellationToken ct = default)
    {
        var jc = await _cardSource.FetchJcAsync(ct);
        var bd = await _cardSource.FetchBdAsync(ct);
        var sfc = await _cardSource.FetchSfcAsync(ct);

        await _repo.UpsertCardsAsync(jc.Concat(bd).Concat(sfc), ct);
        return (jc.Count, bd.Count, sfc.Count);
    }

    /// <summary>为某板块生成今日推荐</summary>
    public async Task<List<MatchRecommendation>> RecommendAsync(string board, string? expect = null, CancellationToken ct = default)
    {
        var cards = await _repo.GetCardsAsync(board, expect, ct);
        if (cards.Count == 0) return new();

        var fids = cards.Where(c => c.MatchId is not null).Select(c => c.MatchId!.Value).ToList();
        var oddsDelta = fids.Count > 0 ? await _repo.GetEuOddsDeltaAsync(fids, ct) : new();
        var exRatio = fids.Count > 0 ? await _repo.GetExchangeRatioAsync(fids, ct) : new();
        var totalLines = fids.Count > 0 ? await _repo.GetTotalLineAsync(fids, ct) : new();
        var closingEu = fids.Count > 0 ? await _repo.GetClosingEuAsync(fids, ct) : new();
        var asian = fids.Count > 0 ? await _repo.GetAsianMoveAsync(fids, ct) : new();
        var lateSteam = fids.Count > 0 ? await _repo.GetLateHomeSteamAsync(fids, ct) : new();
        var teamForm = fids.Count > 0 ? await _repo.GetTeamFormPointsAsync(fids, ct) : new();
        var hitSnap = HitProbabilityService.Snapshot.Empty;
        if (_hit is not null)
        {
            try { hitSnap = await _hit.GetSnapshotAsync(ct); }
            catch (OperationCanceledException) { throw; }
            catch { /* 模型拟合失败时仍给出推荐，只是没有预计命中率 */ }
        }

        // 胜负彩等板块 MatchTime 可能缺失（页面未直接给），从 FbMatch 关联补齐
        var needTime = cards.Where(c => c.MatchTime is null && c.MatchId is not null)
            .Select(c => c.MatchId!.Value).Distinct().ToList();
        var matchTimes = needTime.Count > 0 ? await _repo.GetMatchTimesAsync(needTime, ct) : new();

        var now = DateTime.Now;
        var result = new List<MatchRecommendation>();
        foreach (var c in cards)
        {
            // 已开赛（比赛时间已过）的场次不再参与推荐
            if (c.MatchTime is null && c.MatchId is not null && matchTimes.TryGetValue(c.MatchId.Value, out var mt))
                c.MatchTime = mt;
            if (c.MatchTime is not null && c.MatchTime.Value <= now)
                continue;
            if (string.Equals(board, Boards.Jc, StringComparison.OrdinalIgnoreCase)
                && c.MatchTime is DateTime kick
                && !RecommendationSelection.JcSaleHours.CanStillBuy(RecommendationSelection.JcSaleHours.Now(), kick))
                continue;

            decimal? delta = null, drawDelta = null;
            decimal? rh = null, rd = null, ra = null;
            decimal? line = null;
            var spfHome = c.SpfHome;
            var spfDraw = c.SpfDraw;
            var spfAway = c.SpfAway;
            if (c.MatchId is not null)
            {
                if (oddsDelta.TryGetValue(c.MatchId.Value, out var od))
                {
                    delta = od.Home;
                    drawDelta = od.Draw;
                }
                if (exRatio.TryGetValue(c.MatchId.Value, out var ex))
                {
                    rh = ex.Home;
                    rd = ex.Draw;
                    ra = ex.Away;
                }
                totalLines.TryGetValue(c.MatchId.Value, out line);
                if (closingEu.TryGetValue(c.MatchId.Value, out var eu))
                {
                    spfHome ??= eu.Home;
                    spfDraw ??= eu.Draw;
                    spfAway ??= eu.Away;
                }
            }

            (decimal? Open, decimal? Close) ah = default;
            if (c.MatchId is not null) asian.TryGetValue(c.MatchId.Value, out ah);
            decimal? lateDelta = null;
            int? homePts = null, awayPts = null;
            if (c.MatchId is not null)
            {
                if (lateSteam.TryGetValue(c.MatchId.Value, out var ld)) lateDelta = ld;
                if (teamForm.TryGetValue(c.MatchId.Value, out var form))
                {
                    homePts = form.HomePts;
                    awayPts = form.AwayPts;
                }
            }

            var rec = RecommendationEngine.Recommend(
                c.Board, c.Expect, c.MatchNo, c.MatchId, c.LeagueName,
                c.HomeTeam ?? "", c.AwayTeam ?? "", c.MatchTime, c.Handicap,
                delta, rh, ra, line,
                spfHome, spfDraw, spfAway,
                drawDelta, rd,
                ah.Open, ah.Close,
                lateDelta, homePts, awayPts);

            var changed = HitProbabilityService.Stamp(rec, hitSnap, spfHome, spfDraw, spfAway, ah.Open, ah.Close);
            if (changed)
            {
                RecommendationEngine.ReapplyGuards(
                    rec, delta, ah.Open, ah.Close, lateDelta, homePts, awayPts,
                    spfHome, spfDraw, spfAway);
            }
            RecommendationEngine.ApplyScoreline(rec, hitSnap.Scores);

            result.Add(rec);
        }

        return result;
    }

    /// <summary>生成全板块推荐并落库（复盘用）。竞彩最多 8 / 北单最多 11，不够不凑；胜负彩保留全部。</summary>
    public async Task<Dictionary<string, List<MatchRecommendation>>> RecommendAllAndSaveAsync(CancellationToken ct = default)
    {
        var all = new Dictionary<string, List<MatchRecommendation>>();
        var toSave = new List<RecommendationRecord>();

        foreach (var board in new[] { Boards.Jc, Boards.Bd, Boards.Sfc })
        {
            var recs = await RecommendAsync(board, null, ct);
            var strong = RecommendationSelection.ForDisplay(board, recs);
            all[board] = strong;

            // 先清当日该板块旧精选，再写入；否则被策略踢出的场次会一直留在复盘表里
            await _repo.DeleteRecommendationsByBoardDateAsync(board, DateTime.Today, ct);

            foreach (var r in strong)
            {
                var isCover = r.RecResult is null;
                var signalDesc = !isCover
                    ? r.SignalDesc
                    : (string.IsNullOrWhiteSpace(r.SignalDesc)
                        ? "无赔率与必发数据，建议全包（胜/平/负皆选）"
                        : r.SignalDesc + "，建议全包");

                toSave.Add(new RecommendationRecord
                {
                    Board = r.Board, Expect = r.Expect, MatchId = r.MatchId, MatchNo = r.MatchNo,
                    HomeTeam = r.HomeTeam, AwayTeam = r.AwayTeam, MatchTime = r.MatchTime,
                    RecResult = isCover ? null : r.RecResult,
                    IsCoverAll = isCover,
                    Confidence = isCover ? null : r.Confidence,
                    SignalLevel = r.SignalLevel,
                    SignalDesc = signalDesc,
                    Score1 = r.ScoreCandidates.Count > 0 ? r.ScoreCandidates[0] : null,
                    Score2 = r.ScoreCandidates.Count > 1 ? r.ScoreCandidates[1] : null,
                    Goals1 = r.GoalCandidates.Count > 0 ? r.GoalCandidates[0] : null,
                    Goals2 = r.GoalCandidates.Count > 1 ? r.GoalCandidates[1] : null,
                    RecHandicap = r.RecHandicap,
                    RecHalfFull = r.HalfFullPick,
                    RecDate = DateTime.Today
                });
            }
        }

        await _repo.SaveRecommendationsAsync(toSave, ct);
        return all;
    }

    /// <summary>
    /// 复盘：把已完场比赛的赛果回填到推荐记录，返回命中统计。
    /// 对每一条未回填的推荐，按 fid 查赛果，回填 ActualScore/ActualGoals/ActualResult/Hit。
    /// </summary>
    public async Task<SettlementResult> SettleAsync(CancellationToken ct = default)
    {
        var unsettled = await _repo.GetUnsettledRecommendationsAsync(ct);
        var fids = unsettled.Where(r => r.MatchId is not null).Select(r => r.MatchId!.Value).Distinct().ToList();
        var results = await _repo.GetResultsAsync(fids, ct);

        var settled = new List<SettlementItem>();
        foreach (var r in unsettled)
        {
            if (r.MatchId is null || !results.TryGetValue(r.MatchId.Value, out var res)) continue;
            if (res.ResultSpf is null) continue;   // 尚未完场

            var actualScore = res.Home is not null && res.Away is not null ? $"{res.Home}-{res.Away}" : "";
            var actualGoals = res.Home is not null && res.Away is not null ? res.Home + res.Away : null;
            var actualResult = res.ResultSpf!.Value;
            var isCover = r.IsCoverAll || r.RecResult is null || (r.Confidence is null && r.RecResult == '1');
            var hit = RecommendationScoring.DirectionalHit(isCover ? null : r.RecResult, isCover, actualResult);
            var counts = RecommendationScoring.CountsInHitRate(isCover ? null : r.RecResult, isCover, r.Confidence);

            await _repo.BackfillResultAsync(r.RecId, actualScore, actualGoals ?? 0, actualResult, hit, ct);

            settled.Add(new SettlementItem
            {
                Board = r.Board, MatchNo = r.MatchNo, HomeTeam = r.HomeTeam, AwayTeam = r.AwayTeam,
                RecResult = r.RecResult, ActualResult = actualResult, Hit = hit, Counts = counts,
                RecScore = $"{r.Score1 ?? "-"}/{r.Score2 ?? "-"}", ActualScore = actualScore,
                RecGoals = r.Goals1?.ToString() ?? "-", ActualGoals = actualGoals?.ToString() ?? "-"
            });
        }

        // 命中率只统计有方向且置信度达到发布门槛的场次
        var counted = settled.Where(s => s.Counts).ToList();
        var total = counted.Count;
        var hitCount = counted.Count(s => s.Hit == true);
        var byBoard = counted.GroupBy(s => s.Board)
            .Select(g => new BoardSettlement
            {
                Board = g.Key,
                Total = g.Count(),
                Hit = g.Count(s => s.Hit == true),
                HitRate = g.Count() > 0 ? Math.Round(100.0 * g.Count(s => s.Hit == true) / g.Count(), 1) : 0
            }).ToList();

        return new SettlementResult
        {
            Total = total,
            Hit = hitCount,
            HitRate = total > 0 ? Math.Round(100.0 * hitCount / total, 1) : 0,
            ByBoard = byBoard,
            Items = settled
        };
    }
}

/// <summary>复盘结果</summary>
public sealed class SettlementResult
{
    public int Total { get; set; }
    public int Hit { get; set; }
    public double HitRate { get; set; }
    public List<BoardSettlement> ByBoard { get; set; } = new();
    public List<SettlementItem> Items { get; set; } = new();
}

public sealed class BoardSettlement
{
    public string Board { get; set; } = "";
    public int Total { get; set; }
    public int Hit { get; set; }
    public double HitRate { get; set; }
}

public sealed class SettlementItem
{
    public string Board { get; set; } = "";
    public string? MatchNo { get; set; }
    public string? HomeTeam { get; set; }
    public string? AwayTeam { get; set; }
    public char? RecResult { get; set; }
    public char ActualResult { get; set; }
    public bool? Hit { get; set; }
    /// <summary>是否计入本次命中率（有方向且置信度达到发布门槛）。</summary>
    public bool Counts { get; set; }
    public string? RecScore { get; set; }
    public string? ActualScore { get; set; }
    public string? RecGoals { get; set; }
    public string? ActualGoals { get; set; }
}
