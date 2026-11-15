using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;

namespace AiFootBall.Tests;

/// <summary>
/// 用冻结包里的 82c93bce 二进制读写。当前工程里的仓库类不能代替这次调用。
/// 程序来源只认 <c>deploy-package/pending/football-5c3911c</c>，四个 DLL 的 SHA256 必须与部署记录一致。
/// 不使用现网内容根 <c>deploy-package/app/api</c>：那里有生产连接配置和正在运行的调度。
/// 不启动 <c>AiFootBall.Api</c>，也不加载 <c>AiFootBall.Sources</c>。API 宿主会打开调度、预热模型并查找采集脚本。
/// 连接串只接受调用方已经核对过的隔离库；生产机上直接拒绝，不打开连接。
/// </summary>
public static class LegacyBinaryProbe
{
    public const string RunId = "legacy-82c93bce";

    public const string ApiSha = "1BE1E9E0F979DC9450C40F17AA113416CB36732F392399AA30362F23A3A7ED4A";
    public const string CoreSha = "DB3284B6B82CA2F7B613A6BC790351D2CD8F6E7EAAF567B902224B191400F7D2";
    public const string DataSha = "619BAC146E341CFA1A0E9E265C45A2E3A20CA2DBDB2E17179A2694EF3925F976";
    public const string SourcesSha = "712672D4ED2AEB4D9094A9499679492A44568A677905CFAB24EBF8A9A791F521";

    public static string PackageDirectory(string repoRoot)
        => Path.GetFullPath(Path.Combine(repoRoot, "deploy-package", "pending", "football-5c3911c"));

