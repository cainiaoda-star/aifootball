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
    sp.GetRequiredService<MilkScanService>()));
builder.Services.AddSingleton<UserRepository>();
builder.Services.AddSingleton<BettingRepository>();
builder.Services.AddSingleton<AnalysisRepository>();
builder.Services.AddSingleton<PurchaseRepository>();
builder.Services.AddSingleton<JcOddsRepository>();
builder.Services.AddSingleton<SummaryRepository>();
builder.Services.AddSingleton<MilkRepository>();
builder.Services.AddSingleton<MilkScanService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<PurchaseSettlementService>();
builder.Services.AddSingleton(sp => new JobScheduler(
    jobsOptions,
    sp.GetRequiredService<CollectionService>(),
    sp.GetRequiredService<SyncLogRepository>(),
    sp.GetRequiredService<RecommendationService>(),
    sp.GetRequiredService<PurchaseSettlementService>(),
    message => sp.GetRequiredService<ILoggerFactory>().CreateLogger("Scheduler").LogInformation("{Message}", message),
    sp.GetRequiredService<LottoRecommendationService>()));
builder.Services.AddHostedService<SchedulerHostedService>();

var app = builder.Build();
await RecommendationSchema.EnsureAsync(app.Services.GetRequiredService<SqlConnectionFactory>());
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
        return Results.BadRequest(new { message = "code 应为 ssq/dlt/fc3d/kl8/pl3/pl5" });
    return Results.Ok(await svc.GetByCodeAsync(code, ct));
});

