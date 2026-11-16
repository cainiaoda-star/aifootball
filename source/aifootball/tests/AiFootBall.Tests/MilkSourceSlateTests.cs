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
        DateTime? sampleAt = null, IEnumerable<string>? keys = null, string league = "日职乙")
        => new()
        {
            MatchId = id,
            MatchNo = no,
            LeagueName = league,
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

    private static MilkInputRejection Rej(long id, string no, string reason, string evidence,
        string home = "大宫松鼠RB", string away = "山形山神")
        => new()
        {
            MatchId = id, MatchNo = no, HomeTeam = home, AwayTeam = away,
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
    public void NameUnresolved_ZeroGames_EvenWithCleanVariantProbe_IsRejected()
    {
        // ★ 2026-11-14 第十二轮：这条替换掉原来那条「0 场 + 变体探查为空 → 升级 new-team 并放行」。
        //
        // 为什么取消了：计数回答的是「用**这个名字**查到几场」，而 name-unresolved 的成因
        // 恰恰是「名字写法不同」。「谢周三」0 场 / 「谢菲尔德星期三」74 场、
        // 「不来梅」0 场 / 「云达不莱梅」58 场——同一个零把「真新队」与「换了个写法」判成一件事。
        // 变体探查查不到，也只说明「附近没有相似写法」，仍然证明不了「这支队是新的」。
        //
        // 所以现在两种结局都是阻断：查到写法 → 队名解析缺陷；查不到 → 仍然无法确认身份。
        var source = new[] { Src(1, "周六002", "首次出现FC", "山形山神") };
        var rejects = new[]
        {
            Rej(1, "周六002", MilkInputRejection.ReasonNameUnresolved, "team=首次出现FC;league=日职乙;games=0;min=3",
                home: "首次出现FC", away: "山形山神")
        };

        var r = MilkSourceSlate.Verify(source, Array.Empty<MilkInputMatch>(), rejects, RunAt,
            _ => 0, nameVariants: _ => Array.Empty<string>());

        Assert.False(r.Ok);
        var issue = Assert.Single(r.Issues, i => i.Kind == "rejected-rejection");
        Assert.Contains("队名无法确认", issue.Key);
        Assert.Contains("0 场", issue.Key);
        Assert.Contains("不能作为新队证明", issue.Key);
        Assert.DoesNotContain(r.Rejections, x => x.Accepted);
        // 原因码保持原样：不许在核对器里改写成 new-team（那等于用结论证明结论）。
        Assert.DoesNotContain(r.Rejections, x => x.Reason == MilkInputRejection.ReasonNewTeam);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void NameUnresolved_LowButNonZeroGames_IsNotAutoExempt(int games)
    {
        // 「1~2 场」是最容易被误判成「新队」的区间：既不是 0，又达不到门槛 3。
        // 但它同样证明不了身份——样本少只说明这个名字在库里出现得少。
        var source = new[] { Src(1, "周六002", "某支新队", "山形山神") };
        var rejects = new[]
        {
            Rej(1, "周六002", MilkInputRejection.ReasonNameUnresolved,
                $"team=某支新队;league=日职乙;games={games};min=3",
                home: "某支新队", away: "山形山神")
        };

        var r = MilkSourceSlate.Verify(source, Array.Empty<MilkInputMatch>(), rejects, RunAt,
            _ => games, nameVariants: _ => Array.Empty<string>());

        Assert.False(r.Ok);
        var issue = Assert.Single(r.Issues, i => i.Kind == "rejected-rejection");
        Assert.Contains("队名无法确认", issue.Key);
        Assert.Contains($"{games} 场", issue.Key);
        Assert.DoesNotContain(r.Rejections, x => x.Accepted);
    }

    [Fact]
    public void ThinSample_SameLowCount_IsStillAccepted_BecauseIdentityIsConfirmed()
    {
        // 「另行区分」（用户 2026-11-14 第十二轮点名）：样本不足本身有一个原因码 thin-sample，
        // 它成立的前提是**模型认出了这支队**（身份已确认），只是本联赛样本不够。
        // 同样 2 场，走 name-unresolved 是阻断，走 thin-sample 是规则内拒绝——
        // 差别不在数字，在身份有没有被确认。门槛值两边都是 3，没有改。
        var source = new[] { Src(1, "周六002", "某支球队", "山形山神"), Src(2, "周六003", "某支新队", "山形山神") };
        var rejects = new[]
        {
            Rej(1, "周六002", MilkInputRejection.ReasonThinSample, "games=2;min=3;league=日职乙",
                home: "某支球队", away: "山形山神"),
            Rej(2, "周六003", MilkInputRejection.ReasonNameUnresolved, "team=某支新队;league=日职乙;games=2;min=3",
                home: "某支新队", away: "山形山神")
        };

        var r = MilkSourceSlate.Verify(source, Array.Empty<MilkInputMatch>(), rejects, RunAt,
            _ => 2, nameVariants: _ => Array.Empty<string>());

        Assert.False(r.Ok);   // 未确认身份那一场必须阻断
        var byReason = r.Rejections.ToDictionary(x => x.Reason, StringComparer.Ordinal);
        Assert.True(byReason[MilkInputRejection.ReasonThinSample].Accepted);
        Assert.False(byReason[MilkInputRejection.ReasonNameUnresolved].Accepted);
        var issue = Assert.Single(r.Issues, i => i.Kind == "rejected-rejection");
        Assert.Contains("name-unresolved", issue.Key);
        Assert.DoesNotContain("thin-sample", issue.Key);
        Assert.Equal(3, MilkThresholds.MinTeamGames);   // 门槛未被改动
    }

    [Fact]
    public void SelfDeclaredNewTeam_IsNotTrusted()
    {
        // 上一版 `new-team` 在规则表里是 Accepted=true —— 调用方自己写一句就能跳过两道库检，
        // 与 `engine-skipped` 同一种病（自报即自证）。现在自报按 name-unresolved 重走判定：
        // 库里有 39 场就不是新队，哪怕它自己写的是 new-team。
        var source = new[] { Src(1, "周六002", "大宫松鼠RB", "山形山神") };
        var rejects = new[]
        {
            Rej(1, "周六002", MilkInputRejection.ReasonNewTeam, "team=大宫松鼠RB;games=0;min=5")
        };

        var r = MilkSourceSlate.Verify(source, Array.Empty<MilkInputMatch>(), rejects, RunAt,
            _ => 39, nameVariants: _ => Array.Empty<string>());

        Assert.False(r.Ok);
        Assert.Contains(r.Issues, i => i.Kind == "rejected-rejection" && i.Key.Contains("39"));
        Assert.DoesNotContain(r.Rejections, x => x.Accepted);
    }

    [Fact]
    public void NameUnresolved_ZeroGames_WithoutVariantProbe_IsRejected()
    {
        // 零计数不能证明是真新队：「谢周三」按本名查是 0 场、「谢菲尔德星期三」是 74 场。
        // 第十二轮起即使做了变体探查、查到 0 条写法，也照样阻断（见上面那条）；
        // 这条覆盖连探查都没做的情况——理由里要写明这一点，事后才分得清「查过没有」。
        var source = new[] { Src(1, "周六002", "某新队", "山形山神") };
        var rejects = new[]
        {
            Rej(1, "周六002", MilkInputRejection.ReasonNameUnresolved, "team=某新队;league=日职乙;games=0;min=3",
                home: "某新队", away: "山形山神")
        };

        var r = MilkSourceSlate.Verify(source, Array.Empty<MilkInputMatch>(), rejects, RunAt, _ => 0);

        Assert.False(r.Ok);
        Assert.Contains(r.Issues, i => i.Kind == "rejected-rejection" && i.Key.Contains("变体探查未完成"));
        Assert.DoesNotContain(r.Rejections, x => x.Accepted);
    }

    [Fact]
    public void NameUnresolved_ZeroGames_ButVariantExists_IsRejected()
    {
        // 同一个真实形态：队名写法不同 → 按本名查 0 场，库内却有同一身份的另一种写法。
        // 零计数在这里是**名字缺陷的影子**，不是「新队」的证据。
        var source = new[] { Src(1, "周六002", "不来梅", "奥格斯堡") };
        var rejects = new[]
        {
            Rej(1, "周六002", MilkInputRejection.ReasonNameUnresolved, "team=不来梅;league=德甲;games=0;min=5",
                home: "不来梅", away: "奥格斯堡")
        };

        var r = MilkSourceSlate.Verify(source, Array.Empty<MilkInputMatch>(), rejects, RunAt,
            _ => 0,
            nameVariants: n => n == "不来梅" ? new[] { "云达不莱梅" } : Array.Empty<string>());

        Assert.False(r.Ok);
        var issue = Assert.Single(r.Issues, i => i.Kind == "rejected-rejection");
        Assert.Contains("相近写法", issue.Key);
        Assert.Contains("云达不莱梅", issue.Key);
        Assert.DoesNotContain(r.Rejections, x => x.Accepted);
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
        // 这场没有输入凭证，缺档只记在该场，不再单独把整轮判失败。
        var keys = FullKeys();
        keys.Remove("BF|2:1");
        keys.Add("BQ|平/胜");

        Assert.Equal(54, keys.Distinct().Count());

        var r = MilkSourceSlate.Verify(
            new[] { Src(1, "周六001", "A", "B", keys: keys) }, Array.Empty<MilkInputMatch>(),
            new[] { Rej(1, "周六001", MilkInputRejection.ReasonThinSample, "games=1/2;min=5") }, RunAt);

        Assert.True(r.Ok, r.Detail);
        var gap = Assert.Single(r.Issues, i => i.Kind == "source-option-gap");
        Assert.Contains("2:1", gap.Key);
        Assert.False(gap.BlocksRound);
        Assert.DoesNotContain(r.Issues, i => i.Kind == "source-option-gap" && i.Key.Contains("BQ"));
    }

    [Fact]
    public void IncompleteBook_IsParkedOnThatMatch_AndTheCompleteMatchStays()
    {
        var full = FullKeys();
        var gapKeys = Without("BF|1:0");
        var source = new[]
        {
            Src(1, "周六001", "缺档主", "缺档客", keys: gapKeys),
            Src(2, "周六002", "合成主", "合成客", keys: full)
        };
        var build = new MilkBuildResult
        {
            Inputs =
            {
                InpFrom(1, "周六001", "缺档主", "缺档客", gapKeys),
                InpFrom(2, "周六002", "合成主", "合成客", full)
            },
            Picks =
            {
                new MilkPick { MatchId = 1, PlayType = JcPlayTypes.Bf, OptionCode = "2:1", OptionName = "2:1", Odds = 10m },
                new MilkPick { MatchId = 2, PlayType = JcPlayTypes.Bq, OptionCode = "3-3", OptionName = "3-3", Odds = 3.50m }
            }
        };

        MilkSourceSlate.ParkIncompleteBooks(build, source);
        var report = MilkSourceSlate.Verify(source, build.Inputs, build.Rejections, RunAt);

        Assert.True(report.Ok, report.Detail);
        Assert.DoesNotContain(build.Picks, p => p.MatchId == 1);
        Assert.Contains(build.Picks, p => p.MatchId == 2 && p.OptionCode == "3-3");
        Assert.DoesNotContain(build.Inputs, i => i.MatchId == 1);
        Assert.Contains(build.Inputs, i => i.MatchId == 2);
        var parked = Assert.Single(build.Rejections);
        Assert.Equal(MilkInputRejection.ReasonIncompleteOdds, parked.Reason);
        Assert.Equal(JcPlayDomain.GapEvidence(gapKeys), parked.Evidence);
        Assert.True(Assert.Single(report.Rejections).Accepted);
        var gap = Assert.Single(report.Issues, i => i.Kind == "source-option-gap");
        Assert.StartsWith("1|", gap.Key);
        Assert.Contains("1:0", gap.Key);
        Assert.False(gap.BlocksRound);
    }

    [Fact]
    public void IncompleteOdds_OnACompleteBook_IsNotAccepted()
    {
        var evidence = JcPlayDomain.GapEvidence(FullKeys());
        var r = MilkSourceSlate.Verify(
            new[] { Src(1, "周六001", "A", "B") },
            Array.Empty<MilkInputMatch>(),
            new[] { Rej(1, "周六001", MilkInputRejection.ReasonIncompleteOdds, evidence) },
            RunAt);

        Assert.False(r.Ok);
        Assert.Contains(r.Issues, i => i.Kind == "rejected-rejection" && i.Key.Contains("赔率簿完整"));
        Assert.False(Assert.Single(r.Rejections).Accepted);
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
    public void NameUnresolved_Issue_ReachesThePublishGate()
    {
        // 「判定成立」不等于「本轮不发布」，中间还有一跳：源侧问题进核对器的 pre 列表，
        // 由它把整轮判成不通过 → 服务把轮次标 failed → 不写 `FbMilkRunDate` →
        // 榜面读的是「该受注日最新的 success 轮」，于是停在上一次成功的榜上。
        //
        // 隔离库 ④-16d 验的是最后那一跳，但那里的 failed 是手工关的（假定服务已经这么判）。
        // 这条补上前半跳：源侧问题**确实是致命的**。若哪天有人把它降级成告警/过滤掉，
        // ④-16d 依旧会绿（它自己关轮次），只有这条会红。
        var source = new[] { Src(1, "周六002", "首次出现FC", "山形山神") };
        var rejects = new[]
        {
            Rej(1, "周六002", MilkInputRejection.ReasonNameUnresolved,
                "team=首次出现FC;league=日职乙;games=0;min=3",
                home: "首次出现FC", away: "山形山神")
        };

        var src = MilkSourceSlate.Verify(source, Array.Empty<MilkInputMatch>(), rejects, RunAt,
            _ => 0, nameVariants: _ => Array.Empty<string>());
        Assert.False(src.Ok);

        // 四张表的账本本身完全对齐（预期 = 实际）：整轮不通过只可能来自源侧那一条。
        var aligned = new[] { new MilkLedgerRow { Key = "1", Count = 1 } };
        var report = MilkRunLedger.Verify(
            new[] { new MilkLedgerPair { Table = "input", Expected = aligned, Actual = aligned } },
            src.Issues);

        Assert.False(report.Ok);
        Assert.Contains(report.Issues, i => i.Kind == "rejected-rejection" && i.Key.Contains("不能作为新队证明"));
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

    // ─────────── 第九轮：门槛同源 + 启售依据「无法判定」 ───────────

    [Fact]
    public void MinTeamGames_IsOneConstant()
    {
        // 引擎出票门槛与核对器「真新队」门槛必须是同一个数（原来是 3 与 5）。
        // 一支 4 场样本的队曾同时被引擎「算进来」、被核对器「当新队放行」，两个结论都自称有依据。
        Assert.Equal(MilkThresholds.MinTeamGames, MilkSourceSlate.DefaultMinTeamGames);
        Assert.Equal(MilkThresholds.MinTeamGames, AiFootBall.Sources.MilkPickEngine.MinTeamGames);
        Assert.Equal(MilkThresholds.MaxSampleAgeMinutes, MilkSourceSlate.MaxSampleAgeMinutes);
    }

    [Fact]
    public void SourceOptionGap_RecordsSaleBasisAsUnavailable()
    {
        // 库里没有逐玩法启售字段，「缺档」与「未启售」无法区分。
        // 判据照旧阻断（保守），但必须把「无法判定」写进问题键——
        // 否则事后会把它当成「真缺档」的结论，而它其实只是「不知道」。
        //
        // 构造：源侧自己少一档、引擎与源侧完全一致（没有 missing-option），
        // 这样报出来的只可能是「源侧缺档」，不会和「引擎少算」混在一起。
        var keys = FullKeys();
        var source = new[] { Src(1, "周六001", "A", "B", keys: keys.Take(keys.Count - 1)) };
        var inputs = new[] { InpFrom(1, "周六001", "A", "B", keys.Take(keys.Count - 1)) };

        var r = MilkSourceSlate.Verify(source, inputs, Array.Empty<MilkInputRejection>(), RunAt);

        var gap = Assert.Single(r.Issues, i => i.Kind == "source-option-gap");
        Assert.Contains($"sale-basis={MilkSourceSlate.SaleBasis}", gap.Key);
        Assert.Equal("unavailable", MilkSourceSlate.SaleBasis);
        Assert.DoesNotContain(r.Issues, i => i.Kind == "missing-option");
        Assert.False(r.Ok);
    }

    // ─────────── 明确联赛排除：只豁免该场 source-option-gap ───────────

    private static List<string> Without(string key)
    {
        var keys = FullKeys();
        Assert.True(keys.Remove(key));
        return keys;
    }

    [Fact]
    public void ConfirmedLeagueExclusion_IncompleteOptions_DoesNotBlock_AndKeepsRecords()
    {
        // 1398695 的形态：已被名单排除，源侧档位不满。豁免只少报这一场的缺档。
        var keys = Without("BF|1:0");
        const string evidence = "list=NoPickLeagues;league=欧国联";
        var source = new[] { Src(1398695, "周日001", "A", "B", keys: keys, league: "欧国联") };
        var rejects = new[] { Rej(1398695, "周日001", MilkInputRejection.ReasonLeagueExcluded, evidence) };

        var r = MilkSourceSlate.Verify(source, Array.Empty<MilkInputMatch>(), rejects, RunAt);

        Assert.True(r.Ok, r.Detail);
        Assert.Equal(1398695, Assert.Single(r.Source).MatchId);
        var kept = Assert.Single(r.Rejections);
        Assert.Equal(MilkInputRejection.ReasonLeagueExcluded, kept.Reason);
        Assert.Equal(evidence, kept.Evidence);
        Assert.True(kept.Accepted);
        Assert.DoesNotContain(r.Issues, i => i.Kind == "source-option-gap");
    }

    [Fact]
    public void ConfirmedNoParlayExclusion_IncompleteOptions_DoesNotBlock()
    {
        const string evidence = "list=NoParlayLeagues;league=澳超";
        var r = MilkSourceSlate.Verify(
            new[] { Src(2, "周日002", "A", "B", keys: Without("JQ|5"), league: "澳超") },
            Array.Empty<MilkInputMatch>(),
            new[] { Rej(2, "周日002", MilkInputRejection.ReasonLeagueExcluded, evidence) },
            RunAt);

        Assert.True(r.Ok, r.Detail);
        Assert.Equal(evidence, Assert.Single(r.Rejections).Evidence);
        Assert.True(Assert.Single(r.Rejections).Accepted);
    }

    [Fact]
    public void ForgedLeagueExclusion_OnFullBook_StillRejectsTheReason()
    {
        // 54 档齐全也不能用错误的 league-excluded 把一场本该计算的比赛放过去。
        const string evidence = "list=NoPickLeagues;league=欧国联";
        var source = new[] { Src(3, "周六003", "A", "B", league: "日职乙") };
        var rejects = new[] { Rej(3, "周六003", MilkInputRejection.ReasonLeagueExcluded, evidence) };

        var r = MilkSourceSlate.Verify(source, Array.Empty<MilkInputMatch>(), rejects, RunAt);

        Assert.False(r.Ok);
        Assert.Contains(r.Issues, i => i.Kind == "rejected-rejection" && i.Key.Contains("联赛不一致"));
        Assert.DoesNotContain(r.Issues, i => i.Kind == "source-option-gap");
        Assert.Equal(evidence, Assert.Single(r.Rejections).Evidence);
        Assert.False(Assert.Single(r.Rejections).Accepted);
        Assert.Equal(3, Assert.Single(r.Source).MatchId);
    }

    [Fact]
    public void LeagueLabel_DoesNotMatchSourceLeague_StillGaps()
    {
        // 「女欧国联」含子串所以在禁推名单里，但证据写的是「欧国联」，精确比对失败。
        const string evidence = "list=NoPickLeagues;league=欧国联";
        var r = MilkSourceSlate.Verify(
            new[] { Src(4, "周六004", "A", "B", keys: Without("BF|1:0"), league: "女欧国联") },
            Array.Empty<MilkInputMatch>(),
            new[] { Rej(4, "周六004", MilkInputRejection.ReasonLeagueExcluded, evidence) },
            RunAt);

        Assert.False(r.Ok);
        Assert.Contains(r.Issues, i => i.Kind == "rejected-rejection" && i.Key.Contains("联赛不一致"));
        Assert.Contains(r.Issues, i => i.Kind == "source-option-gap" && i.Key.StartsWith("4|"));
    }

    [Fact]
    public void WrongListName_ForExcludedLeague_StillGaps()
    {
        const string evidence = "list=NoPickLeagues;league=澳超";
        var r = MilkSourceSlate.Verify(
            new[] { Src(5, "周六005", "A", "B", keys: Without("SPF|3"), league: "澳超") },
            Array.Empty<MilkInputMatch>(),
            new[] { Rej(5, "周六005", MilkInputRejection.ReasonLeagueExcluded, evidence) },
            RunAt);

        Assert.False(r.Ok);
        Assert.Contains(r.Issues, i => i.Kind == "rejected-rejection" && i.Key.Contains("联赛不一致"));
        Assert.Contains(r.Issues, i => i.Kind == "source-option-gap" && i.Key.Contains("SPF") && i.Key.StartsWith("5|"));
    }

    [Theory]
    [InlineData("list=NoPickLeagues;list=NoParlayLeagues;league=欧国联", "重复冲突字段")]
    [InlineData("list=NoPickLeagues;list=NoPickLeagues;league=欧国联", "重复冲突字段")]
    [InlineData("list=NoPickLeagues", "缺字段")]
    [InlineData("list=Banned;league=欧国联", "未知list")]
    [InlineData("list=NoPickLeagues;league=欧国联;", "证据字段无法解析")]
    public void MalformedLeagueEvidence_DoesNotExempt(string evidence, string defect)
    {
        var r = MilkSourceSlate.Verify(
            new[] { Src(6, "周六006", "A", "B", keys: Without("BQ|3-3"), league: "欧国联") },
            Array.Empty<MilkInputMatch>(),
            new[] { Rej(6, "周六006", MilkInputRejection.ReasonLeagueExcluded, evidence) },
            RunAt);

        Assert.False(r.Ok);
        Assert.Contains(r.Issues, i => i.Kind == "rejected-rejection" && i.Key.Contains(defect));
        Assert.Contains(r.Issues, i => i.Kind == "source-option-gap" && i.Key.StartsWith("6|"));
        Assert.Equal(evidence, Assert.Single(r.Rejections).Evidence);
    }

    [Fact]
    public void NameUnresolved_IncompleteOptions_StillGaps()
    {
        var keys = Without("BF|2:1");
        var source = new[] { Src(7, "周六007", "首次出现FC", "山形山神", keys: keys) };
        var rejects = new[]
        {
            Rej(7, "周六007", MilkInputRejection.ReasonNameUnresolved,
                "team=首次出现FC;league=日职乙;games=0;min=3",
                home: "首次出现FC", away: "山形山神")
        };

        var r = MilkSourceSlate.Verify(source, Array.Empty<MilkInputMatch>(), rejects, RunAt,
            _ => 0, nameVariants: _ => Array.Empty<string>());

        Assert.False(r.Ok);
        Assert.Contains(r.Issues, i => i.Kind == "rejected-rejection" && i.Key.Contains("不能作为新队证明"));
        Assert.Contains(r.Issues, i => i.Kind == "source-option-gap" && i.Key.StartsWith("7|") && i.Key.Contains("2:1"));
    }

    [Fact]
    public void NoOdds_OnExcludedLeagueName_StillGaps()
    {
        // 原因不是 league-excluded，就不能借名单把缺档放过去。
        var keys = FullKeys().Where(k => !k.StartsWith("SPF|", StringComparison.Ordinal)).ToList();
        var source = new[] { Src(8, "周六008", "A", "B", keys: keys, league: "欧国联") };
        var rejects = new[] { Rej(8, "周六008", MilkInputRejection.ReasonNoOdds, "options=0;source=0") };

        var r = MilkSourceSlate.Verify(source, Array.Empty<MilkInputMatch>(), rejects, RunAt);

        Assert.False(r.Ok);
        Assert.Contains(r.Issues, i => i.Kind == "source-option-gap" && i.Key.Contains("SPF") && i.Key.StartsWith("8|"));
        Assert.Contains(r.Issues, i => i.Kind == "contradicted-rejection");
        Assert.DoesNotContain(r.Rejections, x => x.Accepted);
    }

    [Fact]
    public void ComputedMatch_OnExcludedLeague_IncompleteOptions_StillGaps()
    {
        const string evidence = "list=NoPickLeagues;league=欧国联";
        var keys = Without("JQ|0");
        var source = new[] { Src(9, "周六009", "A", "B", keys: keys, league: "欧国联") };
        var inputs = new[] { InpFrom(9, "周六009", "A", "B", keys) };
        var rejects = new[] { Rej(9, "周六009", MilkInputRejection.ReasonLeagueExcluded, evidence) };

        var r = MilkSourceSlate.Verify(source, inputs, rejects, RunAt);

        Assert.False(r.Ok);
        Assert.Contains(r.Issues, i => i.Kind == "duplicate-input" && i.Key == "9");
        Assert.Contains(r.Issues, i => i.Kind == "source-option-gap" && i.Key.StartsWith("9|") && i.Key.Contains("JQ"));
        Assert.Equal(evidence, Assert.Single(r.Rejections).Evidence);
    }

    [Fact]
    public void ConfirmedExclusion_DoesNotClearAnotherMatchsGap()
    {
        var excluded = Src(1398695, "周日001", "A", "B", keys: Without("BF|1:0"), league: "欧国联");
        var ordinary = Src(10, "周六010", "C", "D", keys: Without("BQ|0-0"));
        var rejects = new[]
        {
            Rej(1398695, "周日001", MilkInputRejection.ReasonLeagueExcluded, "list=NoPickLeagues;league=欧国联")
        };
        var inputs = new[] { InpFrom(10, "周六010", "C", "D", ordinary.OptionKeys) };

        var r = MilkSourceSlate.Verify(new[] { excluded, ordinary }, inputs, rejects, RunAt);

        Assert.False(r.Ok);
        Assert.Contains(r.Issues, i => i.Kind == "source-option-gap" && i.Key.StartsWith("10|") && i.Key.Contains("0-0"));
        Assert.DoesNotContain(r.Issues, i => i.Kind == "source-option-gap" && i.Key.StartsWith("1398695|"));
        Assert.Contains(r.Source, m => m.MatchId == 1398695);
        Assert.True(Assert.Single(r.Rejections).Accepted);
    }
}
