using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiFootBall.Api;

/// <summary>页面上的技能。复盘写的和用户录入的存在同一份文件里。</summary>
public static class SkillBoard
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string FilePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AiFootBall.sln")))
                return Path.Combine(dir.FullName, "skills", "board.json");
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("找不到 AiFootBall.sln，技能不能写到代码目录外。");
    }

    public static SkillFile Read()
    {
        lock (Gate)
        {
            var path = FilePath();
            if (!File.Exists(path))
                return new SkillFile();
            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SkillFile>(text, Json) ?? new SkillFile();
        }
    }

    public static SkillItem Add(string title, string body, string source)
    {
        lock (Gate)
        {
            var file = ReadUnlocked();
            var item = new SkillItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Source = source,
                Title = title.Trim(),
                Body = body.Trim(),
                CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
            };
            file.Items.Add(item);
            WriteUnlocked(file);
            return item;
        }
    }

    public static bool Delete(string id)
    {
        lock (Gate)
        {
            var file = ReadUnlocked();
            var removed = file.Items.RemoveAll(x => string.Equals(x.Id, id, StringComparison.Ordinal));
            if (removed == 0) return false;
            WriteUnlocked(file);
            return true;
        }
    }

    private static SkillFile ReadUnlocked()
    {
        var path = FilePath();
        if (!File.Exists(path)) return new SkillFile();
        var text = File.ReadAllText(path);
        return JsonSerializer.Deserialize<SkillFile>(text, Json) ?? new SkillFile();
    }

    private static void WriteUnlocked(SkillFile file)
    {
        var path = FilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(file, Json));
    }
}

public sealed class SkillFile
{
    public string Note { get; set; } = "这里的技能要学。55% 和必发 75% 不在这里改。";
    public List<SkillItem> Items { get; set; } = new();
}

public sealed class SkillItem
{
    public string Id { get; set; } = "";
    public string Source { get; set; } = "user";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string CreatedAt { get; set; } = "";
}
