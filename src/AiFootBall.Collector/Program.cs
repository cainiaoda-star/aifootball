using AiFootBall.Core.Models;
using AiFootBall.Core.Options;
using AiFootBall.Data;
using AiFootBall.Sources;

namespace AiFootBall.Collector;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        var opts = ParseArgs(args.Skip(1).ToArray());

        var (dbOptions, collectorOptions, jobsOptions) = AppConfig.Load();
        var factory = new SqlConnectionFactory(dbOptions.ConnectionString);
        var lottoRepo = new LotteryRepository(factory);
        var fbRepo = new FootballRepository(factory);
        var oddsRepo = new OddsRepository(factory);
        var exchangeRepo = new ExchangeRepository(factory);
        var syncRepo = new SyncLogRepository(factory);
        var bettingRepo = new BettingRepository(factory);
        var analysisRepo = new AnalysisRepository(factory);

        using var http = HttpClients.Create(collectorOptions);
        var service = new CollectionService(http, collectorOptions, lottoRepo, fbRepo, oddsRepo,
            exchangeRepo, syncRepo);
        var cardSource = new BettingCardSource(http);
        var jcOddsRepo = new JcOddsRepository(factory);
        var recService = new RecommendationService(cardSource, bettingRepo, jcOddsRepo);
        var progress = new Progress<string>(Console.WriteLine);

        try
        {
            switch (cmd)
            {
                case "initdb":
                {
                    var init = new DatabaseInitializer(factory);
                    await init.InitializeAsync(Console.WriteLine);
                    Console.WriteLine("数据库初始化完成。");
                    return 0;
                }

                case "lotto":
                    await service.CollectLotteryAsync(opts.GetValueOrDefault("code"), !opts.ContainsKey("no-backfill"), progress);
                    return 0;

                case "football":
                    // --future N 同时采集未来 N 天赛程（赛前赔率/必发采样依赖场次记录）
                    await service.CollectFootballAsync(BuildDates(opts, collectorOptions.FootballBackfillDays), progress);
                    return 0;

                case "odds":
                {
                    var (from, to) = BuildOddsWindow(opts, collectorOptions.FootballBackfillDays);
                    var limit = opts.TryGetValue("limit", out var l) && int.TryParse(l, out var n) ? n : 0;
                    var onlyMissing = opts.ContainsKey("missing");
                    var sample = opts.ContainsKey("sample");
                    await service.CollectOddsAsync(from, to, onlyMissing, limit, progress,
                        sample ? DateTime.Now : null);
                    return 0;
                }

                case "sample":
                {
                    // 赔率轨迹采样：默认看未来 8 小时、含 1 小时前已开赛的场次
                    var hours = Num(opts, "hours", 8);
                    var behind = Num(opts, "behind", 1);
                    var recheck = (int)Num(opts, "recheck", 25);
                    var limit = (int)Num(opts, "limit", 0);
                    await service.SampleOddsAsync(hours, behind, recheck, limit, progress);
                    return 0;
                }

                case "exchange":
                {
                    // 单场直采（调试/补采）
                    if (opts.TryGetValue("fid", out var exFidText) && long.TryParse(exFidText, out var exFid))
                    {
                        var src = new FiveHundredExchangeSource(http);
                        var one = await src.FetchAsync(exFid);
                        var n = await exchangeRepo.UpsertAsync(new[] { one });
                        Console.WriteLine($"赛事 {exFid}：状态 {one.Status}，快照 {n} 条，大额明细 {one.Trades.Count} 条");
                        if (!string.IsNullOrEmpty(one.Error)) Console.WriteLine($"  错误：{one.Error}");
                        return 0;
                    }

                    // 必发成交数据：默认未来 10 小时 + 1 小时前已开赛
                    var hours = Num(opts, "hours", 10);
                    var behind = Num(opts, "behind", 1);
                    var recheck = (int)Num(opts, "recheck", 25);
                    var limit = (int)Num(opts, "limit", 0);
                    await service.CollectExchangeAsync(hours, behind, recheck, limit, progress);
                    return 0;
                }

                case "all":
                {
                    await service.CollectLotteryAsync(null, true, progress);
                    await service.CollectFootballAsync(BuildDates(opts, collectorOptions.FootballBackfillDays), progress);
                    var (from, to) = BuildOddsWindow(opts, collectorOptions.FootballBackfillDays);
                    await service.CollectOddsAsync(from, to, false, 0, progress);
                    return 0;
                }

                case "scheduler":
                {
                    var purchaseRepo = new PurchaseRepository(factory);
                    var purchaseSettle = new PurchaseSettlementService(purchaseRepo, fbRepo);
                    var scheduler = new JobScheduler(jobsOptions, service, syncRepo, recService, purchaseSettle, Console.WriteLine);
                    await scheduler.InitializeAsync();
                    if (opts.TryGetValue("once", out var once))
                    {
                        var (ok, msg) = await scheduler.RunOnceAsync(once);
                        Console.WriteLine(msg);
                        return ok ? 0 : 1;
                    }

                    using var cts = new CancellationTokenSource();
                    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
                    await scheduler.RunAsync(cts.Token);
                    return 0;
                }

                case "cards":
                {
                    var (jc, bd, sfc) = await recService.CollectCardsAsync();
                    Console.WriteLine($"受注场次采集完成：竞彩 {jc} 场 / 北单 {bd} 场 / 胜负彩 {sfc} 场");
                    return 0;
                }

                case "recommend":
                {
                    var board = opts.GetValueOrDefault("board", "all");
                    var all = await recService.RecommendAllAndSaveAsync();
                    foreach (var (b, recs) in all)
                    {
                        if (board != "all" && board != b) continue;
                        Console.WriteLine($"\n===== {(b == Boards.Jc ? "竞彩" : b == Boards.Bd ? "北单" : "胜负彩")}（{recs.Count} 场）=====");
                        foreach (var r in recs.Where(x => x.RecResult is not null).OrderByDescending(x => x.SignalLevel).ThenByDescending(x => x.Confidence))
                        {
                            var res = r.RecResult == '3' ? "主胜" : r.RecResult == '0' ? "客胜" : "平";
                            var score = r.ScoreCandidates.Count > 0 ? string.Join("、", r.ScoreCandidates) : "-";
                            var goals = r.GoalCandidates.Count > 0 ? string.Join("、", r.GoalCandidates) : "-";
                            Console.WriteLine($"  [{r.MatchNo}] {r.HomeTeam} vs {r.AwayTeam} → {res}(置信{r.Confidence}% L{r.SignalLevel}) 比分[{score}] 进球[{goals}] {r.SignalDesc}");
                        }
                    }
                    return 0;
                }

                case "settle":
                {
                    var result = await recService.SettleAsync();
                    Console.WriteLine($"===== 复盘回填完成 =====");
                    Console.WriteLine($"总 {result.Total} 场，命中 {result.Hit} 场，命中率 {result.HitRate}%");
                    foreach (var b in result.ByBoard)
                        Console.WriteLine($"  {(b.Board == Boards.Jc ? "竞彩" : b.Board == Boards.Bd ? "北单" : "胜负彩")}: {b.Hit}/{b.Total} (命中率 {b.HitRate}%)");
                    Console.WriteLine("\n[明细]");
                    foreach (var it in result.Items)
                    {
                        var res = it.RecResult == '3' ? "主胜" : it.RecResult == '0' ? "客胜" : it.RecResult == '1' ? "平" : "全包";
                        var ares = it.ActualResult == '3' ? "主胜" : it.ActualResult == '0' ? "客胜" : "平";
                        var mark = it.Hit is null ? "不计入" : it.Hit == true ? "✓命中" : "✗未中";
                        Console.WriteLine($"  [{(it.Board == Boards.Jc ? "竞彩" : it.Board == Boards.Bd ? "北单" : "胜负彩")}|{it.MatchNo}] {it.HomeTeam} vs {it.AwayTeam} 推{res} 实{ares} {mark} 比分[{it.RecScore}→{it.ActualScore}] 进球[{it.RecGoals}→{it.ActualGoals}]");
                    }
                    return 0;
                }

                case "analyze":
                {
                    var d = opts.TryGetValue("date", out var dt) && DateTime.TryParse(dt, out var dv) ? dv : DateTime.Today;
                    var a = await analysisRepo.AnalyzeAsync(d);
                    Console.WriteLine($"===== {a.Date:yyyy-MM-dd} 完场分析（{a.MatchCount} 场）=====");
                    Console.WriteLine("\n[赔率变化 → 赛果]");
                    foreach (var r in a.OddsMovement) Console.WriteLine($"  {r.grp}：{r.n}场，主胜{r.homePct}% / 客胜{r.awayPct}%");
                    Console.WriteLine("\n[必发投注 → 赛果]");
                    foreach (var r in a.Exchange) Console.WriteLine($"  {r.grp}：{r.n}场，主胜{r.homePct}% / 客胜{r.awayPct}%");
                    Console.WriteLine("\n[亚盘让球 → 赛果]");
                    foreach (var r in a.Asian) Console.WriteLine($"  {r.grp}：{r.n}场，主胜{r.homePct}% / 客胜{r.awayPct}%");
                    Console.WriteLine("\n[大小球盘口 → 大球率]");
                    foreach (var r in a.Totals) Console.WriteLine($"  {r.grp}：{r.n}场，大球率{r.overPct}%");
                    return 0;
                }

                case "report":
                    return await PrintReport(lottoRepo, fbRepo, oddsRepo, exchangeRepo);

                case "probe":
                {
                    if (!opts.TryGetValue("fid", out var fidText) || !long.TryParse(fidText, out var fid))
                    {
                        Console.WriteLine("用法：probe --fid 1442570 [--kind exchange]");
                        return 1;
                    }

                    var kind = opts.GetValueOrDefault("kind", "odds");
                    if (kind.Equals("exchange", StringComparison.OrdinalIgnoreCase))
                    {
                        var ex = new FiveHundredExchangeSource(http);
                        var r = await ex.FetchAsync(fid);
                        Console.WriteLine($"赛事 {fid} 必发：状态 {r.Status}，大额明细 {r.Trades.Count} 条");
                        if (!string.IsNullOrEmpty(r.Error)) Console.WriteLine($"  错误：{r.Error}");
                        var s = r.Snapshot;
                        if (s is not null)
                        {
                            Console.WriteLine($"  百家：{s.EuroHome}/{s.EuroDraw}/{s.EuroAway}  概率 {s.EuroProbHome}%/{s.EuroProbDraw}%/{s.EuroProbAway}%");
                            Console.WriteLine($"  成交价：{s.PriceHome}/{s.PriceDraw}/{s.PriceAway}");
                            Console.WriteLine($"  成交量：{s.VolumeHome}/{s.VolumeDraw}/{s.VolumeAway}  合计 {s.TotalVolume}");
                            Console.WriteLine($"  交易比例：{s.RatioHome}%/{s.RatioDraw}%/{s.RatioAway}%  北单 {s.BdRatioHome}/{s.BdRatioDraw}/{s.BdRatioAway}");
                            Console.WriteLine($"  庄家盈亏：{s.PnlHome}/{s.PnlDraw}/{s.PnlAway}  必发指数 {s.BfIdxHome}/{s.BfIdxDraw}/{s.BfIdxAway}");
                            Console.WriteLine($"  冷热：{s.HotIdxHome}/{s.HotIdxDraw}/{s.HotIdxAway}  盈亏指数 {s.PnlIdxHome}/{s.PnlIdxDraw}/{s.PnlIdxAway}");
                            Console.WriteLine($"  大额：合计 {s.BigTotal}（{s.BigHome}/{s.BigDraw}/{s.BigAway}）");
                            Console.WriteLine($"  提点：{s.TipScale} | {s.TipLean}");
                        }
                        foreach (var t in r.Trades.Take(6))
                            Console.WriteLine($"    {t.TradeTime:MM-dd HH:mm} {t.Side} {t.Direction} {t.Volume} ({t.Ratio}%)");
                        return 0;
                    }

                    var source = new FiveHundredOddsSource(http, collectorOptions);
                    var result = await source.FetchAsync(fid);
                    Console.WriteLine($"赛事 {fid}：状态 {result.Status}，公司 {result.CompanyCount}，赔率 {result.Odds.Count} 行");
                    if (!string.IsNullOrEmpty(result.Error)) Console.WriteLine($"  错误：{result.Error}");
                    foreach (var g in result.Odds.GroupBy(o => (o.OddsType, o.IsOpening)))
                        Console.WriteLine($"  {g.Key.OddsType} {(g.Key.IsOpening ? "初盘" : "终盘")}：{g.Count()} 行");
                    return 0;
                }

                default:
                    PrintHelp();
                    return 0;
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("已取消。");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"执行失败：{ex.Message}");
            return 1;
        }
    }

    private static List<DateOnly> BuildDates(Dictionary<string, string> args, int defaultDays, int futureDays = 0)
    {
        if (args.TryGetValue("date", out var dateText) && DateOnly.TryParse(dateText, out var single))
            return new List<DateOnly> { single };

        var days = args.TryGetValue("days", out var d) && int.TryParse(d, out var n) ? n : defaultDays;
        var future = args.TryGetValue("future", out var f) && int.TryParse(f, out var fn) ? fn : futureDays;
        var today = DateOnly.FromDateTime(DateTime.Today);

        var list = Enumerable.Range(0, Math.Max(1, days)).Select(i => today.AddDays(-i)).ToList();
        // 未来赛程用于提前建立场次记录，才能做赛前赔率/必发的时间序列采样
        list.AddRange(Enumerable.Range(1, Math.Max(0, future)).Select(i => today.AddDays(i)));
        return list;
    }

    private static (DateTime From, DateTime To) BuildOddsWindow(Dictionary<string, string> args, int defaultDays)
    {
        if (args.TryGetValue("date", out var dateText) && DateTime.TryParse(dateText, out var single))
            return (single.Date, single.Date.AddDays(1));

        if (args.TryGetValue("all", out _))
            return (new DateTime(2005, 1, 1), DateTime.Now.AddDays(1));

        var days = args.TryGetValue("days", out var d) && int.TryParse(d, out var n) ? n : defaultDays;
        return (DateTime.Today.AddDays(-Math.Max(1, days)), DateTime.Today.AddDays(1));
    }

    private static double Num(Dictionary<string, string> args, string key, double fallback)
        => args.TryGetValue(key, out var t) && double.TryParse(t, out var v) ? v : fallback;

    private static async Task<int> PrintReport(LotteryRepository lottoRepo, FootballRepository fbRepo,
        OddsRepository oddsRepo, ExchangeRepository exchangeRepo)
    {
        Console.WriteLine("================ 彩票数据概览 ================");
        Console.WriteLine($"{"代码",-8}{"名称",-10}{"类别",-6}{"期数",8}{"首期日期",14}{"末期日期",14}{"最新期号",12}");
        foreach (var row in await lottoRepo.GetOverviewAsync())
        {
            Console.WriteLine($"{row.Code,-8}{row.Name,-10}{row.Category,-6}{row.DrawCount,8}" +
                              $"{(string?)row.FirstDate,14}{(string?)row.LastDate,14}{(string?)row.LastIssue,12}");
        }

        Console.WriteLine("\n================ 足球数据概览 ================");
        var fb = (await fbRepo.GetOverviewAsync()).FirstOrDefault();
        if (fb is not null)
        {
            Console.WriteLine($"赛事 {fb.MatchCount} 场（完场 {fb.FinishedCount}），联赛 {fb.LeagueCount}，球队 {fb.TeamCount}，" +
                              $"范围 {fb.FirstDate} ~ {fb.LastDate}");
        }

        Console.WriteLine("\n================ 赔率覆盖 ================");
        var cov = await oddsRepo.GetCoverageAsync();
        if (cov is not null)
        {
            Console.WriteLine($"赛事 {cov.MatchTotal} 场：已采集赔率 {cov.WithOdds}，无赔率 {cov.NoOdds}，" +
                              $"失败 {cov.Failed}，未检查 {cov.Unchecked}");
            Console.WriteLine($"赔率行数 {cov.OddsRows}（欧赔 {cov.EuRows} / 亚盘 {cov.AhRows} / 大小球 {cov.OuRows}），" +
                              $"覆盖 {cov.OddsMatches} 场，公司 {cov.Companies} 家");
        }

        var mv = await oddsRepo.GetMovementStatsAsync();
        if (mv.Count > 0)
        {
            Console.WriteLine("\n玩法    样本数   主胜变化%  盘口变化%  大小球变化%  主胜平均变动");
            foreach (var m in mv)
            {
                Console.WriteLine($"{m.OddsType,-8}{m.Samples,7}{m.HomeChangedPct,11}{m.LineChangedPct,11}" +
                                  $"{m.TotalChangedPct,13}{m.AvgHomeDelta,14}");
            }
        }

        Console.WriteLine("\n================ 必发 / 赔率轨迹 ================");
        var exCov = await exchangeRepo.GetCoverageAsync();
        if (exCov is not null)
        {
            Console.WriteLine($"必发覆盖：有数据 {exCov.WithExchange} 场，无盘口 {exCov.NoExchange}，失败 {exCov.Failed}，" +
                              $"未检查 {exCov.Unchecked}");
            Console.WriteLine($"快照 {exCov.Snapshots} 条，大额明细 {exCov.Trades} 条，" +
                              $"最新总成交 {exCov.LatestTotalVolume}");
            Console.WriteLine($"赔率轨迹点 {exCov.OddsHistoryPoints} 条，覆盖 {exCov.OddsHistoryMatches} 场");
        }

        return 0;
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            var key = args[i].TrimStart('-');
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) dict[key] = args[++i];
            else dict[key] = "true";
        }
        return dict;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            AiFootBall 数据采集器

            用法：
              initdb                                初始化数据库（建库、建表、写彩种字典）
              lotto [--code dlt] [--no-backfill]    采集彩票开奖（pinble 为主 + 500 全量回补）
              football [--days 7]                   采集最近 N 天足球赛事
              football [--date 2026-10-27]          采集指定日期赛事
              odds [--days 7] [--missing] [--limit 200]
                                                    采集赔率（欧赔/亚盘/大小球，初盘+终盘）
                                                    --missing 只补未采集过的赛事
              odds [--date 2026-10-27] [--sample]   采集指定日期赛事赔率；--sample 同时写轨迹点
              odds --all                            全量回补历史赛事赔率
              sample [--hours 8] [--behind 1] [--recheck 25] [--limit 0]
                                                    赔率轨迹采样（临近开赛的场次，形成初盘→赛前曲线）
              exchange [--hours 10] [--behind 1] [--recheck 25] [--limit 0]
                                                    采集必发成交数据（成交量/庄家盈亏/指数/大额明细）
              cards                                采集竞彩/北单/胜负彩官方受注场次
              recommend [--board jc|bd|sfc|all]    生成今日推荐并落库（双信号命中+比分进球预测，北单取15场）
              settle                               复盘：回填已完场赛果并统计命中率
              all                                   彩票 + 足球 + 赔率
              scheduler                             启动内置 Cron 定时调度
              scheduler --once odds-backfill        手动执行一次指定作业
              report                                打印数据概览（含赔率与必发覆盖）
              probe --fid 1442570 [--kind exchange] 诊断单场抓取结果
            """);
    }
}
