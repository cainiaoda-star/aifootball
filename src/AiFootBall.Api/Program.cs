using System.Text.Json.Serialization;
using AiFootBall.Api;
using AiFootBall.Core.Models;
using AiFootBall.Core.Options;
using AiFootBall.Data;
using AiFootBall.Sources;

var builder = WebApplication.CreateBuilder(args);

// 监听 127.0.0.1（本机环回），去酒店等外网环境下 IP 不变，仅本机浏览器可访问
builder.WebHost.ConfigureKestrel(k =>
{
    var port = int.TryParse(Environment.GetEnvironmentVariable("AIFOOTBALL_PORT"), out var p) ? p : 5088;
    k.ListenLocalhost(port);
});

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DictionaryKeyPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var connectionString = builder.Configuration.GetConnectionString("AiFootBall")
                       ?? Environment.GetEnvironmentVariable("AIFOOTBALL_CONN")
                       ?? throw new InvalidOperationException("缺少数据库连接串");
builder.Services.AddSingleton(new SqlConnectionFactory(connectionString));
builder.Services.AddSingleton<LotteryRepository>();
builder.Services.AddSingleton<LottoRecommendationRepository>();
builder.Services.AddSingleton<LottoUserTicketRepository>();
builder.Services.AddSingleton<LottoRecommendationService>();
builder.Services.AddSingleton<FootballRepository>();
builder.Services.AddSingleton<OddsRepository>();
builder.Services.AddSingleton<ExchangeRepository>();
builder.Services.AddSingleton<TrajectoryRepository>();
builder.Services.AddSingleton<SyncLogRepository>();

// 采集与调度
var collectorOptions = new CollectorOptions();
builder.Configuration.GetSection("Collector").Bind(collectorOptions);
var jobsOptions = new JobsOptions();
builder.Configuration.GetSection("Jobs").Bind(jobsOptions);

builder.Services.AddSingleton(collectorOptions);
builder.Services.AddSingleton(jobsOptions);
builder.Services.AddSingleton(_ => HttpClients.Create(collectorOptions));
builder.Services.AddSingleton<CollectionService>();
builder.Services.AddSingleton(sp => new BettingCardSource(sp.GetRequiredService<HttpClient>()));
builder.Services.AddSingleton<HitProbabilityService>();
builder.Services.AddSingleton(sp => new RecommendationService(
    sp.GetRequiredService<BettingCardSource>(),
    sp.GetRequiredService<BettingRepository>(),
    sp.GetRequiredService<JcOddsRepository>(),
    sp.GetRequiredService<HitProbabilityService>(),
    sp.GetRequiredService<MilkScanService>(),
    sp.GetRequiredService<TicketRepository>(),
    sp.GetRequiredService<MilkPickRepository>(),
    sp.GetRequiredService<OddsCaptureRepository>()));
builder.Services.AddSingleton<UserRepository>();
builder.Services.AddSingleton<BettingRepository>();
builder.Services.AddSingleton<AnalysisRepository>();
builder.Services.AddSingleton<PurchaseRepository>();
builder.Services.AddSingleton<TicketRepository>();
builder.Services.AddSingleton<JcOddsRepository>();
builder.Services.AddSingleton<OddsMoveRepository>();
builder.Services.AddSingleton<OddsCaptureRepository>();
builder.Services.AddSingleton<SummaryRepository>();
builder.Services.AddSingleton<MilkRepository>();
builder.Services.AddSingleton<MilkPickRepository>();
builder.Services.AddSingleton<MilkScanService>();
builder.Services.AddSingleton<DailyFollowRepository>();
builder.Services.AddSingleton<DailyFollowService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<PurchaseSettlementService>();
builder.Services.AddSingleton(sp => new JobScheduler(
    jobsOptions,
    sp.GetRequiredService<CollectionService>(),
    sp.GetRequiredService<SyncLogRepository>(),
    sp.GetRequiredService<RecommendationService>(),
    sp.GetRequiredService<PurchaseSettlementService>(),
    message => sp.GetRequiredService<ILoggerFactory>().CreateLogger("Scheduler").LogInformation("{Message}", message),
    sp.GetRequiredService<LottoRecommendationService>(),
    sp.GetRequiredService<DailyFollowService>()));
builder.Services.AddHostedService<SchedulerHostedService>();
// token 健康守护：连续被人机验证拦截时自动用本机浏览器续期，避免回补空转
builder.Services.AddHostedService<TokenHealthService>();

var app = builder.Build();

// 装载 500.com 人机验证 token（环境变量优先，其次在内容目录及其上级找 secrets/500-cookie.txt）
{
    static string? ResolveCookieFile(string contentRoot)
    {
        var env = Environment.GetEnvironmentVariable("AIFOOTBALL_500_COOKIE");
        if (!string.IsNullOrWhiteSpace(env)) return env;

        var dir = new DirectoryInfo(contentRoot);
        for (var i = 0; i < 5 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "secrets", "500-cookie.txt");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    var cookiePath = ResolveCookieFile(app.Environment.ContentRootPath);
    var n = BotGuard.LoadPersisted(cookiePath);
    app.Logger.LogInformation(n > 0
        ? "500.com 验证 Cookie 已装载 {Count} 项（{Path}），UA={Ua}"
        : "500.com 验证 Cookie 未装载（未找到 secrets/500-cookie.txt），盘口采集预计会被拦截（{Path} / UA={Ua}）",
        n, cookiePath ?? "(none)", BotGuard.UserAgent ?? "(默认)");

    // 自动续期器与 token 文件绑定；找不到 harvest.mjs 时 Enabled=false，只记日志不报错
    TokenRefresher.Configure(cookiePath);
    if (TokenRefresher.Enabled)
        app.Logger.LogInformation("token 自动续期已就绪，脚本 {Script}", TokenRefresher.ScriptPath);
    else
        app.Logger.LogWarning("token 自动续期未启用（找不到 _probe/harvest.mjs），被拦截时需人工执行");
}

await RecommendationSchema.EnsureAsync(app.Services.GetRequiredService<SqlConnectionFactory>());

// 模型预热（2026-11-05）：训练样本涨到 25 万场后，HitProbabilityService 首次拟合要 30~40 秒。
// 以前这笔时间由「第一个打开页面的请求」承担，表现就是点开大奶榜干等半分钟，还以为页面挂了。
// 这里在后台先跑一遍，用户来的时候直接命中缓存。失败不影响启动，首个请求会自己重试。
_ = Task.Run(async () =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        var snapshot = await app.Services.GetRequiredService<HitProbabilityService>().GetSnapshotAsync();
        app.Logger.LogInformation(
            "模型预热完成，耗时 {Ms}ms：命中率模型样本 {Rows} 场，比分模型 Ready={Ready}",
            sw.ElapsedMilliseconds, snapshot.Model.TrainCount, snapshot.Scores.Ready);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "模型预热失败（{Ms}ms），首个请求会重试", sw.ElapsedMilliseconds);
    }
});
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            ctx.Context.Response.Headers.CacheControl = "no-cache";
    }
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

/* ---------------- 彩票 ---------------- */

// 数字彩选号方案（最新一期主推+备选）
app.MapGet("/api/lotto/recommend", async (LottoRecommendationService svc, CancellationToken ct) =>
    Results.Ok(await svc.GetLatestAsync(ct)));

app.MapGet("/api/lotto/recommend/{code}", async (string code, LottoRecommendationService svc, CancellationToken ct) =>
{
    if (!LottoCodes.All.Contains(code, StringComparer.OrdinalIgnoreCase))
        return Results.BadRequest(new { message = UnsupportedLottoMessage() });
    return Results.Ok(await svc.GetByCodeAsync(code, ct));
});

app.MapGet("/api/lotto/review/{code}", async (string code, LottoRecommendationService svc, CancellationToken ct) =>
{
    if (!LottoCodes.All.Contains(code, StringComparer.OrdinalIgnoreCase))
        return Results.BadRequest(new { message = UnsupportedLottoMessage() });
    return Results.Ok(await svc.ReviewAsync(code, ct));
});

app.MapPost("/api/lotto/recommend/save", async (LottoRecommendationService svc, CancellationToken ct) =>
{
    var all = await svc.GenerateAllAndSaveAsync(ct);
    return Results.Ok(new
    {
        saved = all.Sum(x => x.Picks.Count),
        boards = all.Select(x => new { x.LotteryCode, x.TargetIssue, picks = x.Picks.Count, x.Summary })
    });
});

