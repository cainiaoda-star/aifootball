namespace AiFootBall.Core.Models;

/// <summary>一场完场样本。亚盘和必发可以缺。</summary>
public sealed class HitTrainingRow
{
    public DateTime MatchTime { get; init; }
    public long MatchId { get; init; }
    /// <summary>赛果 3/1/0。用字符串承接数据库的 CHAR。</summary>
    public string Result { get; init; } = "";
    public decimal HomeOdds { get; init; }
    public decimal DrawOdds { get; init; }
    public decimal AwayOdds { get; init; }
    public decimal? AhOpen { get; init; }
    public decimal? AhClose { get; init; }
    public decimal? RatioHome { get; init; }
    public decimal? RatioDraw { get; init; }
    public decimal? RatioAway { get; init; }
    /// <summary>欧赔主胜初盘。缺了就不把升降送进模型。</summary>
    public decimal? HomeOddsOpen { get; init; }
    public string? LeagueName { get; init; }
}

/// <summary>
/// 三个方向各做一个逻辑回归。特征是去水概率、亚盘升降水、必发占比、欧赔主胜连续升降、亚盘终盘深浅。
/// 按比赛时间切分：前面拟合，中间做保序校准，最后一段只用来比 Brier 分数。
/// 校准模型不比去水更好时，预测退回去水概率，仍可按联赛基础胜率收缩。
/// 联赛场次少于 12 时不收缩，避免把一两轮的样本写成规律。
/// </summary>
public sealed class HitProbabilityModel
{
    private readonly SideFit _home;
    private readonly SideFit _draw;
    private readonly SideFit _away;
    private const int FeatureCount = 5;
    private const int LeagueMinMatches = 12;
    private const decimal LeagueStrength = 80m;

    private readonly double _meanHomeRatio;
    private readonly double _meanAwayRatio;
    private readonly double _meanDrawRatio;
    private readonly Dictionary<string, (int H, int D, int A)> _leagues;

    private HitProbabilityModel(bool usesLogistic, double modelBrier, double devigBrier, int trainCount, int holdoutCount,
        SideFit home, SideFit draw, SideFit away, double meanHomeRatio, double meanAwayRatio, double meanDrawRatio,
        Dictionary<string, (int H, int D, int A)>? leagues = null)
    {
        UsesLogistic = usesLogistic;
        HoldoutBrierModel = modelBrier;
        HoldoutBrierDevig = devigBrier;
        TrainCount = trainCount;
        HoldoutCount = holdoutCount;
        _home = home;
        _draw = draw;
        _away = away;
        _meanHomeRatio = meanHomeRatio;
        _meanAwayRatio = meanAwayRatio;
        _meanDrawRatio = meanDrawRatio;
        _leagues = leagues ?? new Dictionary<string, (int H, int D, int A)>(StringComparer.Ordinal);
    }

    public bool UsesLogistic { get; }
    public double HoldoutBrierModel { get; }
    public double HoldoutBrierDevig { get; }
    public int TrainCount { get; }
    public int HoldoutCount { get; }

    public static HitProbabilityModel DevigOnly { get; } = new(
        false, 1, 1, 0, 0, SideFit.Empty, SideFit.Empty, SideFit.Empty, 0.45, 0.30, 0.25);

