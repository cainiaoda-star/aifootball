/*
  竞彩固定奖金：2026-10-31 18:06 之前采样时，500 页面 data-type="nspf"（不让球）被存成了 NSPF（让球），
  data-type="spf"（让球）被存成了 SPF（胜平负）。这里把竞彩行对调回来。
  只跑一次。之后的采样已经按正确玩法写入，再跑会把新数据换反。
  北单行（MatchNo 是纯数字）不动。
*/
SET XACT_ABORT ON;
BEGIN TRAN;

UPDATE dbo.FbJcOddsHistory
SET PlayType = CASE PlayType WHEN 'NSPF' THEN 'SPF' ELSE 'NSPF' END,
    OptionName = CASE
        WHEN PlayType = 'NSPF' THEN CASE OptionCode WHEN '3' THEN N'胜' WHEN '1' THEN N'平' ELSE N'负' END
        ELSE CASE OptionCode WHEN '3' THEN N'让胜' WHEN '1' THEN N'让平' ELSE N'让负' END
    END
WHERE PlayType IN ('NSPF', 'SPF')
  AND MatchNo LIKE N'周%'
  AND CapturedAt < '2026-10-31T18:06:00';

COMMIT;