// 用户选号票（避热门号选号器 / 手工录入）
app.MapPost("/api/lotto/tickets", async (HttpRequest req, LottoRecommendationService svc, CancellationToken ct) =>
{
    var body = await req.ReadFromJsonAsync<LottoUserTicketSaveRequest>();
    if (body is null || string.IsNullOrWhiteSpace(body.Code))
        return Results.BadRequest(new { message = "code 不能为空" });
    if (body.Tickets is not { Count: > 0 })
        return Results.BadRequest(new { message = "tickets 不能为空" });
    try
    {
        var result = await svc.SaveUserTicketsAsync(body.Code.ToLowerInvariant(), body.Tickets, body.TargetIssue, ct);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapGet("/api/lotto/tickets", async (string? code, int? days, LottoRecommendationService svc, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(code) || !LottoCodes.All.Contains(code, StringComparer.OrdinalIgnoreCase))
        return Results.BadRequest(new { message = "code 应为 " + string.Join('/', LottoCodes.All) });
    return Results.Ok(await svc.GetUserTicketsAsync(code.ToLowerInvariant(), days ?? 30, ct));
});

// 彩种列表
app.MapGet("/api/lottery/types", async (LotteryRepository repo) => Results.Ok(await repo.GetLotteryTypesAsync()));

// 数据概览
app.MapGet("/api/lottery/overview", async (LotteryRepository repo) => Results.Ok(await repo.GetOverviewAsync()));

// 最近开奖
app.MapGet("/api/lottery/{code}/draws", async (string code, int? take, LotteryRepository repo) =>
{
    var draws = await repo.GetDrawsAsync(code, take ?? 30);
    return draws.Count == 0
        ? Results.NotFound(new { message = $"彩种 {code} 无数据" })
        : Results.Ok(draws.Select(d => new
        {
            d.Issue, DrawDate = d.DrawDate.ToString("yyyy-MM-dd"), d.Reds, d.Blues,
            Sum = d.Reds.Sum(), d.SalesAmount, d.PoolAmount, d.Source
        }));
});

// 号码频率（含近 30/100 期热度、当前遗漏、最大遗漏、平均间隔）
app.MapGet("/api/lottery/{code}/frequency", async (string code, string? ball, int? take, LotteryRepository repo) =>
{
    var lt = await repo.GetLotteryTypeAsync(code);
    if (lt is null) return Results.NotFound(new { message = $"彩种 {code} 不存在" });

    var draws = await repo.GetDrawsAsync(code, take ?? 500);
    var ballType = string.Equals(ball, "B", StringComparison.OrdinalIgnoreCase) ? 'B' : 'R';
    var max = ballType == 'B' ? lt.BlueMax : lt.RedMax;
    var list = LotteryMath.ComputeFrequency(draws, ballType, max);
    return Results.Ok(new { code, name = lt.Name, ball = ballType.ToString(), sampleSize = draws.Count, numbers = list });
});

// 综合走势分析：和值/奇偶/连号/重号
app.MapGet("/api/lottery/{code}/analysis", async (string code, int? take, LotteryRepository repo) =>
{
    var lt = await repo.GetLotteryTypeAsync(code);
    if (lt is null) return Results.NotFound(new { message = $"彩种 {code} 不存在" });

    var draws = await repo.GetDrawsAsync(code, take ?? 200);
    if (draws.Count == 0) return Results.NotFound(new { message = $"彩种 {code} 无数据" });

    var sums = draws.Select(d => d.Reds.Sum()).ToList();
    var freq = LotteryMath.ComputeFrequency(draws, 'R', lt.RedMax);
    var repeat = LotteryMath.RepeatCountSeries(draws);

    return Results.Ok(new
    {
        code,
        name = lt.Name,
        sampleSize = draws.Count,
        dateRange = new { from = draws.Min(d => d.DrawDate).ToString("yyyy-MM-dd"), to = draws.Max(d => d.DrawDate).ToString("yyyy-MM-dd") },
        sum = new
        {
            min = sums.Min(), max = sums.Max(), avg = Math.Round(sums.Average(), 2),
            distribution = LotteryMath.ComputeSumDistribution(draws)
        },
        oddEven = LotteryMath.ComputeOddEvenDistribution(draws),
        consecutiveRate = LotteryMath.ConsecutiveRate(draws),
        repeat = new { avg = repeat.Count > 0 ? Math.Round(repeat.Average(), 2) : 0, series = repeat.Take(20) },
        hot = freq.OrderByDescending(x => x.CountLast100).ThenByDescending(x => x.TotalCount).Take(10)
            .Select(x => new { x.Number, x.TotalCount, x.CountLast100, x.CurrentOmission }),
        cold = freq.OrderBy(x => x.CountLast100).ThenByDescending(x => x.CurrentOmission).Take(10)
            .Select(x => new { x.Number, x.TotalCount, x.CountLast100, x.CurrentOmission })
    });
});

/* ---------------- 足球 ---------------- */

app.MapGet("/api/football/overview", async (FootballRepository repo) =>
    Results.Ok((await repo.GetOverviewAsync()).FirstOrDefault()));

app.MapGet("/api/football/matches", async (string? from, string? to, int? leagueId, int? take, FootballRepository repo) =>
{
    var f = DateOnly.TryParse(from, out var fv) ? fv : DateOnly.FromDateTime(DateTime.Today);
    var t = DateOnly.TryParse(to, out var tv) ? tv : f;
    var list = await repo.GetMatchesAsync(f, t, leagueId, take ?? 500);
    return Results.Ok(list.Select(m => new
    {
        m.MatchId, m.LeagueName, MatchTime = m.MatchTime.ToString("yyyy-MM-dd HH:mm"),
        m.HomeTeamName, m.AwayTeamName, m.HomeScore, m.AwayScore,
        m.Handicap, ResultSpf = m.ResultSpf?.ToString(), ResultHandicap = m.ResultHandicap?.ToString(),
        m.TotalGoals, Status = m.Status
    }));
});

app.MapGet("/api/football/league-stats", async (string? from, string? to, FootballRepository repo) =>
{
    var t = DateOnly.TryParse(to, out var tv) ? tv : DateOnly.FromDateTime(DateTime.Today);
    var f = DateOnly.TryParse(from, out var fv) ? fv : t.AddDays(-7);
    return Results.Ok(await repo.GetLeagueStatsAsync(f, t));
});

/* ---------------- 赔率 ---------------- */

// 覆盖情况
app.MapGet("/api/football/odds-coverage", async (OddsRepository repo) =>
    Results.Ok(await repo.GetCoverageAsync()));

// 初盘→终盘变化统计（按玩法）
app.MapGet("/api/football/odds-movement", async (OddsRepository repo) =>
    Results.Ok(await repo.GetMovementStatsAsync()));

/* ---------------- 赔率轨迹 / 必发 ---------------- */

// 必发与轨迹数据覆盖
app.MapGet("/api/football/exchange-coverage", async (ExchangeRepository repo) =>
    Results.Ok(await repo.GetCoverageAsync()));

// 单场赔率轨迹（初盘 → 赛前 → 开赛的多个采样点）
app.MapGet("/api/football/odds-trajectory/{matchId:long}",
    async (long matchId, TrajectoryRepository repo) =>
    {
        var data = await repo.GetOddsTrajectoryAsync(matchId);
        return data is null
            ? Results.NotFound(new { message = $"赛事 {matchId} 暂无轨迹数据（需在赛前多轮采样）" })
            : Results.Ok(data);
    });

// 轨迹特征 → 赛果统计
app.MapGet("/api/football/trajectory-stats", async (TrajectoryRepository repo, int minPoints = 3) =>
    Results.Ok(await repo.GetTrajectoryStatsAsync(minPoints)));

// 必发成交信号 → 赛果统计
app.MapGet("/api/football/exchange-stats", async (TrajectoryRepository repo) =>
    Results.Ok(await repo.GetExchangeStatsAsync()));

// 单场必发成交序列 + 大额明细
app.MapGet("/api/football/exchange/{matchId:long}",
    async (long matchId, TrajectoryRepository repo) =>
    {
        var data = await repo.GetExchangeTimelineAsync(matchId);
        return data is null
            ? Results.NotFound(new { message = $"赛事 {matchId} 暂无必发数据" })
            : Results.Ok(data);
    });

// 单场水位变化：胜平负三线欧赔 + 必发成交量的逐采样点涨跌（含箭头方向所需的 delta）
app.MapGet("/api/football/water/{matchId:long}",
    async (long matchId, TrajectoryRepository repo) =>
    {
        var data = await repo.GetWaterMovementAsync(matchId);
        return data is null
            ? Results.NotFound(new { message = $"赛事 {matchId} 暂无水位变化数据（需赛前多轮采样）" })
            : Results.Ok(data);
    });

// 竞彩固定奖金四类玩法即时水位（当前赔率 + 相对上次采样的涨跌）
app.MapGet("/api/football/jc-water/{matchNo}",
    async (string matchNo, JcOddsRepository repo) =>
    {
        var data = await repo.GetWaterAsync(matchNo);
        return data is null
            ? Results.NotFound(new { message = $"场次 {matchNo} 暂无竞彩固定奖金采样数据" })
            : Results.Ok(data);
    });

// 变盘监控：几点、哪一档、从多少到多少、距开赛多久。只记事实，不给方向、不拦注。
// 起因：11-04 朗斯场 16:02 就把方向定死了，六个半小时后我出注，一次都没比较过前后值。
app.MapGet("/api/odds/moves",
    async (OddsMoveRepository repo, string? date, long? matchId, int? take, CancellationToken ct) =>
    {
        if (matchId is > 0)
            return Results.Ok(new
            {
                scope = "match",
                matchId,
                lastScan = await repo.GetLastScanAsync(ct),
                threshold = JcOddsRepository.MoveThreshold,
                rows = await repo.GetByMatchAsync(matchId.Value, ct)
            });

        var day = DateOnly.TryParse(date, out var d) ? d : DateOnly.FromDateTime(DateTime.Today);
        // 竞彩档期与自然日错位：周四档的欧战多在次日凌晨开赛（MatchTime 落在明天），
        // 只按 MatchTime=今天取会漏掉一整批在售场次。所以「今天在售」默认取 [今天, 后天) 两天窗口，
        // 前端已按 MatchStatus 把完赛的折叠、只突出未开赛的。
        // 显式传了 date 才精确到单日（复盘某一期时用）。
        if (string.IsNullOrWhiteSpace(date))
        {
            var from = day.ToDateTime(TimeOnly.MinValue);
            var to = from.AddDays(2);
            return Results.Ok(new
            {
                scope = "sale-window",
                date = day.ToString("yyyy-MM-dd"),
                window = new { from = from.ToString("yyyy-MM-dd"), to = to.ToString("yyyy-MM-dd") },
                lastScan = await repo.GetLastScanAsync(ct),
                threshold = JcOddsRepository.MoveThreshold,
                rows = await repo.GetByMatchRangeAsync(from, to, take ?? 500, ct)
            });
        }

        return Results.Ok(new
        {
            scope = "day",
            date = day.ToString("yyyy-MM-dd"),
            lastScan = await repo.GetLastScanAsync(ct),
            threshold = JcOddsRepository.MoveThreshold,
            rows = await repo.GetByMatchDayAsync(day, take ?? 500, ct)
        });
    });

// 近 N 天的变盘密度（按开赛日 + 玩法）。条数突然翻倍通常意味着临场有资金进场。
app.MapGet("/api/odds/moves/daily",
    async (OddsMoveRepository repo, int? days, CancellationToken ct) =>
        Results.Ok(new
        {
            threshold = JcOddsRepository.MoveThreshold,
            lastScan = await repo.GetLastScanAsync(ct),
            rows = await repo.GetDailyAsync(days ?? 14, ct)
        }));

/* ---------- 逐场采集台账 + 走向（2026-11-05） ----------
   台账回答「采了没」：今天几轮、每轮几场、哪两轮之间断了多久。
   走向回答「往哪走」：每场每玩法的首末热门档、去水概率迁移、变盘时间分布。

   这两件事以前都没有。11-05 上午 11:09 → 11:15 → 11:37 空了 22 分钟，
   在明细表里和「市场很平静」完全一样；11-04 朗斯场 13:29 比分盘整批指向科莫，
   也没有任何一行数据把「整批」这件事说出来。 */

// 采集台账 + 断档
app.MapGet("/api/odds/scan-log",
    async (OddsCaptureRepository repo, string? date, string? source, CancellationToken ct) =>
    {
        var day = DateOnly.TryParse(date, out var d) ? d : DateOnly.FromDateTime(DateTime.Today);
        var res = await repo.GetScanLogAsync(day, string.IsNullOrWhiteSpace(source) ? "jc" : source, ct);
        return Results.Ok(res);
    });

// 走向标签分布：如果 95% 都是「无明显迁移」，说明这个指标没区分度，要换掉而不是继续展示
app.MapGet("/api/odds/trend/tags",
    async (OddsCaptureRepository repo, int? days, CancellationToken ct) =>
        Results.Ok(new
        {
            days = days ?? 30,
            rows = await repo.GetTagDistributionAsync(days ?? 30, ct)
        }));

// 当日逐场走向：按场分组，每场带 SPF 三线迁移 + 各玩法首末档。
// status=open（默认）只出未开赛；status=all 连完场一起出（历史复盘用，靠 date 切日期）。
// 不传 date 时用「销售窗口」[今天, 后天)：竞彩周四档欧战多在次日凌晨开赛，单看开赛日=今天会漏掉整批在售场次。
app.MapGet("/api/odds/trend",
    async (OddsCaptureRepository repo, OddsMoveRepository moveRepo, string? date, string? play,
        string? status, int? take, CancellationToken ct) =>
    {
        var day = DateOnly.TryParse(date, out var d) ? d : DateOnly.FromDateTime(DateTime.Today);
        var openOnly = !string.Equals(status, "all", StringComparison.OrdinalIgnoreCase);
        var saleWindow = string.IsNullOrWhiteSpace(date);
        var from = day.ToDateTime(TimeOnly.MinValue);
        var to = from.AddDays(2);
        var rows = saleWindow
            ? await repo.GetTrendByRangeAsync(from, to, play, take ?? 600, openOnly, ct)
            : await repo.GetTrendByDayAsync(day, play, take ?? 600, openOnly, ct);

        // SPF 的三线去水概率迁移（百分点）。市场定价往哪边挪，看这个比看赔率数字直观。
        static decimal? Prob(decimal? o) => o is > 1 ? 1m / o.Value : null;
        static decimal? ShiftPp(decimal? open, decimal? curr, decimal? openSum, decimal? currSum)
        {
            if (open is null || curr is null || openSum is null or <= 0 || currSum is null or <= 0) return null;
            return decimal.Round((curr.Value / currSum.Value - open.Value / openSum.Value) * 100m, 2);
        }

        var matches = rows
            .GroupBy(r => r.MatchId)
            .Select(g =>
            {
                var head = g.First();
                var spf = g.FirstOrDefault(r => r.PlayType == "SPF");
                var openSum = spf is null ? null : Prob(spf.OpenWin) + Prob(spf.OpenDraw) + Prob(spf.OpenLoss);
                var currSum = spf is null ? null : Prob(spf.CurrWin) + Prob(spf.CurrDraw) + Prob(spf.CurrLoss);

                return new
                {
                    matchId = g.Key,
                    matchNo = head.MatchNo,
                    leagueName = head.LeagueName,
                    homeTeam = head.HomeTeamName,
                    awayTeam = head.AwayTeamName,
                    matchTime = head.MatchTime,
                    minutesToKick = head.MinutesToKick,
                    // 赛果：没有它，页面只能写「已开赛」，标签永远无法验收——看起来就像没更新。
                    homeScore = head.HomeScore,
                    awayScore = head.AwayScore,
                    matchStatus = head.MatchStatus,
                    resultSpf = head.ResultSpf,
                    finished = head.MatchStatus == 3,
                    tagHit = spf?.TagHit,
                    // 亚盘让球线：正 = 客让、负 = 主让（500 约定）。竞彩让球数不在库里，让球看这里。
                    ahOpen = head.AhOpen,
                    ahLast = head.AhLast,
                    // 每场的「总活跃度」：所有玩法加起来变了几次，临场变了几次
                    rounds = g.Max(r => r.Rounds),
                    moves = g.Sum(r => r.Moves),
                    bigMoves = g.Sum(r => r.BigMoves),
                    lateMoves = g.Sum(r => r.LateMoves),
                    firstMoveAt = g.Where(r => r.FirstMoveAt != null).Min(r => r.FirstMoveAt),
                    lastMoveAt = g.Where(r => r.LastMoveAt != null).Max(r => r.LastMoveAt),
                    trendTag = spf?.TrendTag,
                    spf = spf is null ? null : new
                    {
                        openWin = spf.OpenWin, openDraw = spf.OpenDraw, openLoss = spf.OpenLoss,
                        currWin = spf.CurrWin, currDraw = spf.CurrDraw, currLoss = spf.CurrLoss,
                        shiftWinPp = ShiftPp(Prob(spf.OpenWin), Prob(spf.CurrWin), openSum, currSum),
                        shiftDrawPp = ShiftPp(Prob(spf.OpenDraw), Prob(spf.CurrDraw), openSum, currSum),
                        shiftLossPp = ShiftPp(Prob(spf.OpenLoss), Prob(spf.CurrLoss), openSum, currSum)
                    },
                    plays = g.OrderBy(r => PlayOrder(r.PlayType)).Select(r => new
                    {
                        playType = r.PlayType,
                        playName = JcPlayName(r.PlayType),
                        rounds = r.Rounds,
                        options = r.Options,
                        moves = r.Moves,
                        bigMoves = r.BigMoves,
                        up = r.UpCount,
                        down = r.DownCount,
                        firstTopName = r.FirstTopName,
                        firstTopOdds = r.FirstTopOdds,
                        lastTopName = r.LastTopName,
                        lastTopOdds = r.LastTopOdds,
                        topShiftPct = r.TopShift is null ? (decimal?)null : decimal.Round(r.TopShift.Value * 100m, 2),
                        topChanged = r.FirstTopCode != r.LastTopCode,
                        lateMoves = r.LateMoves,
                        trendTag = r.TrendTag,
                        tagHit = r.TagHit
                    })
                };
            })
            .OrderBy(m => m.matchTime)
            .ToList();

        return Results.Ok(new
        {
            scope = saleWindow ? "sale-window" : "day",
            date = day.ToString("yyyy-MM-dd"),
            window = saleWindow ? new { from = from.ToString("yyyy-MM-dd"), to = to.ToString("yyyy-MM-dd") } : null,
            lastScan = await moveRepo.GetLastScanAsync(ct),
            threshold = JcOddsRepository.MoveThreshold,
            matches = matches
        });
    });

// 单场走向：每玩法一行 + 完整变盘序列（时间正序）
app.MapGet("/api/odds/trend/{matchId:long}",
    async (long matchId, OddsCaptureRepository repo, CancellationToken ct) =>
    {
        var data = await repo.GetTrendByMatchAsync(matchId, ct);
        return data is null
            ? Results.NotFound(new { message = $"赛事 {matchId} 暂无走向记录（需至少一轮采样）" })
            : Results.Ok(data);
    });

// 净胜球诊断：市场把这场比赛的比分定成了什么分布。
// 这是描述不是预测——MAE 1.32 球，整球全对 24.7%，别拿它当比分推荐。
// status=open（默认）只出未开赛，与逐场走向一致；status=all 连完场一起出（历史复盘）。
// 不传 date 时同样用「销售窗口」[今天, 后天)，否则周四档欧战会被自然日错位漏掉。
app.MapGet("/api/odds/score-diag",
    async (OddsCaptureRepository repo, string? date, string? status, CancellationToken ct) =>
    {
        var day = DateOnly.TryParse(date, out var d) ? d : DateOnly.FromDateTime(DateTime.Now);
        var openOnly = !string.Equals(status, "all", StringComparison.OrdinalIgnoreCase);
        var saleWindow = string.IsNullOrWhiteSpace(date);
        var from = day.ToDateTime(TimeOnly.MinValue);
        var to = from.AddDays(2);
        var rows = saleWindow
            ? await repo.GetScoreDiagByRangeAsync(from, to, openOnly: openOnly, ct: ct)
            : await repo.GetScoreDiagByDayAsync(day, openOnly: openOnly, ct: ct);
        return Results.Ok(new
        {
            scope = saleWindow ? "sale-window" : "day",
            date = day,
            window = saleWindow ? new { from = from.ToString("yyyy-MM-dd"), to = to.ToString("yyyy-MM-dd") } : null,
            matches = rows.Count,
            rows
        });
    });

// 标签自检：按尾部标签分组看真实大比分率。没有区分度就该下线，不能只展示不验证。
app.MapGet("/api/odds/score-diag/stats",
    async (OddsCaptureRepository repo, int? days, CancellationToken ct) =>
        Results.Ok(new
        {
            days = days ?? 90,
            rows = await repo.GetScoreDiagStatsAsync(days ?? 90, ct)
        }));

app.MapGet("/api/odds/score-diag/{matchId:long}",
    async (long matchId, OddsCaptureRepository repo, CancellationToken ct) =>
    {
        var data = await repo.GetScoreDiagAsync(matchId, ct);
        return data is null
            ? Results.NotFound(new { message = $"赛事 {matchId} 无比分盘快照，无法还原净胜球分布" })
            : Results.Ok(data);
    });

static int PlayOrder(string playType) => playType switch
{
    "SPF" => 0, "NSPF" => 1, "JQ" => 2, "BQ" => 3, "BF" => 4, _ => 9
};

static string JcPlayName(string playType) => playType switch
{
    "SPF" => "胜平负", "NSPF" => "让球", "BF" => "比分", "JQ" => "总进球", "BQ" => "半全场", _ => playType
};


// 单场赔率：欧赔 / 亚盘 / 大小球（各含初盘与终盘）
app.MapGet("/api/football/odds/{matchId:long}", async (long matchId, OddsRepository oddsRepo, FootballRepository fbRepo) =>
{
    var rows = await oddsRepo.GetOddsAsync(matchId);
    if (rows.Count == 0) return Results.NotFound(new { message = $"赛事 {matchId} 暂无赔率数据" });

    var match = await fbRepo.GetMatchAsync(matchId);

    return Results.Ok(new
    {
        matchId,
        match = match is null ? null : new
        {
            match.MatchId,
            match.LeagueName,
            MatchTime = match.MatchTime.ToString("yyyy-MM-dd HH:mm"),
            match.HomeTeamName,
            match.AwayTeamName,
            match.HomeScore,
            match.AwayScore,
            match.Handicap,
            ResultSpf = match.ResultSpf?.ToString(),
            ResultHandicap = match.ResultHandicap?.ToString(),
            match.TotalGoals,
            match.Status
        },
        companyCount = rows.Where(r => r.CompanyId > 0).Select(r => r.CompanyId).Distinct().Count(),
        consensus = rows.Where(r => r.CompanyId == 0),
        eu = rows.Where(r => r.OddsType == OddsTypes.Eu),
        ah = rows.Where(r => r.OddsType == OddsTypes.Ah),
        ou = rows.Where(r => r.OddsType == OddsTypes.Ou)
    });
});

// 初终盘变化 → 赛果关系（数据价值验证）
app.MapGet("/api/football/odds-signal", async (OddsRepository repo) => Results.Ok(await repo.GetSignalStatsAsync()));

/* ---------------- 定时作业 ---------------- */

app.MapGet("/api/jobs", (JobScheduler scheduler) => Results.Ok(scheduler.Describe()));

app.MapPut("/api/jobs/{name}", (string name, JobScheduleEntry req, JobScheduler scheduler, IWebHostEnvironment env) =>
{
    var (ok, message) = scheduler.Update(name, req.Cron, req.Enabled, req.Hours, req.RecheckMinutes);
    if (!ok) return Results.BadRequest(new { ok, message });
    JobScheduleStore.Upsert(JobScheduleStore.FilePath(env.ContentRootPath), new JobScheduleEntry
    {
        Name = name,
        Cron = req.Cron.Trim(),
        Enabled = req.Enabled,
        Hours = req.Hours,
        RecheckMinutes = req.RecheckMinutes
    });
    return Results.Ok(new { ok, message });
});

app.MapPost("/api/jobs/{name}/run", async (string name, JobScheduler scheduler) =>
{
    // 手动补采可能跑十几分钟；不要绑请求 CancellationToken，否则客户端超时会把采集掐断
    var (ok, message) = await scheduler.RunOnceAsync(name, CancellationToken.None);
    return ok ? Results.Ok(new { ok, message }) : Results.BadRequest(new { ok, message });
});

// 补写指定日期的复盘（定时任务错过时手动兜底）。默认写「前一天」，带 date 参数写指定那天。
app.MapPost("/api/review/write", async (RecommendationService svc, string? date, CancellationToken ct) =>
{
    DateTime day;
    if (!string.IsNullOrWhiteSpace(date) && DateTime.TryParse(date, out var dv)) day = dv;
    else day = DateTime.Now.Hour < 12 ? DateTime.Now.Date.AddDays(-1) : DateTime.Now.Date;
    var path = await svc.WriteDailyReviewAsync(day, ct);
    return Results.Ok(new { day = day.ToString("yyyy-MM-dd"), path });
});

/* ---------------- 每日逐场跟进复盘（2026-11-06 新口径） ---------------- */

// 读某天逐场跟进（每场一注 + 变化标注 + 赛后复盘）+ 当天技能总结 + 推荐变更历史。
app.MapGet("/api/daily-follow", async (DailyFollowRepository repo, string? date, CancellationToken ct) =>
{
    var d = DateTime.TryParse(date, out var dv) ? dv.Date : DateTime.Today;
    var rows = await repo.GetDayAsync(d, ct);
    var skill = await repo.GetSkillAsync(d, ct);
    var changes = await repo.GetChangesAsync(d, ct);
    var settled = rows.Where(r => r.SettledAt is not null).ToList();
    var hit = settled.Count(r => r.Hit == true);
    return Results.Ok(new
    {
        date = d.ToString("yyyy-MM-dd"),
        total = rows.Count,
        settled = settled.Count,
        hit,
        hitRate = settled.Count > 0 ? Math.Round(100.0 * hit / settled.Count, 1) : 0,
        items = rows,
        changes,
        skill
    });
});

// 手动生成/刷新某天逐场跟进。
app.MapPost("/api/daily-follow/build", async (DailyFollowService svc, string? date, CancellationToken ct) =>
{
    var d = DateTime.TryParse(date, out var dv) ? dv.Date : DateTime.Today;
    var result = await svc.BuildDailyAsync(d, ct);
    return result.Success ? Results.Ok(new { day = d.ToString("yyyy-MM-dd"), result.Message }) : Results.Problem(result.Message);
});

// 手动触发逐场赛后复盘。
app.MapPost("/api/daily-follow/settle", async (DailyFollowService svc, string? date, CancellationToken ct) =>
{
    var d = DateTime.TryParse(date, out var dv) ? dv.Date : DateTime.Today;
    var result = await svc.SettleDailyAsync(d, ct);
    return Results.Ok(new { day = d.ToString("yyyy-MM-dd"), result.Message });
});

// 手动触发每日技能总结。
app.MapPost("/api/daily-follow/skill", async (DailyFollowService svc, string? date, CancellationToken ct) =>
{
    var d = DateTime.TryParse(date, out var dv) ? dv.Date : DateTime.Today;
    var result = await svc.SummarizeSkillAsync(d, ct);
    return Results.Ok(new { day = d.ToString("yyyy-MM-dd"), result.Message });
});

// 手动补写某天技能（自动总结覆盖不到的跨联赛规律，可带证据）。
app.MapPost("/api/daily-follow/skill/write", async (DailyFollowRepository repo, DailySkillWriteRequest req, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.Body))
        return Results.Problem("title 和 body 不能为空");
    var d = DateTime.TryParse(req.Date, out var dv) ? dv.Date : DateTime.Today;
    await repo.SaveSkillAsync(d, req.Title, req.Body, req.Evidence, ct);
    return Results.Ok(new { day = d.ToString("yyyy-MM-dd"), title = req.Title });
});