    public static HitProbabilityModel Fit(IReadOnlyList<HitTrainingRow> rows)
    {
        var usable = rows
            .Where(r => SideOf(r) is '3' or '1' or '0')
            .Select(r => (Row: r, Price: HitRateMath.Devig(r.HomeOdds, r.DrawOdds, r.AwayOdds)))
            .Where(x => x.Price is not null)
            .OrderBy(x => x.Row.MatchTime)
            .ThenBy(x => x.Row.MatchId)
            .ToList();

        var leagues = CountLeagues(usable);
        if (usable.Count < 300) return Inactive(leagues);

        var holdoutCount = Math.Max(40, usable.Count / 5);
        var fitCount = usable.Count - holdoutCount;
        if (fitCount < 200) return Inactive(leagues);

        var calibCount = Math.Max(30, fitCount / 5);
        var trainCount = fitCount - calibCount;
        if (trainCount < 150) return Inactive(leagues);

        var train = usable.Take(trainCount).ToList();
        var calib = usable.Skip(trainCount).Take(calibCount).ToList();
        var hold = usable.Skip(fitCount).ToList();

        var meanHome = MeanRatio(train.Select(x => x.Row.RatioHome));
        var meanAway = MeanRatio(train.Select(x => x.Row.RatioAway));
        var drawVals = train.Select(x => DrawRatio(x.Row)).Where(v => v is not null).Select(v => v!.Value).ToList();
        var meanDraw = drawVals.Count == 0 ? 0.25 : drawVals.Average();

        var home = FitSide(train, calib, meanHome, meanAway, meanDraw, '3');
        var draw = FitSide(train, calib, meanHome, meanAway, meanDraw, '1');
        var away = FitSide(train, calib, meanHome, meanAway, meanDraw, '0');

        var modelRows = hold.Select(x =>
        {
            var p = PredictRaw(home, draw, away, x.Row, x.Price!.Value, meanHome, meanAway, meanDraw);
            return (p.Home, p.Draw, p.Away, SideOf(x.Row));
        }).ToList();
        var devigRows = hold.Select(x =>
        {
            var p = x.Price!.Value;
            return ((double)p.Home, (double)p.Draw, (double)p.Away, SideOf(x.Row));
        }).ToList();

        var modelBrier = HitRateMath.Brier(modelRows);
        var devigBrier = HitRateMath.Brier(devigRows);
        var useModel = modelBrier + 0.001 < devigBrier;
        return new HitProbabilityModel(useModel, modelBrier, devigBrier, trainCount, hold.Count,
            home, draw, away, meanHome, meanAway, meanDraw, leagues);
    }

    public (decimal Home, decimal Draw, decimal Away) Predict(
        decimal? homeOdds, decimal? drawOdds, decimal? awayOdds, decimal? ahOpen, decimal? ahClose,
        decimal? ratioHome, decimal? ratioAway, decimal? ratioDraw = null,
        string? league = null, decimal? homeOddsDelta = null)
    {
        var price = HitRateMath.Devig(homeOdds, drawOdds, awayOdds);
        if (price is null) return (0, 0, 0);

        (double Home, double Draw, double Away) raw = ((double)price.Value.Home, (double)price.Value.Draw, (double)price.Value.Away);
        if (UsesLogistic)
        {
            var row = new HitTrainingRow
            {
                HomeOdds = homeOdds!.Value,
                DrawOdds = drawOdds!.Value,
                AwayOdds = awayOdds!.Value,
                HomeOddsOpen = homeOddsDelta is decimal delta ? homeOdds!.Value - delta : null,
                AhOpen = ahOpen,
                AhClose = ahClose,
                RatioHome = ratioHome,
                RatioDraw = ratioDraw,
                RatioAway = ratioAway,
                LeagueName = league
            };
            raw = PredictRaw(_home, _draw, _away, row, price.Value, _meanHomeRatio, _meanAwayRatio, _meanDrawRatio);
        }

        return ShrinkLeague((decimal)raw.Home, (decimal)raw.Draw, (decimal)raw.Away, league);
    }

    private static HitProbabilityModel Inactive(Dictionary<string, (int H, int D, int A)> leagues) =>
        new(false, 1, 1, 0, 0, SideFit.Empty, SideFit.Empty, SideFit.Empty, 0.45, 0.30, 0.25, leagues);

    private (decimal Home, decimal Draw, decimal Away) ShrinkLeague(decimal home, decimal draw, decimal away, string? league)
    {
        var name = league?.Trim();
        if (string.IsNullOrEmpty(name) || !_leagues.TryGetValue(name, out var c))
            return (home, draw, away);
        var n = c.H + c.D + c.A;
        if (n < LeagueMinMatches) return (home, draw, away);

        var hs = HitRateMath.Shrink(c.H, n, home, LeagueStrength);
        var ds = HitRateMath.Shrink(c.D, n, draw, LeagueStrength);
        var aws = HitRateMath.Shrink(c.A, n, away, LeagueStrength);
        var sum = hs + ds + aws;
        if (sum <= 0) return (home, draw, away);
        return (hs / sum, ds / sum, aws / sum);
    }

