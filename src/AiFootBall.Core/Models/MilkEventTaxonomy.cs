namespace AiFootBall.Core.Models;

/// <summary>
/// 「大奶」尾部事件口径：完场复盘标签 + 赛前观察标签。
/// 与主推（胜平负规则）独立，不伪装成必中。
/// </summary>
public static class MilkEventTaxonomy
{
    public static class Tags
    {
        public const string Goals7p = "goals7p";
        public const string HfWinLose = "hf_win_lose";
        public const string HfLoseWin = "hf_lose_win";
        public const string ScoreOtherHome = "score_other_home";
        public const string ScoreOtherAway = "score_other_away";
        public const string UpsetHome = "upset_home";
        public const string UpsetAway = "upset_away";

        // 赛前观察（非赛果）
        public const string Goals7pWatch = "goals7p_watch";
        public const string HfFlipWatch = "hf_flip_watch";
        public const string UpsetWatch = "upset_watch";
        public const string ScoreOtherWatch = "score_other_watch";
    }

    public static readonly IReadOnlyDictionary<string, string> TagLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Tags.Goals7p] = "7+",
        [Tags.HfWinLose] = "胜/负",
        [Tags.HfLoseWin] = "负/胜",
        [Tags.ScoreOtherHome] = "胜其它",
        [Tags.ScoreOtherAway] = "负其它",
        [Tags.UpsetHome] = "主热爆冷",
        [Tags.UpsetAway] = "客热爆冷",
        [Tags.Goals7pWatch] = "高球观察",
        [Tags.HfFlipWatch] = "半全场翻转观察",
        [Tags.UpsetWatch] = "爆冷观察",
        [Tags.ScoreOtherWatch] = "非常规比分观察",
    };

    /// <summary>竞彩比分标准格（落在格内不算胜/负/平其它）。</summary>
    public static readonly HashSet<string> JcExactHomeScores = new(StringComparer.Ordinal)
    {
        "1:0", "2:0", "2:1", "3:0", "3:1", "3:2", "4:0", "4:1", "4:2", "5:0", "5:1", "5:2"
    };

    public static readonly HashSet<string> JcExactDrawScores = new(StringComparer.Ordinal)
    {
        "0:0", "1:1", "2:2", "3:3"
    };

    public static readonly HashSet<string> JcExactAwayScores = new(StringComparer.Ordinal)
    {
        "0:1", "0:2", "1:2", "0:3", "1:3", "2:3", "0:4", "1:4", "2:4", "0:5", "1:5", "2:5"
    };

    public const decimal UpsetEuMax = 1.50m;
    public const decimal UpsetExMin = 70m;
    // 赛前爆冷观察：只盯极端大热，避免列表被「爆冷」刷屏（精选优先看 7+ / 半全场翻转）
    public const decimal UpsetWatchEuMax = 1.18m;
    public const decimal UpsetWatchExMin = 88m;
    public const decimal HighGoalsLineMin = 2.75m;
    public const decimal SoftJq7SpMax = 25m;
    public const decimal SoftHfFlipSpMax = 22m;

    public sealed class MilkTagHit
    {
        public string Tag { get; init; } = "";
        public string Label { get; init; } = "";
        public string Note { get; init; } = "";
    }

    public sealed class FinishedInput
    {
        public int Home { get; init; }
        public int Away { get; init; }
        public int? HalfHome { get; init; }
        public int? HalfAway { get; init; }
        /// <summary>赛前/终盘欧赔主胜（CompanyId=0）。</summary>
        public decimal? EuHome { get; init; }
        public decimal? EuAway { get; init; }
        /// <summary>必发主/客成交占比 %。</summary>
        public decimal? ExHome { get; init; }
        public decimal? ExAway { get; init; }
    }

    public sealed class WatchInput
    {
        public decimal? EuHome { get; init; }
        public decimal? EuAway { get; init; }
        public decimal? ExHome { get; init; }
        public decimal? ExAway { get; init; }
        public decimal? TotalLine { get; init; }
        public decimal? Jq7Sp { get; init; }
        public decimal? BqWinLoseSp { get; init; }
        public decimal? BqLoseWinSp { get; init; }
        public decimal? BfOtherHomeSp { get; init; }
        public decimal? BfOtherAwaySp { get; init; }
    }

    public static string ScoreKey(int home, int away) => $"{home}:{away}";

    public static bool IsJcScoreOtherHome(int home, int away) =>
        home > away && !JcExactHomeScores.Contains(ScoreKey(home, away));

    public static bool IsJcScoreOtherAway(int home, int away) =>
        home < away && !JcExactAwayScores.Contains(ScoreKey(home, away));

    public static bool IsJcScoreOtherDraw(int home, int away) =>
        home == away && !JcExactDrawScores.Contains(ScoreKey(home, away));

    /// <summary>完场复盘：按比分 / 半全场 / 冷热打标签。</summary>
    public static List<MilkTagHit> ClassifyFinished(FinishedInput m)
    {
        var hits = new List<MilkTagHit>();
        var goals = m.Home + m.Away;
        var score = ScoreKey(m.Home, m.Away);

        if (goals >= 7)
        {
            hits.Add(Hit(Tags.Goals7p, $"{score} 共 {goals} 球"));
        }

        if (m.HalfHome is int hh && m.HalfAway is int ha)
        {
            var halfHomeWin = hh > ha;
            var halfAwayWin = hh < ha;
            var fullHomeWin = m.Home > m.Away;
            var fullAwayWin = m.Home < m.Away;
            if (halfHomeWin && fullAwayWin)
                hits.Add(Hit(Tags.HfWinLose, $"半场 {hh}:{ha} → 全场 {score}"));
            if (halfAwayWin && fullHomeWin)
                hits.Add(Hit(Tags.HfLoseWin, $"半场 {hh}:{ha} → 全场 {score}"));
        }

        if (IsJcScoreOtherHome(m.Home, m.Away))
            hits.Add(Hit(Tags.ScoreOtherHome, $"{score}（标准格外主胜）"));
        if (IsJcScoreOtherAway(m.Home, m.Away))
            hits.Add(Hit(Tags.ScoreOtherAway, $"{score}（标准格外客胜）"));

        var margin = Math.Abs(m.Home - m.Away);
        var homeHot = (m.EuHome is <= UpsetEuMax) || (m.ExHome is >= UpsetExMin);
        var awayHot = (m.EuAway is <= UpsetEuMax) || (m.ExAway is >= UpsetExMin);
        if (homeHot && m.Home < m.Away)
        {
            var note = BuildUpsetNote("主热", m.EuHome, m.ExHome, score, margin);
            hits.Add(Hit(Tags.UpsetHome, note));
        }
        if (awayHot && m.Home > m.Away)
        {
            var note = BuildUpsetNote("客热", m.EuAway, m.ExAway, score, margin);
            hits.Add(Hit(Tags.UpsetAway, note));
        }

        return hits;
    }

    /// <summary>赛前观察：仅用盘口 / SP / 必发打候选标签。</summary>
    public static List<MilkTagHit> ClassifyWatch(WatchInput m)
    {
        var hits = new List<MilkTagHit>();

        if ((m.TotalLine is >= HighGoalsLineMin) || (m.Jq7Sp is > 0 and <= SoftJq7SpMax))
        {
            var parts = new List<string>();
            if (m.TotalLine is decimal tl) parts.Add($"大小球 {tl:0.##}");
            if (m.Jq7Sp is decimal jq) parts.Add($"7+ SP {jq:0.##}");
            hits.Add(Hit(Tags.Goals7pWatch, string.Join("；", parts)));
        }

        var flipNotes = new List<string>();
        if (m.BqWinLoseSp is > 0 and <= SoftHfFlipSpMax)
            flipNotes.Add($"胜/负 SP {m.BqWinLoseSp:0.##}");
        if (m.BqLoseWinSp is > 0 and <= SoftHfFlipSpMax)
            flipNotes.Add($"负/胜 SP {m.BqLoseWinSp:0.##}");
        if (flipNotes.Count > 0)
            hits.Add(Hit(Tags.HfFlipWatch, string.Join("；", flipNotes)));

        if ((m.EuHome is <= UpsetWatchEuMax) || (m.EuAway is <= UpsetWatchEuMax)
            || (m.ExHome is >= UpsetWatchExMin) || (m.ExAway is >= UpsetWatchExMin))
        {
            var parts = new List<string>();
            if (m.EuHome is <= UpsetWatchEuMax) parts.Add($"主胜欧赔 {m.EuHome:0.00}");
            if (m.EuAway is <= UpsetWatchEuMax) parts.Add($"客胜欧赔 {m.EuAway:0.00}");
            if (m.ExHome is >= UpsetWatchExMin) parts.Add($"必发主 {m.ExHome:0.#}%");
            if (m.ExAway is >= UpsetWatchExMin) parts.Add($"必发客 {m.ExAway:0.#}%");
            hits.Add(Hit(Tags.UpsetWatch, string.Join("；", parts)));
        }

        var otherNotes = new List<string>();
        if (m.BfOtherHomeSp is > 0) otherNotes.Add($"胜其它 SP {m.BfOtherHomeSp:0.##}");
        if (m.BfOtherAwaySp is > 0) otherNotes.Add($"负其它 SP {m.BfOtherAwaySp:0.##}");
        if (otherNotes.Count > 0
            && ((m.TotalLine is >= 2.5m) || (m.EuHome is <= 1.40m) || (m.EuAway is <= 1.40m)))
        {
            hits.Add(Hit(Tags.ScoreOtherWatch, string.Join("；", otherNotes)));
        }

        return hits;
    }

    private static MilkTagHit Hit(string tag, string note) => new()
    {
        Tag = tag,
        Label = TagLabels.TryGetValue(tag, out var lab) ? lab : tag,
        Note = note
    };

    private static string BuildUpsetNote(string side, decimal? eu, decimal? ex, string score, int margin)
    {
        var parts = new List<string> { side };
        if (eu is decimal e) parts.Add($"欧赔 {e:0.00}");
        if (ex is decimal x) parts.Add($"必发 {x:0.#}%");
        parts.Add($"赛果 {score}");
        if (margin >= 2) parts.Add($"净胜{margin}");
        return string.Join("；", parts);
    }
}
