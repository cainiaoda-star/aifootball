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
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", opt.UserAgent);
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
        var types = await _lotto.GetLotteryTypesAsync(ct);
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
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        var source = new FiveHundredMatchSource(_http, _opt);
        var total = 0;
        var failed = 0;

        foreach (var date in dates.OrderBy(x => x))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (matches, leagues, teams) = await source.FetchByDateAsync(date, ct);
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
        }

        var message = $"赔率采集完成：{done} 场（有赔率 {withOdds} / 无数据 {noData} / 失败 {failed}），写入 {rows} 行";
        await _sync.WriteAsync("odds", source.Name, failed == done && done > 0 ? "FAILED" : "SUCCESS",
            rows, message, ct);
        log?.Report(message);

        return CollectionResult.Ok(rows, message);
    }

    /// <summary>
    /// 赔率轨迹采样：时间窗内的场次 + 当前在售受注场，落库时同步写入 FbOddsHistory，
    /// 由此得到「初盘 → 赛前 → 开赛」的完整赔率变化曲线。
    /// </summary>
    /// <param name="hoursAhead">向前看多少小时内的赛事（在售场即使更远也会采）</param>
    /// <param name="hoursBehind">向后包含多少小时（捕获开赛瞬间与滚球初段）</param>
    /// <param name="recheckMinutes">同一场两次采样最小间隔</param>
    public async Task<CollectionResult> SampleOddsAsync(double hoursAhead, double hoursBehind,
        int recheckMinutes, int limit, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var now = DateTime.Now;
        var from = now.AddHours(-hoursBehind);
        var to = now.AddHours(hoursAhead);

        var matches = await _odds.GetMatchesForSamplingAsync(from, to, limit, recheckMinutes, ct: ct);
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
                });

            points += await _odds.UpsertAsync(results, sampleAt, ct);
            done += results.Count;
            failed += results.Count(r => r.Status == 2);
            log?.Report($"  采样进度 {done}/{matches.Count}，失败 {failed}（节流 {OddsHttp.ThrottleIntervalMs}ms）");
        }

        await _sync.WriteAsync("odds-sample", source.Name, "SUCCESS", done,
            $"轨迹采样 {done} 场，失败 {failed}", ct);

        var msg = $"轨迹采样完成：{done} 场（失败 {failed}），赔率行 {points}";
        log?.Report(msg);
        return CollectionResult.Ok(done, msg);
    }

    /* ---------------- 必发（交易所）成交 ---------------- */

    /// <summary>
    /// 采集必发成交数据：成交量/成交价/庄家盈亏/指数与大额交易明细。
    /// 单场 1 个请求，成本低；高频采样即可还原资金流过程。
    /// </summary>
    public async Task<CollectionResult> CollectExchangeAsync(double hoursAhead, double hoursBehind,
        int recheckMinutes, int limit, IProgress<string>? log = null, CancellationToken ct = default)
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
        return await FetchExchangeAsync(matches, log, ct);
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
        List<MatchBrief> matches, IProgress<string>? log, CancellationToken ct)
    {
        var source = new FiveHundredExchangeSource(_http);
        var concurrency = Math.Max(1, _opt.OddsConcurrency);
        var batchSize = Math.Max(concurrency, _opt.OddsBatchSize);

        int written = 0, done = 0, withData = 0, noData = 0, failed = 0;
        var bigTrades = 0;

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
        }

        await _sync.WriteAsync("exchange", source.Name, "SUCCESS", withData,
            $"{done} 场，有必发 {withData}", ct);

        var msg = $"必发采集完成：{done} 场（有数据 {withData} / 无盘口 {noData} / 失败 {failed}），大额明细 {bigTrades} 条";
        log?.Report(msg);
        return CollectionResult.Ok(withData, msg);
    }
}
