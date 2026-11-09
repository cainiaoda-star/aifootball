using System.Globalization;
using System.Text.RegularExpressions;
using AiFootBall.Core.Abstractions;
using AiFootBall.Core.Models;
using AiFootBall.Core.Options;
using HtmlAgilityPack;

namespace AiFootBall.Sources;

/// <summary>
/// 500.com 必发（Betfair 交易所）成交数据采集器。
/// 页面：https://odds.500.com/fenxi/touzhu-{fid}.shtml （GBK，单请求）
///
/// 解析内容：
///   · 百家欧指指数 / 概率
///   · 必发成交价 / 成交量 / 庄家盈亏（主平客）
///   · 交易比例（北单、必发）
///   · 必发指数 / 冷热指数 / 盈亏指数
///   · 总成交额、大额成交额与大额三分布
///   · 大额交易明细（成交时间、方向、买/卖、金额、占比）→ 形成“资金流”过程数据
/// 该页只有存在必发盘口的场次才有成交数据，其余返回 Status=0。
/// </summary>
public sealed class FiveHundredExchangeSource : IExchangeSource
{
    private const string BaseUrl = "https://odds.500.com/fenxi";
    private const string Referer = "https://odds.500.com/";

    private static readonly Regex TimeRegex =
        new(@"(\d{4})-(\d{1,2})-(\d{1,2})\s+(\d{1,2}):(\d{2})", RegexOptions.Compiled);

    private readonly HttpClient _http;

    public FiveHundredExchangeSource(HttpClient http) => _http = http;

    public string Name => "500-exchange";

    public async Task<MatchExchangeResult> FetchAsync(long matchId, CancellationToken ct = default)
    {
        var (html, reason) = await OddsHttp.GetAsync(_http, $"{BaseUrl}/touzhu-{matchId}.shtml", Referer, ct);
        if (html is null)
            return new MatchExchangeResult { MatchId = matchId, Status = 2, Error = reason };

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var snapshot = ParseSnapshot(doc, matchId);
        var trades = ParseTrades(doc, matchId).ToList();

        if (!snapshot.HasValue && trades.Count == 0)
            return new MatchExchangeResult { MatchId = matchId, Status = 0, Error = "无必发盘口" };

        return new MatchExchangeResult
        {
            MatchId = matchId,
            Snapshot = snapshot,
            Trades = trades,
            Status = 1
        };
    }

