using System.Security.Cryptography;
using System.Text;
using AiFootBall.Core.Models;
using Dapper;
using Microsoft.Data.SqlClient;

namespace AiFootBall.Data;

/// <summary>用户账号（简单账号密码登录，PBKDF2 加盐哈希）</summary>
public sealed class UserRepository
{
    private readonly SqlConnectionFactory _factory;

    public UserRepository(SqlConnectionFactory factory) => _factory = factory;

    /// <summary>创建用户，返回 null 表示用户名已存在</summary>
    public async Task<UserAccount?> CreateAsync(string userName, string password, string? nickName = null, CancellationToken ct = default)
    {
        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var hash = Hash(password, salt);

        await using var conn = await _factory.OpenAsync(ct);
        try
        {
            var id = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                @"INSERT INTO dbo.UserAccount (UserName, PasswordHash, Salt, NickName)
                  VALUES (@u, @h, @s, @n);
                  SELECT CAST(SCOPE_IDENTITY() AS INT);",
                new { u = userName, h = hash, s = salt, n = nickName }, cancellationToken: ct));
            return await GetByUserNameAsync(userName, ct);
        }
        catch (SqlException)
        {
            return null; // 用户名已存在
        }
    }

    /// <summary>校验登录，成功返回用户（并刷新 LastLoginAt）</summary>
    public async Task<UserAccount?> AuthenticateAsync(string userName, string password, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<UserAccount>(new CommandDefinition(
            @"SELECT * FROM dbo.UserAccount WHERE UserName = @u",
            new { u = userName }, cancellationToken: ct));
        if (row is null) return null;

        if (!FixedTimeEquals(row.PasswordHash, Hash(password, row.Salt))) return null;

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.UserAccount SET LastLoginAt = SYSDATETIME() WHERE UserId = @id",
            new { id = row.UserId }, cancellationToken: ct));
        row.LastLoginAt = DateTime.Now;
        return row;
    }

    /// <summary>校验当前密码后更换。当前密码不对返回 false。</summary>
    public async Task<bool> ChangePasswordAsync(string userName, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<UserAccount>(new CommandDefinition(
            "SELECT * FROM dbo.UserAccount WHERE UserName = @u",
            new { u = userName }, cancellationToken: ct));
        if (row is null) return false;
        if (!FixedTimeEquals(row.PasswordHash, Hash(currentPassword, row.Salt))) return false;

        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var hash = Hash(newPassword, salt);
        var n = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.UserAccount SET PasswordHash = @h, Salt = @s WHERE UserId = @id",
            new { h = hash, s = salt, id = row.UserId }, cancellationToken: ct));
        return n == 1;
    }

    public async Task<UserAccount?> GetByUserNameAsync(string userName, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<UserAccount>(new CommandDefinition(
            "SELECT * FROM dbo.UserAccount WHERE UserName = @u",
            new { u = userName }, cancellationToken: ct));
    }

    private static string Hash(string password, string salt)
    {
        var bytes = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            Convert.FromHexString(salt),
            10000, HashAlgorithmName.SHA256, 32);
        return Convert.ToHexString(bytes);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
