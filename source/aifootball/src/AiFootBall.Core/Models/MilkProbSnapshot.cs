namespace AiFootBall.Core.Models;

/// <summary>
/// 一场一个玩法一个档位的概率快照（落库 <c>dbo.FbMilkProbSnapshot</c>，2026-11-12 加）。
///
/// 存在的理由：第三方校验方案的第二条 P0——「没有完整分布存档，事后无法解释某场为什么没出票」。
/// 在此之前只存了**上榜那几注**的 <see cref="MilkPick.ModelProbability"/> / <c>ModelPctPure</c>；
/// 一场有 9 档半全场，其中 8 档被闸掉时，库里什么都不留。于是「这道闸拦得对不对」
/// 这个问题永远只能靠重跑模型来回答，而模型每天重拟合，重跑出来的不是当时那个数。
///
/// 落库口径（必须写清楚，否则这张表会被误用）：
/// · 覆盖**所有进入计算的场次**，不是只有出票的场次；被闸掉、被赔率区间挤掉的档也在。
/// · <see cref="ModelPctPure"/> 是纯模型（掺盘口之前），<see cref="MarketPct"/> 是去水后的市场隐含，
///   <see cref="BlendedPct"/> 是按当时权重混合并归一后的值。三列同时存，才分得清
///   「模型错」和「市场错」——只存混合值时两者永远混在一起。
/// · 快照价是**当时的固定奖金**，不是最终价。竞彩受注期间会调价，赛后复盘要用
///   <see cref="MilkPick.Odds"/> 那一套口径，别拿这张表的赔率去算收益。
/// </summary>
public sealed class MilkProbSnapshot
{
    /// <summary>受注日（与 FbMilkPick.PickDate 同一口径，按注单分组那套期号规则）。</summary>
    public DateTime SnapshotDate { get; set; }

    public long MatchId { get; set; }
    public string? MatchNo { get; set; }

    /// <summary>BF=比分 / BQ=半全场 / JQ=总进球 / NSPF=让球胜平负。</summary>
    public string PlayType { get; set; } = "";

    /// <summary>档位代码：比分 "3:1"、半全场 "3-0"、总进球 "0".."7"（7=7+）、让球 "3"/"1"/"0"。</summary>
    public string OptionCode { get; set; } = "";

    /// <summary>当时的固定奖金。快照不全或缺档时该档赔率为空。</summary>
    public decimal? Odds { get; set; }

    /// <summary>去水后的市场隐含概率（%）。赔率缺失或该玩法快照不全时为 null——null ≠ 0。</summary>
    public decimal? MarketPct { get; set; }

    /// <summary>纯模型概率（%）。模型算不出这一档（如 λ 缺失）时为 null。</summary>
    public decimal? ModelPctPure { get; set; }

    /// <summary>混合并归一后的概率（%）= 落库的 ModelProbability 同口径。</summary>
    public decimal? BlendedPct { get; set; }

    /// <summary>这一档是否真的掺过盘口（玩法快照是否完整）。false 时三列里市场与混合列为 null。</summary>
    public bool MarketPriced { get; set; }

    /// <summary>模型版本戳，与 <see cref="MilkPick.ModelVersion"/> 同一串，便于整批归因。</summary>
    public string? ModelVersion { get; set; }
}
