using Microsoft.Data.SqlClient;

namespace AiFootBall.Data;

/// <summary>SQL Server 连接工厂。每次使用创建新连接，交由连接池复用。</summary>
public sealed class SqlConnectionFactory
{
    private readonly string _connectionString;

    public SqlConnectionFactory(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("连接字符串不能为空", nameof(connectionString));
        _connectionString = connectionString;
    }

    public string ConnectionString => _connectionString;

    public async Task<SqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    /// <summary>切换到 master 库的连接串（用于建库/初始化）</summary>
    public string MasterConnectionString
    {
        get
        {
            var b = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = "master" };
            return b.ConnectionString;
        }
    }
}
