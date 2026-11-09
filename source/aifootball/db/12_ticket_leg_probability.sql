/* ------------------------------------------------------------
   票腿概率拆成三个数：票面用的、模型算的、盘口去水的。

   起因（2026-11-03，策略审查 B 项）：票腿概率来自 JcCoverTicket.LegProbability，
   那是竞彩让球三项直接去水；而选腿的 55% 门槛用的是 HandicapProbability（引擎的模型值）。
   同一张票，门槛按模型判、奖金概率按盘口算，两套数谁也没标注，于是
   「中奖率 92.7% / 回本率 19.7%」这两个数说不清是哪个口径给的。

   审查原判断是「票腿概率未收缩所以偏乐观」。实测不同意：拿 11-03 的票对比，
   模型 57.8 对盘口 59.7、模型 61.1 对盘口 57.4，高低混着来，没有系统性偏乐观。
   两个数都不含经验校准（都没按水位段历史命中率收缩），所以谁也不能自称准。

   处理：票面概率取两者较低值（保守下界），并把原值都落库。
   不拍脑袋给 Shrink 强度——没有让球方向的经验样本，任何收缩系数都是编的。
   留这两列是为了以后能回答「模型和盘口到底谁准」，那是这套系统唯一能自证的问题。
   ------------------------------------------------------------ */

IF COL_LENGTH(N'dbo.TicketPlanLine', N'ModelProbability') IS NULL
    ALTER TABLE dbo.TicketPlanLine ADD ModelProbability DECIMAL(6,2) NULL;
GO

IF COL_LENGTH(N'dbo.TicketPlanLine', N'MarketProbability') IS NULL
    ALTER TABLE dbo.TicketPlanLine ADD MarketProbability DECIMAL(6,2) NULL;
GO

-- LegProbability 从此含义变成「两者较低值」，不是单纯去水值。老行保持原样，不回填：
-- 当时算的就是盘口值，改了反而让人以为历史也是双口径。
