using System.Text.Json;

namespace AiFootBall.Api;

/// <summary>页面上改过的采集时间。有这份文件时盖过 appsettings.json。</summary>
public sealed class JobScheduleEntry
{
    public string Name { get; set; } = "";
    public string Cron { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int? Hours { get; set; }
    public int? RecheckMinutes { get; set; }
}

public static class JobScheduleStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static string FilePath(string contentRoot) => Path.Combine(contentRoot, "job-schedules.json");

    public static List<JobScheduleEntry> Load(string path)
    {
        if (!File.Exists(path)) return new();
        var list = JsonSerializer.Deserialize<List<JobScheduleEntry>>(File.ReadAllText(path), Json);
        return list ?? new();
    }

    public static void Upsert(string path, JobScheduleEntry entry)
    {
        var all = Load(path);
        var index = all.FindIndex(x => x.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) all[index] = entry;
        else all.Add(entry);
        File.WriteAllText(path, JsonSerializer.Serialize(all, Json));
    }
}
