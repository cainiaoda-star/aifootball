using AiFootBall.Core.Models;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 队名归一化兜底（2026-11-14，1419319 大宫松鼠RB 事件）。
///
/// 现场：竞彩把球队写成「大宫松鼠RB」（红牛冠名），库内 39 场全叫「大宫松鼠」。
/// 名字解析只允许**查询名比库内名短**（`cand.Length &lt;= shortName.Length` 直接跳过），
/// 于是查询名更长时规则完全不触发 → 整场被判「模型不认识」→
/// 没有输入凭证、没有存档、没有闸门，静默消失，而库里那 39 场一直躺在「大宫松鼠」名下。
///
/// 这两条测试分别钉住两个修法：
///   · 人工别名（TeamAliases）—— 已知的译名/冠名差异；
///   · 核心名兜底（去 ASCII 标记后一字不差且全库唯一）—— 没见过的新后缀。
/// 同时钉住边界：核心名撞两支以上时**不许猜**，返回「认不出」。
/// </summary>
public class TeamNameCoreTests
{
    private static ScorelineModel Model(params (string Home, string Away)[] extra)
    {
        var samples = new List<ScoreSample>();
        var day = new DateTime(2026, 1, 1);
        for (var i = 0; i < 20; i++)
        {
            samples.Add(new ScoreSample
            {
                LeagueName = "日职乙", HomeTeam = "大宫松鼠", AwayTeam = "山形山神",
                HomeScore = 1 + i % 3, AwayScore = i % 2, MatchTime = day.AddDays(i)
            });
            samples.Add(new ScoreSample
            {
                LeagueName = "日职乙", HomeTeam = "山形山神", AwayTeam = "大宫松鼠",
                HomeScore = i % 2, AwayScore = 1 + i % 3, MatchTime = day.AddDays(i + 30)
            });
        }

        foreach (var (h, a) in extra)
            for (var i = 0; i < 3; i++)
                samples.Add(new ScoreSample
                {
                    LeagueName = "日职乙", HomeTeam = h, AwayTeam = a,
                    HomeScore = 1, AwayScore = 1, MatchTime = day.AddDays(60 + i)
                });

        return ScorelineModel.Fit(samples);
    }

    [Fact]
    public void SponsorSuffix_ResolvesViaAlias()
    {
        var model = Model();
        Assert.True(model.KnowsTeam("日职乙", "大宫松鼠"));
        Assert.Equal("大宫松鼠", model.Resolve("日职乙", "大宫松鼠RB"));
        Assert.True(model.TeamGames("日职乙", "大宫松鼠RB") > 0);
    }

    [Fact]
    public void UnknownAsciiSuffix_ResolvesViaCoreName()
    {
        // 「山形山神SC」不在别名表里：靠核心名（去掉 SC）唯一命中。
        var model = Model();
        Assert.Equal("山形山神", model.Resolve("日职乙", "山形山神SC"));
    }

    [Fact]
    public void AmbiguousCoreNames_AreNotGuessed()
    {
        // 同核心名对应两支不同球队时，必须返回「认不出」——宁可丢场，不猜错队。
        var model = Model(("测试甲队", "测试乙队"), ("测试甲队FC", "测试乙队"));
        Assert.Null(model.Resolve("日职乙", "测试甲队SC"));
    }

    [Fact]
    public void ReserveTeamIsNeverMatchedForSeniorQuery()
    {
        // 2026-11-14 实测的两条误配：库内一线队没样本时，唯一核心名是梯队，
        // 「全库唯一」就把一线队配成了梯队。唯一性只保证不猜，不保证是同一支队。
        //   不来梅 → JFV不来梅U19
        //   谢周三 → 谢周三U21
        //
        // 第十一轮加了这两条别名之后结论不变、理由更强：别名指向的**一线队不在库内**时，
        // 仍然只许返回「认不出」，不许退回去拿梯队顶替（正例见 Alias_ResolvesToSeniorTeam_NotReserveTeam）。
        var model = Model(("JFV不来梅U19", "奥格斯堡青年队"), ("谢周三U21", "女王公园巡游者U21"));
        Assert.Null(model.Resolve("德甲", "不来梅"));
        Assert.Null(model.Resolve("英锦标赛", "谢周三"));
    }

    [Fact]
    public void ExplicitYouthQuery_StillResolvesYouthTeam()
    {
        // 反向不能误伤：查询名自己带梯队标记时，队内精确命中照旧——
        // 修的是「一线队被配成梯队」，不是「不许用梯队」。
        var model = Model(("JFV不来梅U19", "奥格斯堡青年队"), ("谢周三U21", "女王公园巡游者U21"));
        Assert.Equal("谢周三U21", model.Resolve("英锦标赛", "谢周三U21"));
        Assert.Equal("JFV不来梅U19", model.Resolve("德甲", "JFV不来梅U19"));
    }