app.MapGet("/api/jobs/logs", async (int? take, SyncLogRepository repo) =>
    Results.Ok(await repo.GetRecentAsync(take ?? 50)));

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", time = DateTime.Now }));

/* ---------------- 命中率校准 ---------------- */

// 校准报表：预测值 vs 实际命中率，附带与市场基准的 Brier 对比和平注回报率。
// 没有这张表，所有阈值（55/60/70）都只能靠单场手感调整。
app.MapGet("/api/calibration", async (int? days, BettingRepository repo, CancellationToken ct) =>
    Results.Ok(await CalibrationService.BuildAsync(repo, days ?? 60, ct)));

/* ---------------- 票面 KPI ---------------- */

// 按票型统计中奖率 / 回本率 / 实际回报。回答的是「哪种买法真的回本」，
// 与单场命中率（/api/calibration）是两回事：串关经常中奖却回不了本。
app.MapGet("/api/ticket/kpi", async (int? days, TicketRepository repo, CancellationToken ct) =>
    Results.Ok(await repo.GetKpiAsync(days ?? 90, ct)));

/* ---------------- 否决台账 ---------------- */

// 被规则挡下的场次，赛后回填「照打会不会中」。
// 「必发 ≥75% 不追」这类阈值一直没被赛前样本检验过，有了这张表才能检验。
app.MapGet("/api/reject", async (int? days, string? board, RecommendationService svc, CancellationToken ct) =>
    Results.Ok(await svc.GetVetoReportAsync(Math.Clamp(days ?? 30, 1, 365), ct, board)));

