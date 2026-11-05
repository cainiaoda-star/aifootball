using AiFootBall.Core.Models;
using AiFootBall.Data;

namespace AiFootBall.Sources;

/// <summary>
/// 推荐编排：采集官方受注场次 → 关联 fid → 计算赔率/必发信号 → 生成推荐。
/// </summary>
public sealed class RecommendationService
{
    /// <summary>
    /// 实时推荐认的最新盘口年龄（小时）。超过就不参与信号计算。
    /// 依据：必发 30 分钟一轮、临场盘 20 分钟一轮，正常场次不会超过 12 小时；
    /// 而 12 小时以上的盘往往来自前一晚，用它算「升/降」等于把旧盘当现盘。
    /// 待采集覆盖度上来以后（待赛场次基本都有 1 小时内的盘），可以收紧到 6。
    /// </summary>
    private const double MarketTtlHours = 12;

    private readonly BettingCardSource _cardSource;
    private readonly BettingRepository _repo;
    private readonly JcOddsRepository _jcOdds;
    private readonly HitProbabilityService? _hit;
    private readonly MilkScanService? _milk;
    private readonly TicketRepository? _tickets;
    private readonly MilkPickRepository? _milkPicks;

    public RecommendationService(BettingCardSource cardSource, BettingRepository repo, JcOddsRepository jcOdds,
        HitProbabilityService? hit = null, MilkScanService? milk = null, TicketRepository? tickets = null,
        MilkPickRepository? milkPicks = null)
    {
        _cardSource = cardSource;
        _repo = repo;
        _jcOdds = jcOdds;
        _hit = hit;
        _milk = milk;
        _tickets = tickets;
        _milkPicks = milkPicks;
    }

    /// <summary>采样竞彩四类玩法固定奖金，写入 FbJcOddsHistory 时间序列。</summary>
    public async Task<CollectionResult> SampleJcOddsAsync(CancellationToken ct = default)
    {
        var now = DateTime.Now;
        var sampleAt = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);

        var cards = await _cardSource.FetchJcAllPlaysAsync(ct);
        if (cards.Count == 0)
        {
            // 空盘也要落台账。否则「今天竞彩没有在售」和「采集挂了」在页面上长得一样。
            await _jcOdds.LogScanFailureAsync("jc", sampleAt, "竞彩返回 0 场（无受注或接口异常）", ct);
            return CollectionResult.Ok(0, "竞彩无受注场次");
        }

        var withBf = cards.Count(c => c.BfOdds.Count > 0);
        var withJq = cards.Count(c => c.JqOdds.Count > 0);
        var withBq = cards.Count(c => c.BqOdds.Count > 0);

        var snapshots = BettingCardSource.ToSnapshots(cards);
        var rows = await _jcOdds.SaveSnapshotAsync(snapshots, sampleAt, ct: ct);

