using AiFootBall.Core.Models;
using AiFootBall.Data;

namespace AiFootBall.Sources;

/// <summary>给推荐标上预计命中率。模型按时间切分后缓存半小时。</summary>
public sealed class HitProbabilityService
{
    private readonly BettingRepository _repo;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Snapshot _cache = Snapshot.Empty;
    private DateTime _cachedAt = DateTime.MinValue;

    public HitProbabilityService(BettingRepository repo) => _repo = repo;

    public async Task<Snapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        if (DateTime.UtcNow - _cachedAt < TimeSpan.FromMinutes(30) && _cache.Loaded)
            return _cache;

        await _gate.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - _cachedAt < TimeSpan.FromMinutes(30) && _cache.Loaded)
                return _cache;

            try
            {
                var rows = await _repo.GetHitTrainingRowsAsync(ct);
                var buckets = await _repo.GetHitBucketsAsync(ct);
                var scores = await _repo.GetScoreSamplesAsync(ct);
                var map = new Dictionary<(char Side, string Band), (int Hits, int Total)>();
                foreach (var b in buckets)
                {
                    if (string.IsNullOrEmpty(b.Side)) continue;
                    map[(b.Side[0], b.Band)] = (b.Hits, b.Total);
                }
                _cache = new Snapshot(HitProbabilityModel.Fit(rows), map, true, ScorelineModel.Fit(scores));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                _cache = new Snapshot(HitProbabilityModel.DevigOnly, new(), true, ScorelineModel.Empty);
            }

            _cachedAt = DateTime.UtcNow;
            return _cache;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>标上预计命中率。模型明显强于规则时改方向，返回方向是否变了。</summary>
    public static bool Stamp(
        MatchRecommendation rec, Snapshot snapshot,
        decimal? home, decimal? draw, decimal? away,
        decimal? ahOpen, decimal? ahClose)
    {
        var price = HitRateMath.Devig(home, draw, away);
        if (price is null) return false;

        var modelPrice = snapshot.Model.Predict(
            home, draw, away, ahOpen, ahClose, rec.RatioHome, rec.RatioAway, rec.RatioDraw,
            rec.LeagueName, rec.HomeOddsDelta);
        if (modelPrice.Home <= 0 && modelPrice.Draw <= 0 && modelPrice.Away <= 0)
            modelPrice = price.Value;

        var changed = snapshot.Model.UsesLogistic
                      && TryApplyModelSide(rec, modelPrice, home, draw, away);

        if (rec.RecResult is not char side || side is not ('3' or '1' or '0')) return changed;
        var p0 = side switch
        {
            '3' => modelPrice.Home,
            '1' => modelPrice.Draw,
            _ => modelPrice.Away
        };
        if (p0 <= 0)
        {
            p0 = side switch { '3' => price.Value.Home, '1' => price.Value.Draw, _ => price.Value.Away };
        }

        var odds = side switch { '3' => home, '1' => draw, _ => away };
        var band = odds is > 1 ? HitRateMath.OddsBand(odds.Value) : "";
        var hits = 0;
        var total = 0;
        if (band.Length > 0 && snapshot.Buckets.TryGetValue((side, band), out var bucket))
        {
            hits = bucket.Hits;
            total = bucket.Total;
        }

        var shrunk = HitRateMath.Shrink(hits, total, p0);
        rec.HitProbability = Math.Round(shrunk * 100m, 1);
        rec.HitProbabilityNote = snapshot.Model.UsesLogistic
            ? "去水后用欧赔升降、亚盘和必发校准，再按联赛和水位段收缩"
            : "去水后的水位，再按联赛和该水位段已结算场次收缩";

        // 该侧×水位段历史命中偏低时，硬编码置信度让位给经验率，并可能踢出精选
        ApplyEmpiricalGate(rec, hits, total, odds);
        return changed;
    }

    /// <summary>
    /// 规则先给方向。模型三项里最高的一项领先次高至少 5 个点、且不低于 50% 时，可以改方向。
    /// 水位低于 1.90 不改上去。平局只作参考，不进精选。必发 ≥75% 不追。
    /// </summary>
    private static bool TryApplyModelSide(
        MatchRecommendation rec,
        (decimal Home, decimal Draw, decimal Away) p,
        decimal? home, decimal? draw, decimal? away)
    {
        var best = '3';
        var top = p.Home;
        if (p.Draw > top) { best = '1'; top = p.Draw; }
        if (p.Away > top) { best = '0'; top = p.Away; }
        var second = best switch
        {
            '3' => Math.Max(p.Draw, p.Away),
            '1' => Math.Max(p.Home, p.Away),
            _ => Math.Max(p.Home, p.Draw)
        };
        var lead = top - second;
        if ((best == '3' && rec.RatioHome >= 75m) || (best == '0' && rec.RatioAway >= 75m))
            return false;

        var odds = best switch { '3' => home, '1' => draw, _ => away };
        if (best == '1')
        {
            if (top < 0.42m || lead < 0.04m) return false;
            if (rec.RecResult is '3' or '0' && rec.SignalLevel >= 2 && top < 0.48m) return false;
            var was = rec.RecResult;
            rec.RecResult = '1';
            rec.SignalLevel = 1;
            rec.Confidence = Math.Min(45m, decimal.Round(top * 100m, 1));
            rec.IsOddsFallback = false;
            if (!rec.SignalDesc.Contains("模型看平", StringComparison.Ordinal))
                rec.SignalDesc += "；模型看平，不进精选";
            return was != '1';
        }

        var strong = top >= 0.50m && lead >= 0.05m;
        if (!strong) return false;
        if (odds is < RecommendationScoring.MinPublishOdds) return false;
        if (rec.RecResult == best) return false;

        var previous = rec.RecResult;
        rec.RecResult = best;
        rec.SignalLevel = 2;
        rec.Confidence = Math.Max(RecommendationScoring.PublishConfidence, decimal.Round(top * 100m, 1));
        rec.IsOddsFallback = false;
        var name = best == '3' ? "主胜" : "客胜";
        rec.SignalDesc += $"；模型改看{name} {top * 100m:0.#}%";
        return previous != best;
    }

    /// <summary>
    /// 样本够时：用真实命中率校正置信度。水位段低于精选门槛则降权，等级 3 也同样。
    /// 超低水热门不在这里误杀，市场价已经定价了命中率。
    /// </summary>
    internal static void ApplyEmpiricalGate(
        MatchRecommendation rec, int hits, int total, decimal? pickOdds)
    {
        if (total < 12 || rec.RecResult is null || rec.IsOddsFallback) return;
        if (pickOdds is > 1m and < 1.40m) return;

        var rate = 100m * hits / total;
        if (total >= 20 && rec.Confidence is decimal conf)
            rec.Confidence = Math.Round(0.35m * conf + 0.65m * rate, 1);

        if (rate < RecommendationScoring.MinFeaturedHitProbability && rec.SignalLevel >= 2)
        {
            rec.SignalLevel = 1;
            if (rec.Confidence is null || rec.Confidence >= RecommendationScoring.PublishConfidence)
                rec.Confidence = Math.Min(rec.Confidence ?? 55m, 55m);
            rec.SignalDesc += $"；该侧水位段历史命中 {rate:0.#}%({hits}/{total})，降权不进精选";
        }
    }

    public sealed class Snapshot
    {
        public static Snapshot Empty { get; } = new(HitProbabilityModel.DevigOnly, new(), false, ScorelineModel.Empty);

        public Snapshot(
            HitProbabilityModel model,
            Dictionary<(char Side, string Band), (int Hits, int Total)> buckets,
            bool loaded,
            ScorelineModel? scores = null)
        {
            Model = model;
            Buckets = buckets;
            Loaded = loaded;
            Scores = scores ?? ScorelineModel.Empty;
        }

        public HitProbabilityModel Model { get; }
        public Dictionary<(char Side, string Band), (int Hits, int Total)> Buckets { get; }
        public bool Loaded { get; }
        public ScorelineModel Scores { get; }
    }
}
