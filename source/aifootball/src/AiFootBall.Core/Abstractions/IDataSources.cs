using AiFootBall.Core.Models;

namespace AiFootBall.Core.Abstractions;

/// <summary>彩票开奖数据源。实现方负责网络抓取与解析，不关心存储。</summary>
public interface ILotterySource
{
    string Name { get; }

    /// <summary>抓取该彩种可获取的历史开奖（按期号倒序或正序均可）。</summary>
    Task<IReadOnlyList<LotteryDrawRecord>> FetchAsync(LotteryTypeInfo lottery, CancellationToken ct = default);
}

/// <summary>足球赛事数据源。</summary>
public interface IMatchSource
{
    string Name { get; }

    /// <summary>抓取指定日期的全部赛事（含已完场与未开赛）。</summary>
    Task<(IReadOnlyList<MatchRecord> Matches, IReadOnlyList<LeagueRecord> Leagues, IReadOnlyList<TeamRecord> Teams)>
        FetchByDateAsync(DateOnly date, CancellationToken ct = default);
}

/// <summary>赔率数据源：按赛事抓取欧赔、亚盘、大小球（初盘 + 终盘）。</summary>
public interface IOddsSource
{
    string Name { get; }

    /// <summary>抓取单场赛事的全部赔率。</summary>
    Task<MatchOddsResult> FetchAsync(long matchId, CancellationToken ct = default);
}

/// <summary>必发（交易所）成交数据源：成交量、成交价、庄家盈亏、大额交易明细。</summary>
public interface IExchangeSource
{
    string Name { get; }

    /// <summary>抓取单场赛事的必发成交快照与大额明细。</summary>
    Task<MatchExchangeResult> FetchAsync(long matchId, CancellationToken ct = default);
}
