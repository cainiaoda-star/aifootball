-- 待审。不要放进 db\，启动初始化不会执行本文件。
-- 不回填历史轮次。FbMilkRun / FbMilkInput 只在表已经存在时补可空列。

IF OBJECT_ID(N'dbo.FbMilkModelFitContent', N'U') IS NULL
CREATE TABLE dbo.FbMilkModelFitContent
(
    ContentId        bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_FbMilkModelFitContent PRIMARY KEY,
    ContentSha256    char(64)     NOT NULL,
    Status           varchar(16)  NOT NULL,
    SampleCount      int          NOT NULL,
    FitFrom          datetime2    NULL,
    FitTo            datetime2    NULL,
    GlobalHome       float        NOT NULL,
    GlobalAway       float        NOT NULL,
    Rho              decimal(6,3) NOT NULL,
    HalfLifeDays     float        NULL,
    CrossLeague      bit          NOT NULL,
    WindowMonths     int          NOT NULL,
    DurationMinutes  int          NOT NULL,
    ScoreMin         int          NOT NULL,
    ScoreMax         int          NOT NULL,
    PriorGames       int          NOT NULL,
    MinOwnGames      int          NOT NULL,
    LeaguePrior      int          NOT NULL,
    LambdaMin        float        NOT NULL,
    LambdaMax        float        NOT NULL,
    RateMin          float        NOT NULL,
    RateMax          float        NOT NULL,
    ScaleMin         float        NOT NULL,
    ScaleMax         float        NOT NULL,
    HalfGoalShare    float        NOT NULL,
    MaxHalfGoals     int          NOT NULL
);
GO
SET QUOTED_IDENTIFIER ON;
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'UX_FbMilkModelFitContent_ReadySha'
      AND object_id = OBJECT_ID(N'dbo.FbMilkModelFitContent'))
    CREATE UNIQUE INDEX UX_FbMilkModelFitContent_ReadySha
        ON dbo.FbMilkModelFitContent (ContentSha256)
        WHERE Status = 'ready';
GO

IF OBJECT_ID(N'dbo.FbMilkModelFitInstance', N'U') IS NULL
CREATE TABLE dbo.FbMilkModelFitInstance
(
    InstanceId     bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_FbMilkModelFitInstance PRIMARY KEY,
    ContentId      bigint       NOT NULL,
    Status         varchar(16)  NOT NULL,
    FitCutoff      datetime2    NOT NULL,
    QueryClock     datetime2    NOT NULL,
    UsableCut      datetime2    NOT NULL,
    CodeVersion    nvarchar(120) NOT NULL,
    ConfigVersion  nvarchar(80) NOT NULL,
    AliasMapSha    char(64)     NOT NULL,
    LeagueMapSha   char(64)     NOT NULL,
    CreatedAt      datetime2    NOT NULL CONSTRAINT DF_FbMilkModelFitInstance_Created DEFAULT (SYSDATETIME()),
    CONSTRAINT FK_FbMilkModelFitInstance_Content
        FOREIGN KEY (ContentId) REFERENCES dbo.FbMilkModelFitContent (ContentId)
);
GO

IF OBJECT_ID(N'dbo.FbMilkModelFitSample', N'U') IS NULL
CREATE TABLE dbo.FbMilkModelFitSample
(
    ContentId    bigint        NOT NULL,
    Ordinal      int           NOT NULL,
    MatchId      bigint        NOT NULL,
    LeagueName   nvarchar(128) NOT NULL,
    HomeTeam     nvarchar(128) NOT NULL,
    AwayTeam     nvarchar(128) NOT NULL,
    CanonHome    nvarchar(128) NOT NULL,
    CanonAway    nvarchar(128) NOT NULL,
    HomeScore    smallint      NOT NULL,
    AwayScore    smallint      NOT NULL,
    MatchTime    datetime2     NULL,
    TimeKind     tinyint       NULL,
    CONSTRAINT PK_FbMilkModelFitSample PRIMARY KEY (ContentId, Ordinal),
    CONSTRAINT FK_FbMilkModelFitSample_Content
        FOREIGN KEY (ContentId) REFERENCES dbo.FbMilkModelFitContent (ContentId)
);
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_FbMilkModelFitSample_Match'
      AND object_id = OBJECT_ID(N'dbo.FbMilkModelFitSample'))
    CREATE INDEX IX_FbMilkModelFitSample_Match
        ON dbo.FbMilkModelFitSample (ContentId, MatchId);
