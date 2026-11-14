using AiFootBall.Core.Models;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 源侧账本（2026-11-14 第七轮）。
///
/// 这一组测试存在的唯一理由：**「预期」不能取自引擎自己的输出**。
/// 1419319 那次核对照样通过，不是因为判据写错了，而是因为判据的输入就是引擎写的凭证——
/// 引擎在写凭证之前就跳过了那场，两边同时为 0。
/// 所以这里的每个用例都从**源侧**构造，然后证明「引擎少一场 / 少一档 / 自己跳过」
/// 都会被判失败，而不是被当成「本来就没有」。
/// </summary>
public class MilkSourceSlateTests
{
    private static readonly DateTime RunAt = new(2026, 11, 14, 8, 40, 0);
    private static readonly DateTime Fresh = RunAt.AddMinutes(-2);

    /// <summary>完整档位域（五玩法 54 档），顺序 SPF→NSPF→BQ→JQ→BF。</summary>
    private static List<string> FullKeys()
        => JcPlayDomain.Plays
            .SelectMany(p => JcPlayDomain.Of(p)!.Select(c => $"{p}|{c}"))
            .ToList();

    private static MilkSourceMatch Src(long id, string no, string home, string away, int options = 54,
        DateTime? sampleAt = null, IEnumerable<string>? keys = null)
        => new()
        {
            MatchId = id,
            MatchNo = no,
            LeagueName = "日职乙",
            HomeTeam = home,
            AwayTeam = away,
            MatchTime = RunAt.AddHours(4),
            SalesDate = RunAt.Date,
            LastSampleAt = sampleAt ?? Fresh,
            OptionKeys = keys?.ToList() ?? FullKeys().Take(options).ToList()
        };

    private static MilkInputMatch Inp(long id, string no, string home, string away, int options = 54)
        => InpFrom(id, no, home, away, FullKeys().Take(options));

    /// <summary>按指定档位键构造凭证——「引擎与源侧完全一致地少一档」这类用例要用它。</summary>
    private static MilkInputMatch InpFrom(long id, string no, string home, string away, IEnumerable<string> keys)
        => new()
        {
            MatchId = id, MatchNo = no, HomeTeam = home, AwayTeam = away,
            Options = keys.Select(k => new MilkInputOption
            {
                PlayType = k[..k.IndexOf('|')], OptionCode = k[(k.IndexOf('|') + 1)..]
            }).ToList()
        };

    private static MilkInputRejection Rej(long id, string no, string reason, string evidence)
        => new()
        {
            MatchId = id, MatchNo = no, HomeTeam = "大宫松鼠RB", AwayTeam = "山形山神",
            Reason = reason, Evidence = evidence
        };

    [Fact]
    public void MissingMatch_WithNoRejection_Fails()
    {
        // 1419319 的形态：源侧 6 场，引擎只写了 5 张凭证，剩下那场连拒绝记录都没有。
        var source = new[] { Src(1, "周六001", "A", "B"), Src(2, "周六002", "大宫松鼠RB", "山形山神") };
        var inputs = new[] { Inp(1, "周六001", "A", "B") };

        var r = MilkSourceSlate.Verify(source, inputs, Array.Empty<MilkInputRejection>(), RunAt);

        Assert.False(r.Ok);
        Assert.Contains(r.Issues, i => i.Kind == "missing-input" && i.Key.Contains("周六002"));
    }

    [Fact]
    public void NameUnresolved_ButTeamHasGames_Fails_WithEvidence()
    {
        // 引擎说「我不认识大宫松鼠RB」，独立库检说这支队近 18 个月有 39 场 ——
        // 那就不是新队，是队名归一化缺陷。拒绝记录再规范，这一轮也不许发布。
        var source = new[] { Src(1, "周六001", "A", "B"), Src(2, "周六002", "大宫松鼠RB", "山形山神") };
        var inputs = new[] { Inp(1, "周六001", "A", "B") };
        var rejects = new[]
        {
            Rej(2, "周六002", MilkInputRejection.ReasonNameUnresolved, "team=大宫松鼠RB;league=日职乙;games=0;min=5")
        };

        var r = MilkSourceSlate.Verify(source, inputs, rejects, RunAt,
            name => name == "大宫松鼠RB" ? 39 : 0);

        Assert.False(r.Ok);
        Assert.Contains(r.Issues, i => i.Kind == "rejected-rejection" && i.Key.Contains("39"));
    }

