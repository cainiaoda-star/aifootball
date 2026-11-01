using AiFootBall.Core.Models;
using Dapper;

namespace AiFootBall.Data;

public sealed class LottoRecommendationRepository
{
    private readonly SqlConnectionFactory _factory;
    public LottoRecommendationRepository(SqlConnectionFactory factory) => _factory = factory;

    public async Task ReplacePlanAsync(LottoPlanSet plan, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync(new CommandDefinition(
            @"DELETE FROM dbo.LottoRecommendation
              WHERE LotteryCode = @code AND TargetIssue = @issue",
            new { code = plan.LotteryCode, issue = plan.TargetIssue }, tx, cancellationToken: ct));

        foreach (var p in plan.Picks)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                @"INSERT INTO dbo.LottoRecommendation
                    (LotteryCode, TargetIssue, BasedOnIssue, RankNo, IsPrimary, PlayType,
                     RedNumbers, BlueNumbers, Score, SignalDesc, RecDate)
                  VALUES
                    (@code, @issue, @based, @rank, @primary, @play,
                     @reds, @blues, @score, @desc, @rd)",
                new
                {
                    code = plan.LotteryCode,
                    issue = plan.TargetIssue,
                    based = plan.BasedOnIssue,
                    rank = p.RankNo,
                    primary = p.IsPrimary,
                    play = p.PlayType,
                    reds = p.RedCsv,
                    blues = p.BlueCsv,
                    score = p.Score,
                    desc = p.SignalDesc,
                    rd = plan.RecDate.ToDateTime(TimeOnly.MinValue)
                }, tx, cancellationToken: ct));
        }
        tx.Commit();
    }

    public async Task<List<LottoRecommendationRecord>> GetByCodeAsync(string code, string? issue = null,
        CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        if (!string.IsNullOrWhiteSpace(issue))
        {
            return (await conn.QueryAsync<LottoRecommendationRecord>(new CommandDefinition(
                @"SELECT * FROM dbo.LottoRecommendation
                  WHERE LotteryCode = @code AND TargetIssue = @issue
                  ORDER BY RankNo",
                new { code, issue }, cancellationToken: ct))).ToList();
        }

        return (await conn.QueryAsync<LottoRecommendationRecord>(new CommandDefinition(
            @"SELECT * FROM dbo.LottoRecommendation
              WHERE LotteryCode = @code
                AND TargetIssue = (
                    SELECT TOP 1 TargetIssue FROM dbo.LottoRecommendation
                    WHERE LotteryCode = @code ORDER BY RecDate DESC, TargetIssue DESC)
              ORDER BY RankNo",
            new { code }, cancellationToken: ct))).ToList();
    }

    /// <summary>最近若干期已保存的选号，含主推和备选。同一期只留保存下来的那一套。</summary>
    public async Task<List<LottoRecommendationRecord>> GetRecentIssuesAsync(
        string code, int issueCount, CancellationToken ct = default)
    {
        var take = issueCount < 1 ? 1 : issueCount;
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<LottoRecommendationRecord>(new CommandDefinition(
            @"SELECT r.*
              FROM dbo.LottoRecommendation r
              INNER JOIN (
                  SELECT TOP (@n) TargetIssue
                  FROM dbo.LottoRecommendation
                  WHERE LotteryCode = @code
                  GROUP BY TargetIssue
                  ORDER BY MAX(RecDate) DESC, TRY_CAST(TargetIssue AS BIGINT) DESC, TargetIssue DESC
              ) i ON i.TargetIssue = r.TargetIssue
              WHERE r.LotteryCode = @code
              ORDER BY TRY_CAST(r.TargetIssue AS BIGINT) DESC, r.TargetIssue DESC, r.RankNo",
            new { code, n = take }, cancellationToken: ct))).ToList();
    }

    public async Task<List<LottoRecommendationRecord>> GetLatestAllAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<LottoRecommendationRecord>(new CommandDefinition(
            @"SELECT r.* FROM dbo.LottoRecommendation r
              INNER JOIN (
                  SELECT LotteryCode, MAX(RecDate) AS RecDate
                  FROM dbo.LottoRecommendation
                  WHERE LotteryCode IN @codes
                  GROUP BY LotteryCode
              ) x ON x.LotteryCode = r.LotteryCode AND x.RecDate = r.RecDate
              INNER JOIN (
                  SELECT LotteryCode, RecDate, MAX(TargetIssue) AS TargetIssue
                  FROM dbo.LottoRecommendation
                  WHERE LotteryCode IN @codes
                  GROUP BY LotteryCode, RecDate
              ) y ON y.LotteryCode = r.LotteryCode AND y.RecDate = r.RecDate AND y.TargetIssue = r.TargetIssue
              WHERE r.LotteryCode IN @codes
              ORDER BY r.LotteryCode, r.RankNo",
            new { codes = LottoCodes.All }, cancellationToken: ct))).ToList();
    }
}
