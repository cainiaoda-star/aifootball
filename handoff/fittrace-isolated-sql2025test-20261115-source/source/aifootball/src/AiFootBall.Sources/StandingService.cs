using AiFootBall.Core.Models;
using AiFootBall.Data;

namespace AiFootBall.Sources;

/// <summary>
/// 联赛积分榜的维护与查询。
///
/// 数据来源只有历史比分——没有第三方积分榜接口可用，也不该依赖：
/// 自己回算的好处是「当期排名」精确到每场开赛前，第三方榜只给当天快照，
/// 做回测时拿不到"当时"的排名。
/// </summary>
public sealed class StandingService
{
    private readonly StandingRepository _repo;

    public StandingService(StandingRepository repo) => _repo = repo;

    /// <summary>全量回算并落库：每场赛前排名 + 今日积分榜快照。</summary>
    public async Task<StandingRebuildResult> RebuildAsync(DateTime from, CancellationToken ct = default)
    {
        var matches = await _repo.LoadMatchesAsync(from, null, ct);
        var ranks = StandingMath.Compute(matches);
        var written = await _repo.SaveMatchRanksAsync(ranks, ct);

        var snapshot = StandingMath.BuildTable(matches, DateTime.Now);
        var snapRows = await _repo.SaveSnapshotAsync(DateTime.Today, snapshot, ct);

        return new StandingRebuildResult
        {
            Matches = matches.Count,
            RankRows = ranks.Count,
            RankUsable = ranks.Count(r => r.Usable),
            RankWritten = written,
            SnapshotRows = snapRows,
            Leagues = snapshot.Select(s => s.LeagueName).Distinct().Count(),
        };
    }

    /// <summary>取某天的当期积分榜。</summary>
    public Task<List<StandingRow>> GetSnapshotAsync(DateTime asOfDate, string? league = null, CancellationToken ct = default)
        => _repo.GetSnapshotAsync(asOfDate, league, ct);

    /// <summary>批量取赛前排名。</summary>
    public Task<Dictionary<long, MatchPreRank>> GetMatchRanksAsync(IEnumerable<long> matchIds, CancellationToken ct = default)
        => _repo.GetMatchRanksAsync(matchIds, ct);
}

public sealed class StandingRebuildResult
{
    public int Matches { get; set; }
    public int RankRows { get; set; }
    public int RankUsable { get; set; }
    public int RankWritten { get; set; }
    public int SnapshotRows { get; set; }
    public int Leagues { get; set; }

    public override string ToString()
        => $"场次 {Matches}，赛前排名 {RankRows} 行（可用 {RankUsable}），写入 {RankWritten} 行；" +
           $"快照 {SnapshotRows} 行 / {Leagues} 个联赛";
}
