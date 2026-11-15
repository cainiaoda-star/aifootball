namespace AiFootBall.Core.Models;

/// <summary>
/// 大小球胜算。进球按泊松，盘口按亚盘结算：整数盘等于盘口是走盘，四分之一盘赢一半按一半计入。
/// </summary>
public static class TotalLineMath
{
    private const int MaxGoals = 12;

    public readonly record struct Split(decimal Over, decimal Under, decimal OverPush, decimal UnderPush);

    public static Split? Chances(double lambdaHome, double lambdaAway, decimal line)
    {
        if (line is <= 0.5m or >= 8m) return null;
        if (lambdaHome <= 0 || lambdaAway <= 0) return null;

        var mass = new double[MaxGoals * 2 + 1];
        double z = 0;
        for (var h = 0; h <= MaxGoals; h++)
        {
            var ph = Poisson(h, lambdaHome);
            for (var a = 0; a <= MaxGoals; a++)
            {
                var pr = ph * Poisson(a, lambdaAway);
                mass[h + a] += pr;
                z += pr;
            }
        }
        if (z <= 0) return null;

        double Pge(int n)
        {
            double s = 0;
            for (var i = Math.Max(0, n); i < mass.Length; i++) s += mass[i];
            return s / z;
        }
        double Ple(int n)
        {
            double s = 0;
            var last = Math.Min(n, mass.Length - 1);
            for (var i = 0; i <= last; i++) s += mass[i];
            return s / z;
        }
        double Pat(int n) => n >= 0 && n < mass.Length ? mass[n] / z : 0;

        var cents = (int)decimal.Round(line * 100m, 0, MidpointRounding.AwayFromZero);
        var frac = cents % 100;
        var n = cents / 100;
        double over, under, overPush, underPush;
        if (frac == 0)
        {
            over = Pge(n + 1);
            under = n == 0 ? 0 : Ple(n - 1);
            overPush = underPush = Pat(n);
        }
        else if (frac == 50)
        {
            over = Pge(n + 1);
            under = Ple(n);
            overPush = underPush = 0;
        }
        else if (frac == 25)
        {
            over = Pge(n + 1);
            under = n == 0 ? 0 : Ple(n - 1) + 0.5 * Pat(n);
            overPush = underPush = 0.5 * Pat(n);
        }
        else if (frac == 75)
        {
            over = Pge(n + 2) + 0.5 * Pat(n + 1);
            under = Ple(n);
            overPush = underPush = 0.5 * Pat(n + 1);
        }
        else return null;

        return new Split((decimal)over, (decimal)under, (decimal)overPush, (decimal)underPush);
    }

    /// <summary>
    /// 按亚盘结算完场进球。返回 赢、输、走盘、赢半、输半。
    /// 不是四分之一档，或方向不是大/小，返回 null。
    /// </summary>
    public static string? Grade(int goals, decimal line, string side)
    {
        if (side is not ("大" or "小")) return null;
        var cents = (int)decimal.Round(line * 100m, 0, MidpointRounding.AwayFromZero);
        if (cents <= 50 || cents >= 800) return null;
        var frac = cents % 100;
        var n = cents / 100;
        int over;
        if (frac == 0)
        {
            if (goals > n) over = 2;
            else if (goals < n) over = -2;
            else over = 0;
        }
        else if (frac == 50)
        {
            over = goals >= n + 1 ? 2 : -2;
        }
        else if (frac == 25)
        {
            if (goals >= n + 1) over = 2;
            else if (goals == n) over = -1;
            else over = -2;
        }
        else if (frac == 75)
        {
            if (goals >= n + 2) over = 2;
            else if (goals == n + 1) over = 1;
            else over = -2;
        }
        else return null;

        var view = side == "大" ? over : -over;
        return view switch
        {
            2 => "赢",
            1 => "赢半",
            0 => "走盘",
            -1 => "输半",
            _ => "输"
        };
    }

    public static void Apply(MatchRecommendation rec, ScorelineModel? scores)
    {
        rec.OuSide = null;
        rec.OuProbability = null;
        rec.OuPush = null;
        rec.OuNote = null;
        if (rec.TotalLine is not decimal line) return;

        var team = scores?.ExpectedGoals(rec.LeagueName, rec.HomeTeam, rec.AwayTeam, line);
        var fromTeams = team is not null;
        var lh = team?.Home ?? (double)line / 2;
        var la = team?.Away ?? (double)line / 2;
        var split = Chances(lh, la, line);
        if (split is null) return;

        var over = split.Value.Over >= split.Value.Under;
        var win = over ? split.Value.Over : split.Value.Under;
        var push = over ? split.Value.OverPush : split.Value.UnderPush;
        rec.OuSide = over ? "大" : "小";
        rec.OuProbability = Math.Round(win * 100m, 1);
        rec.OuPush = Math.Round(push * 100m, 1);
        var how = fromTeams ? "按球队攻防，再向盘口缩放" : "没有球队样本，按盘口当进球期望";
        rec.OuNote = $"大小球 {line:0.##}，看{rec.OuSide}，胜算 {rec.OuProbability:0.#}%，走盘约 {rec.OuPush:0.#}%。{how}。只作参考，不进精选。";
    }

    private static double Poisson(int k, double lambda)
    {
        if (lambda < 1e-9) return k == 0 ? 1 : 0;
        var p = Math.Exp(-lambda);
        for (var i = 1; i <= k; i++) p *= lambda / i;
        return p;
    }
}