    [Fact]
    public void NameUnresolved_AndTrulyNewTeam_IsAcceptedAsNewTeam()
    {
        var source = new[] { Src(1, "周六002", "首次出现FC", "山形山神") };
        var rejects = new[]
        {
            Rej(1, "周六002", MilkInputRejection.ReasonNameUnresolved, "team=首次出现FC;league=日职乙;games=0;min=5")
        };

        var r = MilkSourceSlate.Verify(source, Array.Empty<MilkInputMatch>(), rejects, RunAt, _ => 0);

        Assert.True(r.Ok, r.Detail);
        var one = Assert.Single(r.Rejections);
        Assert.Equal(MilkInputRejection.ReasonNewTeam, one.Reason);
        Assert.True(one.Accepted);
        Assert.Contains("dbGames=0", one.Evidence);
    }

    [Fact]
    public void NameUnresolved_WithoutIndependentLookup_IsRejected()
    {
        // 查不到库 = 无法证明是新队。宁可少发一轮，也不把「认不出」当「新队」。
        var source = new[] { Src(1, "周六002", "某队", "山形山神") };
        var rejects = new[]
        {
            Rej(1, "周六002", MilkInputRejection.ReasonNameUnresolved, "team=某队;league=日职乙;games=0;min=5")
        };

        var r = MilkSourceSlate.Verify(source, Array.Empty<MilkInputMatch>(), rejects, RunAt, _ => -1);

        Assert.False(r.Ok);
        Assert.Contains(r.Issues, i => i.Kind == "rejected-rejection" && i.Key.Contains("无法证明"));
    }

    [Fact]
    public void EngineSaysNoOdds_ButSourceHasOptions_Fails()
    {
        // 「历史赔率有记录不等于本轮仍有效」的反向：源侧明明有档，
        // 引擎却拿了个空集回来 —— 那是取数错误，不是没价。
        var source = new[] { Src(1, "周六001", "A", "B") };
        var rejects = new[] { Rej(1, "周六001", MilkInputRejection.ReasonNoOdds, "options=0") };

        var r = MilkSourceSlate.Verify(source, Array.Empty<MilkInputMatch>(), rejects, RunAt);

        Assert.False(r.Ok);
        Assert.Contains(r.Issues, i => i.Kind == "contradicted-rejection");
    }

    [Fact]
    public void SourceMissingOneOption_Fails_EvenWhenEngineMatchesSource()
    {
        // 源侧自己少采一档、引擎照着少算一档：两边同时「对」，正是这一轮要堵的洞。
        // 判据只能是玩法域，不能是「源侧采到几档」。
        var keys = FullKeys();
        keys.Remove("BF|1:0");
        var source = new[] { Src(1, "周六001", "A", "B", keys: keys) };
        var inputs = new[] { InpFrom(1, "周六001", "A", "B", keys) };

        var r = MilkSourceSlate.Verify(source, inputs, Array.Empty<MilkInputRejection>(), RunAt);

        Assert.False(r.Ok);
        Assert.Contains(r.Issues, i => i.Kind == "source-option-gap"
                                       && i.Key.Contains("BF") && i.Key.Contains("1:0"));
        // 引擎与源侧完全一致，所以不该有「引擎少算」的报错——两件事必须分开。
        Assert.DoesNotContain(r.Issues, i => i.Kind == "missing-option");
    }

