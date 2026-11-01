using System.Text;
using AiFootBall.Core.Models;
using AiFootBall.Data;

namespace AiFootBall.Sources;

/// <summary>大奶：历史频率聚合 + 今日受注场观察扫描。</summary>
public sealed class MilkScanService
{
    private readonly MilkRepository _milk;
    private readonly BettingRepository _betting;

    public MilkScanService(MilkRepository milk, BettingRepository betting)
    {
        _milk = milk;
        _betting = betting;
    }

    public sealed class TagStat
    {
        public string Tag { get; init; } = "";
        public string Label { get; init; } = "";
        public int Count { get; init; }
        public decimal RatePct { get; init; }
        public decimal? JcSpMedian { get; init; }
    }

    public sealed class LeagueStat
    {
        public string League { get; init; } = "";
        public int MilkMatches { get; init; }
        public int TotalMatches { get; init; }
        public decimal RatePct { get; init; }
    }

    public sealed class SampleCase
    {
        public long MatchId { get; init; }
        public DateTime MatchTime { get; init; }
        public string? League { get; init; }
        public string? Home { get; init; }
        public string? Away { get; init; }
        public string Score { get; init; } = "";
        public string? Half { get; init; }
        public string[] Tags { get; init; } = Array.Empty<string>();
        public string[] Labels { get; init; } = Array.Empty<string>();
        public string? Board { get; init; }
        public string? MatchNo { get; init; }
    }

    public sealed class StatsResult
    {
        public string Scope { get; init; } = "all";
        public DateTime From { get; init; }
        public DateTime To { get; init; }
        public int MatchCount { get; init; }
        public int MilkMatchCount { get; init; }
        public decimal MilkDayPct90 { get; init; }
        public List<TagStat> Tags { get; init; } = new();
        public List<LeagueStat> Leagues { get; init; } = new();
        public List<SampleCase> Samples { get; init; } = new();
        public string Note { get; init; } = "尾部事件复盘统计，非投注建议。";
    }

    public const int TodayPickLimit = 6;

    public sealed class WatchItem
    {
        public string Board { get; init; } = "";
        public string MatchNo { get; init; } = "";
        public long? MatchId { get; init; }
        public string? League { get; init; }
        public string? Home { get; init; }
        public string? Away { get; init; }
        public DateTime? MatchTime { get; init; }
        public List<MilkEventTaxonomy.MilkTagHit> Tags { get; init; } = new();
        public decimal? EuHome { get; init; }
        public decimal? EuAway { get; init; }
        public decimal? TotalLine { get; init; }
        public decimal? Jq7Sp { get; init; }
        public decimal? BqWinLoseSp { get; init; }
        public decimal? BqLoseWinSp { get; init; }
        public decimal? BqWinDrawSp { get; init; }
        public decimal? BqLoseDrawSp { get; init; }
        /// <summary>精选排序分（越高越靠前）。</summary>
        public decimal Score { get; init; }
        /// <summary>主观察点（最强标签中文名）。</summary>
        public string? Focus { get; init; }
    }

    public sealed class TodayResult
    {
        public DateTime GeneratedAt { get; init; }
        public int CardCount { get; init; }
        /// <summary>原始有标签的候选池大小（精选前）。</summary>
        public int PoolCount { get; init; }
        public int WatchCount { get; init; }
        public int PickLimit { get; init; } = TodayPickLimit;
        public List<WatchItem> Items { get; init; } = new();
        public string Note { get; init; } =
            "冷门参考最多 6 场，只对照、不购买，不生成方案。";
    }