    public static void AssertDirectoryAllowed(string directory)
    {
        var full = Path.GetFullPath(directory);
        var live = Path.Combine("deploy-package", "app", "api");
        if (full.Contains(live, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "不得使用现网内容根 deploy-package/app/api。不从那里启动程序，也不读取它旁边的配置。");
    }

    public static void AssertPackage(string repoRoot)
    {
        var dir = PackageDirectory(repoRoot);
        AssertDirectoryAllowed(dir);
        if (!Directory.Exists(dir))
            throw new InvalidOperationException("找不到冻结包：" + dir);
        AssertSha(dir, "AiFootBall.Api.dll", ApiSha);
        AssertSha(dir, "AiFootBall.Core.dll", CoreSha);
        AssertSha(dir, "AiFootBall.Data.dll", DataSha);
        AssertSha(dir, "AiFootBall.Sources.dll", SourcesSha);
    }

    public static void AssertIsolatedConnection(string connectionString)
    {
        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(connectionString);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("连接串无法解析：" + ex.GetType().Name);
        }

        if (!string.Equals(builder.InitialCatalog, FitTraceDbGuard.DatabaseName, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "冻结程序只允许库 " + FitTraceDbGuard.DatabaseName + "。");
        if (FitTraceDbGuard.IsDocumentedProductionTarget(builder.DataSource))
            throw new InvalidOperationException("冻结程序拒绝已记录的生产地址。");
        if (string.Equals(Environment.MachineName, FitTraceDbGuard.ProductionMachine, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "生产机 " + FitTraceDbGuard.ProductionMachine + " 不启动冻结程序，也不连接。");
    }

    /// <summary>只加载 Core 与 Data，确认旧入口还在。不调用它们，也不连接数据库。</summary>
    public static IReadOnlyList<string> Inspect(string repoRoot)
    {
        AssertPackage(repoRoot);
        var alc = new LegacyAlc(PackageDirectory(repoRoot));
        var data = alc.LoadFromAssemblyPath(Path.Combine(PackageDirectory(repoRoot), "AiFootBall.Data.dll"));
        var repo = data.GetType("AiFootBall.Data.MilkPickRepository")
                   ?? throw new InvalidOperationException("冻结的 Data 程序集里没有 MilkPickRepository。");
        _ = Method(repo, "StartRunAsync", 5);
        _ = Method(repo, "SaveInputsAsync", 4);
        _ = Method(repo, "GetBoardStatusAsync", 2);
        var names = alc.Assemblies
            .Select(a => a.GetName().Name ?? "")
            .Where(n => n.StartsWith("AiFootBall.", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        if (names.Contains("AiFootBall.Api") || names.Contains("AiFootBall.Sources"))
            throw new InvalidOperationException("冻结探针加载了 API 或采集程序集。");
        return names;
    }

    public static async Task<LegacyReadback> ExerciseAsync(string connectionString, string repoRoot)
    {
        AssertIsolatedConnection(connectionString);
        AssertPackage(repoRoot);
        var dir = PackageDirectory(repoRoot);
        var alc = new LegacyAlc(dir);
        var core = alc.LoadFromAssemblyPath(Path.Combine(dir, "AiFootBall.Core.dll"));
        var data = alc.LoadFromAssemblyPath(Path.Combine(dir, "AiFootBall.Data.dll"));
        var factory = Activator.CreateInstance(data.GetType("AiFootBall.Data.SqlConnectionFactory")!, connectionString)!;
        var repo = Activator.CreateInstance(data.GetType("AiFootBall.Data.MilkPickRepository")!, factory)!;

        var day = new DateTime(2026, 11, 1);
        var run = Activator.CreateInstance(core.GetType("AiFootBall.Core.Models.MilkRun")!)!;
        Set(run, "RunId", RunId);
        Set(run, "StartedAt", day.AddHours(8).AddMinutes(40));
        Set(run, "RawMatches", 1);
        Set(run, "SalesDates", Array.Empty<DateTime>());
        Set(run, "VersionTag", "1.0.0.0+82c93bce");

        var matchType = core.GetType("AiFootBall.Core.Models.MilkInputMatch")!;
        var match = Activator.CreateInstance(matchType)!;
        Set(match, "MatchId", 88011L);
        Set(match, "MatchNo", "周日011");
        Set(match, "LeagueName", "英超");
        Set(match, "HomeTeam", "甲队");
        Set(match, "AwayTeam", "乙队");
        Set(match, "MatchTime", day.AddHours(20));
        Set(match, "VersionTag", "1.0.0.0+82c93bce");
        var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(matchType))!;
        list.Add(match);

        await Call(repo, Method(repo.GetType(), "StartRunAsync", 5),
            new object?[] { run, day, "skip", "冻结二进制读写", CancellationToken.None });
        var written = await Call(repo, Method(repo.GetType(), "SaveInputsAsync", 4),
            new object?[] { day, list, RunId, CancellationToken.None });
        if (written is not int rows || rows != 1)
            throw new InvalidOperationException("冻结程序写入的输入行数不是 1。");

        var status = await Call(repo, Method(repo.GetType(), "GetBoardStatusAsync", 2),
            new object?[] { day, CancellationToken.None });
        var runId = status?.GetType().GetProperty("LastRunId")?.GetValue(status) as string;
        var state = status?.GetType().GetProperty("LastStatus")?.GetValue(status) as string;
        if (runId is null || !string.Equals(runId, RunId, StringComparison.Ordinal))
            throw new InvalidOperationException("冻结程序读回的 RunId 不是刚写入的那一轮。");
        return new LegacyReadback(runId, state, rows);
    }

    private static void AssertSha(string dir, string file, string expected)
    {
        var path = Path.Combine(dir, file);
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(sha.ComputeHash(stream));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(file + " 不是冻结的 82c93bce。");
    }

    private static void Set(object target, string name, object? value)
    {
        var prop = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
                   ?? throw new InvalidOperationException("冻结程序没有属性 " + name + "。");
        prop.SetValue(target, value);
    }

    private static MethodInfo Method(Type type, string name, int parameters)
    {
        var found = type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => m.Name == name && m.GetParameters().Length == parameters)
            .ToList();
        if (found.Count != 1)
            throw new InvalidOperationException("冻结程序的 " + name + " 重载数量不是 1。");
        return found[0];
    }

    private static async Task<object?> Call(object target, MethodInfo method, object?[] args)
    {
        var task = (Task)method.Invoke(target, args)!;
        await task.ConfigureAwait(false);
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    private sealed class LegacyAlc : AssemblyLoadContext
    {
        private readonly string _dir;

        public LegacyAlc(string dir) : base("legacy-82c93bce")
        {
            _dir = dir;
            Resolving += (_, name) =>
            {
                if (name.Name is not null && name.Name.StartsWith("AiFootBall.", StringComparison.Ordinal))
                    throw new InvalidOperationException("不得加载 " + name.Name + "。只允许冻结的 Core 与 Data。");
                return Default.LoadFromAssemblyName(name);
            };
        }

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is "AiFootBall.Core" or "AiFootBall.Data")
                return LoadFromAssemblyPath(Path.Combine(_dir, name.Name + ".dll"));
            if (name.Name is "AiFootBall.Api" or "AiFootBall.Sources" or "AiFootBall.Collector" or "AiFootBall.Backtest")
                throw new InvalidOperationException("不得加载 " + name.Name + "。不启动调度，也不访问采集。");
            return null;
        }
    }
}

public readonly record struct LegacyReadback(string RunId, string? Status, int InputRows);
