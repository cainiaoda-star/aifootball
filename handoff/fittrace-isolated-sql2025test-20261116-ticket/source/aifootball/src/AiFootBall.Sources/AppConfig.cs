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

        // 作业清单住在 jobs.json（2026-11-10 从 appsettings.json 拆出）。
        // appsettings.json 含数据库密码，整份被 .gitignore 排除，作业定义跟着进了黑箱：
        // 版本库里看不到有哪些作业、cron 多少，换台机器恢复配置只能凭记忆重敲，
        // 漏掉一个不会报错 —— 生产上已经因此少了 rebuild-standings，
        // FbMatchRank 从 2026-11-08 起停更两天，SyncLog 里连一行都没有。
        //
        // 保留「appsettings 里有就用 appsettings」的旧路径，是为了老部署包还能跑；
        // 两个都没有才说明配置真的坏了，此时不抛异常 —— CLI 的 initdb 等命令
        // 不需要作业清单，抛了会把它们一起打死。Api 那边是硬要求，由 Program.cs 校验。
        if (jobs.Jobs.Count == 0)
        {
            var jobsPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "jobs.json");
            if (File.Exists(jobsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(jobsPath), new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });
                if (doc.RootElement.TryGetProperty("Jobs", out var jj))
                    jobs = jj.Deserialize<JobsOptions>(JsonOpts) ?? jobs;
            }
        }

        var env = Environment.GetEnvironmentVariable("AIFOOTBALL_CONN");
        if (!string.IsNullOrWhiteSpace(env)) db.ConnectionString = env;

        if (string.IsNullOrWhiteSpace(db.ConnectionString))
            throw new InvalidOperationException("未配置数据库连接串（appsettings.json 或 AIFOOTBALL_CONN 环境变量）");

        return (db, collector, jobs);
    }
}
