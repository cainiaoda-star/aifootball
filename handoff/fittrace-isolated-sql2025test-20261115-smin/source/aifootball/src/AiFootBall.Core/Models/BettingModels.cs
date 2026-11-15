namespace AiFootBall.Core.Models;

/// <summary>投注板块</summary>
public static class Boards
{
    public const string Jc = "jc";      // 竞彩
    public const string Bd = "bd";      // 北单（北京单场）
    public const string Sfc = "sfc";    // 胜负彩（14场/任九）
}

/// <summary>官方受注场次（竞彩/北单/胜负彩）</summary>
public sealed class BettingCardRecord
{
    public long CardId { get; set; }
    public string Board { get; set; } = "";
    public string? Expect { get; set; }
    public string MatchNo { get; set; } = "";
    public long? MatchId { get; set; }
    public string? LeagueName { get; set; }
    public string? HomeTeam { get; set; }
    public string? AwayTeam { get; set; }
    public DateTime? MatchTime { get; set; }
    public decimal? Handicap { get; set; }
    public decimal? SpfHome { get; set; }
    public decimal? SpfDraw { get; set; }
    public decimal? SpfAway { get; set; }
    public bool IsSingle { get; set; }
    public DateTime CapturedAt { get; set; }
}

/// <summary>
/// 推荐信号的机器可读标记。<see cref="MatchRecommendation.SignalDesc"/> 是给人看的文案，
/// 少写一个词、换个说法，靠 <c>Contains</c> 的判定就会静默失效；判定一律看这里的位。
/// 约定：每一位都在写 SignalDesc 的同一处一起置位，文案怎么改都不影响结论。
/// </summary>
[Flags]
public enum SignalTag
{
    None = 0,
    /// <summary>主胜赔率小降（双信号或单信号）。</summary>
    OddsSmallDrop = 1 << 0,
    /// <summary>主胜赔率大降。回测是诱盘形态，不推主。</summary>
    OddsBigDrop = 1 << 1,
    /// <summary>主胜赔率大升。</summary>
    OddsBigRise = 1 << 2,
    /// <summary>
    /// 单信号看好客胜（文案里会出现「看客胜」）。两个来源：大升单信号、
    /// 命中率模型把方向改成客胜。长水降权要按它判，所以两边都要置位。
    /// </summary>
    AwayPick = 1 << 3,
    /// <summary>必发一侧 60%–75%，可跟随的偏热。</summary>
    ExchangeHot = 1 << 4,
    /// <summary>必发一侧 ≥75%，极端大热，不追。</summary>
    ExchangeExtremeHot = 1 << 5,
    /// <summary>欧赔兜底：没有升降/必发信号，按最低赔率给方向。</summary>
    OddsFallback = 1 << 6,
    /// <summary>均势看平：平赔被压或必发平领先。</summary>
    DrawEven = 1 << 7,
    /// <summary>命中率模型把方向改成了平。</summary>
    DrawByModel = 1 << 8,
    /// <summary>平局形态回测偏低，只发参考。</summary>
    DrawLowBacktest = 1 << 9,
    /// <summary>已经明确「不进精选」。后置降权规则看到这一位就不再重复处理。</summary>
    NotFeatured = 1 << 10,
    /// <summary>澳超客胜：页面和让球票都剔掉。</summary>
    ALeagueAwayNotFeatured = 1 << 11,
    /// <summary>意甲比分/进球抬档，文案只补一次。</summary>
    SerieABigGoals = 1 << 12,
    /// <summary>强热门大球，比分看净胜 2 球以上，文案只补一次。</summary>
    StrongFavouriteBigGoals = 1 << 13,
    /// <summary>必发客胜盈亏指数深负（钱往客胜跑），欧战/杯赛低水热门的反向信号。</summary>
    AwayMoneySteam = 1 << 14,
}

/// <summary>单场推荐（引擎输出）</summary>
public sealed class MatchRecommendation
{
    public string Board { get; set; } = "";
    public string? Expect { get; set; }
    public string MatchNo { get; set; } = "";
    public long? MatchId { get; set; }
    public string? LeagueName { get; set; }
    public string HomeTeam { get; set; } = "";
    public string AwayTeam { get; set; } = "";
    public DateTime? MatchTime { get; set; }
    public decimal? Handicap { get; set; }

    /// <summary>推荐结果：3=主胜 1=平 0=客胜；null=无强信号不推荐</summary>
    public char? RecResult { get; set; }
    public decimal? Confidence { get; set; }
    public string SignalDesc { get; set; } = "";
    public int SignalLevel { get; set; }   // 3=双信号强命中 2=单信号 1=弱信号 0=无信号