// 最近发的票（含未结算），看今天实际发的是什么结构。
app.MapGet("/api/ticket", async (int? take, TicketRepository repo, CancellationToken ct) =>
{
    var pending = await repo.GetPendingAsync(null, ct);
    var list = pending
        .OrderByDescending(t => t.PlanDate)
        .Take(Math.Clamp(take ?? 20, 1, 100))
        .Select(t => new
        {
            t.TicketId,
            planDate = t.PlanDate.ToString("yyyy-MM-dd"),
            t.Board,
            t.PassType,
            t.LegCount,
            t.BetCount,
            t.BetAmount,
            t.HitProbability,
            t.BackProbability,
            t.ExpectedReturn,
            t.Status,
            lines = t.Legs.Select(l => new
            {
                l.Seq, l.MatchNo, l.HomeTeam, l.AwayTeam,
                option = l.PickOption, l.Odds,
                // 票面用的是「模型 / 盘口较低值」；另两个数并排给出，好判断这条腿当时有多悬。
                legProbability = l.LegProbability,
                modelProbability = l.ModelProbability,
                marketProbability = l.MarketProbability,
                l.LineHit
            })
        });
    return Results.Ok(list);
});

/* ---------------- token 续期 ---------------- */

// 续期状态：是否启用、上次成功时间、最近日志。排障时先看这里。
app.MapGet("/api/token/status", () => Results.Ok(TokenRefresher.Snapshot()));

// 手动续期：token 被拦后不想等守护进程，可以主动触发一次
app.MapPost("/api/token/refresh", async () =>
{
    var (ok, message) = await TokenRefresher.TryRefreshAsync("manual", null, CancellationToken.None, force: true);
    return ok ? Results.Ok(new { ok, message }) : Results.BadRequest(new { ok, message });
});

/* ---------------- 登录 / 推荐 ---------------- */

// 登录
app.MapPost("/api/auth/login", async (LoginRequest req, UserRepository users, AuthService auth, CancellationToken ct) =>
{
    var user = await users.AuthenticateAsync(req.UserName, req.Password, ct);
    if (user is null) return Results.Unauthorized();
    var token = auth.Issue(user);
    return Results.Ok(new { token, userName = user.UserName, nickName = user.NickName ?? user.UserName });
});

// 注册（可选，首次使用建号）
app.MapPost("/api/auth/register", async (LoginRequest req, UserRepository users, AuthService auth, CancellationToken ct) =>
{
    var user = await users.CreateAsync(req.UserName, req.Password, req.NickName, ct);
    if (user is null) return Results.BadRequest(new { message = "用户名已存在" });
    var token = auth.Issue(user);
    return Results.Ok(new { token, userName = user.UserName, nickName = user.NickName ?? user.UserName });
});

