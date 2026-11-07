namespace AiFootBall.Core.Models;

/// <summary>联赛（对应 dbo.FbLeague）</summary>
public sealed class LeagueRecord
{
    public int LeagueId { get; init; }
    public string Name { get; init; } = "";
    public string? Country { get; init; }
    public string? Season { get; init; }
}

/// <summary>球队（对应 dbo.FbTeam）</summary>
public sealed class TeamRecord
{
    public int TeamId { get; init; }
    public string Name { get; init; } = "";
    public int? LeagueId { get; init; }
}

/// <summary>赛事（对应 dbo.FbMatch）</summary>
public sealed class MatchRecord
{
    /// <summary>500.com 赛事 ID (fid)</summary>
    public long MatchId { get; init; }

    public int? LeagueId { get; init; }
    public string LeagueName { get; init; } = "";
    public string? Season { get; init; }

    public DateTime MatchTime { get; init; }

    public int? HomeTeamId { get; init; }
    public int? AwayTeamId { get; init; }
    public string HomeTeamName { get; init; } = "";
    public string AwayTeamName { get; init; } = "";

    public int? HomeScore { get; init; }
    public int? AwayScore { get; init; }
    public int? HalfHomeScore { get; init; }
    public int? HalfAwayScore { get; init; }

    /// <summary>0=未开赛 1=进行中 3=完场 2=取消</summary>
    public int Status { get; init; }

    public decimal? Handicap { get; init; }

    public string Source { get; init; } = "500";

    /// <summary>
    /// 完场判定：比分齐全 + （源标完场 或 开赛已过 150 分钟）。
    /// 不看 Status 单独作数——2026-11-07 实测该字段会被当晚重采洗回 0（全库 1485 行脏数据）。
    /// 口径必须与 <see cref="ReviewScore.Finished"/> 保持一致，否则「采集算完场、结算算未完场」。
    /// </summary>
    public bool IsFinished => HomeScore.HasValue && AwayScore.HasValue
        && (Status == 3 || MatchTime <= DateTime.Now.AddMinutes(-150));

    /// <summary>胜平负：3=主胜 1=平 0=客胜</summary>
    public char? ResultSpf => IsFinished
        ? (HomeScore > AwayScore ? '3' : HomeScore == AwayScore ? '1' : '0')
        : null;

    /// <summary>让球胜平负（按 Handicap 折算，主队让球为负）</summary>
    public char? ResultHandicap
    {
        get
        {
            if (!IsFinished || Handicap is null) return null;
            var diff = HomeScore!.Value + Handicap.Value - AwayScore!.Value;
            return diff > 0 ? '3' : Math.Abs(diff) < 0.001m ? '1' : '0';
        }
    }

    public int? TotalGoals => IsFinished ? HomeScore + AwayScore : null;
}

/// <summary>联赛维度统计</summary>
public sealed class LeagueStat
{
    public int? LeagueId { get; set; }
    public string LeagueName { get; set; } = "";
    public int Matches { get; set; }
    public int HomeWins { get; set; }
    public int Draws { get; set; }
    public int AwayWins { get; set; }
    public double AvgGoals { get; set; }
    public double Over25Rate { get; set; }
    public double BothScoreRate { get; set; }
}

/// <summary>赛事简要信息（赔率采集队列用）</summary>
public sealed class MatchBrief
{
    public long MatchId { get; set; }
    public DateTime MatchTime { get; set; }
    public string LeagueName { get; set; } = "";
    public string HomeTeamName { get; set; } = "";
    public string AwayTeamName { get; set; } = "";
    public int Status { get; set; }
    public byte? OddsStatus { get; set; }
    public DateTime? OddsCheckedAt { get; set; }
    public short? OddsCompanyCount { get; set; }
}

/// <summary>单场赔率明细（接口输出）</summary>
public sealed class FbOddsView
{
    public int CompanyId { get; set; }
    public string Company { get; set; } = "";
    public string OddsType { get; set; } = "";
    public bool IsOpening { get; set; }
    public decimal? HomeWin { get; set; }
    public decimal? Draw { get; set; }
    public decimal? AwayWin { get; set; }
    public decimal? ReturnRate { get; set; }
    public decimal? KellyWin { get; set; }
    public decimal? KellyDraw { get; set; }
    public decimal? KellyLoss { get; set; }
    public decimal? Handicap { get; set; }
    public decimal? HomeWater { get; set; }
    public decimal? AwayWater { get; set; }
    public decimal? TotalLine { get; set; }
    public decimal? OverWater { get; set; }
    public decimal? UnderWater { get; set; }
}