    /// <summary>同步解析（供 probe 诊断复用）。</summary>
    public static (ExchangeSnapshot Snapshot, List<ExchangeTrade> Trades) Parse(string html, long matchId)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return (ParseSnapshot(doc, matchId), ParseTrades(doc, matchId).ToList());
    }

    private static ExchangeSnapshot ParseSnapshot(HtmlDocument doc, long matchId)
    {
        var snap = new ExchangeSnapshot { MatchId = matchId, CapturedAt = DateTime.Now };

        var table = doc.DocumentNode
            .SelectNodes("//table[contains(@class,'bif-yab')]")
            ?.FirstOrDefault(t => t.InnerText.Contains("成交量"));
        if (table is null) return snap;

        // 主/平/客 三行：靠 td[0] 文本判定，兜底按出现顺序
        var dataRows = new List<HtmlNode>();
        foreach (var tr in table.SelectNodes(".//tr") ?? Enumerable.Empty<HtmlNode>())
        {
            var tds = tr.SelectNodes("./td");
            if (tds is null || tds.Count < 9) continue;
            var label = Clean(tds[0].InnerText);
            if (label.Length == 0) continue;
            if (label.Contains("数据提点") || label.Contains("综合")) continue;
            dataRows.Add(tr);
            if (dataRows.Count == 3) break;
        }
        if (dataRows.Count < 3) return snap;

        for (var i = 0; i < 3; i++)
        {
            var tds = dataRows[i].SelectNodes("./td");
            if (tds is null || tds.Count < 11) continue;

            var label = Clean(tds[0].InnerText);
            var side = label.Contains("平") && !label.Contains("平手") ? "D" : i switch
            {
                0 => "H",
                1 => "D",
                _ => "A"
            };

            var euroIdx = Num(tds[1].InnerText);
            var euroProb = Pct(tds[2].InnerText);
            var bdRatio = Pct(tds[3].InnerText);
            var bfRatio = Pct(tds[4].InnerText);
            var price = Num(tds[5].InnerText);
            var volume = Num(tds[6].InnerText);
            var pnl = Num(tds[7].InnerText);
            var bfIdx = Num(tds[8].InnerText);
            var hotIdx = Num(tds[9].InnerText);
            var pnlIdx = Num(tds[10].InnerText);

            switch (side)
            {
                case "H":
                    snap.EuroHome = euroIdx; snap.EuroProbHome = euroProb;
                    snap.BdRatioHome = bdRatio; snap.RatioHome = bfRatio;
                    snap.PriceHome = price; snap.VolumeHome = volume; snap.PnlHome = pnl;
                    snap.BfIdxHome = bfIdx; snap.HotIdxHome = hotIdx; snap.PnlIdxHome = pnlIdx;
                    break;
                case "D":
                    snap.EuroDraw = euroIdx; snap.EuroProbDraw = euroProb;
                    snap.BdRatioDraw = bdRatio; snap.RatioDraw = bfRatio;
                    snap.PriceDraw = price; snap.VolumeDraw = volume; snap.PnlDraw = pnl;
                    snap.BfIdxDraw = bfIdx; snap.HotIdxDraw = hotIdx; snap.PnlIdxDraw = pnlIdx;
                    break;
                default:
                    snap.EuroAway = euroIdx; snap.EuroProbAway = euroProb;
                    snap.BdRatioAway = bdRatio; snap.RatioAway = bfRatio;
                    snap.PriceAway = price; snap.VolumeAway = volume; snap.PnlAway = pnl;
                    snap.BfIdxAway = bfIdx; snap.HotIdxAway = hotIdx; snap.PnlIdxAway = pnlIdx;
                    break;
            }
        }

        // 数据提点：每行最后一列是文本，首行左侧还有 rowspan="3" 的「数据提点」标签
        var tips = new List<string>();
        foreach (var tr in table.SelectNodes(".//tr") ?? Enumerable.Empty<HtmlNode>())
        {
            var tds = tr.SelectNodes("./td");
            if (tds is null || tds.Count == 0) continue;
            var last = Clean(tds[^1].InnerText);
            if (last.Length <= 2) continue;
            if (last.Contains("数据提点") || last.Contains("暂无数据")) continue;
            if (!last.StartsWith("本场比赛")) continue;
            tips.Add(last);
        }
        if (tips.Count > 0) snap.TipScale = Clip(tips[0], 200);
        if (tips.Count > 1) snap.TipLean = Clip(tips[1], 200);

        // 总交易额与大额总交易（两个 czxl-zxl）
        var totals = doc.DocumentNode.SelectNodes("//span[contains(@class,'czxl-zxl')]/em");
        if (totals is { Count: > 0 }) snap.TotalVolume = Num(totals[0].InnerText);
        if (totals is { Count: > 1 }) snap.BigTotal = Num(totals[1].InnerText);

        // 大额成交三分布（第二个 data-detail）
        var details = doc.DocumentNode.SelectNodes("//div[contains(@class,'data-detail')]");
        if (details is { Count: > 1 })
        {
            var lis = details[1].SelectNodes(".//li");
            if (lis is { Count: >= 3 })
            {
                snap.BigHome = Num(lis[0].InnerText);
                snap.BigDraw = Num(lis[1].InnerText);
                snap.BigAway = Num(lis[2].InnerText);
            }
        }

        return snap;
    }

    private static IEnumerable<ExchangeTrade> ParseTrades(HtmlDocument doc, long matchId)
    {
        // 大额交易明细表：表头含「综合」「属性」「成交量」「交易时间」
        var table = doc.DocumentNode
            .SelectNodes("//table[contains(@class,'bif-yab')]")
            ?.FirstOrDefault(t => t.InnerText.Contains("交易时间"));
        if (table is null) yield break;

        var capturedAt = DateTime.Now;
        foreach (var tr in table.SelectNodes(".//tr") ?? Enumerable.Empty<HtmlNode>())
        {
            var tds = tr.SelectNodes("./td");
            if (tds is null || tds.Count < 5) continue;

            var sideText = Clean(tds[0].InnerText);
            var dirText = Clean(tds[1].InnerText);
            var volText = Clean(tds[2].InnerText);
            var timeText = Clean(tds[3].InnerText);
            var ratioText = Clean(tds[4].InnerText);

            if (sideText.Length == 0 || dirText.Length == 0) continue;
            if (volText.Length == 0 || !Num(volText).HasValue) continue;
            if (!TryParseTime(timeText, out var tradeTime)) continue;

            var side = sideText switch
            {
                "主" => "H",
                "平" => "D",
                "客" => "A",
                _ => sideText.Contains("主") ? "H" : sideText.Contains("客") ? "A" : "D"
            };

            yield return new ExchangeTrade
            {
                MatchId = matchId,
                TradeTime = tradeTime,
                Side = side,
                Direction = dirText.Length > 4 ? dirText[..4] : dirText,
                Volume = Num(volText)!.Value,
                Ratio = Pct(ratioText),
                CapturedAt = capturedAt
            };
        }
    }

    private static bool TryParseTime(string text, out DateTime value)
    {
        value = default;
        var m = TimeRegex.Match(text);
        if (!m.Success) return false;
        value = new DateTime(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
            int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value), 0);
        return true;
    }

    private static string Clean(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var t = HtmlEntity.DeEntitize(Regex.Replace(html, "<[^>]+>", ""));
        t = t.Replace('\u00a0', ' ').Replace("↑", "").Replace("↓", "");
        return Regex.Replace(t, @"\s+", " ").Trim();
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>解析数字：支持千分位、负号、空白与破折号占位。</summary>
    private static decimal? Num(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = Clean(text).Replace(",", "").Replace("%", "").Replace("港币", "").Trim();
        if (t is "" or "-" or "—" or "--") return null;
        var m = Regex.Match(t, @"-?\d+(?:\.\d+)?");
        if (!m.Success) return null;
        return decimal.TryParse(m.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>解析百分比，返回 0~100 的数值。</summary>
    private static decimal? Pct(string? text)
    {
        var v = Num(text);
        return v;
    }
}
