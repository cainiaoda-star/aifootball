using System.Globalization;
using System.Text.RegularExpressions;
using AiFootBall.Core.Abstractions;
using AiFootBall.Core.Models;
using AiFootBall.Core.Options;
using HtmlAgilityPack;

namespace AiFootBall.Sources;

/// <summary>
/// 500.com 赔率采集器。
/// 数据来源（每场 3 个页面，均为 GBK）：
///   https://odds.500.com/fenxi/ouzhi-{fid}.shtml   百家欧指（每家公司两行：初盘 / 即时，另含返还率、凯利）
///   https://odds.500.com/fenxi/yazhi-{fid}.shtml   亚指对比（即时盘口 + 初始盘口，含主客水位）
///   https://odds.500.com/fenxi/daxiao-{fid}.shtml  大小对比（即时 + 初始）
/// 三个页面底部还有 平均值 / 最高值 / 最低值 汇总行，一并入库（CompanyId = 0）。
/// </summary>
public sealed class FiveHundredOddsSource : IOddsSource
{
    private const string BaseUrl = "https://odds.500.com/fenxi";

    /// <summary>中文盘口名 → 数值（ref 属性缺失时的兜底）</summary>
    private static readonly Dictionary<string, decimal> HandicapNames = new()
    {
        ["平手"] = 0m, ["平/半"] = 0.25m, ["平手/半球"] = 0.25m, ["半球"] = 0.5m,
        ["半/一"] = 0.75m, ["半球/一球"] = 0.75m, ["一球"] = 1m,
        ["一/球半"] = 1.25m, ["一球/球半"] = 1.25m, ["球半"] = 1.5m,
        ["球半/两球"] = 1.75m, ["球半/两"] = 1.75m, ["两球"] = 2m,
        ["两/两球半"] = 2.25m, ["两球/两球半"] = 2.25m, ["两球半"] = 2.5m,
        ["两球半/三球"] = 2.75m, ["三球"] = 3m, ["三/三球半"] = 3.25m,
        ["三球/三球半"] = 3.25m, ["三球半"] = 3.5m, ["三球半/四球"] = 3.75m,
        ["四球"] = 4m, ["四/四球半"] = 4.25m, ["四球半"] = 4.5m, ["五球"] = 5m
    };

