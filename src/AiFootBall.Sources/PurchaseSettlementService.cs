using AiFootBall.Core.Models;
using AiFootBall.Data;

namespace AiFootBall.Sources;

/// <summary>购买方案自动结算。竞彩、北单用完场比分和赔率；胜负彩、任九用 500.com 开奖页的彩果和税前单注奖金。</summary>
public sealed class PurchaseSettlementService
{
    private readonly PurchaseRepository _purchases;
    private readonly FootballRepository _football;
    private readonly BettingCardSource _cards;

    public PurchaseSettlementService(PurchaseRepository purchases, FootballRepository football, BettingCardSource cards)
    {
        _purchases = purchases;
        _football = football;
        _cards = cards;
    }

    public sealed class ItemResult
    {
        public long PurchaseId { get; set; }
        public string Status { get; set; } = "";
        public string? Message { get; set; }
        public decimal? ActualWin { get; set; }
        public int LinesReady { get; set; }
        public int LinesTotal { get; set; }
    }

    public sealed class SettleBatchResult
    {
        public int Checked { get; set; }
        public int Settled { get; set; }
        public int Won { get; set; }
        public int Lost { get; set; }
        public int Waiting { get; set; }
        public int Skipped { get; set; }
        public List<ItemResult> Items { get; set; } = new();
    }

    /// <summary>结算全部待开奖方案；可选只结算指定 ID。</summary>
    public async Task<SettleBatchResult> AutoSettleAsync(long? purchaseId = null, CancellationToken ct = default)
    {
        var result = new SettleBatchResult();
        List<PurchaseRecord> list;
        if (purchaseId is > 0)
        {
            var one = await _purchases.GetByIdAsync(purchaseId.Value, ct);
            list = one is null ? new() : new() { one };
        }
        else
        {
            list = await _purchases.GetUnsettledAsync(ct);
        }

        result.Checked = list.Count;
        var force = purchaseId is > 0; // 指定 ID 时允许重算已结方案
        foreach (var p in list)
        {
            if (!force && p.Status is "WON" or "LOST" or "CANCELLED")
            {
                result.Skipped++;
                result.Items.Add(new ItemResult { PurchaseId = p.PurchaseId, Status = "SKIP", Message = "已结算或已撤销" });
                continue;
            }
            if (force && p.Status == "CANCELLED")
            {
                result.Skipped++;
                result.Items.Add(new ItemResult { PurchaseId = p.PurchaseId, Status = "SKIP", Message = "已撤销" });
                continue;
            }

            var item = await SettleOneAsync(p, ct);
            result.Items.Add(item);
            switch (item.Status)
            {
                case "WON": result.Settled++; result.Won++; break;
                case "LOST": result.Settled++; result.Lost++; break;
                case "WAIT": result.Waiting++; break;
                default: result.Skipped++; break;
            }
        }
        return result;
    }

