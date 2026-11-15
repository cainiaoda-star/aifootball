using AiFootBall.Core.Models;
using Dapper;
using Microsoft.Data.SqlClient;

namespace AiFootBall.Data;

/// <summary>用户选号票（避热门号选号器 / 手工录入）。只追加，不覆盖。</summary>
public sealed class LottoUserTicketRepository
{
    private readonly SqlConnectionFactory _factory;
    public LottoUserTicketRepository(SqlConnectionFactory factory) => _factory = factory;

    /// <summary>保存一批票。调用方负责算 TargetIssue（一般取最新开奖期号 +1）。</summary>
    public async Task<int> SaveAsync(string code, string targetIssue, DateOnly recDate,
        List<LottoUserTicketIn> tickets, CancellationToken ct = default)
    {
        if (tickets.Count == 0) return 0;
        await using var conn = await _factory.OpenAsync(ct);
        var rows = tickets.Select(t => new
        {
            code,
            targetIssue,
            RedNumbers = string.Join(",", t.Reds.Select(x => x.ToString("D2"))),
            BlueNumbers = t.Blues.Count == 0 ? null : string.Join(",", t.Blues.Select(x => x.ToString("D2"))),
            // PlayType / BetAmount 须由服务端校验后写入，禁止此处默默改成「单式」或覆盖金额。
            PlayType = t.PlayType,
            t.CoolScore,
            LevelName = t.LevelName,
            Source = string.IsNullOrWhiteSpace(t.Source) ? "pick" : t.Source,
            BetAmount = t.BetAmount,
            recDate
        }).ToList();
        return await conn.ExecuteAsync(new CommandDefinition(
            @"INSERT INTO dbo.LottoUserTicket
                (LotteryCode, TargetIssue, RedNumbers, BlueNumbers, PlayType, CoolScore, LevelName, Source, BetAmount, RecDate)
              VALUES (@code, @targetIssue, @RedNumbers, @BlueNumbers, @PlayType, @CoolScore, @LevelName, @Source, @BetAmount, @recDate)",
            rows, cancellationToken: ct));
    }

    /// <summary>按彩种取最近 N 天的票（含未开奖期）。</summary>
    public async Task<List<LottoUserTicketRow>> GetRecentAsync(string code, int days, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<LottoUserTicketRow>(new CommandDefinition(
            @"SELECT TicketId, LotteryCode, TargetIssue, RedNumbers, BlueNumbers, PlayType,
                     CoolScore, LevelName, Source, BetAmount, RecDate, CreatedAt
              FROM dbo.LottoUserTicket
              WHERE LotteryCode = @code AND RecDate >= DATEADD(day, -@days, CONVERT(DATE, GETDATE()))
              ORDER BY TargetIssue, TicketId",
            new { code, days }, cancellationToken: ct))).ToList();
    }
}

/// <summary>待入库的一注。</summary>
public sealed class LottoUserTicketIn
{
    public List<int> Reds { get; set; } = new();
    public List<int> Blues { get; set; } = new();
    /// <summary>玩法；缺省由服务端按彩种规范化（快乐8→选十），勿默认写成单式。</summary>
    public string PlayType { get; set; } = "";
    /// <summary>胆拖拖码。本轮用户票不落库；若客户端传入非空则校验拒绝，禁止静默丢弃。</summary>
    public List<int>? TuoReds { get; set; }
    public decimal? CoolScore { get; set; }
    public string? LevelName { get; set; }
    public string? Source { get; set; }
    public decimal BetAmount { get; set; } = 2m;
}

/// <summary>API 保存请求体。</summary>
public sealed class LottoUserTicketSaveRequest
{
    public string Code { get; set; } = "";
    /// <summary>目标期号。必填；服务端不做最新开奖 +1 猜测。</summary>
    public string? TargetIssue { get; set; }
    public List<LottoUserTicketIn> Tickets { get; set; } = new();
}

/// <summary>落库行。</summary>
public sealed class LottoUserTicketRow
{
    public long TicketId { get; set; }
    public string LotteryCode { get; set; } = "";
    public string TargetIssue { get; set; } = "";
    public string RedNumbers { get; set; } = "";
    public string? BlueNumbers { get; set; }
    public string PlayType { get; set; } = "";
    public decimal? CoolScore { get; set; }
    public string? LevelName { get; set; }
    public string Source { get; set; } = "pick";
    public decimal BetAmount { get; set; }
    public DateOnly RecDate { get; set; }
    public DateTime CreatedAt { get; set; }
}
