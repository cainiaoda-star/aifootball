using AiFootBall.Sources;
using Xunit;

namespace AiFootBall.Tests;

/// <summary>
/// 采集作业的成败口径（2026-11-10 补）。
///
/// 背景：`SampleOddsAsync` 原来把状态硬编码成 "SUCCESS"，`RowsAffected` 又填「场数」
/// 而不是写入行数。于是 11-10 05:09 那轮出现了一条自相矛盾的日志：
///
///     job:odds-sample  SUCCESS  15  |轨迹采样 15 场，失败 15
///     job:odds-sample-late SUCCESS 15 |…赔率行 0；验证拦截 2326 次，需更换 token
///
/// 「15 场全失败、写入 0 行」被两层都读成绿灯：状态 SUCCESS、行数 15（看着像有产出）。
/// token 被打掉的整夜，监控页干干净净，而欧战通宵场次（凌晨 2~7 点开赛，近 7 天 738 场）
/// 的赛前采样一个点都没有。
///
/// 这批测试锁三件事：
///   1. 全部失败才判失败，部分失败不算（反爬抖动是常态，报红会把真问题淹掉）；
///   2. 没有待采场次（done=0）是闲时，绝不能算失败；
///   3. 失败结果仍要如实带出写入行数，不许抹成 0。
/// </summary>
public class CollectionStatusTests
{
    [Fact]
    public void AllFailed_WhenEveryMatchFailed_IsTrue()
    {
        // 11-10 05:09 的真实形态：15 场，失败 15，赔率行 0。
        Assert.True(CollectionResult.AllFailed(15, 15));
        Assert.True(CollectionResult.AllFailed(10, 10));
        Assert.True(CollectionResult.AllFailed(40, 40));
    }

    [Fact]
    public void AllFailed_WhenPartlyFailed_IsFalse()
    {
        // 14 失败 1 成功仍算成功：偶尔被拦一次不该让整轮报红。
        Assert.False(CollectionResult.AllFailed(15, 14));
        Assert.False(CollectionResult.AllFailed(58, 3));
    }

    [Fact]
    public void AllFailed_WhenNothingToDo_IsFalse()
    {
        // done=0 是「无待采样赛事」的正常闲时，不能被当成失败。
        Assert.False(CollectionResult.AllFailed(0, 0));
    }

    [Fact]
    public void AllFailed_WhenAllSucceeded_IsFalse()
    {
        Assert.False(CollectionResult.AllFailed(28, 0));
    }

    [Fact]
    public void Fail_KeepsRows()
    {
        // 「部分批次写进去了才被打断」：结论是失败，但行数不能抹成 0。
        var r = CollectionResult.Fail(37, "token 失效已提前中止");

        Assert.False(r.Success);
        Assert.Equal(37, r.Rows);
        Assert.Contains("token", r.Message);
    }

    [Fact]
    public void Fail_WithoutRows_StillZero()
    {
        var r = CollectionResult.Fail("未注入依赖");

        Assert.False(r.Success);
        Assert.Equal(0, r.Rows);
    }

    [Fact]
    public void Ok_IsSuccess()
    {
        var r = CollectionResult.Ok(5673, "轨迹采样 58 场，失败 0");

        Assert.True(r.Success);
        Assert.Equal(5673, r.Rows);
    }

    // ---- 第三种状态：跑了但没活干（2026-11-11 补）----------------------------
    // 「无待采样赛事」原来返回 Ok → 作业层写 SUCCESS。于是「作业活着、一场没采」
    // 和「采了 30 场全成功」在库里长得一模一样，看板永远绿。
    // 这和 11-10 那次「15 场全失败写成 SUCCESS」是同一个病：状态字段没有表达能力。

    [Fact]
    public void Skipped_IsIdle_NotSuccessText()
    {
        var r = CollectionResult.Skipped("无待采样赛事");

        Assert.True(r.Success);              // 不许触发告警：凌晨确实经常没活干
        Assert.True(r.Idle);
        Assert.Equal(0, r.Rows);
        Assert.Equal("IDLE", r.StatusText);  // 落库必须能和 SUCCESS 分开
    }