    public async Task<StatsResult> GetStatsAsync(string? scope, DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        var sc = NormalizeScope(scope);
        var toDate = (to ?? DateTime.Today).Date;
        var fromDate = (from ?? toDate.AddDays(-90)).Date;
        if (fromDate > toDate) (fromDate, toDate) = (toDate, fromDate);

        var rows = await _milk.GetFinishedAsync(sc, fromDate, toDate, ct);
        var tagCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var leagueTotal = new Dictionary<string, int>(StringComparer.Ordinal);
        var leagueMilk = new Dictionary<string, int>(StringComparer.Ordinal);
        var milkByDay = new HashSet<DateTime>();
        var allDays = new HashSet<DateTime>();
        var hitPairs = new List<(long MatchId, string Tag)>();
        var samples = new List<SampleCase>();
        var milkMatchCount = 0;

        foreach (var r in rows)
        {
            allDays.Add(r.MatchTime.Date);
            var league = string.IsNullOrWhiteSpace(r.LeagueName) ? "未知" : r.LeagueName!;
            leagueTotal[league] = leagueTotal.GetValueOrDefault(league) + 1;

            var tags = MilkEventTaxonomy.ClassifyFinished(new MilkEventTaxonomy.FinishedInput
            {
                Home = r.HomeScore,
                Away = r.AwayScore,
                HalfHome = r.HalfHomeScore,
                HalfAway = r.HalfAwayScore,
                EuHome = r.EuHome,
                EuAway = r.EuAway,
                ExHome = r.ExHome,
                ExAway = r.ExAway
            });
            if (tags.Count == 0) continue;

            milkMatchCount++;
            milkByDay.Add(r.MatchTime.Date);
            leagueMilk[league] = leagueMilk.GetValueOrDefault(league) + 1;
            foreach (var t in tags)
            {
                tagCounts[t.Tag] = tagCounts.GetValueOrDefault(t.Tag) + 1;
                hitPairs.Add((r.MatchId, t.Tag));
            }

            if (samples.Count < 40)
            {
                samples.Add(new SampleCase
                {
                    MatchId = r.MatchId,
                    MatchTime = r.MatchTime,
                    League = r.LeagueName,
                    Home = r.HomeTeam,
                    Away = r.AwayTeam,
                    Score = $"{r.HomeScore}:{r.AwayScore}",
                    Half = r.HalfHomeScore is int hh && r.HalfAwayScore is int ha ? $"{hh}:{ha}" : null,
                    Tags = tags.Select(x => x.Tag).ToArray(),
                    Labels = tags.Select(x => x.Label).ToArray(),
                    Board = r.Board,
                    MatchNo = r.MatchNo
                });
            }
        }

        Dictionary<string, decimal?> spMedians = new(StringComparer.Ordinal);
        if (sc is "jc" or "card")
        {
            try { spMedians = await _milk.GetJcSpMediansForHitsAsync(hitPairs, ct); }
            catch { /* SP 缺失时仍返回频率 */ }
        }

        var n = Math.Max(1, rows.Count);
        var tagStats = tagCounts
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new TagStat
            {
                Tag = kv.Key,
                Label = MilkEventTaxonomy.TagLabels.TryGetValue(kv.Key, out var lab) ? lab : kv.Key,
                Count = kv.Value,
                RatePct = Math.Round(100m * kv.Value / n, 2),
                JcSpMedian = spMedians.TryGetValue(kv.Key, out var med) ? med : null
            })
            .ToList();

        var leagues = leagueMilk
            .OrderByDescending(kv => kv.Value)
            .Take(15)
            .Select(kv =>
            {
                var total = leagueTotal.GetValueOrDefault(kv.Key);
                return new LeagueStat
                {
                    League = kv.Key,
                    MilkMatches = kv.Value,
                    TotalMatches = total,
                    RatePct = total > 0 ? Math.Round(100m * kv.Value / total, 2) : 0
                };
            })
            .ToList();

        var dayWindow = allDays.Where(d => d >= toDate.AddDays(-90) && d <= toDate).ToList();
        if (dayWindow.Count == 0) dayWindow = allDays.ToList();
        var milkDayPct = dayWindow.Count == 0
            ? 0
            : Math.Round(100m * dayWindow.Count(d => milkByDay.Contains(d)) / dayWindow.Count, 1);

