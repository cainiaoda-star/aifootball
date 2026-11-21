using System.Globalization;

namespace AiFootBall.Core.Models.Candidate;

/// <summary>
/// 全支撑 Dixon-Coles。只修正 0:0、1:0、0:1、1:1。
/// 归一用闭合式 Z，不把截断网格重新除掉冒充 100%。
/// 网格外的质量留在 TailMass，并计入总进球 7+ 和对应方向的「其他」。
/// 本类型不替换 <see cref="ScorelineModel"/> 的线上拟合。
/// </summary>
public static class FullSupportDixonColes
{
    public const int MaxCells = 200;
    public const double TruncationLimit = 1e-9;

    public static SupportDistribution Create(double lambdaHome, double lambdaAway, double rho)
    {
        if (!double.IsFinite(lambdaHome) || !double.IsFinite(lambdaAway) || !double.IsFinite(rho)
            || lambdaHome <= 0 || lambdaAway <= 0)
            return SupportDistribution.Fail("lambda-invalid");

        var t00 = 1 - lambdaHome * lambdaAway * rho;
        var t10 = 1 + lambdaAway * rho;
        var t01 = 1 + lambdaHome * rho;
        var t11 = 1 - rho;
        if (t00 <= 0 || t10 <= 0 || t01 <= 0 || t11 <= 0)
            return SupportDistribution.Fail("tau-negative");

        var n = Math.Max(MarginLength(lambdaHome), MarginLength(lambdaAway));
        if (n < 2) return SupportDistribution.Fail("tail-not-converged");
        var homeLog = new double[n];
        var awayLog = new double[n];
        for (var i = 0; i < n; i++)
        {
            homeLog[i] = LogSeries(i, lambdaHome);
            awayLog[i] = LogSeries(i, lambdaAway);
        }

        var z = 1
            + (t00 - 1) * Math.Exp(homeLog[0] + awayLog[0])
            + (t10 - 1) * Math.Exp(homeLog[1] + awayLog[0])
            + (t01 - 1) * Math.Exp(homeLog[0] + awayLog[1])
            + (t11 - 1) * Math.Exp(homeLog[1] + awayLog[1]);
        if (!double.IsFinite(z) || z <= 0)
            return SupportDistribution.Fail("normalizer-invalid");

        var cell = new double[n * n];
        double sum = 0, hole = 0;
        var logZ = Math.Log(z);
        for (var h = 0; h < n; h++)
        for (var a = 0; a < n; a++)
        {
            var tau = (h, a) switch
            {
                (0, 0) => t00,
                (1, 0) => t10,
                (0, 1) => t01,
                (1, 1) => t11,
                _ => 1d
            };
            if (tau <= 0) return SupportDistribution.Fail("tau-negative");
            var logq = Math.Log(tau) + homeLog[h] + awayLog[a] - logZ;
            if (!double.IsFinite(logq)) return SupportDistribution.Fail("cell-invalid");
            var p = Math.Exp(logq);
            if (!double.IsFinite(p) || p < 0) return SupportDistribution.Fail("cell-invalid");
            if (!(p > 0))
            {
                var powered = Math.Exp(logq * 0.5);
                if (powered > 0) hole += powered;
            }
            cell[h * n + a] = p;
            sum += p;
        }

        var tail = 1d - sum;
        if (!double.IsFinite(tail) || tail < -1e-6)
            return SupportDistribution.Fail("tail-negative");
        if (tail < 0) tail = 0;
        var external = Math.Max(tail, MarginTail(lambdaHome, n) + MarginTail(lambdaAway, n));
        if (n >= MaxCells && (external > TruncationLimit || hole > TruncationLimit))
            return SupportDistribution.Fail("tail-not-converged");
        return SupportDistribution.Accepted(cell, n, z, external, lambdaHome, lambdaAway, rho, hole);
    }

    /// <summary>
    /// 两条轴算到同一个长度。不能在一条轴的线和已经接近 1 时停掉，让另一条轴的格子里留下 0。
    /// </summary>
    static int MarginLength(double lambda)
    {
        double logFact = 0;
        for (var k = 0; k < MaxCells - 1; k++)
        {
            var logp = -lambda + k * Math.Log(lambda) - logFact;
            var ratio = lambda / (k + 1d);
            if (k + 1d > lambda && ratio < 1 && double.IsFinite(logp))
            {
                var tail = Math.Exp(logp) * ratio / (1d - ratio);
                if (double.IsFinite(tail) && tail < 1e-18) return k + 1;
            }
            logFact += Math.Log(k + 1d);
        }
        return MaxCells;
    }

    static double MarginTail(double lambda, int length)
    {
        if (length <= 0) return 1;
        var k = length - 1;
        double logFact = 0;
        for (var i = 1; i <= k; i++) logFact += Math.Log(i);
        var logp = -lambda + k * Math.Log(lambda) - logFact;
        var ratio = lambda / (k + 1d);
        if (!(ratio < 1) || !double.IsFinite(logp)) return 1;
        var tail = Math.Exp(logp) * ratio / (1d - ratio);
        return double.IsFinite(tail) && tail > 0 ? tail : 0;
    }

    static double LogSeries(int k, double lambda)
    {
        double logFact = 0;
        for (var i = 1; i <= k; i++) logFact += Math.Log(i);
        return -lambda + k * Math.Log(lambda) - logFact;
    }
}

public sealed class SupportDistribution
{
    readonly double[] _cell;

    SupportDistribution(string status, double[] cell, int stride, double normalizer, double tail,
        double lambdaHome, double lambdaAway, double rho, double internalTruncation)
    {
        Status = status;
        _cell = cell;
        Stride = stride;
        Normalizer = normalizer;
        TailMass = tail;
        LambdaHome = lambdaHome;
        LambdaAway = lambdaAway;
        Rho = rho;
        InternalTruncation = internalTruncation;
    }

    public static SupportDistribution Fail(string status) => new(status, Array.Empty<double>(), 0, 0, 0, 0, 0, 0, 0);

    public static SupportDistribution Accepted(double[] cell, int stride, double normalizer, double tail,
        double lambdaHome = 0, double lambdaAway = 0, double rho = 0, double internalTruncation = 0)
        => new("ok", cell, stride, normalizer, tail, lambdaHome, lambdaAway, rho, internalTruncation);

    public string Status { get; }
    public bool Ok => Status == "ok";
    public int Stride { get; }
    public double Normalizer { get; }
    public double TailMass { get; }
    /// <summary>网格内部因下溢写成 0、但 T=2 下仍有质量的保守上界。不能靠把这些格子留成 0 来声称精确。</summary>
    public double InternalTruncation { get; }
    public double LambdaHome { get; }
    public double LambdaAway { get; }
    public double Rho { get; }

    public double Prob(int home, int away)
    {
        if (!Ok || home < 0 || away < 0 || home >= Stride || away >= Stride) return 0;
        return _cell[home * Stride + away];
    }
}

/// <summary>
/// 分布温度。pT = q^(1/T) / Z(T)。T=1 是恒等，不是校准成功。
/// 目标是平均负对数似然加 alpha*(log T)^2。alpha 只能由调用方从内层窗传入。
/// </summary>
public static class TemperatureCalibrator
{
    public static TemperatureFit Fit(
        IReadOnlyList<SupportDistribution> predicted,
        IReadOnlyList<(int Home, int Away)> actual,
        double alpha)
    {
        if (predicted.Count != actual.Count)
            return TemperatureFit.Fail("length-mismatch");
        if (!double.IsFinite(alpha) || alpha < 0)
            return TemperatureFit.Fail("alpha-invalid");
        if (predicted.Count < CandidateProtocol.MinCalibrationMatches)
            return TemperatureFit.Fail("insufficient-matches");
        foreach (var d in predicted)
            if (!d.Ok) return TemperatureFit.Fail("distribution-not-ok");

        var bestT = 1d;
        var best = Objective(predicted, actual, 1d, alpha);
        foreach (var t in CandidateProtocol.TemperatureGrid)
        {
            var v = Objective(predicted, actual, t, alpha);
            if (v < best)
            {
                best = v;
                bestT = t;
            }
        }

        if (!double.IsFinite(best))
            return TemperatureFit.Fail("objective-invalid");
        return TemperatureFit.Computed(bestT, alpha, best, predicted.Count);
    }

    public static SupportDistribution Apply(SupportDistribution raw, double temperature)
    {
        if (!raw.Ok) return SupportDistribution.Fail(raw.Status);
        if (!double.IsFinite(temperature) || temperature <= 0)
            return SupportDistribution.Fail("temperature-invalid");
        if (Math.Abs(temperature - 1d) < 1e-12) return raw;

        var n = raw.Stride;
        var powered = new double[n * n];
        double sum = 0;
        var inv = 1d / temperature;
        for (var h = 0; h < n; h++)
        for (var a = 0; a < n; a++)
        {
            var q = raw.Prob(h, a);
            var p = q <= 0 ? 0 : Math.Pow(q, inv);
            if (!double.IsFinite(p) || p < 0)
                return SupportDistribution.Fail("temperature-cell-invalid");
            powered[h * n + a] = p;
            sum += p;
        }

        if (sum <= 0 || !double.IsFinite(sum))
            return SupportDistribution.Fail("temperature-normalizer-invalid");
        var external = raw.LambdaHome > 0 && raw.LambdaAway > 0 && raw.Normalizer > 0
            ? UpperOmittedPower(raw.LambdaHome, raw.LambdaAway, raw.Normalizer, raw.Stride, inv)
            : raw.TailMass > 0 ? double.PositiveInfinity : 0;
        var omitted = raw.InternalTruncation + external;
        if (!double.IsFinite(omitted))
            return SupportDistribution.Fail("tail-not-enumerated");
        var denom = sum + omitted;
        var share = omitted / denom;
        if (share > FullSupportDixonColes.TruncationLimit)
            return raw.LambdaHome > 0 && raw.LambdaAway > 0
                ? Expand(raw, temperature)
                : SupportDistribution.Fail("post-temperature-tail");
        for (var i = 0; i < powered.Length; i++) powered[i] /= denom;
        return SupportDistribution.Accepted(powered, n, denom, share, raw.LambdaHome, raw.LambdaAway, raw.Rho);
    }

    /// <summary>
    /// 原始网格在温度之后的遗漏份额超过界限时，把网格加大，直到界限外的份额低于界限。
    /// 剩下的份额照实保存，不写成 0。
    /// </summary>
    static SupportDistribution Expand(SupportDistribution raw, double temperature)
    {
        var inv = 1d / temperature;
        var z = DixonColesNormalizer(raw.LambdaHome, raw.LambdaAway, raw.Rho);
        if (!double.IsFinite(z) || z <= 0) return SupportDistribution.Fail("normalizer-invalid");
        var logZ = Math.Log(z);
        var n = Math.Max(raw.Stride, 1);
        while (n <= FullSupportDixonColes.MaxCells)
        {
            var limit = Math.Min(n + 8, FullSupportDixonColes.MaxCells);
            var homeLog = new double[limit];
            var awayLog = new double[limit];
            for (var i = 0; i < limit; i++)
            {
                homeLog[i] = LogPoisson(i, raw.LambdaHome);
                awayLog[i] = LogPoisson(i, raw.LambdaAway);
            }
            var cells = new double[n * n];
            double inside = 0, omitted = 0;
            for (var h = 0; h < limit; h++)
            for (var a = 0; a < limit; a++)
            {
                var logq = homeLog[h] + awayLog[a] - logZ;
                if (h <= 1 && a <= 1)
                    logq = LogUnnormalized(raw.LambdaHome, raw.LambdaAway, raw.Rho, h, a) - logZ;
                if (!double.IsFinite(logq)) continue;
                var p = Math.Exp(logq * inv);
                if (!(p > 0)) continue;
                if (h < n && a < n)
                {
                    cells[h * n + a] = p;
                    inside += p;
                }
                else omitted += p;
            }
            var denom = inside + omitted;
            if (!(denom > 0) || !double.IsFinite(denom))
                return SupportDistribution.Fail("temperature-normalizer-invalid");
            var share = omitted / denom;
            if (share <= FullSupportDixonColes.TruncationLimit || n == FullSupportDixonColes.MaxCells)
            {
                if (share > FullSupportDixonColes.TruncationLimit)
                    return SupportDistribution.Fail("post-temperature-tail");
                for (var i = 0; i < cells.Length; i++) cells[i] /= denom;
                return SupportDistribution.Accepted(cells, n, denom, share, raw.LambdaHome, raw.LambdaAway, raw.Rho);
            }
            var grown = Math.Min(n + 4, FullSupportDixonColes.MaxCells);
            if (grown == n) return SupportDistribution.Fail("post-temperature-tail");
            n = grown;
        }
        return SupportDistribution.Fail("post-temperature-tail");
    }

    static double DixonColesNormalizer(double lambdaHome, double lambdaAway, double rho)
    {
        var t00 = 1 - lambdaHome * lambdaAway * rho;
        var t10 = 1 + lambdaAway * rho;
        var t01 = 1 + lambdaHome * rho;
        var t11 = 1 - rho;
        var p0h = Math.Exp(-lambdaHome);
        var p0a = Math.Exp(-lambdaAway);
        return 1
            + (t00 - 1) * p0h * p0a
            + (t10 - 1) * p0h * lambdaHome * p0a
            + (t01 - 1) * p0h * p0a * lambdaAway
            + (t11 - 1) * p0h * lambdaHome * p0a * lambdaAway;
    }

    /// <summary>
    /// 温度之后的遗漏质量。把尾部总质量再取一次幂不能当作上界，所以这里继续逐格算。
    /// 手工网格没有 λ 时，正的尾部无法逐格展开，调用方必须失败。
    /// </summary>
    public static double OmittedAfterTemperature(SupportDistribution raw, double invTemperature)
    {
        var error = TruncationError(raw, invTemperature);
        if (!double.IsFinite(error.Internal) || !double.IsFinite(error.External)) return double.NaN;
        return error.Internal + error.External;
    }

    /// <summary>
    /// 内部截断和网格外尾部分开算。格子里的 0 不是精度证明，TailMass 为 0 也不是。
    /// </summary>
    public static (double Internal, double External) TruncationError(SupportDistribution raw, double invTemperature)
    {
        if (!double.IsFinite(invTemperature) || invTemperature <= 0) return (double.NaN, double.NaN);
        if (raw.LambdaHome <= 0 || raw.LambdaAway <= 0)
            return (raw.InternalTruncation, raw.TailMass > 0 ? double.PositiveInfinity : 0);
        if (!(raw.Normalizer > 0) || !raw.Ok) return (double.PositiveInfinity, double.PositiveInfinity);
        var logZ = Math.Log(raw.Normalizer);
        double hole = 0;
        for (var h = 0; h < raw.Stride; h++)
        for (var a = 0; a < raw.Stride; a++)
        {
            if (raw.Prob(h, a) > 0) continue;
            var logq = LogUnnormalized(raw.LambdaHome, raw.LambdaAway, raw.Rho, h, a) - logZ;
            if (!double.IsFinite(logq)) continue;
            var powered = Math.Exp(logq * invTemperature);
            if (powered > 0) hole += powered;
        }
        var external = UpperOmittedPower(raw.LambdaHome, raw.LambdaAway, raw.Normalizer, raw.Stride, invTemperature);
        return (hole, external);
    }