    private static Dictionary<string, (int H, int D, int A)> CountLeagues(
        List<(HitTrainingRow Row, (decimal Home, decimal Draw, decimal Away)? Price)> rows)
    {
        var map = new Dictionary<string, (int H, int D, int A)>(StringComparer.Ordinal);
        foreach (var (row, _) in rows)
        {
            var name = row.LeagueName?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            map.TryGetValue(name, out var c);
            switch (SideOf(row))
            {
                case '3': c.H++; break;
                case '1': c.D++; break;
                case '0': c.A++; break;
                default: continue;
            }
            map[name] = c;
        }
        return map;
    }

    private static (double Home, double Draw, double Away) PredictRaw(
        SideFit home, SideFit draw, SideFit away, HitTrainingRow row,
        (decimal Home, decimal Draw, decimal Away) price,
        double meanHome, double meanAway, double meanDraw)
    {
        var h = home.Probability(Features(row, price, '3', meanHome, meanAway, meanDraw));
        var d = draw.Probability(Features(row, price, '1', meanHome, meanAway, meanDraw));
        var a = away.Probability(Features(row, price, '0', meanHome, meanAway, meanDraw));
        var sum = h + d + a;
        if (sum <= 0) return ((double)price.Home, (double)price.Draw, (double)price.Away);
        return (h / sum, d / sum, a / sum);
    }

    private static SideFit FitSide(
        List<(HitTrainingRow Row, (decimal Home, decimal Draw, decimal Away)? Price)> train,
        List<(HitTrainingRow Row, (decimal Home, decimal Draw, decimal Away)? Price)> calib,
        double meanHome, double meanAway, double meanDraw, char side)
    {
        var xs = new double[train.Count][];
        var ys = new double[train.Count];
        for (var i = 0; i < train.Count; i++)
        {
            xs[i] = Features(train[i].Row, train[i].Price!.Value, side, meanHome, meanAway, meanDraw);
            ys[i] = SideOf(train[i].Row) == side ? 1 : 0;
        }

        var feat = xs[0].Length;
        var mean = new double[feat];
        var std = new double[feat];
        for (var f = 0; f < feat; f++)
        {
            double s = 0;
            for (var i = 0; i < xs.Length; i++) s += xs[i][f];
            mean[f] = s / xs.Length;
            double v = 0;
            for (var i = 0; i < xs.Length; i++)
            {
                var d = xs[i][f] - mean[f];
                v += d * d;
            }
            std[f] = Math.Sqrt(v / xs.Length);
            if (std[f] < 1e-6) std[f] = 1;
        }

        var w = new double[feat];
        var bias = 0d;
        const int steps = 800;
        const double lr = 0.2;
        const double l2 = 0.01;
        for (var step = 0; step < steps; step++)
        {
            var gradW = new double[feat];
            double gradB = 0;
            for (var i = 0; i < xs.Length; i++)
            {
                var z = bias;
                for (var f = 0; f < feat; f++) z += w[f] * ((xs[i][f] - mean[f]) / std[f]);
                var p = Sigmoid(z);
                var err = p - ys[i];
                gradB += err;
                for (var f = 0; f < feat; f++) gradW[f] += err * ((xs[i][f] - mean[f]) / std[f]);
            }
            var n = xs.Length;
            bias -= lr * gradB / n;
            for (var f = 0; f < feat; f++) w[f] -= lr * (gradW[f] / n + l2 * w[f]);
        }

        var scored = new List<(double Score, double Y)>(calib.Count);
        foreach (var (row, price) in calib)
        {
            var x = Features(row, price!.Value, side, meanHome, meanAway, meanDraw);
            scored.Add((Raw(x, mean, std, w, bias), SideOf(row) == side ? 1 : 0));
        }

        return new SideFit(mean, std, w, bias, Pav(scored));
    }

    private static char SideOf(HitTrainingRow row) =>
        string.IsNullOrEmpty(row.Result) ? '\0' : row.Result[0];

