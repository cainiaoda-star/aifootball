using System.Globalization;
using System.Text.RegularExpressions;
using AiFootBall.Core.Abstractions;
using AiFootBall.Core.Models;
using AiFootBall.Core.Options;
using HtmlAgilityPack;

namespace AiFootBall.Sources;

/// <summary>
/// 500.com 足球赛事采集器。
/// 数据来源：
///   1) https://live.500.com/wanchang.php?e=yyyy-MM-dd  当日全部完场赛事
///   2) https://live.500.com/?e=yyyy-MM-dd              当日竞彩赛程（含未开赛、让球）
/// 页面为 GBK 编码，行结构 tr[id=a{fid}]，队名链接 /team/{id}/，联赛链接 /zuqiu-{sid}/。
/// </summary>
public sealed class FiveHundredMatchSource : IMatchSource
{
    private static readonly Regex TeamIdRegex = new(@"/team/(\d+)", RegexOptions.Compiled);
    private static readonly Regex LeagueIdRegex = new(@"zuqiu-(\d+)", RegexOptions.Compiled);
    private static readonly Regex TimeRegex = new(@"^(\d{2})-(\d{2})\s+(\d{2}):(\d{2})$", RegexOptions.Compiled);
    private static readonly Regex ScoreRegex = new(@"^(\d{1,2})\s*[-:]\s*(\d{1,2})$", RegexOptions.Compiled);
    private static readonly Regex HandicapRegex = new(@"\(([+-]?\d+(?:\.\d+)?)\)", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly CollectorOptions _opt;

    public FiveHundredMatchSource(HttpClient http, CollectorOptions opt)
    {
        _http = http;
        _opt = opt;
    }

    public string Name => "500";

    public async Task<(IReadOnlyList<MatchRecord> Matches, IReadOnlyList<LeagueRecord> Leagues, IReadOnlyList<TeamRecord> Teams)>
        FetchByDateAsync(DateOnly date, CancellationToken ct = default)
        => await FetchByDateAsync(date, resultsOnly: false, ct);

    /// <summary>
    /// </summary>
    /// <param name="resultsOnly">
    /// true = 只抓完场页。历史回填用：竞彩页（live.500.com/?e=...）对过去的日期只剩十几行残留赛程，
    /// 却要多花一次请求，实测 2026-07-18 完场页 877 场、竞彩页只有 15 行——直接跳过，回填速度约快一倍。
    /// 线上日常采集必须为 false，未开赛场次与让球只在竞彩页上有。
    /// </param>
    public async Task<(IReadOnlyList<MatchRecord> Matches, IReadOnlyList<LeagueRecord> Leagues, IReadOnlyList<TeamRecord> Teams)>
        FetchByDateAsync(DateOnly date, bool resultsOnly, CancellationToken ct = default)
    {
        var ymd = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var matches = new List<MatchRecord>();
        var leagues = new Dictionary<int, LeagueRecord>();
        var teams = new Dictionary<int, TeamRecord>();

        var urls = resultsOnly
            ? new[] { $"https://live.500.com/wanchang.php?e={ymd}" }
            : new[]
            {
                $"https://live.500.com/wanchang.php?e={ymd}",
                $"https://live.500.com/?e={ymd}"
            };

        foreach (var url in urls)
        {
            string html;
            try
            {
                using var resp = await _http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) continue;
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                html = TextDecoder.DecodeGbk(bytes);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                continue;
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            foreach (var tr in doc.DocumentNode.SelectNodes("//tr[starts-with(@id,'a')]") ?? Enumerable.Empty<HtmlNode>())
            {
                var parsed = ParseRow(tr, date);
                if (parsed is null) continue;

                var (match, league, home, away) = parsed.Value;
                matches.Add(match);
                if (league is not null) leagues[league.LeagueId] = league;
                if (home is not null) teams[home.TeamId] = home;
                if (away is not null) teams[away.TeamId] = away;
            }

            await Task.Delay(_opt.RequestDelayMs, ct);
        }

        // 同一赛事可能同时出现在完场页与竞彩页，按 fid 合并：
        // 赛果取完场页，让球/队 ID 等缺失字段从另一条补齐
        var merged = matches
            .GroupBy(m => m.MatchId)
            .Select(g => Merge(g.ToList()))
            .ToList();

        return (merged, leagues.Values.ToList(), teams.Values.ToList());
    }

    private static MatchRecord Merge(List<MatchRecord> group)
    {
        var primary = group
            .OrderByDescending(m => m.IsFinished ? 2 : m.Status == 1 ? 1 : 0)
            .First();

        if (group.Count == 1) return primary;

        return new MatchRecord
        {
            MatchId = primary.MatchId,
            LeagueId = primary.LeagueId ?? group.Select(x => x.LeagueId).FirstOrDefault(x => x.HasValue),
            LeagueName = string.IsNullOrEmpty(primary.LeagueName) ? group.First(x => x.LeagueName.Length > 0).LeagueName : primary.LeagueName,
            MatchTime = primary.MatchTime,
            HomeTeamId = primary.HomeTeamId ?? group.Select(x => x.HomeTeamId).FirstOrDefault(x => x.HasValue),
            AwayTeamId = primary.AwayTeamId ?? group.Select(x => x.AwayTeamId).FirstOrDefault(x => x.HasValue),
            HomeTeamName = primary.HomeTeamName,
            AwayTeamName = primary.AwayTeamName,
            HomeScore = primary.HomeScore,
            AwayScore = primary.AwayScore,
            HalfHomeScore = primary.HalfHomeScore,
            HalfAwayScore = primary.HalfAwayScore,
            Status = primary.Status,
            Handicap = primary.Handicap ?? group.Select(x => x.Handicap).FirstOrDefault(x => x.HasValue),
            Source = primary.Source
        };
    }

    private (MatchRecord, LeagueRecord?, TeamRecord?, TeamRecord?)? ParseRow(HtmlNode tr, DateOnly date)
    {
        var idAttr = tr.GetAttributeValue("id", "");
        if (!long.TryParse(idAttr.TrimStart('a'), out var fid)) return null;

        var texts = (tr.SelectNodes("./td") ?? Enumerable.Empty<HtmlNode>())
            .Select(td => PinbleLotterySource.CleanText(td.InnerText))
            .ToList();
        if (texts.Count < 5) return null;

        // 时间
        DateTime? matchTime = null;
        foreach (var t in texts)
        {
            var m = TimeRegex.Match(t);
            if (!m.Success) continue;
            var year = date.Year;
            var dt = new DateTime(year, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value), 0);
            // 处理跨年（如 1 月抓取 12 月赛事）
            if (dt.Month - date.Month > 6) dt = dt.AddYears(-1);
            else if (date.Month - dt.Month > 6) dt = dt.AddYears(1);
            matchTime = dt;
            break;
        }
        if (matchTime is null) return null;

        // 联赛
        LeagueRecord? league = null;
        var leagueA = tr.SelectSingleNode(".//a[contains(@href,'liansai.500.com/zuqiu-')]");
        if (leagueA is not null)
        {
            var lm = LeagueIdRegex.Match(leagueA.GetAttributeValue("href", ""));
            var name = PinbleLotterySource.CleanText(leagueA.InnerText);
            if (lm.Success && name.Length > 0)
                league = new LeagueRecord { LeagueId = int.Parse(lm.Groups[1].Value), Name = name };
        }

        // 部分赛事（如地方联赛）联赛单元格无链接，退化为纯文本名称
        var leagueNameFallback = league?.Name;
        if (leagueNameFallback is null && texts.Count > 0)
        {
            var first = texts[0];
            if (first.Length is > 1 and < 20 && !first.Any(char.IsDigit) && !first.Contains('第'))
                leagueNameFallback = first;
        }

        // 主客队
        var teamAs = tr.SelectNodes(".//a[contains(@href,'liansai.500.com/team/')]");
        if (teamAs is null || teamAs.Count < 2) return null;

        var homeA = teamAs[0];
        var awayA = teamAs[1];
        var home = BuildTeam(homeA, league?.LeagueId);
        var away = BuildTeam(awayA, league?.LeagueId);
        if (home is null || away is null) return null;

        // 比分（td.pk 内的 clt1 / clt3）
        int? homeScore = null, awayScore = null;
        var pk = tr.SelectSingleNode(".//div[@class='pk']");
        if (pk is not null)
        {
            var h = pk.SelectSingleNode(".//a[contains(@class,'clt1')]");
            var a = pk.SelectSingleNode(".//a[contains(@class,'clt3')]");
            if (h is not null && a is not null)
            {
                if (int.TryParse(PinbleLotterySource.CleanText(h.InnerText), out var hv)) homeScore = hv;
                if (int.TryParse(PinbleLotterySource.CleanText(a.InnerText), out var av)) awayScore = av;
            }
        }

        // 比分/半场兜底（如竞彩页日期列为 "1 - 2" 的纯文本行）
        var scoreTexts = texts.Where(t => ScoreRegex.IsMatch(t)).ToList();
        int? halfHome = null, halfAway = null;
        if (homeScore is null && scoreTexts.Count > 0)
        {
            var m = ScoreRegex.Match(scoreTexts[0]);
            homeScore = int.Parse(m.Groups[1].Value);
            awayScore = int.Parse(m.Groups[2].Value);
            scoreTexts.RemoveAt(0);
        }
        if (scoreTexts.Count > 0)
        {
            var m = ScoreRegex.Match(scoreTexts[0]);
            halfHome = int.Parse(m.Groups[1].Value);
            halfAway = int.Parse(m.Groups[2].Value);
        }

        // 状态
        var status = 0;
        if (texts.Any(t => t.Contains("完"))) status = 3;
        else if (texts.Any(t => t.Any(c => "中上下腰".Contains(c)))) status = 1;
        else if (texts.Any(t => t.Contains("取") || t.Contains("推") || t.Contains("延"))) status = 2;
        if (status != 3) { homeScore = null; awayScore = null; halfHome = null; halfAway = null; }
        else if (homeScore is null) status = 0; // 标完但无比分，忽略

        // 让球
        decimal? handicap = null;
        var rq = tr.SelectSingleNode(".//span[contains(@class,'sp_rq')]");
        if (rq is not null)
        {
            var m = HandicapRegex.Match(PinbleLotterySource.CleanText(rq.InnerText));
            if (m.Success) handicap = decimal.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        var record = new MatchRecord
        {
            MatchId = fid,
            LeagueId = league?.LeagueId,
            LeagueName = leagueNameFallback ?? "",
            MatchTime = matchTime.Value,
            HomeTeamId = home.TeamId,
            AwayTeamId = away.TeamId,
            HomeTeamName = home.Name,
            AwayTeamName = away.Name,
            HomeScore = homeScore,
            AwayScore = awayScore,
            HalfHomeScore = halfHome,
            HalfAwayScore = halfAway,
            Status = status,
            Handicap = handicap
        };

        return (record, league, home, away);
    }

    private static TeamRecord? BuildTeam(HtmlNode a, int? leagueId)
    {
        var m = TeamIdRegex.Match(a.GetAttributeValue("href", ""));
        if (!m.Success) return null;

        var nameNode = a.SelectSingleNode(".//span[@class='mainName' or @class='clientName']");
        var name = PinbleLotterySource.CleanText(nameNode?.InnerText ?? a.InnerText);
        if (name.Length == 0) return null;

        return new TeamRecord { TeamId = int.Parse(m.Groups[1].Value), Name = name, LeagueId = leagueId };
    }
}
