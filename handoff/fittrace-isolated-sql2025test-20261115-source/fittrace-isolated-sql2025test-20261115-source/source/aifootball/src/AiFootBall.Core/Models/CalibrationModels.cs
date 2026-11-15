namespace AiFootBall.Core.Models;

/// <summary>
/// 一条已结算、可用于校准统计的推荐。只取「有预测值、有水位、已出赛果」的行；
/// 全包（IsCoverAll）与无方向的场次不在其中——它们本来就不该计入命中率。
/// </summary>
public sealed class CalibrationRow
{
    public long RecId { get; set; }
    public string Board { get; set; } = "";
    public DateTime RecDate { get; set; }
    public DateTime? MatchTime { get; set; }
    public decimal HitProbability { get; set; }
    public decimal? MarketProb { get; set; }
    public decimal? PickOdds { get; set; }
    public bool Hit { get; set; }
    public string? ModelTag { get; set; }
    public DateTime? DataAt { get; set; }
    public int? SignalLevel { get; set; }
    public char? RecResult { get; set; }
    public char? ActualResult { get; set; }

    /// <summary>推荐到开赛之间，最后一次采到盘口的时间早于开赛多少小时。空=没有采集记录。</summary>
    public double? DataAgeHours =>
        MatchTime is DateTime mt && DataAt is DateTime at
            ? Math.Max(0, (mt - at).TotalHours)
            : null;
}

/// <summary>一个预测区间内的校准结果。</summary>
public sealed class CalibrationBand
{
    public string Label { get; set; } = "";
    public decimal Low { get; set; }
    public decimal High { get; set; }
    public int Count { get; set; }
    public int Hits { get; set; }
    /// <summary>实际命中率（0-100）。</summary>
    public decimal Actual { get; set; }
    /// <summary>该区间平均预测值（0-100）。</summary>
    public decimal Predicted { get; set; }
    /// <summary>实际 − 预测。正数=模型保守，负数=模型过于乐观。</summary>
    public decimal Gap { get; set; }
    /// <summary>按 1 单位平注、真实水位结算的回报率。没有水位的行不计入。</summary>
    public decimal? Roi { get; set; }
    public int PricedCount { get; set; }

    /// <summary>样本太薄时不要下结论：n&lt;30 时 95% 置信区间宽到 ±18 个点以上。</summary>
    public bool Thin => Count < 30;
}

/// <summary>校准总览。目的是回答一个问题：55% 这条门槛，实际命中率到底是多少。</summary>
public sealed class CalibrationReport
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public int Total { get; set; }
    public int Hits { get; set; }

    /// <summary>整体实际命中率（0-100）。</summary>
    public decimal Actual { get; set; }
    /// <summary>整体平均预测值（0-100）。</summary>
    public decimal Predicted { get; set; }
    public decimal Bias => decimal.Round(Actual - Predicted, 2);

    /// <summary>Brier 分数（越小越好）。0.25 = 永远猜 50%，是「模型无用」的参照线。</summary>
    public double Brier { get; set; }
    /// <summary>只按去水概率（市场价）算的 Brier，用来判断模型是否比市场更准。</summary>
    public double BrierBaseline { get; set; }
    /// <summary>对数损失，对过度自信的惩罚比 Brier 更重。</summary>
    public double LogLoss { get; set; }

    /// <summary>平注回报率（%）。负数即抽水造成的结构性亏损。</summary>
    public decimal? Roi { get; set; }
    public int PricedCount { get; set; }

    public List<CalibrationBand> Bands { get; set; } = new();
    public List<CalibrationBand> ByModel { get; set; } = new();

    /// <summary>按盘龄分层：盘口在开赛前多久被最后一次采到。</summary>
    public List<CalibrationBand> ByDataAge { get; set; } = new();

    public string Verdict { get; set; } = "";
}
