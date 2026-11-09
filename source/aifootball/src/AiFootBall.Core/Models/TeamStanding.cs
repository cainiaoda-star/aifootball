namespace AiFootBall.Core.Models;

/// <summary>
/// 回算积分榜所需的一场比赛（最小字段集）。
/// 只读历史比分，不需要赔率，也不需要赛季字段——FbMatch.Season 全为 NULL，
/// 赛季只能靠「相邻比赛间隔」自己切。
/// </summary>
public sealed class StandingMatch
{
    public long MatchId { get; set; }
    public string LeagueName { get; set; } = "";
    public DateTime MatchTime { get; set; }
    public string HomeTeam { get; set; } = "";
    public string AwayTeam { get; set; } = "";
    public int HomeScore { get; set; }
    public int AwayScore { get; set; }
}

/// <summary>
/// 一场比赛的**赛前**联赛排名。
///
/// 为什么要赛前而不是赛后：赛后排名已经包含了这场球的结果，拿它做特征等于用未来信息。
/// Rank=0 表示当时不可用（赛季刚开打、该队已赛不足 3 场）——回测必须把 0 排除，不能当 1 用。
/// </summary>
public sealed class MatchPreRank
{
    public long MatchId { get; set; }
    public string LeagueName { get; set; } = "";
    public int SeasonNo { get; set; }
    public DateTime SeasonStart { get; set; }
    public int TeamCount { get; set; }

    public int HomeRankPre { get; set; }
    public int AwayRankPre { get; set; }
    public int HomePtsPre { get; set; }
    public int AwayPtsPre { get; set; }
    public int HomeGdPre { get; set; }
    public int AwayGdPre { get; set; }
    public int HomePlayedPre { get; set; }
    public int AwayPlayedPre { get; set; }

    public bool Usable => HomeRankPre > 0 && AwayRankPre > 0;

    /// <summary>主队排名分位：0 = 榜首，1 = 垫底。跨联赛只能比分位，不能比名次。</summary>
    public decimal HomeRankPct => TeamCount <= 1 ? 0.5m : (HomeRankPre - 1m) / (TeamCount - 1m);

    /// <summary>客队排名分位。</summary>
    public decimal AwayRankPct => TeamCount <= 1 ? 0.5m : (AwayRankPre - 1m) / (TeamCount - 1m);
}

/// <summary>
/// 一场比赛的「半场谁先进球 + 双方赛前排名」组合，复盘学习用。
/// 2026-11-08 用户提出：11-07 的 007/008（胜负）和 027（负平）都是排名靠后的一方先进球。
/// </summary>
public sealed class LeadOutcomeRow
{
    public string MatchNo { get; set; } = "";
    public string LeagueName { get; set; } = "";
    public string HomeTeam { get; set; } = "";
    public string AwayTeam { get; set; } = "";
    public int HomeRankPre { get; set; }
    public int AwayRankPre { get; set; }
    public int TeamCount { get; set; }
    public int HalfHome { get; set; }
    public int HalfAway { get; set; }
    public int HomeScore { get; set; }
    public int AwayScore { get; set; }
    public bool HasRank { get; set; }
    public bool HasHalf { get; set; }
    public bool Finished { get; set; }

    /// <summary>半场领先方：1=主队先进球，-1=客队先进球，0=半场平（判断不出谁先进球）。</summary>
    public int LeadSide => !HasHalf ? 0 : HalfHome > HalfAway ? 1 : HalfHome < HalfAway ? -1 : 0;

    /// <summary>领先方是否最终赢球。</summary>
    public bool? LeadWon => !Finished || LeadSide == 0
        ? null
        : LeadSide == 1 ? HomeScore > AwayScore : AwayScore > HomeScore;

    /// <summary>领先方是否最终被追平（且没输）。</summary>
    public bool? LeadDrawn => !Finished || LeadSide == 0
        ? null
        : LeadSide == 1 ? HomeScore == AwayScore : AwayScore == HomeScore;

    /// <summary>领先方是否最终被逆转。</summary>
    public bool? LeadLost => !Finished || LeadSide == 0 ? null : LeadWon == false && LeadDrawn == false;

