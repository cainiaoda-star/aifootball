using AiFootBall.Core.Models;
using Dapper;
using Microsoft.Data.SqlClient;

namespace AiFootBall.Data;

/// <summary>竞彩固定奖金（四类玩法）历史采样仓储。</summary>
public sealed class JcOddsRepository
{
    private readonly SqlConnectionFactory _factory;

    public JcOddsRepository(SqlConnectionFactory factory) => _factory = factory;

    /// <summary>把一批竞彩固定奖金快照写成时间序列点（同一 CapturedAt，先删旧再插，保证幂等）。</summary>
    public async Task<int> SaveSnapshotAsync(IReadOnlyList<JcOddsSnapshot> snapshots, DateTime capturedAt,
        CancellationToken ct = default)
    {
        var points = new List<JcOddsHistoryPoint>();
        foreach (var s in snapshots)
        {
            foreach (var o in s.Options)
            {
                o.MatchId = s.MatchId;
                o.MatchNo = s.MatchNo;
                o.CapturedAt = capturedAt;
                points.Add(o);
            }
        }
        if (points.Count == 0) return 0;

        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        // 先清掉本分钟已采样的旧数据，避免同分钟重复触发造成重复行
        var matchNos = points.Select(p => p.MatchNo).Distinct().ToArray();
        await conn.ExecuteAsync(new CommandDefinition(
            @"DELETE FROM dbo.FbJcOddsHistory WHERE CapturedAt = @at AND MatchNo IN @nos",
            new { at = capturedAt, nos = matchNos }, tx, cancellationToken: ct));

        var affected = 0;
        foreach (var p in points)
        {
            affected += await conn.ExecuteAsync(new CommandDefinition(
                @"INSERT INTO dbo.FbJcOddsHistory (MatchId, MatchNo, PlayType, OptionCode, OptionName, Odds, CapturedAt)
                  VALUES (@mid, @no, @pt, @code, @name, @odds, @at)",
                new
                {
                    mid = p.MatchId, no = p.MatchNo, pt = p.PlayType, code = p.OptionCode,
                    name = p.OptionName, odds = p.Odds, at = p.CapturedAt
                }, tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return affected;
    }

    /// <summary>
    /// 按场次号取竞彩固定奖金的最新水位（当前值 + 相对上一次采样的涨跌）。
    /// 返回结构按玩法分组，每个选项给出 odds 与 delta。
    /// </summary>
    public async Task<object?> GetWaterAsync(string matchNo, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);

        // 最近两次采样时间点
        var times = (await conn.QueryAsync<DateTime>(new CommandDefinition(
            @"SELECT DISTINCT TOP 2 CapturedAt FROM dbo.FbJcOddsHistory
              WHERE MatchNo = @no ORDER BY CapturedAt DESC", new { no = matchNo }, cancellationToken: ct))).ToList();
        if (times.Count == 0) return null;

        var latestAt = times[0];
        var prevAt = times.Count > 1 ? times[1] : (DateTime?)null;

        // 最新一次采样的所有选项
        var latest = await conn.QueryAsync<JcOddsHistoryPoint>(new CommandDefinition(
            @"SELECT * FROM dbo.FbJcOddsHistory WHERE MatchNo = @no AND CapturedAt = @at",
            new { no = matchNo, at = latestAt }, cancellationToken: ct));

        // 上一次采样的选项（算涨跌）
        Dictionary<string, decimal> prevMap = new();
        if (prevAt is not null)
        {
            var prev = await conn.QueryAsync<JcOddsHistoryPoint>(new CommandDefinition(
                @"SELECT * FROM dbo.FbJcOddsHistory WHERE MatchNo = @no AND CapturedAt = @at",
                new { no = matchNo, at = prevAt }, cancellationToken: ct));
            foreach (var p in prev)
                prevMap[$"{p.PlayType}|{p.OptionCode}"] = p.Odds;
        }

        // 按玩法分组
        var groups = latest
            .GroupBy(p => p.PlayType)
            .Select(g => new
            {
                playType = g.Key,
                playName = PlayName(g.Key),
                options = g.Select(p =>
                {
                    var key = $"{p.PlayType}|{p.OptionCode}";
                    decimal? delta = prevMap.TryGetValue(key, out var prevOdds)
                        ? decimal.Round(p.Odds - prevOdds, 3)
                        : null;
                    return new
                    {
                        code = p.OptionCode,
                        name = p.OptionName,
                        odds = p.Odds,
                        delta
                    };
                }).ToList()
            }).ToList();

        return new
        {
            matchNo,
            capturedAt = latestAt,
            prevCapturedAt = prevAt,
            groups
        };
    }

    /// <summary>每场每个选项取最近一次大于 1 的赔率。1 是完场后的占位，不当成报价。</summary>
    public async Task<List<JcOddsHistoryPoint>> GetLatestAsync(IEnumerable<long> matchIds, CancellationToken ct = default)
    {
        var ids = matchIds.Distinct().ToArray();
        if (ids.Length == 0) return new();

        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<JcOddsHistoryPoint>(new CommandDefinition(
            @"SELECT MatchId, PlayType, OptionCode, OptionName, Odds
              FROM (
                  SELECT MatchId, PlayType, OptionCode, OptionName, Odds,
                         ROW_NUMBER() OVER (PARTITION BY MatchId, PlayType, OptionCode ORDER BY CapturedAt DESC) AS rn
                  FROM dbo.FbJcOddsHistory
                  WHERE MatchId IN @ids AND Odds > 1
              ) t WHERE rn = 1",
            new { ids }, cancellationToken: ct))).ToList();
    }

    /// <summary>各场让球胜平负的最新价。键是 MatchId，值是让胜/让平/让负。</summary>
    public async Task<Dictionary<long, (decimal Win, decimal Draw, decimal Loss)>> GetLatestNspfAsync(
        IEnumerable<long> matchIds, CancellationToken ct = default)
    {
        var ids = matchIds.Distinct().ToArray();
        var map = new Dictionary<long, (decimal Win, decimal Draw, decimal Loss)>();
        if (ids.Length == 0) return map;

        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<(long MatchId, string OptionCode, decimal Odds)>(new CommandDefinition(
            @"SELECT MatchId, OptionCode, Odds
              FROM (
                  SELECT MatchId, OptionCode, Odds,
                         ROW_NUMBER() OVER (PARTITION BY MatchId, OptionCode ORDER BY CapturedAt DESC) AS rn
                  FROM dbo.FbJcOddsHistory
                  WHERE PlayType = 'NSPF' AND MatchId IN @ids AND OptionCode IN ('3','1','0')
              ) t
              WHERE rn = 1",
            new { ids }, cancellationToken: ct));

        foreach (var g in rows.GroupBy(r => r.MatchId))
        {
            decimal win = 0, draw = 0, loss = 0;
            foreach (var row in g)
            {
                if (row.OptionCode == "3") win = row.Odds;
                else if (row.OptionCode == "1") draw = row.Odds;
                else if (row.OptionCode == "0") loss = row.Odds;
            }
            if (win > 1 && draw > 1 && loss > 1)
                map[g.Key] = (win, draw, loss);
        }
        return map;
    }

    /// <summary>北单开奖页上的 SP。同一选项价格没变就不再写一行。</summary>
    public async Task InsertChangedAsync(IReadOnlyList<JcOddsHistoryPoint> points, CancellationToken ct = default)
    {
        if (points.Count == 0) return;
        var at = DateTime.Now;
        await using var conn = await _factory.OpenAsync(ct);
        foreach (var p in points)
        {
            if (p.MatchId is null || p.Odds <= 1) continue;
            await conn.ExecuteAsync(new CommandDefinition(
                @"INSERT INTO dbo.FbJcOddsHistory (MatchId, MatchNo, PlayType, OptionCode, OptionName, Odds, CapturedAt)
                  SELECT @mid, @no, @pt, @code, @name, @odds, @at
                  WHERE NOT EXISTS (
                      SELECT 1 FROM dbo.FbJcOddsHistory
                      WHERE MatchId = @mid AND PlayType = @pt AND OptionCode = @code AND Odds = @odds)",
                new
                {
                    mid = p.MatchId, no = p.MatchNo, pt = p.PlayType, code = p.OptionCode,
                    name = p.OptionName, odds = p.Odds, at
                }, cancellationToken: ct));
        }
    }

    private static string PlayName(string playType) => playType switch
    {
        JcPlayTypes.Nspf => "让球胜平负",
        JcPlayTypes.Spf => "胜平负",
        JcPlayTypes.Bf => "全场比分",
        JcPlayTypes.Jq => "总进球",
        JcPlayTypes.Bq => "半全场",
        _ => playType
    };
}
