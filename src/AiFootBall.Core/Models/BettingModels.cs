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

    /// <summary>赔率初盘→终盘主胜变化</summary>
    public decimal? HomeOddsDelta { get; set; }
    /// <summary>赔率初盘→终盘平局变化（负=平赔被压）</summary>
    public decimal? DrawOddsDelta { get; set; }
    /// <summary>必发主胜投注占比</summary>
    public decimal? RatioHome { get; set; }
    /// <summary>必发平局投注占比</summary>
    public decimal? RatioDraw { get; set; }
    /// <summary>必发客胜投注占比</summary>
    public decimal? RatioAway { get; set; }

    /// <summary>大小球盘口线（用于进球数预测）</summary>
    public decimal? TotalLine { get; set; }

    /// <summary>最可能比分（2 个，如 "3:2"）</summary>
    public List<string> ScoreCandidates { get; set; } = new();
    /// <summary>最可能进球数（2 个，如 4、5）</summary>
    public List<int> GoalCandidates { get; set; } = new();

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
    /// <summary>让球胜平负（nspf）</summary>
    public const string Nspf = "NSPF";
    /// <summary>胜平负（spf，不让球）</summary>
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
    public string? RecHalfFull { get; set; }
    public string? ActualScore { get; set; } // 实际比分（赛后可回填）
    public int? ActualGoals { get; set; }   // 实际总进球
    public char? ActualResult { get; set; }
    public bool? Hit { get; set; }
    public DateTime RecDate { get; set; }   // 推荐日期
    public DateTime CreatedAt { get; set; }
    public string? LeagueName { get; set; } // 比赛类型（JOIN FbMatch 填充，不落库）
}