    [Fact]
    public void DifferentYouthLevels_DoNotMerge()
    {
        // U19 与 U21 是两支不同的队：核心名相同，但标记不同，不能互配。
        var model = Model(("谢周三U21", "女王公园巡游者U21"), ("谢周三U19", "女王公园巡游者U19"));
        Assert.Null(model.Resolve("英锦标赛", "谢周三U23"));
    }

    [Fact]
    public void CoreKey_StripsReserveMarks_WhichIsWhyIdentityGateExists()
    {
        // 核心名会把 U21 一起抹掉——所以「核心名相等」本身不能作为身份判据，
        // 必须另加梯队标记与 ASCII 记号两道闸（见 ScorelineModel.Resolve）。
        // 注意别再拿「谢周三/谢周三U21」举例（2026-11-14 第十一轮）：
        // 「谢周三」现在有别名（→ 谢菲尔德星期三），Key 会先过别名表，两个核心名就不再相等。
        Assert.Equal(TeamNames.CoreKey("帕丘卡"), TeamNames.CoreKey("帕丘卡III"));
        Assert.Equal(TeamNames.CoreKey("大宫松鼠"), TeamNames.CoreKey("大宫松鼠RB"));
    }

    // ─────────── 别名补齐（2026-11-14 第十一轮）：解析与计数都要命中一线队 ───────────
    //
    // 两条都先做了独立库检（FbMatch 近 18 个月，Status=3）：
    //   谢菲尔德星期三 74 场 / 库里没有「谢周三」，只有 谢周三U21 31、谢周三U18 1（梯队）
    //   云达不莱梅 58 场 / 同前缀的 青年队 52、女足 48、U19 34、C队 2、JFV不来梅U19 1、
    //   不莱梅港 48（另一家俱乐部）
    // 别名只指向一线队；梯队能不能被挡住，仍然由 SameIdentity 决定。

    [Fact]
    public void Alias_MapsShortNameToSeniorTeam()
    {
        Assert.Equal("谢菲尔德星期三", TeamAliases.Canonical("谢周三"));
        Assert.Equal("云达不莱梅", TeamAliases.Canonical("不来梅"));

        // 计数用的键必须一起收敛，否则「解析认出来了、计数仍是 0」——正是 name-unresolved 的形态。
        Assert.Equal(TeamNames.CoreKey("谢菲尔德星期三"), TeamNames.CoreKey("谢周三"));
        Assert.Equal(TeamNames.CoreKey("云达不莱梅"), TeamNames.CoreKey("不来梅"));
    }

    [Fact]
    public void Alias_ResolvesToSeniorTeam_NotReserveTeam()
    {
        // 一线队与梯队同时在库：别名必须落在**一线队**上。
        var model = Model(
            ("谢菲尔德星期三", "女王公园巡游者"), ("谢菲尔德星期三", "女王公园巡游者"),
            ("谢周三U21", "女王公园巡游者U21"),
            ("云达不莱梅", "奥格斯堡"),
            ("JFV不来梅U19", "奥格斯堡青年队"));

        Assert.Equal("谢菲尔德星期三", model.Resolve("日职乙", "谢周三"));
        Assert.Equal("云达不莱梅", model.Resolve("日职乙", "不来梅"));

        // 计数也必须命中一线队那一份：6 场（两次 extra）对梯队的 3 场。
        Assert.Equal(6, model.TeamGames("日职乙", "谢周三"));
        Assert.Equal(3, model.TeamGames("日职乙", "谢周三U21"));
        Assert.Equal(3, model.TeamGames("日职乙", "不来梅"));
    }

    [Fact]
    public void Alias_DoesNotMergeReserveOrNeighbourClub()
    {
        // 别名是把「简称」接到一线队，不是放宽身份：梯队与另一家俱乐部一条都不许并进来。
        Assert.False(TeamNames.SameIdentity("谢周三", "谢周三U21"));
        Assert.False(TeamNames.SameIdentity("谢周三", "谢周三U18"));
        Assert.False(TeamNames.SameIdentity("不来梅", "JFV不来梅U19"));
        Assert.False(TeamNames.SameIdentity("不来梅", "云达不莱梅U19"));
        Assert.False(TeamNames.SameIdentity("不来梅", "云达不莱梅女足"));
        Assert.False(TeamNames.SameIdentity("不来梅", "云达不莱梅青年队"));
        // 不莱梅港是另一家俱乐部：核心名不同，计数时被 CoreKey 挡掉（同身份判据管不了这一层）。
        Assert.NotEqual(TeamNames.CoreKey("不来梅"), TeamNames.CoreKey("不莱梅港"));
        Assert.NotEqual(TeamNames.CoreKey("不来梅"), TeamNames.CoreKey("联合不莱梅"));
    }

