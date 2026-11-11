using System.Net;
using AiFootBall.Core.Models;
using AiFootBall.Core.Options;
using AiFootBall.Data;

namespace AiFootBall.Sources;

/// <summary>采集结果</summary>
public sealed record CollectionResult(bool Success, int Rows, string Message)
{
    public static CollectionResult Ok(int rows, string message) => new(true, rows, message);
    public static CollectionResult Fail(string message) => new(false, 0, message);

    /// <summary>
    /// 失败但仍要如实报出写入行数。全失败时通常就是 0，但「部分批次成功后才被打断」
    /// 也会走到这里——行数不能因为结论是失败就抹成 0，否则又变成另一种口径失真。
    /// </summary>
    public static CollectionResult Fail(int rows, string message) => new(false, rows, message);

    /// <summary>
    /// 「本轮有事要做，却一场都没成功」——只有这种才判失败。
    ///
    /// 部分失败仍算成功：反爬抖动是常态，逐次报红会把真问题淹掉，也会把人训练成不看告警。
    /// 必须 done &gt; 0：没有待采场次（done=0, failed=0）是正常闲时，绝不能判失败。
    /// </summary>
    public static bool AllFailed(int done, int failed) => done > 0 && failed == done;

    /// <summary>
    /// 本轮是否该因为「token 已被打回验证状态」提前中止。
    ///
    /// 必须同时满足两条：**本轮零产出** 且 **本轮拦截达到阈值**。
    ///
    /// 只按拦截次数判会误伤：无盘口场次本来就会被拦，偶发几次是常态，
    /// 那时中止等于自己把还能采的场次丢掉。反过来只按零产出判更糟——
    /// 凌晨大批小联赛本来就没有必发盘口，零产出是正常结果，不能当故障。
    ///
    /// 零产出 **且** 连续被拦，才是「这个 token 已经不能用了」的硬信号：
    /// 一整轮跑下来一行都没落库，继续跑只是逐场白等 10 秒冷却。
    /// 2026-11-10 实测：夜间没有这条短路时，odds-sample 每轮跑满 10 分钟、
    /// exchange 跑满 15 分钟、odds-sample-late 跑满 8 分钟，三个作业轮流把
    /// 单槽主通道占死，water-sample 一夜被看门狗判 STUCK 21 次。
    /// </summary>
    public static bool ShouldAbortOnVerify(int produced, long verifyDelta, int threshold = 6)
        => produced == 0 && verifyDelta >= threshold;

    /// <summary>
    /// 多段采集合成一个结果：**任意一段失败，整体就是失败**，行数相加，失败原因全部带上。
    ///
    /// water-sample 一轮里跑了两段（赔率轨迹 + 必发成交），原来无条件写
    /// <c>CollectionResult.Ok(resOdds.Rows + resEx.Rows, ...)</c>：
    /// 赔率段已经如实返回 Fail，这一句把结论丢掉了。三层链路
    /// （叶子 → 组合 → 作业）只要有一层用 Ok 无脑包装，上面修的全都白做。
    ///
    /// 不做「部分成功就算成功」：两段是两个不同的数据源，等赔率的和等必发的不是同一批人。
    /// 宁可让这一轮报红，也不要让必发断了三天没人知道。
    ///
    /// <para>
    /// 2026-11-10 晚：water-sample 拆成 water-sample（只跑赔率）+ exchange-late（只跑必发），
    /// 两段成了两个独立作业，各自把自己的结果写进 SyncLog，**不再有合并这一步**。
    /// 因此本方法目前没有生产调用者，只剩测试在用。保留是因为语义本身没错，
    /// 将来若再把两段合回一个作业可以直接复用。看到它没人调用不必惊讶，也不要
    /// 误以为「两段合成」还在生效——那件事已经不存在了。
    /// </para>
    /// </summary>
    public static CollectionResult Combine(string prefix, params CollectionResult[] parts)
    {
        var rows = 0;
        var failures = new List<string>();
        foreach (var p in parts)
        {
            rows += p.Rows;
            if (!p.Success && !string.IsNullOrWhiteSpace(p.Message)) failures.Add(p.Message);
        }

        var message = failures.Count == 0
            ? prefix
            : $"{prefix}；失败段：{string.Join("；", failures)}";
        return failures.Count == 0 ? Ok(rows, message) : Fail(rows, message);
    }
}