app.MapPost("/api/auth/password", async (PasswordChangeRequest req, HttpRequest request, UserRepository users, AuthService auth, CancellationToken ct) =>
{
    var token = request.Headers.Authorization.ToString().Replace("Bearer ", "");
    if (!auth.TryGet(token, out var userName, out _)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 6)
        return Results.BadRequest(new { ok = false, message = "新密码至少 6 位" });
    if (!string.Equals(req.NewPassword, req.ConfirmPassword, StringComparison.Ordinal))
        return Results.BadRequest(new { ok = false, message = "两次输入的新密码不一致" });
    if (string.Equals(req.CurrentPassword, req.NewPassword, StringComparison.Ordinal))
        return Results.BadRequest(new { ok = false, message = "新密码要和当前密码不同" });

    var ok = await users.ChangePasswordAsync(userName, req.CurrentPassword ?? "", req.NewPassword, ct);
    return ok
        ? Results.Ok(new { ok = true, message = "密码已修改，下次登录用新密码" })
        : Results.BadRequest(new { ok = false, message = "当前密码不正确" });
});

// 当前登录用户
app.MapGet("/api/auth/me", (HttpRequest request, AuthService auth) =>
{
    var token = request.Headers.Authorization.ToString().Replace("Bearer ", "");
    return auth.TryGet(token, out var u, out var n)
        ? Results.Ok(new { userName = u, nickName = n })
        : Results.Unauthorized();
});

app.MapGet("/api/skills", () => Results.Ok(SkillBoard.Read()));

app.MapPost("/api/skills", (SkillWriteRequest req, HttpRequest request, AuthService auth) =>
{
    var token = request.Headers.Authorization.ToString().Replace("Bearer ", "");
    if (!auth.TryGet(token, out _, out _)) return Results.Unauthorized();
    var title = (req.Title ?? "").Trim();
    var body = (req.Body ?? "").Trim();
    if (title.Length == 0 || body.Length == 0)
        return Results.BadRequest(new { ok = false, message = "标题和正文都要写" });
    if (title.Length > 80 || body.Length > 4000)
        return Results.BadRequest(new { ok = false, message = "标题 80 字以内，正文 4000 字以内" });
    return Results.Ok(SkillBoard.Add(title, body, "user"));
});

app.MapDelete("/api/skills/{id}", (string id, HttpRequest request, AuthService auth) =>
{
    var token = request.Headers.Authorization.ToString().Replace("Bearer ", "");
    if (!auth.TryGet(token, out _, out _)) return Results.Unauthorized();
    return SkillBoard.Delete(id) ? Results.Ok(new { ok = true }) : Results.NotFound();
});

// 采集受注场次（内部触发）
app.MapPost("/api/recommend/collect", async (RecommendationService svc, CancellationToken ct) =>
{
    var (jc, bd, sfc) = await svc.CollectCardsAsync(ct);
    return Results.Ok(new { jc, bd, sfc });
});

// 保存今日推荐（落库，供开奖后复盘）
app.MapPost("/api/recommend/save", async (RecommendationService svc, CancellationToken ct) =>
{
    var all = await svc.RecommendAllAndSaveAsync(ct);
    return Results.Ok(new
    {
        saved = all.Values.Sum(v => v.Count),
        jc = all.GetValueOrDefault(Boards.Jc)?.Count ?? 0,
        bd = all.GetValueOrDefault(Boards.Bd)?.Count ?? 0,
        sfc = all.GetValueOrDefault(Boards.Sfc)?.Count ?? 0
    });
});

// 复盘：回填已完场赛果并统计命中
app.MapPost("/api/recommend/settle", async (RecommendationService svc, CancellationToken ct) =>
{
    var result = await svc.SettleAsync(ct);
    return Results.Ok(result);
});

// 某日推荐复盘记录（含命中情况）
app.MapGet("/api/recommend/history", async (BettingRepository repo, string? date, CancellationToken ct) =>
{
    var d = DateTime.TryParse(date, out var dv) ? dv : DateTime.Today;
    var recs = await repo.GetRecommendationsByDateAsync(d, ct);
    return Results.Ok(new
    {
        date = d.ToString("yyyy-MM-dd"),
        total = recs.Count,
        hit = recs.Count(r => r.Hit == true),
        items = recs.Select(r => new
        {
            r.Board, r.MatchNo, r.HomeTeam, r.AwayTeam,
            recResult = r.RecResult?.ToString(), isCoverAll = r.IsCoverAll, actualResult = r.ActualResult?.ToString(),
            r.Confidence, r.Hit, r.Score1, r.Score2, r.Goals1, r.Goals2,
            r.ActualScore, r.ActualGoals, r.SignalDesc
        })
    });
});

// 历史推荐列表（全量，按推荐时间降序）
app.MapGet("/api/recommend/list", async (BettingRepository repo, int? take, int? offset, CancellationToken ct) =>
{
    var t = Math.Clamp(take ?? 500, 1, 2000);
    var o = Math.Max(0, offset ?? 0);
    var items = await repo.GetRecommendationListAsync(t, o, ct);
    var total = await repo.CountRecommendationsAsync(ct);
    return Results.Ok(new
    {
        total,
        take = t,
        offset = o,
        items = items.Select(r => new
        {
            r.RecId, r.Board, r.MatchNo, r.HomeTeam, r.AwayTeam,
            matchTime = r.MatchTime, createdAt = r.CreatedAt, recDate = r.RecDate,
            recResult = r.RecResult?.ToString(), isCoverAll = r.IsCoverAll,
            r.Confidence, r.SignalDesc,
            r.Score1, r.Score2, r.Goals1, r.Goals2
        })
    });
});

// 复盘列表（已回填赛果，按比赛时间降序，含比赛类型）
app.MapGet("/api/review/list", async (BettingRepository repo, int? take, int? offset, CancellationToken ct) =>
{
    var t = Math.Clamp(take ?? 500, 1, 2000);
    var o = Math.Max(0, offset ?? 0);
    var items = await repo.GetReviewedListAsync(t, o, ct);
    var stats = await repo.GetReviewedStatsAsync(ct);
    return Results.Ok(new
    {
        total = stats.Total,
        hit = stats.Hit,
        hitRate = Rate(stats.Hit, stats.Total).hitRate,
        hitLow = Rate(stats.Hit, stats.Total).hitLow,
        hitHigh = Rate(stats.Hit, stats.Total).hitHigh,
        take = t,
        offset = o,
        items = items.Select(r => new
        {
            r.RecId, r.Board, r.MatchNo, r.HomeTeam, r.AwayTeam, r.LeagueName,
            matchTime = r.MatchTime, recDate = r.RecDate,
            recResult = r.RecResult?.ToString(), isCoverAll = r.IsCoverAll, actualResult = r.ActualResult?.ToString(),
            r.Hit, r.Confidence, r.SignalDesc,
            r.Score1, r.Score2, r.Goals1, r.Goals2,
            r.ActualScore, r.ActualGoals
        })
    });
});

/* ---------------- 我的投注（购买记录 + 开奖回填） ---------------- */

// 图片上传：保存到 wwwroot/uploads，返回可访问的相对路径
app.MapPost("/api/purchase/upload", async (HttpRequest request, IWebHostEnvironment env) =>
{
    if (!request.HasFormContentType || request.Form.Files.Count == 0)
        return Results.BadRequest(new { message = "未收到文件" });

    var file = request.Form.Files[0];
    if (file.Length == 0) return Results.BadRequest(new { message = "空文件" });
    if (file.Length > 20 * 1024 * 1024) return Results.BadRequest(new { message = "文件超过 20MB" });

    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    var allow = new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };
    if (!allow.Contains(ext)) return Results.BadRequest(new { message = "仅支持图片格式" });

    var webRoot = string.IsNullOrWhiteSpace(env.WebRootPath)
        ? Path.Combine(env.ContentRootPath, "wwwroot")
        : env.WebRootPath;
    var dir = Path.Combine(webRoot, "uploads");
    Directory.CreateDirectory(dir);
    var name = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}{ext}";
    var full = Path.Combine(dir, name);
    await using (var fs = File.Create(full))
        await file.CopyToAsync(fs);

    return Results.Ok(new { path = $"/uploads/{name}" });
});