        samples = samples.OrderByDescending(s => s.MatchTime).Take(25).ToList();

        return new StatsResult
        {
            Scope = sc,
            From = fromDate,
            To = toDate,
            MatchCount = rows.Count,
            MilkMatchCount = milkMatchCount,
            MilkDayPct90 = milkDayPct,
            Tags = tagStats,
            Leagues = leagues,
            Samples = samples
        };
    }

    public async Task<TodayResult> GetTodayAsync(CancellationToken ct = default)
    {
        var cards = await _milk.GetOpenCardsAsync(ct);
        var seeds = cards.Select(c => new ScanCard(
            c.Board, c.MatchNo, c.MatchId, c.LeagueName, c.HomeTeam, c.AwayTeam, c.MatchTime, c.SpfHome, c.SpfAway)).ToList();
        var pool = await WatchPoolAsync(seeds, ct);
        var items = PickDiversified(DedupSameMatch(pool), TodayPickLimit);

        return new TodayResult
        {
            GeneratedAt = DateTime.Now,
            CardCount = cards.Count,
            PoolCount = pool.Count,
            WatchCount = items.Count,
            PickLimit = TodayPickLimit,
            Items = items
        };
    }

    /// <summary>复盘当天开赛的竞彩、北单，按冷门参考同一套挑 6 场，再对照赛果。</summary>
    public async Task<string> ReviewBodyAsync(
        IReadOnlyList<BettingCardRecord> cards,
        IReadOnlyDictionary<long, ReviewScore> scores,
        CancellationToken ct = default)
    {
        var seeds = cards
            .Where(c => string.Equals(c.Board, Boards.Jc, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Board, Boards.Bd, StringComparison.OrdinalIgnoreCase))
            .Select(c => new ScanCard(
                c.Board, c.MatchNo, c.MatchId, c.LeagueName, c.HomeTeam, c.AwayTeam, c.MatchTime, c.SpfHome, c.SpfAway))
            .ToList();
        var pool = await WatchPoolAsync(seeds, ct);
        var picked = PickDiversified(DedupSameMatch(pool), TodayPickLimit);
        return FormatReview(picked, scores);
    }

    public static string FormatReview(IReadOnlyList<WatchItem> items, IReadOnlyDictionary<long, ReviewScore> scores)
    {
        if (items.Count == 0) return "这天没有冷门参考。";

        var sb = new StringBuilder();
        var done = items.Count(it => ScoreOf(it, scores)?.Finished == true);
        var parts = new List<string>();
        void Bucket(string name, string bucket)
        {
            var g = items.Where(it => FocusBucket(it) == bucket).ToList();
            if (g.Count == 0) return;
            var hit = g.Count(it => Judge(it, ScoreOf(it, scores)) == "打出");
            parts.Add($"{name} {g.Count} 场打出 {hit}");
        }
        Bucket("高球", "goals");
        Bucket("翻转", "flip");
        Bucket("爆冷", "upset");
        Bucket("非常规比分", "other");
        var pending = items.Count - done;
        var tail = pending > 0 ? $"，未完场 {pending}" : "";
        sb.AppendLine($"- {items.Count} 场，完场 {done}{tail}。{string.Join("，", parts)}。");
        sb.AppendLine();

        foreach (var it in items)
        {
            var score = ScoreOf(it, scores);
            var board = string.Equals(it.Board, Boards.Jc, StringComparison.OrdinalIgnoreCase) ? "竞彩"
                : string.Equals(it.Board, Boards.Bd, StringComparison.OrdinalIgnoreCase) ? "北单" : it.Board;
            var when = it.MatchTime?.ToString("HH:mm") ?? "";
            sb.AppendLine($"### {board} {it.MatchNo} {it.League} {it.Home} vs {it.Away} {when}");
            sb.AppendLine();
            var notes = string.Join("；", it.Tags.Select(t => string.IsNullOrWhiteSpace(t.Note) ? t.Label : $"{t.Label} {t.Note}"));
            sb.AppendLine($"- 主看 {it.Focus}。{notes}");
            if (score?.Finished == true)
            {
                var half = score.HalfHomeScore is int hh && score.HalfAwayScore is int ha ? $"，半场 {hh}:{ha}" : "";
                sb.AppendLine($"- 赛果 {score.HomeScore}:{score.AwayScore}{half}，进球 {score.HomeScore + score.AwayScore}");
                var happened = MilkEventTaxonomy.ClassifyFinished(new MilkEventTaxonomy.FinishedInput
                {
                    Home = score.HomeScore!.Value,
                    Away = score.AwayScore!.Value,
                    HalfHome = score.HalfHomeScore,
                    HalfAway = score.HalfAwayScore,
                    EuHome = it.EuHome,
                    EuAway = it.EuAway
                });
                sb.AppendLine(happened.Count == 0
                    ? "- 当天没有打出这些尾部。"
                    : $"- 当天打出：{string.Join("、", happened.Select(h => h.Label))}。");
            }
            else
            {
                sb.AppendLine("- 还没有完场比分。");
            }
            sb.AppendLine($"- 对照：{Judge(it, score)}");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    readonly record struct ScanCard(
        string Board, string MatchNo, long? MatchId, string? League, string? Home, string? Away,
        DateTime? MatchTime, decimal? SpfHome, decimal? SpfAway);

    async Task<List<WatchItem>> WatchPoolAsync(IReadOnlyList<ScanCard> cards, CancellationToken ct)
    {
        var fids = cards.Where(c => c.MatchId is not null).Select(c => c.MatchId!.Value).Distinct().ToList();
        var closingEu = fids.Count > 0 ? await _betting.GetClosingEuAsync(fids, ct) : new();
        var exRatio = fids.Count > 0 ? await _betting.GetExchangeRatioAsync(fids, ct) : new();
        var totalLines = fids.Count > 0 ? await _betting.GetTotalLineAsync(fids, ct) : new();
        var jcOpts = fids.Count > 0 ? await _milk.GetLatestJcOptionOddsAsync(fids, ct) : new();

        var items = new List<WatchItem>();
        foreach (var c in cards)
        {
            decimal? euH = c.SpfHome, euA = c.SpfAway;
            decimal? exH = null, exA = null, line = null;
            decimal? jq7 = null, bqWl = null, bqLw = null, bqWd = null, bqLd = null, bfOh = null, bfOa = null;

            if (c.MatchId is long mid)
            {
                if (closingEu.TryGetValue(mid, out var eu))
                {
                    euH ??= eu.Home;
                    euA ??= eu.Away;
                }
                if (exRatio.TryGetValue(mid, out var ex))
                {
                    exH = ex.Home;
                    exA = ex.Away;
                }
                totalLines.TryGetValue(mid, out line);
                if (jcOpts.TryGetValue(mid, out var map))
                {
                    jq7 = Pick(map, JcPlayTypes.Jq, "7", "7+");
                    bqWl = Pick(map, JcPlayTypes.Bq, "3-0");
                    bqLw = Pick(map, JcPlayTypes.Bq, "0-3");
                    bqWd = Pick(map, JcPlayTypes.Bq, "3-1");
                    bqLd = Pick(map, JcPlayTypes.Bq, "0-1");
                    bfOh = PickContains(map, JcPlayTypes.Bf, "胜其它", "胜其他");
                    bfOa = PickContains(map, JcPlayTypes.Bf, "负其它", "负其他");
                }
            }

            var tags = MilkEventTaxonomy.ClassifyWatch(new MilkEventTaxonomy.WatchInput
            {
                EuHome = euH,
                EuAway = euA,
                ExHome = exH,
                ExAway = exA,
                TotalLine = line,
                Jq7Sp = jq7,
                BqWinLoseSp = bqWl,
                BqLoseWinSp = bqLw,
                BqWinDrawSp = bqWd,
                BqLoseDrawSp = bqLd,
                BfOtherHomeSp = bfOh,
                BfOtherAwaySp = bfOa
            });
            if (tags.Count == 0) continue;

            var scored = ScoreWatch(tags, c.Board, euH, euA, exH, exA, line, jq7, bqWl, bqLw, bqWd, bqLd);
            if (scored.Score < 28m) continue;

            items.Add(new WatchItem
            {
                Board = c.Board,
                MatchNo = c.MatchNo,
                MatchId = c.MatchId,
                League = c.League,
                Home = c.Home,
                Away = c.Away,
                MatchTime = c.MatchTime,
                Tags = tags,
                EuHome = euH,
                EuAway = euA,
                TotalLine = line,
                Jq7Sp = jq7,
                BqWinLoseSp = bqWl,
                BqLoseWinSp = bqLw,
                BqWinDrawSp = bqWd,
                BqLoseDrawSp = bqLd,
                Score = scored.Score,
                Focus = scored.Focus
            });
        }
        return items;
    }

    static ReviewScore? ScoreOf(WatchItem it, IReadOnlyDictionary<long, ReviewScore> scores)
    {
        if (it.MatchId is not long id) return null;
        return scores.TryGetValue(id, out var score) ? score : null;
    }

    static string Judge(WatchItem it, ReviewScore? score)
    {
        if (score is not { Finished: true, HomeScore: int h, AwayScore: int a }) return "未完场";
        return FocusBucket(it) switch
        {
            "goals" => h + a >= 7 ? "打出" : "没打出",
            "flip" => Flip(score) switch
            {
                null => "没有半场比分",
                true => "打出",
                false => "没打出"
            },
            "upset" => Upset(it, h, a) ? "打出" : "没打出",
            _ => MilkEventTaxonomy.IsJcScoreOtherHome(h, a) || MilkEventTaxonomy.IsJcScoreOtherAway(h, a)
                ? "打出" : "没打出"
        };
    }

    static bool? Flip(ReviewScore score)
    {
        if (score.HalfHomeScore is not int hh || score.HalfAwayScore is not int ha) return null;
        var halfHome = hh > ha;
        var halfAway = hh < ha;
        var fullHome = score.HomeScore > score.AwayScore;
        var fullAway = score.HomeScore < score.AwayScore;
        if (!halfHome && !halfAway) return false;
        var fullDraw = score.HomeScore == score.AwayScore;
        return (halfHome && (fullAway || fullDraw)) || (halfAway && (fullHome || fullDraw));
    }

    static bool Upset(WatchItem it, int home, int away)
    {
        var note = it.Tags.FirstOrDefault(t => t.Tag == MilkEventTaxonomy.Tags.UpsetWatch)?.Note ?? "";
        var homeHot = it.EuHome is <= MilkEventTaxonomy.UpsetWatchEuMax
            || note.Contains("必发主", StringComparison.Ordinal);
        var awayHot = it.EuAway is <= MilkEventTaxonomy.UpsetWatchEuMax
            || note.Contains("必发客", StringComparison.Ordinal);
        if (!homeHot && !awayHot) return false;
        var homeFailed = home <= away;
        var awayFailed = away <= home;
        if (homeHot && awayHot) return homeFailed && awayFailed;
        return homeHot ? homeFailed : awayFailed;
    }

    private static (decimal Score, string Focus) ScoreWatch(
        List<MilkEventTaxonomy.MilkTagHit> tags,
        string board,
        decimal? euH, decimal? euA, decimal? exH, decimal? exA,
        decimal? line, decimal? jq7, decimal? bqWl, decimal? bqLw, decimal? bqWd, decimal? bqLd)
    {
        decimal score = 0;
        string focus = tags[0].Label;
        decimal focusW = -1;

        void Consider(string tag, string label, decimal w)
        {
            score += w;
            if (w > focusW)
            {
                focusW = w;
                focus = label;
            }
        }

        foreach (var t in tags)
        {
            switch (t.Tag)
            {
                case MilkEventTaxonomy.Tags.Goals7pWatch:
                {
                    // 主看高球：盘口高 / 7+ SP 相对软
                    var w = 48m;
                    if (line is >= 3.5m) w += 18;
                    else if (line is >= 3.25m) w += 12;
                    else if (line is >= 3.0m) w += 8;
                    else if (line is >= 2.75m) w += 4;
                    if (jq7 is > 0 and <= 10m) w += 16;
                    else if (jq7 is > 0 and <= 15m) w += 10;
                    else if (jq7 is > 0 and <= SoftJq7) w += 5;
                    Consider(t.Tag, t.Label, w);
                    break;
                }
                case MilkEventTaxonomy.Tags.HfFlipWatch:
                {
                    // 胜/负、负/胜到 22，胜/平、负/平到 15。取落在线内的最短价。
                    var soft = new[]
                    {
                        SoftHalfPrice(bqWl, SoftHfFlip),
                        SoftHalfPrice(bqLw, SoftHfFlip),
                        SoftHalfPrice(bqWd, MilkEventTaxonomy.SoftHfDrawSpMax),
                        SoftHalfPrice(bqLd, MilkEventTaxonomy.SoftHfDrawSpMax)
                    }.Min();
                    var w = 50m;
                    if (soft <= 10m) w += 18;
                    else if (soft <= 14m) w += 12;
                    else if (soft < 99m) w += 6;
                    // 无高球盘时，翻转更容易成为主焦点
                    if (line is null || line < 2.75m) w += 8;
                    Consider(t.Tag, t.Label, w);
                    break;
                }
                case MilkEventTaxonomy.Tags.UpsetWatch:
                {
                    // 权重压低：仅极端大热才进 Focus
                    var w = 16m;
                    if (euH is <= 1.12m || euA is <= 1.12m) w += 10;
                    else if (euH is <= 1.18m || euA is <= 1.18m) w += 5;
                    if (exH is >= 90m || exA is >= 90m) w += 8;
                    Consider(t.Tag, t.Label, w);
                    break;
                }
                case MilkEventTaxonomy.Tags.ScoreOtherWatch:
                    Consider(t.Tag, t.Label, 8m);
                    break;
                default:
                    Consider(t.Tag, t.Label, 4m);
                    break;
            }
        }

        if (string.Equals(board, Boards.Jc, StringComparison.OrdinalIgnoreCase))
            score += 4;
        // 高球+翻转同场额外加分
        var hasGoals = tags.Any(t => t.Tag == MilkEventTaxonomy.Tags.Goals7pWatch);
        var hasFlip = tags.Any(t => t.Tag == MilkEventTaxonomy.Tags.HfFlipWatch);
        if (hasGoals && hasFlip) score += 14;
        else if (tags.Count >= 3) score += 8;
        else if (tags.Count >= 2) score += 4;

        return (Math.Round(score, 1), focus);
    }

    private static decimal SoftHalfPrice(decimal? sp, decimal cap)
        => sp is decimal v && v > 0m && v <= cap ? v : 99m;

    private const decimal SoftHfFlip = MilkEventTaxonomy.SoftHfFlipSpMax;
    private const decimal SoftJq7 = MilkEventTaxonomy.SoftJq7SpMax;
    // 6 席配额：高球为主、翻转必留、爆冷极少
    private const int MaxGoalsFocus = 3;
    private const int MaxFlipFocus = 2;
    private const int MaxUpsetFocus = 1;

    private static string FocusBucket(WatchItem i)
    {
        var f = i.Focus ?? "";
        if (f.Contains("翻转", StringComparison.Ordinal)) return "flip";
        if (f.Contains("高球", StringComparison.Ordinal)) return "goals";
        if (f.Contains("爆冷", StringComparison.Ordinal)) return "upset";
        return "other";
    }

    /// <summary>精选 6 场：高球≤3、翻转≤2、爆冷≤1，避免整表一种味道。</summary>
    private static List<WatchItem> PickDiversified(List<WatchItem> pool, int limit)
    {
        var ordered = pool
            .OrderByDescending(i => i.Score)
            .ThenBy(i => i.MatchTime ?? DateTime.MaxValue)
            .ToList();
        var picked = new List<WatchItem>();
        var goalsN = 0;
        var flipN = 0;
        var upsetN = 0;

        bool TryAdd(WatchItem it, bool enforceQuota)
        {
            if (picked.Contains(it)) return false;
            if (picked.Count >= limit) return false;
            var b = FocusBucket(it);
            if (enforceQuota)
            {
                if (b == "goals" && goalsN >= MaxGoalsFocus) return false;
                if (b == "flip" && flipN >= MaxFlipFocus) return false;
                if (b == "upset" && upsetN >= MaxUpsetFocus) return false;
            }
            picked.Add(it);
            if (b == "goals") goalsN++;
            else if (b == "flip") flipN++;
            else if (b == "upset") upsetN++;
            return true;
        }

        // 1) 先尽量各拿满：翻转 → 高球 → 其它非爆冷
        foreach (var it in ordered.Where(i => FocusBucket(i) == "flip"))
            TryAdd(it, enforceQuota: true);
        foreach (var it in ordered.Where(i => FocusBucket(i) == "goals"))
            TryAdd(it, enforceQuota: true);
        foreach (var it in ordered.Where(i => FocusBucket(i) is "other"))
            TryAdd(it, enforceQuota: true);
        foreach (var it in ordered.Where(i => FocusBucket(i) == "upset"))
            TryAdd(it, enforceQuota: true);

        // 2) 仍空则补：优先其它/翻转，高球不超过配额除非别无选择
        foreach (var it in ordered.Where(i => FocusBucket(i) != "goals"))
            TryAdd(it, enforceQuota: false);
        foreach (var it in ordered)
            TryAdd(it, enforceQuota: false);

        return picked
            .OrderByDescending(i => i.Score)
            .ThenBy(i => i.MatchTime ?? DateTime.MaxValue)
            .ToList();
    }

    private static List<WatchItem> DedupSameMatch(List<WatchItem> items)
    {
        var best = new Dictionary<string, WatchItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in items)
        {
            var key = it.MatchId is long id
                ? "id:" + id
                : "name:" + (it.Home ?? "") + "|" + (it.Away ?? "");
            if (!best.TryGetValue(key, out var prev))
            {
                best[key] = it;
                continue;
            }
            // 同分时优先竞彩
            var preferNew = it.Score > prev.Score
                || (it.Score == prev.Score
                    && string.Equals(it.Board, Boards.Jc, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(prev.Board, Boards.Jc, StringComparison.OrdinalIgnoreCase));
            if (preferNew) best[key] = it;
        }
        return best.Values.ToList();
    }

    private static string NormalizeScope(string? scope)
    {
        var s = (scope ?? "all").Trim().ToLowerInvariant();
        return s is "jc" or "card" or "all" ? s : "all";
    }

    private static decimal? Pick(Dictionary<string, decimal> map, string play, params string[] codes)
    {
        foreach (var code in codes)
        {
            if (map.TryGetValue($"{play}|{code}", out var o)) return o;
        }
        return null;
    }

    private static decimal? PickContains(Dictionary<string, decimal> map, string play, params string[] needles)
    {
        foreach (var kv in map)
        {
            if (!kv.Key.StartsWith(play + "|", StringComparison.OrdinalIgnoreCase)) continue;
            var code = kv.Key[(play.Length + 1)..];
            foreach (var n in needles)
            {
                if (code.Contains(n, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            }
        }
        return null;
    }
}