    /// <summary>领先方的赛前排名（1 起）。</summary>
    public int LeadRank => LeadSide == 1 ? HomeRankPre : AwayRankPre;

    /// <summary>领先方的排名分位：0=榜首，1=垫底。</summary>
    public decimal LeadRankPct => TeamCount <= 1 ? 0.5m : (LeadRank - 1m) / (TeamCount - 1m);

    /// <summary>
    /// 领先方是不是「排名靠后的一方」——只要求名次比对手低，不额外要求分位在后半区。
    ///
    /// 2026-11-08 用户原话是「排名靠后的先进球」，三场里周六007 朴次茅斯是 10/24 打 5/24，
    /// 名次确实更低，但分位 0.39 属中上游。若按「分位≥50%」判，这场会被漏掉——
    /// 与用户的观察不符。所以判定只认名次高低，分位只用于查基线表。
    /// </summary>
    public bool LeadIsUnderdog => HasRank && LeadSide != 0
        && (LeadSide == 1 ? HomeRankPre > AwayRankPre : AwayRankPre > HomeRankPre);
}

/// <summary>按日期保存下来的当期积分榜一行。</summary>
public sealed class StandingRow
{
    public DateTime AsOfDate { get; set; }
    public string LeagueName { get; set; } = "";
    public DateTime SeasonStart { get; set; }
    public string TeamName { get; set; } = "";
    public int Rank { get; set; }
    public int Played { get; set; }
    public int Points { get; set; }
    public int GoalsFor { get; set; }
    public int GoalsAgainst { get; set; }
    public int TeamCount { get; set; }
}

/// <summary>
/// 积分榜回算。纯函数，不碰数据库，方便单测。
///
/// 两条硬约束（都是踩过的坑）：
/// 1. **同一开赛时间的比赛必须用同一份赛前状态**。一场一场地更新会让 20:30 同时开球的场次
///    互相"看到"对方赛后的积分，排名就假了。所以按 MatchTime 分批：先全部记排名，再全部更新。
/// 2. **赛季只能靠间隔切**。FbMatch.Season 是 NULL，跨赛季不切断，榜首会带着上赛季的积分跑一整年。
///    阈值 60 天：主流联赛夏歇 60~90 天，冬歇一般 20~40 天，能分开，又不至于把冬歇切成两个赛季。
/// </summary>
public static class StandingMath
{
    /// <summary>相邻比赛间隔达到该天数即视为新赛季。</summary>
    public const int SeasonGapDays = 60;

    /// <summary>两队都已赛满该场次才输出排名；赛季前几轮的"排名"是名字排序，没有信息。</summary>
    public const int MinPlayedForRank = 3;

    private struct Stat
    {
        public string Team;
        public int Played;
        public int Pts;
        public int Gf;
        public int Ga;
        public int Gd => Gf - Ga;

        public void Add(int gf, int ga)
        {
            Played++;
            Gf += gf;
            Ga += ga;
            Pts += gf > ga ? 3 : gf == ga ? 1 : 0;
        }
    }

