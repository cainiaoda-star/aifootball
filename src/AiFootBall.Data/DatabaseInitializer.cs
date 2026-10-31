using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace AiFootBall.Data;

/// <summary>
/// 数据库初始化器：执行嵌入的 db/*.sql 脚本（按文件名排序），支持 GO 分批。
/// </summary>
public sealed class DatabaseInitializer
{
    private readonly SqlConnectionFactory _factory;
    private static readonly Regex GoSplitter = new(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    public DatabaseInitializer(SqlConnectionFactory factory) => _factory = factory;

    public async Task InitializeAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        var scripts = LoadScripts();
        if (scripts.Count == 0)
            throw new InvalidOperationException("未找到嵌入的 SQL 脚本资源");

        await using var conn = new SqlConnection(_factory.MasterConnectionString);
        await conn.OpenAsync(ct);

        foreach (var (name, sql) in scripts)
        {
            log?.Invoke($"执行脚本 {name} ...");
            var batches = GoSplitter.Split(sql)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList();

            foreach (var batch in batches)
            {
                await using var cmd = new SqlCommand(batch, conn) { CommandTimeout = 60 };
                try
                {
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                catch (SqlException ex)
                {
                    throw new InvalidOperationException(
                        $"脚本 {name} 执行失败：{ex.Message}\n--- 批次 ---\n{batch[..Math.Min(400, batch.Length)]}", ex);
                }
            }
            log?.Invoke($"  完成 {name}（{batches.Count} 个批次）");
        }
    }

    private static List<(string Name, string Sql)> LoadScripts()
    {
        var asm = Assembly.GetExecutingAssembly();
        var result = new List<(string, string)>();
        foreach (var res in asm.GetManifestResourceNames())
        {
            if (!res.StartsWith("Sql.", StringComparison.OrdinalIgnoreCase)) continue;
            using var stream = asm.GetManifestResourceStream(res)!;
            using var reader = new StreamReader(stream);
            result.Add((res.Substring(4), reader.ReadToEnd()));
        }
        result.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return result;
    }
}
