namespace AiFootBall.Core.Models;

/// <summary>完场比分，用来估计球队进球和失球。</summary>
public sealed class ScoreSample
{
    public string? LeagueName { get; init; }
    public string HomeTeam { get; init; } = "";
    public string AwayTeam { get; init; } = "";
    public int HomeScore { get; init; }
    public int AwayScore { get; init; }
}

/// <summary>
/// 比分：球队进球、失球做攻防，Dixon-Coles 修正 0-0、1-0、0-1、1-1，再按大小球盘口缩放。
/// 主受让时不抬净胜球。主让时，只有净胜能盖住让球、且概率还够看，才用大比分。
/// </summary>
public sealed class ScorelineModel
{
    private const double PriorGames = 8;
    private const int MaxGoals = 6;

    private readonly double _globalHome;
    private readonly double _globalAway;
    private readonly Dictionary<string, (double HomeSum, double AwaySum, int N)> _leagues;
    private readonly Dictionary<string, (double For, double Against, int Games)> _teams;
    private readonly double _rho;

    private ScorelineModel(
        bool ready,
        double globalHome,
        double globalAway,
        Dictionary<string, (double HomeSum, double AwaySum, int N)> leagues,
        Dictionary<string, (double For, double Against, int Games)> teams,
        double rho)
    {
        Ready = ready;
        _globalHome = globalHome;
        _globalAway = globalAway;
        _leagues = leagues;
        _teams = teams;
        _rho = rho;
    }

    public static ScorelineModel Empty { get; } = new(false, 1.45, 1.15, new(), new(), -0.1);

    public bool Ready { get; }

    /// <summary>主客进球期望。有大小球盘时会把合计缩放到盘口的 0.65–1.55 倍。</summary>
    public (double Home, double Away)? ExpectedGoals(string? league, string home, string away, decimal? ouLine)
    {
        if (!Ready) return null;
        return Lambdas(league, home, away, ouLine);
    }

    public static ScorelineModel Fit(IReadOnlyList<ScoreSample> samples)
    {
        var usable = samples.Where(s =>
            !string.IsNullOrWhiteSpace(s.HomeTeam)
            && !string.IsNullOrWhiteSpace(s.AwayTeam)
            && s.HomeScore >= 0 && s.AwayScore >= 0
            && s.HomeScore <= 12 && s.AwayScore <= 12).ToList();
        if (usable.Count < 30) return Empty;

        double homeSum = 0, awaySum = 0;
        var leagues = new Dictionary<string, (double HomeSum, double AwaySum, int N)>(StringComparer.Ordinal);
        var teams = new Dictionary<string, (double For, double Against, int Games)>(StringComparer.Ordinal);
        foreach (var s in usable)
        {
            homeSum += s.HomeScore;
            awaySum += s.AwayScore;
            var league = s.LeagueName?.Trim() ?? "";
            if (league.Length > 0)
            {
                leagues.TryGetValue(league, out var lg);
                lg.HomeSum += s.HomeScore;
                lg.AwaySum += s.AwayScore;
                lg.N++;
                leagues[league] = lg;
            }
            AddTeam(teams, league, s.HomeTeam, s.HomeScore, s.AwayScore);
            AddTeam(teams, league, s.AwayTeam, s.AwayScore, s.HomeScore);
        }

        var n = usable.Count;
        var globalHome = homeSum / n;
        var globalAway = awaySum / n;
        var draft = new ScorelineModel(true, globalHome, globalAway, leagues, teams, -0.1);
        var rho = draft.FitRho(usable);
        return new ScorelineModel(true, globalHome, globalAway, leagues, teams, rho);
    }

    public List<(int Home, int Away)> Top(
        string? league, string home, string away, char direction,
        decimal? ouLine, decimal? handicap, int take = 2, int minMargin = 1)
    {
        if (!Ready || direction is not ('3' or '1' or '0')) return new();
        var (lh, la) = Lambdas(league, home, away, ouLine);
        var margin = Math.Max(1, minMargin);
        var cells = new List<(int Home, int Away, double P)>();
        for (var h = 0; h <= MaxGoals; h++)
        {
            for (var a = 0; a <= MaxGoals; a++)
            {
                var keep = direction switch
                {
                    '3' => h - a >= margin,
                    '0' => a - h >= margin,
                    _ => h == a
                };
                if (!keep) continue;
                cells.Add((h, a, Tau(h, a, lh, la) * Poisson(h, lh) * Poisson(a, la)));
            }
        }
        if (cells.Count == 0) return new();

        var pool = cells;
        if (handicap is < 0 && direction == '3')
            pool = PreferCover(cells, handicap.Value, homeSide: true);
        else if (handicap is > 0 && direction == '0')
            pool = PreferCover(cells, handicap.Value, homeSide: false);

        return pool
            .OrderByDescending(c => c.P)
            .Take(take)
            .Select(c => (c.Home, c.Away))
            .ToList();
    }