    /// <summary>按联赛分组回算每场比赛的赛前排名。返回数量与输入一致（不可用的 Rank 记 0）。</summary>
    public static List<MatchPreRank> Compute(IEnumerable<StandingMatch> matches)
    {
        var output = new List<MatchPreRank>();

        foreach (var league in matches
                     .Where(m => !string.IsNullOrWhiteSpace(m.LeagueName)
                                 && !string.IsNullOrWhiteSpace(m.HomeTeam)
                                 && !string.IsNullOrWhiteSpace(m.AwayTeam))
                     .GroupBy(m => m.LeagueName))
        {
            var ordered = league
                .OrderBy(m => m.MatchTime)
                .ThenBy(m => m.MatchId)
                // 队名归一到规范名（2026-11-08）。同一队两个写法时，各算一份积分 = 积分榜上凭空
                // 多出一支「只有半个赛季战绩」的球队，两边的排名都会偏低。
                // 例：瑞典超 2025 赛季的「索尔纳」与「AIK索尔纳」，各 20 场上下，分开算都比真实名次低。
                .Select(m => new StandingMatch
                {
                    MatchId = m.MatchId,
                    LeagueName = m.LeagueName,
                    MatchTime = m.MatchTime,
                    HomeTeam = TeamAliases.Canonical(m.HomeTeam),
                    AwayTeam = TeamAliases.Canonical(m.AwayTeam),
                    HomeScore = m.HomeScore,
                    AwayScore = m.AwayScore
                })
                .ToList();
            if (ordered.Count == 0) continue;

            // 1. 切赛季
            var (seasonOf, seasonStart) = SplitSeasons(ordered);

            // 2. 每个赛季的参赛队（用全赛季出现过的队，避免赛季初少几队导致分位漂移）
            var teamsBySeason = new Dictionary<int, List<string>>();
            for (var i = 0; i < ordered.Count; i++)
            {
                var no = seasonOf[i];
                if (!teamsBySeason.TryGetValue(no, out var list))
                    teamsBySeason[no] = list = new List<string>();
                if (!list.Contains(ordered[i].HomeTeam)) list.Add(ordered[i].HomeTeam);
                if (!list.Contains(ordered[i].AwayTeam)) list.Add(ordered[i].AwayTeam);
            }
            foreach (var list in teamsBySeason.Values) list.Sort(StringComparer.Ordinal);

            // 3. 逐个赛季推进
            foreach (var season in teamsBySeason.Keys.OrderBy(k => k))
            {
                var teams = teamsBySeason[season];
                var table = new Dictionary<string, Stat>(StringComparer.Ordinal);
                foreach (var t in teams) table[t] = new Stat { Team = t };

                var rows = new List<int>();
                for (var i = 0; i < ordered.Count; i++)
                    if (seasonOf[i] == season) rows.Add(i);

                foreach (var batch in rows.GroupBy(i => ordered[i].MatchTime).OrderBy(g => g.Key))
                {
                    var rank = Snapshot(table, teams);

                    foreach (var i in batch)
                    {
                        var m = ordered[i];
                        var h = table[m.HomeTeam];
                        var a = table[m.AwayTeam];
                        var usableHome = h.Played >= MinPlayedForRank;
                        var usableAway = a.Played >= MinPlayedForRank;

                        output.Add(new MatchPreRank
                        {
                            MatchId = m.MatchId,
                            LeagueName = m.LeagueName,
                            SeasonNo = season,
                            SeasonStart = seasonStart[i].Date,
                            TeamCount = teams.Count,
                            HomeRankPre = usableHome ? rank.GetValueOrDefault(m.HomeTeam) : 0,
                            AwayRankPre = usableAway ? rank.GetValueOrDefault(m.AwayTeam) : 0,
                            HomePtsPre = h.Pts,
                            AwayPtsPre = a.Pts,
                            HomeGdPre = h.Gd,
                            AwayGdPre = a.Gd,
                            HomePlayedPre = h.Played,
                            AwayPlayedPre = a.Played,
                        });
                    }

                    // 赛前状态全部记录完，再统一结算这一批，避免同批互看
                    foreach (var i in batch)
                    {
                        var m = ordered[i];
                        var h = table[m.HomeTeam];
                        h.Add(m.HomeScore, m.AwayScore);
                        table[m.HomeTeam] = h;
                        var a = table[m.AwayTeam];
                        a.Add(m.AwayScore, m.HomeScore);
                        table[m.AwayTeam] = a;
                    }
                }
            }
        }

        return output;
    }

    /// <summary>按积分→净胜球→进球→队名 排一份名次表。</summary>
    private static Dictionary<string, int> Snapshot(Dictionary<string, Stat> table, List<string> teams)
    {
        var ordered = teams
            .Select(t => table[t])
            .OrderByDescending(s => s.Pts)
            .ThenByDescending(s => s.Gd)
            .ThenByDescending(s => s.Gf)
            .ThenBy(s => s.Team, StringComparer.Ordinal)
            .ToList();

        var rank = new Dictionary<string, int>(teams.Count, StringComparer.Ordinal);
        for (var i = 0; i < ordered.Count; i++) rank[ordered[i].Team] = i + 1;
        return rank;
    }

