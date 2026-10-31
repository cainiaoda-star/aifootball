using AiFootBall.Core.Models;

namespace AiFootBall.Sources;

/// <summary>
/// 推荐引擎：赔率变化和必发占比用来定方向。
/// 水位低于 1.90 只标注，不写「照发」，也不把置信度抬到 60。弱信号和欧赔兜底不进精选。
/// 热门一侧必发极端大热仍不发布。亚盘与欧赔方向冲突时降权。
/// </summary>
public static class RecommendationEngine
{
    /// <summary>赔率变化阈值</summary>
    private const decimal OddsDrop = -0.05m;     // 小降：近60日主胜约53%，不是单日的61%
    private const decimal OddsBigDrop = -0.20m;  // 大降：近60日主胜约30%，诱盘，不推主
    private const decimal OddsRise = 0.05m;      // 主胜赔率上升门槛
    private const decimal OddsBigRise = 0.20m;   // 大升：近60日客胜约55%，不是大降的镜像
    private const decimal HotRatio = 60m;        // 必发偏热下限。68.8% 是混桶，不能当单独命中率
    private const decimal ExtremeHot = 75m;      // 达到后视为极端大热，不再追热
    private const decimal MaxDrawOdds = 3.40m;   // 平局水位高于此视为长冷门，不推平
    private const decimal MinSideOddsForDraw = 2.00m; // 主客任一侧低于此=一边倒，不推平
    private const decimal MaxAbsHandicapForDraw = 0.5m; // 亚盘深于半球，方向感太强，不推平
    private const decimal DeepHomeHandicap = -0.75m; // 主让0.75+：近60日整体主胜约70%，欧赔>1.50时约56%
    private const decimal DeepHomeEuMax = 1.50m; // 高于这条的深主让不进精选
    private const decimal DrawOddsPress = -0.08m; // 回测：平赔再降 ≥0.08 时平局率约 37%
    private const decimal DrawRatioLead = 25m;    // 必发平占比不低于此，且不低于主客，才算平局资金领先
    private const decimal LateReverse = 0.08m;   // 尾盘相对近点反抽超过此值视为反抽
    private const int FormVetoMaxPts = 0;        // 近 5 场 0 分才否决