// 购买截图 OCR：识别场次/对阵/选项/赔率，返回结构化数据（供前端自动预填）
app.MapPost("/api/purchase/ocr", async (HttpRequest request, BettingRepository betting, CancellationToken ct) =>
{
    string image;
    if (request.HasFormContentType && request.Form.Files.Count > 0)
    {
        var f = request.Form.Files[0];
        using var ms = new MemoryStream();
        await f.CopyToAsync(ms, ct);
        image = Convert.ToBase64String(ms.ToArray());
    }
    else
    {
        using var reader = new StreamReader(request.Body);
        var bodyText = await reader.ReadToEndAsync(ct);
        try
        {
            var json = System.Text.Json.JsonDocument.Parse(bodyText);
            image = json.RootElement.GetProperty("image").GetString() ?? "";
        }
        catch
        {
            return Results.BadRequest(new { message = "缺少图片数据" });
        }
    }

    if (string.IsNullOrEmpty(image)) return Results.BadRequest(new { message = "图片为空" });

    // 票面分类：jc-score|jc-spf|bd-score|bd-spf（及 -app）；兼容 physical|app|auto
    var kind = "auto";
    if (request.HasFormContentType && request.Form.TryGetValue("kind", out var kindVal))
        kind = kindVal.ToString();
    else if (request.Query.TryGetValue("kind", out var kindQ))
        kind = kindQ.ToString();
    var allowedKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "auto", "physical", "app",
        "jc-score", "jc-spf", "bd-score", "bd-spf",
        "jc-score-app", "jc-spf-app", "bd-score-app", "bd-spf-app"
    };
    if (!allowedKinds.Contains(kind)) kind = "auto";

    string? layout = null;
    if (request.HasFormContentType && request.Form.TryGetValue("layout", out var layoutVal))
        layout = layoutVal.ToString();
    else if (request.Query.TryGetValue("layout", out var layoutQ))
        layout = layoutQ.ToString();
    if (layout is not ("physical" or "app")) layout = null;

    var ocrUrl = Environment.GetEnvironmentVariable("AIFOOTBALL_OCR_URL") ?? "http://127.0.0.1:5089";
    // 2026-11-04：OCR 服务改三遍识别（PSM 4/6/11）做表格重建，单张要 30~60 秒
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(150) };
    try
    {
        var resp = await client.PostAsJsonAsync($"{ocrUrl}/ocr", new { image, kind, layout }, ct);
        if (!resp.IsSuccessStatusCode)
            return Results.BadRequest(new { message = "OCR 服务不可用，请确认已启动" });

        var ocrResult = await resp.Content.ReadFromJsonAsync<OcrResponse>(cancellationToken: ct);
        var parsed = ocrResult?.Parsed ?? new OcrParsed();

        // 1) 场次号反查；2) 号缺失/不准时用 OCR 队名碎片模糊匹配受注表
        var matchNos = parsed.Matches.Select(m => m.MatchNo).Where(n => !string.IsNullOrWhiteSpace(n)).ToList()!;
        var refMap = await betting.ResolveMatchesByNoAsync(matchNos!, ct);
        // 竞彩场次号带「周X」；北单是纯数字序号
        var weekPrefix = string.Equals(parsed.Board, "bd", StringComparison.OrdinalIgnoreCase)
            ? null
            : parsed.Matches
                .Select(m => m.MatchNo)
                .FirstOrDefault(n => n is { Length: >= 2 } && n.StartsWith("周", StringComparison.Ordinal))
                ?[..2];

        var isBd = string.Equals(parsed.Board, "bd", StringComparison.OrdinalIgnoreCase);
        var matches = new List<OcrMatch>();
        foreach (var m in parsed.Matches)
        {
            var linked = false;
            if (m.MatchNo != null && refMap.TryGetValue(m.MatchNo, out var mr))
            {
                // 北单场次号跨期复用：须与主队名对得上才按号补全
                if (!isBd || OcrTeamLooksLike(m.Home, mr.HomeTeam))
                {
                    m.Home = mr.HomeTeam ?? m.Home;
                    m.Away = mr.AwayTeam ?? m.Away;
                    linked = true;
                }
            }
            if (!linked)
            {
                var byTeam = await betting.ResolveMatchByTeamHintAsync(weekPrefix, m.Home, m.Away, ct);
                // 北单票面通常只打主队简称，只认主队命中，避免客队简称把主客颠倒
                if (byTeam != null && OcrTeamLooksLike(m.Home, byTeam.HomeTeam))
                {
                    if (!isBd) m.MatchNo = byTeam.MatchNo ?? m.MatchNo;
                    m.Home = byTeam.HomeTeam ?? m.Home;
                    m.Away = byTeam.AwayTeam ?? m.Away;
                }
            }
            matches.Add(m);
        }

        var board = isBd ? Boards.Bd : Boards.Jc;
        var passType = parsed.PassType
                       ?? RecommendationSelection.PassTypeValue(board, matches.Count);

        return Results.Ok(new
        {
            board = parsed.Board,
            playType = parsed.PlayType,
            passType,
            multiple = parsed.Multiple,
            betAmount = parsed.BetAmount,
            expectedWin = parsed.ExpectedWin,
            ticketKind = parsed.TicketKind ?? kind,
            ticketPreset = parsed.TicketPreset ?? kind,
            teamsOnly = parsed.TeamsOnly,
            rawText = ocrResult?.Text,
            matches
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = $"OCR 失败：{ex.Message}" });
    }
});

// 录入时按场次号带出主客队
app.MapGet("/api/purchase/match", async (string? no, string? board, string? expect, BettingRepository betting, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(no))
        return Results.BadRequest(new { message = "缺少场次号" });
    var hit = await betting.FindCardByNoAsync(no, board, expect, ct);
    if (hit is null && !string.IsNullOrWhiteSpace(board) && no.Trim().StartsWith('周'))
        hit = await betting.FindCardByNoAsync(no, null, null, ct);
    return hit is null
        ? Results.Ok(new { matchNo = no.Trim(), homeTeam = (string?)null, awayTeam = (string?)null })
        : Results.Ok(new { matchNo = hit.MatchNo, homeTeam = hit.HomeTeam, awayTeam = hit.AwayTeam });
});

// 购买方案列表（含明细）
app.MapGet("/api/purchase", async (PurchaseRepository repo, string? date, CancellationToken ct) =>
{
    var list = string.IsNullOrWhiteSpace(date)
        ? await repo.GetAllAsync(ct)
        : await repo.GetByDateAsync(DateTime.Parse(date), ct);
    return Results.Ok(new
    {
        total = list.Count,
        items = list.Select(PurchaseView)
    });
});

// 中奖统计
app.MapGet("/api/purchase/stats", async (PurchaseRepository repo, CancellationToken ct) =>
    Results.Ok(await repo.GetStatsAsync(ct)));

// 录入 / 更新购买方案
app.MapPost("/api/purchase", async (PurchaseUpsertRequest req, PurchaseRepository repo, CancellationToken ct) =>
{
    if (string.Equals(req.PlayType, "胜平负", StringComparison.Ordinal))
    {
        foreach (var line in req.Lines)
        {
            line.Handicap = null;
            line.Option1 = PlainSpfOption(line.Option1);
            line.Option2 = PlainSpfOption(line.Option2);
        }
    }
    var sfc = string.Equals(req.Board, Boards.Sfc, StringComparison.OrdinalIgnoreCase)
              || req.PlayType is "胜负彩" or "任选九";
    if (sfc)
    {
        var why = SfcTicketMath.Normalize(req);
        if (why is not null)
            return Results.BadRequest(new { message = why });
    }
    var id = await repo.UpsertAsync(req, ct);
    return Results.Ok(new { purchaseId = id, betCount = req.BetCount, betAmount = req.BetAmount });
});

// 开奖回填（手动）
app.MapPost("/api/purchase/settle", async (PurchaseSettleRequest req, PurchaseRepository repo, CancellationToken ct) =>
{
    await repo.SettleAsync(req, ct);
    return Results.Ok(new { ok = true, purchaseId = req.PurchaseId });
});

// 自动结算：按完场比分判定命中并计算过关奖金；?id= 只结一张，否则结全部待开奖
app.MapPost("/api/purchase/auto-settle", async (long? id, PurchaseSettlementService svc, CancellationToken ct) =>
{
    var result = await svc.AutoSettleAsync(id, ct);
    return Results.Ok(new
    {
        ok = true,
        result.Checked,
        result.Settled,
        result.Won,
        result.Lost,
        result.Waiting,
        result.Skipped,
        items = result.Items
    });
});

// 删除方案
app.MapDelete("/api/purchase/{id:long}", async (long id, PurchaseRepository repo, CancellationToken ct) =>
{
    await repo.DeleteAsync(id, ct);
    return Results.Ok(new { ok = true });
});

// 不提供选号的彩种：文案从白名单生成，免得以后加彩种忘了改提示
static string UnsupportedLottoMessage() =>
    $"code 应为 {string.Join('/', LottoCodes.All)}；七乐彩/七星彩只采开奖，不提供选号";

// 购买方案投影
static string? PlainSpfOption(string? opt)
{
    if (string.IsNullOrWhiteSpace(opt)) return opt;
    return opt.Replace("让", "", StringComparison.Ordinal)
        .Replace("主胜", "胜", StringComparison.Ordinal)
        .Replace("客胜", "负", StringComparison.Ordinal)
        .Replace("主负", "负", StringComparison.Ordinal);
}

static object PurchaseView(PurchaseRecord p) => new
{
    p.PurchaseId, p.Board, p.Expect, p.PlayType, p.PassType, p.Multiple, p.BetCount, p.BetAmount,
    p.ExpectedWin, p.ActualWin, p.Status, BuyDate = p.BuyDate.ToString("yyyy-MM-dd"),
    p.TicketImage, p.ResultImage, p.IsHit, p.HitLevel, p.Remark,
    lines = p.Lines.Select(l => new
    {
        l.Seq, l.MatchNo, l.LeagueName, l.HomeTeam, l.AwayTeam, l.MatchTime, l.Handicap,
        l.Option1, l.Option2, l.Odds1, l.Odds2, l.ActualScore, l.ActualGoals, l.ActualResult, l.LineHit
    })
};

// 今日推荐（按板块）
app.MapGet("/api/recommend/{board}", async (string board, RecommendationService svc, HttpRequest request, AuthService auth, CancellationToken ct) =>
{
    var token = request.Headers.Authorization.ToString().Replace("Bearer ", "");
    if (!auth.TryGet(token, out _, out _)) return Results.Unauthorized();

    // 2026-11-04 改向：胜负彩、北单推荐下线（采集保留），只发竞彩大奶。
    if (board is not Boards.Jc)
        return Results.BadRequest(new { message = "北单/胜负彩推荐已下线（2026-11-04），只保留采集。请用 /api/recommend 看竞彩大奶。" });

    var recs = await svc.RecommendAsync(board, null, ct);
    var build = await svc.RecommendMilkWithGateAsync(recs, ct);
    var milkPicks = build.Picks;
    var ticket = MilkPickEngine.BuildTicket(milkPicks, DateTime.Today);

    return Results.Ok(new
    {
        board,
        total = recs.Count,
        saleNote = RecommendationSelection.JcSaleHours.Note(RecommendationSelection.JcSaleHours.Now()),
        milkPicks = milkPicks.Select(ToMilkItem),
        milkTicket = ticket is null ? null : ToMilkTicket(ticket),
        milkGate = ToMilkGate(build),
        showRows = build.ShowRows.Select(ToMilkShowRow),
        note = "每日只推荐竞彩大奶：比分、半全场（胜负/负胜/胜平/负平，赔率 ≥4.0）、总进球。EV=概率×赔率，列出只为排序，长期仍期望为负。"
    });
});

