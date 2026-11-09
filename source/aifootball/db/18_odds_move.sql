/* ============================================================================
   18_odds_move.sql —— 变盘监控（2026-11-05）

   起因：11-04 朗斯场，竞彩半全场 16:02 就把「胜胜 5.85→6.20、平负 4.50→4.20」
   定死了，之后六个半小时一条没动。我 21:51:54 出注，用的是同一批快照，
   却从来没比较过前后值——数据躺在库里，没有任何一行代码去读它的变化。

   这张表只回答一件事：**几点、哪一档、从多少变到多少、距开赛还有多久。**
   它不做判断、不给方向、不拦注。判断会过时（方向闸当天就被否掉了），
   事实不会。

   阈值 5%：3.00 变化 0.15 记一笔，6.20 变化 0.31 记一笔。
   低赔率档的 5% 绝对量更小，是想要的——热门方向动 5% 比冷门动 5% 有意义得多。
   ============================================================================ */

IF OBJECT_ID(N'dbo.FbOddsMove', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.FbOddsMove
    (
        MoveId        BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_FbOddsMove PRIMARY KEY,
        MatchId       BIGINT        NOT NULL,
        MatchNo       NVARCHAR(32)  NULL,
        LeagueName    NVARCHAR(64)  NULL,
        HomeTeam      NVARCHAR(64)  NULL,
        AwayTeam      NVARCHAR(64)  NULL,
        MatchTime     DATETIME2(0)  NULL,
        PlayType      VARCHAR(8)    NOT NULL,
        OptionCode    NVARCHAR(16)  NOT NULL,
        OptionName    NVARCHAR(32)  NULL,
        PrevOdds      DECIMAL(8,3)  NOT NULL,
        CurrOdds      DECIMAL(8,3)  NOT NULL,
        -- (CurrOdds - PrevOdds) / PrevOdds，正=赔率升（该档被看淡），负=降（被压重）
        ChangePct     DECIMAL(8,4)  NOT NULL,
        PrevAt        DATETIME2(0)  NOT NULL,
        -- 本次新价第一次出现的时刻，即「变盘发生在这一分钟」
        MoveAt        DATETIME2(0)  NOT NULL,
        MinutesToKick INT           NULL,
        CapturedAt    DATETIME2(0)  NOT NULL CONSTRAINT DF_FbOddsMove_CapturedAt DEFAULT (SYSDATETIME())
    );

    CREATE UNIQUE INDEX UX_FbOddsMove_Key ON dbo.FbOddsMove (MatchId, PlayType, OptionCode, MoveAt);
    CREATE INDEX IX_FbOddsMove_MoveAt  ON dbo.FbOddsMove (MoveAt DESC) INCLUDE (MatchId, PlayType, OptionCode, ChangePct);
    CREATE INDEX IX_FbOddsMove_MatchId ON dbo.FbOddsMove (MatchId, MoveAt);
END
GO

-- 相关子查询找「上一次不同价」需要 OptionCode 落在键上，否则每次要扫该场该玩法的全部时间点。
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_JcOddsHistory_Move' AND object_id = OBJECT_ID(N'dbo.FbJcOddsHistory'))
    CREATE INDEX IX_JcOddsHistory_Move
        ON dbo.FbJcOddsHistory (MatchId, PlayType, OptionCode, CapturedAt)
        INCLUDE (Odds, OptionName);
GO

/* ----------------------------------------------------------------------------
   历史回填（只在表为空时跑一次）

   gaps-and-islands：同一档位连续同价的多次采样算「一段」，段的第一分钟就是变盘时刻。
   直接用 LAG(Odds) 是不行的——6.20 连着采了几百次，LAG 永远给出 6.20，
   变化会被埋在重复记录里，正是这次踩的坑。
   ---------------------------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM dbo.FbOddsMove)
BEGIN
    ;WITH x AS (
        SELECT MatchId, PlayType, OptionCode, OptionName, Odds, CapturedAt,
               ROW_NUMBER() OVER (PARTITION BY MatchId, PlayType, OptionCode ORDER BY CapturedAt)
             - ROW_NUMBER() OVER (PARTITION BY MatchId, PlayType, OptionCode, Odds ORDER BY CapturedAt) AS island
        FROM dbo.FbJcOddsHistory
    ), isl AS (
        SELECT MatchId, PlayType, OptionCode, MIN(OptionName) AS OptionName, Odds,
               MIN(CapturedAt) AS first_at, MAX(CapturedAt) AS last_at
        FROM x
        GROUP BY MatchId, PlayType, OptionCode, Odds, island
    ), steps AS (
        SELECT MatchId, PlayType, OptionCode, OptionName, Odds, first_at,
               LAG(Odds)    OVER (PARTITION BY MatchId, PlayType, OptionCode ORDER BY first_at) AS prev_odds,
               LAG(last_at) OVER (PARTITION BY MatchId, PlayType, OptionCode ORDER BY first_at) AS prev_last_at
        FROM isl
    ), nos AS (
        -- FbMatch 没有 MatchNo（场次号只在赔率表和受注卡上），从赔率表按场取一个
        SELECT MatchId, MAX(MatchNo) AS MatchNo
        FROM dbo.FbJcOddsHistory
        WHERE MatchNo IS NOT NULL
        GROUP BY MatchId
    )
    INSERT INTO dbo.FbOddsMove
        (MatchId, MatchNo, LeagueName, HomeTeam, AwayTeam, MatchTime,
         PlayType, OptionCode, OptionName, PrevOdds, CurrOdds, ChangePct, PrevAt, MoveAt, MinutesToKick)
    SELECT s.MatchId, n.MatchNo, m.LeagueName, m.HomeTeamName, m.AwayTeamName, m.MatchTime,
           s.PlayType, s.OptionCode, s.OptionName, s.prev_odds, s.Odds,
           (s.Odds - s.prev_odds) / s.prev_odds, s.prev_last_at, s.first_at,
           DATEDIFF(minute, s.first_at, m.MatchTime)
    FROM steps s
    JOIN dbo.FbMatch m ON m.MatchId = s.MatchId
    LEFT JOIN nos n ON n.MatchId = s.MatchId
    WHERE s.prev_odds IS NOT NULL
      AND s.prev_odds > 1
      AND ABS(s.Odds - s.prev_odds) / s.prev_odds >= 0.05;

    PRINT CONCAT(N'变盘回填完成，共 ', @@ROWCOUNT, N' 条');
END
GO