    [Fact]
    public void StatusText_ThreeWay()
    {
        Assert.Equal("SUCCESS", CollectionResult.Ok(10, "ok").StatusText);
        Assert.Equal("FAILED", CollectionResult.Fail("boom").StatusText);
        Assert.Equal("FAILED", CollectionResult.Fail(7, "半路挂了").StatusText);
        Assert.Equal("IDLE", CollectionResult.Skipped("无待采样赛事").StatusText);
    }

    [Fact]
    public void Skipped_DoesNotChangeFailureVerdict()
    {
        // Idle 只描述「没活干」，不许被拿去当失败或成功的替代口径：
        // AllFailed 仍只看 done/failed 两个数，与 Idle 无关。
        Assert.False(CollectionResult.AllFailed(0, 0));
        Assert.True(CollectionResult.Skipped("x").Success);
    }
}

/// <summary>
/// token 被打掉时该不该提前中止（2026-11-10 补）。
///
/// 只有 `CollectOddsAsync` 有这条短路，`SampleOddsAsync`（odds-sample /
/// odds-sample-late / water-sample 的赔率段）和 `FetchExchangeAsync` 都没有。
/// 后果不是「慢」，是**把单槽主通道占死**：夜间 token 失效后，
/// odds-sample 每轮 400 场逐场白等 10 秒冷却 → 跑满 10 分钟超时，
/// exchange 跑满 15 分钟、odds-sample-late 跑满 8 分钟，三个作业轮流占通道，
/// water-sample 一夜被看门狗判 STUCK 21 次（11-10 实测）。
///
/// 判据必须是「零产出 **且** 连续被拦」，只按其中一个都会误判：
///   · 只看拦截次数 —— 无盘口场次本来就会触发拦截，偶发几次是常态；
///   · 只看零产出 —— 凌晨大批小联赛没有必发盘口，零产出是正常结果。
/// </summary>
public class VerifyAbortTests
{
    [Fact]
    public void Abort_WhenNothingWrittenAndBlockedEnough()
    {
        // 真实形态：整轮零落库 + 拦截 15 次。
        Assert.True(CollectionResult.ShouldAbortOnVerify(0, 15));
        Assert.True(CollectionResult.ShouldAbortOnVerify(0, 6));
    }

    [Fact]
    public void NoAbort_WhenSomeRowsLanded()
    {
        // 有数据写进去说明 token 还能用，跑完才是对的。
        Assert.False(CollectionResult.ShouldAbortOnVerify(1, 300));
        Assert.False(CollectionResult.ShouldAbortOnVerify(8700, 400));
    }

    [Fact]
    public void NoAbort_WhenNoInterception()
    {
        // 没被拦却零产出 = 真的没有盘口（凌晨小联赛常态），不是 token 问题。
        Assert.False(CollectionResult.ShouldAbortOnVerify(0, 0));
        Assert.False(CollectionResult.ShouldAbortOnVerify(0, 5));
    }

    [Fact]
    public void NoAbort_AtThresholdMinusOne()
    {
        Assert.False(CollectionResult.ShouldAbortOnVerify(0, 5));
        Assert.True(CollectionResult.ShouldAbortOnVerify(0, 6));
    }
}

/// <summary>
/// 多段采集合成（2026-11-10 补）。
///
/// water-sample 一轮跑两段（赔率轨迹 + 必发成交），原来无条件
/// `CollectionResult.Ok(resOdds.Rows + resEx.Rows, …)`：赔率段已经如实返回 Fail，
/// 这一句把结论吃掉了，页面还是绿。三层链路里只要有**一层**用 Ok 无脑包装，
/// 下面几层修得再对也没用。
/// </summary>
public class CollectionCombineTests
{
    [Fact]
    public void Combine_AllOk_IsSuccessAndSumsRows()
    {
        var r = CollectionResult.Combine("水位采样",
            CollectionResult.Ok(120, "赔率段"),
            CollectionResult.Ok(34, "必发段"));

        Assert.True(r.Success);
        Assert.Equal(154, r.Rows);
        Assert.Equal("水位采样", r.Message);
    }

