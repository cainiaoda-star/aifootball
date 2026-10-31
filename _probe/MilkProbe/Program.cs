using System.Text;
using System.Text.Json;
using AiFootBall.Data;
using AiFootBall.Sources;
using Microsoft.Extensions.Configuration;

Console.OutputEncoding = Encoding.UTF8;

// 连接串：环境变量 AIFOOTBALL_CONN，否则读 Api appsettings（不把密码写进探针源码）
var conn = Environment.GetEnvironmentVariable("AIFOOTBALL_CONN");
if (string.IsNullOrWhiteSpace(conn))
{
    // 探针源码在 _probe/MilkProbe；运行目录在 bin/Release/net8.0
    var candidates = new[]
    {
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AiFootBall.Api")),
        Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "src", "AiFootBall.Api")),
        Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", "..", "src", "AiFootBall.Api")),
    };
    var apiRoot = candidates.FirstOrDefault(Directory.Exists);
    if (apiRoot is not null)
    {
        var cfg = new ConfigurationBuilder()
            .SetBasePath(apiRoot)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();
        conn = cfg.GetConnectionString("AiFootBall");
    }
}
if (string.IsNullOrWhiteSpace(conn))
{
    Console.Error.WriteLine("缺少连接串：设 AIFOOTBALL_CONN 或提供 src/AiFootBall.Api/appsettings.json");
    return 1;
}

var factory = new SqlConnectionFactory(conn);
var milk = new MilkScanService(new MilkRepository(factory), new BettingRepository(factory));
var opt = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

foreach (var scope in new[] { "all", "card", "jc" })
{
    Console.WriteLine($"===== scope={scope} =====");
    var stats = await milk.GetStatsAsync(scope, null, null);
    Console.WriteLine($"matches={stats.MatchCount} milkMatches={stats.MilkMatchCount} milkDayPct90={stats.MilkDayPct90}%");
    foreach (var t in stats.Tags.Take(8))
        Console.WriteLine($"  {t.Label,-8} count={t.Count,5} rate={t.RatePct,6}% spMed={t.JcSpMedian?.ToString("0.##") ?? "-"}");
    Console.WriteLine("  leagues: " + string.Join(", ", stats.Leagues.Take(5).Select(l => $"{l.League}:{l.MilkMatches}")));
    Console.WriteLine();
}

Console.WriteLine("===== today watch =====");
var today = await milk.GetTodayAsync();
Console.WriteLine($"cards={today.CardCount} watch={today.WatchCount}");
foreach (var it in today.Items.Take(12))
{
    var tags = string.Join(",", it.Tags.Select(t => t.Label));
    Console.WriteLine($"  {it.Board} {it.MatchNo} {it.Home} vs {it.Away} | {tags}");
}

File.WriteAllText(Path.Combine("..", "milk_stats_all.json"), JsonSerializer.Serialize(await milk.GetStatsAsync("all", null, null), opt));
return 0;