    [Fact]
    public void CoreKey_StripsOnlyAsciiMarkers()
    {
        Assert.Equal("大宫松鼠", TeamNames.CoreKey("大宫松鼠RB"));
        Assert.Equal("大宫松鼠", TeamNames.CoreKey("大宫松鼠"));
        Assert.Equal("蔚山现代", TeamNames.CoreKey("蔚山HD"));
        // 不是「相似就行」：改字（塞维利亚 vs 塞维尔亚）推不出来，必须一模一样。
        Assert.NotEqual(TeamNames.CoreKey("塞维利亚"), TeamNames.CoreKey("塞维尔亚"));
    }

    // ─────────── 身份判据：解析与计数**共用同一个函数**（2026-11-14 第十轮） ───────────
    //
    // 第九轮这里是两个函数：解析用「候选记号 ⊆ 查询记号」，计数用「互相包含」。
    // 第十轮取消后者——库里存在 `BKMA` / `BKMA II`、`WSG蒂洛尔` / `WSG蒂洛尔 II`、
    // `帕丘卡` / `帕丘卡III` 这种只差一个 ASCII 记号的两支队，互相包含会把预备队并进一线队，
    // 「解析拒绝、计数合并」的老问题只是换了个方向重演。

    [Fact]
    public void Identity_NeverMergesReserveTeamIntoSenior()
    {
        Assert.False(TeamNames.SameIdentity("不来梅", "JFV不来梅U19"));
        Assert.False(TeamNames.SameIdentity("谢周三", "谢周三U21"));
        // 梯队之间也不能互并：U19 与 U21 是两支不同的队。
        Assert.False(TeamNames.SameIdentity("谢周三U21", "谢周三U19"));
        // 罗马数字二队（生产库内真实存在，且这些写法不在中文名单里，靠 HierarchyMark 的第二类识别）。
        Assert.False(TeamNames.SameIdentity("BKMA", "BKMA II"));
        Assert.False(TeamNames.SameIdentity("帕丘卡", "帕丘卡III"));
        Assert.False(TeamNames.SameIdentity("WSG蒂洛尔", "WSG蒂洛尔 II"));
        // 年龄段：库里 U15~U23 全有，原来只认 U19~U23，U18 会被当成一线队。
        Assert.False(TeamNames.SameIdentity("曼联", "曼联U18"));
        Assert.False(TeamNames.SameIdentity("曼联U18", "曼联U21"));
        // 单独的 B（`马拉加 B`）与中文「二队」。
        Assert.False(TeamNames.SameIdentity("马拉加", "马拉加 B"));
        Assert.False(TeamNames.SameIdentity("FC摩城", "FC摩城二队"));
    }

    [Fact]
    public void Identity_IgnoresClubAffixes()
    {
        // 身份只看**梯队层次**：PSV / FC / SC / RB / HD 是俱乐部名的一部分，不是另一支队。
        // 这里曾经写成「候选的 ASCII 记号必须是查询的子集」，结果误杀两组真匹配：
        // `埃因霍温 → PSV埃因霍温`（147 场）、`辛那提 → FC辛辛那提`（75 场）。
        Assert.True(TeamNames.SameIdentity("大宫松鼠RB", "大宫松鼠"));
        Assert.True(TeamNames.SameIdentity("大宫松鼠", "大宫松鼠SC"));
        Assert.True(TeamNames.SameIdentity("埃因霍温", "PSV埃因霍温"));
        Assert.True(TeamNames.SameIdentity("辛辛那提", "FC辛辛那提"));
        Assert.True(TeamNames.SameIdentity("蔚山现代", "蔚山HD"));
    }

    [Fact]
    public void Identity_IsWhatResolveUses()
    {
        // 这条钉住「解析和计数是同一份判据」：凡是 Resolve 认可的匹配，
        // SameIdentity 必须同时为真；反之亦然。两条链路再分叉，就会回到
        // 「解析说不认识、计数说库里有 22 场」的自相矛盾。
        var model = Model(
            ("JFV不来梅U19", "奥格斯堡青年队"),
            ("谢周三U21", "女王公园巡游者U21"),
            ("帕丘卡III", "另一队"),
            ("PSV埃因霍温", "第三队"));
        foreach (var (q, c) in new[]
                 {
                     ("不来梅", "JFV不来梅U19"),
                     ("谢周三", "谢周三U21"),
                     ("帕丘卡", "帕丘卡III"),
                     ("大宫松鼠SC", "大宫松鼠"),
                     ("大宫松鼠RB", "大宫松鼠"),
                     ("埃因霍温", "PSV埃因霍温")
                 })
        {
            var resolved = model.Resolve("日职乙", q);
            var accepted = resolved is not null
                           && string.Equals(TeamNames.CoreKey(resolved), TeamNames.CoreKey(c));
            Assert.Equal(accepted, TeamNames.SameIdentity(q, c));
        }
    }
}
