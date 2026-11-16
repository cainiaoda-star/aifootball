using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using AiFootBall.Core.Models;
using AiFootBall.Data;
using AiFootBall.Sources;

namespace AiFootBall.Tests;

/// <summary>
/// 测试进程加载到的四个业务程序集必须就是候选目录里的那四份字节。
/// 只做 LoadFrom 和哈希，不调用入口，不启动宿主，不启动调度。
/// </summary>
internal static class CandidateLoadGuard
{
    internal const string CandidateDirectory = "football-97927cbcb5e2";
    internal const string ApiSha = "38E3ADE7A23661B0662F0771930928D5FB4B158CE1A178F0B6128C594AA952D7";
    internal const string CoreSha = "0928542BAD6EEA36CA28FE8710845CFFD699C36E64F8F78E3058288FB6B1B3D0";
    internal const string DataSha = "19BEFE683A7D3F94FB6B1C13F39B705B7F0F68B2AF17BB330532A3C4617EC473";
    internal const string SourcesSha = "1D3E3D68CC03B0E942424DA113E32F81200A55093CB1BB860C500047E00F5022";

    [ModuleInitializer]
    internal static void VerifyLoadedCandidates()
    {
        var root = RepoRoot();
        var dir = Path.GetFullPath(Path.Combine(root, "deploy-package", "pending", CandidateDirectory));
        var lines = new List<string>();
        try
        {
            lines.Add(Describe(typeof(ScorelineModel).Assembly, "AiFootBall.Core", CoreSha, dir));
            lines.Add(Describe(typeof(SqlConnectionFactory).Assembly, "AiFootBall.Data", DataSha, dir));
            lines.Add(Describe(typeof(RecommendationService).Assembly, "AiFootBall.Sources", SourcesSha, dir));

            var apiPath = Path.Combine(dir, "AiFootBall.Api.dll");
            var api = Assembly.LoadFrom(apiPath);
            if (api.EntryPoint is not null && Environment.GetEnvironmentVariable("AIFOOTBALL_CANDIDATE_INVOKE_ENTRY") == "1")
                throw new InvalidOperationException("本复测禁止调用 API 入口。");
            lines.Add(Describe(api, "AiFootBall.Api", ApiSha, dir));
        }
        catch (Exception ex)
        {
            lines.Add("load-error=" + ex.GetType().FullName + " " + ex.Message);
            Write(root, lines);
            throw;
        }

        Write(root, lines);
        if (lines.Exists(line => line.Contains(" match=false", StringComparison.Ordinal)))
            throw new InvalidOperationException("实际加载的程序集与候选 SHA 不一致。见 candidate-load-evidence.txt");
    }

    private static string Describe(Assembly assembly, string name, string expected, string candidateDir)
    {
        var location = assembly.Location;
        if (string.IsNullOrWhiteSpace(location))
            return name + " path= sha= expected=" + expected + " match=false";
        var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(location)));
        var candidatePath = Path.Combine(candidateDir, name + ".dll");
        var candidateSha = File.Exists(candidatePath)
            ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(candidatePath)))
            : "";
        var match = sha.Equals(expected, StringComparison.OrdinalIgnoreCase)
                    && candidateSha.Equals(expected, StringComparison.OrdinalIgnoreCase);
        return name
               + " path=" + location
               + " sha=" + sha
               + " candidate=" + candidatePath
               + " candidateSha=" + candidateSha
               + " expected=" + expected
               + " match=" + (match ? "true" : "false");
    }

    private static void Write(string root, IReadOnlyList<string> lines)
    {
        var text = string.Join(Environment.NewLine, lines) + Environment.NewLine;
        Console.WriteLine(text);
        File.WriteAllText(Path.Combine(root, "candidate-load-evidence.txt"), text);
        File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "candidate-load-evidence.txt"), text);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var pending = Path.Combine(dir.FullName, "source", "aifootball", "sql", "pending", "2026-11-15_milk_model_fit.sql");
            if (File.Exists(pending)) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到待审脚本，无法定位候选 DLL。");
    }
}