    /// <summary>去水概率、偏向该方向的亚盘变化、该方向的必发占比、欧赔主胜连续升降、亚盘终盘深浅。</summary>
    private static double[] Features(
        HitTrainingRow row, (decimal Home, decimal Draw, decimal Away) price, char side,
        double meanHome, double meanAway, double meanDraw)
    {
        var move = row.AhOpen is decimal open && row.AhClose is decimal close ? (double)(open - close) : 0d;
        var homeRatio = row.RatioHome is decimal rh ? (double)rh / 100 : meanHome;
        var awayRatio = row.RatioAway is decimal ra ? (double)ra / 100 : meanAway;
        var drawRatio = DrawRatio(row) is double dr ? dr : meanDraw;
        var eu = row.HomeOddsOpen is decimal euOpen && euOpen > 1 ? (double)(row.HomeOdds - euOpen) : 0d;
        var ah = row.AhClose is decimal ahClose ? (double)ahClose : 0d;
        return side switch
        {
            '3' => new[] { (double)price.Home, move, homeRatio, -eu, -ah },
            '0' => new[] { (double)price.Away, -move, awayRatio, eu, ah },
            _ => new[] { (double)price.Draw, -Math.Abs(move), drawRatio, -Math.Abs(eu), -Math.Abs(ah) }
        };
    }

    private static double? DrawRatio(HitTrainingRow row)
    {
        if (row.RatioDraw is decimal rd && rd >= 0)
            return Math.Clamp((double)rd / 100, 0, 1);
        // 旧快照无平占比时，用 100−主−客 兜底
        if (row.RatioHome is not decimal h || row.RatioAway is not decimal a) return null;
        return Math.Clamp((double)(100m - h - a) / 100, 0, 1);
    }

    private static double MeanRatio(IEnumerable<decimal?> values)
    {
        var list = values.Where(v => v is not null).Select(v => (double)v!.Value / 100).ToList();
        return list.Count == 0 ? 0.33 : list.Average();
    }

    private static double Raw(double[] x, double[] mean, double[] std, double[] w, double bias)
    {
        var z = bias;
        var n = Math.Min(w.Length, Math.Min(x.Length, mean.Length));
        for (var f = 0; f < n; f++) z += w[f] * ((x[f] - mean[f]) / std[f]);
        return Sigmoid(z);
    }

    private static double Sigmoid(double z)
    {
        if (z > 20) return 1;
        if (z < -20) return 0;
        return 1 / (1 + Math.Exp(-z));
    }

    private static List<IsoBlock> Pav(List<(double Score, double Y)> points)
    {
        var blocks = new List<IsoBlock>();
        foreach (var (score, y) in points.OrderBy(p => p.Score))
        {
            blocks.Add(new IsoBlock(score, score, y, 1));
            while (blocks.Count >= 2 && blocks[^2].Mean > blocks[^1].Mean + 1e-12)
            {
                var merged = blocks[^2].Merge(blocks[^1]);
                blocks.RemoveAt(blocks.Count - 1);
                blocks.RemoveAt(blocks.Count - 1);
                blocks.Add(merged);
            }
        }
        return blocks;
    }

    private sealed class SideFit
    {
        private readonly double[] _mean;
        private readonly double[] _std;
        private readonly double[] _w;
        private readonly double _bias;
        private readonly List<IsoBlock> _iso;

        public static SideFit Empty { get; } = new(new double[FeatureCount], Enumerable.Repeat(1d, FeatureCount).ToArray(), new double[FeatureCount], 0, new List<IsoBlock>());

        public SideFit(double[] mean, double[] std, double[] w, double bias, List<IsoBlock> iso)
        {
            _mean = mean;
            _std = std;
            _w = w;
            _bias = bias;
            _iso = iso;
        }

        public double Probability(double[] x)
        {
            var raw = Raw(x, _mean, _std, _w, _bias);
            if (_iso.Count == 0) return raw;
            if (raw <= _iso[0].Min) return _iso[0].Mean;
            foreach (var block in _iso)
            {
                if (raw <= block.Max) return block.Mean;
            }
            return _iso[^1].Mean;
        }
    }

    private sealed class IsoBlock
    {
        public IsoBlock(double min, double max, double sum, int count)
        {
            Min = min;
            Max = max;
            Sum = sum;
            Count = count;
        }

        public double Min { get; }
        public double Max { get; }
        public double Sum { get; }
        public int Count { get; }
        public double Mean => Count == 0 ? 0 : Sum / Count;

        public IsoBlock Merge(IsoBlock other) =>
            new(Math.Min(Min, other.Min), Math.Max(Max, other.Max), Sum + other.Sum, Count + other.Count);
    }
}