    private static readonly char[] NoiseChars = { '↑', '↓', ' ', '\u00a0', '\t', '\r', '\n' };
    private static readonly Regex DigitsRegex = new(@"-?\d+(?:\.\d+)?", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly CollectorOptions _opt;

    public FiveHundredOddsSource(HttpClient http, CollectorOptions opt)
    {
        _http = http;
        _opt = opt;
    }

    public string Name => "500-odds";

    /// <summary>当前节流间隔（毫秒），用于观测自适应效果</summary>
    public static int ThrottleIntervalMs => OddsHttp.ThrottleIntervalMs;

    /// <summary>运行期计数器：成功 / 428 / 429 / 挑战页</summary>
    public static long StatOk => OddsHttp.StatOk;
    public static long Stat428 => OddsHttp.Stat428;
    public static long Stat429 => OddsHttp.Stat429;
    public static long StatChallenge => OddsHttp.StatChallenge;

    public async Task<MatchOddsResult> FetchAsync(long matchId, CancellationToken ct = default)
    {
        var odds = new List<OddsRecord>();
        var failed = 0;
        var requested = 0;
        var errors = new List<string>();

        var targets = new[]
        {
            ("ouzhi", OddsTypes.Eu),
            ("yazhi", OddsTypes.Ah),
            ("daxiao", OddsTypes.Ou)
        };

        foreach (var (path, type) in targets)
        {
            requested++;
            try
            {
                var (page, reason) = await GetHtmlAsync($"{BaseUrl}/{path}-{matchId}.shtml", ct);
                if (page is null) { failed++; errors.Add($"{path}:{reason}"); continue; }

                var doc = new HtmlDocument();
                doc.LoadHtml(page);

                var parsed = type == OddsTypes.Eu ? ParseEuropean(doc, matchId) : ParseAsian(doc, matchId, type);
                if (parsed.Count > 0) odds.AddRange(parsed);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{path}:{ex.GetType().Name}:{ex.Message}");
            }
        }

        var companies = odds.Where(o => o.CompanyId > 0)
            .Select(o => o.CompanyId).Distinct().Count();

        var status = odds.Count > 0 ? 1 : failed >= requested ? 2 : 0;

        return new MatchOddsResult
        {
            MatchId = matchId,
            Odds = odds,
            Status = status,
            CompanyCount = companies,
            Error = errors.Count > 0 ? string.Join(" | ", errors) : null
        };
    }

    /* ---------------- 欧赔 ---------------- */

    private static List<OddsRecord> ParseEuropean(HtmlDocument doc, long matchId)
    {
        var list = new List<OddsRecord>();
        var table = doc.GetElementbyId("datatb");
        if (table is null) return list;

        foreach (var tr in CompanyRows(table))
        {
            var cid = ParseCompanyId(tr);
            if (cid is null) continue;
            var name = ParseCompanyName(tr);
            if (name.Length == 0) continue;

            var tds = tr.SelectNodes("./td");
            if (tds is null || tds.Count < 6) continue;

            // 结构：[0]序号 [1]公司 [2]欧指(初盘/即时) [3]概率 [4]返还率(初盘/即时) [5]凯利(初盘/即时)
            var priceRows = NestedRows(tds[2]);
            if (priceRows.Count == 0) continue;

            var returnRows = NestedRows(tds[4]);
            var kellyRows = NestedRows(tds[5]);

            for (var i = 0; i < priceRows.Count && i < 2; i++)
            {
                var price = priceRows[i];
                if (price.Count < 3) continue;

                var home = Num(price[0].InnerText);
                var draw = Num(price[1].InnerText);
                var away = Num(price[2].InnerText);
                if (home is null || draw is null || away is null) continue;

                var rec = new OddsRecord
                {
                    MatchId = matchId,
                    CompanyId = cid.Value,
                    Company = name,
                    OddsType = OddsTypes.Eu,
                    IsOpening = i == 0,
                    HomeWin = home,
                    Draw = draw,
                    AwayWin = away,
                    ReturnRate = returnRows.Count > i && returnRows[i].Count > 0
                        ? Num(returnRows[i][0].InnerText)
                        : null
                };

                if (kellyRows.Count > i && kellyRows[i].Count >= 3)
                {
                    rec.KellyWin = Num(kellyRows[i][0].InnerText);
                    rec.KellyDraw = Num(kellyRows[i][1].InnerText);
                    rec.KellyLoss = Num(kellyRows[i][2].InnerText);
                }

                list.Add(rec);
            }
        }

        return list;
    }

    /* ---------------- 亚盘 / 大小球 ---------------- */

    private static List<OddsRecord> ParseAsian(HtmlDocument doc, long matchId, string oddsType)
    {
        var list = new List<OddsRecord>();
        var table = doc.GetElementbyId("datatb");
        if (table is null) return list;

        foreach (var tr in CompanyRows(table))
        {
            var cid = ParseCompanyId(tr);
            if (cid is null) continue;
            var name = ParseCompanyName(tr);
            if (name.Length == 0) continue;

            var tds = tr.SelectNodes("./td");
            if (tds is null || tds.Count < 5) continue;

            // 结构：[0]序号 [1]公司 [2]即时盘 [3]变化时间 [4]初始盘 [5]变化时间 [6]统计
            var current = ParseWaterTriple(tds[2]);
            var opening = ParseWaterTriple(tds[4]);
            if (current is null && opening is null) continue;

            if (opening is not null)
                list.Add(Build(oddsType, matchId, cid.Value, name, true, opening));
            if (current is not null)
                list.Add(Build(oddsType, matchId, cid.Value, name, false, current));
        }

        return list;
    }

    private static OddsRecord Build(string oddsType, long matchId, int cid, string company, bool isOpening, decimal?[] triple)
    {
        var line = triple[1];
        return oddsType == OddsTypes.Ah
            ? new OddsRecord
            {
                MatchId = matchId, CompanyId = cid, Company = company,
                OddsType = OddsTypes.Ah, IsOpening = isOpening,
                Handicap = line, HomeWater = triple[0], AwayWater = triple[2]
            }
            : new OddsRecord
            {
                MatchId = matchId, CompanyId = cid, Company = company,
                OddsType = OddsTypes.Ou, IsOpening = isOpening,
                TotalLine = line is null ? null : Math.Abs(line.Value),
                OverWater = triple[0], UnderWater = triple[2]
            };
    }

    /// <summary>解析「水位 / 盘口 / 水位」三元组</summary>
    private static decimal?[]? ParseWaterTriple(HtmlNode td)
    {
        var rows = NestedRows(td);
        if (rows.Count == 0) return null;
        var cells = rows[0];
        if (cells.Count < 3) return null;

        var home = Num(cells[0].InnerText);
        var away = Num(cells[2].InnerText);
        var line = ParseLine(cells[1]);
        if (home is null && away is null && line is null) return null;
        return new[] { home, line, away };
    }

    private static decimal? ParseLine(HtmlNode cell)
    {
        var refValue = cell.GetAttributeValue("ref", "");
        if (refValue.Length > 0)
        {
            var v = Num(refValue);
            if (v is not null) return v;
        }

        var text = Clean(cell.InnerText);
        var m = DigitsRegex.Match(text);
        if (m.Success && decimal.TryParse(m.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var direct))
            return direct;

        foreach (var (key, value) in HandicapNames)
        {
            if (text.Contains(key, StringComparison.Ordinal))
                return key == "平手" ? 0m : value;
        }
        return null;
    }

    /* ---------------- 通用 ---------------- */

    /// <summary>取公司行：datatb 内的 tr（含 id 才能识别公司号）</summary>
    private static IEnumerable<HtmlNode> CompanyRows(HtmlNode table)
    {
        var rows = table.SelectNodes("./tr");
        if (rows is not null)
            foreach (var r in rows) yield return r;

        var footer = table.OwnerDocument?.GetElementbyId("table_btm");
        if (footer is null) yield break;

        foreach (var r in footer.SelectNodes(".//tr") ?? Enumerable.Empty<HtmlNode>())
        {
            var tds = r.SelectNodes("./td");
            if (tds is null || tds.Count < 3) continue;
            var label = Clean(tds[1].InnerText);
            if (label == "平均值") yield return r;
        }
    }

    private static int? ParseCompanyId(HtmlNode tr)
    {
        var id = tr.GetAttributeValue("id", "").Trim();
        if (id.Length > 0 && int.TryParse(id, out var cid)) return cid;

        // 汇总行无 id，平均值统一记为公司号 0
        var tds = tr.SelectNodes("./td");
        var label = tds is { Count: > 1 } ? Clean(tds[1].InnerText) : "";
        return label == "平均值" ? 0 : null;
    }

    private static string ParseCompanyName(HtmlNode tr)
    {
        var tds = tr.SelectNodes("./td");
        if (tds is null || tds.Count < 2) return "";
        var a = tds[1].SelectSingleNode(".//a");
        if (a is not null)
        {
            var title = a.GetAttributeValue("title", "");
            if (title.Length > 0) return Clean(title);
        }
        return Clean(tds[1].InnerText);
    }

    /// <summary>解析 td 内嵌的 pl_table_data 表，返回每行的单元格集合</summary>
    private static List<List<HtmlNode>> NestedRows(HtmlNode td)
    {
        var result = new List<List<HtmlNode>>();
        var table = td.SelectSingleNode(".//table");
        if (table is null) return result;

        foreach (var tr in table.SelectNodes(".//tr") ?? Enumerable.Empty<HtmlNode>())
        {
            var cells = tr.SelectNodes("./td");
            if (cells is null || cells.Count == 0) continue;
            result.Add(cells.ToList());
        }
        return result;
    }

    private static string Clean(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var text = HtmlEntity.DeEntitize(html);
        return new string(text.Where(c => !NoiseChars.Contains(c)).ToArray()).Trim();
    }

    private static decimal? Num(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = DigitsRegex.Match(Clean(HtmlEntity.DeEntitize(text)));
        if (!m.Success) return null;
        return decimal.TryParse(m.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private Task<(string? Html, string Reason)> GetHtmlAsync(string url, CancellationToken ct)
        => OddsHttp.GetAsync(_http, url, "https://odds.500.com/", ct);
}
