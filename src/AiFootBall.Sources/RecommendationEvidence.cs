using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Encodings.Web;
using System.Text.Json;
using AiFootBall.Core.Models;
using AiFootBall.Data;

namespace AiFootBall.Sources;

/// <summary>
/// 把当时页面上的推荐和库里已保存的行写成证据。目录按时间追加，不覆盖。
/// </summary>
public static class RecommendationEvidence
{
    public static readonly string[] PageFields =
    [
        "matchNo", "matchId", "leagueName", "homeTeam", "awayTeam", "matchTime", "handicap",
        "result", "resultText", "confidence", "signalLevel", "signalDesc",
        "hitProbability", "hitProbabilityNote", "handicapOdds", "plainResult",
        "homeOddsDelta", "drawOddsDelta", "ratioHome", "ratioDraw", "ratioAway",
        "scores", "goals", "refScores", "refGoals",
        "recHandicap", "recHandicapText", "halfFullPick", "isFeatured", "publishMarkets",
        "upsetWatch", "upsetNote",
        "totalLine", "ouSide", "ouProbability", "ouPush", "ouNote"
    ];

    public static readonly string[] DatabaseFields =
    [
        "RecId", "Board", "Expect", "MatchId", "MatchNo", "HomeTeam", "AwayTeam", "MatchTime",
        "RecResult", "Confidence", "SignalLevel", "IsCoverAll", "SignalDesc",
        "Score1", "Score2", "Goals1", "Goals2", "RecHandicap", "Handicap", "HandicapHit",
        "RecHalfFull", "ActualScore", "ActualGoals", "ActualResult", "Hit", "RecDate", "CreatedAt"
    ];

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string EvidenceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AiFootBall.sln")))
                return Path.Combine(dir.FullName, "evidence", "recommendations");
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("找不到 AiFootBall.sln，证据不能写到代码目录外。");
    }

    public static async Task<string> CaptureAsync(
        RecommendationService svc, BettingRepository repo, CancellationToken ct = default)
    {
        var now = DateTime.Now;
        var boards = new List<object>();
        foreach (var board in new[] { Boards.Jc, Boards.Bd, Boards.Sfc })
        {
            var recs = await svc.RecommendAsync(board, null, ct);
            var handicap = board == Boards.Jc
                ? RecommendationSelection.ForJcHandicap(recs).Select(r => PageOf(r, asHandicap: true)).ToList()
                : new List<object>();
            var draws = board == Boards.Jc
                ? RecommendationSelection.ForJcDraw(recs).Select(r => PageOf(r)).ToList()
                : new List<object>();
            var shown = RecommendationSelection.ForDisplay(board, recs);
            var name = board == Boards.Jc ? "竞彩" : board == Boards.Bd ? "北单" : "胜负彩";
            boards.Add(new
            {
                board,
                name,
                total = recs.Count,
                published = shown.Count,
                handicap,
                draws,
                items = shown.Select(r => PageOf(r)).ToList()
            });
        }

        var rows = await repo.GetRecommendationsByDateAsync(now, ct);
        if (now.Hour < 12)
        {
            var yesterday = await repo.GetRecommendationsByDateAsync(now.Date.AddDays(-1), ct);
            rows = yesterday.Concat(rows).ToList();
        }
        return Write(EvidenceRoot(), now, boards, rows);
    }

    public static object PageOf(MatchRecommendation r, bool asHandicap = false)
    {
        var bd = asHandicap || (r.Board == Boards.Bd && r.RecHandicap is not null);
        var pick = bd ? r.RecHandicap : r.RecResult;
        return new
        {
            r.MatchNo,
            r.MatchId,
            r.LeagueName,
            r.HomeTeam,
            r.AwayTeam,
            r.MatchTime,
            r.Handicap,
            result = pick?.ToString(),
            resultText = bd
                ? RecommendationSelection.HandicapOptionName(r.RecHandicap!.Value)
                : r.RecResult == '3' ? "主胜" : r.RecResult == '0' ? "客胜" : r.RecResult == '1' ? "平局" : "全包",
            r.Confidence,
            r.SignalLevel,
            r.SignalDesc,
            hitProbability = bd ? r.HandicapProbability : r.HitProbability,
            hitProbabilityNote = !bd ? r.HitProbabilityNote
                : asHandicap && r.Board == Boards.Jc ? "欧赔去水 + 大小球盘折算到竞彩让球数"
                : "欧赔去水 + 大小球盘折算到北单让球数",
            handicapOdds = bd ? r.HandicapOdds : null,
            plainResult = bd ? r.RecResult?.ToString() : null,
            homeOddsDelta = r.HomeOddsDelta,
            drawOddsDelta = r.DrawOddsDelta,
            ratioHome = r.RatioHome,
            ratioDraw = r.RatioDraw,
            ratioAway = r.RatioAway,
            scores = r.ScoreCandidates,
            goals = r.GoalCandidates,
            refScores = r.RefScores,
            refGoals = r.RefGoals,
            recHandicap = r.RecHandicap?.ToString(),
            recHandicapText = r.RecHandicap switch { '3' => "让胜", '1' => "让平", '0' => "让负", _ => null },
            halfFullPick = r.HalfFullPick,
            r.IsFeatured,
            r.PublishMarkets,
            r.UpsetWatch,
            r.UpsetNote,
            r.TotalLine,
            r.OuSide,
            r.OuProbability,
            r.OuPush,
            r.OuNote
        };
    }

    public static string Write(
        string root, DateTime now, IReadOnlyList<object> boards, IReadOnlyList<RecommendationRecord> rows)
    {
        var dir = Path.Combine(root, now.ToString("yyyy-MM-dd"), now.ToString("HHmmss"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "fields.json"), JsonSerializer.Serialize(new
        {
            page = "GET /api/recommend 每个 items[] 的字段",
            pageFields = PageFields,
            database = "dbo.Recommendation 当天已保存的列",
            databaseFields = DatabaseFields
        }, Json));
        File.WriteAllText(Path.Combine(dir, "page.json"), JsonSerializer.Serialize(new
        {
            capturedAt = now.ToString("yyyy-MM-dd HH:mm:ss"),
            boards
        }, Json));
        File.WriteAllText(Path.Combine(dir, "database.json"), JsonSerializer.Serialize(new
        {
            capturedAt = now.ToString("yyyy-MM-dd HH:mm:ss"),
            count = rows.Count,
            note = "凌晨 12 点前同时带上前一天已保存的行。推荐只在 08:40、11:40、16:40、20:40 落库。",
            rows
        }, Json));
        Draw(Path.Combine(dir, "slate.png"), now, boards);
        return dir;
    }

