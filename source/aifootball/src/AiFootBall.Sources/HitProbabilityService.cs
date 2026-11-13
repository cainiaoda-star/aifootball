using AiFootBall.Core.Models;
using AiFootBall.Data;

namespace AiFootBall.Sources;

/// <summary>给推荐标上预计命中率。模型按时间切分后缓存半小时。</summary>
public sealed class HitProbabilityService
{
    private readonly BettingRepository _repo;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    private Snapshot _cache = Snapshot.Empty;
    private DateTime _cachedAt = DateTime.MinValue;
    private int _refreshing;

    public HitProbabilityService(BettingRepository repo) => _repo = repo;

    /// <summary>
    /// 上一次拟合失败的原因（成功时清空）。
    ///
    /// 2026-11-05 加：原来这里 catch 之后静默换成 Empty，还把「空模型」当成正常结果缓存半小时。
    /// 后果是大奶榜整整半小时一场不出，而接口只说一句「0 注」，看的人以为是「今天没有正期望的注」，
    /// 实际是模型根本没跑起来。失败必须能说出来，而且不能被缓存住。
    /// </summary>
    public string? LastError { get; private set; }

    public async Task<Snapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        // 有旧快照就先给旧的，过期了也只触发后台刷新。
        // 2026-11-05 改：样本涨到 25 万场后拟合要 30~40 秒，原来每 30 分钟过期一次，
        // 就有一个倒霉的请求（往往是用户点大奶榜那一下）要干等半分钟。宁可用 30 分钟前的模型，
        // 也不要卡住人——反正这期间也不会有什么变化值得为它暂停一次交互。
        if (_cache.Loaded)
        {
            if (DateTime.UtcNow - _cachedAt < CacheTtl) return _cache;
            StartBackgroundRefresh();
            return _cache;
        }

