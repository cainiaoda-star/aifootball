using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AiFootBall.Core.Models;

/// <summary>一场进入拟合累加的样本。原始名和规范化名都留，公式仍只用规范化名。</summary>
public sealed class FitSampleRow
{
    public long MatchId { get; init; }
    public string LeagueName { get; init; } = "";
    public string HomeTeam { get; init; } = "";
    public string AwayTeam { get; init; } = "";
    public string CanonHome { get; init; } = "";
    public string CanonAway { get; init; } = "";
    public int HomeScore { get; init; }
    public int AwayScore { get; init; }
    public DateTime? MatchTime { get; init; }

    public static FitSampleRow From(ScoreSample raw)
    {
        var league = raw.LeagueName?.Trim() ?? "";
        return new FitSampleRow
        {
            MatchId = raw.MatchId,
            LeagueName = league,
            HomeTeam = raw.HomeTeam?.Trim() ?? "",
            AwayTeam = raw.AwayTeam?.Trim() ?? "",
            CanonHome = TeamAliases.Canonical(raw.HomeTeam),
            CanonAway = TeamAliases.Canonical(raw.AwayTeam),
            HomeScore = raw.HomeScore,
            AwayScore = raw.AwayScore,
            MatchTime = raw.MatchTime
        };
    }
}

/// <summary>联赛累计。N 是场次，Weight 是权重，两者分开。</summary>
public readonly record struct FitLeagueTotal(string League, double HomeSum, double AwaySum, double Weight, int N);

/// <summary>本联赛里一支队的进球、失球和真实场次。</summary>
public readonly record struct FitTeamLeagueTotal(
    string League, string Team, double GoalsFor, double GoalsAgainst, double Weight, int Games);

/// <summary>跨联赛累计。它不是一场的最终 λ。</summary>
public readonly record struct FitTeamTotal(string Team, double AttSum, double DefSum, double Weight, int Games);

/// <summary>
/// 一次拟合里、时钟之外的内容。相同内容指纹相同，与 fitMoment 无关。
/// </summary>
public sealed class FitModelTrace
{
    public required IReadOnlyList<FitSampleRow> Samples { get; init; }
    public required IReadOnlyList<FitLeagueTotal> Leagues { get; init; }
    public required IReadOnlyList<FitTeamLeagueTotal> TeamLeagues { get; init; }
    public required IReadOnlyList<FitTeamTotal> Teams { get; init; }
    public double GlobalHome { get; init; }
    public double GlobalAway { get; init; }
    public double Rho { get; init; }
    public double? HalfLifeDays { get; init; }
    public bool CrossLeague { get; init; }

    public static FitModelTrace Capture(
        IReadOnlyList<FitSampleRow> samples,
        IReadOnlyDictionary<string, (double HomeSum, double AwaySum, double Weight, int N)> leagues,
        IReadOnlyDictionary<string, (double For, double Against, double Weight, int Games)> teams,
        IReadOnlyDictionary<string, (double AttSum, double DefSum, double Weight, int Games)> teamTotals,
        double globalHome,
        double globalAway,
        double rho,
        double? halfLifeDays,
        bool crossLeague)
    {
        return new FitModelTrace
        {
            Samples = samples.ToList(),
            GlobalHome = globalHome,
            GlobalAway = globalAway,
            Rho = rho,
            HalfLifeDays = halfLifeDays,
            CrossLeague = crossLeague,
            Leagues = leagues
                .Select(kv => new FitLeagueTotal(kv.Key, kv.Value.HomeSum, kv.Value.AwaySum, kv.Value.Weight, kv.Value.N))
                .OrderBy(x => x.League, StringComparer.Ordinal)
                .ToList(),
            TeamLeagues = teams
                .Select(kv =>
                {
                    var (league, team) = SplitKey(kv.Key);
                    return new FitTeamLeagueTotal(league, team, kv.Value.For, kv.Value.Against, kv.Value.Weight, kv.Value.Games);
                })
                .OrderBy(x => x.League, StringComparer.Ordinal)
                .ThenBy(x => x.Team, StringComparer.Ordinal)
                .ToList(),
            Teams = teamTotals
                .Select(kv => new FitTeamTotal(kv.Key, kv.Value.AttSum, kv.Value.DefSum, kv.Value.Weight, kv.Value.Games))
                .OrderBy(x => x.Team, StringComparer.Ordinal)
                .ToList()
        };
    }

