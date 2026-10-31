using System.Globalization;
using System.Text.RegularExpressions;
using AiFootBall.Core.Models;
using HtmlAgilityPack;

namespace AiFootBall.Sources;

/// <summary>
/// 500.com 官方受注场次采集：竞彩(jczq)、北单(bjdc)、胜负彩(sfc)。
/// 三个板块页面均为 GBK 服务端渲染，无需登录即可读取受注场次、让球、赔率。
/// 解析基于 HTML data-* 属性（竞彩/北单）与行结构（胜负彩）。
/// </summary>
public sealed class BettingCardSource
{
    private static readonly Regex FidRegex = new(@"shuju-(\d+)\.shtml", RegexOptions.Compiled);
    private static readonly Regex NumRegex = new(@"-?\d+(?:\.\d+)?", RegexOptions.Compiled);
    private static readonly Regex ExpectRegex = new(@"expect=(\d+)", RegexOptions.Compiled);

    private readonly HttpClient _http;

    public BettingCardSource(HttpClient http) => _http = http;

    public async Task<List<BettingCardRecord>> FetchJcAsync(CancellationToken ct = default)
    {
        var html = await GetGbkAsync("https://trade.500.com/jczq/", "https://trade.500.com/", ct);
        return html is null ? new() : ParseJc(html);
    }

    public async Task<List<BettingCardRecord>> FetchBdAsync(CancellationToken ct = default)
    {
        var html = await GetGbkAsync("https://trade.500.com/bjdc/", "https://trade.500.com/", ct);
        return html is null ? new() : ParseBd(html);
    }

    public async Task<List<BettingCardRecord>> FetchSfcAsync(CancellationToken ct = default)
    {
        var html = await GetGbkAsync("https://trade.500.com/sfc/", "https://trade.500.com/", ct);
        return html is null ? new() : ParseSfc(html, out _);
    }

    /// <summary>北单胜平负、比分、半全场的开奖 SP。完场后未中选项会变成空，只留下开出的那一项。</summary>
    public async Task<Dictionary<long, BdMarketOdds>> FetchBdMarketOddsAsync(CancellationToken ct = default)
    {
        var map = new Dictionary<long, BdMarketOdds>();
        var referer = "https://trade.500.com/bjdc/";
        try
        {
            ParseBdSpf(await GetGbkAsync("https://trade.500.com/bjdc/", "https://trade.500.com/", ct), map);
            await Task.Delay(400, ct);
            ParseBdScores(await GetGbkAsync("https://trade.500.com/bjdc/project_fq_bf.php", referer, ct), map);
            await Task.Delay(400, ct);
            ParseBdHalf(await GetGbkAsync("https://trade.500.com/bjdc/project_fq_bq.php", referer, ct), map);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 页面打不开时沿用已经解析到的部分 */ }
        return map;
    }

    public static void ParseBdSpf(string? html, Dictionary<long, BdMarketOdds> map)
    {
        foreach (var tr in RowsWithFid(html))
        {
            var row = Row(map, tr);
            foreach (var input in Inputs(tr))
            {
                var label = input.GetAttributeValue("value", "");
                if (label is not ("胜" or "平" or "负")) continue;
                var n = ParseNum(input.ParentNode?.InnerText);
                if (n is not > 1) continue;
                if (label == "胜") row.Home = n;
                else if (label == "平") row.Draw = n;
                else row.Away = n;
            }
        }
    }

    public static void ParseBdScores(string? html, Dictionary<long, BdMarketOdds> map)
    {
        foreach (var tr in RowsWithFid(html))
        {
            var row = Row(map, tr);
            var next = tr.NextSibling;
            while (next is not null && next.NodeType != HtmlNodeType.Element) next = next.NextSibling;
            if (next is null || !next.GetAttributeValue("class", "").Contains("hide_b")) continue;
            foreach (var input in Inputs(next))
            {
                var code = input.GetAttributeValue("value", "");
                if (!Regex.IsMatch(code, @"^\d+:\d+$")) continue;
                var span = input.ParentNode?.SelectSingleNode(".//span[contains(@class,'sp_value')]");
                var n = ParseNum(span?.InnerText);
                if (n is > 1) row.Scores[code] = n.Value;
            }
        }
    }

