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
    /// <summary>
    /// 盘口新鲜度容忍窗口（小时）。超过这个时长的采样不再当作「现盘」，
    /// 用它算出来的升降/资金流一律当没有（宁可少一个信号，也不要凭空造一个）。
    ///
    /// 2026-11-10 从 12 收到 8。依据不是调参，是两条约束：
    ///   · 下限：必须大于 OddsRepository.StaleResampleHours（6 小时）。
    ///     那是「盘口薄的场次最长多久重采一次」，容忍窗口比它小的话，
    ///     刚采回来的价会立刻被判成过期，采了等于没采，而且会出现时有时无的抖动。
    ///   · 上限：欧赔已经是 30 分钟粒度（water-sample 每 10 分钟一轮、每 3 轮一次三页全量），
    ///     8 小时相当于容忍 16 个刷新周期。再宽就没有「临场」的意义了 ——
    ///     12 小时的本意是「昨晚采的今天还能用」，但那正是这次要否掉的东西。
    ///
    /// 没有做「按玩法分档」（欧赔严、必发松）：那需要逐判据回测，
    /// 而现在必发的问题根本不在这 —— 夜间大量场次压根没有必发盘口，收紧 TTL
    /// 只会把「没数据」变成「判为过期」，两者结果都是 null，改了等于没改。
    ///
    /// 写成算式而不是字面量 8，是为了不让两个数脱钩：改 StaleResampleHours 时
    /// 这里自动跟着走，不会再出现「采样周期改了、容忍窗口忘了改」这种只有
    /// 跑一晚才发现的问题。6 × 4/3 = 8。
    /// </summary>
    private const double MarketTtlHours = OddsRepository.StaleResampleHours * 4.0 / 3.0;

    private readonly BettingCardSource _cardSource;
    private readonly BettingRepository _repo;
    private readonly JcOddsRepository _jcOdds;
    private readonly HitProbabilityService? _hit;
    private readonly MilkScanService? _milk;
    private readonly TicketRepository? _tickets;
    private readonly MilkPickRepository? _milkPicks;
    private readonly OddsCaptureRepository? _oddsCapture;
    /// <summary>积分榜仓储（2026-11-08 加）：复盘要用「赛前排名」讲「排名靠后先进球」。</summary>
    private readonly StandingRepository? _standings;

    public RecommendationService(BettingCardSource cardSource, BettingRepository repo, JcOddsRepository jcOdds,
        HitProbabilityService? hit = null, MilkScanService? milk = null, TicketRepository? tickets = null,
        MilkPickRepository? milkPicks = null, OddsCaptureRepository? oddsCapture = null,
        StandingRepository? standings = null)
    {
        _cardSource = cardSource;
        _repo = repo;
        _jcOdds = jcOdds;
        _hit = hit;
        _milk = milk;
        _tickets = tickets;
        _milkPicks = milkPicks;
        _oddsCapture = oddsCapture;
        _standings = standings;
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
    public async Task<CollectionResult> BackfillJcDayAsync(DateOnly date, CancellationToken ct = default,
        IProgress<string>? log = null)
    {
        // 抓取和落库分开计时：回补慢到底是「500 页面慢」还是「写库慢」，
        // 光看作业总耗时永远分不清，而这两者的处置完全不同（前者等网络/换 token，
        // 后者调批次/加索引）。
        var fetchClock = System.Diagnostics.Stopwatch.StartNew();
        var cards = await _cardSource.FetchJcAllPlaysAsync(ct, date);
        fetchClock.Stop();
        log?.Report($"    {date:yyyy-MM-dd} 抓取 {cards.Count} 场，{fetchClock.ElapsedMilliseconds}ms");
        var ended = cards.Where(c => c.IsEnd && c.BuyEndTime is not null && c.MatchId is not null).ToList();
        var onDay = ended.Where(c => Math.Abs((DateOnly.FromDateTime(c.BuyEndTime!.Value).DayNumber - date.DayNumber)) <= 1).ToList();
        if (onDay.Count == 0)
            return CollectionResult.Ok(0, $"{date:yyyy-MM-dd} 没有已完场的竞彩（页面 {cards.Count} 场，可能已超出 500 保留范围）");
        if (onDay.Count < ended.Count / 2)
            return CollectionResult.Ok(0, $"{date:yyyy-MM-dd} 页面退回了在售期，不写入");

        var endAt = onDay.ToDictionary(c => c.MatchId!.Value, c => c.BuyEndTime!.Value);
        var rows = 0;
        foreach (var g in BettingCardSource.ToSnapshots(onDay).GroupBy(s => endAt[s.MatchId!.Value]))
            rows += await _jcOdds.SaveSnapshotAsync(g.ToList(), g.Key, watchMoves: false, ct: ct, log: log);

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
        IReadOnlyList<MatchRecommendation>? jcRecs = null, CancellationToken ct = default,
        bool perMatch = false, DateTime? asOf = null)
    {
        var recs = jcRecs ?? await RecommendAsync(Boards.Jc, null, ct);
        var fids = recs.Where(r => r.MatchId is > 0).Select(r => r.MatchId!.Value).Distinct().ToList();
        if (fids.Count == 0 || _hit is null) return new();

        // `asOf`（2026-11-14 第九轮）：落库说的那一轮只认该时刻及之前的采样，
        // 与 `GetJcSourceSnapshotAsync(runAt)` 同一时间点。不给（诊断/API 预览）就取此刻最新。
        // 两条链路各看一个时间点，等于核对器在验另一批数据——缺档、超龄都查不到真正被用的那份。
        var points = await _jcOdds.GetLatestAsync(fids, ct, asOf);
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

        // 净胜球诊断（每 2 分钟刷新）：临场转向信号，主胜方向注 + 转向客队 → 撤注。
        // 取不到就整批跳过，别让一个可选字段把整张榜拖死。
        Dictionary<long, ScoreDiagRow> scoreDiags;
        try
        {
            scoreDiags = new Dictionary<long, ScoreDiagRow>();
            if (_oddsCapture is not null)
                foreach (var mid in fids)
                {
                    var d = await _oddsCapture.GetScoreDiagAsync(mid, ct);
                    if (d is not null) scoreDiags[mid] = d;
                }
        }
        catch (OperationCanceledException) { throw; }
        catch { scoreDiags = new(); }

        // 主胜「开盘→临场」水位（2026-11-07，技能11升水否决）：升水≥12% 撤主胜方向注。
        // 取不到就算了，别让一个可选字段把整张榜拖死。
        Dictionary<long, (decimal? Open, decimal? Curr)> spfWater;
        try { spfWater = _oddsCapture is not null ? await _oddsCapture.GetSpfWaterAsync(fids, ct) : new(); }
        catch (OperationCanceledException) { throw; }
        catch { spfWater = new(); }

        // 俱乐部画像（2026-11-07）：热门方历史反转倾向，高反转队反向档提级、碾压队抑制反向。
        // 取不到就算了，别让一个可选字段把整张榜拖死。
        Dictionary<string, TeamProfile> teamProfiles;
        try
        {
            teamProfiles = _oddsCapture is not null
                ? await _oddsCapture.GetTeamProfilesAsync(
                    recs.SelectMany(r => new[] { r.HomeTeam, r.AwayTeam }), ct: ct)
                : new();
        }
        catch (OperationCanceledException) { throw; }
        catch { teamProfiles = new(); }

        // 竞彩/必发定价背离（2026-11-08，曼城复盘落地）：竞彩客胜被单边砸价而必发不认 → 客胜方向注撤。
        // 回测见 PriceDivergence。取不到就算了，不能让一个可选信号把整张榜拖死。
        Dictionary<long, PriceDivergence.PriceSpan> priceSpans;
        try { priceSpans = _oddsCapture is not null ? await _oddsCapture.GetPriceSpanAsync(fids, ct) : new(); }
        catch (OperationCanceledException) { throw; }
        catch { priceSpans = new(); }

        // 体能/状态（2026-11-08，曼城复盘）：休息小时数、上一场是否欧战、最近三场胜负。
        // 只用于标注 FatigueTag，不参与概率——回测说「累了的强队会输」是反的（见 TeamForm）。
        // 队名要先解析成全称，和画像同一口径；取不到就算了，不能把一个标注拖死整张榜。
        Dictionary<string, TeamForm> teamForms;
        try
        {
            var formNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in recs)
                foreach (var n in new[] { r.HomeTeam, r.AwayTeam })
                {
                    if (string.IsNullOrWhiteSpace(n)) continue;
                    var key = snap.Scores.Resolve(r.LeagueName, n!);
                    if (!string.IsNullOrEmpty(key)) formNames.Add(key);
                }

            teamForms = _oddsCapture is null || formNames.Count == 0
                ? new()
                : await _oddsCapture.GetTeamFormAsync(formNames, DateTime.Now, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { teamForms = new(); }

        var built = MilkPickEngine.BuildDetailed(
            recs, odds, snap.Scores, asian, scoreDiags, spfWater, teamProfiles, priceSpans, teamForms, perMatch);
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
        // 截止时刻显式传给 Fit（2026-11-13）：与 SQL 里的 before 同值，两层都说「只用这一天之前的」。
        // Fit 内部遇到没有 MatchTime 的样本在给了 asOf 时一律排除——「不知道」不能当「安全」。
        var scores = ScorelineModel.Fit(samples, asOf: day.Date);

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
        var exPnl = fids.Count > 0 ? await _repo.GetExchangePnlAsync(fids, ct, fresh, review) : new();
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
                if (string.Equals(board, Boards.Jc, StringComparison.OrdinalIgnoreCase)
                    && !RecommendationSelection.JcSaleHours.Sellable(
                        c.MatchNo, c.MatchTime, RecommendationSelection.JcSaleHours.Now()))
                    continue;
            }

            decimal? delta = null, drawDelta = null;
            decimal? rh = null, rd = null, ra = null;
            decimal? pnlH = null, pnlA = null;
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
                if (exPnl.TryGetValue(c.MatchId.Value, out var pnl))
                {
                    pnlH = pnl.PnlIdxHome;
                    pnlA = pnl.PnlIdxAway;
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
                lateDelta, homePts, awayPts,
                pnlH, pnlA);

            // 收盘欧赔主胜绝对值（2026-11-11 加）。必须单独留一份：
            // 上面的 spfHome 会被 `spfHome ??= eu.Home` 顶成欧赔，但竞彩板块本身已有竞彩价时
            // 它就是竞彩价——同一个变量两种含义，不能拿来判「欧赔档位」。
            if (c.MatchId is long euId && closingEu.TryGetValue(euId, out var euClose))
                rec.HomeWinEuClose = euClose.Home;

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
        => DailyReview.WriteAsync(this, _repo, day, ct, _milk, _tickets, _standings);

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
    /// 临场转向哨兵（2026-11-05 加）：开赛前扫一遍当天已发出的未开赛大奶注，
    /// 对比净胜球诊断的转向信号与注的方向，命中的落告警到 dbo.FbTurnAlert。
    ///
    /// 这是「早干啥去了」的直接补丁——数据每 2 分钟在采、净胜球诊断在刷新，
    /// 但以前没有任何一条代码把「已出注的场次临场发生了市场转向」主动推出来。
    /// 返回本次新落库的告警条数。
    /// </summary>
    public async Task<TurnGuardRunResult> RunTurnGuardAsync(CancellationToken ct = default)
    {
        if (_milkPicks is null || _oddsCapture is null) return new TurnGuardRunResult();

        var (scan, openCount) = await ScanTurnAlertsAsync(ct);
        if (openCount == 0) return new TurnGuardRunResult();

        var save = await _milkPicks.SaveTurnAlertsAsync(DateTime.Today, scan, ct);
        var (marked, voided) = await _milkPicks.ApplyTurnAlertsToPicksAsync(DateTime.Today, ct);

        // ★ 高危告警必须当场改变榜面（2026-11-08，闭环的核心）。
        //
        // 旧行为：告警只写进 FbTurnAlert，榜单纹丝不动，下一次重算要等到 16:40 / 20:40。
        // 15:30 开赛的场次，最后一版榜是 11:40 —— 12:00 之后市场怎么塌，榜都不会动。
        // 用户 2026-11-08 的原话：「你监控到了，为啥不去改首页上的推荐结果，你是看着我往坑里跳吗？」
        //
        // 现在：一旦出现**新的**高危告警（mid→high 也算新），立刻全量重算当日榜，
        // 重算后再判一次并把告警回写——这样撤掉的注会从主榜消失，或换成同场另一个档位，
        // 推荐页在同一分钟内就看到结果，而不是等到下一个固定时点。
        var recalculated = false;
        if (save.NewHigh > 0)
        {
            try
            {
                await RecommendAllAndSaveAsync(ct);
                recalculated = true;
            }
            catch (OperationCanceledException) { throw; }
            catch { recalculated = false; }

            if (recalculated)
            {
                // 重算 = DELETE + INSERT，PickId 全变了、注也可能换了档位，
                // 所以必须用新榜重判一遍再回写，否则红标会丢。
                var (scan2, open2) = await ScanTurnAlertsAsync(ct);
                if (open2 > 0)
                {
                    await _milkPicks.SaveTurnAlertsAsync(DateTime.Today, scan2, ct);
                    (marked, voided) = await _milkPicks.ApplyTurnAlertsToPicksAsync(DateTime.Today, ct);
                }
            }
        }

        return new TurnGuardRunResult
        {
            Alerts = scan.Count,
            High = scan.Count(a => a.Severity == "high"),
            NewHigh = save.NewHigh,
            Marked = marked,
            Voided = voided,
            Recalculated = recalculated
        };
    }

    /// <summary>
    /// 只读诊断（2026-11-08 加）：逐注告诉调用方「这一注被判成什么、为什么」。
    /// 不落库、不改榜，专门回答「你监控到了为什么不改榜」的另一半——
    /// 「为什么这一注没被判成危险」。没有它，用户只能看到「无告警」三个字，等于黑箱。
    /// </summary>
    public async Task<List<TurnGuardScanRow>> ScanTurnGuardAsync(CancellationToken ct = default)
    {
        var rows = new List<TurnGuardScanRow>();
        if (_milkPicks is null) return rows;

        var open = await _milkPicks.GetOpenDayAsync(DateTime.Today, ct);
        if (open.Count == 0) return rows;

        var (alerts, _) = await ScanTurnAlertsAsync(ct);
        var map = alerts
            .GroupBy(a => (a.MatchId, a.PlayType, a.OptionCode))
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var p in open)
        {
            var dir = TurnGuard.PickDirection(p.PlayType, p.OptionCode);
            var hit = map.TryGetValue((p.MatchId, p.PlayType, p.OptionCode), out var a);
            rows.Add(new TurnGuardScanRow
            {
                PickId = p.PickId,
                MatchId = p.MatchId,
                MatchNo = p.MatchNo,
                HomeTeam = p.HomeTeam,
                AwayTeam = p.AwayTeam,
                MatchTime = p.MatchTime,
                PlayType = p.PlayType,
                OptionCode = p.OptionCode,
                OptionName = p.OptionName,
                Odds = p.Odds,
                BaselineEv = p.BaselineEv,
                Payback = TurnGuard.PaybackOf(p.PlayType),
                // ★ 必须用 BaselineEvFloor（与出票闸同源），不能用 PaybackOf（2026-11-08 晚修）。
                //   用返奖率当线会低估门槛：比分/总进球的出票下限是返奖率×0.85，
                //   拿返奖率去比会把这些注全标成「破线」，而它们其实是出票闸放行的。
                EvBroken = p.BaselineEv is decimal bev2 && bev2 > 0m
                           && bev2 < TurnGuard.BaselineEvFloor(p.PlayType),
                Direction = dir switch
                {
                    '3' => "主胜",
                    '1' => "平",
                    '0' => "客胜",
                    _ => "无方向"
                },
                Alerted = hit,
                Severity = a?.Severity,
                Reason = a?.Reason,
                SignalCode = a?.SignalCode,
                // 文案要说清两件事分别判没判（2026-11-08 改）：EV 破线跟方向无关，
                // 总进球/平局照样适用——旧文案一句「不参与转向判定」会让人以为这注完全没被检查过。
                Verdict = hit ? "已告警"
                    : dir is null
                        ? $"总进球无方向，转向信号不判；实证EV {p.BaselineEv:0.000} vs 出票下限 {TurnGuard.BaselineEvFloor(p.PlayType):0.000}（返奖率 {TurnGuard.PaybackOf(p.PlayType):0.000}×{TurnGuard.FloorRatioOf(p.PlayType):0.00}），未破线"
                    : dir == '1'
                        ? $"平局方向，转向信号不判（库里无推翻平局的实测证据）；实证EV {p.BaselineEv:0.000} vs 出票下限 {TurnGuard.BaselineEvFloor(p.PlayType):0.000}（返奖率 {TurnGuard.PaybackOf(p.PlayType):0.000}×{TurnGuard.FloorRatioOf(p.PlayType):0.00}），未破线"
                    : $"有方向（{dir}）：转向信号与注方向一致或不足以构成告警；实证EV {p.BaselineEv:0.000} vs 出票下限 {TurnGuard.BaselineEvFloor(p.PlayType):0.000}（返奖率 {TurnGuard.PaybackOf(p.PlayType):0.000}×{TurnGuard.FloorRatioOf(p.PlayType):0.00}），未破线"
            });
        }

        return rows.OrderBy(r => r.MatchTime).ToList();
    }

    /// <summary>
    /// 只做扫描、不落库（2026-11-08 从 RunTurnGuardAsync 拆出来）。
    /// 拆的理由：高危告警触发重算后要拿**新榜**再判一次，扫描逻辑必须可重入。
    /// </summary>
    private async Task<(List<TurnAlertRow> Alerts, int OpenCount)> ScanTurnAlertsAsync(CancellationToken ct)
    {
        var result = new List<TurnAlertRow>();
        if (_milkPicks is null || _oddsCapture is null) return (result, 0);

        var open = await _milkPicks.GetOpenDayAsync(DateTime.Today, ct);
        if (open.Count == 0) return (result, 0);

        var matchIds = open.Where(p => p.MatchId > 0).Select(p => p.MatchId).Distinct().ToList();
        if (matchIds.Count == 0) return (result, open.Count);

        // 净胜球诊断：一场一行，直接读已算好的标量（不碰明细）。
        var diagMap = new Dictionary<long, ScoreDiagRow>();
        foreach (var mid in matchIds)
        {
            var d = await _oddsCapture.GetScoreDiagAsync(mid, ct);
            if (d is not null) diagMap[mid] = d;
        }

        // 让胜赔率（NSPF 让胜）：出注时 vs 临场最新，看主队赢盘有没有被市场抛弃。
        var nspf = await _jcOdds.GetLatestNspfAsync(matchIds, ct);
        // 出注时让胜赔率：从 FbJcOddsHistory 里取该场最早的 NSPF 让胜快照。
        // 这里用最新 NSPF 的「让胜」字段做临场值，出注值由 GetTurnGuardNspfOpenAsync 补。
        var nspfOpen = await _jcOdds.GetEarliestNspfAsync(matchIds, ct);

        // 必发静态资金快照（盈亏指数 + 客胜占比）：补第 4 个「静态资金异常」信号。
        // 这是主胜大热反向/客胜资金涌入的铁证，时间序列诊断报不出来。
        Dictionary<long, (decimal? PnlIdxHome, decimal? PnlIdxAway)> exPnl;
        try { exPnl = await _repo.GetExchangePnlAsync(matchIds, ct); }
        catch (OperationCanceledException) { throw; }
        catch { exPnl = new(); }
        Dictionary<long, (decimal? Home, decimal? Draw, decimal? Away)> exRatio;
        try { exRatio = await _repo.GetExchangeRatioAsync(matchIds, ct); }
        catch (OperationCanceledException) { throw; }
        catch { exRatio = new(); }

        // 定价背离（2026-11-08）：让告警和选注共用同一个判据（PriceDivergence），
        // 而不是各写一套——上一轮的教训就是「告警在喊、选注不动」，两模块零联动。
        Dictionary<long, PriceDivergence.PriceSpan> priceSpans;
        try { priceSpans = await _oddsCapture.GetPriceSpanAsync(matchIds, ct); }
        catch (OperationCanceledException) { throw; }
        catch { priceSpans = new(); }

        var alerts = result;
        foreach (var p in open)
        {
            // 没有净胜球诊断 ≠ 这一注没问题（2026-11-08 修）。
            // 原来这里是 `if (!diagMap.TryGetValue(...)) continue;`——取不到诊断就直接跳过，
            // 于是「实证 EV 已跌破返奖率」这种与诊断无关的绝对判据永远轮不到执行。
            // 现在照常进判官（diag 传 null），转向类信号自然不报，但 EV 破线照样拦。
            diagMap.TryGetValue(p.MatchId, out var diag);

            var pickDir = TurnGuard.PickDirection(p.PlayType, p.OptionCode);

            (decimal OddsAtIssue, decimal OddsNow)? rangSheng = null;
            if (nspfOpen.TryGetValue(p.MatchId, out var openWin) && nspf.TryGetValue(p.MatchId, out var now))
                rangSheng = (openWin.Win, now.Win);

            (decimal? PnlIdxAway, decimal? RatioAway)? awayMoney = null;
            var hasPnl = exPnl.TryGetValue(p.MatchId, out var pnl);
            var hasRatio = exRatio.TryGetValue(p.MatchId, out var ratio);
            if (hasPnl || hasRatio)
                awayMoney = (hasPnl ? pnl.PnlIdxAway : null, hasRatio ? ratio.Away : null);

            var divergence = priceSpans.TryGetValue(p.MatchId, out var span)
                ? PriceDivergence.Judge(span)
                : null;

            var alert = TurnGuard.Judge(p, pickDir, diag, rangSheng, awayMoney, divergence);
            if (alert is not null) alerts.Add(alert);
        }

        return (alerts, open.Count);
    }

    /// <summary>
    /// 大奶推荐落库 + 大奶票出票。榜是空的（模型未就绪/无受注场）就不出票，当天不留空票。
    /// 台账先删当日未结算行再写，重算刷新概率不会冲掉已结算的历史。
    /// </summary>
    /// <summary>
    /// 稳胆榜（2026-11-09 加，用户拍板）。
    ///
    /// 只用到两样东西：当日竞彩场次、每场胜平负的快照。**不碰模型、不碰必发、不碰亚盘。**
    /// 理由写在 <see cref="SteadyPickEngine"/> 的注释里——这个栏目卖的是命中率，
    /// 而模型在胜平负上 51.9% vs 市场 56.9%，掺进来只会更低。
    ///
    /// 与大奶榜共用同一份 recs 时传进来，避免重复跑整条盘口管线。
    /// </summary>
    public async Task<SteadyPickEngine.Result> BuildSteadyAsync(
        IReadOnlyList<MatchRecommendation>? jcRecs = null, CancellationToken ct = default)
    {
        var recs = jcRecs ?? await RecommendAsync(Boards.Jc, null, ct);
        var fids = recs.Where(r => r.MatchId is > 0).Select(r => r.MatchId!.Value).Distinct().ToList();
        if (fids.Count == 0) return new SteadyPickEngine.Result();

        var points = await _jcOdds.GetLatestAsync(fids, ct);
        var odds = points
            .Where(p => p.MatchId is > 0)
            .GroupBy(p => p.MatchId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        return SteadyPickEngine.Build(recs, odds);
    }

    private async Task SaveMilkAsync(List<MatchRecommendation>? jcRecs, CancellationToken ct)
    {
        if (_milkPicks is null || _tickets is null) return;

        // 2026-11-10 修：按**注单自己的受注日**分组落库，不再一律用 DateTime.Today。
        //
        // 出错形态（当晚实测）：22:11 手动重算时页面已切到「周三」期号，算出 3 注周三场次，
        // 却全部记成 PickDate=11-10。而 SaveDayAsync 是先 DELETE 当日未结算行再 INSERT，
        // 于是 21:50 定版的周二两注（伯顿 半全场平负 @7.20）从榜面消失，只剩台账里那一份。
        // 用户 21:50 看到的榜和 22:11 之后的榜是两套东西，日志里却都叫「已刷新」。
        //
        // 口径与稳胆一致：**期号说了算**（见 SteadyPickRules.SalesDate）。
        // 「周三005」开球在 11-12 03:00，属于 11-11 那一期，不该记在 11-10 名下。
        // 分组之后，每一期只删自己那天，不同期互不覆盖。
        var today = DateTime.Today;

        // ★ 2026-11-13 第二轮：**「全部被过滤」与「采集失败」必须分开**（用户当天指出）。
        //
        // 上一版的写法是「范围来自参与计算的受注日，0 场就什么都不动」。
        // 它把 11-13 那种情况正好漏掉了：受注 11 场全被禁推名单挡在引擎之前，
        // 于是 `scopeDates` 为空 → 一行不碰 → 11:40 那注新加坡 vs 巴拉圭继续挂在榜上，
        // 而它按当时的规则根本不该存在。这不是「没有输入」，这是「有输入、全部被过滤」。
        //
        // 现在的口径写在 MilkBoardPlan 里（纯函数，可单测）：
        //   · 范围来自**过滤前**的场次（`jcRecs`，即 RecommendAsync 的原样输出）。
        //     全被挡掉时它仍然是 11 场，受注日仍然算得出来；
        //     绝不能从 `build.Inputs`（禁推在它之前就 return 了）、`FbMilkGate`、
        //     `Recommendation` 反推——那三者全是「算完之后」的产物，恰好同时为 0。
        //   · 有受注日 → 按日重写，注单列表可以为空（= 撤掉该日应撤的旧票）；
        //     SaveDayAsync 的删除谓词保证只删未开赛、已结算与已撤注保留。
        //   · 过滤前就是 0 场 → 连期号都确认不了，本轮一行不动（采集中断不等于撤榜）。
        //
        // 另外每轮生成一个 RunId（2026-11-13 第二轮）：输入凭证、全档存档、闸门、注单、
        // 轮次头五处都写同一个 RunId，验收按轮核对，不再拿上一轮的存档冒充本轮覆盖。
        // ★ `runAt` 是**这一轮的时间点**，计算与核对都必须停在这里（2026-11-14 第九轮）。
        // 之前只把它写进 `FbMilkRun.StartedAt` 和源快照，引擎仍取「此刻最新价」：
        // 采样在 runAt 之后又落一行时，账本核对的是 08:40 的清单、票是用 08:52 的价算的，
        // 两条链路对不上还都自称有依据。现在 `asOf: runAt` 让两边看同一份采样。
        var runAt = DateTime.Now;
        var build = await RecommendMilkWithGateAsync(jcRecs, ct, asOf: runAt);

        // ★ 采集状态（2026-11-13 第三轮）：`RawMatches > 0` 只证明「拿到了一些场次」。
        // 部分采集会让 `GetCardsAsync` 的工作集凭空缩小（其余行停在上一批的 CapturedAt），
        // 按不完整的工作集重写，没采到的那几场的旧票就会被撤掉。
        // 这里取「本批 vs 该期已知在售场次」，不给结论——结论在 MilkBoardPlan 里。
        MilkCaptureState capture;
        try
        {
            var (batchAt, issues) = await _repo.GetJcCaptureStateAsync(ct);
            capture = MilkCaptureState.Build(batchAt, runAt, MilkCaptureState.MaxBatchAge, issues);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // 查不到采集状态 = 无法证明完整，按「不完整」处理（不清榜），不猜。
            capture = MilkCaptureState.Missing($"采集状态查询失败：{ex.GetType().Name}: {ex.Message}");
        }

        var run = new MilkRun
        {
            RunId = MilkRun.NewId(runAt),
            StartedAt = runAt,
            RawMatches = jcRecs?.Count ?? 0,
            SalesDates = SalesDatesFor(jcRecs, today),
            VersionTag = build.Inputs.FirstOrDefault()?.VersionTag
                         ?? build.ProbSnapshots.FirstOrDefault()?.ModelVersion,
            Capture = capture
        };
        var plan = MilkBoardPlan.Create(run, build.Picks, today);

        // ★ 源侧快照（2026-11-14 第七轮）：本轮的「应该算哪些场」由**源侧**给出，不是引擎的输入凭证。
        //
        // 为什么必须另取一份：`build.Inputs` 只包含「写凭证之前没被 continue 掉」的场次。
        // 1419319 那种情况（模型认不出队名）在写凭证之前就 continue 了，
        // 于是「预期」和「实际」两边同时少一场，核对照样通过、照样发布。
        //
        // 受注规则与打分入口**同一份实现**（`JcSaleHours.Sellable`）：两处各写一遍的话，
        // 源侧会混进已停售的场次，要么误报缺失，要么把该算的一场放过去。
        var source = new List<MilkSourceMatch>();
        string? sourceError = null;
        try
        {
            var beijing = RecommendationSelection.JcSaleHours.Now();
            // `runAt` 就是下面写进 `FbMilkRun.StartedAt` 的那个时刻，源快照按它截止
            // （2026-11-14 第八轮）：不加这个边界，下一轮或事后补采的赔率会补进上一轮的「应有场次」，
            // 于是「那一轮到底该有几场」永远无法复现。
            source = (await _repo.GetJcSourceSnapshotAsync(runAt, ct))
                .Where(m => RecommendationSelection.JcSaleHours.Sellable(m.MatchNo, m.MatchTime, beijing))
                .Select(m =>
                {
                    m.SalesDate = SteadyPickRules.SalesDate(
                        string.IsNullOrWhiteSpace(m.MatchNo) ? null : m.MatchNo, m.MatchTime, today);
                    return m;
                })
                .ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // 取不到源侧快照 = 无法证明覆盖，按失败处理（不发布），不猜。
            sourceError = $"{ex.GetType().Name}: {ex.Message}";
        }

        // ★ 顺序（2026-11-13 第三轮，用户明确要求）：先落 running 头 → 再写四类数据 →
        // 全部成功才标 success 并发布。中途崩溃留下的是 running，**永远不会**被榜面选中；
        // 反过来的顺序（先写数据后标状态）会留下没有轮次头的孤儿行，事后无从判断算不算数。
        var started = false;
        try
        {
            await _milkPicks.StartRunAsync(run, today, plan.Mode.ToString().ToLowerInvariant(), plan.Reason, ct);
            started = true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await _milkPicks.LogAsync("milk:run", "FAIL", 0,
                $"[{run.RunId}] 轮次头写入失败，本轮不发布（榜面保持上一轮）："
                + $"{ex.GetType().Name}: {ex.Message}", CancellationToken.None);
        }

        var steps = new MilkRunSteps();

        var pickRows = 0;
        if (plan.Mode != MilkBoardMode.Rewrite)
        {
            // Skip / Partial 都是「一行不动」。但两者原因不同，日志必须分开：
            // Skip = 没取得可用期号；Partial = 期号确认了、采集不完整。
            steps.Ok("picks");
            await _milkPicks.LogAsync("milk:picks",
                plan.Mode == MilkBoardMode.Partial ? "PARTIAL" : "SKIP", 0,
                $"[{run.RunId}] {plan.Reason}；本轮不写不删，注单 0 不等于今天没有推荐",
                CancellationToken.None);
        }
        else
        {
            try
            {
                foreach (var w in plan.Writes)
                    pickRows += await _milkPicks.SaveDayAsync(w.SalesDate, w.Picks, run.RunId, ct);
                steps.Ok("picks");

                await _milkPicks.LogAsync("milk:picks", "OK", pickRows,
                    $"[{run.RunId}] {plan.Reason}"
                    + $"，受注日 {string.Join(",", plan.Writes.Select(w => w.SalesDate.ToString("MM-dd")))}"
                    + $"；本轮写库 {pickRows} 行（旧轮的行保留，榜面只显示本轮）",
                    CancellationToken.None);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // 注单写失败 = 这一轮不完整，不许发布；否则用户看到的是半份榜。
                steps.Fail("picks", ex);
                await _milkPicks.LogAsync("milk:picks", "FAIL", pickRows,
                    $"[{run.RunId}] 注单写入失败（本轮不发布）：{ex.GetType().Name}: {ex.Message}",
                    CancellationToken.None);
            }
        }

        // 方向闸的诊断与榜同批落库（2026-11-05）。存下来才回答得了
        // 「11-04 那三场当时模型指哪、市场指哪」——快照一滚就再也追不回来了。
        // 2026-11-13 第二轮：按 RunId 追加 + 保留历史，同一场不再互相覆盖。
        var gateRows = 0;
        try { gateRows = await _milkPicks.SaveGateAsync(DateTime.Today, build.Gates, run.RunId, ct); steps.Ok("gate"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // 闸门是「这场为什么没发」的唯一凭证，属于四类必须齐全的落库之一：
            // 写不进去这一轮就不发布，否则会出现「榜更新了、诊断还停在上一轮」。
            steps.Fail("gate", ex);
            await _milkPicks.LogAsync("milk:gate", "FAIL", 0,
                $"[{run.RunId}] 方向闸写入失败（本轮不发布）：{ex.GetType().Name}: {ex.Message}",
                CancellationToken.None);
        }

        // 单列候选判定与榜同批落库（2026-11-08）：判定原因（模型否决 / 比值越界 / 联赛无基线）
        // 是出榜那一刻的模型算出来的，模型每天重拟合，事后还原不出「当时为什么没选它」。
        // 候选列表本身仍不落库（赔率+赛果的纯派生视图），只存判据和结论。
        try { await _milkPicks.SaveCounterCandidatesAsync(DateTime.Today, build.CounterCandidates, ct); }
        catch (OperationCanceledException) { throw; }
        catch { /* 同上：判定表写不进去不能挡住出票 */ }

        // 完整分布存档 + 输入凭证（2026-11-12 加，2026-11-13 加凭证与日志）。
        //
        // 为什么两件事要一起写：存档回答「这场算出了什么」，凭证回答「这场到底有没有算」。
        // 只有存档时，一次空跑就能把两者一起抹掉，事后分不清「没算」和「写了但丢了」。
        //
        // 为什么不再用裸 `catch { }`：11-13 那张表被清空之后，库里没有任何一行能说明
        // 是「本次 0 行」还是「写坏了」。现在无论成败都往 SyncLog 写一行，
        // 带输入场次、档位行数、实际写入行数、版本戳与异常原文。
        //
        // 2026-11-13 第二轮：两者都按 RunId 写、历史保留。
        // 「本轮 0 档」不再靠「一行没写」来表达——那与「写坏了」长得一样；
        // 改由本轮在 FbMilkRun 里留一条 SnapshotRows=0 的记录，验收按轮一查便知。
        var inputTag = run.VersionTag;
        var inputRows = 0;
        var snapRows = 0;
        var sourceRows = 0;
        try
        {
            inputRows = await _milkPicks.SaveInputsAsync(today, build.Inputs, run.RunId, ct);
            steps.Ok("input");
            snapRows = await _milkPicks.SaveProbSnapshotsAsync(today, build.ProbSnapshots, run.RunId, ct);
            steps.Ok("snapshot");
            var note = build.ProbSnapshots.Count == 0 ? "；本轮 0 档，不写存档（上一轮的行原样保留）" : "";
            await _milkPicks.LogAsync("milk:snapshot", "SUCCESS", snapRows,
                $"[{run.RunId}] 输入 {build.Inputs.Count} 场 / 凭证 {inputRows} 行；"
                + $"档位 {build.ProbSnapshots.Count} 行 / 存档 {snapRows} 行{note}；版本 {inputTag}",
                CancellationToken.None);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // 失败同样要留下能查的一行——11-13 那张表被清空时，库里没有任何一行能说明
            // 是「本次 0 行」还是「写坏了」。这一轮也不会发布（见下面的发布判据）。
            steps.Fail(inputRows == 0 ? "input" : "snapshot", ex);
            await _milkPicks.LogAsync("milk:snapshot", "FAIL", 0,
                $"[{run.RunId}] 输入 {build.Inputs.Count} 场 / 档位 {build.ProbSnapshots.Count} 行 写入失败"
                + $"（本轮不发布）：{ex.GetType().Name}: {ex.Message}", CancellationToken.None);
        }

        // 源侧账本单独一步（2026-11-14）：它记录「源里每一场算没算、没算凭什么」，
        // 写不进去就等于这一轮无法证明覆盖，必须单独判失败，不能混进存档那一步的原因里。
        try
        {
            sourceRows = await _milkPicks.SaveSourceAsync(run.RunId, today, source, build.Rejections, ct);
            steps.Ok("source");
            await _milkPicks.LogAsync("milk:source", "OK", sourceRows,
                $"[{run.RunId}] 源侧 {source.Count} 场 / 账本 {sourceRows} 行"
                + $"（算 {build.Inputs.Count} / 拒 {build.Rejections.Count}）"
                + $"；采样最新 {(source.Count == 0 ? "-" : source.Max(m => m.LastSampleAt)?.ToString("MM-dd HH:mm") ?? "-")}",
                CancellationToken.None);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            steps.Fail("source", ex);
            await _milkPicks.LogAsync("milk:source", "FAIL", 0,
                $"[{run.RunId}] 源侧账本写入失败（本轮不发布，无法证明覆盖）："
                + $"{ex.GetType().Name}: {ex.Message}", CancellationToken.None);
        }

        // 轮次头（2026-11-13 第二轮）：本轮算了几场、出了几注、走的是哪种模式，落一行。
        // 没有它，「全被过滤」的一轮在库里只表现为「四张表都没变化」，与「作业没跑」无法区分。
        // 收尾（2026-11-13 第三轮）：**四类全部成功才发布**；否则标成失败/跳过，榜面继续显示上一轮。
        //
        // 「发布」= 在一个事务里写 `FbMilkRunDate`（含 0 注的受注日）并把头标成 success。
        // 榜面读的是 `FbMilkRunDate ⋈ FbMilkRun(Status='success')`，失败轮 RunId 再大也选不上。
        if (started)
        {
            try
            {
                if (plan.Mode != MilkBoardMode.Rewrite)
                {
                    await _milkPicks.CloseRunAsync(run.RunId, plan.RunStatus, plan.Reason,
                        build.Inputs.Count, snapRows, gateRows, 0, CancellationToken.None);
                    await _milkPicks.LogAsync("milk:run", plan.Mode == MilkBoardMode.Partial ? "PARTIAL" : "SKIP", 0,
                        $"[{run.RunId}] 未发布（{plan.RunStatus}）：{plan.Reason}", CancellationToken.None);
                }
                else if (!steps.AllOk("picks", "gate", "input", "snapshot", "source")
                         || !MilkRunSteps.SnapshotCoversInputs(build.Inputs.Count, snapRows))
                {
                    var extra = MilkRunSteps.SnapshotCoversInputs(build.Inputs.Count, snapRows)
                        ? steps.Detail
                        : $"存档缺失：输入 {build.Inputs.Count} 场但存档 0 行。{steps.Detail}";
                    await _milkPicks.CloseRunAsync(run.RunId, MilkRunStatus.Failed,
                        $"落库未全部成功：{steps.Summary}。{extra}",
                        build.Inputs.Count, snapRows, gateRows, pickRows, CancellationToken.None);
                    await _milkPicks.LogAsync("milk:run", "FAIL", pickRows,
                        $"[{run.RunId}] 未发布（failed）：{steps.Summary}；{extra}", CancellationToken.None);
                }
                else
                {
                    // ★ 2026-11-13 第四轮：发布前**逐场逐玩法逐档位**核对（用户明确要求）。
                    //
                    // 上一版的判据是「四步都 ok + input>0 ⇒ snapshot>0」，那只是两个总数：
                    // 少写了一半档位、或者同一档写了两行，总数照样可能「看起来正常」。
                    // 而榜面读的就是重写后的行——少的那几档当场从榜上消失，多的那几行让同一注出现两次。
                    // 所以判据改成「预期账本 vs 库里读回的实际账本」逐键比对，任何缺失/重复/多写都判 failed。
                    MilkLedgerReport? ledger = null;
                    string? ledgerError = null;
                    try
                    {
                        ledger = await VerifyRunLedgerAsync(
                            run.RunId, plan, build, source, sourceError, runAt, ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { ledgerError = $"{ex.GetType().Name}: {ex.Message}"; }

                    if (ledger is null || !ledger.Ok)
                    {
                        var why = ledger is null
                            ? $"逐档核对无法执行（{ledgerError}）"
                            : $"逐档核对不通过（缺失 {ledger.Missing}、重复 {ledger.Duplicated}、多写 {ledger.Unexpected}"
                              + $"、豁免/拒绝不成立 {ledger.ExemptDefects}）：{ledger.Detail}";
                        await _milkPicks.CloseRunAsync(run.RunId, MilkRunStatus.Failed,
                            $"落库核对失败：{why}",
                            build.Inputs.Count, snapRows, gateRows, pickRows, CancellationToken.None);
                        await _milkPicks.LogAsync("milk:verify", "FAIL", pickRows,
                            $"[{run.RunId}] {why}；{steps.Summary}；本轮不发布，榜面保持上一轮",
                            CancellationToken.None);
                    }
                    else
                    {
                        var dates = plan.Writes
                            .Select(w => new MilkRunDateCount { SalesDate = w.SalesDate, PickCount = w.Picks.Count })
                            .ToList();
                        await _milkPicks.PublishRunAsync(run.RunId, dates,
                            build.Inputs.Count, snapRows, gateRows, pickRows, plan.Reason, ct);
                        await _milkPicks.LogAsync("milk:run", "SUCCESS", pickRows,
                            $"[{run.RunId}] 已发布：{plan.Reason}；受注日 "
                            + string.Join(",", dates.Select(x => $"{x.SalesDate:MM-dd}({x.PickCount}注)"))
                            + $"；{steps.Summary}；逐档核对 {ledger.Summary}；{ledger.ExemptSummary}"
                            // 采集口径要写全：`Batch >= Known` 只到「与库内已知集合一致」，
                            // 不是「源站没漏场」，也不是「赔率新鲜」。日志里不写清，事后会被当成保证。
                            + $"；采集 {capture.IssueText}（与库内已知集合一致，非源站基准；"
                            + $"批次 {capture.BatchAt:MM-dd HH:mm} 不代表赔率新鲜）",
                            CancellationToken.None);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // 发布失败（含事务中途失败）：尽力把这一轮标成 failed，别让它停在 running。
                try
                {
                    await _milkPicks.CloseRunAsync(run.RunId, MilkRunStatus.Failed,
                        $"发布失败：{ex.GetType().Name}: {ex.Message}", ct: CancellationToken.None);
                }
                catch { /* 连标记都失败时，running 本身也不会被榜面选中 */ }

                await _milkPicks.LogAsync("milk:run", "FAIL", 0,
                    $"[{run.RunId}] 发布失败（榜面保持上一轮）：{ex.GetType().Name}: {ex.Message}",
                    CancellationToken.None);
            }
        }

        // 票只装**当日受注**的那批注（2026-11-10 随上面的分组一起改）。
        // 跨期混票会把「明天才开售/才停售」的场次塞进今天的方案：用户照着买，
        // 其中一半根本不在今天这一期里。榜可以同时挂着两期（页面按日期分区显示），
        // 但购买方案必须只对应一期，否则它既不是今天的方案也不是明天的。
        var todayPicks = plan.Writes.FirstOrDefault(w => w.SalesDate == today)?.Picks
                         ?? (IReadOnlyList<MilkPick>)Array.Empty<MilkPick>();
        var draft = MilkPickEngine.BuildTicket(todayPicks, today);
        if (draft is null) return;
        await _tickets.UpsertAsync(draft, ct);
    }

    /// <summary>
    /// 发布前的逐档核对（2026-11-13 第四轮；第五轮把预期改到输入侧；第七轮加源侧账本）。
    ///
    /// 预期账本：
    ///   pick     = plan.Writes 里每一期的每一注（键含受注日）
    ///   gate     = build.Gates（一场一行）
    ///   snapshot = **<see cref="MilkExpectedSlate"/> 独立推出的档位**（输入 × 玩法规则），
    ///              不是 `build.ProbSnapshots`——拿引擎输出当预期，引擎漏了永远发现不了
    ///   input    = build.Inputs（一场一行，与 SaveInputsAsync 的去重口径一致）
    ///   source   = **源侧快照**（BettingCard × 本轮受注规则），本轮写了一行还是拒了一行
    /// 实际账本按 `RunId` 从五张表读回，逐键比对。读的是**本轮的行**，
    /// 所以上一轮的历史不会让核对结果看起来「多出来」。
    ///
    /// 第七轮补的两道判据（都是 11-14 验收暴露出来的）：
    ///   · <see cref="MilkSourceSlate"/>：源侧每一场必须有凭证或带证据的拒绝记录；
    ///     算过的场次还要过「采样不超龄」与「源侧每一档都进了计算」两道；
    ///   · <see cref="MilkProbScale"/>：每个玩法每列概率合计必须是 100%（BF 小数当百分数即在此拦下）。
    ///
    /// 核对失败一律判 failed（不发布）：这几张表的行数完全由本轮输入决定，
    /// 「少写一半」与「写坏了」在榜面上的表现一模一样——都是榜上少了几注。
    /// </summary>
    private async Task<MilkLedgerReport> VerifyRunLedgerAsync(
        string runId, MilkBoardPlan plan, MilkBuildResult build,
        IReadOnlyList<MilkSourceMatch> source, string? sourceError, DateTime runAt,
        CancellationToken ct)
    {
        var actual = await _milkPicks!.ReadRunLedgerAsync(runId, ct);

        List<MilkLedgerRow> Act(string table) => actual
            .Where(a => a.Table == table)
            .Select(a => new MilkLedgerRow { Key = a.Key, Count = a.Count })
            .ToList();

        var picks = plan.Writes
            .SelectMany(w => MilkRunLedger.FromPicks(w.SalesDate, w.Picks))
            .ToList();

        // 独立预期：输入里有哪些档 × 玩法规则允许哪些档。引擎少生成一场/一档都会在这里显形。
        var slate = MilkExpectedSlate.Build(build.Inputs, build.SlateExemptions);
        var pre = MilkRunLedger.UnlandablePicks(plan.Writes.SelectMany(w => w.Picks));

        // 不被接受的「合法缺项」不当缺项：直接判失败，并逐条说明是哪一环不成立
        // （原因不在枚举 / 缺证据 / 证据对不上 / 该原因本身不接受）。否则那条路会变成万能挡箭牌。
        foreach (var e in slate.Rejected)
            pre.Add(new MilkLedgerIssue
            {
                Table = "snapshot", Kind = "rejected-exemption",
                Key = $"{e.Key}|{e.Reason}|{e.Defect}", Expected = 0, Actual = 0
            });

        // 赔率缺失不能借豁免绕过推荐限制：no-odds 与该场出票不能同时成立。
        pre.AddRange(MilkExpectedSlate.CrossCheck(slate, plan.Writes.SelectMany(w => w.Picks)));

        // 源侧账本（2026-11-14）：场次集合与拒绝证据。
        // 「这个队名到底是不是新队」由独立库检回答（FbMatch 近 18 个月按核心名计数），
        // 引擎自己说「我不认识」不能当证据。名字先一次性查好，再交给纯函数判定——
        // 不把 IO 塞进判定里，判定才能单测。
        var unresolved = build.Rejections
            .Where(r => string.Equals(r.Reason, MilkInputRejection.ReasonNameUnresolved, StringComparison.Ordinal))
            .SelectMany(r => new[] { r.HomeTeam, r.AwayTeam })
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Dictionary<string, int> games = new(StringComparer.Ordinal);
        if (unresolved.Count > 0)
        {
            try { games = await _repo.GetRecentTeamGamesAsync(unresolved, ct: ct); }
            catch (OperationCanceledException) { throw; }
            catch { games = new(StringComparer.Ordinal); }   // 查不到 → 全部按「无法证明」处理
        }
        var gamesSnapshot = games;
        var sourceReport = MilkSourceSlate.Verify(
            source, build.Inputs, build.Rejections, runAt,
            name => !string.IsNullOrWhiteSpace(name) && gamesSnapshot.TryGetValue(name!.Trim(), out var n) ? n : -1,
            // 门槛只有一份（2026-11-14 第九轮）：核对器这里的值曾经是 5、引擎出票门槛是 3，
            // 一支 4 场样本的队会被引擎算进来、又被核对器当「真新队」放行。
            // 现在两边都读 `MilkThresholds.MinTeamGames`（= 引擎线上实际生效的 3）。
            minTeamGames: MilkThresholds.MinTeamGames);
        pre.AddRange(sourceReport.Issues);
        if (!string.IsNullOrWhiteSpace(sourceError))
            pre.Add(new MilkLedgerIssue
            {
                Table = "source", Kind = "source-unavailable", Key = sourceError, Expected = 1, Actual = 0
            });

        // 概率单位/归一（2026-11-14）：BF 曾把 0~1 的小数写进「%」列，
        // 逐场合计 1.00 对 99.95，任何按这张表算的汇总都会差 100 倍。
        pre.AddRange(MilkProbScale.Verify(build.ProbSnapshots));

        return MilkRunLedger.Verify(
            new[]
            {
                new MilkLedgerPair { Table = "pick", Expected = picks, Actual = Act("pick") },
                new MilkLedgerPair { Table = "gate", Expected = MilkRunLedger.FromGates(build.Gates), Actual = Act("gate") },
                new MilkLedgerPair
                {
                    Table = "snapshot", Expected = slate.Rows, Actual = Act("snapshot"),
                    Ignore = slate.IgnoreKeys
                },
                new MilkLedgerPair { Table = "input", Expected = MilkRunLedger.FromInputs(build.Inputs), Actual = Act("input") },
                new MilkLedgerPair
                {
                    Table = "source",
                    Expected = source.Select(m => new MilkLedgerRow { Key = MilkRunLedger.Keys.Input(m.MatchId) }).ToList(),
                    Actual = Act("source")
                }
            },
            pre,
            // 豁免必须连证据一起进日志：只写「有 3 条豁免」，事后无法判断当时成不成立。
            $"{slate.Summary}；豁免明细 {slate.Detail}；{sourceReport.Summary}；拒绝明细 {Reject(sourceReport.Rejections)}");
    }

    private static string Reject(IReadOnlyList<MilkInputRejection> rows)
    {
        if (rows.Count == 0) return "无";
        var head = rows.Take(8).Select(r => r.ToString());
        var more = rows.Count > 8 ? $"；另有 {rows.Count - 8} 条" : string.Empty;
        return string.Join("；", head) + more;
    }

    /// <summary>
    /// 本次重算覆盖到的受注日（2026-11-13 加）。
    ///
    /// 口径是「参与计算的场次属于哪一期」，不是「今天」。原因见 SaveMilkAsync 里的注释：
    /// 22:00 之后重算会同时算出次日期的场次，用 Today 一刀切会把停售后定版的当期票删掉。
    ///
    /// 返回空集表示**这次没有任何输入**——调用方必须据此什么都不做，
    /// 不能把「采集中断 / 期号还没放出来」当成「今天没有推荐」去清榜。
    /// </summary>
    internal static List<DateTime> SalesDatesFor(IReadOnlyList<MatchRecommendation>? recs, DateTime today)
    {
        if (recs is null || recs.Count == 0) return new List<DateTime>();
        return recs
            .Select(r => SteadyPickRules.SalesDate(
                string.IsNullOrWhiteSpace(r.MatchNo) ? null : r.MatchNo, r.MatchTime, today))
            .Distinct()
            .OrderBy(x => x)
            .ToList();
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
        var sidecarErrors = new List<string>();
        try { await SettleTicketsAsync(ct); }
        catch (Exception ex) { sidecarErrors.Add(DescribeSidecarFailure("票面台账", ex)); }

        // 否决记录也用同一份赛果判定：「当时照打会不会中」。
        try { await SettleVetoesAsync(ct); }
        catch (Exception ex) { sidecarErrors.Add(DescribeSidecarFailure("否决记录", ex)); }

        // 大奶台账：比分/半全场/总进球对赛果判中。半场比分没采到的场留着下轮。
        try { if (_milkPicks is not null) await _milkPicks.SettleAsync(ct); }
        catch (Exception ex) { sidecarErrors.Add(DescribeSidecarFailure("大奶台账", ex)); }

        return new SettlementResult
        {
            Total = total,
            Hit = hitCount,
            HitRate = total > 0 ? Math.Round(100.0 * hitCount / total, 1) : 0,
            ByBoard = byBoard,
            Items = settled,
            // 旁路失败的痕迹必须跟着结果一起返回，由作业写进 SyncLog。
            // 只往 Console 打是不够的：这个进程的 stdout/stderr 走计划任务，
            // 按天日志是程序自己写的结构化文件，两者不通。
            SidecarErrors = sidecarErrors
        };
    }

    /// <summary>
    /// 旁路台账结算失败时的统一留痕（2026-11-10 加）。
    ///
    /// 旁路失败不该拖垮复盘——这条设计没错。错的是原来写成 <c>catch (Exception) { }</c>，
    /// 一个字节都不留：SQL 写错（列名不存在）、表结构漂移这类**永久性失败**，
    /// 和「表还没建」的一次性情况长得一模一样，全都表现为
    /// 「Job 全绿、复盘正常、只有台账再也不更新」。
    ///
    /// 实测代价：11-10 给 FbMilkPick 加 Handicap 列时，结算先跑到、补列没跑到，
    /// SELECT 直接报列名无效，异常被这个空 catch 吃掉 —— 台账静默停摆，
    /// 而 SyncLog 里 settle-hourly 全程 SUCCESS。发现它靠的是去查列存不存在，
    /// 不是靠日志。所以这里返回文字，由调用方拼进 SyncLog 的 message。
    /// </summary>
    private static string DescribeSidecarFailure(string what, Exception ex)
        => $"{what}结算失败（复盘继续）：{ex.GetType().Name}: {ex.Message}";
}

/// <summary>复盘结果</summary>
public sealed class SettlementResult
{
    public int Total { get; set; }
    public int Hit { get; set; }
    public double HitRate { get; set; }
    public List<BoardSettlement> ByBoard { get; set; } = new();
    public List<SettlementItem> Items { get; set; } = new();
    /// <summary>旁路台账（票面/否决/大奶）结算失败的描述。空表示全部结算正常。</summary>
    public List<string> SidecarErrors { get; set; } = new();
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
