using System.Text.RegularExpressions;
using AiFootBall.Core.Abstractions;
using AiFootBall.Core.Models;
using AiFootBall.Core.Options;
using HtmlAgilityPack;

namespace AiFootBall.Sources;

/// <summary>
/// pinble（拼搏在线）历史开奖采集器。
/// 页面 https://www.pinble.com/nav/lottery/history/... 的 tbody#tbody 内含最近 200 期：
/// td[0]=期号，td[1]=开奖日期，td[2]=号码（div.qiu-item，qiu-red/qiu-lan/qiu-ju 区分球色）。
/// </summary>
public sealed class PinbleLotterySource : ILotterySource
{
    private readonly HttpClient _http;
    private readonly CollectorOptions _opt;

    public PinbleLotterySource(HttpClient http, CollectorOptions opt)
    {
        _http = http;
        _opt = opt;
    }

    public string Name => "pinble";

    public async Task<IReadOnlyList<LotteryDrawRecord>> FetchAsync(LotteryTypeInfo lottery, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(lottery.SourceUrl))
            return Array.Empty<LotteryDrawRecord>();

        using var resp = await _http.GetAsync(lottery.SourceUrl, ct);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        var html = TextDecoder.Decode(bytes, resp.Content.Headers.ContentType?.CharSet);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var rows = doc.DocumentNode.SelectNodes("//tbody[@id='tbody']/tr");
        if (rows is null || rows.Count == 0)
            return Array.Empty<LotteryDrawRecord>();

        var list = new List<LotteryDrawRecord>(rows.Count);
        foreach (var tr in rows)
        {
            var cells = tr.SelectNodes("./td");
            if (cells is null || cells.Count < 3) continue;

            var issue = CleanText(cells[0].InnerText);
            if (!Regex.IsMatch(issue, @"^\d{5,7}$")) continue;

            var dateText = CleanText(cells[1].InnerText);
            if (!DateTime.TryParse(dateText, out var dt)) continue;

            var balls = tr.SelectNodes(".//div[contains(@class,'qiu-item')]");
            if (balls is null || balls.Count == 0) continue;

            var reds = new List<int>();
            var blues = new List<int>();
            foreach (var b in balls)
            {
                if (!int.TryParse(CleanText(b.InnerText), out var num)) continue;
                var cls = b.GetAttributeValue("class", "");
                if (cls.Contains("qiu-lan", StringComparison.Ordinal)) blues.Add(num);
                else reds.Add(num);
            }

            if (reds.Count == 0) continue;

            list.Add(new LotteryDrawRecord
            {
                LotteryCode = lottery.Code,
                Issue = NormalizeIssue(issue),
                DrawDate = DateOnly.FromDateTime(dt),
                Reds = reds,
                Blues = blues,
                Source = Name
            });
        }

        return list;
    }

    private static string NormalizeIssue(string issue) => issue.Length switch
    {
        5 => "20" + issue,   // 26122 -> 2026122
        _ => issue
    };

    internal static string CleanText(string s) =>
        Regex.Replace(HtmlEntity.DeEntitize(s ?? ""), @"\s+", " ").Trim();
}
