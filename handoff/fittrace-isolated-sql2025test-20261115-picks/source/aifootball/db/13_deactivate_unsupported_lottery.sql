/* ------------------------------------------------------------
   七乐彩/七星彩从彩种列表停用（2026-11-03，待办第 8 项）

   背景：LotteryType.IsActive 建表时就有，但 LotteryRepository 从来没读过它，
   字段一直是装饰品。于是 /api/lottery/types 返回 8 个彩种，其中 qlc/qxc
   没有选号引擎，调 /api/lotto/recommend/qlc 直接 400（白名单 LottoCodes.All
   早就把它们排除了，只是没人告诉前端）。

   处理：
   - qlc / qxc 置 IsActive = 0，接口默认只返回启用的 6 个彩种
   - 开奖采集不受影响：CollectionService 显式传 includeInactive:true，
     七乐彩 3003 期历史按日继续追加 —— 停采会让已入库数据变成孤岛
   - /api/lottery/types 每项带 supportsRecommend，前端按它渲染入口，
     不再各自维护白名单
   - 02_seed.sql 的 MERGE 同步带上 IsActive 列，重跑 seed 不会把停用覆盖回启用

   不删行：LotteryDraw 按 LotteryId 外键挂着几万期开奖，删字典行就断参照。
   ------------------------------------------------------------ */

-- 守卫：建库脚本 01_schema 还没跑过的空库不能在这里炸掉启动流程。
IF OBJECT_ID(N'dbo.LotteryType', N'U') IS NOT NULL
    UPDATE dbo.LotteryType SET IsActive = 0 WHERE Code IN ('qlc', 'qxc');
GO

-- 防御：万一 seed 先跑过一轮把 IsActive 覆盖成 1，这里再校正一次。
-- 语句本身幂等，重复执行结果不变。