GO

IF OBJECT_ID(N'dbo.FbMilkModelFitLeague', N'U') IS NULL
CREATE TABLE dbo.FbMilkModelFitLeague
(
    ContentId   bigint        NOT NULL,
    LeagueName  nvarchar(128) NOT NULL,
    HomeSum     float         NOT NULL,
    AwaySum     float         NOT NULL,
    Weight      float         NOT NULL,
    SampleCount int           NOT NULL,
    CONSTRAINT PK_FbMilkModelFitLeague PRIMARY KEY (ContentId, LeagueName),
    CONSTRAINT FK_FbMilkModelFitLeague_Content
        FOREIGN KEY (ContentId) REFERENCES dbo.FbMilkModelFitContent (ContentId)
);
GO

IF OBJECT_ID(N'dbo.FbMilkModelFitTeamLeague', N'U') IS NULL
CREATE TABLE dbo.FbMilkModelFitTeamLeague
(
    ContentId     bigint        NOT NULL,
    LeagueName    nvarchar(128) NOT NULL,
    TeamName      nvarchar(128) NOT NULL,
    GoalsFor      float         NOT NULL,
    GoalsAgainst  float         NOT NULL,
    Weight        float         NOT NULL,
    Games         int           NOT NULL,
    CONSTRAINT PK_FbMilkModelFitTeamLeague PRIMARY KEY (ContentId, LeagueName, TeamName),
    CONSTRAINT FK_FbMilkModelFitTeamLeague_Content
        FOREIGN KEY (ContentId) REFERENCES dbo.FbMilkModelFitContent (ContentId)
);
GO

IF OBJECT_ID(N'dbo.FbMilkModelFitTeam', N'U') IS NULL
CREATE TABLE dbo.FbMilkModelFitTeam
(
    ContentId  bigint        NOT NULL,
    TeamName   nvarchar(128) NOT NULL,
    AttSum     float         NOT NULL,
    DefSum     float         NOT NULL,
    Weight     float         NOT NULL,
    Games      int           NOT NULL,
    CONSTRAINT PK_FbMilkModelFitTeam PRIMARY KEY (ContentId, TeamName),
    CONSTRAINT FK_FbMilkModelFitTeam_Content
        FOREIGN KEY (ContentId) REFERENCES dbo.FbMilkModelFitContent (ContentId)
);
GO

IF OBJECT_ID(N'dbo.FbMilkRun', N'U') IS NOT NULL
AND COL_LENGTH(N'dbo.FbMilkRun', N'ModelFitInstanceId') IS NULL
    ALTER TABLE dbo.FbMilkRun ADD ModelFitInstanceId bigint NULL;

IF OBJECT_ID(N'dbo.FbMilkRun', N'U') IS NOT NULL
AND COL_LENGTH(N'dbo.FbMilkRun', N'ModelFitInstanceId') IS NOT NULL
AND NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_FbMilkRun_ModelFitInstance')
    ALTER TABLE dbo.FbMilkRun ADD CONSTRAINT FK_FbMilkRun_ModelFitInstance
        FOREIGN KEY (ModelFitInstanceId) REFERENCES dbo.FbMilkModelFitInstance (InstanceId);

IF OBJECT_ID(N'dbo.FbMilkInput', N'U') IS NOT NULL
AND COL_LENGTH(N'dbo.FbMilkInput', N'ResolvedLeague') IS NULL
    ALTER TABLE dbo.FbMilkInput ADD
        ResolvedLeague nvarchar(128) NULL,
        TotalLine      decimal(8,2)  NULL,
        LambdaHome     float         NULL,
        LambdaAway     float         NULL;