    /// <summary>取某一时点的完整积分榜（用于每日快照）。</summary>
    public static List<MatchPreRank> ComputeAsOf(IEnumerable<StandingMatch> matches, DateTime asOf)
        => Compute(matches.Where(m => m.MatchTime <= asOf));

    /// <summary>
    /// 生成 asOf 时点的积分榜（只取最近一个赛季）。这就是「当期排名」快照。
    /// </summary>
    public static List<StandingRow> BuildTable(IEnumerable<StandingMatch> matches, DateTime asOf)
    {
        var rows = new List<StandingRow>();
        foreach (var league in matches
                     .Where(m => !string.IsNullOrWhiteSpace(m.LeagueName) && m.MatchTime <= asOf)
                     .GroupBy(m => m.LeagueName))
        {
            var ordered = league
                .OrderBy(m => m.MatchTime)
                .ThenBy(m => m.MatchId)
                .Select(m => new StandingMatch
                {
                    MatchId = m.MatchId,
                    LeagueName = m.LeagueName,
                    MatchTime = m.MatchTime,
                    HomeTeam = TeamAliases.Canonical(m.HomeTeam),
                    AwayTeam = TeamAliases.Canonical(m.AwayTeam),
                    HomeScore = m.HomeScore,
                    AwayScore = m.AwayScore
                })
                .ToList();
            if (ordered.Count == 0) continue;

            var (seasonOf, seasonStart) = SplitSeasons(ordered);
            var lastSeason = seasonOf[^1];
            var startAt = seasonStart[^1];

            var idx = new List<int>();
            for (var i = 0; i < ordered.Count; i++)
                if (seasonOf[i] == lastSeason) idx.Add(i);

            var teams = new List<string>();
            foreach (var i in idx)
            {
                if (!teams.Contains(ordered[i].HomeTeam)) teams.Add(ordered[i].HomeTeam);
                if (!teams.Contains(ordered[i].AwayTeam)) teams.Add(ordered[i].AwayTeam);
            }
            teams.Sort(StringComparer.Ordinal);
            if (teams.Count == 0) continue;

            var table = new Dictionary<string, Stat>(StringComparer.Ordinal);
            foreach (var t in teams) table[t] = new Stat { Team = t };
            foreach (var i in idx)
            {
                var m = ordered[i];
                var h = table[m.HomeTeam];
                h.Add(m.HomeScore, m.AwayScore);
                table[m.HomeTeam] = h;
                var a = table[m.AwayTeam];
                a.Add(m.AwayScore, m.HomeScore);
                table[m.AwayTeam] = a;
            }

            var rank = Snapshot(table, teams);
            foreach (var t in teams)
            {
                var s = table[t];
                if (s.Played == 0) continue;
                rows.Add(new StandingRow
                {
                    AsOfDate = asOf.Date,
                    LeagueName = league.Key,
                    SeasonStart = startAt.Date,
                    TeamName = t,
                    Rank = rank[t],
                    Played = s.Played,
                    Points = s.Pts,
                    GoalsFor = s.Gf,
                    GoalsAgainst = s.Ga,
                    TeamCount = teams.Count,
                });
            }
        }
        return rows;
    }

    /// <summary>按「相邻比赛间隔 ≥ SeasonGapDays」切赛季，返回每场的赛季号与赛季首日。</summary>
    private static (List<int> SeasonOf, List<DateTime> SeasonStart) SplitSeasons(List<StandingMatch> ordered)
    {
        var seasonOf = new List<int>(ordered.Count);
        var seasonStart = new List<DateTime>(ordered.Count);
        var seasonNo = 1;
        var start = ordered[0].MatchTime;
        for (var i = 0; i < ordered.Count; i++)
        {
            if (i > 0 && (ordered[i].MatchTime - ordered[i - 1].MatchTime).TotalDays >= SeasonGapDays)
            {
                seasonNo++;
                start = ordered[i].MatchTime;
            }
            seasonOf.Add(seasonNo);
            seasonStart.Add(start);
        }
        return (seasonOf, seasonStart);
    }
}