// 推荐概览（首页）。2026-11-04 改向：只出竞彩大奶。
app.MapGet("/api/recommend", async (RecommendationService svc, HttpRequest request, AuthService auth, CancellationToken ct) =>
{
    var token = request.Headers.Authorization.ToString().Replace("Bearer ", "");
    if (!auth.TryGet(token, out _, out _)) return Results.Unauthorized();

    var recs = await svc.RecommendAsync(Boards.Jc, null, ct);
    var build = await svc.RecommendMilkWithGateAsync(recs, ct);
    var milkPicks = build.Picks;
    var ticket = MilkPickEngine.BuildTicket(milkPicks, DateTime.Today);

    return Results.Ok(new
    {
        date = DateTime.Now.ToString("yyyy-MM-dd"),
        boards = new[]
        {
            new
            {
                board = Boards.Jc,
                name = "竞彩大奶",
                total = recs.Count,
                saleNote = RecommendationSelection.JcSaleHours.Note(RecommendationSelection.JcSaleHours.Now()),
                milkPicks = milkPicks.Select(ToMilkItem).ToList(),
                milkTicket = ticket is null ? null : ToMilkTicket(ticket),
                milkGate = ToMilkGate(build),
                showRows = build.ShowRows.Select(ToMilkShowRow).ToList()
            }
        },
        // 2026-11-04 起胜平负/让球/大小球全部停止发布（用户拍板：「发出来还是错的，浪费」）。
        // 后台仍在算、仍落 Recommendation 表——模型校准和否决台账只能靠这批数据，
        // 但对外一个方向都不给，页面/接口只留大奶，免得又被当成可下注的建议。
        note = "只发竞彩大奶（比分 / 半全场 / 总进球）。胜平负、让球、大小球已停止发布，后台只留作校准。"
    });
});

static object ToHotItem(MatchRecommendation r)
{
    var side = r.HotSide == '0' ? "客胜" : "主胜";
    return new
    {
        r.MatchNo, r.MatchId, r.Board, r.LeagueName, r.HomeTeam, r.AwayTeam, r.MatchTime, r.Handicap,
        result = r.HotSide?.ToString(),
        resultText = side,
        hotWatch = true,
        hotRatio = r.HotRatio,
        signalDesc = r.HotNote,
        isFeatured = false
    };
}

// 大奶推荐行投影（页面榜）
static object ToMilkItem(MilkPick p) => new
{
    p.MatchNo, p.MatchId, p.LeagueName, p.HomeTeam, p.AwayTeam, p.MatchTime,
    playType = p.PlayType,
    playName = p.PlayName,
    optionCode = p.OptionCode,
    option = MilkPickMath.ShortName(p.PlayType, p.OptionCode),
    display = p.OptionName,
    odds = p.Odds,
    modelProbability = p.ModelProbability,
    ev = p.Ev,
    // 逆市场标记（2026-11-05 加）：前端据此打红标。市场方向过半的注已经在引擎里撤掉了，
    // 能带着这个标记出来的都是弱背离（市场方向 <45%），留给人自己判断。
    divergedFromMarket = p.DivergedFromMarket,
    marketDirection = p.MarketDirection?.ToString(),
    marketDirectionName = p.MarketDirectionName,
    marketDirectionPct = p.MarketDirectionPct,
    // 纯模型标记（2026-11-05 加）：该玩法快照档数不全，概率没掺盘口，EV 会偏高。
    marketPriced = p.MarketPriced
};

// 大奶票投影（与票面台账同源的一张票）
static object ToMilkTicket(TicketDraft t) => new
{
    t.PassType, t.LegCount, t.BetCount, t.Multiple, t.BetAmount,
    chanceAnyPrize = t.HitProbability,
    chanceBackStake = t.BackProbability,
    expectedReturn = t.ExpectedReturn,
    note = "单关：取全榜命中率最高的那一注（≥8%）。串关的命中率是相乘的，两条 20% 的腿串起来只剩 3.9%，和「提高中奖率」直接相反。概率是模型近似，大奶命中率天然低。",
    lines = t.Legs.Select(l => new
    {
        l.Seq, l.MatchNo, l.HomeTeam, l.AwayTeam,
        playType = l.PlayType,
        option = l.PickOption,
        l.Odds,
        probability = l.LegProbability
    })
};

// 方向闸摘要。只把「有话说」的场次发给前端：被拦下的、有诱盘告警的。
// 全部 pass 的场次对页面没用，几十行反而把关键那几行淹掉。
static object ToMilkGate(MilkBuildResult build) => new
{
    diverged = build.DivergedMatches,
    blocked = build.BlockedPicks,
    scanned = build.Gates.Count,
    rows = build.Gates
        .Where(g => g.Verdict == MilkGateVerdicts.Diverge || !string.IsNullOrEmpty(g.AlertCode))
        .OrderBy(g => g.Verdict == MilkGateVerdicts.Diverge ? 0 : 1)
        .Select(g => new
        {
            matchNo = g.MatchNo,
            homeTeam = g.HomeTeam,
            awayTeam = g.AwayTeam,
            matchTime = g.MatchTime?.ToString("yyyy-MM-dd HH:mm"),
            modelDirection = g.ModelDirection?.ToString(),
            pickDirection = g.PickDirection?.ToString(),
            marketDirection = g.MarketDirection?.ToString(),
            marketSource = g.MarketSource,
            marketHomePct = g.MarketHomePct,
            marketDrawPct = g.MarketDrawPct,
            marketAwayPct = g.MarketAwayPct,
            verdict = g.Verdict,
            alertCode = g.AlertCode,
            alertNote = g.AlertNote,
            blockedPicks = g.BlockedPicks,
            blockedBest = g.BlockedBest
        })
        .ToList(),
    // 构建漏斗：空榜时前端直接写明卡在哪一层，不用再让人对着「0 注」猜。
    funnel = new
    {
        recs = build.Funnel.Recs,
        withOdds = build.Funnel.WithOdds,
        named = build.Funnel.Named,
        known = build.Funnel.Known,
        thinSample = build.Funnel.ThinSample,
        distributed = build.Funnel.Distributed,
        candidates = build.Funnel.Candidates,
        blockedByGate = build.Funnel.BlockedByGate,
        afterGate = build.Funnel.AfterGate,
        afterEv = build.Funnel.AfterEv,
        // 逆市场撤注数（2026-11-05 加）：没这个数，用户只会看到「昨天 6 注今天 5 注」，
        // 以为又是随机波动，看不出是规则撤掉的。
        divergentDropped = build.Funnel.DivergentDropped,
        final = build.Funnel.Final,
        scoresReady = build.Funnel.ScoresReady,
        scoresNote = build.Funnel.ScoresNote,
        skipped = build.Funnel.Skipped
    }
};

// 「模型 vs 市场」对照行投影（方案 2，只展示不出注）。
static object ToMilkShowRow(MilkShowRow s) => new
{
    s.MatchNo, s.MatchId, s.LeagueName, s.HomeTeam, s.AwayTeam,
    matchTime = s.MatchTime?.ToString("yyyy-MM-dd HH:mm"),
    modelHomePct = s.ModelHomePct,
    modelDrawPct = s.ModelDrawPct,
    modelAwayPct = s.ModelAwayPct,
    marketHomePct = s.MarketHomePct,
    marketDrawPct = s.MarketDrawPct,
    marketAwayPct = s.MarketAwayPct,
    marketSource = s.MarketSource,
    status = s.Status,
    homeGames = s.HomeGames,
    awayGames = s.AwayGames,
    modelDirection = s.ModelDirection?.ToString(),
    marketDirection = s.MarketDirection?.ToString()
};

static object ToItem(MatchRecommendation r)
{
    // 北单只卖让球胜平负：对外的推荐、命中率都换成让球选项，不让球方向只留在说明里。
    var bd = r.Board == Boards.Bd && r.RecHandicap is not null;
    var pick = bd ? r.RecHandicap : r.RecResult;
    return new
{
    r.MatchNo, r.MatchId, r.Expect, r.LeagueName, r.HomeTeam, r.AwayTeam, r.MatchTime, r.Handicap,
    result = pick?.ToString(),
    resultText = bd
        ? RecommendationSelection.HandicapOptionName(r.RecHandicap!.Value)
        : r.RecResult == '3' ? "主胜" : r.RecResult == '0' ? "客胜" : r.RecResult == '1' ? "平局" : "全包",
    r.Confidence, r.SignalLevel,
    signalDesc = bd && r.HandicapProbability < RecommendationScoring.MinFeaturedHitProbability
        ? (r.SignalDesc ?? "") + "不到 55%，不标强推荐。"
        : r.SignalDesc,
    hitProbability = bd ? r.HandicapProbability : r.HitProbability,
    hitProbabilityNote = bd ? "欧赔去水 + 大小球盘折算到北单让球数" : r.HitProbabilityNote,
    handicapOdds = bd ? r.HandicapOdds : null,
    plainResult = bd ? r.RecResult?.ToString() : null,
    homeOddsDelta = r.HomeOddsDelta, drawOddsDelta = r.DrawOddsDelta,
    ratioHome = r.RatioHome, ratioDraw = r.RatioDraw, ratioAway = r.RatioAway,
    scores = r.ScoreCandidates, goals = r.GoalCandidates,
    refScores = r.RefScores, refGoals = r.RefGoals,
    recHandicap = r.RecHandicap?.ToString(),
    recHandicapText = r.RecHandicap switch { '3' => "让胜", '1' => "让平", '0' => "让负", _ => null },
    halfFullPick = r.HalfFullPick,
    r.IsFeatured, r.PublishMarkets,
    r.Board, r.UpsetWatch, r.UpsetNote,
    r.TotalLine, r.OuSide, r.OuProbability, r.OuPush, r.OuNote
};
}

static (double hitRate, double hitLow, double hitHigh) Rate(int hit, int total)
{
    var w = HitRateMath.Wilson(hit, total);
    return (Math.Round(w.Point * 100, 1), Math.Round(w.Low * 100, 1), Math.Round(w.High * 100, 1));
}

static object WithInterval(int hit, int total)
{
    var rate = Rate(hit, total);
    return new { total, hit, hitRate = rate.hitRate, hitLow = rate.hitLow, hitHigh = rate.hitHigh };
}

// 每日完场规律分析
app.MapGet("/api/analysis/today", async (AnalysisRepository repo, string? date, CancellationToken ct) =>
{
    var d = DateTime.TryParse(date, out var dv) ? dv : DateTime.Today;
    var result = await repo.AnalyzeAsync(d, ct);
    return Results.Ok(result);
});

// 大奶：历史频率（scope=all|card|jc；默认近 90 天）
app.MapGet("/api/milk/stats", async (MilkScanService milk, string? scope, string? from, string? to, CancellationToken ct) =>
{
    DateTime? fromDt = DateTime.TryParse(from, out var f) ? f.Date : null;
    DateTime? toDt = DateTime.TryParse(to, out var t) ? t.Date : null;
    return Results.Ok(await milk.GetStatsAsync(scope, fromDt, toDt, ct));
});