    [Fact]
    public void SourceMissingWholePlay_CannotBeExemptedByNoOdds()
    {
        // 「没采到赔率」不能当成「未启售」。整块缺 SPF 时：
        //   ① 玩法域判缺档；
        //   ② 引擎据此报 no-odds 也不成立（源侧有其它玩法），属 contradicted-rejection。
        var keys = FullKeys().Where(k => !k.StartsWith("SPF|", StringComparison.Ordinal)).ToList();
        var source = new[] { Src(1, "周六001", "A", "B", keys: keys) };
        var rejects = new[] { Rej(1, "周六001", MilkInputRejection.ReasonNoOdds, "options=0;source=0") };

        var r = MilkSourceSlate.Verify(source, Array.Empty<MilkInputMatch>(), rejects, RunAt);

        Assert.False(r.Ok);
        Assert.Contains(r.Issues, i => i.Kind == "source-option-gap" && i.Key.Contains("SPF"));
        Assert.Contains(r.Issues, i => i.Kind == "contradicted-rejection");
        Assert.DoesNotContain(r.Rejections, x => x.Accepted);
    }

    [Fact]
    public void TotalOptionCountIsNotTheCriterion()
    {
        // 档数仍是 54（BF 少一档、BQ 多一条脏代码），只看总数会判「齐」。
        // 生产中半全场真出现过 `平/胜`/`平/负`/`平/平` 这类脏代码，所以必须逐档位比对。
        var keys = FullKeys();
        keys.Remove("BF|2:1");
        keys.Add("BQ|平/胜");

        Assert.Equal(54, keys.Distinct().Count());

        var r = MilkSourceSlate.Verify(
            new[] { Src(1, "周六001", "A", "B", keys: keys) }, Array.Empty<MilkInputMatch>(),
            new[] { Rej(1, "周六001", MilkInputRejection.ReasonThinSample, "games=1/2;min=5") }, RunAt);

        Assert.False(r.Ok);
        Assert.Contains(r.Issues, i => i.Kind == "source-option-gap" && i.Key.Contains("2:1"));
        Assert.DoesNotContain(r.Issues, i => i.Kind == "source-option-gap" && i.Key.Contains("BQ"));
    }

    [Fact]
    public void DirtyExtraOption_AloneDoesNotBlock()
    {
        // 多一条域外脏码是数据卫生问题，不是「少覆盖」——不能因为它挡住发布。
        var keys = FullKeys();
        keys.Add("BQ|平/胜");
        var source = new[] { Src(1, "周六001", "A", "B", keys: keys) };

        var r = MilkSourceSlate.Verify(source, new[] { Inp(1, "周六001", "A", "B") },
            Array.Empty<MilkInputRejection>(), RunAt);

        Assert.True(r.Ok, r.Detail);
    }

    [Fact]
    public void MissingOneOptionInVoucher_Fails()
    {
        // 源侧 54 档，输入凭证只有 53 档：档位层面的「少算」。
        // 这一层之前没人查：预期从输入推出，输入少一档，预期也跟着少一档，两边同时「对」。
        var source = new[] { Src(1, "周六001", "A", "B", options: 54) };
        var inputs = new[] { Inp(1, "周六001", "A", "B", options: 53) };

        var r = MilkSourceSlate.Verify(source, inputs, Array.Empty<MilkInputRejection>(), RunAt);

        Assert.False(r.Ok);
        Assert.Contains(r.Issues, i => i.Kind == "missing-option");
    }

    [Fact]
    public void StaleSample_Fails_ButFreshPasses()
    {
        var stale = new[] { Src(1, "周六001", "A", "B", sampleAt: RunAt.AddMinutes(-31)) };
        var fresh = new[] { Src(1, "周六001", "A", "B", sampleAt: RunAt.AddMinutes(-29)) };
        var inputs = new[] { Inp(1, "周六001", "A", "B") };

        var bad = MilkSourceSlate.Verify(stale, inputs, Array.Empty<MilkInputRejection>(), RunAt);
        var ok = MilkSourceSlate.Verify(fresh, inputs, Array.Empty<MilkInputRejection>(), RunAt);

        Assert.Contains(bad.Issues, i => i.Kind == "stale-sample");
        Assert.True(ok.Ok, ok.Detail);
    }