    /// <summary>
    /// SignalDesc 的结构化版本。所有规则判定都看这一组位，不看文案。
    /// 只活在内存里：每次 Recommend() 都会重新置位，不落库、不从库里恢复。
    /// </summary>
    public SignalTag Tags { get; set; } = SignalTag.None;

    /// <summary>赔率初盘→终盘主胜变化</summary>
    public decimal? HomeOddsDelta { get; set; }

    /// <summary>
    /// 欧赔平均值（CompanyId=0）**收盘主胜绝对赔率**，2026-11-11 加。
    ///
    /// 为什么不能拿 <see cref="PickOdds"/> 或 spfHome 顶替：那两个是**竞彩**价，
    /// 而「收盘欧赔档位」这条判据的证据全部来自欧赔口径。实测两者在 2.4~3.0 段
    /// 根本不是同一批比赛（竞彩 2.42-3.00 档实际主胜 40%、欧赔 2.90-3.60 档只有 21%），
    /// 用竞彩价代欧赔价会把这条判据用到错误的场上。
    ///
    /// 口径与 <see cref="HomeOddsDelta"/> 完全一致（同表同条件、IsOpening=0 的 MAX），
    /// 所以两者可以互相印证；注意 MAX 取的是「即时盘里的最大主胜赔率」而非「最后一条」，
    /// 这是项目既有口径，改它会同时移动一批历史判据，暂不动。
    /// </summary>
    public decimal? HomeWinEuClose { get; set; }
    /// <summary>赔率初盘→终盘平局变化（负=平赔被压）</summary>
    public decimal? DrawOddsDelta { get; set; }
    /// <summary>必发主胜投注占比</summary>
    public decimal? RatioHome { get; set; }
    /// <summary>必发平局投注占比</summary>
    public decimal? RatioDraw { get; set; }
    /// <summary>必发客胜投注占比</summary>
    public decimal? RatioAway { get; set; }

    /// <summary>必发主胜盈亏指数（PnlIdx = Pnl/TotalVolume×100；深负=主胜被买重、庄家在该结果亏钱）。</summary>
    public decimal? PnlIdxHome { get; set; }
    /// <summary>必发客胜盈亏指数（PnlIdx，深负=客胜被买重、钱在涌入客胜）。</summary>
    public decimal? PnlIdxAway { get; set; }

    /// <summary>大小球盘口线（用于进球数预测）</summary>
    public decimal? TotalLine { get; set; }

    /// <summary>最可能比分（2 个，如 "3:2"）。精选发出去之前清空，不进投注。</summary>
    public List<string> ScoreCandidates { get; set; } = new();
    /// <summary>最可能进球数（2 个，如 4、5）。精选发出去之前清空，不进投注。</summary>
    public List<int> GoalCandidates { get; set; } = new();
    /// <summary>比分参考。页面可看，勾选和投注单不用它。</summary>
    public List<string> RefScores { get; set; } = new();
    /// <summary>总进球参考。页面可看，勾选和投注单不用它。</summary>
    public List<int> RefGoals { get; set; } = new();

    /// <summary>让球胜平负：3=让胜 1=让平 0=让负。由最可能比分和让球数折出。</summary>
    public char? RecHandicap { get; set; }
    /// <summary>半全场推荐，如「胜/胜、平/胜」。只在竞彩的比分档场次上给出。</summary>
    public string? HalfFullPick { get; set; }
    /// <summary>当天精选/强推荐。竞彩够格场次；北单为命中率最好的若干场。</summary>
    public bool IsFeatured { get; set; }
    /// <summary>是否发布比分、进球数、半全场。竞彩精选场次为 true。</summary>
    public bool PublishMarkets { get; set; }
    /// <summary>仅欧赔兜底、无升降/必发方向。低水可标注，但不抬进精选。</summary>
    public bool IsOddsFallback { get; set; }

    /// <summary>这一场的预计命中率，0 到 100。由去水概率、模型校准和水位段收缩得到。</summary>
    public decimal? HitProbability { get; set; }
    /// <summary>预计命中率用了哪套算法。</summary>
    public string? HitProbabilityNote { get; set; }

    /// <summary>推荐方向当时的水位。校准和期望回报都要用它，缺了就只能凭感觉谈收益。</summary>
    public decimal? PickOdds { get; set; }

    /// <summary>市场去水后对该方向的隐含概率（0-100）。模型必须打得过这条线才有价值。</summary>
    public decimal? MarketProb { get; set; }

    /// <summary>产生命中率预测的模型标识（devig / logistic + 训练样本数）。</summary>
    public string? ModelTag { get; set; }

    /// <summary>这条预测所依据的盘口采集时间。事后可判断当时盘是否新鲜。</summary>
    public DateTime? DataAt { get; set; }

    /// <summary>北单要买的让球选项（RecHandicap）打出的概率，0 到 100。北单只卖让球胜平负，精选按它筛。</summary>
    public decimal? HandicapProbability { get; set; }
    /// <summary>北单该让球选项的 SP（开奖前会变）。</summary>
    public decimal? HandicapOdds { get; set; }