        return await RefreshAsync(ct);
    }

    /// <summary>后台刷新。同一时刻只允许一个在跑，失败时保留旧快照。</summary>
    private void StartBackgroundRefresh()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        });
    }

    private async Task<Snapshot> RefreshAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - _cachedAt < CacheTtl && _cache.Loaded)
                return _cache;

            try
            {
                // 拟合时刻只取一次，取样与 Fit 用同一个值（2026-11-13）。
                // 之前线上 Fit 不传 asOf，模型对「开赛时间在未来却已有比分」的场次毫无防备：
                // 库里有 2 场这种脏数据，把版本戳的拟合窗口推到两天之后。
                // 现在取样 SQL 与 Fit 两层都卡这个时刻，任何一层漏了，另一层还能兜住。
                var fitMoment = DateTime.Now;
                var rows = await _repo.GetHitTrainingRowsAsync(ct);
                var buckets = await _repo.GetHitBucketsAsync(ct);
                var scores = await _repo.GetScoreSamplesAsync(ct, asOf: fitMoment);
                var map = new Dictionary<(char Side, string Band), (int Hits, int Total)>();
                foreach (var b in buckets)
                {
                    if (string.IsNullOrEmpty(b.Side)) continue;
                    map[(b.Side[0], b.Band)] = (b.Hits, b.Total);
                }
                _cache = new Snapshot(HitProbabilityModel.Fit(rows), map, true,
                    ScorelineModel.Fit(scores, asOf: fitMoment));
                LastError = null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 失败不写缓存：_cachedAt 停在旧值，下一次请求会重试整条拟合。
                // 换来的代价是数据库抖动期间每次请求都要多试一次，但比「空半小时还理直气壮」好。
                LastError = ex.Message;
                _cache = new Snapshot(HitProbabilityModel.DevigOnly, new(), true, ScorelineModel.Empty);
                return _cache;
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

        // 市场基准：同一方向的去水概率。模型与它的差值就是「这单到底有没有 edge」。
        var marketP = side switch { '3' => price.Value.Home, '1' => price.Value.Draw, _ => price.Value.Away };
        rec.MarketProb = Math.Round(marketP <= 0 ? 0m : marketP * 100m, 1);

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

        // 落库用：水位是「这条预测值多少钱」的唯一凭据，模型标识用来事后区分
        // 哪一版模型给出的预测，避免模型换代后把两批结论混在一起算校准。
        rec.PickOdds = odds;
        rec.ModelTag = BuildModelTag(snapshot.Model);

        // 该侧×水位段历史命中偏低时，硬编码置信度让位给经验率，并可能踢出精选
        ApplyEmpiricalGate(rec, hits, total, odds);
        return changed;
    }

    /// <summary>
    /// 模型标识。只保留能区分「预测能力是否变化」的要素：是否走了回归、训练样本量、
    /// 留出集 Brier。不写时间戳——同一版模型跨天应当可以合并统计。
    /// </summary>
    private static string BuildModelTag(HitProbabilityModel model)
    {
        if (!model.UsesLogistic) return $"devig n={model.TrainCount}";
        return $"logistic n={model.TrainCount} b={model.HoldoutBrierModel:0.0000}/{model.HoldoutBrierDevig:0.0000}";
    }

    /// <summary>
    /// 规则先给方向。模型三项里最高的一项领先次高至少 5 个点、且不低于 50% 时，可以改方向。
    /// 水位低于 1.90 不改上去。平局只作参考，不进精选。必发 ≥75% 不追。
    /// </summary>
    internal static bool TryApplyModelSide(
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
        if ((best == '3' && rec.RatioHome >= RecommendationScoring.ExtremeHotRatio)
            || (best == '0' && rec.RatioAway >= RecommendationScoring.ExtremeHotRatio))
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
            if ((rec.Tags & SignalTag.DrawByModel) == 0)
            {
                rec.Tags |= SignalTag.DrawByModel | SignalTag.NotFeatured;
                rec.SignalDesc += "；模型看平，不进精选";
            }
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
        // 「模型改看客胜」也是单信号客向，长水降权要按它判，所以和「大升看客胜」同一位。
        if (best == '0') rec.Tags |= SignalTag.AwayPick;
        var name = best == '3' ? "主胜" : "客胜";
        rec.SignalDesc += $"；模型改看{name} {top * 100m:0.#}%";
        return previous != best;
    }

    /// <summary>
    /// 样本够时：用真实命中率校正置信度。水位段低于精选门槛则降权，等级 3 也同样。
    /// 超低水热门不在这里误杀，市场价已经定价了命中率。
    /// 样本量分级：≥40 场才允许踢出精选，20~39 场只调置信度，20 场以下完全不动。
    /// 12 场的点估计区间约 ±28 点，拿它否决 944 场的 55 门槛属于用噪声推翻证据。
    /// </summary>
    internal static void ApplyEmpiricalGate(
        MatchRecommendation rec, int hits, int total, decimal? pickOdds)
    {
        if (total < RecommendationScoring.MinSampleForConfidence || rec.RecResult is null || rec.IsOddsFallback) return;
        if (pickOdds is > 1m and < 1.40m) return;

        var rate = 100m * hits / total;
        // 先验取精选门槛本身：扣掉运气成分，样本越少越贴近先验，不会把 1/12 当成 8.3%。
        var prior = RecommendationScoring.MinFeaturedHitProbability / 100m;
        var shrunk = 100m * HitRateMath.Shrink(hits, total, prior);

        if (rec.Confidence is decimal conf)
            rec.Confidence = Math.Round(0.35m * conf + 0.65m * shrunk, 1);

        if (total < RecommendationScoring.MinSampleForDemote) return;
        if (shrunk >= RecommendationScoring.MinFeaturedHitProbability || rec.SignalLevel < 2) return;

        rec.SignalLevel = 1;
        if (rec.Confidence is null || rec.Confidence >= RecommendationScoring.PublishConfidence)
            rec.Confidence = Math.Min(rec.Confidence ?? 55m, 55m);
        rec.Tags |= SignalTag.NotFeatured;
        rec.SignalDesc += $"；该侧水位段历史命中 {rate:0.#}%({hits}/{total})，收缩后 {shrunk:0.#}%，降权不进精选";
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