    /// <summary>与 <c>ScorelineModel.Key</c> 同一分隔。队名本身不含这个字符。</summary>
    public static (string League, string Team) SplitKey(string key)
    {
        var i = key.IndexOf('\n');
        if (i < 0) return ("", key);
        return (key[..i], key[(i + 1)..]);
    }
}

/// <summary>
/// 内容指纹。时钟不进哈希。字节规则见留痕方案：UTF-8、无 BOM、<c>\n</c> 分行、<c>0x1F</c> 分字段。
/// </summary>
public static class FitContentCodec
{
    public const string Schema = "fit-content-1";
    public const char Field = '\u001f';
    public const int WindowMonths = 18;
    public const int ScoreMin = 0;
    public const int ScoreMax = 12;
    public const int PriorGames = 8;
    public const int MinOwnGames = 5;
    public const int LeaguePrior = 30;
    public const double LambdaMin = 0.15;
    public const double LambdaMax = 4.5;
    public const double RateMin = 0.2;
    public const double RateMax = 4.5;
    public const double ScaleMin = 0.65;
    public const double ScaleMax = 1.55;
    public const double HalfGoalShare = 0.45;
    public const int MaxHalfGoals = 12;

    public static (string Informational, string Mvid) CodeIdentity()
    {
        var asm = typeof(FitContentCodec).Assembly;
        var info = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "";
        var mvid = asm.ManifestModule.ModuleVersionId.ToString("N");
        return (info, mvid);
    }

    public static string Hash(FitModelTrace trace)
    {
        var bytes = Encoding.UTF8.GetBytes(Text(trace));
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public static string MapSha(IEnumerable<(string From, string To)> pairs)
    {
        var text = string.Join('\n', pairs.Select(p => p.From + Field + p.To));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public static string Text(FitModelTrace trace)
    {
        var sb = new StringBuilder();
        sb.Append(Schema).Append('\n');
        sb.Append(ParameterLine(trace)).Append('\n');
        foreach (var row in Ordered(trace.Samples))
        {
            sb.Append(SampleLine(row)).Append('\n');
        }
        return sb.ToString();
    }

    public static string ParameterLine(FitModelTrace trace)
    {
        var (info, mvid) = CodeIdentity();
        var half = trace.HalfLifeDays is double h
            ? h.ToString("0.0###", CultureInfo.InvariantCulture)
            : "null";
        var fields = new[]
        {
            info,
            mvid,
            trace.Rho.ToString("0.000", CultureInfo.InvariantCulture),
            half,
            trace.CrossLeague ? "1" : "0",
            WindowMonths.ToString(CultureInfo.InvariantCulture),
            ScorelineModel.MatchDurationMinutes.ToString(CultureInfo.InvariantCulture),
            ScoreMin.ToString(CultureInfo.InvariantCulture),
            ScoreMax.ToString(CultureInfo.InvariantCulture),
            PriorGames.ToString(CultureInfo.InvariantCulture),
            MinOwnGames.ToString(CultureInfo.InvariantCulture),
            LeaguePrior.ToString(CultureInfo.InvariantCulture),
            LambdaMin.ToString("0.00", CultureInfo.InvariantCulture),
            LambdaMax.ToString("0.0", CultureInfo.InvariantCulture),
            RateMin.ToString("0.0", CultureInfo.InvariantCulture),
            RateMax.ToString("0.0", CultureInfo.InvariantCulture),
            ScaleMin.ToString("0.00", CultureInfo.InvariantCulture),
            ScaleMax.ToString("0.00", CultureInfo.InvariantCulture),
            HalfGoalShare.ToString("0.00", CultureInfo.InvariantCulture),
            MaxHalfGoals.ToString(CultureInfo.InvariantCulture)
        };
        return string.Join(Field, fields);
    }

    public static IEnumerable<FitSampleRow> Ordered(IReadOnlyList<FitSampleRow> samples)
        => samples
            .OrderBy(s => s.MatchTime?.Ticks ?? long.MinValue)
            .ThenBy(s => s.MatchId);

    public static string SampleLine(FitSampleRow row)
    {
        var time = row.MatchTime is DateTime t
            ? t.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture)
            : "";
        var kind = row.MatchTime is DateTime k ? ((int)k.Kind).ToString(CultureInfo.InvariantCulture) : "";
        var fields = new[]
        {
            row.MatchId.ToString(CultureInfo.InvariantCulture),
            row.LeagueName,
            row.HomeTeam,
            row.AwayTeam,
            row.CanonHome,
            row.CanonAway,
            row.HomeScore.ToString(CultureInfo.InvariantCulture),
            row.AwayScore.ToString(CultureInfo.InvariantCulture),
            time,
            kind
        };
        return string.Join(Field, fields);
    }
}

/// <summary>存储层一次绑定的结果。发布只认 <see cref="Committed"/>，不再另传一个就绪布尔。</summary>
public readonly record struct MilkFitBindResult(bool Committed, string Reason)
{
    public static MilkFitBindResult Ok() => new(true, "");

    public static MilkFitBindResult Reject(string reason) => new(false, reason);
}

/// <summary>发布前的留痕判据。唯一依据是存储层已经提交的绑定。</summary>
public static class MilkFitTraceGate
{
    public const string BlockReason = "模型留痕未提交";