#pragma warning disable CA1416 // 证据图用 GDI+，这台机器是 Windows
    static void Draw(string path, DateTime now, IReadOnlyList<object> boards)
    {
        var lines = new List<string> { $"推荐证据 {now:yyyy-MM-dd HH:mm:ss}  页面当时发布的场次" };
        foreach (var board in boards)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(board, Json));
            var root = doc.RootElement;
            var name = root.GetProperty("name").GetString();
            var total = root.GetProperty("total").GetInt32();
            var published = root.GetProperty("published").GetInt32();
            lines.Add("");
            lines.Add($"{name}  受注 {total}  发布 {published}");
            if (published == 0)
            {
                lines.Add("  （没有发布）");
                continue;
            }

            foreach (var item in root.GetProperty("items").EnumerateArray())
            {
                var no = item.GetProperty("matchNo").GetString();
                var home = item.GetProperty("homeTeam").GetString();
                var away = item.GetProperty("awayTeam").GetString();
                var text = item.GetProperty("resultText").GetString();
                var hit = item.TryGetProperty("hitProbability", out var hp) && hp.ValueKind == JsonValueKind.Number
                    ? hp.GetDecimal().ToString("0.0") + "%"
                    : "—";
                var featured = item.TryGetProperty("isFeatured", out var feat) && feat.ValueKind == JsonValueKind.True;
                var desc = item.TryGetProperty("signalDesc", out var sd) ? sd.GetString() : "";
                lines.Add($"  {no}  {home} vs {away}  {text}  {hit}{(featured ? "  精选" : "")}");
                if (!string.IsNullOrWhiteSpace(desc))
                    lines.Add("    " + desc);
            }
        }

        using var font = new Font("Microsoft YaHei", 14f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var bold = new Font("Microsoft YaHei", 16f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var measure = new Bitmap(1, 1);
        using var g0 = Graphics.FromImage(measure);
        const int width = 1500;
        const int pad = 16;
        var height = pad;
        foreach (var line in lines)
        {
            var size = g0.MeasureString(line, line.StartsWith("推荐证据", StringComparison.Ordinal) ? bold : font, width - pad * 2);
            height += (int)Math.Ceiling(size.Height) + 4;
        }
        height += pad;

        using var bmp = new Bitmap(width, Math.Max(height, 80));
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(15, 20, 27));
        using var dim = new SolidBrush(Color.FromArgb(170, 178, 188));
        var y = (float)pad;
        foreach (var line in lines)
        {
            var use = line.StartsWith("推荐证据", StringComparison.Ordinal) || (!line.StartsWith(" ") && line.Length > 0)
                ? bold : font;
            Brush brush = line.Contains("没有发布", StringComparison.Ordinal) ? dim : Brushes.WhiteSmoke;
            var size = g.MeasureString(line, use, width - pad * 2);
            g.DrawString(line, use, brush, new RectangleF(pad, y, width - pad * 2, size.Height + 2));
            y += size.Height + 4;
        }
        bmp.Save(path, ImageFormat.Png);
    }
#pragma warning restore CA1416
}
