using System.Globalization;
using System.Text.RegularExpressions;
using AiFootBall.Core.Models;
using AiFootBall.Data;

namespace AiFootBall.Sources;

/// <summary>北单一场的胜平负、比分、半全场 SP。</summary>
public sealed class BdMarketOdds
{
    public decimal? Home { get; set; }
    public decimal? Draw { get; set; }
    public decimal? Away { get; set; }
    public Dictionary<string, decimal> Scores { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, decimal> HalfFull { get; } = new(StringComparer.Ordinal);
}

/// <summary>把推荐方向、对应半全场和最终比分的赔率写成复盘上的短句。</summary>
public static class ReviewOdds
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Dictionary<long, BdMarketOdds> _live = new();
    private static DateTime _liveAt = DateTime.MinValue;

    public static async Task ApplyAsync(
        IReadOnlyList<FinishedSummaryItem> items,
        JcOddsRepository jc,
        BettingCardSource cards,
        CancellationToken ct)
    {
        var ids = items.Select(i => i.MatchId).Distinct().ToArray();
        var stored = await jc.GetLatestAsync(ids, ct);
        var live = await LiveBdAsync(cards, ct);
        var changed = new List<JcOddsHistoryPoint>();

        foreach (var item in items)
        {
            var odds = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var p in stored)
            {
                if (p.MatchId != item.MatchId || BogusScorePrice(p.PlayType, p.OptionCode, p.Odds)) continue;
                Index(odds, p.PlayType, p.OptionCode, p.Odds);
                Index(odds, p.PlayType, p.OptionName, p.Odds);
            }

            if (item.Board == Boards.Bd && live.TryGetValue(item.MatchId, out var bd))
            {
                Take(item, odds, changed, stored, JcPlayTypes.Spf, "3", "胜", bd.Home);
                Take(item, odds, changed, stored, JcPlayTypes.Spf, "1", "平", bd.Draw);
                Take(item, odds, changed, stored, JcPlayTypes.Spf, "0", "负", bd.Away);
                foreach (var (code, price) in bd.Scores)
                    Take(item, odds, changed, stored, JcPlayTypes.Bf, code, code, price);
                foreach (var (name, price) in bd.HalfFull)
                    Take(item, odds, changed, stored, JcPlayTypes.Bq, name, name, price);
            }

            item.OddsNotes = Build(item, odds);
        }

