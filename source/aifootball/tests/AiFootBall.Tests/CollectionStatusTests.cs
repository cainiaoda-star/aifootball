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
}
