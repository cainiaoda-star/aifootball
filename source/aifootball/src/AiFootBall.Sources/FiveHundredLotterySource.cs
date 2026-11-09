using System.Text.RegularExpressions;
using AiFootBall.Core.Abstractions;
using AiFootBall.Core.Models;
using HtmlAgilityPack;

namespace AiFootBall.Sources;

/// <summary>
/// 500.com 彩票全量历史采集器（datachart.500.com）。
/// 支持 ssq / dlt / qlc，可通过 start/end 参数一次性取全部历史，并带销售额、奖池与部分奖级。
/// 期号在 500 站为 5 位（yy+3位序号），此处统一转换为 7 位标准期号。
/// </summary>
public sealed class FiveHundredLotterySource : ILotterySource
{
    private static readonly Dictionary<string, string> StartIssues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ssq"] = "03001",
        ["dlt"] = "07001",
        ["qlc"] = "03001"
    };

    private readonly HttpClient _http;

    public FiveHundredLotterySource(HttpClient http) => _http = http;

    public string Name => "500";

    public static bool Supports(string code) => StartIssues.ContainsKey(code);

    public async Task<IReadOnlyList<LotteryDrawRecord>> FetchAsync(LotteryTypeInfo lottery, CancellationToken ct = default)
    {
        if (!StartIssues.TryGetValue(lottery.Code, out var start))
            return Array.Empty<LotteryDrawRecord>();

        var end = $"{DateTime.Now.Year % 100:D2}999";
        var url = $"https://datachart.500.com/{lottery.Code}/history/newinc/history.php?start={start}&end={end}";

        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        var html = TextDecoder.Decode(bytes, resp.Content.Headers.ContentType?.CharSet);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var rows = doc.DocumentNode.SelectNodes("//tr[contains(@class,'t_tr1')]");
        if (rows is null) return Array.Empty<LotteryDrawRecord>();

        var list = new List<LotteryDrawRecord>(rows.Count);
        foreach (var tr in rows)
        {
            var cells = tr.SelectNodes("./td");
            if (cells is null || cells.Count < 5) continue;

            var texts = cells.Select(c => PinbleLotterySource.CleanText(c.InnerText)).ToList();
            if (texts.Any(t => t.Contains("注数") || t.Contains("奖金"))) continue; // 表头

            // 定位 5 位期号
            var issueIdx = texts.FindIndex(t => Regex.IsMatch(t, @"^\d{5}$"));
            if (issueIdx < 0 || issueIdx > 2) continue;

            var total = lottery.RedCount + lottery.BlueCount;
            if (texts.Count < issueIdx + 2) continue;

            // 两种版式：① 每球一格（dlt/ssq）；② 全部号码挤在一格、空格分隔（qlc）
            var nums = new List<int>();
            var compact = false;
            var packed = texts[issueIdx + 1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (packed.Length == total && packed.All(p => int.TryParse(p, out _)))
            {
                nums = packed.Select(int.Parse).ToList();
                compact = true;
            }
            else
            {
                if (texts.Count < issueIdx + total + 2) continue;
                nums = texts.Skip(issueIdx + 1).Take(total)
                    .Select(t => int.TryParse(t, out var v) ? v : -1).ToList();
                if (nums.Any(v => v < 0)) continue;
            }

            var reds = nums.Take(lottery.RedCount).ToList();
            var blues = nums.Skip(lottery.RedCount).ToList();

            var dateText = texts[^1];
            if (!DateTime.TryParse(dateText, out var dt)) continue;

            long? sales = null, pool = null;
            var prizes = new List<PrizeRecord>();
            if (!compact)
            {
                sales = TryLong(texts.ElementAtOrDefault(issueIdx + 1 + total));
                pool = TryLong(texts.ElementAtOrDefault(texts.Count - 2));

                var p1Count = TryInt(texts.ElementAtOrDefault(issueIdx + 2 + total));
                var p1Amount = TryLong(texts.ElementAtOrDefault(issueIdx + 3 + total));
                var p2Count = TryInt(texts.ElementAtOrDefault(issueIdx + 4 + total));
                var p2Amount = TryLong(texts.ElementAtOrDefault(issueIdx + 5 + total));
                if (p1Count.HasValue || p1Amount.HasValue)
                    prizes.Add(new PrizeRecord { Level = 1, BetCount = p1Count, Amount = p1Amount });
                if (p2Count.HasValue || p2Amount.HasValue)
                    prizes.Add(new PrizeRecord { Level = 2, BetCount = p2Count, Amount = p2Amount });
            }

            list.Add(new LotteryDrawRecord
            {
                LotteryCode = lottery.Code,
                Issue = "20" + texts[issueIdx],
                DrawDate = DateOnly.FromDateTime(dt),
                Reds = reds,
                Blues = blues,
                SalesAmount = sales,
                PoolAmount = pool,
                Prizes = prizes,
                Source = Name
            });
        }

        return list;
    }

    private static long? TryLong(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var cleaned = s.Replace(",", "").Replace("，", "").Trim();
        return long.TryParse(cleaned, out var v) ? v : null;
    }

    private static int? TryInt(string? s)
    {
        var v = TryLong(s);
        return v.HasValue && v <= int.MaxValue ? (int)v.Value : null;
    }
}