app.MapGet("/api/lotto/review/{code}", async (string code, LottoRecommendationService svc, CancellationToken ct) =>
{
    if (!LottoCodes.All.Contains(code, StringComparer.OrdinalIgnoreCase))
        return Results.BadRequest(new { message = "code 应为 ssq/dlt/fc3d/kl8/pl3/pl5" });
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

app.MapGet("/api/jobs/logs", async (int? take, SyncLogRepository repo) =>
    Results.Ok(await repo.GetRecentAsync(take ?? 50)));

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", time = DateTime.Now }));

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
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
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
app.MapGet("/api/recommend/{board}", async (string board, RecommendationService svc, JcOddsRepository jcOdds, HttpRequest request, AuthService auth, CancellationToken ct) =>
{
    var token = request.Headers.Authorization.ToString().Replace("Bearer ", "");
    if (!auth.TryGet(token, out _, out _)) return Results.Unauthorized();

    if (board is not (Boards.Jc or Boards.Bd or Boards.Sfc))
        return Results.BadRequest(new { message = "board 应为 jc/bd/sfc" });

    var recs = await svc.RecommendAsync(board, null, ct);
    var hcpRows = board == Boards.Jc
        ? RecommendationSelection.ForJcHandicap(recs)
        : new List<MatchRecommendation>();
    var handicap = board == Boards.Jc ? hcpRows.Select(ToJcHandicapItem).ToList() : null;
    var draws = board == Boards.Jc
        ? RecommendationSelection.ForJcDraw(recs).Select(ToJcDrawItem).ToList()
        : null;
    var stakePlan = board == Boards.Jc ? await JcStakePlanAsync(jcOdds, hcpRows, ct) : null;
    var strong = RecommendationSelection.ForDisplay(board, recs);
    var pageItems = board == Boards.Jc
        ? RecommendationSelection.ForJcCalculated(recs)
        : board == Boards.Bd
            ? RecommendationSelection.ForBdCalculated(recs)
            : strong;
    var plain = board == Boards.Bd
        ? RecommendationSelection.ForBdPlain(recs).Select(ToBdPlainItem).ToList()
        : null;

    return Results.Ok(new
    {
        board,
        total = recs.Count,
        recommended = strong.Count,
        passTip = RecommendationSelection.PassTip(board, strong.Count),
        passType = RecommendationSelection.PassTypeValue(board, strong.Count),
        saleNote = board == Boards.Jc ? RecommendationSelection.JcSaleHours.Note(RecommendationSelection.JcSaleHours.Now()) : null,
        stakePlan,
        handicapItems = handicap,
        drawItems = draws,
        plainItems = plain,
        items = pageItems.Select(ToItem)
    });
});

// 全板块推荐概览（首页）
app.MapGet("/api/recommend", async (RecommendationService svc, JcOddsRepository jcOdds, HttpRequest request, AuthService auth, CancellationToken ct) =>
{
    var token = request.Headers.Authorization.ToString().Replace("Bearer ", "");
    if (!auth.TryGet(token, out _, out _)) return Results.Unauthorized();

    var boards = new List<object>();
    var upset = new List<MatchRecommendation>();
    var hot = new List<MatchRecommendation>();
    var totals = new List<MatchRecommendation>();
    foreach (var board in new[] { Boards.Jc, Boards.Bd, Boards.Sfc })
    {
        var recs = await svc.RecommendAsync(board, null, ct);
        var hcpRows = board == Boards.Jc
            ? RecommendationSelection.ForJcHandicap(recs)
            : new List<MatchRecommendation>();
        var handicap = board == Boards.Jc ? hcpRows.Select(ToJcHandicapItem).ToList() : null;
        var draws = board == Boards.Jc
            ? RecommendationSelection.ForJcDraw(recs).Select(ToJcDrawItem).ToList()
            : null;
        var stakePlan = board == Boards.Jc ? await JcStakePlanAsync(jcOdds, hcpRows, ct) : null;
        var strong = RecommendationSelection.ForDisplay(board, recs);
        var pageItems = board == Boards.Jc
            ? RecommendationSelection.ForJcCalculated(recs)
            : board == Boards.Bd
                ? RecommendationSelection.ForBdCalculated(recs)
                : strong;
        var plain = board == Boards.Bd
            ? RecommendationSelection.ForBdPlain(recs).Select(ToBdPlainItem).ToList()
            : null;
        hot.AddRange(recs.Where(r => r.HotWatch));
        if (board is Boards.Jc or Boards.Bd)
        {
            upset.AddRange(recs.Where(r => r.UpsetWatch));
            totals.AddRange(recs.Where(r => r.OuSide is not null));
        }

        boards.Add(new
        {
            board,
            name = board == Boards.Jc ? "竞彩" : board == Boards.Bd ? "北单" : "胜负彩",
            total = recs.Count,
            recommended = strong.Count,
            level3 = strong.Count(r => r.SignalLevel == 3),
            slateHit = board == Boards.Sfc ? SlateHit(strong) : null,
            passTip = RecommendationSelection.PassTip(board, strong.Count),
            passType = RecommendationSelection.PassTypeValue(board, strong.Count),
            saleNote = board == Boards.Jc ? RecommendationSelection.JcSaleHours.Note(RecommendationSelection.JcSaleHours.Now()) : null,
            stakePlan,
            handicapItems = handicap,
            drawItems = draws,
            plainItems = plain,
            items = pageItems.Select(ToItem)
        });
    }

    return Results.Ok(new
    {
        date = DateTime.Now.ToString("yyyy-MM-dd"),
        boards,
        upsetWatch = DedupeUpset(upset).Select(ToItem),
        hotWatch = DedupeHot(hot).Select(ToHotItem),
        totals = DedupeWithinBoard(totals.Where(r => r.Board == Boards.Jc))
            .Concat(DedupeWithinBoard(totals.Where(r => r.Board == Boards.Bd)))
            .Select(ToItem)
    });
});

static List<MatchRecommendation> DedupeWithinBoard(IEnumerable<MatchRecommendation> rows)
{
    var seen = new HashSet<long>();
    var kept = new List<MatchRecommendation>();
    foreach (var r in rows
        .OrderByDescending(r => r.OuProbability ?? 0)
        .ThenBy(r => RecommendationSelection.OfficialMatchOrder(r.MatchNo)))
    {
        if (r.MatchId is long id && !seen.Add(id)) continue;
        kept.Add(r);
    }
    return kept;
}

static List<MatchRecommendation> DedupeHot(List<MatchRecommendation> rows)
{
    var seen = new HashSet<string>(StringComparer.Ordinal);
    var kept = new List<MatchRecommendation>();
    foreach (var r in rows)
    {
        var key = (r.Board ?? "") + "|" + (r.MatchId?.ToString() ?? r.MatchNo ?? "");
        if (!seen.Add(key)) continue;
        kept.Add(r);
    }
    return kept;
}

static List<MatchRecommendation> DedupeUpset(List<MatchRecommendation> rows)
{
    var seen = new HashSet<long>();
    var kept = new List<MatchRecommendation>();
    foreach (var r in rows
        .OrderBy(r => r.Board == Boards.Jc ? 0 : 1)
        .ThenBy(r => RecommendationSelection.OfficialMatchOrder(r.MatchNo)))
    {
        if (r.MatchId is long id && !seen.Add(id)) continue;
        kept.Add(r);
    }
    return kept;
}

// 推荐项投影（含比分、进球数预测）
static async Task<object> JcStakePlanAsync(
    JcOddsRepository jcOdds, IReadOnlyList<MatchRecommendation> picks, CancellationToken ct)
{
    var ids = picks.Where(r => r.MatchId is > 0).Select(r => r.MatchId!.Value).Distinct().ToList();
    var prices = ids.Count == 0
        ? new Dictionary<long, (decimal Win, decimal Draw, decimal Loss)>()
        : await jcOdds.GetLatestNspfAsync(ids, ct);

    var legs = new List<(MatchRecommendation Rec, decimal Odds, string Option, decimal? LegP)>();
    foreach (var r in picks)
    {
        if (r.RecHandicap is not ('3' or '1' or '0') || r.MatchId is not long id) continue;
        if (!prices.TryGetValue(id, out var price)) continue;
        if (r.SignalDesc?.Contains("澳超客胜不进精选", StringComparison.Ordinal) == true) continue;
        var leg = RecommendationSelection.JcCoverTicket.TryHandicapLeg(
            r.RecHandicap, r.HandicapProbability, price.Win, price.Draw, price.Loss);
        if (leg is null) continue;
        var legP = RecommendationSelection.JcCoverTicket.LegProbability(leg.Value.Side, price.Win, price.Draw, price.Loss);
        legs.Add((r, leg.Value.Odds, leg.Value.Option, legP));
    }

    var picked = RecommendationSelection.JcCoverTicket.Pick(legs, x => x.Odds, x => x.LegP * 100m);
    if (picked.Count == 0)
        return new { ready = false, have = 0 };

    var n = picked.Count;
    var oddsList = picked.Select(x => x.Odds).ToList();
    var miss = RecommendationSelection.JcCoverTicket.OneMiss(oddsList);
    var miss2 = RecommendationSelection.JcCoverTicket.TwoMiss(oddsList);
    var chances = picked.All(x => x.LegP is not null)
        ? RecommendationSelection.JcCoverTicket.Chances(picked.Select(x => (x.Odds, x.LegP!.Value)).ToList())
        : ((decimal AnyPrize, decimal BackStake, decimal AllHit)?)null;
    return new
    {
        ready = true,
        legCount = n,
        chanceAnyPrize = chances is null ? (decimal?)null : Math.Round(chances.Value.AnyPrize * 100m, 1),
        chanceBackStake = chances is null ? (decimal?)null : Math.Round(chances.Value.BackStake * 100m, 1),
        chanceAllHit = chances is null ? (decimal?)null : Math.Round(chances.Value.AllHit * 100m, 1),
        betCount = RecommendationSelection.JcCoverTicket.BetCountFor(n),
        passType = RecommendationSelection.JcCoverTicket.PassFor(n),
        multiple = RecommendationSelection.JcCoverTicket.Multiple,
        stake = RecommendationSelection.JcCoverTicket.StakeFor(n),
        allHit = RecommendationSelection.JcCoverTicket.AllHit(oddsList),
        missLow = miss.Low,
        missHigh = miss.High,
        miss2Low = miss2.Low,
        miss2High = miss2.High,
        lines = picked.Select(x => new
        {
            x.Rec.MatchNo,
            x.Rec.HomeTeam,
            x.Rec.AwayTeam,
            x.Rec.Handicap,
            option = x.Option,
            odds = x.Odds,
            hitProbability = x.LegP is null ? (decimal?)null : Math.Round(x.LegP.Value * 100m, 1)
        })
    };
}

static object ToJcDrawItem(MatchRecommendation r)
{
    var desc = r.SignalDesc ?? "";
    desc = desc.Replace("（平局回测偏低，不进精选）", "（平局这种形态近 60 日大约 37%，按算出来的数发）", StringComparison.Ordinal);
    desc = desc.Replace("；模型看平，不进精选", "；模型看平", StringComparison.Ordinal);
    return new
    {
        r.MatchNo, r.MatchId, r.LeagueName, r.HomeTeam, r.AwayTeam, r.MatchTime, r.Handicap,
        result = "1",
        resultText = "平局",
        hitProbability = r.HitProbability ?? r.Confidence,
        hitProbabilityNote = r.HitProbabilityNote,
        r.SignalLevel,
        signalDesc = desc,
        isDraw = true
    };
}

static object ToJcHandicapItem(MatchRecommendation r)
{
    var hcp = r.Handicap is decimal h ? (h > 0 ? $"+{h:0.##}" : $"{h:0.##}") : "";
    var name = r.RecHandicap is char side ? RecommendationSelection.HandicapOptionName(side) : "";
    var price = r.HandicapOdds is decimal o ? $"，让球价 {o:0.00}" : "";
    var plain = r.RecResult == '3' ? "主胜" : r.RecResult == '0' ? "客胜" : r.RecResult == '1' ? "平局" : null;
    var plainText = plain is null ? "" : $"不让球参考：{plain}。";
    var settle = r.RecHandicap == '1' ? "刚好走在盘口上才算中。" : "要打过这个盘口。";
    var gate = r.HandicapProbability >= RecommendationScoring.MinFeaturedHitProbability
        ? ""
        : "不到 55%，不标精选。";
    return new
    {
        r.MatchNo, r.MatchId, r.LeagueName, r.HomeTeam, r.AwayTeam, r.MatchTime, r.Handicap,
        result = r.RecHandicap?.ToString(),
        resultText = name,
        hitProbability = r.HandicapProbability,
        hitProbabilityNote = "欧赔去水 + 大小球盘折算到竞彩让球数",
        handicapOdds = r.HandicapOdds,
        signalDesc = $"让{hcp} {name} 约 {r.HandicapProbability:0.0}%{price}。{settle}{plainText}{gate}",
        isHandicap = true
    };
}

static object ToBdPlainItem(MatchRecommendation r)
{
    var side = r.RecResult == '3' ? "主胜" : r.RecResult == '0' ? "客胜" : r.RecResult == '1' ? "平局" : "";
    return new
    {
        r.MatchNo, r.MatchId, r.LeagueName, r.HomeTeam, r.AwayTeam, r.MatchTime,
        result = r.RecResult?.ToString(),
        resultText = side,
        hitProbability = r.HitProbability ?? r.Confidence,
        hitProbabilityNote = r.HitProbabilityNote,
        r.SignalLevel,
        signalDesc = (r.SignalDesc ?? "") + "不让球参考，北单不卖。",
        isPlain = true
    };
}

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

static double? SlateHit(IReadOnlyList<MatchRecommendation> recs)
{
    var directional = recs.Where(r => r.RecResult is '3' or '1' or '0').ToList();
    if (directional.Count == 0 || directional.Any(r => r.HitProbability is null)) return null;
    var product = HitRateMath.Parlay(directional.Select(r => r.HitProbability!.Value / 100m));
    if (product is null) return null;
    var pct = (double)product.Value * 100;
    return pct >= 1 ? Math.Round(pct, 2) : Math.Round(pct, 4);
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
