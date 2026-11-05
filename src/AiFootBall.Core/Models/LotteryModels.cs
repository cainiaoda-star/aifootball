namespace AiFootBall.Core.Models;

/// <summary>彩种定义（对应 dbo.LotteryType）</summary>
public sealed class LotteryTypeInfo
{
    public int LotteryId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public int RedCount { get; set; }
    public int RedMax { get; set; }
    public int BlueCount { get; set; }
    public int BlueMax { get; set; }
    /// <summary>开奖日，1=周一 … 7=周日</summary>
    public string? DrawWeekdays { get; set; }
    /// <summary>pinble 历史页地址</summary>
    public string? SourceUrl { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// 本项目是否提供选号。七乐彩/七星彩只有开奖数据、没有选号引擎，这里为 false。
    /// 前端渲染彩种入口时按这个字段过滤，不要再自己维护一份白名单。
    /// </summary>
    public bool SupportsRecommend => LottoCodes.All.Contains(Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>数字型彩票（3D/排列三/七星彩等），号码为有序个位号</summary>
    public bool IsDigitGame => RedMax <= 9 && BlueCount == 0;
}

/// <summary>单个奖级中奖信息</summary>
public sealed class PrizeRecord
{
    public int Level { get; init; }
    public int? BetCount { get; init; }
    public long? Amount { get; init; }
}

/// <summary>一期开奖记录（与数据源无关的统一模型）</summary>
public sealed class LotteryDrawRecord
{
    public string LotteryCode { get; init; } = "";

    /// <summary>期号，如 2026122</summary>
    public string Issue { get; init; } = "";

    public DateOnly DrawDate { get; init; }

    /// <summary>前区/红球（有序）</summary>
    public IReadOnlyList<int> Reds { get; init; } = Array.Empty<int>();

    /// <summary>后区/蓝球（有序）</summary>
    public IReadOnlyList<int> Blues { get; init; } = Array.Empty<int>();

    public long? SalesAmount { get; init; }
    public long? PoolAmount { get; init; }
    public IReadOnlyList<PrizeRecord> Prizes { get; init; } = Array.Empty<PrizeRecord>();

    public string Source { get; init; } = "";

    public string RedCsv => string.Join(",", Reds.Select(x => x.ToString("D2")));
    public string? BlueCsv => Blues.Count == 0 ? null : string.Join(",", Blues.Select(x => x.ToString("D2")));
}

/// <summary>号码频率统计行</summary>
public sealed class NumberFrequency
{
    public int Number { get; set; }
    public string BallType { get; set; } = "R";
    public int TotalCount { get; set; }
    public int CountLast30 { get; set; }
    public int CountLast100 { get; set; }
    public double AvgInterval { get; set; }
    public int CurrentOmission { get; set; }
    public int MaxOmission { get; set; }
}