    /// <summary>
    /// 网格外逐格幂次之和，再加上最外圈之后的几何上界。原始 TailMass 为 0 也不能当成温度后的零尾部。
    /// </summary>
    static double UpperOmittedPower(double lambdaHome, double lambdaAway, double normalizer, int stride, double inv)
    {
        var logZ = Math.Log(normalizer);
        var covered = stride - 1;
        double enumerated = 0;
        while (covered < FullSupportDixonColes.MaxCells - 1)
        {
            var next = Math.Min(FullSupportDixonColes.MaxCells - 1, Math.Max(covered + 4, stride + 4));
            if (next <= covered) break;
            var homeLog = new double[next + 1];
            var awayLog = new double[next + 1];
            for (var i = 0; i <= next; i++)
            {
                homeLog[i] = LogPoisson(i, lambdaHome);
                awayLog[i] = LogPoisson(i, lambdaAway);
            }
            for (var h = 0; h <= next; h++)
            for (var a = 0; a <= next; a++)
            {
                if (h < stride && a < stride) continue;
                if (h <= covered && a <= covered) continue;
                var logq = homeLog[h] + awayLog[a] - logZ;
                if (!double.IsFinite(logq)) continue;
                var powered = Math.Exp(logq * inv);
                if (powered > 0) enumerated += powered;
            }
            var ratioHome = next + 1 > lambdaHome ? lambdaHome / (next + 2) : 0.99;
            var ratioAway = next + 1 > lambdaAway ? lambdaAway / (next + 2) : 0.99;
            var ratio = Math.Max(ratioHome, ratioAway);
            if (ratio >= 1) ratio = 0.99;
            var edge = Math.Exp((homeLog[next] + awayLog[0] - logZ) * inv);
            var bound = edge / (1d - ratio) * (next + 2) * 2d;
            if (!double.IsFinite(bound) || bound < 0) bound = edge;
            if (bound <= 1e-18 || next == FullSupportDixonColes.MaxCells - 1)
                return enumerated + Math.Max(bound, 0);
            covered = next;
        }
        return enumerated;
    }

    public static double LogProbability(double lambdaHome, double lambdaAway, double rho, double temperature, int home, int away)
    {
        if (home < 0 || away < 0 || temperature <= 0) return double.NaN;
        var dist = FullSupportDixonColes.Create(lambdaHome, lambdaAway, rho);
        if (!dist.Ok) return double.NaN;
        var scaled = Apply(dist, temperature);
        if (!scaled.Ok) return double.NaN;
        var direct = LogProbability(scaled, temperature, home, away);
        if (double.IsFinite(direct)) return direct;
        var logq = LogUnnormalized(lambdaHome, lambdaAway, rho, home, away) - Math.Log(dist.Normalizer);
        if (!double.IsFinite(logq)) return double.NaN;
        if (Math.Abs(temperature - 1d) < 1e-12) return logq;
        return logq / temperature - Math.Log(scaled.Normalizer);
    }

    public static double LogProbability(SupportDistribution scaled, double temperature, int home, int away)
    {
        if (!scaled.Ok || home < 0 || away < 0 || temperature <= 0) return double.NaN;
        if (home < scaled.Stride && away < scaled.Stride)
        {
            var p = scaled.Prob(home, away);
            if (p > 0) return Math.Log(p);
        }
        if (scaled.LambdaHome <= 0 || scaled.LambdaAway <= 0 || !(scaled.Normalizer > 0))
            return double.NaN;
        var logq = LogUnnormalized(scaled.LambdaHome, scaled.LambdaAway, scaled.Rho, home, away);
        if (Math.Abs(temperature - 1d) < 1e-12)
            return logq - Math.Log(scaled.Normalizer);
        var z = DixonColesNormalizer(scaled.LambdaHome, scaled.LambdaAway, scaled.Rho);
        if (!(z > 0)) return double.NaN;
        logq -= Math.Log(z);
        if (!double.IsFinite(logq)) return double.NaN;
        return logq / temperature - Math.Log(scaled.Normalizer);
    }

    static double LogUnnormalized(double lambdaHome, double lambdaAway, double rho, int home, int away)
    {
        var tau = (home, away) switch
        {
            (0, 0) => 1 - lambdaHome * lambdaAway * rho,
            (1, 0) => 1 + lambdaAway * rho,
            (0, 1) => 1 + lambdaHome * rho,
            (1, 1) => 1 - rho,
            _ => 1d
        };
        if (tau <= 0) return double.NaN;
        return Math.Log(tau) + LogPoisson(home, lambdaHome) + LogPoisson(away, lambdaAway);
    }

    static double LogPoisson(int k, double lambda)
    {
        if (k < 0 || lambda <= 0) return double.NaN;
        double s = -lambda + k * Math.Log(lambda);
        for (var i = 1; i <= k; i++) s -= Math.Log(i);
        return s;
    }

    static double Objective(
        IReadOnlyList<SupportDistribution> predicted,
        IReadOnlyList<(int Home, int Away)> actual,
        double t,
        double alpha)
    {
        double nll = 0;
        for (var i = 0; i < predicted.Count; i++)
        {
            var scaled = Apply(predicted[i], t);
            if (!scaled.Ok) return double.PositiveInfinity;
            var (h, a) = actual[i];
            var lp = LogProbability(scaled, t, h, a);
            if (!double.IsFinite(lp)) return double.PositiveInfinity;
            nll += -lp;
        }

        var logT = Math.Log(t);
        return nll / predicted.Count + alpha * logT * logT;
    }
}

public sealed class TemperatureFit
{
    TemperatureFit(string status, double temperature, double alpha, double objective, int matches,
        string evidenceId, string modelVersion, string codeHash)
    {
        Status = status;
        Temperature = temperature;
        Alpha = alpha;
        Objective = objective;
        Matches = matches;
        EvidenceId = evidenceId;
        ModelVersion = modelVersion;
        CodeHash = codeHash;
    }

    public static TemperatureFit Fail(string status) => new(status, 0, 0, double.NaN, 0, "", "", "");

    public static TemperatureFit Computed(double temperature, double alpha, double objective, int matches)
        => new("computed", temperature, alpha, objective, matches, "", "", "");

    public static TemperatureFit Bound(double temperature, double alpha, double objective, int matches,
        string evidenceId, string modelVersion, string codeHash)
        => new("bound", temperature, alpha, objective, matches, evidenceId, modelVersion, codeHash);

    internal static TemperatureFit ForTest(string modelVersion, string codeHash)
        => new("computed", 1, CandidateProtocol.AlphaInnerDefault, 0, CandidateProtocol.MinCalibrationMatches,
            CandidateProtocol.TestEvidenceId, modelVersion, codeHash);

    public string Status { get; }
    public bool Ok => Status is "computed" or "bound";
    public double Temperature { get; }
    public double Alpha { get; }
    public double Objective { get; }
    public int Matches { get; }
    public string EvidenceId { get; }
    public string ModelVersion { get; }
    public string CodeHash { get; }
}

/// <summary>
/// 校准证据包。绑定到具体模型、政策版本、样本窗与文件哈希。
/// kind=retrospective / seen-backtest 一律拒绝；不得冒充未见验证。
/// </summary>
public sealed class CalibratorEvidence
{
    public string EvidenceId { get; init; } = "";
    public string ModelVersion { get; init; } = "";
    public string CodeHash { get; init; } = "";
    public string PolicyVersion { get; init; } = "";
    public string SampleWindow { get; init; } = "";
    public string FileHash { get; init; } = "";
    public string Kind { get; init; } = "";
    /// <summary>计数：archive.log 独立 test matchId 数（≥ MinTestMatches）。</summary>
    public int Matches { get; init; }
    /// <summary>Hash(ModelVersion)；追溯冻结模型标识，不是 Core DLL 自指。</summary>
    public string ModelFingerprint { get; init; } = "";
    /// <summary>Hash(CodeHash/codeVersion)；追溯冻结实现标识。</summary>
    public string CodeFingerprint { get; init; } = "";
    /// <summary>Hash(PolicyVersion)；须等于 Hash(CandidateProtocol.RuleVersion)。</summary>
    public string PolicyFingerprint { get; init; } = "";
    /// <summary>评估/校准用：results.log 规范化摘要；建证与 Bind 须与磁盘重算一致。</summary>
    public string ResultsHash { get; init; } = "";
    /// <summary>已关联赛果场次数；建证/绑定要求等于 Matches。</summary>
    public int ResultsAttached { get; init; }
    /// <summary>训练名册规范化摘要；增删替换不重叠 ID 也必须使本哈希变化。</summary>
    public string TrainRosterHash { get; init; } = "";
    /// <summary>训练名册场次数。0 仅表示交叉检查清单为空，不得当作已完成 400 场校准。</summary>
    public int TrainRosterCount { get; init; }
    /// <summary>冻结窗元数据 MetaHash（window-meta.integrity）；本地摘要≠可信时间戳。</summary>
    public string WindowMetaHash { get; init; } = "";
}

/// <summary>
/// 训练名册一行。At/OddsAt/Kickoff 须为该场真实时间，禁止用窗级 trainCutoff 代填三列。
/// At=TimeSplit 排序时刻（通常为该场预测/采样时刻）；OddsAt=盘口捕获；Kickoff=开赛。
/// </summary>
public sealed class TrainRosterRow
{
    public long MatchId { get; init; }
    public string At { get; init; } = "";
    public string OddsAt { get; init; } = "";
    public string Kickoff { get; init; } = "";
}

/// <summary>
/// 将 TemperatureFit 合法绑定到证据。生产 Bind 须带窗目录并重算摘要；BoundEvidenceId 为空时生产仍失败。
/// 测试用 BindForTest / BindPredeclaredForTest 指定 requiredEvidenceId，不得写成真实算法合格。
/// </summary>
public static class CalibratorBinding
{
    public const string KindPredeclaredUnseen = "predeclared-unseen";
    public const string KindFixture = "fixture-not-a-pass";

    /// <summary>生产绑定：对照实际窗目录重算，不接受仅对象自洽。</summary>
    public static TemperatureFit Bind(TemperatureFit fit, CalibratorEvidence evidence, string windowDirectory)
        => BindCore(fit, evidence, CandidateProtocol.BoundEvidenceId, allowFixture: false, windowDirectory);

    /// <summary>无窗目录的生产入口：predeclared 直接拒绝，避免跳过磁盘复核。</summary>
    public static TemperatureFit Bind(TemperatureFit fit, CalibratorEvidence evidence)
        => BindCore(fit, evidence, CandidateProtocol.BoundEvidenceId, allowFixture: false, windowDirectory: null);

    internal static TemperatureFit BindForTest(TemperatureFit fit, CalibratorEvidence evidence, string requiredEvidenceId)
        => BindCore(fit, evidence, requiredEvidenceId, allowFixture: true, windowDirectory: null);

    /// <summary>
    /// 与生产共用 BindCore：指定 requiredEvidenceId + 窗目录做摘要核对。
    /// 离线入口与单测使用；不得写成生产 BoundEvidenceId 已写入。
    /// </summary>
    public static TemperatureFit BindVerified(
        TemperatureFit fit, CalibratorEvidence evidence, string requiredEvidenceId, string windowDirectory)
        => BindCore(fit, evidence, requiredEvidenceId, allowFixture: false, windowDirectory);

    /// <summary>测试入口：等同 BindVerified。</summary>
    internal static TemperatureFit BindPredeclaredForTest(
        TemperatureFit fit, CalibratorEvidence evidence, string requiredEvidenceId, string windowDirectory)
        => BindVerified(fit, evidence, requiredEvidenceId, windowDirectory);

    static TemperatureFit BindCore(
        TemperatureFit fit, CalibratorEvidence evidence, string requiredEvidenceId, bool allowFixture, string? windowDirectory)
    {
        if (fit is not { Status: "computed" })
            return TemperatureFit.Fail("calibrator-not-ready");
        if (string.IsNullOrWhiteSpace(requiredEvidenceId))
            return TemperatureFit.Fail("bound-evidence-id-empty");
        if (evidence is null)
            return TemperatureFit.Fail("evidence-missing");
        if (string.IsNullOrWhiteSpace(evidence.EvidenceId))
            return TemperatureFit.Fail("evidence-id-missing");
        if (!string.Equals(evidence.EvidenceId, requiredEvidenceId, StringComparison.Ordinal))
            return TemperatureFit.Fail("evidence-id-mismatch");
        if (string.Equals(evidence.Kind, "retrospective", StringComparison.Ordinal)
            || string.Equals(evidence.Kind, "seen-backtest", StringComparison.Ordinal))
            return TemperatureFit.Fail("evidence-seen-or-retrospective");
        if (allowFixture)
        {
            if (!string.Equals(evidence.Kind, KindFixture, StringComparison.Ordinal))
                return TemperatureFit.Fail("evidence-kind-invalid");
        }
        else if (!string.Equals(evidence.Kind, KindPredeclaredUnseen, StringComparison.Ordinal))
            return TemperatureFit.Fail("evidence-kind-invalid");
        if (string.IsNullOrWhiteSpace(evidence.ModelVersion) || string.IsNullOrWhiteSpace(evidence.CodeHash))
            return TemperatureFit.Fail("evidence-version-missing");
        if (string.IsNullOrWhiteSpace(evidence.PolicyVersion)
            || !string.Equals(evidence.PolicyVersion, CandidateProtocol.RuleVersion, StringComparison.Ordinal))
            return TemperatureFit.Fail("evidence-policy-mismatch");
        if (string.IsNullOrWhiteSpace(evidence.SampleWindow))
            return TemperatureFit.Fail("evidence-window-missing");
        if (string.Equals(evidence.Kind, KindPredeclaredUnseen, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(windowDirectory))
                return TemperatureFit.Fail("evidence-window-directory-missing");
            var verify = CandidateArchive.VerifyEvidenceAgainstWindow(evidence, windowDirectory);
            if (verify != "evidence:window-ok")
                return TemperatureFit.Fail(verify);
            if (evidence.Matches < CandidateProtocol.MinTestMatches)
                return TemperatureFit.Fail("insufficient-matches");
            if (evidence.ResultsAttached < CandidateProtocol.MinTestMatches
                || evidence.ResultsAttached != evidence.Matches)
                return TemperatureFit.Fail("insufficient-results");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(evidence.FileHash) || !IsSha256Hex(evidence.FileHash))
                return TemperatureFit.Fail("evidence-filehash-invalid");
        }
        if (fit.Matches < CandidateProtocol.MinCalibrationMatches)
            return TemperatureFit.Fail("insufficient-matches");
        return TemperatureFit.Bound(fit.Temperature, fit.Alpha, fit.Objective, fit.Matches,
            evidence.EvidenceId, evidence.ModelVersion, evidence.CodeHash);
    }

    public static bool IsSha256Hex(string value)
    {
        if (value.Length != 64) return false;
        foreach (var c in value)
        {
            var ok = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');
            if (!ok) return false;
        }
        return true;
    }
}