    /// <summary>让平档的概率（%，模型泊松三项）。给「赢球不赢盘」让平优先规则用（教训 1e/1l）。</summary>
    public decimal? HandicapDrawProbability { get; set; }
    /// <summary>让平档的北单 SP（开奖前会变）。</summary>
    public decimal? HandicapDrawOdds { get; set; }

    /// <summary>
    /// 让球方向概率的模型值（欧赔去水 → 泊松 → 折成让球数三项）。
    /// 和 HandicapMarketProbability 一起留，才能回答「模型和盘口谁更准」。
    /// </summary>
    public decimal? HandicapModelProbability { get; set; }
    /// <summary>
    /// 让球方向概率的盘口值（竞彩让球固定奖金三项直接去水）。
    /// 这是对这一注本身的定价，优先于模型值——出票和精选门槛都用它。
    /// </summary>
    public decimal? HandicapMarketProbability { get; set; }
    /// <summary>该场的 HandicapProbability 取自哪里：market（让球盘去水）/ model（欧赔反推）。</summary>
    public string? HandicapSource { get; set; }

    /// <summary>主胜小降、必发 60%–75%、水位 1.50–1.90。页面单独标「警惕爆冷」，不进投注。</summary>
    public bool UpsetWatch { get; set; }
    public string? UpsetNote { get; set; }

    /// <summary>必发主胜或客胜达到 75%。从推荐挪到大热区，只展示，不追。</summary>
    public bool HotWatch { get; set; }
    /// <summary>大热的一侧：3 主胜，0 客胜。</summary>
    public char? HotSide { get; set; }
    /// <summary>大热一侧的必发占比。</summary>
    public decimal? HotRatio { get; set; }
    public string? HotNote { get; set; }

    /// <summary>
    /// 规则否决前本来会打的方向。否决会把 RecResult 清成 null，方向就永久丢失，
    /// 事后无法回答「这条规则挡掉的场次表现如何」。留一份意图，只用于否决台账。
    /// </summary>
    public char? ProposedResult { get; set; }
    /// <summary>否决原因代码（heat-extreme / conflict / trap ...）。机器可读，便于按规则统计。</summary>
    public string? VetoCode { get; set; }

    /// <summary>大小球参考：大或小。不进精选，不进投注。</summary>
    public string? OuSide { get; set; }
    /// <summary>看好的那一侧能赢下的比例，0 到 100。走盘和赢一半已按亚盘算过。</summary>
    public decimal? OuProbability { get; set; }
    /// <summary>这一侧会走盘的比例，0 到 100。</summary>
    public decimal? OuPush { get; set; }
    public string? OuNote { get; set; }
}

/// <summary>历史比分分布（用于比分预测）</summary>
public sealed class ScoreFreq
{
    public int Home { get; set; }
    public int Away { get; set; }
    public int Cnt { get; set; }
    public string Label => $"{Home}-{Away}";
}

/// <summary>登录用户</summary>
public sealed class UserAccount
{
    public int UserId { get; set; }
    public string UserName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Salt { get; set; } = "";
    public string? NickName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}

/// <summary>竞彩固定奖金玩法常量</summary>
public static class JcPlayTypes
{
    /// <summary>让球胜平负。500 页面上是 data-type="spf"，名字和玩法相反。</summary>
    public const string Nspf = "NSPF";
    /// <summary>胜平负（不让球）。500 页面上是 data-type="nspf"。</summary>
    public const string Spf = "SPF";
    /// <summary>全场比分（bf）</summary>
    public const string Bf = "BF";
    /// <summary>总进球（jqs）</summary>
    public const string Jq = "JQ";
    /// <summary>半全场（bqc）</summary>
    public const string Bq = "BQ";
}

/// <summary>竞彩固定奖金选项（一场一个玩法一个选项一行，对应 dbo.FbJcOddsHistory）</summary>
public sealed class JcOddsHistoryPoint
{
    public long Id { get; set; }
    public long? MatchId { get; set; }
    public string MatchNo { get; set; } = "";
    public string PlayType { get; set; } = "";
    /// <summary>选项代码：3/1/0、1:0、3-3、0~7 等</summary>
    public string OptionCode { get; set; } = "";
    /// <summary>显示名：胜/平/负/让胜/让平/让负/比分/进球数/半全场组合</summary>
    public string OptionName { get; set; } = "";
    public decimal Odds { get; set; }
    public DateTime CapturedAt { get; set; }
}

/// <summary>竞彩固定奖金采样结果（一场四类玩法的所有选项）</summary>
public sealed class JcOddsSnapshot
{
    public long? MatchId { get; set; }
    public string MatchNo { get; set; } = "";
    public List<JcOddsHistoryPoint> Options { get; set; } = new();
}