    private async Task<ItemResult> SettleOneAsync(PurchaseRecord p, CancellationToken ct)
    {
        if (p.Lines.Count == 0)
            return new ItemResult { PurchaseId = p.PurchaseId, Status = "SKIP", Message = "无场次明细" };
        if (IsSfc(p))
            return await SettleSfcAsync(p, ct);

        foreach (var line in p.Lines)
        {
            if (line.MatchId is > 0) continue;
            var mid = await _football.ResolveMatchIdByNoAsync(line.MatchNo, ct)
                      ?? await _football.ResolveMatchIdByTeamsAsync(line.HomeTeam, line.AwayTeam, ct);
            if (mid is > 0)
            {
                line.MatchId = mid;
                await _purchases.SetLineMatchIdAsync(p.PurchaseId, line.Seq, mid.Value, ct);
            }
        }

        var finished = await _football.GetFinishedByIdsAsync(
            p.Lines.Where(l => l.MatchId is > 0).Select(l => l.MatchId!.Value), ct);

        var lineSettles = new List<PurchaseLineSettle>();
        var legs = new List<(bool hit, decimal? odds)>();
        var pending = 0;
        var missing = 0;
        var noHandicap = 0;

        foreach (var line in p.Lines.OrderBy(l => l.Seq))
        {
            if (line.MatchId is null or <= 0 || !finished.TryGetValue(line.MatchId.Value, out var m))
            {
                if (line.MatchId is null or <= 0) missing++;
                else pending++;
                legs.Add((false, null));
                continue;
            }

            // 竞彩胜平负按实际比分，不用完场表上的让球盘。让球玩法才取行上盘口，没有再用完场盘口。
            // 北单只有让球胜平负：票面写「胜平负」也按北单开出的让球数结算，不能用完场表（竞彩）的盘口。
            var bd = string.Equals(p.Board, Boards.Bd, StringComparison.OrdinalIgnoreCase);
            var plain = string.Equals(p.PlayType, "胜平负", StringComparison.Ordinal);
            decimal? hcp;
            if (bd && plain)
            {
                hcp = line.Handicap ?? await _football.GetBdHandicapAsync(line.MatchId.Value, ct);
                if (hcp is null)
                {
                    noHandicap++;
                    legs.Add((false, null));
                    continue;
                }
            }
            else
                hcp = plain ? null : line.Handicap ?? m.Handicap;
            var score = new PurchaseSettleMath.MatchScore
            {
                Home = m.HomeScore!.Value,
                Away = m.AwayScore!.Value,
                HalfHome = m.HalfHomeScore,
                HalfAway = m.HalfAwayScore,
                Handicap = hcp
            };
            // 玩法以选项为准纠偏（避免「让胜」被错误 playType 当比分）
            var play = bd && plain ? "让球胜平负" : ResolvePlayType(p.PlayType, line);
            var hit = PurchaseSettleMath.EvaluateLine(play, line, score);
            legs.Add((hit.Hit, hit.Odds));
            lineSettles.Add(new PurchaseLineSettle
            {
                Seq = line.Seq,
                ActualScore = score.ScoreColon,
                ActualGoals = score.Goals,
                ActualResult = score.Spf,
                LineHit = hit.Hit
            });
        }

        if (lineSettles.Count > 0)
            await _purchases.UpdateLineResultsAsync(p.PurchaseId, lineSettles, ct);

        if (pending > 0 || missing > 0 || noHandicap > 0 || lineSettles.Count < p.Lines.Count)
        {
            return new ItemResult
            {
                PurchaseId = p.PurchaseId,
                Status = "WAIT",
                Message = noHandicap > 0
                    ? $"北单有 {noHandicap} 场找不到让球数，请在票上补填让球后再结算"
                    : missing > 0
                    ? $"尚有 {missing} 场未关联赛程，{pending} 场未完场"
                    : $"尚有 {pending} 场未完场",
                LinesReady = lineSettles.Count,
                LinesTotal = p.Lines.Count
            };
        }

        var prize = PurchaseSettleMath.CalculatePrize(legs, p.PassType, p.Multiple);
        var allHit = legs.All(l => l.hit);
        // 全中但赔率缺失导致奖金算不出时，回退预计奖金
        if (prize <= 0 && allHit)
            prize = p.ExpectedWin ?? 0;

        var isHit = prize > 0 || (allHit && (p.PassType is null || PurchaseSettleMath.ParsePassSizes(p.PassType, p.Lines.Count).Count == 0));
        if (!isHit) prize = 0;

        // 纠正误标玩法（让胜票却写成全场比分）。已标明胜平负的票不再改成让球。
        string? fixPlay = null;
        var declaredPlain = string.Equals(p.PlayType, "胜平负", StringComparison.Ordinal);
        if (!declaredPlain && p.Lines.Any(l => $"{l.Option1}|{l.Option2}".Contains('让', StringComparison.Ordinal)))
            fixPlay = "让球胜平负";
        else if (!declaredPlain && p.Lines.Any(l => System.Text.RegularExpressions.Regex.IsMatch($"{l.Option1}|{l.Option2}", @"\d:\d")))
            fixPlay = "全场比分";

        await _purchases.SettleAsync(new PurchaseSettleRequest
        {
            PurchaseId = p.PurchaseId,
            ActualWin = prize,
            IsHit = isHit,
            HitLevel = isHit ? p.PassType : null,
            Remark = "自动结算",
            PlayType = fixPlay,
            Lines = lineSettles
        }, ct);

        return new ItemResult
        {
            PurchaseId = p.PurchaseId,
            Status = isHit ? "WON" : "LOST",
            ActualWin = prize,
            Message = isHit ? $"中奖 {prize:0.##} 元" : "未中奖",
            LinesReady = lineSettles.Count,
            LinesTotal = p.Lines.Count
        };
    }

    private static bool IsSfc(PurchaseRecord p) =>
        string.Equals(p.Board, Boards.Sfc, StringComparison.OrdinalIgnoreCase)
        || p.PlayType is "胜负彩" or "任选九";