// 大奶：今日受注场观察候选（尾部事件，非主推）
app.MapGet("/api/milk/today", async (MilkScanService milk, CancellationToken ct) =>
    Results.Ok(await milk.GetTodayAsync(ct)));

// 大奶推荐台账：某天落库的大奶榜（含赛果与命中），复盘用
app.MapGet("/api/milk/picks", async (MilkPickRepository repo, string? date, CancellationToken ct) =>
{
    var d = DateTime.TryParse(date, out var dv) ? dv.Date : DateTime.Today;
    return Results.Ok(await repo.GetDayAsync(d, ct));
});

// 大奶台账 KPI：近 N 天按玩法分组的命中率、平注 ROI、模型高估幅度、热门选项
app.MapGet("/api/milk/pick-stats", async (MilkPickRepository repo, int? days, CancellationToken ct) =>
    Results.Ok(await repo.GetReportAsync(days ?? 30, ct)));

// 临场转向告警：系统主动喊出来的「已出注场次在开赛前市场转向」报警（2026-11-05 加）。
// 由 turn-guard 定时任务扫描落库，本端点只读，供数据监控页顶部红标。
app.MapGet("/api/milk/turn-alerts", async (MilkPickRepository repo, string? date, CancellationToken ct) =>
{
    var d = DateTime.TryParse(date, out var dv) ? dv.Date : DateTime.Today;
    return Results.Ok(await repo.GetTurnAlertsAsync(d, ct));
});

// 方向闸回测：用某天的历史盘口重算「这道闸会拦下什么」，不落库、不出票。
// 11-04 三场反掉之后的验证入口，也是以后调阈值时唯一不必等开奖的办法。
// save=1 时把诊断补档进 FbMilkGate——只给「闸上线之前那些天」补历史用，
// 当天的台账由出票作业自己写，不要用这个参数覆盖。
app.MapGet("/api/milk/gate/backtest", async (
    RecommendationService svc, MilkPickRepository repo, string? date, int? save, CancellationToken ct) =>
{
    var d = DateTime.TryParse(date, out var dv) ? dv.Date : DateTime.Today.AddDays(-1);
    var build = await svc.BacktestMilkAsync(d, ct);

    var archived = 0;
    if (save == 1 && build.Gates.Count > 0)
        archived = await repo.SaveGateAsync(d, build.Gates, ct);

    return Results.Ok(new
    {
        date = d.ToString("yyyy-MM-dd"),
        picks = build.Picks.Count,
        diverged = build.DivergedMatches,
        blocked = build.BlockedPicks,
        archived,
        gates = build.Gates,
        board = build.Picks.Select(ToMilkItem)
    });
});

// 方向闸台账（2026-11-05）：某天每场「模型指哪、市场指哪、亚盘/必发有没有背离、挡了几注」。
// 这是「今天为什么没出票」的唯一解释入口；fresh=1 时现场重算一遍（不落库），用于核对已有的榜。
app.MapGet("/api/milk/gate", async (MilkPickRepository repo, RecommendationService svc, string? date, int? fresh, CancellationToken ct) =>
{
    var d = DateTime.TryParse(date, out var dv) ? dv.Date : DateTime.Today;

    if (fresh == 1 && d == DateTime.Today)
    {
        var build = await svc.RecommendMilkWithGateAsync(null, ct);
        return Results.Ok(new
        {
            date = d.ToString("yyyy-MM-dd"),
            source = "live",
            diverged = build.DivergedMatches,
            blocked = build.BlockedPicks,
            picks = build.Picks.Count,
            gates = build.Gates
        });
    }

    var rows = await repo.GetGateAsync(d, ct);
    return Results.Ok(new
    {
        date = d.ToString("yyyy-MM-dd"),
        source = "db",
        diverged = rows.Count(r => r.Verdict == MilkGateVerdicts.Diverge),
        blocked = rows.Sum(r => r.BlockedPicks),
        picks = (int?)null,
        gates = rows
    });
});


app.MapGet("/api/summary/finished", async (SummaryRepository repo, JcOddsRepository jcOdds, BettingCardSource cards, string? date, CancellationToken ct) =>
{
    var d = DateTime.TryParse(date, out var dv) ? dv : DateTime.Today;
    var items = await repo.GetFinishedSummaryAsync(d, ct);
    try { await ReviewOdds.ApplyAsync(items, jcOdds, cards, ct); }
    catch (OperationCanceledException) { throw; }
    catch { /* 赔率页打不开时，复盘照常返回 */ }
    var scored = items.Where(i => i.CountsInStats).ToList();
    var total = scored.Count;
    var hit = scored.Count(x => x.Hit);

    // 按推荐质量分层统计（实购 / 高置信 / 中置信 / 低置信）
    var byQuality = new[] { "bought", "high", "medium", "low" }
        .Select(q => new
        {
            quality = q,
            name = q switch { "bought" => "实购场次", "high" => "高置信(≥70%)", "medium" => "中置信(60~70%)", _ => "低置信(<60%)" },
            total = items.Count(i => i.Quality == q),
            hit = items.Count(i => i.Quality == q && i.Hit)
        })
        .Where(x => x.total > 0)
        .ToList();

    // 实购场次单独统计
    var boughtItems = items.Where(i => i.IsBought && !i.IsCoverAll && i.RecResult is not null).ToList();
    var boughtStat = new
    {
        total = boughtItems.Count,
        hit = boughtItems.Count(i => i.Hit),
        hitRate = boughtItems.Count > 0 ? Math.Round(100.0 * boughtItems.Count(i => i.Hit) / boughtItems.Count, 1) : 0
    };

    // 高置信（≥70%）单独统计，作为「有分析价值」的核心口径
    var highItems = scored.Where(i => i.Confidence >= 70m).ToList();
    var highStat = new
    {
        total = highItems.Count,
        hit = highItems.Count(i => i.Hit),
        hitRate = highItems.Count > 0 ? Math.Round(100.0 * highItems.Count(i => i.Hit) / highItems.Count, 1) : 0
    };

    // 按比赛类型（联赛）分组统计
    var byLeague = scored
        .GroupBy(x => string.IsNullOrEmpty(x.LeagueName) ? "未知" : x.LeagueName)
        .Select(g => new { league = g.Key, total = g.Count(), hit = g.Count(x => x.Hit) })
        .OrderByDescending(x => x.total)
        .ToList();

    // 按推荐方向（胜/平/负）统计命中
    var byResult = new[]
    {
        new { r = '3', name = "推荐主胜" },
        new { r = '1', name = "推荐平局" },
        new { r = '0', name = "推荐客胜" },
    }.Select(x => new
    {
        name = x.name,
        total = scored.Count(i => i.RecResult == x.r),
        hit = scored.Count(i => i.RecResult == x.r && i.Hit)
    }).Where(x => x.total > 0).ToList();

    return Results.Ok(new
    {
        date = d.ToString("yyyy-MM-dd"),
        total,
        hit,
        hitRate = Rate(hit, total).hitRate,
        hitLow = Rate(hit, total).hitLow,
        hitHigh = Rate(hit, total).hitHigh,
        boughtStat = WithInterval(boughtStat.hit, boughtStat.total),
        highStat = WithInterval(highStat.hit, highStat.total),
        byQuality,
        byLeague,
        byResult,
        items,
        document = await ReadDailyReviewAsync(d, ct),
        reviewSchedule = "每天 08:20 写前一天"
    });
});

static async Task<string?> ReadDailyReviewAsync(DateTime day, CancellationToken ct)
{
    try
    {
        var path = Path.Combine(DailyReview.ReviewRoot(), day.ToString("yyyy-MM-dd") + ".md");
        return File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
    }
    catch (DirectoryNotFoundException)
    {
        return null;
    }
}

app.Run();

/// <summary>OCR 队名与库表队名是否像同一队（北单场次号跨期复用时防串场）。</summary>
static bool OcrTeamLooksLike(string? ocr, string? db)
{
    if (string.IsNullOrWhiteSpace(ocr) || string.IsNullOrWhiteSpace(db)) return false;
    var a = ocr.Trim();
    var b = db.Trim();
    if (a.Length < 2) return false;
    // 短名被全称包含，或反过来
    if (b.Contains(a, StringComparison.Ordinal) || a.Contains(b, StringComparison.Ordinal)) return true;
    // 至少连续 3 字重合，避免「里奥」误配「里奥夸尔托」
    if (a.Length < 3) return false;
    for (var i = 0; i <= a.Length - 3; i++)
    {
        if (b.Contains(a.Substring(i, 3), StringComparison.Ordinal)) return true;
    }
    return false;
}

internal sealed record LoginRequest(string UserName, string Password, string? NickName);
internal sealed record SkillWriteRequest(string? Title, string? Body);
internal sealed record DailySkillWriteRequest(string? Date, string? Title, string? Body, string? Evidence);
internal sealed record PasswordChangeRequest(string? CurrentPassword, string? NewPassword, string? ConfirmPassword);

// OCR 响应 DTO（与 Node 端 parse-ticket.js 输出对齐）
internal sealed class OcrResponse
{
    public string? Text { get; set; }
    public OcrParsed Parsed { get; set; } = new();
}

internal sealed class OcrParsed
{
    public string Board { get; set; } = "jc";
    public string? PlayType { get; set; }
    public string? PassType { get; set; }
    public int Multiple { get; set; } = 1;
    public decimal? BetAmount { get; set; }
    public decimal? ExpectedWin { get; set; }
    public string? TicketKind { get; set; }
    public string? TicketPreset { get; set; }
    /// <summary>表头没认全、只兜底带出了场次+对阵（选项留给用户手填）</summary>
    public bool TeamsOnly { get; set; }
    public List<OcrMatch> Matches { get; set; } = new();
}

internal sealed class OcrMatch
{
    public string? MatchNo { get; set; }
    public string? Home { get; set; }
    public string? Away { get; set; }
    public decimal? Handicap { get; set; }
    public string? Option1 { get; set; }
    public string? Option2 { get; set; }
    public decimal? Odds1 { get; set; }
    public decimal? Odds2 { get; set; }
}