/// <summary>HttpClient 构造</summary>
public static class HttpClients
{
    public static HttpClient Create(CollectorOptions opt)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true,
            UseCookies = true,
            CookieContainer = BotGuard.Cookies,
            MaxConnectionsPerServer = Math.Max(8, opt.OddsConcurrency * 3)
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(opt.TimeoutSeconds) };
        // 若已装载 500.com 的验证 token，UA 必须与取 token 时一致（token 绑指纹）
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BotGuard.UserAgent ?? opt.UserAgent);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        return http;
    }
}

/// <summary>
/// 采集编排：彩票 / 足球赛事 / 赔率。CLI 与定时调度共用同一套流程。
/// </summary>
public sealed class CollectionService
{
    private readonly CollectorOptions _opt;
    private readonly HttpClient _http;
    private readonly LotteryRepository _lotto;
    private readonly FootballRepository _fb;
    private readonly OddsRepository _odds;
    private readonly ExchangeRepository _exchange;
    private readonly SyncLogRepository _sync;

    public CollectionService(HttpClient http, CollectorOptions opt, LotteryRepository lotto,
        FootballRepository fb, OddsRepository odds, ExchangeRepository exchange, SyncLogRepository sync)
    {
        _http = http;
        _opt = opt;
        _lotto = lotto;
        _fb = fb;
        _odds = odds;
        _exchange = exchange;
        _sync = sync;
    }

    /* ---------------- 彩票 ---------------- */

    public async Task<CollectionResult> CollectLotteryAsync(string? code, bool backfill = true,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        // 含停用彩种：七乐彩/七星彩不选号，但开奖要继续采，历史链不能断。
        var types = await _lotto.GetLotteryTypesAsync(includeInactive: true, ct);
        if (!string.IsNullOrWhiteSpace(code))
            types = types.Where(t => t.Code.Equals(code, StringComparison.OrdinalIgnoreCase)).ToList();

        if (types.Count == 0) return CollectionResult.Fail("未匹配到彩种");

        var pinble = new PinbleLotterySource(_http, _opt);
        var five = new FiveHundredLotterySource(_http);
        var total = 0;

        foreach (var lt in types)
        {
            ct.ThrowIfCancellationRequested();
            log?.Report($"===== {lt.Name}（{lt.Code}）=====");

            var all = new List<LotteryDrawRecord>();
            try
            {
                var rows = await pinble.FetchAsync(lt, ct);
                if (rows.Count > 0)
                    log?.Report($"  [pinble] {rows.Count} 期：{rows.MinBy(r => r.DrawDate)?.Issue} ~ {rows.MaxBy(r => r.DrawDate)?.Issue}");
                all.AddRange(rows);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log?.Report($"  [pinble] 失败：{ex.Message}");
            }

            if (backfill && FiveHundredLotterySource.Supports(lt.Code))
            {
                try
                {
                    var rows = await five.FetchAsync(lt, ct);
                    if (rows.Count > 0)
                        log?.Report($"  [500]    {rows.Count} 期：{rows.MinBy(r => r.DrawDate)?.Issue} ~ {rows.MaxBy(r => r.DrawDate)?.Issue}");
                    all.AddRange(rows);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log?.Report($"  [500]    失败：{ex.Message}");
                }
            }

            // 同期号合并：号码以 pinble 为准，销售额/奖池/奖级用 500 补齐
            var normalized = all
                .GroupBy(r => r.Issue)
                .Select(g =>
                {
                    var primary = g.OrderBy(r => r.Source == pinble.Name ? 0 : 1).First();
                    var richest = g.OrderByDescending(r =>
                        (r.SalesAmount.HasValue ? 1 : 0) + (r.PoolAmount.HasValue ? 1 : 0)).First();
                    return new LotteryDrawRecord
                    {
                        LotteryCode = primary.LotteryCode,
                        Issue = primary.Issue,
                        DrawDate = primary.DrawDate,
                        Reds = primary.Reds,
                        Blues = primary.Blues,
                        SalesAmount = primary.SalesAmount ?? richest.SalesAmount,
                        PoolAmount = primary.PoolAmount ?? richest.PoolAmount,
                        Prizes = primary.Prizes.Count > 0 ? primary.Prizes : richest.Prizes,
                        Source = primary.Source
                    };
                })
                .ToList();

            if (normalized.Count == 0)
            {
                log?.Report("  无新增数据");
                continue;
            }

            var written = await _lotto.UpsertDrawsAsync(lt, normalized, ct);
            total += written;
            log?.Report($"  入库 {written} 期（解析 {all.Count} 条，去重 {normalized.Count} 期）");
            await _sync.WriteAsync($"lotto:{lt.Code}", pinble.Name, "SUCCESS", written, null, ct);

            await Task.Delay(_opt.RequestDelayMs, ct);
        }

        return CollectionResult.Ok(total, $"彩票采集完成，入库 {total} 期");
    }