    /// <summary>
    /// 对一场比赛计算推荐。
    /// </summary>
    /// <param name="homeOddsDelta">欧赔主胜 初盘→终盘 变化（终盘-初盘，负=下降）</param>
    /// <param name="ratioHome">必发主胜投注占比 %</param>
    /// <param name="ratioAway">必发客胜投注占比 %</param>
    /// <param name="totalLine">大小球盘口线（OU 终盘）</param>
    /// <param name="drawOddsDelta">欧赔平局 初盘→终盘 变化（负=平赔被压）</param>
    /// <param name="ratioDraw">必发平局投注占比 %</param>
    /// <param name="lateHomeDelta">尾盘主胜变化（最近采样 − 数小时前；正=尾盘抬高主胜赔=看淡主）</param>
    /// <param name="homeFormPts">主队近 5 场积分（缺省不否决）</param>
    /// <param name="awayFormPts">客队近 5 场积分</param>
    public static MatchRecommendation Recommend(
        string board, string? expect, string matchNo, long? matchId,
        string? league, string home, string away, DateTime? matchTime, decimal? handicap,
        decimal? homeOddsDelta, decimal? ratioHome, decimal? ratioAway, decimal? totalLine = null,
        decimal? spfHome = null, decimal? spfDraw = null, decimal? spfAway = null,
        decimal? drawOddsDelta = null, decimal? ratioDraw = null,
        decimal? asianOpen = null, decimal? asianClose = null,
        decimal? lateHomeDelta = null, int? homeFormPts = null, int? awayFormPts = null)
    {
        var rec = new MatchRecommendation
        {
            Board = board,
            Expect = expect,
            MatchNo = matchNo,
            MatchId = matchId,
            LeagueName = league,
            HomeTeam = home,
            AwayTeam = away,
            MatchTime = matchTime,
            Handicap = handicap,
            HomeOddsDelta = homeOddsDelta,
            DrawOddsDelta = drawOddsDelta,
            RatioHome = ratioHome,
            RatioDraw = ratioDraw,
            RatioAway = ratioAway,
            TotalLine = totalLine
        };

        var homeExtreme = false;
        var awayExtreme = false;

        // 没有升降和必发时：均势仍可推平；否则只能低置信兜底
        if (homeOddsDelta is null && drawOddsDelta is null
            && ratioHome is null && ratioDraw is null && ratioAway is null)
        {
            // 无升降/必发时不推平（水位形态 alone 易虚高）；仅欧赔兜底供胜负彩参考
            rec.SignalLevel = 0;
            rec.SignalDesc = "缺赔率与必发数据，无法判断";
            ApplyOddsFallback(rec, spfHome, spfDraw, spfAway);
        }
        else
        {
        var oddsDown = homeOddsDelta is not null && homeOddsDelta < OddsDrop;
        var oddsBigDown = homeOddsDelta is not null && homeOddsDelta <= OddsBigDrop;
        var oddsUp = homeOddsDelta is not null && homeOddsDelta > OddsRise;
        var oddsBigUp = homeOddsDelta is not null && homeOddsDelta >= OddsBigRise;
        // 60%~75% 才当作可跟随的偏热。≥75% 是极端大热，不追。
        var homeHot = ratioHome is not null && ratioHome >= HotRatio && ratioHome < ExtremeHot;
        var awayHot = ratioAway is not null && ratioAway >= HotRatio && ratioAway < ExtremeHot;
        homeExtreme = ratioHome is not null && ratioHome >= ExtremeHot;
        awayExtreme = ratioAway is not null && ratioAway >= ExtremeHot;

        // 优先级：双信号同向 > 冲突(观望) > 极端大热不追 > 单信号 > 深主让 > 平
        // 近60日：小降主胜~53% / 大降主胜~30% / 大升客胜~55% / 主让0.75+整体~70%（>1.50仅~56%）
        // 注意：大升≠大降镜像——大升+客热是客向强信号，大降+主热仍是诱盘
        if (oddsDown && homeHot && !oddsBigDown)
        {
            rec.RecResult = '3';
            rec.Confidence = 85.7m;
            rec.SignalLevel = 3;
            rec.SignalDesc = $"赔率主胜小降({homeOddsDelta:F2}) + 必发主胜偏热({ratioHome}%)";
        }
        else if (oddsUp && awayHot)
        {
            // 小升或大升 + 客热：客向双信号（大升不按诱盘处理）
            rec.RecResult = '0';
            rec.Confidence = oddsBigUp ? 78m : 71.4m;
            rec.SignalLevel = 3;
            rec.SignalDesc = oddsBigUp
                ? $"赔率主胜大升({homeOddsDelta:F2}) + 必发客胜偏热({ratioAway}%)"
                : $"赔率主胜小升({homeOddsDelta:F2}) + 必发客胜偏热({ratioAway}%)";
        }
        else if (oddsBigDown && homeHot)
        {
            // 大降+偏热：典型诱盘，不推主（含胜负彩）
            rec.RecResult = null;
            rec.Confidence = null;
            rec.SignalLevel = 0;
            rec.SignalDesc = $"赔率主胜大降({homeOddsDelta:F2}) + 必发主胜偏热({ratioHome}%)，诱盘观望";
        }
        else if (oddsDown && (awayHot || awayExtreme))
        {
            // 冲突：不做方向，避免胜负彩跟错边
            rec.RecResult = null;
            rec.Confidence = null;
            rec.SignalLevel = 0;
            rec.SignalDesc = "信号冲突(赔率降但客胜偏热)，观望不进精选";
        }
        else if (oddsUp && (homeHot || homeExtreme))
        {
            rec.RecResult = null;
            rec.Confidence = null;
            rec.SignalLevel = 0;
            rec.SignalDesc = "信号冲突(赔率升但主胜偏热)，观望不进精选";
        }
        else if (oddsDown && homeExtreme)
        {
            rec.RecResult = null;
            rec.Confidence = null;
            rec.SignalLevel = 0;
            rec.SignalDesc = $"赔率主胜下降({homeOddsDelta:F2})，但必发主胜 {ratioHome}% 极端大热，不追热";
        }
        else if (oddsUp && awayExtreme)
        {
            rec.RecResult = null;
            rec.Confidence = null;
            rec.SignalLevel = 0;
            rec.SignalDesc = $"赔率主胜上升({homeOddsDelta:F2})，但必发客胜 {ratioAway}% 极端大热，不追热";
        }
        else if (oddsDown && !oddsBigDown)
        {
            // 小降单信号：近60日主胜约 53%，略高于持平，可参考
            rec.RecResult = '3';
            rec.Confidence = 61.3m;
            rec.SignalLevel = 2;
            rec.SignalDesc = $"赔率主胜小降({homeOddsDelta:F2})";
        }
        else if (oddsBigDown)
        {
            // 大降单信号：诱盘，不推主
            rec.RecResult = null;
            rec.Confidence = null;
            rec.SignalLevel = 0;
            rec.SignalDesc = $"赔率主胜大降({homeOddsDelta:F2})，诱盘观望不进精选";
        }
        else if (oddsBigUp)
        {
            // 大升单信号：客向偏强，可进精选（与大降不对称）
            rec.RecResult = '0';
            rec.Confidence = 62m;
            rec.SignalLevel = 2;
            rec.SignalDesc = $"赔率主胜大升({homeOddsDelta:F2})，看客胜";
        }
        else if (oddsUp)
        {
            rec.RecResult = '0';
            rec.Confidence = 41.7m;
            rec.SignalLevel = 1;
            rec.SignalDesc = $"赔率主胜上升({homeOddsDelta:F2})，弱信号";
        }
        else if (homeHot)
        {
            // 68.8% 是「含双信号」的混桶；剥掉双信号后 alone 约 33%，不进精选
            rec.RecResult = '3';
            rec.Confidence = 45m;
            rec.SignalLevel = 1;
            rec.SignalDesc = $"必发主胜偏热({ratioHome}%)，单信号偏弱不进精选";
        }
        else if (awayHot)
        {
            rec.RecResult = '0';
            rec.Confidence = 55.6m;
            rec.SignalLevel = 1;
            rec.SignalDesc = $"必发客胜偏热({ratioAway}%)，弱信号";
        }
        else if (asianClose is <= DeepHomeHandicap && !homeExtreme)
        {
            // 终盘主让 0.75+：整体主胜约 70%，但欧赔主胜 > 1.50 时只有约 56%，不进精选。
            rec.RecResult = '3';
            if (spfHome is > 1m and <= DeepHomeEuMax)
            {
                rec.Confidence = 73.8m;
                rec.SignalLevel = 2;
                rec.SignalDesc = $"亚盘主让深({asianClose:0.##})，低水主胜";
            }
            else
            {
                rec.Confidence = 56m;
                rec.SignalLevel = 1;
                rec.SignalDesc = $"亚盘主让深({asianClose:0.##})，欧赔主胜高于 1.50，回测约 56%，不进精选";
            }
        }
        else if (!homeExtreme && !awayExtreme
                 && TryPublishableDraw(handicap, spfHome, spfDraw, spfAway,
                     drawOddsDelta, ratioHome, ratioDraw, ratioAway, out var drawConf, out var drawDesc))
        {
            // 回测平约 37%：给方向供参考，但不进精选（置信度 < 发布门槛）
            rec.RecResult = '1';
            rec.Confidence = drawConf;
            rec.SignalLevel = 1;
            rec.SignalDesc = drawDesc + "（平局回测偏低，不进精选）";
        }
        else
        {
            rec.SignalLevel = homeExtreme || awayExtreme ? 1 : 0;
            if (homeExtreme)
                rec.SignalDesc = $"必发主胜占比 {ratioHome}% 极端大热，不追热";
            else if (awayExtreme)
                rec.SignalDesc = $"必发客胜占比 {ratioAway}% 极端大热，不追热";
            else
                rec.SignalDesc = "无强信号，建议观望";
            ApplyOddsFallback(rec, spfHome, spfDraw, spfAway);
        }
        }

        ApplyDeepHomeVsAwayGuard(rec, asianClose, spfHome);
        ApplyAsianConflict(rec, homeOddsDelta, asianOpen, asianClose);
        ApplyLateReverse(rec, homeOddsDelta, lateHomeDelta);
        ApplyFormVeto(rec, homeFormPts, awayFormPts);
        ApplyMarketPolicy(rec, spfHome, spfDraw, spfAway, homeExtreme, awayExtreme);
        ApplyLongPriceCaution(rec, spfHome, spfAway);
        ApplyALeagueCaution(rec);
        if (rec.RecResult is not null)
            PredictScoreAndGoals(rec, totalLine, handicap);

        return rec;
    }