    private static List<(int Home, int Away, double P)> PreferCover(
        List<(int Home, int Away, double P)> cells, decimal handicap, bool homeSide)
    {
        var need = Math.Abs((int)Math.Round(handicap)) + 1;
        var best = cells.Max(c => c.P);
        var cover = cells.Where(c =>
        {
            var diff = homeSide ? c.Home - c.Away : c.Away - c.Home;
            return diff >= need;
        }).ToList();
        if (cover.Count > 0 && cover.Max(c => c.P) >= best * 0.55)
            return cover;
        return cells;
    }

    private (double Home, double Away) Lambdas(string? league, string home, string away, decimal? ouLine)
    {
        var (lgHome, lgAway) = LeagueAvg(league);
        var teamAvg = Math.Max(0.4, (lgHome + lgAway) / 2);
        var h = TeamRate(league, home, teamAvg);
        var a = TeamRate(league, away, teamAvg);
        var lh = lgHome * (h.Attack / teamAvg) * (a.Defense / teamAvg);
        var la = lgAway * (a.Attack / teamAvg) * (h.Defense / teamAvg);
        lh = Math.Clamp(lh, 0.15, 4.5);
        la = Math.Clamp(la, 0.15, 4.5);
        if (ouLine is > 0 && lh + la > 0)
        {
            var scale = Math.Clamp((double)ouLine.Value / (lh + la), 0.65, 1.55);
            lh *= scale;
            la *= scale;
        }
        return (lh, la);
    }

    private (double Home, double Away) LeagueAvg(string? league)
    {
        var name = league?.Trim();
        if (!string.IsNullOrEmpty(name) && _leagues.TryGetValue(name, out var lg) && lg.N >= 12)
        {
            var home = (lg.HomeSum + 30 * _globalHome) / (lg.N + 30);
            var away = (lg.AwaySum + 30 * _globalAway) / (lg.N + 30);
            return (home, away);
        }
        return (_globalHome, _globalAway);
    }

    private (double Attack, double Defense) TeamRate(string? league, string name, double prior)
    {
        var key = Key(league, name);
        if (!_teams.TryGetValue(key, out var t) || t.Games <= 0)
            return (prior, prior);
        var attack = (t.For + PriorGames * prior) / (t.Games + PriorGames);
        var defense = (t.Against + PriorGames * prior) / (t.Games + PriorGames);
        return (Math.Max(0.15, attack), Math.Max(0.15, defense));
    }

    private double FitRho(List<ScoreSample> samples)
    {
        var bestRho = -0.1;
        var best = double.NegativeInfinity;
        var step = samples.Count > 2500 ? samples.Count / 2500 : 1;
        for (var rho = -0.20; rho <= 0.05; rho += 0.01)
        {
            _ = rho;
            double ll = 0;
            var ok = true;
            for (var i = 0; i < samples.Count; i += step)
            {
                var s = samples[i];
                var (lh, la) = Lambdas(s.LeagueName, s.HomeTeam, s.AwayTeam, null);
                var tau = TauAt(s.HomeScore, s.AwayScore, lh, la, rho);
                if (tau <= 1e-6) { ok = false; break; }
                var p = tau * Poisson(Math.Min(s.HomeScore, MaxGoals), lh) * Poisson(Math.Min(s.AwayScore, MaxGoals), la);
                if (p <= 1e-12) p = 1e-12;
                ll += Math.Log(p);
            }
            if (ok && ll > best)
            {
                best = ll;
                bestRho = rho;
            }
        }
        return bestRho;
    }

    private double Tau(int x, int y, double lh, double la) => TauAt(x, y, lh, la, _rho);

    private static double TauAt(int x, int y, double lh, double la, double rho)
    {
        var t = (x, y) switch
        {
            (0, 0) => 1 - lh * la * rho,
            (1, 0) => 1 + la * rho,
            (0, 1) => 1 + lh * rho,
            (1, 1) => 1 - rho,
            _ => 1d
        };
        return t;
    }

    private static double Poisson(int k, double lambda)
    {
        if (k < 0) return 0;
        if (lambda < 1e-6) return k == 0 ? 1 : 0;
        var p = Math.Exp(-lambda);
        for (var i = 1; i <= k; i++) p *= lambda / i;
        return p;
    }

    private static void AddTeam(
        Dictionary<string, (double For, double Against, int Games)> teams,
        string league, string name, int scored, int conceded)
    {
        var key = Key(league, name);
        teams.TryGetValue(key, out var t);
        t.For += scored;
        t.Against += conceded;
        t.Games++;
        teams[key] = t;
    }

    private static string Key(string? league, string name) =>
        (league?.Trim() ?? "") + "\n" + name.Trim();
}
