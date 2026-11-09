/* ============================================================
   AiFootBall - 彩种字典初始化
   ============================================================ */
USE AiFootBall;
GO

MERGE dbo.LotteryType AS t
USING (VALUES
    -- Code,  Name,          Category, RedCount, RedMax, BlueCount, BlueMax, Weekdays,  SourceUrl, IsActive
    -- IsActive=0 表示「只采开奖、不提供选号」：qlc/qxc 没有选号引擎，见 13_deactivate_unsupported_lottery.sql
    ('ssq',  N'双色球',      N'福彩',  6, 33, 1, 16, '2,4,7', N'https://www.pinble.com/nav/lottery/history/2A9xEQ6RvNaR7kKanPVJjM2KcvEifTjU/yAwaub6BF0d6QrtQlu45JQuuu351uuu351.html', 1),
    ('dlt',  N'大乐透',      N'体彩',  5, 35, 2, 12, '1,3,6', N'https://www.pinble.com/nav/lottery/history/2A9xEQ6RvNaR7kKanPVJjM2KcvEifTjU/aoPqzvEDYamzpKAuuu272kzFedAuuu351uuu351.html', 1),
    ('qlc',  N'七乐彩',      N'福彩',  7, 30, 1, 30, '1,3,5', N'https://www.pinble.com/nav/lottery/history/2A9xEQ6RvNaR7kKanPVJjM2KcvEifTjU/yGYguuu849uuu2729Nyuouuu351.html', 0),
    ('fc3d', N'福彩3D',      N'福彩',  3, 9,  0, 0,  '1,2,3,4,5,6,7', N'https://www.pinble.com/nav/lottery/history/2A9xEQ6RvNaR7kKanPVJjM2KcvEifTjU/uuu8497S6sYiL5sYL4UbXtgEIvQuuu351uuu351.html', 1),
    ('kl8',  N'快乐8',       N'福彩', 20, 80, 0, 0,  '1,2,3,4,5,6,7', N'https://www.pinble.com/nav/lottery/history/2A9xEQ6RvNaR7kKanPVJjM2KcvEifTjU/lymoPxv0WNKy097znKX4vwuuu351uuu351.html', 1),
    ('pl3',  N'排列三',      N'体彩',  3, 9,  0, 0,  '1,2,3,4,5,6,7', N'https://www.pinble.com/nav/lottery/history/2A9xEQ6RvNaR7kKanPVJjM2KcvEifTjU/qhCbwx22nSeLAG6khLgitnhnOtYAoHCI.html', 1),
    ('pl5',  N'排列五',      N'体彩',  5, 9,  0, 0,  '1,2,3,4,5,6,7', N'https://www.pinble.com/nav/lottery/history/2A9xEQ6RvNaR7kKanPVJjM2KcvEifTjU/3BU3jrgddb5q6jFkJ9pHxQuuu351uuu351.html', 1),
    ('qxc',  N'七星彩',      N'体彩',  7, 9,  0, 0,  NULL,            N'https://www.pinble.com/nav/lottery/history/2A9xEQ6RvNaR7kKanPVJjM2KcvEifTjU/n9Zuuu849THQoQBaUGNimwIQGIAuuu351uuu351.html', 0)
) AS s (Code, Name, Category, RedCount, RedMax, BlueCount, BlueMax, Weekdays, SourceUrl, IsActive)
ON t.Code = s.Code
WHEN MATCHED THEN UPDATE SET
    t.Name = s.Name, t.Category = s.Category,
    t.RedCount = s.RedCount, t.RedMax = s.RedMax,
    t.BlueCount = s.BlueCount, t.BlueMax = s.BlueMax,
    t.DrawWeekdays = s.Weekdays, t.SourceUrl = s.SourceUrl, t.IsActive = s.IsActive
WHEN NOT MATCHED THEN INSERT (Code, Name, Category, RedCount, RedMax, BlueCount, BlueMax, DrawWeekdays, SourceUrl, IsActive)
    VALUES (s.Code, s.Name, s.Category, s.RedCount, s.RedMax, s.BlueCount, s.BlueMax, s.Weekdays, s.SourceUrl, s.IsActive);
GO