    /// <summary>
    /// 终盘深主让 vs 客向。
    /// 大升或双信号客向：两边都有依据，观望，不改回主胜。
    /// 小升、单独客热：让位给深主让。
    /// </summary>
    private static void ApplyDeepHomeVsAwayGuard(MatchRecommendation rec, decimal? asianClose, decimal? spfHome)
    {
        if (rec.RecResult is not '0') return;
        if (asianClose is null || asianClose.Value > DeepHomeHandicap) return;
        if (rec.IsOddsFallback) return;

        var strongAway = rec.SignalLevel >= 3
                         || rec.SignalDesc.Contains("大升", StringComparison.Ordinal);
        if (strongAway)
        {
            rec.SignalDesc += $"；终盘深主让({asianClose:0.##})与客向冲突，观望";
            rec.RecResult = null;
            rec.Confidence = null;
            rec.SignalLevel = 0;
            return;
        }

        rec.RecResult = '3';
        rec.SignalDesc += $"；终盘深主让({asianClose:0.##})覆盖弱客向，改看主";
        if (spfHome is > 1m and <= DeepHomeEuMax)
        {
            rec.Confidence = 65m;
            rec.SignalLevel = 2;
        }
        else
        {
            rec.Confidence = 56m;
            rec.SignalLevel = 1;
            rec.SignalDesc += "；欧赔主胜高于 1.50，不进精选";
        }
    }