    /* ---------------- 足球赛事 ---------------- */

    public async Task<CollectionResult> CollectFootballAsync(IReadOnlyList<DateOnly> dates,
        IProgress<string>? log = null, CancellationToken ct = default, bool resultsOnly = false)
    {
        var source = new FiveHundredMatchSource(_http, _opt);
        var total = 0;
        var failed = 0;

        foreach (var date in dates.OrderBy(x => x))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (matches, leagues, teams) = await source.FetchByDateAsync(date, resultsOnly, ct);
                if (matches.Count == 0)
                {
                    log?.Report($"[{date:yyyy-MM-dd}] 无赛事（可能非比赛日）");
                    await Task.Delay(_opt.RequestDelayMs, ct);
                    continue;
                }

                var (_, _, mc) = await _fb.UpsertAsync(leagues, teams, matches, ct);
                total += matches.Count;
                log?.Report($"[{date:yyyy-MM-dd}] {matches.Count} 场（完场 {matches.Count(m => m.IsFinished)}），" +
                            $"联赛 {leagues.Count}，球队 {teams.Count}，入库 {mc}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                log?.Report($"[{date:yyyy-MM-dd}] 抓取失败：{ex.Message}");
            }

            await Task.Delay(_opt.RequestDelayMs, ct);
        }

        await _sync.WriteAsync("football", "500", failed == 0 ? "SUCCESS" : "FAILED", total,
            failed > 0 ? $"{failed} 天抓取失败" : null, ct);