    [Fact]
    public void Combine_OneFails_IsFailureEvenIfOtherSucceeded()
    {
        // 必发段断了、赔率段正常 —— 整体必须报红，否则必发断三天没人知道。
        var r = CollectionResult.Combine("水位采样",
            CollectionResult.Ok(120, "赔率段"),
            CollectionResult.Fail(0, "必发段：验证拦截 15 次"));

        Assert.False(r.Success);
        Assert.Equal(120, r.Rows);          // 行数照实累加，不抹成 0
        Assert.Contains("必发段", r.Message);
    }

    [Fact]
    public void Combine_BothFail_KeepsBothReasons()
    {
        var r = CollectionResult.Combine("水位采样",
            CollectionResult.Fail(0, "赔率段：token 失效"),
            CollectionResult.Fail(0, "必发段：token 失效"));

        Assert.False(r.Success);
        Assert.Contains("赔率段", r.Message);
        Assert.Contains("必发段", r.Message);
    }

    [Fact]
    public void Combine_IdleSegments_IsStillSuccess()
    {
        // 两段都「无待采场次」= 闲时，不能报红。
        var r = CollectionResult.Combine("水位采样",
            CollectionResult.Ok(0, "无待采样赛事"),
            CollectionResult.Ok(0, "无待采集赛事"));

        Assert.True(r.Success);
        Assert.Equal(0, r.Rows);
    }
}

/// <summary>
/// SyncLog 列宽截断（2026-11-10 补）。
///
/// Message 是 nvarchar(1000)、TaskName/Source 是 nvarchar(100)，而 WriteAsync
/// 原来一个字符都不截。超长时 SQL Server 抛 2628，整条 INSERT 失败 ——
/// 而最可能写出超长消息的时刻，恰恰是正在拼接多个失败原因的时候。
/// 也就是说：**最需要留下记录的那一刻，日志自己先写不进去**。
/// 目前库内最长只有 170 字，所以这条是「还没踩到」，不是「已经坏了」。
/// </summary>
public class SyncLogClipTests
{
    [Fact]
    public void Short_Untouched()
    {
        Assert.Equal("abc", AiFootBall.Data.SyncLogRepository.ClipMessage("abc"));
        Assert.Null(AiFootBall.Data.SyncLogRepository.ClipMessage(null));
    }

    [Fact]
    public void Long_TrimmedToColumnWidth()
    {
        var s = new string('x', 1500);
        var clipped = AiFootBall.Data.SyncLogRepository.ClipMessage(s);

        Assert.NotNull(clipped);
        Assert.Equal(1000, clipped!.Length);
        Assert.EndsWith("…", clipped);
    }

    [Fact]
    public void TaskName_TrimmedTo100()
    {
        var s = new string('t', 260);
        var clipped = AiFootBall.Data.SyncLogRepository.ClipTask(s);

        Assert.NotNull(clipped);
        Assert.Equal(100, clipped!.Length);
    }
}

/// <summary>
/// 「续期成功」不等于「token 能用」（2026-11-10 补）。
///
/// 11-09 全天报了 41 次 token 续期成功（每 8 分钟一次，00:00~07:35 一刻没停），
/// 而 FbOddsHistory 当天首条仍等到 08:05。续期成功只证明 harvest.mjs 跑完并写了
/// cookie 文件，站点照样可以拒。判据只能看「装了新 cookie 之后又被拦了多少次」。
///
/// 这批测试锁的是判据本身的边界：阈值、无基准、计数器回退、以及必须用增量而非绝对值。
/// </summary>
public class TokenRefreshVerdictTests
{
    private static readonly DateTime SuccessAt = new(2026, 11, 9, 22, 8, 55);

    [Fact]
    public void Ineffective_WhenBlockedAfterSuccess_IsTrue()
    {
        // 真实形态：22:08:55 续期成功，之后又被拦 300 次，整夜打不开。
        Assert.True(TokenRefresher.RefreshProvenIneffective(SuccessAt, 300, 0, 30));
    }

    [Fact]
    public void Ineffective_AtExactThreshold_IsTrue()
    {
        Assert.True(TokenRefresher.RefreshProvenIneffective(SuccessAt, 30, 0, 30));
    }