    /// <summary>
    /// 单信号的长水不进精选。近 60 日：小降且主胜水位 &gt; 1.90 时主胜约 40%；
    /// 大升且客胜水位 &gt; 1.90 时客胜约 33%。短水仍保留。
    /// </summary>
    private static void ApplyLongPriceCaution(MatchRecommendation rec, decimal? spfHome, decimal? spfAway)
    {
        if (rec.RecResult is not ('3' or '0') || rec.SignalLevel != 2 || rec.IsOddsFallback) return;
        if (rec.SignalDesc.Contains("不进精选", StringComparison.Ordinal)) return;

        var longHome = rec.RecResult == '3'
                       && spfHome is > 1.90m
                       && rec.SignalDesc.Contains("主胜小降", StringComparison.Ordinal)
                       && !rec.SignalDesc.Contains("偏热", StringComparison.Ordinal);
        var longAway = rec.RecResult == '0'
                       && spfAway is > 1.90m
                       && rec.SignalDesc.Contains("看客胜", StringComparison.Ordinal)
                       && !rec.SignalDesc.Contains("偏热", StringComparison.Ordinal);
        if (!longHome && !longAway) return;

        rec.SignalLevel = 1;
        rec.Confidence = Math.Min(rec.Confidence ?? 40m, longHome ? 41m : 34m);
        rec.SignalDesc += longHome
            ? "；水位高于 1.90，近60日主胜约40%，不进精选"
            : "；客胜水位高于 1.90，近60日客胜约33%，不进精选";
    }

    /// <summary>澳超早段 1 球领先经常被改写。客胜只作参考，不进精选。不改成必推主胜。</summary>
    private static void ApplyALeagueCaution(MatchRecommendation rec)
    {
        if (rec.RecResult is not '0' || rec.IsOddsFallback) return;
        if (!IsALeague(rec.LeagueName)) return;
        if (rec.SignalDesc.Contains("澳超客胜不进精选", StringComparison.Ordinal)) return;

        rec.SignalDesc += "；澳超客胜不进精选";
        rec.SignalLevel = 1;
        if (rec.Confidence is null || rec.Confidence >= RecommendationScoring.PublishConfidence)
            rec.Confidence = Math.Min(rec.Confidence ?? 55m, 55m);
    }