/// <summary>同一张全支撑比分表聚合出来的玩法边际。半全场没有合法联合分布时阻断。</summary>
public static class PlayMargins
{
    static readonly HashSet<string> HomeListed = new(StringComparer.Ordinal)
    {
        "1:0", "2:0", "2:1", "3:0", "3:1", "3:2", "4:0", "4:1", "4:2", "5:0", "5:1", "5:2"
    };
    static readonly HashSet<string> DrawListed = new(StringComparer.Ordinal) { "0:0", "1:1", "2:2", "3:3" };
    static readonly HashSet<string> AwayListed = new(StringComparer.Ordinal)
    {
        "0:1", "0:2", "1:2", "0:3", "1:3", "2:3", "0:4", "1:4", "2:4", "0:5", "1:5", "2:5"
    };

    public static Margin AggregateGoals(SupportDistribution d)
    {
        if (!d.Ok) return Margin.Fail(d.Status);
        if (d.Stride <= 7) return Margin.Fail("tail-not-enumerated");
        var band = new double[8];
        for (var h = 0; h < d.Stride; h++)
        for (var a = 0; a < d.Stride; a++)
        {
            var g = h + a;
            band[g >= 7 ? 7 : g] += d.Prob(h, a);
        }
        band[7] += d.TailMass;
        return Margin.From("JQ", band);
    }

    public static Margin AggregateScores(SupportDistribution d)
    {
        if (!d.Ok) return Margin.Fail(d.Status);
        if (d.TailMass > FullSupportDixonColes.TruncationLimit)
            return Margin.Fail("tail-not-enumerated");
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var code in global::AiFootBall.Core.Models.JcPlayDomain.Bf) map[code] = 0;
        double homeOther = 0, drawOther = 0, awayOther = 0;
        for (var h = 0; h < d.Stride; h++)
        for (var a = 0; a < d.Stride; a++)
        {
            var p = d.Prob(h, a);
            var code = h + ":" + a;
            if (h > a && HomeListed.Contains(code)) map[code] += p;
            else if (h == a && DrawListed.Contains(code)) map[code] += p;
            else if (h < a && AwayListed.Contains(code)) map[code] += p;
            else if (h > a) homeOther += p;
            else if (h == a) drawOther += p;
            else awayOther += p;
        }
        map["胜其它"] = homeOther;
        map["平其它"] = drawOther;
        map["负其它"] = awayOther;
        var values = global::AiFootBall.Core.Models.JcPlayDomain.Bf.Select(c => map[c]).ToArray();
        return Margin.From("BF", values);
    }

    public static Margin AggregateResult(SupportDistribution d)
    {
        if (!d.Ok) return Margin.Fail(d.Status);
        if (d.TailMass > FullSupportDixonColes.TruncationLimit)
            return Margin.Fail("tail-not-enumerated");
        double home = 0, draw = 0, away = 0;
        for (var h = 0; h < d.Stride; h++)
        for (var a = 0; a < d.Stride; a++)
        {
            var p = d.Prob(h, a);
            if (h > a) home += p;
            else if (h == a) draw += p;
            else away += p;
        }
        return Margin.From("SPF", new[] { home, draw, away });
    }

    /// <summary>
    /// 官方整数让球。正号是主队受让，负号是主队让球。
    /// 让胜、让平、让负看主队进球减客队进球再加让球数的正、零、负。
    /// </summary>
    public static Margin AggregateHandicap(SupportDistribution d, decimal homeHandicap)
    {
        if (homeHandicap != decimal.Truncate(homeHandicap))
            return Margin.Fail("handicap-not-integer");
        if (homeHandicap > 20 || homeHandicap < -20)
            return Margin.Fail("handicap-out-of-range");
        if (!d.Ok) return Margin.Fail(d.Status);
        if (d.TailMass > FullSupportDixonColes.TruncationLimit)
            return Margin.Fail("tail-not-enumerated");
        var k = (int)homeHandicap;
        double win = 0, draw = 0, lose = 0;
        for (var h = 0; h < d.Stride; h++)
        for (var a = 0; a < d.Stride; a++)
        {
            var adj = (h - a) + k;
            var p = d.Prob(h, a);
            if (adj > 0) win += p;
            else if (adj == 0) draw += p;
            else lose += p;
        }
        return Margin.From("NSPF", new[] { win, draw, lose });
    }

    public static string OfficialCode(int home, int away)
    {
        var code = home + ":" + away;
        if (home > away && HomeListed.Contains(code)) return code;
        if (home == away && DrawListed.Contains(code)) return code;
        if (home < away && AwayListed.Contains(code)) return code;
        if (home > away) return "胜其它";
        if (home == away) return "平其它";
        return "负其它";
    }

    /// <summary>
    /// 现有半场模型只给平局条件下的领先比例，不是九格联合分布。
    /// 候选不从终场比分猜半场。
    /// </summary>
    public static Margin AggregateHalfFull() => Margin.Fail("half-full-joint-missing");
}

public sealed class Margin
{
    Margin(string status, string play, double[] values)
    {
        Status = status;
        Play = play;
        Values = values;
    }

    public static Margin Fail(string status) => new(status, "", Array.Empty<double>());

    public static Margin From(string play, double[] values)
    {
        var sum = values.Sum();
        if (values.Any(v => !double.IsFinite(v) || v < -1e-12))
            return Fail("margin-invalid");
        if (Math.Abs(sum - 1d) > 1e-6)
            return Fail("margin-not-normalized");
        return new Margin("ok", play, values);
    }

    public string Status { get; }
    public string Play { get; }
    public double[] Values { get; }
    public bool Ok => Status == "ok";
    public double Sum => Values.Sum();
}

/// <summary>研究用的完整互斥去水。缺一档就失败，不写进生产 MarketPct。</summary>
public static class DejuicedMarket
{
    public static bool TryNormalize(IReadOnlyList<decimal>? odds, int expectedCount, out decimal[] probs, out string status)
    {
        probs = Array.Empty<decimal>();
        if (odds is null || expectedCount < 2 || odds.Count != expectedCount)
        {
            status = "incomplete";
            return false;
        }
        decimal inv = 0;
        foreach (var o in odds)
        {
            if (o <= 1m || o >= 1000m)
            {
                status = "incomplete";
                return false;
            }
            inv += 1m / o;
        }
        if (inv <= 0)
        {
            status = "incomplete";
            return false;
        }
        probs = odds.Select(o => (1m / o) / inv).ToArray();
        status = "ok";
        return true;
    }
}

/// <summary>
/// 候选选票。默认关闭。校准未通过、半全场无联合分布、或预算限额未授权时不出票。
/// 不回退到未校准概率。
/// </summary>
public static class CandidateTickets
{
    public const bool Enabled = false;

    public static CandidateDecision Decide(CandidateRequest request)
    {
        if (!Enabled)
            return CandidateDecision.Blocked("candidate-disabled");
        return DecideCore(request, allowTestBinding: false);
    }

    internal static CandidateDecision DecideForTest(CandidateRequest request)
        => DecideCore(request, allowTestBinding: true);

    static CandidateDecision DecideCore(CandidateRequest request, bool allowTestBinding)
    {
        if (request.Calibrator is not { Status: "computed" or "bound" })
            return CandidateDecision.Blocked("calibrator-missing");
        var bound = request.Calibrator.EvidenceId == CandidateProtocol.BoundEvidenceId
            && CandidateProtocol.BoundEvidenceId.Length > 0;
        var testBound = allowTestBinding && request.Calibrator.EvidenceId == CandidateProtocol.TestEvidenceId;
        if (!bound && !testBound)
            return CandidateDecision.Blocked("calibrator-not-bound");
        if (string.IsNullOrWhiteSpace(request.ModelVersion) || string.IsNullOrWhiteSpace(request.CodeHash)
            || request.ModelVersion != request.Calibrator.ModelVersion
            || request.CodeHash != request.Calibrator.CodeHash)
            return CandidateDecision.Blocked("version-mismatch");
        if (request.Play == "BQ")
            return CandidateDecision.Blocked("half-full-joint-missing");
        if (request.Play is not ("BF" or "JQ" or "SPF" or "NSPF"))
            return CandidateDecision.Blocked("play-not-whitelisted");
        if (request.Play == "NSPF" && (request.Handicap is not decimal handicap
            || handicap != decimal.Truncate(handicap)))
            return CandidateDecision.Blocked("handicap-not-integer");
        if (request.Distribution is not { Ok: true } dist)
            return CandidateDecision.Blocked("distribution-missing");
        if (!request.Sellable || request.Kickoff <= request.DecisionTime)
            return CandidateDecision.Blocked("not-sellable");
        if (request.Stake < 0 || request.SameMatchStake < 0 || request.DayStake < 0)
            return CandidateDecision.Blocked("stake-negative");
        if (request.Stake == 0)
            return CandidateDecision.Blocked("stake-not-positive");
        if (request.StakeLimit is not decimal stakeLimit || request.DayLimit is not decimal dayLimit
            || stakeLimit < 0 || dayLimit < 0)
            return CandidateDecision.Blocked("budget-not-authorized");
        var openStake = 0m;
        if (request.OpenBets is not null)
            foreach (var open in request.OpenBets) openStake += open.Stake;
        if (request.SameMatchStake != 0m || openStake != 0m)
        {
            if (request.OpenBets is null || request.OpenBets.Count == 0)
                return CandidateDecision.Blocked("same-match-bets-missing");
            if (openStake != request.SameMatchStake)
                return CandidateDecision.Blocked("same-match-stake-inconsistent");
            if (request.ExposureVersion is null)
                return CandidateDecision.Blocked("exposure-version-missing");
        }
        if (request.ExposureVersion is null || string.IsNullOrWhiteSpace(request.ReservationId))
            return CandidateDecision.Blocked("exposure-version-missing");
        if (request.Odds <= 1m)
            return CandidateDecision.Blocked("odds-invalid");

        var margin = request.Play switch
        {
            "JQ" => PlayMargins.AggregateGoals(dist),
            "SPF" => PlayMargins.AggregateResult(dist),
            "NSPF" => PlayMargins.AggregateHandicap(dist, request.Handicap!.Value),
            _ => PlayMargins.AggregateScores(dist)
        };
        if (!margin.Ok)
            return CandidateDecision.Blocked(margin.Status);
        var index = request.Play switch
        {
            "JQ" => GoalIndex(request.OptionCode),
            "SPF" or "NSPF" => ResultIndex(request.OptionCode),
            _ => ScoreIndex(request.OptionCode)
        };
        if (index < 0 || index >= margin.Values.Length)
            return CandidateDecision.Blocked("option-invalid");
        var p = margin.Values[index];
        if (!double.IsFinite(p) || p <= 0 || p >= 1)
            return CandidateDecision.Blocked("probability-invalid");

        var ev = (decimal)p * request.Odds - 1m;
        var bets = new List<OutcomeBet>();
        if (request.OpenBets is not null) bets.AddRange(request.OpenBets);
        bets.Add(new OutcomeBet(request.Stake, request.Odds,
            (h, a) => Wins(request.Play, request.OptionCode, h, a, request.Handicap)));
        var book = Joint(dist, bets);
        if (!book.Ok)
            return CandidateDecision.Blocked(book.Status);
        if (book.MaxLoss > stakeLimit)
            return CandidateDecision.Blocked("budget-exceeded");
        if (ev <= request.MinNetEv)
            return CandidateDecision.Blocked("ev-below-frozen-floor");
        if (request.Ledger is null || string.IsNullOrWhiteSpace(request.MatchKey))
            return CandidateDecision.Blocked("concurrency-guard-missing");
        var reserved = request.Ledger.Store.TryReserve(
            request.ReservationId, request.DayKey, request.MatchKey,
            request.ExposureVersion.Value, request.ObservedDayUsed,
            request.Stake, dayLimit, book.MaxLoss, stakeLimit,
            new DecisionBinding(request.Play, request.OptionCode, request.Odds, p, request.Handicap ?? 0m,
                request.ModelVersion, request.CodeHash, CandidateProtocol.RuleVersion, request.Calibrator.EvidenceId));
        if (!reserved.Accepted)
            return CandidateDecision.Blocked(reserved.Status);
        return new CandidateDecision("selected", ev, book.MaxLoss, p);
    }

    public static JointRisk Joint(SupportDistribution distribution, IReadOnlyList<OutcomeBet> bets)
    {
        if (!distribution.Ok) return JointRisk.Fail(distribution.Status);
        if (bets.Count == 0) return JointRisk.Fail("no-bets");
        decimal stakeSum = 0;
        foreach (var bet in bets)
        {
            if (bet.Stake < 0 || bet.Odds <= 1m) return JointRisk.Fail("bet-invalid");
            stakeSum += bet.Stake;
        }
        decimal expected = 0;
        decimal worst = 0;
        decimal best = decimal.MinValue;
        var seen = false;
        for (var h = 0; h < distribution.Stride; h++)
        for (var a = 0; a < distribution.Stride; a++)
        {
            var p = distribution.Prob(h, a);
            if (p <= 0) continue;
            decimal net = -stakeSum;
            foreach (var bet in bets)
                if (bet.Wins(h, a)) net += bet.Stake * bet.Odds;
            expected += (decimal)p * net;
            if (!seen || net < worst) worst = net;
            if (!seen || net > best) best = net;
            seen = true;
        }
        if (!seen) return JointRisk.Fail("empty-support");
        var maxLoss = seen && worst < 0 ? -worst : 0;
        return JointRisk.Create(expected, worst, maxLoss, -stakeSum, best);
    }

    static int GoalIndex(string code)
        => int.TryParse(code, out var g) && g >= 0 && g <= 7 ? g : -1;

    static int ScoreIndex(string code)
    {
        var list = global::AiFootBall.Core.Models.JcPlayDomain.Bf;
        for (var i = 0; i < list.Count; i++)
            if (list[i] == code) return i;
        return -1;
    }

    static int ResultIndex(string code) => code switch { "3" => 0, "1" => 1, "0" => 2, _ => -1 };