    public static void ParseBdHalf(string? html, Dictionary<long, BdMarketOdds> map)
    {
        foreach (var tr in RowsWithFid(html))
        {
            var row = Row(map, tr);
            foreach (var input in Inputs(tr))
            {
                var code = input.GetAttributeValue("value", "");
                if (!Regex.IsMatch(code, @"^[胜平负][-/][胜平负]$")) continue;
                var n = ParseNum(input.ParentNode?.InnerText);
                if (n is not > 1) continue;
                row.HalfFull[code.Replace('-', '/')] = n.Value;
            }
        }
    }

    private static IEnumerable<HtmlNode> RowsWithFid(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) yield break;
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        foreach (var tr in doc.DocumentNode.SelectNodes("//tr[@fid]") ?? Enumerable.Empty<HtmlNode>())
            yield return tr;
    }

    private static BdMarketOdds Row(Dictionary<long, BdMarketOdds> map, HtmlNode tr)
    {
        var id = AttrLong(tr, "fid") ?? 0;
        if (!map.TryGetValue(id, out var row)) map[id] = row = new BdMarketOdds();
        return row;
    }

    private static IEnumerable<HtmlNode> Inputs(HtmlNode node) =>
        node.SelectNodes(".//input[@value]") ?? Enumerable.Empty<HtmlNode>();

    /* ---------------- 竞彩四类玩法（固定奖金） ---------------- */

    /// <summary>
    /// 抓取竞彩四类玩法赔率（固定奖金），返回每场含四类玩法赔率的卡片。
    ///   · 默认页 trade.500.com/jczq/ → 让球胜平负(nspf) + 胜平负(spf)
    ///   · playid=271 → 全场比分(bf)
    ///   · playid=270 → 总进球(jqs)
    ///   · playid=272 → 半全场(bqc)
    /// </summary>
    public async Task<List<JcMatchCard>> FetchJcAllPlaysAsync(CancellationToken ct = default)
    {
        var baseUrl = "https://trade.500.com/jczq/";
        var referer = "https://trade.500.com/";

        // 逐个请求并加延迟，避免 500.com 对连续请求限流导致页面返回空
        var main = await GetGbkAsync(baseUrl, referer, ct);
        await Task.Delay(500, ct);

        // 比分页单独重试（偶发被限流返回空）
        var bf = await GetGbkAsync(baseUrl + "?playid=271&g=2", baseUrl, ct);
        if (bf is null) { await Task.Delay(800, ct); bf = await GetGbkAsync(baseUrl + "?playid=271&g=2", baseUrl, ct); }
        await Task.Delay(500, ct);

        var jq = await GetGbkAsync(baseUrl + "?playid=270&g=2", baseUrl, ct);
        await Task.Delay(500, ct);
        var bq = await GetGbkAsync(baseUrl + "?playid=272&g=2", baseUrl, ct);

        // 以默认页为主，建立每场的卡片
        var cards = ParseJcCards(main);

        // 合并三种玩法的选项
        MergePlayOptions(cards, ParsePlayOptions(bf, "bf"), JcPlayTypes.Bf);
        MergePlayOptions(cards, ParsePlayOptions(jq, "jqs"), JcPlayTypes.Jq);
        MergePlayOptions(cards, ParsePlayOptions(bq, "bqc"), JcPlayTypes.Bq);

        return cards;
    }

    /// <summary>解析默认页为 JcMatchCard（含让球胜平负 + 胜平负）</summary>
    private static List<JcMatchCard> ParseJcCards(string? html)
    {
        var list = new List<JcMatchCard>();
        if (html is null) return list;

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var rows = doc.DocumentNode.SelectNodes("//tr[@data-matchnum]") ?? Enumerable.Empty<HtmlNode>();
        foreach (var tr in rows)
        {
            var fid = AttrLong(tr, "data-fixtureid");
            var no = Attr(tr, "data-matchnum");
            if (fid is null || no.Length == 0) continue;

            var card = new JcMatchCard
            {
                MatchId = fid,
                MatchNo = no,
                HomeTeam = Attr(tr, "data-homesxname"),
                AwayTeam = Attr(tr, "data-awaysxname"),
                Handicap = AttrDecimal(tr, "data-rangqiu")
            };

            // 让球胜平负 nspf
            var nspf = tr.SelectNodes(".//p[@data-type='nspf']") ?? Enumerable.Empty<HtmlNode>();
            var nv = nspf.Select(p => AttrDecimal(p, "data-sp")).Where(v => v is not null).ToList();
            if (nv.Count >= 3) { card.NspfWin = nv[0]; card.NspfDraw = nv[1]; card.NspfLoss = nv[2]; }

            // 胜平负 spf
            var spf = tr.SelectNodes(".//p[@data-type='spf']") ?? Enumerable.Empty<HtmlNode>();
            var sv = spf.Select(p => AttrDecimal(p, "data-sp")).Where(v => v is not null).ToList();
            if (sv.Count >= 3) { card.SpfWin = sv[0]; card.SpfDraw = sv[1]; card.SpfLoss = sv[2]; }

            list.Add(card);
        }
        return list;
    }

    /// <summary>解析玩法页（bf/jqs/bqc）为 场次号 -> 选项字典。
    /// 注意：比分页(bf)的选项在平行的展开行 tr.bet-more-wrap 里（不在 tr[data-matchnum] 内），
    /// 需按顺序与主行配对；总进球/半全场选项直接在主行内。</summary>
    private static Dictionary<string, List<(string Code, decimal Odds)>> ParsePlayOptions(string? html, string playType)
    {
        var result = new Dictionary<string, List<(string, decimal)>>();
        if (html is null) return result;

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var rows = doc.DocumentNode.SelectNodes("//tr[@data-matchnum]") ?? Enumerable.Empty<HtmlNode>();
        foreach (var tr in rows)
        {
            var no = Attr(tr, "data-matchnum");
            if (no.Length == 0) continue;

            var opts = new List<(string, decimal)>();
            // 先找主行内的选项
            foreach (var p in tr.SelectNodes($".//p[@data-type='{playType}']") ?? Enumerable.Empty<HtmlNode>())
            {
                var code = p.GetAttributeValue("data-value", "");
                var sp = AttrDecimal(p, "data-sp");
                if (code.Length > 0 && sp is not null) opts.Add((code, sp.Value));
            }

            // 比分等玩法：选项在紧随其后的展开行 tr.bet-more-wrap 里
            if (opts.Count == 0)
            {
                var more = tr.SelectSingleNode("following-sibling::tr[contains(@class,'bet-more-wrap')][1]");
                foreach (var p in more?.SelectNodes($".//p[@data-type='{playType}']") ?? Enumerable.Empty<HtmlNode>())
                {
                    var code = p.GetAttributeValue("data-value", "");
                    var sp = AttrDecimal(p, "data-sp");
                    if (code.Length > 0 && sp is not null) opts.Add((code, sp.Value));
                }
            }

            if (opts.Count > 0) result[no] = opts;
        }
        return result;
    }

    private static void MergePlayOptions(List<JcMatchCard> cards,
        Dictionary<string, List<(string Code, decimal Odds)>> options, string playType)
    {
        foreach (var card in cards)
        {
            if (!options.TryGetValue(card.MatchNo, out var opts)) continue;
            var dict = opts.ToDictionary(o => o.Code, o => o.Odds);
            switch (playType)
            {
                case JcPlayTypes.Bf: card.BfOdds = dict; break;
                case JcPlayTypes.Jq: card.JqOdds = dict; break;
                case JcPlayTypes.Bq: card.BqOdds = dict; break;
            }
        }
    }

    /// <summary>把四类玩法的卡片转成历史采样点（含中文显示名）。</summary>
    public static List<JcOddsSnapshot> ToSnapshots(IEnumerable<JcMatchCard> cards)
    {
        var result = new List<JcOddsSnapshot>();
        foreach (var c in cards)
        {
            var opts = new List<JcOddsHistoryPoint>();
            void Add(string play, string code, string name, decimal? odds)
            {
                if (odds is null) return;
                opts.Add(new JcOddsHistoryPoint
                {
                    MatchId = c.MatchId, MatchNo = c.MatchNo, PlayType = play,
                    OptionCode = code, OptionName = name, Odds = odds.Value
                });
            }

            // 让球胜平负
            Add(JcPlayTypes.Nspf, "3", "让胜", c.NspfWin);
            Add(JcPlayTypes.Nspf, "1", "让平", c.NspfDraw);
            Add(JcPlayTypes.Nspf, "0", "让负", c.NspfLoss);
            // 胜平负
            Add(JcPlayTypes.Spf, "3", "胜", c.SpfWin);
            Add(JcPlayTypes.Spf, "1", "平", c.SpfDraw);
            Add(JcPlayTypes.Spf, "0", "负", c.SpfLoss);
            // 全场比分
            foreach (var (code, odds) in c.BfOdds)
                Add(JcPlayTypes.Bf, code, code, odds);
            // 总进球
            foreach (var (code, odds) in c.JqOdds)
                Add(JcPlayTypes.Jq, code, JqLabel(code), odds);
            // 半全场
            foreach (var (code, odds) in c.BqOdds)
                Add(JcPlayTypes.Bq, code, BqLabel(code), odds);

            if (opts.Count > 0)
                result.Add(new JcOddsSnapshot { MatchId = c.MatchId, MatchNo = c.MatchNo, Options = opts });
        }
        return result;
    }

    /// <summary>总进球选项代码 → 显示名（0~6，7 表示 7+）</summary>
    private static string JqLabel(string code) => code switch
    {
        "7" => "7+球",
        _ => code + "球"
    };

    /// <summary>半全场选项代码 "半-全"（3=胜 1=平 0=负）→ 显示名</summary>
    private static string BqLabel(string code)
    {
        var parts = code.Split('-');
        if (parts.Length != 2) return code;
        string Half(char c) => c switch { '3' => "胜", '1' => "平", '0' => "负", _ => c.ToString() };
        string Full(char c) => c switch { '3' => "胜", '1' => "平", '0' => "负", _ => c.ToString() };
        return Half(parts[0][0]) + Full(parts[1][0]);
    }

    /* ---------------- 竞彩：data-* 属性解析 ---------------- */

    private static List<BettingCardRecord> ParseJc(string html)
    {
        var list = new List<BettingCardRecord>();
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var rows = doc.DocumentNode.SelectNodes("//tr[@data-matchnum]") ?? Enumerable.Empty<HtmlNode>();
        foreach (var tr in rows)
        {
            var fid = AttrLong(tr, "data-fixtureid");
            if (fid is null) continue;

            var rec = new BettingCardRecord
            {
                Board = Boards.Jc,
                MatchNo = Attr(tr, "data-matchnum"),
                MatchId = fid,
                HomeTeam = Attr(tr, "data-homesxname"),
                AwayTeam = Attr(tr, "data-awaysxname"),
                LeagueName = Attr(tr, "data-simpleleague"),
                Handicap = AttrDecimal(tr, "data-rangqiu"),
                CapturedAt = DateTime.Now
            };

            var date = Attr(tr, "data-matchdate");
            var time = Attr(tr, "data-matchtime");
            if (date.Length > 0 && time.Length > 0 &&
                DateTime.TryParse($"{date} {time}", out var dt)) rec.MatchTime = dt;

            // 让球胜平负：data-type="nspf" 的三个 data-sp
            var nspf = tr.SelectNodes(".//p[@data-type='nspf']") ?? Enumerable.Empty<HtmlNode>();
            var nspfVals = nspf.Select(p => AttrDecimal(p, "data-sp")).Where(v => v is not null).ToList();
            if (nspfVals.Count >= 3)
            {
                rec.SpfHome = nspfVals[0];
                rec.SpfDraw = nspfVals[1];
                rec.SpfAway = nspfVals[2];
            }
            else
            {
                // 无让球胜平负则取胜平负 data-type="spf"
                var spf = tr.SelectNodes(".//p[@data-type='spf']") ?? Enumerable.Empty<HtmlNode>();
                var spfVals = spf.Select(p => AttrDecimal(p, "data-sp")).Where(v => v is not null).ToList();
                if (spfVals.Count >= 3) { rec.SpfHome = spfVals[0]; rec.SpfDraw = spfVals[1]; rec.SpfAway = spfVals[2]; }
            }

            // 单关：data-kdg 或 data-subactive 含单关标记
            rec.IsSingle = tr.GetAttributeValue("data-kdg", "").Contains("单") ||
                           tr.GetAttributeValue("data-subactive", "").Contains("dg:1");

            list.Add(rec);
        }
        return list;
    }

    /* ---------------- 北单：value JS 对象 + fid 属性解析 ---------------- */

    private static List<BettingCardRecord> ParseBd(string html)
    {
        var list = new List<BettingCardRecord>();
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var rows = doc.DocumentNode.SelectNodes("//tr[@fid]") ?? Enumerable.Empty<HtmlNode>();
        foreach (var tr in rows)
        {
            var fid = AttrLong(tr, "fid");
            if (fid is null) continue;

            var value = tr.GetAttributeValue("value", "");

            // 跳过已停售/已开赛的旧场次（disabled='yes'，通常是昨天或更早的场次）
            if (string.Equals(JsVal(value, "disabled"), "yes", StringComparison.OrdinalIgnoreCase)) continue;

            var rec = new BettingCardRecord
            {
                Board = Boards.Bd,
                MatchId = fid,
                MatchNo = JsVal(value, "index"),
                LeagueName = JsVal(value, "leagueName"),
                HomeTeam = JsVal(value, "homeTeam"),
                AwayTeam = JsVal(value, "guestTeam"),
                Handicap = JsValDecimal(value, "rangqiuNum"),
                CapturedAt = DateTime.Now
            };

            // 比赛时间：优先用 endTime（精确到分钟），退而用 scheduleDate
            var endTime = JsVal(value, "endTime");
            var date = JsVal(value, "scheduleDate");
            if (endTime.Length > 0 && DateTime.TryParse(endTime, out var dt)) rec.MatchTime = dt;
            else if (date.Length > 0 && DateTime.TryParse(date, out dt)) rec.MatchTime = dt;

            // 赔率：北单行的平均欧赔在 class=pjoz 的三个 span
            var pjoz = tr.SelectNodes(".//span[contains(@class,'pjoz')]") ?? Enumerable.Empty<HtmlNode>();
            var vals = pjoz.Select(s => ParseNum(s.InnerText)).Where(v => v is not null).Select(v => v!.Value).ToList();
            if (vals.Count >= 3) { rec.SpfHome = vals[0]; rec.SpfDraw = vals[1]; rec.SpfAway = vals[2]; }

            if (!string.IsNullOrEmpty(rec.HomeTeam) || !string.IsNullOrEmpty(rec.AwayTeam))
                list.Add(rec);
        }
        return list;
    }

    private static string JsVal(string js, string key)
    {
        var m = Regex.Match(js, $@"{key}\s*:\s*'([^']*)'");
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(js, $@"{key}\s*:\s*([^,}}]+)");
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    private static decimal? JsValDecimal(string js, string key)
    {
        var v = JsVal(js, key);
        return decimal.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : null;
    }

    /* ---------------- 胜负彩：data-bjpl + team-l/team-r 解析 ---------------- */

    private static List<BettingCardRecord> ParseSfc(string html, out string? expect)
    {
        expect = null;
        var em = ExpectRegex.Match(html);
        if (em.Success) expect = em.Groups[1].Value;

        var list = new List<BettingCardRecord>();
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var rows = doc.DocumentNode.SelectNodes("//tr[@data-cid]") ?? Enumerable.Empty<HtmlNode>();
        foreach (var tr in rows)
        {
            var noNode = tr.SelectSingleNode(".//td[contains(@class,'td-no')]");
            var no = CleanText(noNode?.InnerText ?? "");
            if (no.Length == 0 || !int.TryParse(no, out var noInt) || noInt < 1 || noInt > 20) continue;

            var fid = ExtractFid(tr.InnerHtml);

            var home = ExtractTeamByClass(tr, "team-l");
            var away = ExtractTeamByClass(tr, "team-r");
            var league = CleanText(tr.SelectSingleNode(".//td[contains(@class,'td-evt')]")?.InnerText ?? "");

            var rec = new BettingCardRecord
            {
                Board = Boards.Sfc,
                Expect = expect,
                MatchNo = no,
                MatchId = fid,
                HomeTeam = home,
                AwayTeam = away,
                LeagueName = league,
                CapturedAt = DateTime.Now
            };

            // 赔率在 data-bjpl="胜,平,负"
            var bjpl = tr.GetAttributeValue("data-bjpl", "");
            var parts = bjpl.Split(',');
            if (parts.Length >= 3)
            {
                rec.SpfHome = ParseNum(parts[0]);
                rec.SpfDraw = ParseNum(parts[1]);
                rec.SpfAway = ParseNum(parts[2]);
            }

            list.Add(rec);
        }
        return list;
    }

    private static string? ExtractTeamByClass(HtmlNode tr, string cls)
    {
        var a = tr.SelectSingleNode($".//a[contains(@class,'{cls}')]");
        if (a is null) return null;
        var name = CleanText(a.InnerText);
        return name.Length > 0 ? name : null;
    }

    /* ---------------- 工具 ---------------- */

    private static long? ExtractFid(string innerHtml)
    {
        var m = FidRegex.Match(innerHtml);
        return m.Success && long.TryParse(m.Groups[1].Value, out var fid) ? fid : null;
    }

    private static string Attr(HtmlNode n, string name) => n.GetAttributeValue(name, "")?.Trim() ?? "";

    private static long? AttrLong(HtmlNode n, string name)
    {
        var v = Attr(n, name);
        return long.TryParse(v, out var r) ? r : null;
    }

    private static decimal? AttrDecimal(HtmlNode n, string name)
    {
        var v = Attr(n, name);
        return decimal.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : null;
    }

    private static decimal? ParseNum(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = NumRegex.Match(text);
        return m.Success && decimal.TryParse(m.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static string? ExtractTeam(HtmlNode tr, string cls)
    {
        var selector = cls.Length > 0 ? $".//a[contains(@class,'{cls}')]" : ".//a[contains(@href,'team')]";
        foreach (var a in tr.SelectNodes(selector) ?? Enumerable.Empty<HtmlNode>())
        {
            var title = a.GetAttributeValue("title", "");
            var name = title.Length > 0 ? CleanText(title) : CleanText(a.InnerText);
            if (name.Length > 0) return name;
        }
        return null;
    }

    private static List<decimal> ExtractPrices(string text)
    {
        var result = new List<decimal>();
        foreach (Match m in NumRegex.Matches(text))
        {
            if (decimal.TryParse(m.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
                result.Add(v);
        }
        return result;
    }

    private static string CleanText(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var text = HtmlEntity.DeEntitize(html);
        text = Regex.Replace(text, @"<[^>]+>", " ");
        text = text.Replace("&nbsp;", " ").Replace("\u00a0", " ");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private async Task<string?> GetGbkAsync(string url, string referer, CancellationToken ct)
    {
        // 复用 OddsHttp 的反爬通道：自动处理 428/429 限流与 TencentEdgeOne JS 挑战页
        var (html, reason) = await OddsHttp.GetAsync(_http, url, referer, ct);
        return html;
    }
}