    /// <summary>胜负彩、任九只认 500.com 开奖页。页面没有 14 个彩果或没有奖金表时继续等，不按赔率估。</summary>
    private async Task<ItemResult> SettleSfcAsync(PurchaseRecord p, CancellationToken ct)
    {
        var expect = (p.Expect ?? "").Trim();
        if (expect.Length == 0 || expect.Any(c => !char.IsDigit(c)))
        {
            return new ItemResult
            {
                PurchaseId = p.PurchaseId,
                Status = "WAIT",
                Message = "没有期号，对不上 500.com 开奖",
                LinesTotal = p.Lines.Count
            };
        }

        var renjiu = p.PlayType == "任选九";
        var parsed = new List<(PurchaseLineRecord Line, int No, HashSet<char> Opts)>();
        foreach (var line in p.Lines)
        {
            if (!int.TryParse(line.MatchNo?.Trim(), out var no) || no is < 1 or > 14)
            {
                return new ItemResult
                {
                    PurchaseId = p.PurchaseId,
                    Status = "WAIT",
                    Message = "场次号对不上 1 到 14，不能按 500.com 开奖",
                    LinesTotal = p.Lines.Count
                };
            }
            var opts = SfcTicketMath.ParseOptions(line.Option1, line.Option2);
            if (opts.Count == 0)
            {
                return new ItemResult
                {
                    PurchaseId = p.PurchaseId,
                    Status = "WAIT",
                    Message = $"第 {no} 场没有胜平负选项",
                    LinesTotal = p.Lines.Count
                };
            }
            parsed.Add((line, no, opts));
        }
        if (parsed.Select(x => x.No).Distinct().Count() != parsed.Count)
        {
            return new ItemResult
            {
                PurchaseId = p.PurchaseId,
                Status = "WAIT",
                Message = "场次号重复，不能结算",
                LinesTotal = p.Lines.Count
            };
        }
        if (!renjiu && parsed.Count != 14)
        {
            return new ItemResult
            {
                PurchaseId = p.PurchaseId,
                Status = "WAIT",
                Message = "胜负彩要 14 场才能对开奖",
                LinesTotal = p.Lines.Count
            };
        }
        if (renjiu && parsed.Count is < 9 or > 14)
        {
            return new ItemResult
            {
                PurchaseId = p.PurchaseId,
                Status = "WAIT",
                Message = "任选九要 9 到 14 场才能对开奖",
                LinesTotal = p.Lines.Count
            };
        }

        SfcDraw? draw;
        try
        {
            draw = await _cards.FetchSfcDrawAsync(expect, ct);
        }
        catch
        {
            draw = null;
        }
        if (draw is null || !draw.Complete || draw.Result.Length != 14)
        {
            return new ItemResult
            {
                PurchaseId = p.PurchaseId,
                Status = "WAIT",
                Message = $"500.com 还没有第 {expect} 期开奖，或页面打不开",
                LinesTotal = p.Lines.Count
            };
        }

        var wins = SfcTicketMath.CountWins(parsed.Select(x => (x.No, x.Opts)).ToList(), draw.Result);
        var prize = SfcTicketMath.PrizeYuan(renjiu, wins, draw.FirstPrize, draw.SecondPrize, draw.RenjiuPrize, p.Multiple);
        var hitCount = renjiu ? wins.Renjiu : wins.Fourteen + wins.Thirteen;
        var isHit = hitCount > 0;
        string? level = null;
        if (renjiu)
        {
            if (wins.Renjiu > 0) level = "任九";
        }
        else if (wins.Fourteen > 0)
            level = "一等奖";
        else if (wins.Thirteen > 0)
            level = "二等奖";

        var lineSettles = parsed.Select(x =>
        {
            var actual = draw.Result[x.No - 1];
            return new PurchaseLineSettle
            {
                Seq = x.Line.Seq,
                ActualScore = SfcTicketMath.Label(actual),
                ActualResult = actual,
                LineHit = x.Opts.Contains(actual)
            };
        }).ToList();
        if (lineSettles.Count > 0)
            await _purchases.UpdateLineResultsAsync(p.PurchaseId, lineSettles, ct);

        var remark =
            $"500.com 第 {expect} 期开奖 {draw.Result}。" +
            $"一等奖单注税前 {draw.FirstPrize:0.##}，二等奖 {draw.SecondPrize:0.##}，任九 {draw.RenjiuPrize:0.##}。" +
            $"中奖注数一等奖 {wins.Fourteen}、二等奖 {wins.Thirteen}、任九 {wins.Renjiu}。" +
            "奖金按公布的税前单注，不是赔率连乘。";
        await _purchases.SettleAsync(new PurchaseSettleRequest
        {
            PurchaseId = p.PurchaseId,
            ActualWin = prize,
            IsHit = isHit,
            HitLevel = level,
            Remark = remark,
            Lines = lineSettles
        }, ct);

        return new ItemResult
        {
            PurchaseId = p.PurchaseId,
            Status = isHit ? "WON" : "LOST",
            ActualWin = prize,
            Message = isHit ? $"{level} {prize:0.##} 元（税前）" : "未中奖",
            LinesReady = lineSettles.Count,
            LinesTotal = p.Lines.Count
        };
    }

    /// <summary>根据选项纠正玩法标签（OCR/手填可能把让球票标成全场比分）。</summary>
    private static string? ResolvePlayType(string? playType, PurchaseLineRecord line)
    {
        if (string.Equals(playType, "胜平负", StringComparison.Ordinal)) return "胜平负";
        var opts = $"{line.Option1}|{line.Option2}";
        if (opts.Contains('让', StringComparison.Ordinal)) return "让球胜平负";
        if (System.Text.RegularExpressions.Regex.IsMatch(opts, @"\d:\d")) return "全场比分";
        if ((playType ?? "").Contains("让球", StringComparison.Ordinal)) return "让球胜平负";
        return playType;
    }
}
