using System.Globalization;

namespace AiFootBall.Core.Models.Candidate;

/// <summary>
/// 决策归档要核对的交付物。清单文本可以改，批准只认正在运行的程序和原始数据行。
/// </summary>
public static class DecisionDelivery
{
    public const string DecisionRunId = "jingcai-decision-v8";
    public const string ApprovedRule = "one-ticket-ev-positive-T1.1";
    public const string ApprovedEvidence = "retrospective-not-unseen";
    public const string ShanghaiZone = "Asia/Shanghai";
    public static readonly TimeSpan ShanghaiOffset = TimeSpan.FromHours(8);

    public static readonly string[] RequiredRoles =
    {
        "dll-core", "dll-sources", "dll-data", "dll-api",
        "model-parameters", "data-books", "data-odds", "data-history", "fit-snapshots", "protocol"
    };

    /// <summary>
    /// 夹具测试可以暂时关掉。交付 DLL 默认打开。
    /// 打开时，清单旁边的文件必须等于这里钉住的哈希，自签副本不能代替。
    /// </summary>
    internal static bool EnforceDeliverySeal = true;

    public static string? DeliveryFileHash(string role) => role switch
    {
        "dll-sources" => "EA0FF901262FD3F9E908993EA640C9F85B7B8D13E0D0AE6E7E2E0652F7377810",
        "dll-data" => "5957CA2434903F6B90FABB2215E53DC345D4AD37813ACD6545A6964D94E7B624",
        "dll-api" => "3A5D885A23D8C37F22D427F246D3762D8BEDA554F04D0B49778564161D2E5647",
        "data-books" => "F3F78B48D71DA54DC743A17895E471675302C5AE5BD1D36A9528353AE4F0611C",
        "data-odds" => "E9EF6CCEB9F6199C7290C3917194D23607ECAA57887763983373E8C8E6396149",
        "data-history" => "3108B4E374B29A7FAB72935DDB548C280705BC884607462950DB05A917E3B63C",
        "fit-snapshots" => "9153FEA31A7CD8C11906BCC9AD4D04FBCADE56EB6BD5C3FB8A79832C2839F033",
        "protocol" => "9592A2056635822DEFE45EFEA74EAC5B1DF217DA5DAA70F39FE770E386F7080B",
        _ => null
    };

    public static DateTime ShanghaiToUtc(DateTime shanghaiLocal)
    {
        var local = DateTime.SpecifyKind(shanghaiLocal, DateTimeKind.Unspecified);
        return DateTime.SpecifyKind(local - ShanghaiOffset, DateTimeKind.Utc);
    }

    /// <summary>
    /// 研究/候选运行温度。须与 <see cref="ResearchEvidenceBridge.ResearchFrozenTemperature"/> 及真实 Fit 结果一致；
    /// 禁止只改 RuntimeModel 字符串而预测仍用旧 T。
    /// </summary>
    public const double RuntimeTemperature = 1.25;

    public static string RuntimeModel()
        => "dc1|rho=" + ScorelineModel.DefaultRho.ToString("0.000", CultureInfo.InvariantCulture)
           + "|temperature=" + RuntimeTemperature.ToString("0.00", CultureInfo.InvariantCulture)
           + "|tau=0-0,1-0,0-1,1-1|" + BuildInfo.Version;

    public static string LoadedCorePath => typeof(BuildInfo).Assembly.Location;

    public static DateTime TrainingCutoff(DateTime kickoff)
    {
        var decisionAt = kickoff.AddMinutes(-150);
        return decisionAt > decisionAt.Date ? decisionAt.Date : decisionAt.AddTicks(-1);
    }

    public static string MarketText(IEnumerable<(string Code, decimal Odds)> prices)
        => string.Join(";", prices.Select(o => o.Code + "=" + o.Odds.ToString("0.000", CultureInfo.InvariantCulture)));

    public static string HashText(string text)
        => CandidateArchive.Hash(text ?? "");

    public static string HashFile(string path)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    public sealed class RoleFile
    {
        public string Role { get; init; } = "";
        public string Path { get; init; } = "";
        public string Hash { get; init; } = "";
    }