    [Fact]
    public void NotIneffective_BelowThreshold()
    {
        // 差一次不能算：阈值是用来区分「整轮失败」和「偶发被拦」的。
        Assert.False(TokenRefresher.RefreshProvenIneffective(SuccessAt, 29, 0, 30));
    }

    [Fact]
    public void NotIneffective_WhenNeverSucceeded()
    {
        // 从没续期成功过，就没有「续期之后」这个基准 —— 那是另一个问题（脚本缺失），
        // 不能借用这条判据。
        Assert.False(TokenRefresher.RefreshProvenIneffective(DateTime.MinValue, 999, 0, 30));
    }

    [Fact]
    public void NotIneffective_WhenCounterWentBackwards()
    {
        // 进程重启后 StatVerify 归零，差值变负。没有证据就不能喊「需人工」。
        Assert.False(TokenRefresher.RefreshProvenIneffective(SuccessAt, 5, 120, 30));
    }

    [Fact]
    public void Ineffective_UsesDeltaNotAbsolute()
    {
        // 拦截图样是进程内累计值，续期那一刻可能已经是 2326。
        // 判据必须看「续期之后新增多少」，否则第一轮就会误报。
        Assert.False(TokenRefresher.RefreshProvenIneffective(SuccessAt, 2340, 2326, 30));
        Assert.True(TokenRefresher.RefreshProvenIneffective(SuccessAt, 2400, 2326, 30));
    }

    // ── 熔断：续期被证伪后必须停手 ────────────────────────────────────────────
    //
    // 2026-11-10 22:09~22:50 的真实形态：每 5 次拦截续一次，40 分钟 6 次，
    // 每次日志都写「成功装载 3 项 Cookie」，而数据一个字没进。
    // 续期本身没坏，坏在「续期解决不了这个问题」还一直做。

    [Fact]
    public void OpenCircuit_MakesIsCircuitOpenTrue()
    {
        TokenRefresher.ResetCircuit();
        Assert.False(TokenRefresher.IsCircuitOpen);

        Assert.True(TokenRefresher.OpenCircuit());
        Assert.True(TokenRefresher.IsCircuitOpen);
        Assert.True(TokenRefresher.CircuitUntil > DateTime.Now);

        TokenRefresher.ResetCircuit();
    }

    [Fact]
    public void OpenCircuit_IsIdempotent_DoesNotPushUntilForward()
    {
        // 巡检每 30 秒判一次「续期无效」。若每次都重设到期时刻，
        // 熔断会被无限延长，等于永久停摆 —— 这条就是锁这个。
        TokenRefresher.ResetCircuit();
        Assert.True(TokenRefresher.OpenCircuit());
        var firstUntil = TokenRefresher.CircuitUntil;

        System.Threading.Thread.Sleep(5);
        Assert.False(TokenRefresher.OpenCircuit());      // 第二次不该是新开
        Assert.Equal(firstUntil, TokenRefresher.CircuitUntil);
        Assert.Equal(1, TokenRefresher.CircuitStreak);

        TokenRefresher.ResetCircuit();
    }

    [Fact]
    public void OpenCircuit_StreakStartsAtOne()
    {
        // 第一次熔断 30 分钟。streak 决定退避长度，起点必须是 1 而不是 0
        // （0 会让 CircuitBreakMinutes << -1 变成右移，得到 15 分钟）。
        TokenRefresher.ResetCircuit();
        TokenRefresher.OpenCircuit();
        Assert.Equal(1, TokenRefresher.CircuitStreak);
        var span = TokenRefresher.CircuitUntil!.Value - DateTime.Now;
        Assert.InRange(span.TotalMinutes, 29, 30);

        TokenRefresher.ResetCircuit();
    }

    [Fact]
    public void ResetCircuit_ClearsEverything()
    {
        TokenRefresher.ResetCircuit();
        TokenRefresher.OpenCircuit();
        Assert.True(TokenRefresher.IsCircuitOpen);

        TokenRefresher.ResetCircuit();
        Assert.False(TokenRefresher.IsCircuitOpen);
        Assert.Equal(0, TokenRefresher.CircuitStreak);
        Assert.Null(TokenRefresher.CircuitUntil);
    }
}