        if (changed.Count > 0)
            await jc.InsertChangedAsync(changed, ct);
    }

    public static List<string> Build(FinishedSummaryItem item, IReadOnlyDictionary<string, decimal> odds)
    {
        var notes = new List<string>();
        if (!item.IsCoverAll && item.RecResult is char side)
        {
            var name = SideName(side);
            if (name.Length > 0 && Find(odds, JcPlayTypes.Spf, name) is decimal sp)
                notes.Add($"{name}：{Fmt(sp)}");
        }

        foreach (var hf in HalfFulls(item))
        {
            if (Find(odds, JcPlayTypes.Bq, hf) is decimal price)
                notes.Add($"{hf}：{Fmt(price)}");
        }

        if (!string.IsNullOrWhiteSpace(item.HalfFull)
            && Find(odds, JcPlayTypes.Bq, item.HalfFull) is decimal actualHalf
            && notes.All(n => !n.StartsWith(item.HalfFull + "：", StringComparison.Ordinal)))
            notes.Add($"{item.HalfFull}：{Fmt(actualHalf)}");

        var finalScore = item.FullHome is int h && item.FullAway is int a ? $"{h}:{a}" : null;
        foreach (var score in new[] { item.RecScore1, item.RecScore2 })
        {
            if (string.IsNullOrWhiteSpace(score)) continue;
            var key = Norm(score);
            if (key == finalScore) continue;
            if (Find(odds, JcPlayTypes.Bf, key) is decimal price)
                notes.Add($"比分 {key}：{Fmt(price)}");
        }

        if (finalScore is not null && Find(odds, JcPlayTypes.Bf, finalScore) is decimal finalPrice)
            notes.Add($"最终比分 {finalScore}：{Fmt(finalPrice)}");

        return notes;
    }

    private static async Task<Dictionary<long, BdMarketOdds>> LiveBdAsync(BettingCardSource cards, CancellationToken ct)
    {
        if (_live.Count > 0 && DateTime.UtcNow - _liveAt < TimeSpan.FromMinutes(10))
            return _live;

        await Gate.WaitAsync(ct);
        try
        {
            if (_live.Count > 0 && DateTime.UtcNow - _liveAt < TimeSpan.FromMinutes(10))
                return _live;
            var map = await cards.FetchBdMarketOddsAsync(ct);
            if (map.Count > 0)
            {
                _live = map;
                _liveAt = DateTime.UtcNow;
            }
            return map.Count > 0 ? map : _live;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void Take(
        FinishedSummaryItem item,
        Dictionary<string, decimal> odds,
        List<JcOddsHistoryPoint> changed,
        IReadOnlyList<JcOddsHistoryPoint> stored,
        string play,
        string code,
        string name,
        decimal? price)
    {
        if (price is not > 1 || BogusScorePrice(play, code, price.Value)) return;
        Index(odds, play, code, price.Value);
        Index(odds, play, name, price.Value);
        var same = stored.Any(p => p.MatchId == item.MatchId && p.PlayType == play && p.OptionCode == code && Math.Abs(p.Odds - price.Value) < 0.001m);
        if (!same)
        {
            changed.Add(new JcOddsHistoryPoint
            {
                MatchId = item.MatchId,
                MatchNo = item.MatchNo ?? "",
                PlayType = play,
                OptionCode = code,
                OptionName = name,
                Odds = price.Value
            });
        }
    }

    private static void Index(Dictionary<string, decimal> odds, string play, string? key, decimal price)
    {
        if (string.IsNullOrWhiteSpace(key) || price <= 1) return;
        odds[$"{play}|{Norm(key)}"] = price;
    }

    private static decimal? Find(IReadOnlyDictionary<string, decimal> odds, string play, string key) =>
        odds.TryGetValue($"{play}|{Norm(key)}", out var price) ? price : null;

    private static IEnumerable<string> HalfFulls(FinishedSummaryItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.RecHalfFull))
        {
            foreach (var part in item.RecHalfFull.Split('、', '，', ','))
            {
                var name = Norm(part);
                if (name.Contains('/')) yield return name;
            }
            yield break;
        }

        switch (item.RecResult)
        {
            case '3':
                yield return "胜/胜";
                yield return "平/胜";
                break;
            case '1':
                yield return "平/平";
                yield return "胜/平";
                break;
            case '0':
                yield return "负/负";
                yield return "平/负";
                break;
        }
    }

    internal static string Norm(string key)
    {
        key = key.Trim().Replace(" ", "").Replace("／", "/");
        var score = Regex.Match(key, @"^(\d+)[:\-](\d+)$");
        if (score.Success) return $"{score.Groups[1].Value}:{score.Groups[2].Value}";
        key = key.Replace("-", "/");
        if (Regex.IsMatch(key, @"^[310]/[310]$"))
            return SideName(key[0]) + "/" + SideName(key[2]);
        if (key.Length == 2 && IsSide(key[0]) && IsSide(key[1]))
            return $"{key[0]}/{key[1]}";
        if (key.Length == 1 && IsSide(key[0])) return SideName(key[0]);
        return key;
    }

    private static bool IsSide(char c) => c is '胜' or '平' or '负' or '3' or '1' or '0';

    private static string SideName(char c) => c switch
    {
        '3' or '胜' => "胜",
        '1' or '平' => "平",
        '0' or '负' => "负",
        _ => ""
    };

    private static string Fmt(decimal n) => n.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>比分页曾把「2:0」解析成 2.00。这种和比分前一位相同的整数报价丢掉。</summary>
    private static bool BogusScorePrice(string play, string? code, decimal price)
    {
        if (play != JcPlayTypes.Bf || string.IsNullOrWhiteSpace(code)) return false;
        var parts = code.Split(':');
        return parts.Length == 2
            && int.TryParse(parts[0], out var home)
            && price == home;
    }
}