    internal static bool IsALeague(string? league)
    {
        if (string.IsNullOrWhiteSpace(league)) return false;
        return league.Contains("澳超", StringComparison.Ordinal)
               || league.Contains("A-League", StringComparison.OrdinalIgnoreCase)
               || league.Contains("A League", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 亚盘与欧赔方向冲突时降权。
    /// 终盘仍主让 0.75+ 时，不把「盘口略往客移」当成冲突误杀推主。
    /// 仅当终盘已离开深主让、且往反方向明显移动时才降权。
    /// </summary>
    private static void ApplyAsianConflict(
        MatchRecommendation rec, decimal? homeOddsDelta, decimal? asianOpen, decimal? asianClose)
    {
        if (rec.RecResult is not '3' and not '0') return;
        if (asianOpen is null || asianClose is null) return;
        if (rec.IsOddsFallback) return;

        // 亚盘数值：主让为负。close 比 open 更大（更正）→ 盘口往客队倾斜
        var asianToAway = asianClose.Value - asianOpen.Value;
        if (Math.Abs(asianToAway) < 0.20m) return;

        // 推主且终盘仍深主让：保留，不因小幅回撤误杀
        if (rec.RecResult == '3' && asianClose.Value <= DeepHomeHandicap)
            return;
        // 推客且终盘深客受（主让≥+0.75）：保留客向
        if (rec.RecResult == '0' && asianClose.Value >= 0.75m)
            return;

        // 无欧赔升降时，只处理「推客但终盘深主让」——已由 ApplyDeepHomeVsAwayGuard 处理
        if (homeOddsDelta is null) return;

        var oddsDown = homeOddsDelta < OddsDrop;
        var oddsUp = homeOddsDelta > OddsRise;
        var conflict = (rec.RecResult == '3' && oddsDown && asianToAway >= 0.20m)
                       || (rec.RecResult == '0' && oddsUp && asianToAway <= -0.20m);
        if (!conflict) return;

        rec.SignalDesc += $"；亚盘冲突(初{asianOpen:0.##}→终{asianClose:0.##})，降权";
        if (rec.SignalLevel >= 3)
        {
            rec.SignalLevel = 2;
            if (rec.Confidence is > 70m) rec.Confidence = 65m;
        }
        else if (rec.SignalLevel >= 2)
        {
            rec.SignalLevel = 1;
            if (rec.Confidence is >= RecommendationScoring.PublishConfidence)
                rec.Confidence = 55m;
        }
    }

    /// <summary>意甲今年进球偏多，比分/总进球单独抬档（不改胜平负方向）。</summary>
    internal static bool IsSerieA(string? league)
    {
        if (string.IsNullOrWhiteSpace(league)) return false;
        return league.Contains("意甲", StringComparison.Ordinal)
               || league.Contains("Serie A", StringComparison.OrdinalIgnoreCase)
               || league.Contains("Italy Serie A", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 发布关：极端大热仍撤掉。
    /// 水位低于 1.90 只写水位，不写成稳胆，也不抬置信度。
    /// 1.50 及以下命中率高、奖金薄；1.50 到 1.90 不是稳胆。
    /// 弱信号 / 欧赔兜底的低水：只标注，绝不抬等级或置信度进精选。
    /// </summary>
    private static void ApplyMarketPolicy(
        MatchRecommendation rec, decimal? spfHome, decimal? spfDraw, decimal? spfAway,
        bool homeExtreme, bool awayExtreme)
    {
        if (spfHome is null || spfDraw is null || spfAway is null)
        {
            if (rec.SignalLevel >= 2 && rec.RecResult is not null)
            {
                rec.SignalLevel = 1;
                rec.Confidence = 45m;
                rec.SignalDesc += "；缺少赔率，无法确认水位，不发布";
            }
            return;
        }

        if (rec.RecResult is null) return;

        var h = spfHome.Value;
        var d = spfDraw.Value;
        var a = spfAway.Value;
        var pickOdds = rec.RecResult switch
        {
            '3' => h,
            '1' => d,
            '0' => a,
            _ => (decimal?)null
        };
        var heatBlocked = (rec.RecResult == '3' && homeExtreme) || (rec.RecResult == '0' && awayExtreme);
        if (heatBlocked)
        {
            rec.SignalDesc += "；该方向必发极端大热，不推荐";
            rec.RecResult = null;
            rec.Confidence = null;
            rec.SignalLevel = 0;
            rec.ScoreCandidates.Clear();
            rec.GoalCandidates.Clear();
            return;
        }

        if (pickOdds is null || pickOdds >= RecommendationScoring.MinPublishOdds) return;

        var side = rec.RecResult switch { '3' => "主胜", '0' => "客胜", _ => "平局" };
        // 弱信号或兜底：低水不抬进精选（这是此前「无强信号却照发」的根因）
        if (rec.IsOddsFallback || rec.SignalLevel < 2)
        {
            rec.SignalDesc += $"；{side}水位 {pickOdds:F2}，仅参考，不进精选";
            return;
        }

        rec.SignalDesc += pickOdds <= DeepHomeEuMax
            ? $"；{side}水位 {pickOdds:F2}，短水命中率高、奖金薄"
            : $"；{side}水位 {pickOdds:F2}，高于 1.50，不是稳胆";
    }

    /// <summary>
    /// 可发布的平：主客均势、亚盘不深。
    /// 回测平压盘约 37%，置信度封顶 45，且必须有「市场动作」（平赔压或必发平领先），
    /// 不能仅靠水位形态（去水最高/平≤3）凑够加分。
    /// </summary>
    private static bool TryPublishableDraw(
        decimal? handicap, decimal? spfHome, decimal? spfDraw, decimal? spfAway,
        decimal? drawOddsDelta, decimal? ratioHome, decimal? ratioDraw, decimal? ratioAway,
        out decimal confidence, out string desc)
    {
        confidence = 0;
        desc = "";
        if (spfHome is null || spfDraw is null || spfAway is null) return false;

        var h = spfHome.Value;
        var d = spfDraw.Value;
        var a = spfAway.Value;
        if (d > MaxDrawOdds) return false;
        if (h < MinSideOddsForDraw || a < MinSideOddsForDraw) return false;
        if (handicap is not null && Math.Abs(handicap.Value) > MaxAbsHandicapForDraw) return false;

        var devig = HitRateMath.Devig(h, d, a);
        var drawTop = devig is not null
                      && devig.Value.Draw >= devig.Value.Home
                      && devig.Value.Draw >= devig.Value.Away;
        var drawPressed = drawOddsDelta is <= DrawOddsPress;
        var drawFundLead = ratioDraw is >= DrawRatioLead
                           && ratioDraw >= (ratioHome ?? 0m)
                           && ratioDraw >= (ratioAway ?? 0m);
        var softDraw = d <= 3.00m;

        // 水位形态只算 1 分（drawTop 与 softDraw 不叠）
        var shapeBoost = (drawTop || softDraw) ? 1 : 0;
        var marketBoost = 0;
        if (drawPressed) marketBoost++;
        if (drawFundLead) marketBoost++;

        // 必须有市场动作，且总分 ≥2
        if (marketBoost < 1 || shapeBoost + marketBoost < 2) return false;
        // 置信度贴近回测平局率，勿虚高到 68
        confidence = marketBoost >= 2 && shapeBoost >= 1 ? 45m : 42m;

        var bits = new List<string>
        {
            $"主客均≥{MinSideOddsForDraw:0.##}",
            $"平局 {d:F2}"
        };
        if (drawPressed) bits.Add($"平赔下降({drawOddsDelta:F2})");
        if (drawTop) bits.Add("平局去水最高");
        if (drawFundLead) bits.Add($"必发平领先({ratioDraw:0.#}%)");
        if (softDraw && !drawTop) bits.Add("平水位偏低");
        desc = "均势看平：" + string.Join("，", bits);
        return true;
    }

    /// <summary>
    /// 尾盘反抽：开→终看似同向，但近数小时主胜赔反向走，降权出精选。
    /// </summary>
    private static void ApplyLateReverse(
        MatchRecommendation rec, decimal? homeOddsDelta, decimal? lateHomeDelta)
    {
        _ = homeOddsDelta; // 保留参数：调用方仍传入欧赔升降，尾盘逻辑不再依赖它
        if (rec.RecResult is not '3' and not '0') return;
        if (lateHomeDelta is null) return;
        if (rec.IsOddsFallback || rec.SignalLevel < 2) return;
        // 深主让/仅亚盘信号也可能尾盘反抽；不要求必须有欧赔升降

        var lateFadeHome = lateHomeDelta >= LateReverse;   // 尾盘抬高主胜赔 → 看淡主
        var lateSteamHome = lateHomeDelta <= -LateReverse; // 尾盘压低主胜赔 → 看好主
        var conflict = (rec.RecResult == '3' && lateFadeHome)
                       || (rec.RecResult == '0' && lateSteamHome);
        if (!conflict) return;

        // 尾盘反抽：双信号降一档，单信号才踢出精选（避免误杀）
        rec.SignalDesc += $"；尾盘反抽({lateHomeDelta:+0.00;-0.00})，降权";
        if (rec.SignalLevel >= 3)
        {
            rec.SignalLevel = 2;
            if (rec.Confidence is > 70m) rec.Confidence = 65m;
        }
        else if (rec.SignalLevel >= 2)
        {
            rec.SignalLevel = 1;
            if (rec.Confidence is >= RecommendationScoring.PublishConfidence)
                rec.Confidence = 55m;
        }
    }

    /// <summary>
    /// 近况否决：推主/客但该队近 5 场颗粒无收（0 分），且非双信号（L3），则降权。
    /// </summary>
    private static void ApplyFormVeto(MatchRecommendation rec, int? homeFormPts, int? awayFormPts)
    {
        if (rec.RecResult is not '3' and not '0') return;
        if (rec.IsOddsFallback) return;
        if (rec.SignalLevel >= 3) return; // 小降/升 + 偏热等 L3 保留

        var veto = (rec.RecResult == '3' && homeFormPts is <= FormVetoMaxPts)
                   || (rec.RecResult == '0' && awayFormPts is <= FormVetoMaxPts);
        if (!veto) return;

        var pts = rec.RecResult == '3' ? homeFormPts : awayFormPts;
        rec.SignalDesc += $"；近况极差(近5场{pts}分)，降权不进精选";
        if (rec.SignalLevel >= 2)
        {
            rec.SignalLevel = 1;
            if (rec.Confidence is >= RecommendationScoring.PublishConfidence)
                rec.Confidence = 55m;
        }
    }

    /// <summary>
    /// 欧赔兜底：无信号时按「最低赔率 = 最可能结果」给出推荐，置信度较低（约 35~45%）。
    /// 用于胜负彩等必须覆盖全部场次的场景；完全无赔率数据时保持 RecResult=null。
    /// </summary>
    private static void ApplyOddsFallback(MatchRecommendation rec, decimal? spfHome, decimal? spfDraw, decimal? spfAway)
    {
        if (spfHome is null || spfDraw is null || spfAway is null) return; // 无欧赔数据，无法兜底
        var h = spfHome.Value;
        var d = spfDraw.Value;
        var a = spfAway.Value;

        var prior = rec.SignalDesc;
        var keepPrior = prior.Contains("极端大热", StringComparison.Ordinal);
        string desc;
        if (h <= d && h <= a)
        {
            rec.RecResult = '3';
            rec.Confidence = 40m;
            rec.SignalLevel = 1;
            desc = $"赔率兜底：主胜赔率最低({h:F2})，无强信号";
        }
        else if (a <= h && a <= d)
        {
            rec.RecResult = '0';
            rec.Confidence = 38m;
            rec.SignalLevel = 1;
            desc = $"赔率兜底：客胜赔率最低({a:F2})，无强信号";
        }
        else
        {
            rec.RecResult = '1';
            rec.Confidence = 30m;
            rec.SignalLevel = 1;
            desc = $"赔率兜底：平局赔率最低({d:F2})，无强信号";
        }
        rec.IsOddsFallback = true;
        rec.SignalDesc = keepPrior ? prior + "；" + desc : desc;
    }

    /// <summary>
    /// 预测最可能的比分（2 个）与进球数（2 个）。
    /// 依据：
    ///   1. 全局历史比分分布（1-1、1-0、2-1、0-0、2-0、1-2、0-1、2-2 为最常见）
    ///   2. 推荐方向（主胜/客胜/平）过滤
    ///   3. 让球盘 handicap：主让越深（越负）→ 主队越强，比分差越大
    ///   4. 大小球盘口线 totalLine（若有）决定总进球档位
    /// </summary>
    private static void PredictScoreAndGoals(MatchRecommendation rec, decimal? totalLine, decimal? handicap)
    {
        // 全局比分频率表（基于历史完场统计，值越大越常见）
        var baseScores = new Dictionary<(int, int), int>
        {
            { (1,1), 339 }, { (1,0), 260 }, { (2,1), 259 }, { (0,0), 214 },
            { (2,0), 213 }, { (1,2), 211 }, { (0,1), 203 }, { (2,2), 184 },
            { (0,2), 147 }, { (3,1), 133 }, { (3,0), 127 }, { (1,3), 104 },
            { (3,2), 84 }, { (2,3), 74 }, { (4,0), 71 }, { (0,3), 68 },
            { (4,1), 58 }, { (3,3), 48 }, { (5,1), 46 }, { (0,4), 42 },
            { (1,4), 41 }, { (5,0), 39 }, { (4,2), 30 }, { (2,4), 23 },
            { (3,4), 20 }, { (4,3), 17 }, { (5,2), 20 }, { (6,0), 21 },
            { (0,5), 19 }, { (1,5), 18 }, { (5,3), 10 }, { (3,5), 9 },
            { (6,1), 8 }, { (1,6), 7 }, { (4,4), 6 }, { (5,4), 5 }
        };

        var serieA = IsSerieA(rec.LeagueName);
        // 意甲：大小球至少按 3.0 档看，避免死磕 1:0 / 总 2
        var effectiveLine = serieA ? Math.Max(totalLine ?? 3.0m, 3.0m) : totalLine;

        char? dir = rec.RecResult;
        var candidates = baseScores.ToList();
        if (dir == '3') candidates = baseScores.Where(kv => kv.Key.Item1 > kv.Key.Item2).ToList();   // 主胜
        else if (dir == '0') candidates = baseScores.Where(kv => kv.Key.Item1 < kv.Key.Item2).ToList(); // 客胜
        else if (dir == '1') candidates = baseScores.Where(kv => kv.Key.Item1 == kv.Key.Item2).ToList(); // 平
        if (candidates.Count == 0) candidates = baseScores.ToList();

        var adjusted = candidates.ToDictionary(kv => kv.Key, kv => kv.Value);

        // 让球盘驱动比分差：
        //   让球 -1 表示主队要让客队 1 球。要让球主胜，主队必须净胜 ≥2 球（如 2:0、3:1、4:2）；
        //   净胜恰好 1 球（1:0、2:1）在让球盘上是「让平」，并不算让球主胜。
        //   因此：先按让球盘把「让球后仍算胜」的比分保留、其余硬性剔除，
        //   再在满足条件的比分层内，用目标净胜差加权排序。
        //   受让盘（handicap>0，主队是弱队受让）时让球主胜只需净胜 ≥ -handicap+1，门槛更宽松。
        if (dir == '3' || dir == '0')
        {
            int minDiff = 1;   // 无让球/平手盘：净胜 1 球即可
            int targetDiff = 1;
            if (handicap is not null)
            {
                decimal h = handicap.Value;
                if (h < 0)
                {
                    // 主让 |h| 球：让球主胜需净胜 ≥ |h|+1；目标差跟门槛走
                    minDiff = Math.Abs((int)Math.Round(h)) + 1;
                    targetDiff = minDiff;
                }
                else if (h > 0)
                {
                    // 主受让：胜平负主胜按小胜（净胜 1）即可。
                    // 禁止用 |让球|+1 当目标差，否则受让 +1 会把 2:0、3:1 抬成首选（2026-10-30 布里斯班 0-1 教训）。
                    minDiff = 1;
                    targetDiff = 1;
                }
                if (targetDiff > 4) targetDiff = 4;
            }

            foreach (var k in adjusted.Keys.ToList())
            {
                int diff = dir == '3' ? k.Item1 - k.Item2 : k.Item2 - k.Item1;
                // 硬性过滤：不满足「让球后仍胜」的比分直接剔除
                if (diff < minDiff)
                {
                    adjusted.Remove(k);
                    continue;
                }
                int gap = Math.Abs(diff - targetDiff);
                // 精确命中目标差 → +150；差 1 球 → +50；差 2 球 → -80；差 ≥3 球 → -200
                int diffBonus = gap switch
                {
                    0 => 150,
                    1 => 50,
                    2 => -80,
                    _ => -200
                };
                adjusted[k] += diffBonus;
            }

            // 若过滤后无可用比分（极端深盘），退回不过滤，仅用加权
            if (adjusted.Count == 0)
            {
                foreach (var kv in baseScores)
                {
                    int diff = dir == '3' ? kv.Key.Item1 - kv.Key.Item2 : kv.Key.Item2 - kv.Key.Item1;
                    if (diff < 1) continue;
                    adjusted[kv.Key] = kv.Value;
                }
            }
        }

        // 大小球盘口（若有）决定总进球档位；意甲用抬高后的 effectiveLine
        if (effectiveLine is not null)
        {
            foreach (var k in adjusted.Keys.ToList())
            {
                int total = k.Item1 + k.Item2;
                decimal line = effectiveLine.Value;
                int bonus = 0;
                if (line <= 2.0m) { if (total == 1 || total == 2) bonus = 25; }
                else if (line <= 2.5m) { if (total == 2 || total == 3) bonus = 25; }
                else if (line <= 3.0m) { if (total == 3 || total == 4) bonus = 25; }
                else { if (total >= 4) bonus = 25; }
                adjusted[k] += bonus;
            }
        }

        // 意甲额外抬高大比分、压低 0～2 球冷清比分
        if (serieA)
        {
            foreach (var k in adjusted.Keys.ToList())
            {
                int total = k.Item1 + k.Item2;
                adjusted[k] += total switch
                {
                    >= 5 => 80,
                    4 => 55,
                    3 => 30,
                    2 => -40,
                    _ => -80
                };
            }
        }

        // 取权重最高的 2 个比分；意甲优先总球 ≥3，避免再推 1:0 / 0:1
        IEnumerable<KeyValuePair<(int, int), int>> scoreQuery = adjusted.OrderByDescending(kv => kv.Value);
        if (serieA)
        {
            var high = scoreQuery.Where(kv => kv.Key.Item1 + kv.Key.Item2 >= 3).Take(2).ToList();
            if (high.Count >= 2) scoreQuery = high;
            else scoreQuery = scoreQuery.Take(2);
        }
        else scoreQuery = scoreQuery.Take(2);

        var topScores = scoreQuery.ToList();
        rec.ScoreCandidates = topScores.Select(kv => $"{kv.Key.Item1}:{kv.Key.Item2}").ToList();

        // 进球数：比分总进球，从小到大排序，去重取 2 个
        var goals = topScores.Select(kv => kv.Key.Item1 + kv.Key.Item2).Distinct().OrderBy(g => g).ToList();
        if (goals.Count < 2 && topScores.Count > 0)
        {
            var baseGoal = topScores[0].Key.Item1 + topScores[0].Key.Item2;
            if (baseGoal > 0 && !goals.Contains(baseGoal + 1)) goals.Add(baseGoal + 1);
        }

        // 意甲：进球候选尽量落在 ≥3，避免只给 1、2
        if (serieA)
        {
            var highGoals = adjusted
                .OrderByDescending(kv => kv.Value)
                .Select(kv => kv.Key.Item1 + kv.Key.Item2)
                .Where(g => g >= 3)
                .Distinct()
                .Take(2)
                .OrderBy(g => g)
                .ToList();
            if (highGoals.Count == 1)
                highGoals.Add(highGoals[0] + 1);
            if (highGoals.Count >= 2)
                goals = highGoals;
            if (!rec.SignalDesc.Contains("意甲偏大球", StringComparison.Ordinal))
                rec.SignalDesc += "；意甲偏大球，比分/进球抬档";
        }

        rec.GoalCandidates = goals.Take(2).ToList();
    }

    /// <summary>
    /// 模型改了方向之后，再过一遍否决：深盘冲突、尾盘反抽、近况、极端大热、低水。
    /// </summary>
    internal static void ReapplyGuards(
        MatchRecommendation rec,
        decimal? homeOddsDelta,
        decimal? asianOpen,
        decimal? asianClose,
        decimal? lateHomeDelta,
        int? homeFormPts,
        int? awayFormPts,
        decimal? spfHome,
        decimal? spfDraw,
        decimal? spfAway)
    {
        var homeExtreme = rec.RatioHome >= ExtremeHot;
        var awayExtreme = rec.RatioAway >= ExtremeHot;
        ApplyDeepHomeVsAwayGuard(rec, asianClose, spfHome);
        ApplyAsianConflict(rec, homeOddsDelta, asianOpen, asianClose);
        ApplyLateReverse(rec, homeOddsDelta, lateHomeDelta);
        ApplyFormVeto(rec, homeFormPts, awayFormPts);
        ApplyMarketPolicy(rec, spfHome, spfDraw, spfAway, homeExtreme, awayExtreme);
        ApplyLongPriceCaution(rec, spfHome, spfAway);
        ApplyALeagueCaution(rec);
    }

    /// <summary>有球队攻防时用它替换全局比分表。没有则仍用原来的频率表。</summary>
    internal static void ApplyScoreline(MatchRecommendation rec, ScorelineModel? model)
    {
        if (rec.RecResult is null)
        {
            rec.ScoreCandidates.Clear();
            rec.GoalCandidates.Clear();
            return;
        }

        var line = rec.TotalLine;
        if (IsSerieA(rec.LeagueName))
            line = Math.Max(line ?? 3.0m, 3.0m);

        if (model is { Ready: true })
        {
            var top = model.Top(rec.LeagueName, rec.HomeTeam, rec.AwayTeam, rec.RecResult.Value, line, rec.Handicap);
            if (top.Count > 0)
            {
                rec.ScoreCandidates = top.Select(s => $"{s.Home}:{s.Away}").ToList();
                var goals = top.Select(s => s.Home + s.Away).Distinct().OrderBy(g => g).Take(2).ToList();
                if (goals.Count == 1) goals.Add(goals[0] + 1);
                rec.GoalCandidates = goals;
                return;
            }
        }

        PredictScoreAndGoals(rec, rec.TotalLine, rec.Handicap);
    }
}
