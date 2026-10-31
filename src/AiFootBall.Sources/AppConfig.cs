using System.Text.Json;
using AiFootBall.Core.Options;

namespace AiFootBall.Sources;

/// <summary>命令行读取 appsettings.json（与 API 的配置系统相互独立）。</summary>
public static class AppConfig
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static (DatabaseOptions Db, CollectorOptions Collector, JobsOptions Jobs) Load(string? path = null)
    {
        var db = new DatabaseOptions();
        var collector = new CollectorOptions();
        var jobs = new JobsOptions();

        path ??= Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(path))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            var root = doc.RootElement;

            if (root.TryGetProperty("ConnectionStrings", out var cs) &&
                cs.TryGetProperty("AiFootBall", out var conn))
                db.ConnectionString = conn.GetString() ?? "";

            if (root.TryGetProperty("Collector", out var c))
                collector = c.Deserialize<CollectorOptions>(JsonOpts) ?? collector;

            if (root.TryGetProperty("Jobs", out var j))
                jobs = j.Deserialize<JobsOptions>(JsonOpts) ?? jobs;
        }

        var env = Environment.GetEnvironmentVariable("AIFOOTBALL_CONN");
        if (!string.IsNullOrWhiteSpace(env)) db.ConnectionString = env;

        if (string.IsNullOrWhiteSpace(db.ConnectionString))
            throw new InvalidOperationException("未配置数据库连接串（appsettings.json 或 AIFOOTBALL_CONN 环境变量）");

        return (db, collector, jobs);
    }
}