        return CollectionResult.Ok(rows,
            $"竞彩固定奖金采样 {cards.Count} 场（比分 {withBf} / 总进球 {withJq} / 半全场 {withBq}），写入 {rows} 个选项");
    }

    /// <summary>
    /// 回填某天竞彩已完场场次的最终固定奖金（四类玩法），时间点记为该场截止时间。
    /// 500 只保留最近约 30 天，再早页面会退回当天在售，此时一行都不写。
    /// </summary>
    public async Task<CollectionResult> BackfillJcDayAsync(DateOnly date, CancellationToken ct = default)
    {
        var cards = await _cardSource.FetchJcAllPlaysAsync(ct, date);
        var ended = cards.Where(c => c.IsEnd && c.BuyEndTime is not null && c.MatchId is not null).ToList();
        var onDay = ended.Where(c => Math.Abs((DateOnly.FromDateTime(c.BuyEndTime!.Value).DayNumber - date.DayNumber)) <= 1).ToList();
        if (onDay.Count == 0)
            return CollectionResult.Ok(0, $"{date:yyyy-MM-dd} 没有已完场的竞彩（页面 {cards.Count} 场，可能已超出 500 保留范围）");
        if (onDay.Count < ended.Count / 2)
            return CollectionResult.Ok(0, $"{date:yyyy-MM-dd} 页面退回了在售期，不写入");

        var endAt = onDay.ToDictionary(c => c.MatchId!.Value, c => c.BuyEndTime!.Value);
        var rows = 0;
        foreach (var g in BettingCardSource.ToSnapshots(onDay).GroupBy(s => endAt[s.MatchId!.Value]))
            rows += await _jcOdds.SaveSnapshotAsync(g.ToList(), g.Key, watchMoves: false, ct: ct);

        return CollectionResult.Ok(rows,
            $"{date:yyyy-MM-dd} 竞彩回填 {onDay.Count} 场（比分 {onDay.Count(c => c.BfOdds.Count > 0)} / 总进球 {onDay.Count(c => c.JqOdds.Count > 0)} / 半全场 {onDay.Count(c => c.BqOdds.Count > 0)}），写入 {rows} 个选项");
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
    public Task<List<MatchRecommendation>> RecommendAsync(string board, string? expect = null, CancellationToken ct = default)
        => ScoreAsync(board, expect, null, review: false, ct);

    /// <summary>按开赛日重算已经踢过的场次。复盘用，不套用「在售」过滤。</summary>
    public Task<List<MatchRecommendation>> ScoreForReviewAsync(
        string board, IReadOnlyList<BettingCardRecord> cards, CancellationToken ct = default)
        => ScoreAsync(board, null, cards, review: true, ct);

    /// <summary>
    /// 竞彩大奶榜（2026-11-04 改向）：比分、半全场（胜负/负胜/胜平/负平，赔率 ≥4.0）、总进球。
    /// jcRecs 传当日已算好的竞彩推荐可避免重跑整条盘口管线；不传就现场算一遍。
    /// </summary>
    public async Task<List<MilkPick>> RecommendMilkAsync(
        IReadOnlyList<MatchRecommendation>? jcRecs = null, CancellationToken ct = default)
        => (await RecommendMilkWithGateAsync(jcRecs, ct)).Picks;

    /// <summary>
    /// 大奶榜 + 方向闸诊断。2026-11-05 加：光有榜说不清「这场为什么没发」，
    /// 诊断要和榜一起返回、一起落库，赛后才有东西可复盘。
    /// </summary>
    public async Task<MilkBuildResult> RecommendMilkWithGateAsync(
        IReadOnlyList<MatchRecommendation>? jcRecs = null, CancellationToken ct = default)
    {
        var recs = jcRecs ?? await RecommendAsync(Boards.Jc, null, ct);
        var fids = recs.Where(r => r.MatchId is > 0).Select(r => r.MatchId!.Value).Distinct().ToList();
        if (fids.Count == 0 || _hit is null) return new();

        var points = await _jcOdds.GetLatestAsync(fids, ct);
        var odds = points
            .Where(p => p.MatchId is > 0)
            .GroupBy(p => p.MatchId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        HitProbabilityService.Snapshot snap;
        try { snap = await _hit.GetSnapshotAsync(ct); }
        catch (OperationCanceledException) { throw; }
        catch { snap = HitProbabilityService.Snapshot.Empty; }

        // 亚盘初/终盘只用于诱盘告警（亚盘不当方向用，它的历史方向命中率 47.7%）。
        // 取不到就算了，别让一个可选字段把整张榜拖死。
        Dictionary<long, (decimal? Open, decimal? Close)> asian;
        try { asian = await _repo.GetAsianMoveAsync(fids, ct, MarketTtlHours); }
        catch (OperationCanceledException) { throw; }
        catch { asian = new(); }

        var built = MilkPickEngine.BuildDetailed(recs, odds, snap.Scores, asian);
        // 模型没跑起来的时候，空榜必须自己说出原因——否则「空榜」和「今天确实没注」长得一模一样。
        if (!built.Funnel.ScoresReady)
            built.Funnel.ScoresNote = _hit?.LastError ?? "比分模型样本不足（可用样本 < 30 场），本次不出榜";
        return built;
    }

    /// <summary>
    /// 用历史盘口重算某一天的大奶榜与方向闸——纯诊断，不落库、不出票。
    ///
    /// 为什么要这条路径：11-04 那三场（朗斯 1:9、费内巴切反赢、波尔图没平）的榜已经发出去了，
    /// 想回答「如果当时就有这道闸，会不会拦下来」只能拿同一批赔率快照重跑一遍。
    /// 这也是以后调阈值时唯一的验证手段——不能每次改完都等第二天开奖才知道对不对。
    ///
    /// **这条路径能回答什么、不能回答什么，必须分清**（2026-11-05 实测踩到）：
    ///   · 能：给定今天的模型和当时的价格，这道闸会怎么判。调阈值前后对比就看它。
    ///   · 不能：还原「当晚线上到底怎么想」。模型是拿全量样本重新拟合的离散对象，
    ///     补采、赛果入库、样本窗口滑动都会让它翻方向——实测回测 11-04 的朗斯给出「客胜」，
    ///     而当晚线上给的是「胜胜」（所以那晚才押了半全场胜胜）。
    ///     要精确复盘当晚，只能看 dbo.FbMilkGate——那张表就是为此在出票当时把方向存下来的。
    /// </summary>
    public async Task<MilkBuildResult> BacktestMilkAsync(DateTime day, CancellationToken ct = default)
    {
        var cards = await _repo.GetMilkBacktestCardsAsync(day.Date, ct);
        var fids = cards.Where(c => c.MatchId is > 0).Select(c => c.MatchId!.Value).Distinct().ToList();
        if (fids.Count == 0) return new();   // 回测不用线上的命中率快照，所以不依赖 _hit

        // 复盘口径：必发取「开赛时刻」那一条（必发会记到赛中，用最后一条等于把比分喂进信号）；
        // 盘口不设新鲜度限制，历史数据本来就不新。
        var exRatio = await _repo.GetExchangeRatioAsync(fids, ct, null, asOfKickoff: true);
        var totalLines = await _repo.GetTotalLineAsync(fids, ct);
        var asian = await _repo.GetAsianMoveAsync(fids, ct, null);

        var recs = new List<MatchRecommendation>(cards.Count);
        foreach (var c in cards)
        {
            if (c.MatchId is not long mid) continue;
            if (string.IsNullOrWhiteSpace(c.HomeTeam) || string.IsNullOrWhiteSpace(c.AwayTeam)) continue;

            var r = new MatchRecommendation
            {
                Board = Boards.Jc,
                Expect = c.Expect,
                MatchNo = c.MatchNo,
                MatchId = mid,
                LeagueName = c.LeagueName,
                HomeTeam = c.HomeTeam,
                AwayTeam = c.AwayTeam,
                MatchTime = c.MatchTime
            };
            if (exRatio.TryGetValue(mid, out var x))
            {
                r.RatioHome = x.Home;
                r.RatioDraw = x.Draw;
                r.RatioAway = x.Away;
            }
            if (totalLines.TryGetValue(mid, out var tl)) r.TotalLine = tl;
            recs.Add(r);
        }

        var points = await _jcOdds.GetLatestAsync(fids, ct);
        var odds = points
            .Where(p => p.MatchId is > 0)
            .GroupBy(p => p.MatchId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        // 关键：模型必须用「当天 0 点之前完赛」的样本重新拟合。
        // 直接用线上的 HitProbabilityService 快照会让回测彻底失去意义——那个快照是全量训练的，
        // 已经知道当天的赛果。2026-11-05 实测：拿线上快照回测 11-04 的朗斯，模型给出「客胜」，
        // 因为它见过 1:9；而在当时它给的是「胜胜」。用带未来信息的模型去评价过去的决策，
        // 得到的只能是「这道闸什么也没拦」。
        // 口径说明：一天里所有场次共用一个「当日 0 点」的模型，比线上逐场赛前重算略保守。
        var samples = await _repo.GetScoreSamplesAsync(ct, day.Date);
        var scores = ScorelineModel.Fit(samples);

        return MilkPickEngine.BuildDetailed(recs, odds, scores, asian);
    }

    async Task<List<MatchRecommendation>> ScoreAsync(
        string board, string? expect, IReadOnlyList<BettingCardRecord>? given, bool review, CancellationToken ct)
    {
        var cards = given?.ToList() ?? await _repo.GetCardsAsync(board, expect, ct);
        if (cards.Count == 0) return new();

        var fids = cards.Where(c => c.MatchId is not null).Select(c => c.MatchId!.Value).ToList();

        // 复盘看历史，必须用当时那批数据，所以不加新鲜度限制；
        // 实时推荐只认 MarketTtlHours 以内采到的盘——把三天前的盘当现盘算升降，
        // 会把「模型有信号」这件事凭空造出来。
        double? fresh = review ? null : MarketTtlHours;

        var oddsDelta = fids.Count > 0 ? await _repo.GetEuOddsDeltaAsync(fids, ct, fresh) : new();
        // 复盘按「开赛时刻」取必发：必发会一直记到赛中，用最后一条等于把比分喂进信号。
        var exRatio = fids.Count > 0 ? await _repo.GetExchangeRatioAsync(fids, ct, fresh, review) : new();
        var totalLines = fids.Count > 0 ? await _repo.GetTotalLineAsync(fids, ct) : new();
        var closingEu = fids.Count > 0 ? await _repo.GetClosingEuAsync(fids, ct, fresh) : new();
        var asian = fids.Count > 0 ? await _repo.GetAsianMoveAsync(fids, ct, fresh) : new();
        var lateSteam = fids.Count > 0 ? await _repo.GetLateHomeSteamAsync(fids, ct, fresh) : new();
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

        var jcSpf = string.Equals(board, Boards.Jc, StringComparison.OrdinalIgnoreCase) && fids.Count > 0
            ? await _jcOdds.GetLatestSpfAsync(fids, ct)
            : new Dictionary<long, (decimal Win, decimal Draw, decimal Loss)>();
        var jcRq = string.Equals(board, Boards.Jc, StringComparison.OrdinalIgnoreCase) && fids.Count > 0
            ? await _jcOdds.GetLatestNspfAsync(fids, ct)
            : new Dictionary<long, (decimal Win, decimal Draw, decimal Loss)>();

        var bdSp = string.Equals(board, Boards.Bd, StringComparison.OrdinalIgnoreCase)
            ? await BdSpAsync(ct)
            : new Dictionary<long, BdMarketOdds>();

        var now = DateTime.Now;
        var result = new List<MatchRecommendation>();
        foreach (var c in cards)
        {
            // 已开赛（比赛时间已过）的场次不再参与推荐
            if (c.MatchTime is null && c.MatchId is not null && matchTimes.TryGetValue(c.MatchId.Value, out var mt))
                c.MatchTime = mt;
            if (!review)
            {
                if (c.MatchTime is not null && c.MatchTime.Value <= now)
                    continue;
                if (string.Equals(board, Boards.Jc, StringComparison.OrdinalIgnoreCase))
                {
                    var beijing = RecommendationSelection.JcSaleHours.Now();
                    if (!RecommendationSelection.JcSaleHours.IsTodaysIssue(c.MatchNo, beijing))
                        continue;
                    if (c.MatchTime is DateTime kick
                        && !RecommendationSelection.JcSaleHours.CanStillBuy(beijing, kick))
                        continue;
                }
            }

            decimal? delta = null, drawDelta = null;
            decimal? rh = null, rd = null, ra = null;
            decimal? line = null;
            decimal? spfHome = null, spfDraw = null, spfAway = null;
            if (c.MatchId is long spfId && jcSpf.TryGetValue(spfId, out var officialSpf))
            {
                spfHome = officialSpf.Win;
                spfDraw = officialSpf.Draw;
                spfAway = officialSpf.Loss;
            }
            else
            {
                spfHome = c.SpfHome;
                spfDraw = c.SpfDraw;
                spfAway = c.SpfAway;
            }
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
            RecommendationEngine.ApplyScoreline(rec, hitSnap.Scores, spfHome, spfAway);

            if (string.Equals(board, Boards.Bd, StringComparison.OrdinalIgnoreCase))
            {
                BdMarketOdds? sp = null;
                if (c.MatchId is long bdId) bdSp.TryGetValue(bdId, out sp);
                RecommendationSelection.ApplyBdHandicap(
                    rec, spfHome, spfDraw, spfAway, line, sp?.Home, sp?.Draw, sp?.Away);
            }

            if (string.Equals(board, Boards.Jc, StringComparison.OrdinalIgnoreCase))
            {
                decimal? rqW = null, rqD = null, rqL = null;
                if (c.MatchId is long rqId && jcRq.TryGetValue(rqId, out var rq))
                {
                    rqW = rq.Win;
                    rqD = rq.Draw;
                    rqL = rq.Loss;
                }
                RecommendationSelection.ApplyJcHandicap(rec, spfHome, spfDraw, spfAway, line, rqW, rqD, rqL);
            }

            HotWatch.Mark(rec);

            if (UpsetWatch.Matches(spfHome, delta, rh))
            {
                rec.UpsetWatch = true;
                rec.UpsetNote = UpsetWatch.Note(spfHome!.Value, delta!.Value, rh!.Value);
            }

            TotalLineMath.Apply(rec, hitSnap.Scores);

            result.Add(rec);
        }

        return result;
    }

    private static readonly SemaphoreSlim BdSpGate = new(1, 1);
    private static Dictionary<long, BdMarketOdds> _bdSp = new();
    private static DateTime _bdSpAt = DateTime.MinValue;

    private async Task<Dictionary<long, BdMarketOdds>> BdSpAsync(CancellationToken ct)
    {
        await BdSpGate.WaitAsync(ct);
        try
        {
            if (DateTime.Now - _bdSpAt < TimeSpan.FromMinutes(3)) return _bdSp;
            try
            {
                var map = await _cardSource.FetchBdSpfAsync(ct);
                if (map.Count > 0) { _bdSp = map; _bdSpAt = DateTime.Now; }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* 拿不到 SP 时仍按概率推，只是不显示 SP */ }
            return _bdSp;
        }
        finally { BdSpGate.Release(); }
    }

    /// <summary>把此刻页面上的推荐和库里当天的行写成证据，按时间追加，不覆盖。</summary>
    public Task<string> CaptureEvidenceAsync(CancellationToken ct = default)
        => RecommendationEvidence.CaptureAsync(this, _repo, ct);

    /// <summary>写一天的复盘文档。竞彩全部场次，北单 A、B、C 级，没进精选的也写。</summary>
    public Task<string> WriteDailyReviewAsync(DateTime day, CancellationToken ct = default)
        => DailyReview.WriteAsync(this, _repo, day, ct, _milk, _tickets);

    /// <summary>生成推荐并落库（复盘用）。2026-11-04 改向：胜负彩、北单推荐下线（采集保留），
    /// 只生成竞彩；竞彩胜平负/让球照算照存（校准、否决台账靠它们），对外发布只发大奶。</summary>
    public async Task<Dictionary<string, List<MatchRecommendation>>> RecommendAllAndSaveAsync(CancellationToken ct = default)    {
        var all = new Dictionary<string, List<MatchRecommendation>>();
        var toSave = new List<RecommendationRecord>();
        // ForDisplay 会把非北单的让球方向清掉（那是给页面展示用的），但落库和出票都要用它。
        // 清空之后 Recommendation.HandicapHit 永远算不出来，票也就永远无法结算，
        // 所以先在展示过滤之前留一份，落库前再回填回去。
        var hcpSnapshot = new Dictionary<string, (char? Pick, decimal? Line)>(StringComparer.Ordinal);
        // 大奶推荐与出票用的未过滤名单（与 /api/recommend 的 hcpRows 同源）。
        var raw = new Dictionary<string, List<MatchRecommendation>>();

        foreach (var board in new[] { Boards.Jc })
        {
            var recs = await RecommendAsync(board, null, ct);
            raw[board] = recs;
            foreach (var r in recs)
                if (r.RecHandicap is char pick && r.MatchNo is not null)
                    hcpSnapshot[$"{board}:{r.MatchNo}"] = (pick, r.Handicap);

            var strong = RecommendationSelection.ForDisplay(board, recs);
            all[board] = strong;

            // 把让球方向补回来，落库和出票都要
            foreach (var r in strong)
                if (r.MatchNo is not null && hcpSnapshot.TryGetValue($"{board}:{r.MatchNo}", out var h))
                    (r.RecHandicap, r.Handicap) = h;

            // 先清当日该板块旧精选，再写入；否则被策略踢出的场次会一直留在复盘表里
            await _repo.DeleteRecommendationsByBoardDateAsync(board, DateTime.Today, ct);

            // 被挡下的场次也留一份。以前只写「发出去的」，于是「75% 极端大热」
            // 这条规则近 30 天在库里出现 0 次——挡掉了什么、挡得对不对，无从回溯。
            // 台账独立一张表，不进命中率、不进校准，不影响任何发布决策。
            await _repo.DeleteVetoesByBoardDateAsync(board, DateTime.Today, ct);
            await _repo.SaveVetoesAsync(VetoLog.Build(board, recs, strong, DateTime.Today), ct);

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
                    HitProbability = isCover ? null : r.HitProbability,
                    HitProbNote = isCover ? null : r.HitProbabilityNote,
                    PickOdds = isCover ? null : r.PickOdds,
                    MarketProb = isCover ? null : r.MarketProb,
                    ModelTag = isCover ? null : r.ModelTag,
                    SignalLevel = r.SignalLevel,
                    SignalDesc = signalDesc,
                    Score1 = r.ScoreCandidates.Count > 0 ? r.ScoreCandidates[0] : null,
                    Score2 = r.ScoreCandidates.Count > 1 ? r.ScoreCandidates[1] : null,
                    Goals1 = r.GoalCandidates.Count > 0 ? r.GoalCandidates[0] : null,
                    Goals2 = r.GoalCandidates.Count > 1 ? r.GoalCandidates[1] : null,
                    RecHandicap = r.RecHandicap,
                    Handicap = r.RecHandicap is null ? null : r.Handicap,
                    RecHalfFull = r.HalfFullPick,
                    RecDate = DateTime.Today
                });
            }
        }

        // 盘口采集时间最后统一回填：校准报表要按「盘龄」分层看命中率，
        // 否则无法区分「模型不准」和「喂进去的是三天前的盘」。
        var ids = toSave.Where(r => r.MatchId is not null).Select(r => r.MatchId!.Value).Distinct().ToList();
        if (ids.Count > 0)
        {
            var fresh = await _repo.GetMarketFreshnessAsync(ids, ct);
            foreach (var r in toSave)
                if (r.MatchId is long mid && fresh.TryGetValue(mid, out var at))
                    r.DataAt = at;
        }

        await _repo.SaveRecommendationsAsync(toSave, ct);

        // 大奶推荐与出票（2026-11-04 改向）：每日发布只发竞彩大奶——比分/半全场/总进球。
        // 台账落 FbMilkPick，票落既有票面台账（单关：概率最高的一注，2串1 命中率相乘与「提高中奖率」相反）。
        await SaveMilkAsync(raw.GetValueOrDefault(Boards.Jc), ct);

        return all;
    }

    /// <summary>
    /// T−60min 重算：只对「马上要开赛、但最后一版数据还早于赛前 60 分钟」的场次重算一遍。
    ///
    /// 固定保存时点（08:40 / 11:40 / 16:40 / 20:40 + 停售前 8 分钟的终版：工作日 21:50、周末 22:50）都是全量重算，
    /// 20:40 那次对当晚早些时候开赛的比赛可能已经晚了。规则文本说的是「终盘」，实际用的是早盘价。
    ///
    /// 返回 (是否需要重算, 待重算场次数, 保存场次数)。
    /// </summary>
    public async Task<(bool Refreshed, int Stale, int Saved)> RefreshNearKickoffAsync(CancellationToken ct = default)
    {
        var stale = await _repo.CountNearKickoffStaleAsync(ct: ct);
        if (stale <= 0) return (false, 0, 0);

        var all = await RecommendAllAndSaveAsync(ct);
        return (true, stale, all.Values.Sum(v => v.Count));
    }

    /// <summary>
    /// 大奶推荐落库 + 大奶票出票。榜是空的（模型未就绪/无受注场）就不出票，当天不留空票。
    /// 台账先删当日未结算行再写，重算刷新概率不会冲掉已结算的历史。
    /// </summary>
    private async Task SaveMilkAsync(List<MatchRecommendation>? jcRecs, CancellationToken ct)
    {
        if (_milkPicks is null || _tickets is null) return;

        var build = await RecommendMilkWithGateAsync(jcRecs, ct);
        await _milkPicks.SaveDayAsync(DateTime.Today, build.Picks, ct);

        // 方向闸的诊断与榜同批落库（2026-11-05）。存下来才回答得了
        // 「11-04 那三场当时模型指哪、市场指哪」——快照一滚就再也追不回来了。
        try { await _milkPicks.SaveGateAsync(DateTime.Today, build.Gates, ct); }
        catch (OperationCanceledException) { throw; }
        catch { /* 台账表还没建时不影响出票，下一轮再记 */ }

        var draft = MilkPickEngine.BuildTicket(build.Picks, DateTime.Today);
        if (draft is null) return;
        await _tickets.UpsertAsync(draft, ct);
    }

    /// <summary>
    /// 回填否决记录的实际结果。北单被挡的是让球那一注，用让球结果判；
    /// 竞彩被挡的是胜平负方向，直接对赛果。未完场的留着，下一轮再判。
    /// </summary>
    private async Task SettleVetoesAsync(CancellationToken ct)
    {
        var pending = await _repo.GetUnsettledVetoesAsync(ct);
        if (pending.Count == 0) return;

        var ids = pending.Where(v => v.MatchId is not null).Select(v => v.MatchId!.Value).Distinct().ToList();
        var results = await _repo.GetResultsAsync(ids, ct);

        foreach (var v in pending)
        {
            if (v.MatchId is not long mid || !results.TryGetValue(mid, out var res)) continue;
            if (res.ResultSpf is not char actual) continue;   // 还没完场

            var score = res.Home is not null && res.Away is not null ? $"{res.Home}-{res.Away}" : "";

            // 判定规则在 VetoLog.Judge 里，纯函数、有单测。这里只负责取数和落库。
            var (hit, hcpHit) = VetoLog.Judge(v.Board, v.Pick, v.HcpPick, v.Handicap,
                actual, res.Home, res.Away);

            await _repo.BackfillVetoResultAsync(v.VetoId, score, actual, hit, hcpHit, ct);
        }
    }

    /// <summary>否决台账报表。表还没建时返回空报表，不抛给调用方。</summary>
    public async Task<VetoReport> GetVetoReportAsync(int days, CancellationToken ct = default, string? board = null)
    {
        try
        {
            var rows = await _repo.GetVetoStatsAsync(days, ct, board);
            return VetoLog.Report(days, rows);
        }
        catch (Exception)
        {
            return new VetoReport { Days = days, Verdict = "否决台账不可用（表未建或数据库未连上）。" };
        }
    }

    /// <summary>
    /// 结算所有腿都已判定的票。返回本次结算张数。
    /// 腿未判完（还有没开赛的）就留着，宁可晚结也不算半张票。
    /// </summary>
    public async Task<int> SettleTicketsAsync(CancellationToken ct = default)
    {
        if (_tickets is null) return 0;
        var pending = await _tickets.GetPendingAsync(null, ct);
        var settled = 0;
        foreach (var t in pending)
        {
            var outcome = TicketPlanBuilder.Settle(t);
            if (outcome is null) continue;
            await _tickets.MarkSettledAsync(t.TicketId, t.Legs,
                outcome.Value.Won, outcome.Value.HitCount, outcome.Value.Prize, ct);
            settled++;
        }
        return settled;
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

            bool? handicapHit = null;
            if (r.RecHandicap is char pick && r.Handicap is decimal hcp && res.Home is int hs && res.Away is int aws)
                handicapHit = HandicapMath.Result(hs, aws, hcp) == pick;

            await _repo.BackfillResultAsync(r.RecId, actualScore, actualGoals ?? 0, actualResult, hit, handicapHit, ct);

            settled.Add(new SettlementItem
            {
                Board = r.Board, MatchNo = r.MatchNo, HomeTeam = r.HomeTeam, AwayTeam = r.AwayTeam,
                RecResult = r.RecResult, ActualResult = actualResult,
                Hit = r.Board == Boards.Bd && handicapHit is not null ? handicapHit : hit,
                Counts = r.Board == Boards.Bd && handicapHit is not null || counts,
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

        // 赛果刚回填完，正好顺手结票。票面台账是新增的旁路，表还没建时不能拖垮复盘本身。
        try { await SettleTicketsAsync(ct); }
        catch (Exception) { /* 台账不可用就当没这张表，复盘照常出 */ }

        // 否决记录也用同一份赛果判定：「当时照打会不会中」。
        try { await SettleVetoesAsync(ct); }
        catch (Exception) { /* 同上，旁路不拖垮主流程 */ }

        // 大奶台账：比分/半全场/总进球对赛果判中。半场比分没采到的场留着下轮。
        try { if (_milkPicks is not null) await _milkPicks.SettleAsync(ct); }
        catch (Exception) { /* 大奶台账不可用不拖垮复盘本身 */ }

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