/// <summary>竞彩受注场次（含四类玩法赔率）</summary>
public sealed class JcMatchCard
{
    public long? MatchId { get; set; }
    public string MatchNo { get; set; } = "";
    public string? HomeTeam { get; set; }
    public string? AwayTeam { get; set; }
    public decimal? Handicap { get; set; }
    /// <summary>竞彩截止时间（data-buyendtime）。回填历史时当作最终奖金的时间点。</summary>
    public DateTime? BuyEndTime { get; set; }
    /// <summary>已完场（data-isend=1），此时页面上的奖金是最终奖金。</summary>
    public bool IsEnd { get; set; }
    /// <summary>让球胜平负：让胜/让平/让负</summary>
    public decimal? NspfWin { get; set; }
    public decimal? NspfDraw { get; set; }
    public decimal? NspfLoss { get; set; }
    /// <summary>胜平负：胜/平/负</summary>
    public decimal? SpfWin { get; set; }
    public decimal? SpfDraw { get; set; }
    public decimal? SpfLoss { get; set; }
    /// <summary>全场比分：code -> 赔率</summary>
    public Dictionary<string, decimal> BfOdds { get; set; } = new();
    /// <summary>总进球：code -> 赔率</summary>
    public Dictionary<string, decimal> JqOdds { get; set; } = new();
    /// <summary>半全场：code -> 赔率</summary>
    public Dictionary<string, decimal> BqOdds { get; set; } = new();
}

/// <summary>推荐记录（复盘用）</summary>
public sealed class RecommendationRecord
{
    public long RecId { get; set; }
    public string Board { get; set; } = "";
    public string? Expect { get; set; }
    public long? MatchId { get; set; }
    public string? MatchNo { get; set; }
    public string? HomeTeam { get; set; }
    public string? AwayTeam { get; set; }
    public DateTime? MatchTime { get; set; }
    /// <summary>3=主胜 1=平 0=客胜；null=全包，不计入命中。</summary>
    public char? RecResult { get; set; }
    public decimal? Confidence { get; set; }
    public int? SignalLevel { get; set; }
    public bool IsCoverAll { get; set; }
    public string? SignalDesc { get; set; }
    public string? Score1 { get; set; }     // 最可能比分 1
    public string? Score2 { get; set; }     // 最可能比分 2
    public int? Goals1 { get; set; }        // 最可能进球数 1
    public int? Goals2 { get; set; }        // 最可能进球数 2
    public char? RecHandicap { get; set; }
    /// <summary>推荐时的让球数（北单 rangqiuNum）。RecHandicap 按它结算。</summary>
    public decimal? Handicap { get; set; }
    /// <summary>北单让球选项是否打出。</summary>
    public bool? HandicapHit { get; set; }
    public string? RecHalfFull { get; set; }
    public string? ActualScore { get; set; } // 实际比分（赛后可回填）
    public int? ActualGoals { get; set; }   // 实际总进球
    public char? ActualResult { get; set; }
    public bool? Hit { get; set; }
    /// <summary>推荐时模型给出的预计命中率（原始值，未被 60/55/45 这些展示下限改写）。</summary>
    public decimal? HitProbability { get; set; }
    public string? HitProbNote { get; set; }
    /// <summary>推荐方向当时的水位。校准与期望回报都靠它。</summary>
    public decimal? PickOdds { get; set; }
    /// <summary>市场去水后的隐含概率（0-100），模型校准的基准线。</summary>
    public decimal? MarketProb { get; set; }
    public string? ModelTag { get; set; }
    /// <summary>该场盘口数据的最新采集时间。</summary>
    public DateTime? DataAt { get; set; }
    public DateTime RecDate { get; set; }   // 推荐日期
    public DateTime CreatedAt { get; set; }
    public string? LeagueName { get; set; } // 比赛类型（JOIN FbMatch 填充，不落库）
}

/// <summary>北单历史一场：让球、平均欧赔、比分和开出的让球选项（3/1/0）及 SP。</summary>
public sealed class BdHistoryRow
{
    public int Expect { get; set; }
    public int MatchNo { get; set; }
    public long? MatchId { get; set; }
    public string? LeagueName { get; set; }
    public DateTime? EndTime { get; set; }
    public string? HomeTeam { get; set; }
    public string? AwayTeam { get; set; }
    public decimal? Handicap { get; set; }
    public decimal? AvgHome { get; set; }
    public decimal? AvgDraw { get; set; }
    public decimal? AvgAway { get; set; }
    public string? AsianHand { get; set; }
    public int? HomeScore { get; set; }
    public int? AwayScore { get; set; }
    public string? ResultCode { get; set; }
    public decimal? ResultSp { get; set; }
}
