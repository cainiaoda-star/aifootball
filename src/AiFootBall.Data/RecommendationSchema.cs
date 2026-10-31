using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace AiFootBall.Data;

/// <summary>启动时执行推荐表的增量脚本。脚本本身可重复执行。</summary>
public static class RecommendationSchema
{
    private static readonly Regex GoSplitter = new(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    public static async Task EnsureAsync(SqlConnectionFactory factory, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        foreach (var file in new[] { "03_recommendation_integrity.sql", "04_lotto_recommendation.sql" })
        {
            var sql = LoadScript(file);
            foreach (var batch in GoSplitter.Split(sql).Select(x => x.Trim()).Where(x => x.Length > 0))
            {
                await using var cmd = new SqlCommand(batch, conn) { CommandTimeout = 60 };
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
    }

    private static string LoadScript(string fileName)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"未找到嵌入脚本 {fileName}");
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