    static bool Wins(string play, string code, int home, int away, decimal? handicap)
    {
        if (play == "JQ")
        {
            var g = home + away;
            var band = g >= 7 ? 7 : g;
            return code == band.ToString(CultureInfo.InvariantCulture);
        }
        if (play == "SPF")
            return code == (home > away ? "3" : home == away ? "1" : "0");
        if (play == "NSPF" && handicap is decimal k)
            return code == MilkPickMath.HandicapCode(home, away, k);
        return PlayMargins.OfficialCode(home, away) == code;
    }

    public static SaveReceipt Commit(CandidateRequest request, AppendOnlyTicketLog log)
    {
        if (!Enabled) return new SaveReceipt("blocked:candidate-disabled", "");
        return CommitCore(request, log, allowTestBinding: false, failAfterReserve: false);
    }

    internal static SaveReceipt CommitForTest(CandidateRequest request, AppendOnlyTicketLog log, bool failAfterReserve = false)
        => CommitCore(request, log, allowTestBinding: true, failAfterReserve);

    static SaveReceipt CommitCore(CandidateRequest request, AppendOnlyTicketLog log, bool allowTestBinding, bool failAfterReserve)
    {
        var decision = DecideCore(request, allowTestBinding);
        if (!decision.Selected) return new SaveReceipt("blocked:" + decision.Status, "");
        if (failAfterReserve)
        {
            request.Ledger?.Store.Abort(request.ReservationId);
            return new SaveReceipt("save-failed", request.ReservationId);
        }
        SaveReceipt receipt;
        try
        {
            receipt = log.Append(new TicketRecord
            {
                Id = request.ReservationId,
                Play = request.Play,
                Option = request.OptionCode,
                MatchId = request.MatchId,
                Odds = request.Odds,
                Stake = request.Stake,
                Lane = "official",
                DecisionAt = request.DecisionTime,
                Kickoff = request.Kickoff,
                Probability = decision.Probability,
                Handicap = request.Handicap ?? 0m,
                ModelVersion = request.ModelVersion,
                CodeHash = request.CodeHash,
                RuleVersion = CandidateProtocol.RuleVersion,
                EvidenceId = request.Calibrator?.EvidenceId ?? ""
            });
        }
        catch (IOException)
        {
            var known = false;
            try { known = log.ContainsId(request.ReservationId); }
            catch (IOException) { known = false; }
            if (!known) request.Ledger?.Store.Abort(request.ReservationId);
            return new SaveReceipt(known ? "save-unmarked" : "save-failed", request.ReservationId);
        }
        catch (InvalidDataException)
        {
            return new SaveReceipt("blocked:ticket-log-corrupt", request.ReservationId);
        }
        if (receipt.Saved || receipt.Status == "idempotent")
        {
            try
            {
                var before = request.Ledger?.Store.State(request.ReservationId);
                request.Ledger?.Store.MarkSaved(request.ReservationId);
                if (receipt.Status == "idempotent" && before == "reserved")
                    return new SaveReceipt("recovered", request.ReservationId);
            }
            catch (IOException)
            {
                return new SaveReceipt("save-unmarked", request.ReservationId);
            }
        }
        else if (!log.ContainsId(request.ReservationId))
            request.Ledger?.Store.Abort(request.ReservationId, log);
        return receipt;
    }

    public static string Save(string lane, CandidateDecision decision)
    {
        if (!string.Equals(lane, "research", StringComparison.Ordinal))
            return "blocked:official-save-disabled";
        if (!decision.Selected)
            return "blocked:" + decision.Status;
        return "research-saved";
    }

    public static string Settle(string play, string code, int? home, int? away, decimal? handicap)
    {
        if (home is null || away is null) return "unsettled";
        if (play == "BQ") return "void:half-full-joint-missing";
        var hit = Wins(play, code, home.Value, away.Value, handicap);
        return hit ? "hit" : "miss";
    }
}

public sealed class OutcomeBet
{
    public OutcomeBet(decimal stake, decimal odds, Func<int, int, bool> wins)
    {
        Stake = stake;
        Odds = odds;
        Wins = wins;
    }

    public decimal Stake { get; }
    public decimal Odds { get; }
    public Func<int, int, bool> Wins { get; }
}

public sealed class JointRisk
{
    JointRisk(string status, decimal expected, decimal worstNet, decimal maxLoss, decimal allLose, decimal best)
    {
        Status = status;
        Expected = expected;
        WorstNet = worstNet;
        MaxLoss = maxLoss;
        AllLose = allLose;
        Best = best;
    }

    public static JointRisk Fail(string status) => new(status, 0, 0, 0, 0, 0);
    public static JointRisk Create(decimal expected, decimal worstNet, decimal maxLoss, decimal allLose, decimal best)
        => new("ok", expected, worstNet, maxLoss, allLose, best);
    public string Status { get; }
    public bool Ok => Status == "ok";
    public decimal Expected { get; }
    /// <summary>最坏格子的净收益。亏损为负，不能拿去和限额比。</summary>
    public decimal WorstNet { get; }
    /// <summary>最坏格子亏掉的本金，非负。限额只和这个数比。</summary>
    public decimal MaxLoss { get; }
    public decimal AllLose { get; }
    public decimal Best { get; }
}

public sealed class CandidateRequest
{
    public TemperatureFit? Calibrator { get; init; }
    public SupportDistribution? Distribution { get; init; }
    public string Play { get; init; } = "";
    public string OptionCode { get; init; } = "";
    public string ModelVersion { get; init; } = "";
    public string CodeHash { get; init; } = "";
    public decimal? Handicap { get; init; }
    public string MatchKey { get; init; } = "";
    public string DayKey { get; init; } = "";
    public string ReservationId { get; init; } = "";
    public long? ExposureVersion { get; init; }
    public decimal ObservedDayUsed { get; init; }
    public long MatchId { get; init; }
    public IReadOnlyList<OutcomeBet>? OpenBets { get; init; }
    public CandidateLedger? Ledger { get; init; }
    public decimal Odds { get; init; }
    public decimal Stake { get; init; }
    public decimal SameMatchStake { get; init; }
    public decimal DayStake { get; init; }
    public decimal? StakeLimit { get; init; }
    public decimal? DayLimit { get; init; }
    public decimal MinNetEv { get; init; }
    public DateTime Kickoff { get; init; }
    public DateTime DecisionTime { get; init; }
    public bool Sellable { get; init; }
}

public sealed class CandidateDecision
{
    public CandidateDecision(string status, decimal ev, decimal exposure, double probability)
    {
        Status = status;
        Ev = ev;
        Exposure = exposure;
        Probability = probability;
    }

    public static CandidateDecision Blocked(string status) => new(status, 0, 0, 0);

    public string Status { get; }
    public bool Selected => Status == "selected";
    public decimal Ev { get; }
    public decimal Exposure { get; }
    public double Probability { get; }
}

/// <summary>
/// 冻结在看见任何外层测试结果之前。缺历史归档时评估器只返回 blocked，不换窗口。
/// </summary>
public static class CandidateProtocol
{
    /// <summary>校准窗最少场次。正式启用必经 TemperatureCalibrator.Fit，与外层门槛同步为 400；达到数量不等于跳过其他校验。</summary>
    public const int MinCalibrationMatches = 400;
    /// <summary>正式新算法外层样本门槛。达到数量门槛不等于跳过其他校验。</summary>
    public const int MinTestMatches = 400;
    public const double LogLossMargin = 0.002;
    public const double AlphaInnerDefault = 0.01;
    /// <summary>生产绑定证据 ID。无合格未见证据时保持空；空则 calibrator-not-bound。</summary>
    public const string BoundEvidenceId = "";
    public const string TestEvidenceId = "test-only";
    public const string RuleVersion = "candidate-rule-v1";
    public static readonly double[] TemperatureGrid =
    {
        0.70, 0.80, 0.90, 1.00, 1.10, 1.25, 1.40, 1.60, 2.00
    };

    public const string QueryTemplate =
        "SET NOCOUNT ON; SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; "
        + "SELECT m.MatchId, m.LeagueName, m.HomeTeamName, m.AwayTeamName, m.MatchTime, "
        + "m.HomeScore, m.AwayScore, m.HalfHomeScore, m.HalfAwayScore, m.Status, "
        + "h.PlayType, h.OptionCode, h.Odds, h.CapturedAt "
        + "FROM dbo.FbMatch m JOIN dbo.FbJcOddsHistory h ON h.MatchId=m.MatchId "
        + "WHERE m.Status=3 AND m.MatchTime>=@trainFrom AND m.MatchTime<@testTo "
        + "AND h.CapturedAt<=DATEADD(minute,-150,m.MatchTime) AND h.Odds>1;";
}

public static class TimeSplit
{
    public static string Check(IReadOnlyList<(string MatchId, DateTime At, string Window, DateTime OddsAt, DateTime Kickoff)> rows)
    {
        string? prev = null;
        DateTime prevAt = DateTime.MinValue;
        var owner = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.At < prevAt) return "time-reversed";
            if (prev is not null && row.Window != prev && row.At < prevAt) return "window-overlap";
            if (prev is not null && row.Window != prev)
            {
                var order = WindowOrder(prev) < WindowOrder(row.Window);
                if (!order) return "window-order";
            }
            prev = row.Window;
            prevAt = row.At;
            if (owner.TryGetValue(row.MatchId, out var w) && w != row.Window) return "match-leak";
            owner[row.MatchId] = row.Window;
            if (row.OddsAt > row.Kickoff) return "odds-after-kickoff";
        }
        return "ok";
    }

    static int WindowOrder(string window) => window switch
    {
        "train" => 0,
        "calibration" => 1,
        "test" => 2,
        _ => 9
    };
}

public static partial class CandidateArchive
{
    public const string ResultPending = "-";
    /// <summary>训练名册 TSV：matchId / at / oddsAt / kickoff。空文件仅表示交叉清单为空，不是校准完成证明。</summary>
    public const string TrainRosterFileName = "train-roster.tsv";
    public const string ResultsFileName = "results.log";
    public const string WindowMetaFileName = "window-meta.txt";

    /// <summary>
    /// 单文件评估。回溯永远不是合格；仅 kind=predeclared-unseen 且时间链/指纹完整时返回 predeclared-unseen:ok。
    /// 声明行不得携带赛果；单文件 ok 不等于 400 场窗合格。
    /// </summary>
    public static string Evaluate(string? archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            return "blocked:archive-missing";
        var text = File.ReadAllText(archivePath);
        if (!text.Contains("runId=", StringComparison.Ordinal) || !text.Contains("lambdaHome=", StringComparison.Ordinal))
            return "blocked:archive-incomplete";
        if (text.Contains("kind=retrospective", StringComparison.Ordinal))
            return "retrospective:not-a-pass";
        var fields = ReadFields(text);
        if (fields == null) return "blocked:duplicate-field";
        return EvaluateFields(fields);
    }

    /// <summary>
    /// 写入训练名册（含逐场真实 At/OddsAt/Kickoff）。仅 matchId 列表已废弃。
    /// 空序列仍落盘文件（仅表头），TrainRosterHash 可复算；不得据此声称校准已完成 400 场。
    /// </summary>
    public static string WriteTrainRoster(string directory, IEnumerable<TrainRosterRow> rows)
    {
        Directory.CreateDirectory(directory);
        var list = rows.ToList();
        var seen = new HashSet<long>();
        var canon = new List<string>();
        foreach (var row in list.OrderBy(r => r.MatchId))
        {
            if (!seen.Add(row.MatchId)) return "blocked:train-roster-duplicate";
            if (string.IsNullOrWhiteSpace(row.At) || string.IsNullOrWhiteSpace(row.OddsAt)
                || string.IsNullOrWhiteSpace(row.Kickoff))
                return "blocked:train-times-missing";
            if (!TryParseArchiveTime(row.At, out var at)
                || !TryParseArchiveTime(row.OddsAt, out var oddsAt)
                || !TryParseArchiveTime(row.Kickoff, out var kickoff))
                return "blocked:train-times-invalid";
            if (oddsAt > kickoff) return "blocked:time-split|odds-after-kickoff";
            // 禁止三列相等且像用同一 cutoff 代填：At、OddsAt、Kickoff 必须可区分角色（允许 At==OddsAt，但 Kickoff 必须严格晚于 OddsAt）。
            if (at == oddsAt && oddsAt == kickoff)
                return "blocked:train-times-placeholder";
            if (kickoff <= oddsAt) return "blocked:train-times-invalid";
            canon.Add(FormatTrainCanon(row.MatchId, row.At, row.OddsAt, row.Kickoff));
        }
        var path = Path.Combine(directory, TrainRosterFileName);
        var lines = new List<string> { "matchId\tat\toddsAt\tkickoff" };
        lines.AddRange(canon);
        File.WriteAllLines(path, lines);
        return "train-roster:ok|count=" + seen.Count.ToString(CultureInfo.InvariantCulture)
               + "|trainRosterHash=" + Hash(string.Join('\n', canon));
    }

    static string FormatTrainCanon(long matchId, string at, string oddsAt, string kickoff)
        => matchId.ToString(CultureInfo.InvariantCulture) + "\t" + at + "\t" + oddsAt + "\t" + kickoff;

    public static string RecomputeTrainRosterHash(string directory)
    {
        var parsed = TryReadTrainRoster(directory, out var rows, out var err);
        if (!parsed) return "";
        var canon = rows.OrderBy(r => r.MatchId)
            .Select(r => FormatTrainCanon(r.MatchId, r.At, r.OddsAt, r.Kickoff)).ToList();
        return Hash(string.Join('\n', canon));
    }