        return CollectionResult.Ok(total, $"足球采集完成，累计 {total} 场");
    }

    /* ---------------- 赔率 ---------------- */

    /// <summary>
    /// 采集指定时间窗口内赛事的赔率（欧赔/亚盘/大小球，初盘 + 终盘）。
    /// </summary>
    /// <param name="onlyMissing">true=只补没采过的；false=全部重采（赛前滚动刷新用）</param>
    /// <param name="sampleAt">非空时把本次「即时盘」作为时间序列点写入（赔率变化轨迹）</param>
    public async Task<CollectionResult> CollectOddsAsync(DateTime from, DateTime to, bool onlyMissing,
        int limit = 0, IProgress<string>? log = null, DateTime? sampleAt = null,
        CancellationToken ct = default)
    {
        var matches = await _odds.GetMatchIdsForOddsAsync(from, to, onlyMissing, limit, ct);
        if (matches.Count == 0)
        {
            log?.Report("没有需要采集赔率的赛事");
            return CollectionResult.Ok(0, "无待采集赛事");
        }

        log?.Report($"待采集 {matches.Count} 场（{from:MM-dd HH:mm} ~ {to:MM-dd HH:mm}，{(onlyMissing ? "仅缺失" : "全部刷新")}）");

        var source = new FiveHundredOddsSource(_http, _opt);
        var concurrency = Math.Max(1, _opt.OddsConcurrency);
        var batchSize = Math.Max(concurrency, _opt.OddsBatchSize);

        int rows = 0, done = 0, withOdds = 0, noData = 0, failed = 0;
        var verifyBefore = OddsHttp.StatVerify;
        var abortedByVerify = false;

        for (var offset = 0; offset < matches.Count; offset += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var slice = matches.Skip(offset).Take(batchSize).ToList();
            var results = new List<MatchOddsResult>(slice.Count);
            var gate = new object();
            var inBatch = 0;

            await Parallel.ForEachAsync(slice,
                new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = ct },
                async (m, token) =>
                {
                    MatchOddsResult r;
                    try
                    {
                        r = await source.FetchAsync(m.MatchId, token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        r = new MatchOddsResult { MatchId = m.MatchId, Status = 2, Error = ex.Message };
                    }

                    lock (gate) results.Add(r);

                    // 每 10 场输出一次心跳，便于长时间回补时观察进度
                    if (Interlocked.Increment(ref inBatch) % 10 == 0)
                        log?.Report($"    …本批 {inBatch}/{slice.Count}，累计 {done + results.Count}/{matches.Count}" +
                                    $"（节流 {FiveHundredOddsSource.ThrottleIntervalMs}ms，" +
                                    $"成功 {FiveHundredOddsSource.StatOk} / 428 {FiveHundredOddsSource.Stat428} / " +
                                    $"429 {FiveHundredOddsSource.Stat429} / 挑战 {FiveHundredOddsSource.StatChallenge}）");
                });

            rows += await _odds.UpsertAsync(results, sampleAt, ct);

            done += results.Count;
            withOdds += results.Count(r => r.Status == 1);
            noData += results.Count(r => r.Status == 0);
            failed += results.Count(r => r.Status == 2);

            foreach (var err in results.Where(r => !string.IsNullOrEmpty(r.Error)).Take(3))
                log?.Report($"  ! {err.MatchId} {err.Error}");

            log?.Report($"  进度 {done}/{matches.Count}：有赔率 {withOdds}，无数据 {noData}，失败 {failed}，累计 {rows} 行（节流 {FiveHundredOddsSource.ThrottleIntervalMs}ms）");

            // token 被打回验证状态时，继续跑只是空转：每场白等 30 秒冷却，还会把场次标成"无数据"。
            // 连续 6 次拦截即中止本轮回补，交给 TokenHealthService 续期后由下一轮继续。
            if (OddsHttp.StatVerify - verifyBefore >= 6)
            {
                abortedByVerify = true;
                log?.Report($"  ! 已连续被人机验证拦截 {OddsHttp.StatVerify - verifyBefore} 次，中止本轮剩余 {matches.Count - done} 场，等待 token 自动续期");
                break;
            }
        }

        var verify = OddsHttp.StatVerify;
        var message = $"赔率采集完成：{done} 场（有赔率 {withOdds} / 无数据 {noData} / 失败 {failed}），写入 {rows} 行"
                      + (abortedByVerify
                          ? $"；token 失效已提前中止（本轮拦截 {verify - verifyBefore} 次），等待自动续期"
                          : verify > verifyBefore
                              ? $"；验证拦截 {verify - verifyBefore} 次"
                              : "");
        // 与 SampleOddsAsync 同一条口径：全部失败时，内层记 FAILED 还不够——
        // 返回 Ok 会让作业层（job:odds-backfill）写 SUCCESS，两套状态各说各话。
        // 11-06~11-10 每天凌晨都是这个形态：odds FAILED + job:odds-backfill SUCCESS 0 行。
        var allFailed = CollectionResult.AllFailed(done, failed);

        await _sync.WriteAsync("odds", source.Name, allFailed ? "FAILED" : "SUCCESS",
            rows, message, ct);
        log?.Report(message);

        return allFailed ? CollectionResult.Fail(rows, message) : CollectionResult.Ok(rows, message);
    }

    /// <summary>
    /// 赔率轨迹采样：时间窗内的场次 + 当前在售受注场，落库时同步写入 FbOddsHistory，
    /// 由此得到「初盘 → 赛前 → 开赛」的完整赔率变化曲线。
    /// </summary>
    /// <param name="hoursAhead">向前看多少小时内的赛事（在售场即使更远也会采）</param>
    /// <param name="hoursBehind">向后包含多少小时（捕获开赛瞬间与滚球初段）</param>
    /// <param name="recheckMinutes">同一场两次采样最小间隔</param>
    public async Task<CollectionResult> SampleOddsAsync(double hoursAhead, double hoursBehind,
        int recheckMinutes, int limit, IProgress<string>? log = null, CancellationToken ct = default,
        bool euroOnly = false, string? logTask = null, bool inplayOnly = false)
    {
        var now = DateTime.Now;
        var from = now.AddHours(-hoursBehind);
        var to = now.AddHours(hoursAhead);

        var matches = await _odds.GetMatchesForSamplingAsync(from, to, limit, recheckMinutes,
            ct: ct, inplayOnly: inplayOnly);
        if (matches.Count == 0)
        {
            log?.Report($"无需采样（{from:MM-dd HH:mm}~{to:MM-dd HH:mm}，间隔 {recheckMinutes} 分钟）");
            return CollectionResult.Ok(0, "无待采样赛事");
        }

        log?.Report($"赔率轨迹采样 {matches.Count} 场（{from:MM-dd HH:mm}~{to:MM-dd HH:mm}，间隔 {recheckMinutes} 分钟）");

        var source = new FiveHundredOddsSource(_http, _opt);
        var concurrency = Math.Max(1, _opt.OddsConcurrency);
        var batchSize = Math.Max(concurrency, _opt.OddsBatchSize);
        var sampleAt = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);

        int points = 0, done = 0, failed = 0;
        var verifyBefore = OddsHttp.StatVerify;
        // 本轮真实发生的 HTTP 成功次数。用途只有归因：
        // 30 场 91 秒到底是「场次多」还是「每场请求多」，光看耗时永远分不清。
        // 有了这个数，91 秒 = 95 次请求 × 900ms 一眼就能算平；
        // 没有它，第一次看到 91 秒的人只会去调 Limit 或并发，而并发改不动节流。
        var okBefore = OddsHttp.StatOk;
        var abortedByVerify = false;

        for (var offset = 0; offset < matches.Count; offset += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var slice = matches.Skip(offset).Take(batchSize).ToList();
            var results = new List<MatchOddsResult>(slice.Count);
            var gate = new object();

            await Parallel.ForEachAsync(slice,
                new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = ct },
                async (m, token) =>
                {
                    MatchOddsResult r;
                    try
                    {
                        r = await source.FetchAsync(m.MatchId, token, euroOnly);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        r = new MatchOddsResult { MatchId = m.MatchId, Status = 2, Error = ex.Message };
                    }
                    lock (gate) results.Add(r);
                });

            points += await _odds.UpsertAsync(results, sampleAt, ct);
            done += results.Count;
            failed += results.Count(r => r.Status == 2);
            log?.Report($"  采样进度 {done}/{matches.Count}，失败 {failed}（节流 {OddsHttp.ThrottleIntervalMs}ms）");

            // 和 CollectOddsAsync 同一条短路，但判据更严（零产出 + 拦截达阈值）。
            // 采样这条路原来完全没有短路：token 被打掉之后，它仍然逐场去试，
            // 每场白等 10 秒冷却，400 场的上限足够跑满 10 分钟超时。
            // 而主通道只有一个槽位，它一占就是整轮，water-sample / exchange 全部排队到被看门狗判 STUCK。
            if (CollectionResult.ShouldAbortOnVerify(points, OddsHttp.StatVerify - verifyBefore))
            {
                abortedByVerify = true;
                log?.Report($"  ! 本轮 {done} 场零产出、被验证拦截 {OddsHttp.StatVerify - verifyBefore} 次，"
                            + $"中止剩余 {matches.Count - done} 场，等待 token 续期");
                break;
            }
        }

        // 2026-11-10：这里原来硬编码 "SUCCESS"，RowsAffected 又填的是「场数」，
        // 于是「15 场全失败、赔率行 0」在两层都被读成绿灯：SyncLog 状态 SUCCESS，
        // 行数 15（看着像有产出）。token 被打掉的整夜，监控页干干净净。
        // 现在：全部失败才判 FAILED（部分失败仍算成功，避免偶发抖动刷告警），
        // 行数一律写真实写入行数 points。
        var allFailed = CollectionResult.AllFailed(done, failed);

        await _sync.WriteAsync(logTask ?? "odds-sample", source.Name, allFailed ? "FAILED" : "SUCCESS", points,
            $"轨迹采样 {done} 场，失败 {failed}，写入 {points} 行"
            + (abortedByVerify ? "（token 失效已提前中止）" : ""), ct, startedAt: now);

        // StatVerify 是进程内累计值，不是本轮。原来直接报它，日志里写着「拦截 2326 次」，
        // 让人以为是这一轮被拦了 2326 次；实际是当天累计。改成本轮增量。
        var verifyDelta = OddsHttp.StatVerify - verifyBefore;
        var reqDelta = OddsHttp.StatOk - okBefore;
        var msg = $"轨迹采样完成：{done} 场（失败 {failed}），请求 {reqDelta} 次，赔率行 {points}"
                  + (abortedByVerify ? $"，token 失效已提前中止（本轮拦截 {verifyDelta} 次）" : "")
                  + (verifyDelta > 0 ? $"；本轮验证拦截 {verifyDelta} 次，需更换 500-cookie.txt 里的 token" : "");
        log?.Report(msg);
        return allFailed ? CollectionResult.Fail(points, msg) : CollectionResult.Ok(points, msg);
    }

    /* ---------------- 必发（交易所）成交 ---------------- */

    /// <summary>
    /// 采集必发成交数据：成交量/成交价/庄家盈亏/指数与大额交易明细。
    /// 单场 1 个请求，成本低；高频采样即可还原资金流过程。
    /// </summary>
    public async Task<CollectionResult> CollectExchangeAsync(double hoursAhead, double hoursBehind,
        int recheckMinutes, int limit, IProgress<string>? log = null, CancellationToken ct = default,
        string? logTask = null)
    {
        var now = DateTime.Now;
        var from = now.AddHours(-hoursBehind);
        var to = now.AddHours(hoursAhead);

        var matches = await _exchange.GetMatchesForExchangeAsync(from, to, limit, recheckMinutes, ct);
        if (matches.Count == 0)
        {
            log?.Report($"无需采集必发（{from:MM-dd HH:mm}~{to:MM-dd HH:mm}，间隔 {recheckMinutes} 分钟）");
            return CollectionResult.Ok(0, "无待采集赛事");
        }

        log?.Report($"必发采集 {matches.Count} 场（{from:MM-dd HH:mm}~{to:MM-dd HH:mm}，间隔 {recheckMinutes} 分钟）");
        return await FetchExchangeAsync(matches, log, ct, logTask);
    }

    /// <summary>
    /// 把库里还没查过必发的完场尽量补上。页面只有最终成交，没有赛前过程。
    /// 已经有快照的场不重抓，避免把赛前采样盖成完场页。
    /// </summary>
    public async Task<CollectionResult> BackfillExchangeAsync(int limit, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var matches = await _exchange.GetUncheckedForBackfillAsync(limit, ct);
        if (matches.Count == 0)
        {
            log?.Report("必发回补：没有未检查的场");
            return CollectionResult.Ok(0, "没有未检查的场");
        }

        log?.Report($"必发回补 {matches.Count} 场（只补没查过的，完场页是最终成交）");
        return await FetchExchangeAsync(matches, log, ct);
    }

    private async Task<CollectionResult> FetchExchangeAsync(
        List<MatchBrief> matches, IProgress<string>? log, CancellationToken ct, string? logTask = null)
    {
        var source = new FiveHundredExchangeSource(_http);
        var concurrency = Math.Max(1, _opt.OddsConcurrency);
        var batchSize = Math.Max(concurrency, _opt.OddsBatchSize);

        // 日志行要能说出这段采集属于哪一次执行。原来固定写 "exchange"，
        // 于是 exchange-late / water-sample 内部的子采集也顶着 exchange 的名字进日志，
        // 库里那一列看起来是「exchange 作业频繁失败」，实际是另外两个作业在写。
        // 传 null 时保持旧名（单独跑 exchange / 回补时行为不变）。
        var taskName = logTask ?? "exchange";
        var startedAt = DateTime.Now;

        int written = 0, done = 0, withData = 0, noData = 0, failed = 0;
        var bigTrades = 0;
        var verifyBefore = OddsHttp.StatVerify;
        var abortedByVerify = false;

        for (var offset = 0; offset < matches.Count; offset += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var slice = matches.Skip(offset).Take(batchSize).ToList();
            var results = new List<MatchExchangeResult>(slice.Count);
            var gate = new object();

            await Parallel.ForEachAsync(slice,
                new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = ct },
                async (m, token) =>
                {
                    MatchExchangeResult r;
                    try
                    {
                        r = await source.FetchAsync(m.MatchId, token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        r = new MatchExchangeResult { MatchId = m.MatchId, Status = 2, Error = ex.Message };
                    }
                    lock (gate) results.Add(r);
                });

            written += await _exchange.UpsertAsync(results, ct);
            done += results.Count;
            withData += results.Count(r => r.Status == 1);
            noData += results.Count(r => r.Status == 0);
            failed += results.Count(r => r.Status == 2);
            bigTrades += results.Sum(r => r.Trades.Count);

            log?.Report($"  进度 {done}/{matches.Count}：有必发 {withData}，无盘口 {noData}，失败 {failed}，大额明细 {bigTrades} 条");

            foreach (var err in results.Where(r => r.Status == 2 && !string.IsNullOrEmpty(r.Error)).Take(2))
                log?.Report($"  ! {err.MatchId} {err.Error}");

            // 与赔率两条采集同一条短路。必发这里更容易被误判成「正常」：
            // 凌晨大批小联赛本来就没有必发盘口，零产出是常态，所以判据里的
            // 「被验证拦截」这一半不能省——无盘口的场次不会累加 StatVerify。
            if (CollectionResult.ShouldAbortOnVerify(written, OddsHttp.StatVerify - verifyBefore))
            {
                abortedByVerify = true;
                log?.Report($"  ! 本轮 {done} 场零写入、被验证拦截 {OddsHttp.StatVerify - verifyBefore} 次，"
                            + $"中止剩余 {matches.Count - done} 场，等待 token 续期");
                break;
            }
        }

        var allFailed = CollectionResult.AllFailed(done, failed);

        await _sync.WriteAsync(taskName, source.Name, allFailed ? "FAILED" : "SUCCESS", written,
            $"{done} 场，有必发 {withData}，写入 {written}"
            + (abortedByVerify ? "（token 失效已提前中止）" : ""), ct, startedAt: startedAt);

        var verifyDelta = OddsHttp.StatVerify - verifyBefore;
        var msg = $"必发采集完成：{done} 场（有数据 {withData} / 无盘口 {noData} / 失败 {failed}），大额明细 {bigTrades} 条"
                  + (abortedByVerify ? $"，token 失效已提前中止（本轮拦截 {verifyDelta} 次）" : "")
                  + (verifyDelta > 0 ? $"；本轮验证拦截 {verifyDelta} 次，需更换 500-cookie.txt 里的 token" : "");
        log?.Report(msg);
        return allFailed ? CollectionResult.Fail(written, msg) : CollectionResult.Ok(written, msg);
    }
}