    public static string? CheckSet(IReadOnlyList<RoleFile> roles)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in roles)
        {
            if (!seen.Add(role.Role)) return "blocked:required-file-duplicate";
            if (Array.IndexOf(RequiredRoles, role.Role) < 0) return "blocked:required-file-extra";
        }
        foreach (var required in RequiredRoles)
            if (!seen.Contains(required)) return "blocked:required-file-missing";
        return null;
    }

    public static string? Bind(IReadOnlyDictionary<string, string> fields, string booksText, string oddsText, string historyText)
    {
        if (!string.Equals(fields.GetValueOrDefault("runId"), DecisionRunId, StringComparison.Ordinal))
            return "blocked:source-row-mismatch";
        var play = fields.GetValueOrDefault("play") ?? "";
        if (play == "BQ") return "blocked:half-full-joint-missing";
        if (play is not ("BF" or "JQ" or "SPF" or "NSPF")) return "blocked:source-row-mismatch";
        if (!long.TryParse(fields.GetValueOrDefault("matchId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var matchId))
            return "blocked:source-row-mismatch";
        var books = ParseBooks(booksText);
        var matches = new List<int>();
        for (var i = 0; i < books.Count; i++)
            if (books[i].MatchId == matchId && books[i].Play == play) matches.Add(i);
        if (matches.Count == 0) return "blocked:source-row-mismatch";
        if (matches.Count > 1) return "blocked:source-row-duplicate";
        var book = books[matches[0]];
        if (!book.HasOddsAt) return "blocked:time-missing";
        if (!DateTime.TryParse(fields.GetValueOrDefault("asOf"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var oddsAt))
            return "blocked:time-missing";
        if (oddsAt > book.OddsAt) return "blocked:future-time";
        if (oddsAt != book.OddsAt) return "blocked:source-row-mismatch";
        if (oddsAt > book.Kickoff) return "blocked:future-time";
        var handicapText = fields.GetValueOrDefault("handicap") ?? "";
        if (play == "NSPF")
        {
            if (!book.HasHandicap) return "blocked:handicap-missing";
            if (!decimal.TryParse(handicapText, NumberStyles.Number, CultureInfo.InvariantCulture, out var handicap))
                return "blocked:handicap-missing";
            if (handicap != book.Handicap) return "blocked:source-row-mismatch";
        }
        else if (handicapText == "absent")
        {
            if (book.HasHandicap) return "blocked:source-row-mismatch";
        }
        else
        {
            if (!book.HasHandicap) return "blocked:handicap-missing";
            if (!decimal.TryParse(handicapText, NumberStyles.Number, CultureInfo.InvariantCulture, out var handicap))
                return "blocked:source-row-mismatch";
            if (handicap != book.Handicap) return "blocked:source-row-mismatch";
        }
        var cutoff = TrainingCutoff(book.Kickoff);
        if (!DateTime.TryParse(fields.GetValueOrDefault("trainCutoff"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var claimed))
            return "blocked:source-row-mismatch";
        if (claimed != cutoff) return "blocked:source-row-mismatch";
        if (claimed > book.Kickoff) return "blocked:future-time";
        var market = MarketText(ParseOdds(oddsText).Where(o => o.MatchId == matchId && o.Play == play).Select(o => (o.Code, o.Odds)));
        if (!string.Equals(market, fields.GetValueOrDefault("oddsSnapshot"), StringComparison.Ordinal))
            return "blocked:source-row-mismatch";
        if (!ParseHistory(historyText).Any(h => h == matchId)) return "blocked:source-row-mismatch";
        return null;
    }

    public static List<SourceBook> ParseBooks(string text)
    {
        var list = new List<SourceBook>();
        foreach (var line in text.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("MatchId", StringComparison.Ordinal) || line.Length == 0) continue;
            var part = line.Split('\t');
            if (part.Length < 9 || !long.TryParse(part[0], out var id)) continue;
            if (!DateTime.TryParse(part[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var kickoff)) continue;
            var hasOddsAt = DateTime.TryParse(part[5], CultureInfo.InvariantCulture, DateTimeStyles.None, out var captured);
            var hasHandicap = decimal.TryParse(part[6], NumberStyles.Number, CultureInfo.InvariantCulture, out var handicap);
            list.Add(new SourceBook(id, kickoff, part[4], hasOddsAt, hasOddsAt ? captured : default, hasHandicap, hasHandicap ? handicap : 0m));
        }
        return list;
    }

    public static List<SourceOdds> ParseOdds(string text)
    {
        var list = new List<SourceOdds>();
        foreach (var line in text.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("MatchId", StringComparison.Ordinal) || line.Length == 0) continue;
            var part = line.Split('\t');
            if (part.Length < 4 || !long.TryParse(part[0], out var id)) continue;
            if (!decimal.TryParse(part[3], NumberStyles.Number, CultureInfo.InvariantCulture, out var odds)) continue;
            list.Add(new SourceOdds(id, part[1], part[2], odds));
        }
        return list;
    }

    public static List<long> ParseHistory(string text)
    {
        var list = new List<long>();
        foreach (var line in text.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            var part = line.Split('\t');
            if (part.Length < 7 || !long.TryParse(part[0], out var id)) continue;
            list.Add(id);
        }
        return list;
    }

    public readonly record struct SourceBook(long MatchId, DateTime Kickoff, string Play, bool HasOddsAt, DateTime OddsAt, bool HasHandicap, decimal Handicap);
    public readonly record struct SourceOdds(long MatchId, string Play, string Code, decimal Odds);
}

/// <summary>
/// 竞彩三条正式链路。北单、健康检查和 14 项数据库测试都不是这三条。
/// 公开候选开关关闭，保存不读资金限额。
/// </summary>
public static class JingcaiOfficialChain
{
    public const string SaveFundCoupling = "official-save-does-not-read-stake-limits";

    public static string Score(string play, string option, int? home, int? away, decimal? handicap)
        => CandidateTickets.Settle(play, option, home, away, handicap);

    public const string SharedSave =
        "POST /api/recommend/save 与作业 save-recommendations → RecommendationService.RecommendAllAndSaveAsync → JingcaiOfficialSave.Execute；隔离 POST /api/isolate/jingcai-chains → JingcaiOfficialSave.RunAcceptance → 同一 Execute";

    public static readonly Chain Milk = new(
        "竞彩大奶",
        "BF,JQ",
        "JingcaiOfficialSave.Select",
        SharedSave,
        OfficialRead,
        OfficialSettle,
        OfficialBoard);

    public const string OfficialRead = "GET /api/milk/picks?date=";
    public const string OfficialSettle =
        "POST /api/recommend/settle 的正式票旁路只结算 FbMilkPick；让球 k 只读本票 DivergenceNote；不写 Handicap 列，也不写比赛盘口和 Recommendation";
    public const string OfficialBoard = "GET /api/milk/board-status?date=";
    public const string OldHistory = "GET /api/recommend/history 只读旧 Recommendation 链，正式票不在这张表";

    public static readonly Chain Spf = new(
        "竞彩胜平负",
        "SPF",
        "JingcaiOfficialSave.Select",
        SharedSave,
        OfficialRead,
        OfficialSettle,
        OfficialBoard);

    public static readonly Chain Nspf = new(
        "竞彩让球胜平负",
        "NSPF",
        "JingcaiOfficialSave.Select",
        SharedSave,
        OfficialRead,
        OfficialSettle,
        OfficialBoard);

    public static readonly Chain[] All = { Milk, Spf, Nspf };

    public static readonly string[] NotThisRound =
    {
        "GET /api/health",
        "GET /api/isolate/db",
        "POST /api/bd/spf-picks",
        "GET /api/bd/spf-picks",
        "GET /api/bd/spf-record",
        "FitTraceDbTests"
    };

    public readonly record struct Chain(string Name, string Plays, string Decision, string Save, string Read, string Settle, string DateBoard);
}