    static bool TryReadTrainRoster(string directory, out List<TrainRosterRow> rows, out string error)
    {
        rows = new List<TrainRosterRow>();
        error = "";
        var path = Path.Combine(directory, TrainRosterFileName);
        if (!File.Exists(path))
        {
            error = "blocked:train-roster-missing";
            return false;
        }
        var seen = new HashSet<long>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("matchId\t", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = line.Split('\t');
            if (parts.Length < 4)
            {
                error = "blocked:train-times-missing";
                return false;
            }
            if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mid))
            {
                error = "blocked:train-roster-invalid";
                return false;
            }
            if (string.IsNullOrWhiteSpace(parts[1]) || string.IsNullOrWhiteSpace(parts[2])
                || string.IsNullOrWhiteSpace(parts[3]))
            {
                error = "blocked:train-times-missing";
                return false;
            }
            if (!seen.Add(mid))
            {
                error = "blocked:train-roster-duplicate";
                return false;
            }
            rows.Add(new TrainRosterRow { MatchId = mid, At = parts[1], OddsAt = parts[2], Kickoff = parts[3] });
        }
        return true;
    }

    /// <summary>
    /// 窗级评估（验证计数用）：声明独立 matchId≥400；训练名册防交叉 + 真实逐场时间进 TimeSplit；
    /// TrainRosterHash 进入摘要。空名册 ≠ 校准完成。不含赛果（见 EvaluateResults）。
    /// </summary>
    public static string EvaluateWindow(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return "blocked:archive-missing";
        var log = Path.Combine(directory, "archive.log");
        if (!File.Exists(log)) return "blocked:archive-missing";
        if (!TryReadTrainRoster(directory, out var trainRows, out var rosterErr))
            return rosterErr;
        var lines = File.ReadAllLines(log);
        if (lines.Length == 0) return "blocked:archive-incomplete";

        var trainIds = new HashSet<long>(trainRows.Select(r => r.MatchId));
        var trainCanon = trainRows.OrderBy(r => r.MatchId)
            .Select(r => FormatTrainCanon(r.MatchId, r.At, r.OddsAt, r.Kickoff)).ToList();
        var trainRosterHash = Hash(string.Join('\n', trainCanon));

        var matchIds = new HashSet<long>();
        var identityHashes = new List<string>();
        var splitRows = new List<(string MatchId, DateTime At, string Window, DateTime OddsAt, DateTime Kickoff)>();
        string? evidenceId = null;
        string? sampleRef = null;
        string? modelVersion = null;
        string? codeVersion = null;
        string? modelHash = null;
        string? codeHash = null;
        string? ruleHash = null;
        DateTime? trainCutoff = null;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Contains("kind=retrospective", StringComparison.Ordinal))
                return "retrospective:not-a-pass";
            var fields = ReadFields(line);
            if (fields == null) return "blocked:duplicate-field";
            var one = EvaluateFields(fields);
            if (one != "predeclared-unseen:ok") return one;
            if (!long.TryParse(fields["matchId"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var matchId))
                return "blocked:archive-incomplete";
            if (trainIds.Contains(matchId))
                return "blocked:train-test-match-leak";
            if (!TryParseArchiveTime(fields["asOf"], out var predictedAt)
                || !TryParseArchiveTime(fields["oddsCapturedAt"], out var oddsAt)
                || !TryParseArchiveTime(fields["kickoff"], out var kickoff)
                || !TryParseArchiveTime(fields["trainCutoff"], out var rowTrainCutoff))
                return "blocked:time-invalid";
            if (trainCutoff is null) trainCutoff = rowTrainCutoff;
            else if (trainCutoff.Value != rowTrainCutoff)
                return "blocked:train-cutoff-mismatch";
            matchIds.Add(matchId);
            identityHashes.Add(fields["identityHash"]);
            splitRows.Add((matchId.ToString(CultureInfo.InvariantCulture), predictedAt, "test", oddsAt, kickoff));

            var eid = fields["evidenceId"];
            var window = fields["sampleRef"];
            var model = fields["modelVersion"];
            var code = fields["codeVersion"];
            var mHash = fields["modelHash"];
            var cHash = fields["codeHash"];
            var rHash = fields["ruleHash"];
            if (evidenceId is null) evidenceId = eid;
            else if (!string.Equals(evidenceId, eid, StringComparison.Ordinal))
                return "blocked:evidence-id-mismatch";
            if (sampleRef is null) sampleRef = window;
            else if (!string.Equals(sampleRef, window, StringComparison.Ordinal))
                return "blocked:sample-window-mismatch";
            if (modelVersion is null) modelVersion = model;
            else if (!string.Equals(modelVersion, model, StringComparison.Ordinal))
                return "blocked:model-version-mismatch";
            if (codeVersion is null) codeVersion = code;
            else if (!string.Equals(codeVersion, code, StringComparison.Ordinal))
                return "blocked:code-version-mismatch";
            if (modelHash is null) modelHash = mHash;
            else if (!string.Equals(modelHash, mHash, StringComparison.Ordinal))
                return "blocked:model-fingerprint-mismatch";
            if (codeHash is null) codeHash = cHash;
            else if (!string.Equals(codeHash, cHash, StringComparison.Ordinal))
                return "blocked:code-fingerprint-mismatch";
            if (ruleHash is null) ruleHash = rHash;
            else if (!string.Equals(ruleHash, rHash, StringComparison.Ordinal))
                return "blocked:rule-fingerprint-mismatch";
        }

        if (matchIds.Count < CandidateProtocol.MinTestMatches)
            return "blocked:insufficient-matches|" + matchIds.Count.ToString(CultureInfo.InvariantCulture);

        // 训练场：真实逐场时间进 TimeSplit(train)；禁止 cutoff 代填。
        foreach (var tr in trainRows.OrderBy(r => r.MatchId))
        {
            if (!TryParseArchiveTime(tr.At, out var at)
                || !TryParseArchiveTime(tr.OddsAt, out var oAt)
                || !TryParseArchiveTime(tr.Kickoff, out var ko))
                return "blocked:train-times-invalid";
            if (at == oAt && oAt == ko) return "blocked:train-times-placeholder";
            if (oAt > ko) return "blocked:time-split|odds-after-kickoff";
            if (trainCutoff is not null && ko >= trainCutoff.Value)
                return "blocked:train-after-cutoff";
            if (trainCutoff is not null && at >= trainCutoff.Value)
                return "blocked:train-time-leak";
            splitRows.Add((tr.MatchId.ToString(CultureInfo.InvariantCulture), at, "train", oAt, ko));
        }
        splitRows.Sort((a, b) =>
        {
            var wo = WindowOrderKey(a.Window).CompareTo(WindowOrderKey(b.Window));
            return wo != 0 ? wo : a.At.CompareTo(b.At);
        });
        var split = TimeSplit.Check(splitRows);
        if (split != "ok") return "blocked:time-split|" + split;

        if (!string.Equals(modelHash, Hash(modelVersion ?? ""), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(codeHash, Hash(codeVersion ?? ""), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(ruleHash, Hash(CandidateProtocol.RuleVersion), StringComparison.OrdinalIgnoreCase))
            return "blocked:fingerprint-mismatch";

        identityHashes.Sort(StringComparer.Ordinal);
        var fileHash = Hash(string.Join('\n', identityHashes));
        return "predeclared-unseen:pass"
            + "|matches=" + matchIds.Count.ToString(CultureInfo.InvariantCulture)
            + "|evidenceId=" + evidenceId
            + "|fileHash=" + fileHash
            + "|window=" + sampleRef
            + "|model=" + modelVersion
            + "|code=" + codeVersion
            + "|modelFp=" + modelHash
            + "|codeFp=" + codeHash
            + "|ruleFp=" + ruleHash
            + "|rule=" + CandidateProtocol.RuleVersion
            + "|trainRoster=" + trainIds.Count.ToString(CultureInfo.InvariantCulture)
            + "|trainRosterHash=" + trainRosterHash;
    }

    static int WindowOrderKey(string window) => window switch
    {
        "train" => 0,
        "calibration" => 1,
        "test" => 2,
        _ => 9
    };

    /// <summary>
    /// 赛果评估（校准/外层评估用）：须全部声明场有且仅有一条一致赛果；孤儿/重复/冲突拒绝。
    /// </summary>
    public static string EvaluateResults(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return "blocked:archive-missing";
        var log = Path.Combine(directory, "archive.log");
        if (!File.Exists(log)) return "blocked:archive-missing";
        var declareIds = new HashSet<long>();
        var kickoffs = new Dictionary<long, DateTime>();
        foreach (var line in File.ReadAllLines(log))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Contains("kind=retrospective", StringComparison.Ordinal))
                return "retrospective:not-a-pass";
            var fields = ReadFields(line);
            if (fields == null) return "blocked:duplicate-field";
            if (!long.TryParse(fields.GetValueOrDefault("matchId") ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out var mid))
                return "blocked:archive-incomplete";
            declareIds.Add(mid);
            if (TryParseArchiveTime(fields.GetValueOrDefault("kickoff") ?? "", out var ko))
                kickoffs[mid] = ko;
        }
        var resultsPath = Path.Combine(directory, ResultsFileName);
        if (!File.Exists(resultsPath))
            return "blocked:results-missing";
        var byMatch = new Dictionary<long, (int Home, int Away, string SettledAt)>();
        var canon = new List<string>();
        foreach (var line in File.ReadAllLines(resultsPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = ReadFields(line);
            if (fields == null) return "blocked:results-duplicate-field";
            if (!long.TryParse(fields.GetValueOrDefault("matchId") ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out var mid)
                || !int.TryParse(fields.GetValueOrDefault("home") ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out var home)
                || !int.TryParse(fields.GetValueOrDefault("away") ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out var away)
                || string.IsNullOrWhiteSpace(fields.GetValueOrDefault("settledAt")))
                return "blocked:results-incomplete";
            if (!declareIds.Contains(mid)) return "blocked:results-orphan|" + mid.ToString(CultureInfo.InvariantCulture);
            if (!TryParseArchiveTime(fields["settledAt"], out var settled)) return "blocked:time-invalid";
            if (kickoffs.TryGetValue(mid, out var ko) && settled < ko) return "blocked:result-before-kickoff";
            if (byMatch.TryGetValue(mid, out var prev))
            {
                if (prev.Home == home && prev.Away == away
                    && string.Equals(prev.SettledAt, fields["settledAt"], StringComparison.Ordinal))
                    return "blocked:results-duplicate";
                return "blocked:results-conflict|" + mid.ToString(CultureInfo.InvariantCulture);
            }
            byMatch[mid] = (home, away, fields["settledAt"]);
            canon.Add("matchId=" + mid.ToString(CultureInfo.InvariantCulture)
                      + "\thome=" + home.ToString(CultureInfo.InvariantCulture)
                      + "\taway=" + away.ToString(CultureInfo.InvariantCulture)
                      + "\tsettledAt=" + fields["settledAt"]);
        }
        if (byMatch.Count != declareIds.Count)
            return "blocked:results-incomplete-coverage|" + byMatch.Count.ToString(CultureInfo.InvariantCulture)
                   + "/" + declareIds.Count.ToString(CultureInfo.InvariantCulture);
        canon.Sort(StringComparer.Ordinal);
        var resultsHash = Hash(string.Join('\n', canon));
        return "results:ok"
            + "|attached=" + byMatch.Count.ToString(CultureInfo.InvariantCulture)
            + "|resultsHash=" + resultsHash;
    }

    /// <summary>
    /// 对照磁盘窗重新 EvaluateWindow + EvaluateResults，与证据对象逐项核对（非仅 64 位格式）。
    /// </summary>
    public static string VerifyEvidenceAgainstWindow(CalibratorEvidence evidence, string directory)
    {
        if (evidence is null) return "evidence-missing";
        var win = EvaluateWindow(directory);
        if (!win.StartsWith("predeclared-unseen:pass|", StringComparison.Ordinal))
            return win.StartsWith("blocked:", StringComparison.Ordinal) || win.StartsWith("retrospective", StringComparison.Ordinal)
                ? win
                : "blocked:window-recheck-failed";
        var res = EvaluateResults(directory);
        if (!res.StartsWith("results:ok|", StringComparison.Ordinal))
            return res;
        var w = ParseStatusMap(win);
        var r = ParseStatusMap(res);
        var liveHash = RecomputeWindowFileHash(directory);
        if (!string.Equals(w.GetValueOrDefault("fileHash"), liveHash, StringComparison.OrdinalIgnoreCase))
            return "blocked:evidence-filehash-recheck";
        if (!string.Equals(evidence.FileHash, liveHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(evidence.FileHash, w.GetValueOrDefault("fileHash"), StringComparison.OrdinalIgnoreCase))
            return "evidence-filehash-mismatch";
        if (!string.Equals(evidence.EvidenceId, w.GetValueOrDefault("evidenceId"), StringComparison.Ordinal))
            return "evidence-id-mismatch";
        if (!string.Equals(evidence.SampleWindow, w.GetValueOrDefault("window"), StringComparison.Ordinal))
            return "evidence-window-mismatch";
        if (!string.Equals(evidence.ModelVersion, w.GetValueOrDefault("model"), StringComparison.Ordinal)
            || !string.Equals(evidence.CodeHash, w.GetValueOrDefault("code"), StringComparison.Ordinal)
            || !string.Equals(evidence.PolicyVersion, w.GetValueOrDefault("rule"), StringComparison.Ordinal))
            return "evidence-version-mismatch";
        if (!string.Equals(evidence.ModelFingerprint, w.GetValueOrDefault("modelFp"), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(evidence.CodeFingerprint, w.GetValueOrDefault("codeFp"), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(evidence.PolicyFingerprint, w.GetValueOrDefault("ruleFp"), StringComparison.OrdinalIgnoreCase))
            return "evidence-fingerprint-mismatch";
        if (!string.Equals(evidence.ModelFingerprint, Hash(evidence.ModelVersion), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(evidence.CodeFingerprint, Hash(evidence.CodeHash), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(evidence.PolicyFingerprint, Hash(CandidateProtocol.RuleVersion), StringComparison.OrdinalIgnoreCase))
            return "evidence-fingerprint-mismatch";
        if (!int.TryParse(w.GetValueOrDefault("matches"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var matches)
            || matches != evidence.Matches || matches < CandidateProtocol.MinTestMatches)
            return "evidence-matches-mismatch";
        if (!int.TryParse(r.GetValueOrDefault("attached"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var attached)
            || attached != evidence.ResultsAttached || attached != matches)
            return "evidence-results-mismatch";
        if (!string.Equals(evidence.ResultsHash, r.GetValueOrDefault("resultsHash"), StringComparison.OrdinalIgnoreCase))
            return "evidence-results-hash-mismatch";
        var liveTrainHash = RecomputeTrainRosterHash(directory);
        if (!string.Equals(evidence.TrainRosterHash, liveTrainHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(evidence.TrainRosterHash, w.GetValueOrDefault("trainRosterHash"), StringComparison.OrdinalIgnoreCase))
            return "evidence-train-roster-hash-mismatch";
        if (!int.TryParse(w.GetValueOrDefault("trainRoster"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var trainCount)
            || trainCount != evidence.TrainRosterCount)
            return "evidence-train-roster-count-mismatch";
        var metaLive = EvidenceWindowOffline.CheckWindowMetaIntegrity(directory);
        if (evidence.WindowMetaHash.Length > 0)
        {
            if (!metaLive.StartsWith("window-meta:integrity-ok|", StringComparison.Ordinal))
                return metaLive.StartsWith("blocked:", StringComparison.Ordinal) ? metaLive : "blocked:window-meta-integrity-mismatch";
            var liveMetaHash = EvidenceWindowOffline.ParseMetaHashFromIntegrityStatus(metaLive);
            if (!string.Equals(evidence.WindowMetaHash, liveMetaHash, StringComparison.OrdinalIgnoreCase))
                return "evidence-window-meta-hash-mismatch";
        }
        else if (metaLive.StartsWith("window-meta:integrity-ok|", StringComparison.Ordinal)
                 || metaLive.StartsWith("blocked:window-meta", StringComparison.Ordinal))
        {
            // 磁盘有冻结元数据时，证据必须携带 WindowMetaHash，防止静默绕过。
            if (File.Exists(Path.Combine(directory, WindowMetaFileName))
                || File.Exists(Path.Combine(directory, EvidenceWindowOffline.WindowMetaIntegrityFileName)))
                return "evidence-window-meta-hash-mismatch";
        }
        if (!string.Equals(evidence.Kind, CalibratorBinding.KindPredeclaredUnseen, StringComparison.Ordinal))
            return "evidence-kind-invalid";
        return "evidence:window-ok";
    }

    /// <summary>
    /// 建证：EvaluateWindow（计数）+ EvaluateResults（校准/评估赛果）后构造证据；对象字段必须能被 Verify 复核。
    /// </summary>
    public static bool TryBuildEvidence(string directory, out CalibratorEvidence evidence, out string status)
    {
        evidence = new CalibratorEvidence();
        status = EvaluateWindow(directory);
        if (!status.StartsWith("predeclared-unseen:pass|", StringComparison.Ordinal))
            return false;
        var map = ParseStatusMap(status);
        if (!int.TryParse(map.GetValueOrDefault("matches"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var matches)
            || matches < CandidateProtocol.MinTestMatches)
        {
            status = "blocked:evidence-summary-mismatch";
            return false;
        }
        var recomputed = RecomputeWindowFileHash(directory);
        var fileHash = map.GetValueOrDefault("fileHash") ?? "";
        if (!string.Equals(recomputed, fileHash, StringComparison.OrdinalIgnoreCase)
            || !CalibratorBinding.IsSha256Hex(fileHash))
        {
            status = "blocked:evidence-filehash-recheck";
            return false;
        }
        var resultsStatus = EvaluateResults(directory);
        if (!resultsStatus.StartsWith("results:ok|", StringComparison.Ordinal))
        {
            status = resultsStatus;
            return false;
        }
        var rmap = ParseStatusMap(resultsStatus);
        if (!int.TryParse(rmap.GetValueOrDefault("attached"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var attached)
            || attached != matches)
        {
            status = "blocked:evidence-results-mismatch";
            return false;
        }
        var model = map.GetValueOrDefault("model") ?? "";
        var code = map.GetValueOrDefault("code") ?? "";
        var modelFp = map.GetValueOrDefault("modelFp") ?? "";
        var codeFp = map.GetValueOrDefault("codeFp") ?? "";
        var ruleFp = map.GetValueOrDefault("ruleFp") ?? "";
        if (!string.Equals(modelFp, Hash(model), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(codeFp, Hash(code), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(ruleFp, Hash(CandidateProtocol.RuleVersion), StringComparison.OrdinalIgnoreCase))
        {
            status = "blocked:evidence-fingerprint-recheck";
            return false;
        }
        if (!int.TryParse(map.GetValueOrDefault("trainRoster"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var trainCount))
            trainCount = -1;
        var trainRosterHash = map.GetValueOrDefault("trainRosterHash") ?? "";
        if (!string.Equals(trainRosterHash, RecomputeTrainRosterHash(directory), StringComparison.OrdinalIgnoreCase)
            || !CalibratorBinding.IsSha256Hex(trainRosterHash))
        {
            status = "blocked:evidence-train-roster-recheck";
            return false;
        }
        var windowMetaHash = "";
        var metaLive = EvidenceWindowOffline.CheckWindowMetaIntegrity(directory);
        if (metaLive.StartsWith("window-meta:integrity-ok|", StringComparison.Ordinal))
            windowMetaHash = EvidenceWindowOffline.ParseMetaHashFromIntegrityStatus(metaLive);
        else if (metaLive.StartsWith("blocked:", StringComparison.Ordinal))
        {
            status = metaLive;
            return false;
        }
        evidence = new CalibratorEvidence
        {
            EvidenceId = map.GetValueOrDefault("evidenceId") ?? "",
            Kind = CalibratorBinding.KindPredeclaredUnseen,
            ModelVersion = model,
            CodeHash = code,
            PolicyVersion = map.GetValueOrDefault("rule") ?? CandidateProtocol.RuleVersion,
            SampleWindow = map.GetValueOrDefault("window") ?? "",
            FileHash = fileHash,
            Matches = matches,
            ModelFingerprint = modelFp,
            CodeFingerprint = codeFp,
            PolicyFingerprint = ruleFp,
            ResultsHash = rmap.GetValueOrDefault("resultsHash") ?? "",
            ResultsAttached = attached,
            TrainRosterHash = trainRosterHash,
            TrainRosterCount = trainCount,
            WindowMetaHash = windowMetaHash
        };
        if (evidence.EvidenceId.Length == 0 || evidence.SampleWindow.Length == 0
            || !CalibratorBinding.IsSha256Hex(evidence.ResultsHash))
        {
            status = "blocked:evidence-summary-mismatch";
            evidence = new CalibratorEvidence();
            return false;
        }
        var verify = VerifyEvidenceAgainstWindow(evidence, directory);
        if (verify != "evidence:window-ok")
        {
            status = verify;
            evidence = new CalibratorEvidence();
            return false;
        }
        status = status + "|resultsHash=" + evidence.ResultsHash
                 + "|attached=" + attached.ToString(CultureInfo.InvariantCulture)
                 + "|trainRosterHash=" + trainRosterHash;
        return true;
    }

    public static string RecomputeWindowFileHash(string directory)
    {
        var log = Path.Combine(directory, "archive.log");
        if (!File.Exists(log)) return "";
        var hashes = new List<string>();
        foreach (var line in File.ReadAllLines(log))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = ReadFields(line);
            if (fields == null || !fields.TryGetValue("identityHash", out var ih) || ih.Length == 0)
                return "";
            hashes.Add(ih);
        }
        hashes.Sort(StringComparer.Ordinal);
        return Hash(string.Join('\n', hashes));
    }

    /// <summary>赛果独立关联；不改写声明行 identity。拒孤儿、重复、冲突、早于开赛。</summary>
    public static string AttachResult(string directory, long matchId, int home, int away, string settledAt)
    {
        if (home < 0 || away < 0) return "blocked:result-invalid";
        if (!TryParseArchiveTime(settledAt, out var settled)) return "blocked:time-invalid";
        var log = Path.Combine(directory, "archive.log");
        if (!File.Exists(log)) return "blocked:archive-missing";
        Dictionary<string, string>? declare = null;
        foreach (var line in File.ReadAllLines(log))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = ReadFields(line);
            if (fields == null) continue;
            if (!fields.TryGetValue("matchId", out var mid) || mid != matchId.ToString(CultureInfo.InvariantCulture))
                continue;
            declare = fields;
            break;
        }
        if (declare == null) return "blocked:declare-missing";
        if (!TryParseArchiveTime(declare.GetValueOrDefault("kickoff") ?? "", out var kickoff))
            return "blocked:time-invalid";
        if (settled < kickoff) return "blocked:result-before-kickoff";
        var resultsPath = Path.Combine(directory, ResultsFileName);
        if (File.Exists(resultsPath))
        {
            foreach (var line in File.ReadAllLines(resultsPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var fields = ReadFields(line);
                if (fields == null) continue;
                if (!long.TryParse(fields.GetValueOrDefault("matchId") ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out var mid)
                    || mid != matchId) continue;
                if (int.TryParse(fields.GetValueOrDefault("home"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
                    && int.TryParse(fields.GetValueOrDefault("away"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var a)
                    && h == home && a == away
                    && string.Equals(fields.GetValueOrDefault("settledAt"), settledAt, StringComparison.Ordinal))
                    return "blocked:results-duplicate";
                return "blocked:results-conflict|" + matchId.ToString(CultureInfo.InvariantCulture);
            }
        }
        Directory.CreateDirectory(directory);
        var inv = CultureInfo.InvariantCulture;
        var row = "matchId=" + matchId.ToString(inv)
                  + "\thome=" + home.ToString(inv)
                  + "\taway=" + away.ToString(inv)
                  + "\tsettledAt=" + settledAt;
        File.AppendAllText(resultsPath, row + Environment.NewLine);
        return "result:attached";
    }

    static string EvaluateFields(IReadOnlyDictionary<string, string> fields)
    {
        if (!fields.TryGetValue("kind", out var kind)
            || !string.Equals(kind, CalibratorBinding.KindPredeclaredUnseen, StringComparison.Ordinal))
            return "blocked:not-a-predeclared-unseen-run";
        if (!fields.ContainsKey("runId") || !fields.ContainsKey("lambdaHome") || !fields.ContainsKey("matchId"))
            return "blocked:archive-incomplete";
        foreach (var key in new[]
                 {
                     "evidenceId", "ruleVersion", "modelVersion", "codeVersion", "modelHash", "codeHash", "ruleHash",
                     "asOf", "declaredAt", "oddsCapturedAt", "kickoff", "trainCutoff", "oddsSnapshot",
                     "sampleRef", "identityHash", "home", "away"
                 })
            if (!fields.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                return "blocked:archive-incomplete";
        // 声明不得携带赛果冒充赛前证据。
        if (!string.Equals(fields["home"], ResultPending, StringComparison.Ordinal)
            || !string.Equals(fields["away"], ResultPending, StringComparison.Ordinal))
            return "blocked:declare-carries-result";
        if (IsUnqualifiedEvidenceId(fields["evidenceId"]))
            return "blocked:evidence-id-unqualified";
        if (!string.Equals(fields["ruleVersion"], CandidateProtocol.RuleVersion, StringComparison.Ordinal))
            return "blocked:rule-version-mismatch";
        if (!string.Equals(fields["modelHash"], Hash(fields["modelVersion"]), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(fields["codeHash"], Hash(fields["codeVersion"]), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(fields["ruleHash"], Hash(fields["ruleVersion"]), StringComparison.OrdinalIgnoreCase))
            return "blocked:fingerprint-mismatch";
        var timing = CheckPredeclaredTiming(fields);
        if (timing != null) return timing;
        var integrity = FileIntegrity(fields);
        if (integrity != "integrity:ok") return integrity;
        return "predeclared-unseen:ok";
    }

    static string? CheckPredeclaredTiming(IReadOnlyDictionary<string, string> fields)
    {
        if (!TryParseArchiveTime(fields["declaredAt"], out var declaredAt)
            || !TryParseArchiveTime(fields["oddsCapturedAt"], out var oddsAt)
            || !TryParseArchiveTime(fields["asOf"], out var predictedAt)
            || !TryParseArchiveTime(fields["kickoff"], out var kickoff)
            || !TryParseArchiveTime(fields["trainCutoff"], out var trainCutoff))
            return "blocked:time-invalid";
        // 赛后补盘、声明过晚优先拒绝；再校验声明→盘口→预测→开赛顺序与 TimeSplit。
        if (oddsAt > kickoff) return "blocked:time-split|odds-after-kickoff";
        if (declaredAt >= kickoff) return "blocked:declare-after-kickoff";
        if (predictedAt >= kickoff) return "blocked:predict-after-kickoff";
        if (declaredAt > oddsAt) return "blocked:declare-after-odds";
        if (oddsAt > predictedAt) return "blocked:odds-after-predict";
        if (declaredAt > predictedAt) return "blocked:declare-after-predict";
        if (trainCutoff >= kickoff || trainCutoff >= predictedAt) return "blocked:train-cutoff-leak";
        var split = TimeSplit.Check(new[]
        {
            (fields["matchId"], predictedAt, "test", oddsAt, kickoff)
        });
        if (split != "ok") return "blocked:time-split|" + split;
        return null;
    }

    internal static bool TryParseArchiveTime(string text, out DateTime value)
        => DateTime.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out value);

    static bool IsUnqualifiedEvidenceId(string evidenceId)
    {
        if (string.IsNullOrWhiteSpace(evidenceId)) return true;
        if (string.Equals(evidenceId, "retrospective-not-unseen", StringComparison.Ordinal)) return true;
        if (string.Equals(evidenceId, DecisionDelivery.ApprovedEvidence, StringComparison.Ordinal)) return true;
        if (evidenceId.Contains("retrospective", StringComparison.OrdinalIgnoreCase)) return true;
        if (evidenceId.Contains("seen-backtest", StringComparison.OrdinalIgnoreCase)) return true;
        if (evidenceId.StartsWith("seen-", StringComparison.OrdinalIgnoreCase)) return true;
        if (evidenceId.Contains("-seen-", StringComparison.OrdinalIgnoreCase)) return true;
        if (evidenceId.EndsWith("-seen", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static Dictionary<string, string> ParseStatusMap(string status)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in status.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            map[part[..eq]] = part[(eq + 1)..];
        }
        return map;
    }

    public static string Hash(string text)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text ?? ""));
        return Convert.ToHexString(bytes);
    }

    public const string CheckedFields =
        "play,option,odds,oddsSnapshot,handicap,lambdaHome,lambdaAway,rho,temperature,matchId,runId,model,code,rule,evidence,trainCutoff,declaredAt,oddsCapturedAt,kickoff";

    public static IReadOnlyList<string> SearchRoots { get; set; } = Array.Empty<string>();

    static readonly string[] IdentityKeys =
    {
        "asOf", "away", "codeHash", "codeVersion", "declaredAt", "evidenceHash", "evidenceId", "handicap", "home",
        "kickoff", "lambdaAway", "lambdaHome", "manifestHash", "matchId", "modelHash", "modelVersion", "odds",
        "oddsCapturedAt", "oddsSnapshot", "option", "pFull", "play", "rho", "ruleHash", "ruleVersion", "runId",
        "sourceKey", "temperature", "trainCutoff"
    };

    public static string IdentityHash(IReadOnlyDictionary<string, string> fields)
    {
        var body = string.Join('\n', IdentityKeys.Select(key => key + "=" + (fields.TryGetValue(key, out var value) ? value : "")));
        return Hash(body);
    }

    public static string FormatLine(ArchiveRow row)
        => FormatLine(row, kind: "retrospective");

    public static string FormatLine(ArchiveRow row, string kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) throw new InvalidDataException("archive-kind-missing");
        var inv = CultureInfo.InvariantCulture;
        var predeclared = string.Equals(kind, CalibratorBinding.KindPredeclaredUnseen, StringComparison.Ordinal);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["kind"] = kind,
            ["runId"] = row.RunId,
            ["matchId"] = row.MatchId.ToString(inv),
            ["lambdaHome"] = row.LambdaHome.ToString("0.00000000", inv),
            ["lambdaAway"] = row.LambdaAway.ToString("0.00000000", inv),
            ["rho"] = row.Rho.ToString("0.000", inv),
            ["temperature"] = row.Temperature.ToString("0.00", inv),
            // 预声明行强制不写赛果；回溯行仍可带完场比分。
            ["home"] = predeclared ? ResultPending : row.Home.ToString(inv),
            ["away"] = predeclared ? ResultPending : row.Away.ToString(inv),
            ["pFull"] = row.StoredProbability.ToString("0.00000000", inv),
            ["modelVersion"] = row.ModelVersion,
            ["modelHash"] = Hash(row.ModelVersion),
            ["codeVersion"] = row.CodeHash,
            ["codeHash"] = Hash(row.CodeHash),
            ["ruleVersion"] = row.RuleVersion,
            ["ruleHash"] = Hash(row.RuleVersion),
            ["evidenceId"] = row.EvidenceId,
            ["evidenceHash"] = Hash(row.EvidenceId),
            ["aliasHash"] = row.AliasHash,
            ["sampleRef"] = row.SampleRef,
            ["play"] = row.Play,
            ["option"] = row.Option,
            ["odds"] = row.Odds.ToString("0.000", inv),
            ["oddsSnapshot"] = row.OddsSnapshot,
            ["manifestHash"] = row.ManifestHash,
            ["handicap"] = row.HandicapText.Length == 0 ? row.Handicap.ToString("0.000", inv) : row.HandicapText,
            ["asOf"] = row.AsOf,
            ["declaredAt"] = row.DeclaredAt,
            ["oddsCapturedAt"] = row.OddsCapturedAt,
            ["kickoff"] = row.Kickoff,
            ["trainCutoff"] = row.TrainCutoff,
            ["lane"] = row.Lane,
            ["decision"] = row.Decision,
            ["reason"] = row.Reason,
            ["risk"] = row.RiskState
        };
        foreach (var pair in fields)
            if (pair.Value.Contains('\t') || pair.Value.Contains('\n') || pair.Value.Contains('\r'))
                throw new InvalidDataException("archive-field-delimiter");
        fields["sourceKey"] = Hash(string.Join('\t', fields["matchId"], fields["runId"], fields["asOf"], fields["handicap"], fields["oddsSnapshot"], fields["trainCutoff"], fields["play"]));
        fields["identityHash"] = IdentityHash(fields);
        return string.Join('\t', fields.Select(pair => pair.Key + "=" + pair.Value));
    }

    public static string WriteRetrospective(string directory, ArchiveRow row)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "archive-" + row.RunId + "-" + row.MatchId.ToString(CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N") + ".txt");
        var line = FormatLine(row);
        File.WriteAllText(path, line + Environment.NewLine);
        File.AppendAllText(Path.Combine(directory, "archive.log"), line + Environment.NewLine);
        return path;
    }

    /// <summary>
    /// 合法赛前未见声明写入。强制 kind=predeclared-unseen 且不落赛果；时间链与指纹不合格拒绝落盘。
    /// </summary>
    public static string WritePredeclaredUnseen(string directory, ArchiveRow row)
    {
        var gate = PredeclaredGate(row);
        if (gate != null) throw new InvalidDataException(gate);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "archive-" + row.RunId + "-" + row.MatchId.ToString(CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N") + ".txt");
        var line = FormatLine(row, CalibratorBinding.KindPredeclaredUnseen);
        File.WriteAllText(path, line + Environment.NewLine);
        File.AppendAllText(Path.Combine(directory, "archive.log"), line + Environment.NewLine);
        return path;
    }

    static string? PredeclaredGate(ArchiveRow row)
    {
        if (string.IsNullOrWhiteSpace(row.EvidenceId)) return "evidence-id-missing";
        if (IsUnqualifiedEvidenceId(row.EvidenceId)) return "evidence-id-unqualified";
        if (!string.Equals(row.RuleVersion, CandidateProtocol.RuleVersion, StringComparison.Ordinal))
            return "rule-version-mismatch";
        if (string.IsNullOrWhiteSpace(row.ModelVersion) || string.IsNullOrWhiteSpace(row.CodeHash))
            return "version-missing";
        if (string.IsNullOrWhiteSpace(row.AsOf) || string.IsNullOrWhiteSpace(row.TrainCutoff)
            || string.IsNullOrWhiteSpace(row.OddsSnapshot)
            || string.IsNullOrWhiteSpace(row.DeclaredAt)
            || string.IsNullOrWhiteSpace(row.OddsCapturedAt)
            || string.IsNullOrWhiteSpace(row.Kickoff))
            return "prematch-snapshot-missing";
        if (string.IsNullOrWhiteSpace(row.SampleRef)) return "sample-window-missing";
        if (string.IsNullOrWhiteSpace(row.RunId)) return "run-id-missing";
        var probe = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["matchId"] = row.MatchId.ToString(CultureInfo.InvariantCulture),
            ["declaredAt"] = row.DeclaredAt,
            ["oddsCapturedAt"] = row.OddsCapturedAt,
            ["asOf"] = row.AsOf,
            ["kickoff"] = row.Kickoff,
            ["trainCutoff"] = row.TrainCutoff
        };
        return CheckPredeclaredTiming(probe);
    }

    public static IReadOnlyList<string> Replay(string directory)
    {
        var log = Path.Combine(directory, "archive.log");
        if (!File.Exists(log)) return Array.Empty<string>();
        return File.ReadAllLines(log);
    }

    public static string Recompute(string archivePath, string? approvedManifestPath = null)
    {
        var status = Evaluate(archivePath);
        if (!status.StartsWith("retrospective", StringComparison.Ordinal) && status.StartsWith("blocked", StringComparison.Ordinal))
            return status;
        var fields = ReadFields(File.ReadAllText(archivePath));
        if (fields == null) return "blocked:duplicate-field";
        foreach (var key in fields.Keys)
            if (key.Equals("verified", StringComparison.OrdinalIgnoreCase)
                || key.Equals("approved", StringComparison.OrdinalIgnoreCase))
                return "blocked:unauthenticated-claim";
        var integrity = FileIntegrity(fields);
        if (integrity != "integrity:ok") return integrity;
        var lambdaHome = ReadDouble(fields, "lambdaHome");
        var lambdaAway = ReadDouble(fields, "lambdaAway");
        var rho = ReadDouble(fields, "rho");
        var temperature = ReadDouble(fields, "temperature");
        var homeText = fields.GetValueOrDefault("home") ?? "";
        var awayText = fields.GetValueOrDefault("away") ?? "";
        // 预声明行无赛果：完整性已通过即止，赛果走 AttachResult。
        if (string.Equals(homeText, ResultPending, StringComparison.Ordinal)
            || string.Equals(awayText, ResultPending, StringComparison.Ordinal))
            return "predeclared-unseen:awaiting-result";
        var home = (int)ReadDouble(fields, "home");
        var away = (int)ReadDouble(fields, "away");
        var stored = ReadDouble(fields, "pFull");
        if (string.Equals(fields.GetValueOrDefault("decision"), "ticket", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(approvedManifestPath)) return "blocked:approved-package-missing";
            var approval = RequireApproved(fields, approvedManifestPath);
            if (approval != null) return approval;
            var marketStatus = MarketStatus(fields.GetValueOrDefault("play") ?? "", fields.GetValueOrDefault("oddsSnapshot") ?? "");
            if (marketStatus != null) return marketStatus;
            var bound = BindOriginal(fields, approvedManifestPath);
            if (bound != null) return bound;
            var snapshot = MatchSnapshot(fields, approvedManifestPath);
            if (snapshot != null) return snapshot;
            return RecomputeDecision(fields, lambdaHome, lambdaAway, rho, temperature, stored);
        }
        var dist = FullSupportDixonColes.Create(lambdaHome, lambdaAway, rho);
        var scaled = TemperatureCalibrator.Apply(dist, temperature);
        var one = DistributionScore.One(scaled, home, away);
        if (!one.Ok) return "recompute-failed:" + one.Status;
        var gap = Math.Abs(one.PTrue - stored);
        return gap <= 1e-6 ? "recomputed" : "recompute-mismatch:" + gap.ToString("0.00000000", CultureInfo.InvariantCulture);
    }

    static string RecomputeDecision(Dictionary<string, string> fields, double lambdaHome, double lambdaAway, double rho, double temperature, double stored)
    {
        foreach (var key in new[] { "reason", "evidenceId", "ruleVersion", "risk", "modelVersion", "codeVersion", "play", "option" })
        {
            var value = fields.GetValueOrDefault(key) ?? "";
            if (value.Length == 0 || value == "sample-not-production") return "blocked:decision-version-missing";
        }
        if (!string.Equals(fields.GetValueOrDefault("modelHash"), Hash(fields["modelVersion"]), StringComparison.Ordinal)
            || !string.Equals(fields.GetValueOrDefault("codeHash"), Hash(fields["codeVersion"]), StringComparison.Ordinal)
            || !string.Equals(fields.GetValueOrDefault("ruleHash"), Hash(fields["ruleVersion"]), StringComparison.Ordinal)
            || !string.Equals(fields.GetValueOrDefault("evidenceHash"), Hash(fields["evidenceId"]), StringComparison.Ordinal))
            return "blocked:version-tampered";
        if (!string.Equals(fields["ruleVersion"], DecisionDelivery.ApprovedRule, StringComparison.Ordinal))
            return "blocked:rule-not-approved";
        if (!string.Equals(fields["evidenceId"], DecisionDelivery.ApprovedEvidence, StringComparison.Ordinal))
            return "blocked:evidence-not-approved";
        if (Math.Abs(temperature - 1.1) > 1e-9 || Math.Abs(rho - global::AiFootBall.Core.Models.ScorelineModel.DefaultRho) > 1e-9)
            return "blocked:parameter-not-approved";
        if (!fields.TryGetValue("oddsSnapshot", out var snapshot) || snapshot.Length == 0)
            return "blocked:market-missing";
        List<(string Code, decimal Odds)> market;
        try { market = ParseSnapshot(snapshot); }
        catch (FormatException) { return "blocked:market-missing"; }
        var play = fields["play"];
        var marketStatus = MarketStatus(play, market);
        if (marketStatus != null) return marketStatus;
        var dist = FullSupportDixonColes.Create(lambdaHome, lambdaAway, rho);
        var scaled = TemperatureCalibrator.Apply(dist, temperature);
        if (!scaled.Ok) return "recompute-failed:" + scaled.Status;
        var margin = play switch
        {
            "JQ" => PlayMargins.AggregateGoals(scaled),
            "SPF" => PlayMargins.AggregateResult(scaled),
            "NSPF" => PlayMargins.AggregateHandicap(scaled, (decimal)ReadDouble(fields, "handicap")),
            "BF" => PlayMargins.AggregateScores(scaled),
            _ => Margin.Fail("play-not-in-observation")
        };
        if (!margin.Ok) return "recompute-failed:" + margin.Status;
        string chosen = "";
        double chosenP = -1;
        decimal chosenOdds = 0;
        foreach (var (code, price) in market)
        {
            if (price <= 1m) return "blocked:decision-odds-missing";
            var index = OptionIndex(play, code);
            if (index < 0 || index >= margin.Values.Length) return "recompute-failed:option";
            var p = margin.Values[index];
            if ((decimal)p * price - 1m <= 0m) continue;
            if (p > chosenP)
            {
                chosen = code;
                chosenP = p;
                chosenOdds = price;
            }
        }
        if (chosen.Length == 0) return "blocked:selection-empty";
        if (!string.Equals(chosen, fields["option"], StringComparison.Ordinal))
            return "blocked:selection-mismatch";
        if (Math.Abs(chosenP - stored) > 1e-6)
            return "blocked:probability-mismatch";
        if (Math.Abs((double)chosenOdds - ReadDouble(fields, "odds")) > 1e-9)
            return "blocked:odds-mismatch";
        return "recomputed";
    }

    static int OptionIndex(string play, string option) => play switch
    {
        "JQ" => int.TryParse(option, out var g) && g >= 0 && g <= 7 ? g : -1,
        "SPF" or "NSPF" => option == "3" ? 0 : option == "1" ? 1 : option == "0" ? 2 : -1,
        _ => ScoreOptionIndex(option)
    };

    static List<(string Code, decimal Odds)> ParseSnapshot(string text)
    {
        var list = new List<(string Code, decimal Odds)>();
        foreach (var item in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = item.LastIndexOf('=');
            if (eq <= 0) throw new FormatException();
            list.Add((item[..eq], decimal.Parse(item[(eq + 1)..], CultureInfo.InvariantCulture)));
        }
        if (list.Count == 0) throw new FormatException();
        return list;
    }

    static int ScoreOptionIndex(string code)
    {
        var list = global::AiFootBall.Core.Models.JcPlayDomain.Bf;
        for (var i = 0; i < list.Count; i++)
            if (list[i] == code) return i;
        return -1;
    }

    public static string? MarketStatus(string play, string snapshot)
    {
        try { return MarketStatus(play, ParseSnapshot(snapshot)); }
        catch (FormatException) { return "blocked:market-missing"; }
    }

    static string? MarketStatus(string play, List<(string Code, decimal Odds)> market)
    {
        var domain = global::AiFootBall.Core.Models.JcPlayDomain.Of(play);
        if (domain == null) return "blocked:market-incomplete";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (code, _) in market)
            if (!seen.Add(code)) return "blocked:market-duplicate";
        if (seen.Count != domain.Count) return "blocked:market-incomplete";
        foreach (var code in domain)
            if (!seen.Contains(code)) return "blocked:market-incomplete";
        return null;
    }

    public static string FileIntegrity(string archivePath)
    {
        var fields = ReadFields(File.ReadAllText(archivePath));
        if (fields == null) return "blocked:duplicate-field";
        return FileIntegrity(fields);
    }

    public static string FileIntegrity(IReadOnlyDictionary<string, string> fields)
    {
        if (!fields.TryGetValue("identityHash", out var storedHash) || storedHash.Length == 0)
            return "blocked:identity-missing";
        return string.Equals(storedHash, IdentityHash(fields), StringComparison.Ordinal)
            ? "integrity:ok" : "blocked:identity-mismatch";
    }

    public static string VersionApproval(string archivePath, string? approvedManifestPath = null)
    {
        var fields = ReadFields(File.ReadAllText(archivePath));
        if (fields == null) return "blocked:duplicate-field";
        if (FileIntegrity(fields) != "integrity:ok") return "approval:not-checked";
        if (!string.Equals(fields.GetValueOrDefault("decision"), "ticket", StringComparison.Ordinal))
            return "approval:not-a-ticket";
        if (string.IsNullOrWhiteSpace(approvedManifestPath)) return "blocked:approved-package-missing";
        return RequireApproved(fields, approvedManifestPath) ?? "approval:runtime";
    }

    public static string Rewrite(IReadOnlyDictionary<string, string> fields)
    {
        var copy = new Dictionary<string, string>(fields, StringComparer.Ordinal);
        copy.Remove("identityHash");
        copy.Remove("sourceKey");
        copy["sourceKey"] = Hash(string.Join('\t',
            copy.GetValueOrDefault("matchId") ?? "", copy.GetValueOrDefault("runId") ?? "",
            copy.GetValueOrDefault("asOf") ?? "", copy.GetValueOrDefault("handicap") ?? "",
            copy.GetValueOrDefault("oddsSnapshot") ?? "", copy.GetValueOrDefault("trainCutoff") ?? "",
            copy.GetValueOrDefault("play") ?? ""));
        copy["identityHash"] = IdentityHash(copy);
        return string.Join('\t', copy.Select(pair => pair.Key + "=" + pair.Value));
    }

    public static string? ApproveManifest(string manifestPath, IReadOnlyList<string> extraRoots)
    {
        SearchRoots = extraRoots;
        if (!File.Exists(manifestPath)) return "blocked:manifest-missing";
        try
        {
            var parsed = ParseManifest(File.ReadAllBytes(manifestPath));
            return CheckDelivery(parsed, Path.GetDirectoryName(manifestPath) ?? "", runtime: true);
        }
        catch (InvalidDataException ex) when (ex.Message == "manifest-duplicate-role")
        {
            return "blocked:required-file-duplicate";
        }
        catch (InvalidDataException)
        {
            return "blocked:manifest-duplicate";
        }
    }

    static string? RequireApproved(Dictionary<string, string> fields, string manifestPath)
    {
        if (!File.Exists(manifestPath)) return "blocked:approved-package-missing";
        var bytes = File.ReadAllBytes(manifestPath);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        if (!string.Equals(fields.GetValueOrDefault("manifestHash"), hash, StringComparison.OrdinalIgnoreCase))
            return "blocked:manifest-not-approved";
        Manifest parsed;
        try { parsed = ParseManifest(bytes); }
        catch (InvalidDataException ex) when (ex.Message == "manifest-duplicate-role")
        {
            return "blocked:required-file-duplicate";
        }
        catch (InvalidDataException) { return "blocked:manifest-duplicate"; }
        if (!string.Equals(fields.GetValueOrDefault("modelVersion"), DecisionDelivery.RuntimeModel(), StringComparison.Ordinal)
            || !string.Equals(fields.GetValueOrDefault("codeVersion"), BuildInfo.Version, StringComparison.Ordinal))
            return "blocked:version-not-approved";
        return CheckDelivery(parsed, Path.GetDirectoryName(manifestPath) ?? "", runtime: true);
    }

    static string? CheckDelivery(Manifest parsed, string manifestDir, bool runtime)
    {
        if (!string.Equals(parsed.Fields.GetValueOrDefault("rule"), DecisionDelivery.ApprovedRule, StringComparison.Ordinal))
            return "blocked:rule-not-approved";
        if (!string.Equals(parsed.Fields.GetValueOrDefault("evidence"), DecisionDelivery.ApprovedEvidence, StringComparison.Ordinal))
            return "blocked:evidence-not-approved";
        if (!string.Equals(parsed.Fields.GetValueOrDefault("temperature"), "1.10", StringComparison.Ordinal))
            return "blocked:parameter-not-approved";
        if (!string.Equals(parsed.Fields.GetValueOrDefault("checked"), CheckedFields, StringComparison.Ordinal))
            return "blocked:checked-set-mismatch";
        if (!string.Equals(parsed.Fields.GetValueOrDefault("database"), AcceptDatabase.Next, StringComparison.Ordinal))
            return "blocked:required-file-missing";
        var set = DecisionDelivery.CheckSet(parsed.Roles);
        if (set != null) return set;
        if (runtime)
        {
            var core = parsed.Roles.First(r => r.Role == "dll-core");
            var loaded = DecisionDelivery.LoadedCorePath;
            if (loaded.Length == 0 || !File.Exists(loaded)) return "blocked:version-not-approved";
            var loadedHash = DecisionDelivery.HashFile(loaded);
            if (!string.Equals(loadedHash, core.Hash, StringComparison.OrdinalIgnoreCase))
                return "blocked:version-not-approved";
            var parameters = parsed.Roles.First(r => r.Role == "model-parameters");
            if (!string.Equals(parameters.Hash, DecisionDelivery.HashText(DecisionDelivery.RuntimeModel()), StringComparison.OrdinalIgnoreCase))
                return "blocked:version-not-approved";
        }
        foreach (var role in parsed.Roles)
        {
            if (role.Role == "model-parameters") continue;
            var found = ResolveRole(role.Path, manifestDir);
            if (found == null) return "blocked:required-file-missing";
            if (found == "ambiguous") return "blocked:required-file-duplicate";
            var actual = DecisionDelivery.HashFile(found);
            var sealedHash = DecisionDelivery.DeliveryFileHash(role.Role);
            if (DecisionDelivery.EnforceDeliverySeal && sealedHash != null
                && !string.Equals(actual, sealedHash, StringComparison.OrdinalIgnoreCase))
                return "blocked:sibling-not-approved";
            if (!string.Equals(actual, role.Hash, StringComparison.OrdinalIgnoreCase))
                return "blocked:version-not-approved";
        }
        return null;
    }

    static string? MatchSnapshot(Dictionary<string, string> fields, string manifestPath)
    {
        Manifest parsed;
        try { parsed = ParseManifest(File.ReadAllBytes(manifestPath)); }
        catch (InvalidDataException) { return "blocked:fit-snapshot-missing"; }
        var item = parsed.Roles.FirstOrDefault(r => r.Role == "fit-snapshots");
        if (item == null) return "blocked:fit-snapshot-missing";
        var path = ResolveRole(item.Path, Path.GetDirectoryName(manifestPath) ?? "");
        if (path is null || path == "ambiguous") return "blocked:fit-snapshot-missing";
        string? found = null;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Replace("\r", "", StringComparison.Ordinal);
            if (line.Length == 0 || line.StartsWith("cutoff", StringComparison.Ordinal)) continue;
            var part = line.Split('\t');
            if (part.Length < 5) return "blocked:fit-snapshot-bad";
            if (part[0] == fields.GetValueOrDefault("trainCutoff") && part[1] == fields.GetValueOrDefault("matchId"))
            {
                if (found != null) return "blocked:fit-snapshot-duplicate";
                found = line;
            }
        }
        if (found == null) return "blocked:fit-snapshot-mismatch";
        var hit = found.Split('\t');
        if (hit[2] != fields.GetValueOrDefault("lambdaHome")
            || hit[3] != fields.GetValueOrDefault("lambdaAway")
            || hit[4] != fields.GetValueOrDefault("rho"))
            return "blocked:fit-snapshot-mismatch";
        return null;
    }

    static string? BindOriginal(Dictionary<string, string> fields, string manifestPath)
    {
        Manifest parsed;
        try { parsed = ParseManifest(File.ReadAllBytes(manifestPath)); }
        catch (InvalidDataException) { return "blocked:source-row-mismatch"; }
        string Text(string role)
        {
            var item = parsed.Roles.First(r => r.Role == role);
            var path = ResolveRole(item.Path, Path.GetDirectoryName(manifestPath) ?? "");
            if (path is null || path == "ambiguous") return "";
            return File.ReadAllText(path);
        }
        return DecisionDelivery.Bind(fields, Text("data-books"), Text("data-odds"), Text("data-history"));
    }

    sealed class Manifest
    {
        public Dictionary<string, string> Fields { get; } = new(StringComparer.Ordinal);
        public List<DecisionDelivery.RoleFile> Roles { get; } = new();
    }

    static Manifest ParseManifest(byte[] bytes)
    {
        var parsed = new Manifest();
        var text = System.Text.Encoding.UTF8.GetString(bytes).Replace("\r", "", StringComparison.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) throw new InvalidDataException("manifest");
            var key = line[..eq];
            var value = line[(eq + 1)..];
            if (key.Equals("verified", StringComparison.OrdinalIgnoreCase) || key.Equals("approved", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("manifest-claim");
            if (key == "role")
            {
                var hashAt = value.LastIndexOf('=');
                var roleAt = value.IndexOf('=');
                if (hashAt <= 0 || roleAt <= 0 || hashAt == roleAt) throw new InvalidDataException("manifest");
                var role = value[..roleAt];
                if (parsed.Roles.Any(r => r.Role == role)) throw new InvalidDataException("manifest-duplicate-role");
                parsed.Roles.Add(new DecisionDelivery.RoleFile
                {
                    Role = role,
                    Path = value[(roleAt + 1)..hashAt],
                    Hash = value[(hashAt + 1)..]
                });
                continue;
            }
            if (!parsed.Fields.TryAdd(key, value)) throw new InvalidDataException("manifest-duplicate");
        }
        return parsed;
    }

    static string? ResolveRole(string relative, string manifestDir)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative == "-") return null;
        var rel = relative.Replace('/', Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(manifestDir);
        var path = Path.GetFullPath(Path.Combine(root, rel));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        return File.Exists(path) ? path : null;
    }

    static Dictionary<string, string>? ReadFields(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var line = text.Replace("\r", "", StringComparison.Ordinal).Split('\n')[0];
        foreach (var part in line.Split('\t'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq];
            if (map.ContainsKey(key)) return null;
            map[key] = part[(eq + 1)..];
        }
        return map;
    }

    static double ReadDouble(IReadOnlyDictionary<string, string> fields, string key)
        => double.Parse(fields[key], CultureInfo.InvariantCulture);
}

public sealed class ArchiveRow
{
    public string RunId { get; init; } = "";
    public long MatchId { get; init; }
    public double LambdaHome { get; init; }
    public double LambdaAway { get; init; }
    public double Rho { get; init; }
    public double Temperature { get; init; }
    public int Home { get; init; }
    public int Away { get; init; }
    public double StoredProbability { get; init; }
    public string ModelVersion { get; init; } = "";
    public string CodeHash { get; init; } = "";
    public string AliasHash { get; init; } = "";
    public string SampleRef { get; init; } = "";
    public string Play { get; init; } = "";
    public string Option { get; init; } = "";
    public decimal Odds { get; init; }
    public string HandicapText { get; init; } = "";
    public decimal Handicap { get; init; }
    public string AsOf { get; init; } = "";
    /// <summary>样本窗/场次预声明时刻；须 ≤ 盘口捕获 ≤ 预测 asOf &lt; 开赛。</summary>
    public string DeclaredAt { get; init; } = "";
    /// <summary>赛前盘口捕获时刻；晚于开赛视为赛后补盘。</summary>
    public string OddsCapturedAt { get; init; } = "";
    /// <summary>开赛时刻。</summary>
    public string Kickoff { get; init; } = "";
    public string OddsSnapshot { get; init; } = "";
    public string ManifestHash { get; init; } = "";
    public string TrainCutoff { get; init; } = "";
    public string Lane { get; init; } = "research";
    public string Decision { get; init; } = "";
    public string Reason { get; init; } = "";
    public string EvidenceId { get; init; } = "";
    public string RuleVersion { get; init; } = "";
    public string RiskState { get; init; } = "";
}

public static class DistributionScore
{
    public static ScoreBatch Mean(IReadOnlyList<SupportDistribution> predicted, IReadOnlyList<(int Home, int Away)> actual)
    {
        if (predicted.Count != actual.Count || predicted.Count < CandidateProtocol.MinTestMatches)
            return ScoreBatch.Fail("insufficient-matches");
        double ll = 0, brier = 0;
        for (var i = 0; i < predicted.Count; i++)
        {
            var one = One(predicted[i], actual[i].Home, actual[i].Away);
            if (!one.Ok) return ScoreBatch.Fail(one.Status);
            ll += one.LogLoss;
            brier += one.Brier;
        }
        return ScoreBatch.Accepted(ll / predicted.Count, brier / predicted.Count, "reliability-not-run");
    }

    public static ScoreOne One(SupportDistribution d, int home, int away)
    {
        if (!d.Ok) return ScoreOne.Fail(d.Status);
        if (home < 0 || away < 0 || home >= d.Stride || away >= d.Stride)
            return ScoreOne.Fail("score-outside-enumerated-grid");
        var p = d.Prob(home, away);
        if (p <= 0 || !double.IsFinite(p)) return ScoreOne.Fail("zero-probability");
        double brier = 0;
        for (var h = 0; h < d.Stride; h++)
        for (var a = 0; a < d.Stride; a++)
        {
            var y = h == home && a == away ? 1d : 0d;
            var diff = d.Prob(h, a) - y;
            brier += diff * diff;
        }
        return ScoreOne.Accepted(-Math.Log(p), brier, p);
    }

    public static string Reliability(IReadOnlyList<(double P, int Happened)> rows)
    {
        if (rows.Count < CandidateProtocol.MinTestMatches) return "insufficient-matches";
        var sumP = new double[10];
        var hits = new int[10];
        var n = new int[10];
        foreach (var (p, y) in rows)
        {
            if (!double.IsFinite(p) || p < 0 || p > 1) return "probability-invalid";
            if (y is not (0 or 1)) return "label-invalid";
            var bin = Math.Min(9, (int)(p * 10));
            if (p == 1) bin = 9;
            n[bin]++;
            sumP[bin] += p;
            hits[bin] += y;
        }
        var parts = new string[10];
        for (var i = 0; i < 10; i++)
        {
            var mean = n[i] == 0 ? 0 : sumP[i] / n[i];
            var freq = n[i] == 0 ? 0 : (double)hits[i] / n[i];
            parts[i] = n[i] + ":" + mean.ToString("0.000", CultureInfo.InvariantCulture)
                       + ":" + freq.ToString("0.000", CultureInfo.InvariantCulture);
        }
        return "ok|" + string.Join(",", parts);
    }
}

public sealed class ScoreOne
{
    ScoreOne(string status, double logLoss, double brier, double pTrue)
    {
        Status = status;
        LogLoss = logLoss;
        Brier = brier;
        PTrue = pTrue;
    }

    public static ScoreOne Fail(string status) => new(status, double.NaN, double.NaN, 0);
    public static ScoreOne Accepted(double logLoss, double brier, double pTrue) => new("ok", logLoss, brier, pTrue);
    public string Status { get; }
    public bool Ok => Status == "ok";
    public double LogLoss { get; }
    public double Brier { get; }
    public double PTrue { get; }
}

public sealed class ScoreBatch
{
    ScoreBatch(string status, double logLoss, double brier, string reliability)
    {
        Status = status;
        LogLoss = logLoss;
        Brier = brier;
        Reliability = reliability;
    }

    public static ScoreBatch Fail(string status) => new(status, double.NaN, double.NaN, status);
    public static ScoreBatch Accepted(double logLoss, double brier, string reliability) => new("ok", logLoss, brier, reliability);
    public string Status { get; }
    public double LogLoss { get; }
    public double Brier { get; }
    public string Reliability { get; }
}

public static class CandidateVersion
{
    public static string Describe()
        => "candidate-dc-temperature-v2|enabled="
           + CandidateTickets.Enabled.ToString(CultureInfo.InvariantCulture)
           + "|cells=" + FullSupportDixonColes.MaxCells
           + "|minCal=" + CandidateProtocol.MinCalibrationMatches
           + "|minTest=" + CandidateProtocol.MinTestMatches
           + "|alphaPreset=" + CandidateProtocol.AlphaInnerDefault.ToString(CultureInfo.InvariantCulture);
}