    public static bool AllowsPublish(MilkFitBindResult result) => result.Committed;
}

/// <summary>
/// 留痕失败的原因。只保留异常类型和消息，抹掉连接串。
/// 原因写在当次快照上；发布不读可能被下一次刷新盖掉的全局字段。
/// </summary>
public static class MilkFitTraceDiagnostics
{
    public const string RollbackDataKey = "MilkFitRollback";

    public static string BindRejected(string runId, long? instanceId, string detail)
    {
        var id = instanceId is > 0 ? instanceId.Value.ToString(CultureInfo.InvariantCulture) : "无";
        return Scrub($"{MilkFitTraceGate.BlockReason}：轮次 {runId} 实例 {id}；{detail}");
    }

    public static string DescribePersist(Exception ex, DateTime fitCutoff, DateTime queryClock)
        => Scrub(
            "拟合时刻 " + fitCutoff.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture)
            + " 查询时钟 " + queryClock.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture)
            + " 实例未提交；" + Describe(ex));

    public static string WithSnapshot(string reason, string? snapshotTraceError)
    {
        if (string.IsNullOrEmpty(snapshotTraceError) || reason.Contains(snapshotTraceError, StringComparison.Ordinal))
            return Scrub(reason);
        return Scrub(reason + "；本轮快照：" + snapshotTraceError);
    }

    public static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (var cursor = ex; cursor is not null && parts.Count < 4; cursor = cursor.InnerException)
        {
            parts.Add(cursor.GetType().Name + ": " + cursor.Message);
            if (cursor.Data.Contains(RollbackDataKey) && cursor.Data[RollbackDataKey] is string extra && extra.Length > 0)
                parts.Add("回滚失败: " + extra);
        }
        return Scrub(string.Join(" | ", parts));
    }

    /// <summary>回滚失败只附加在原始异常上，不换成新的外层异常。</summary>
    public static void AttachRollback(Exception original, Exception rollbackFailure)
    {
        original.Data[RollbackDataKey] = Scrub(rollbackFailure.GetType().Name + ": " + rollbackFailure.Message);
    }

    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var cleaned = Regex.Replace(
            text,
            @"(?i)\b(password|pwd|user\s*id|uid)\s*=\s*[^\s;；,]+",
            "$1=***");
        cleaned = Regex.Replace(
            cleaned,
            @"(?i)\b(data\s*source|server|addr|address|network\s*address)\s*=\s*[^\s;；,]+",
            "$1=***");
        return cleaned.Length > 500 ? cleaned[..500] : cleaned;
    }
}