    [Fact]
    public void BothComputedAndRejected_IsDuplicate()
    {
        var source = new[] { Src(1, "周六001", "A", "B") };
        var inputs = new[] { Inp(1, "周六001", "A", "B") };
        var rejects = new[] { Rej(1, "周六001", MilkInputRejection.ReasonThinSample, "games=1/2;min=5") };

        var r = MilkSourceSlate.Verify(source, inputs, rejects, RunAt);

        Assert.Contains(r.Issues, i => i.Kind == "duplicate-input");
    }

    [Fact]
    public void InputNotInSource_IsUnexpected()
    {
        var source = new[] { Src(1, "周六001", "A", "B") };
        var inputs = new[] { Inp(1, "周六001", "A", "B"), Inp(9, "周六009", "X", "Y") };

        var r = MilkSourceSlate.Verify(source, inputs, Array.Empty<MilkInputRejection>(), RunAt);

        Assert.Contains(r.Issues, i => i.Kind == "unexpected-input" && i.Key.Contains("周六009"));
    }

    [Theory]
    [InlineData(MilkInputRejection.ReasonNotSale, "sale=closed")]
    [InlineData(MilkInputRejection.ReasonModelNotReady, "ready=false")]
    public void NonAcceptedReasons_Fail_EvenWithPerfectEvidence(string reason, string evidence)
    {
        // 上一轮的教训：把「引擎自己跳过」写进枚举，等于开了一条万能放行口子。
        var source = new[] { Src(1, "周六001", "A", "B") };
        var rejects = new[] { Rej(1, "周六001", reason, evidence) };

        var r = MilkSourceSlate.Verify(source, Array.Empty<MilkInputMatch>(), rejects, RunAt,
            _ => 0);

        Assert.False(r.Ok);
        Assert.Contains(r.Issues, i => i.Kind == "rejected-rejection");
    }

    [Fact]
    public void UnknownReason_OrMissingEvidence_Fails()
    {
        var source = new[] { Src(1, "周六001", "A", "B"), Src(2, "周六002", "C", "D") };
        var rejects = new[]
        {
            Rej(1, "周六001", "engine-skipped", "whatever"),
            Rej(2, "周六002", MilkInputRejection.ReasonThinSample, "")
        };

        var r = MilkSourceSlate.Verify(source, Array.Empty<MilkInputMatch>(), rejects, RunAt);

        Assert.Equal(2, r.Issues.Count(i => i.Kind == "rejected-rejection"));
    }

    [Fact]
    public void ThinSampleWithEvidence_IsLegitimate()
    {
        // 真样本不足是规则内的拒绝（模型确实没这支队的数据），不能跟队名缺陷混为一谈。
        var source = new[] { Src(1, "周六001", "A", "B") };
        var rejects = new[] { Rej(1, "周六001", MilkInputRejection.ReasonThinSample, "games=1/2;min=5;league=日职乙") };

        var r = MilkSourceSlate.Verify(source, Array.Empty<MilkInputMatch>(), rejects, RunAt);

        Assert.True(r.Ok, r.Detail);
        Assert.True(Assert.Single(r.Rejections).Accepted);
    }

    [Fact]
    public void ZeroSource_ZeroInput_ZeroRejection_Passes()
    {
        // 没有受注场次的一轮：两边都空，本来就通过，不需要任何豁免。
        var r = MilkSourceSlate.Verify(
            Array.Empty<MilkSourceMatch>(), Array.Empty<MilkInputMatch>(),
            Array.Empty<MilkInputRejection>(), RunAt);

        Assert.True(r.Ok);
        Assert.Equal(0, r.ComputedCount);
    }
}
