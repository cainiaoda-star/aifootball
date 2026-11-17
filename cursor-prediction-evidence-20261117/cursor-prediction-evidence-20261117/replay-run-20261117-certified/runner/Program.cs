using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiFootBall.Core.Models;
using AiFootBall.Data;
using AiFootBall.Sources;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static int Main(string[] args)
    {
        var resultsDir = args.Length > 0
            ? args[0]
            : @"C:\Users\Administrator\FitTraceTest\env-evidence\replay-run-20261117-certified";
        var snapDir = Path.Combine(resultsDir, "export");
        var outDir = Path.Combine(resultsDir, "out");
        Directory.CreateDirectory(outDir);

        var days = new[]
        {
            new DaySpec(
                "2026-11-14",
                "20261114T225008-4408e9",
                new DateTime(2026, 11, 14, 22, 50, 8, 983).AddTicks(3333),
                Array.Empty<long>(),
                new long[] { 1416115, 1416128, 1416117 },
                "Exchange missing on 周六005 1416115, 周六009 1416128, 周六010 1416117 — featured-regression-only; included for milk replay."),
            new DaySpec(
                "2026-11-15",
                "20261115T225017-c6849c",
                new DateTime(2026, 11, 15, 22, 50, 17, 813).AddTicks(3333),
                new long[] { 1398695 },
                Array.Empty<long>(),
                "SKIP 周日008 / MatchId 1398695 because JcBookGap=1 (SPF=0, BQ=0, ExchangeBeforeKickoff=0); cannot do a full milk replay."),
            new DaySpec(
                "2026-11-16",
                "20261116T215008-c60013",
                new DateTime(2026, 11, 16, 21, 50, 8, 256).AddTicks(6667),
                Array.Empty<long>(),
                Array.Empty<long>(),
                "")
        };

        var samplesPath = Path.Combine(snapDir, "samples.tsv");
        if (!File.Exists(samplesPath))
        {
            Console.Error.WriteLine("missing samples: " + samplesPath);
            return 2;
        }

        var samples = LoadSamples(samplesPath);
        var outcomes = LoadOutcomes(Path.Combine(snapDir, "outcomes-posthoc.tsv"));
        var dayReports = new List<DayReport>();

        foreach (var day in days)
        {
            var sw = Stopwatch.StartNew();
            var matchPath = Path.Combine(snapDir, "matches-" + day.SalesDate + ".tsv");
            var oddsPath = Path.Combine(snapDir, "jc-odds-" + day.SalesDate + ".tsv");
            var ouPath = Path.Combine(snapDir, "ou-" + day.SalesDate + ".tsv");
            var officialPath = Path.Combine(snapDir, "official-picks-" + day.SalesDate + ".tsv");

            var matches = LoadMatches(matchPath);
            var odds = LoadOdds(oddsPath);
            var ou = LoadOu(ouPath);
            var official = File.Exists(officialPath) ? LoadTsv(officialPath) : new List<Dictionary<string, string>>();
            var handicapRows = LoadTsv(Path.Combine(snapDir, "handicap-" + day.SalesDate + ".tsv"));
            var waterRows = LoadTsv(Path.Combine(snapDir, "spf-water-" + day.SalesDate + ".tsv"));
            var spanRows = LoadTsv(Path.Combine(snapDir, "price-span-" + day.SalesDate + ".tsv"));
            var euroRows = LoadTsv(Path.Combine(snapDir, "euro-close-" + day.SalesDate + ".tsv"));
            var diagRows = LoadTsv(Path.Combine(snapDir, "score-diag-" + day.SalesDate + ".tsv"));

            var hcpMap = handicapRows.ToDictionary(r => ParseLong(r, "MatchId") ?? 0, r => r);
            var waterMap = new Dictionary<long, (decimal? Open, decimal? Curr)>();
            foreach (var r in waterRows)
            {
                var id = ParseLong(r, "MatchId");
                if (id is null) continue;
                if (!CertOk(r, "certified")) continue;
                var open = ParseDec(r, "OpenWin");
                var curr = ParseDec(r, "CurrWin");
                if (open is > 1m && curr is > 1m)
                    waterMap[id.Value] = (open, curr);
            }
            var spanMap = new Dictionary<long, PriceDivergence.PriceSpan>();
            foreach (var r in spanRows)
            {
                var id = ParseLong(r, "MatchId");
                if (id is null) continue;
                if (!CertOk(r, "certified-jc") && !CertOk(r, "certified")) continue;
                spanMap[id.Value] = new PriceDivergence.PriceSpan(
                    ParseDec(r, "JcOpenHome"), ParseDec(r, "JcCurrHome"),
                    ParseDec(r, "JcOpenAway"), ParseDec(r, "JcCurrAway"),
                    ParseDec(r, "ExOpenHome"), ParseDec(r, "ExCurrHome"),
                    ParseDec(r, "ExOpenAway"), ParseDec(r, "ExCurrAway"));
            }
            var euroMap = new Dictionary<long, (decimal Close, DateTime? At, string Cert)>();
            foreach (var r in euroRows)
            {
                var id = ParseLong(r, "MatchId");
                var close = ParseDec(r, "HomeWinEuClose");
                if (id is null || close is null) continue;
                if (!CertOk(r, "certified")) continue;
                euroMap[id.Value] = (close.Value, ParseTime(r, "CapturedAt"), Get(r, "Certification") ?? "");
            }
            var diagMap = new Dictionary<long, ScoreDiagRow>();
            foreach (var r in diagRows)
            {
                var id = ParseLong(r, "MatchId");
                if (id is null) continue;
                if (!CertOk(r, "certified")) continue;
                diagMap[id.Value] = new ScoreDiagRow
                {
                    MatchId = id.Value,
                    MatchNo = Get(r, "MatchNo"),
                    Rounds = (int)(ParseLong(r, "Rounds") ?? 0),
                    FirstAt = ParseTime(r, "FirstAt"),
                    LastAt = ParseTime(r, "LastAt"),
                    ExpGd = ParseDec(r, "ExpGd"),
                    ExpGdOpen = ParseDec(r, "ExpGdOpen"),
                    ExpGdMove = ParseDec(r, "ExpGdMove"),
                    ExpTotal = ParseDec(r, "ExpTotal"),
                    PHome = ParseDec(r, "PHome"),
                    PDraw = ParseDec(r, "PDraw"),
                    PAway = ParseDec(r, "PAway"),
                    PHomeOpen = ParseDec(r, "PHomeOpen"),
                    P3Plus = ParseDec(r, "P3Plus"),
                    PMinus3 = ParseDec(r, "PMinus3"),
                    P3PlusMove = ParseDec(r, "P3PlusMove"),
                    PMinus3Move = ParseDec(r, "PMinus3Move"),
                    DeepNegDrop = (int)(ParseLong(r, "DeepNegDrop") ?? 0),
                    DeepPosDrop = (int)(ParseLong(r, "DeepPosDrop") ?? 0),
                    DistText = Get(r, "DistText"),
                    TailTag = Get(r, "TailTag")
                };
            }

            var skipped = new List<object>();
            var included = new List<MilkReplayMatchInput>();
            var certLedger = new List<object>();
            foreach (var m in matches)
            {
                hcpMap.TryGetValue(m.MatchId, out var hcpRow);
                var hcpCert = hcpRow is not null && CertOk(hcpRow, "certified");
                var hcpVal = hcpCert ? ParseDec(hcpRow!, "Handicap") : null;
                var hcpAt = hcpCert ? ParseTime(hcpRow!, "HandicapKnownAt") : null;
                euroMap.TryGetValue(m.MatchId, out var euro);
                var waterOk = waterMap.ContainsKey(m.MatchId);
                var spanOk = spanMap.ContainsKey(m.MatchId);
                var diagOk = diagMap.ContainsKey(m.MatchId);
                var euroOk = euroMap.ContainsKey(m.MatchId);
                certLedger.Add(new
                {
                    m.MatchId,
                    m.MatchNo,
                    skipped = day.SkipMatchIds.Contains(m.MatchId),
                    handicap = new { certified = hcpCert, value = hcpVal, knownAt = hcpAt, source = hcpRow is null ? null : Get(hcpRow, "SourceTable") },
                    spfWater = new { certified = waterOk, source = "dbo.FbJcOddsHistory SPF option 3 first/last as-of DecisionAt" },
                    scoreDiag = new { certified = diagOk, source = "dbo.FbJcOddsHistory BF first/last qualifying snapshots as-of DecisionAt" },
                    priceSpan = new { certified = spanOk, source = "dbo.FbJcOddsHistory + dbo.FbExchange first/last as-of DecisionAt" },
                    euroClose = new { certified = euroOk, value = euroOk ? euro.Close : (decimal?)null, capturedAt = euroOk ? euro.At : null, source = "dbo.FbOddsHistory EU CompanyId=0 last as-of DecisionAt" }
                });

                if (day.SkipMatchIds.Contains(m.MatchId))
                {
                    skipped.Add(new
                    {
                        m.MatchId,
                        m.MatchNo,
                        Reason = day.Note
                    });
                    continue;
                }

                included.Add(new MilkReplayMatchInput
                {
                    Recommendation = new MatchRecommendation
                    {
                        Board = Boards.Jc,
                        MatchNo = m.MatchNo ?? "",
                        MatchId = m.MatchId,
                        LeagueName = m.LeagueName,
                        HomeTeam = m.HomeTeam ?? "",
                        AwayTeam = m.AwayTeam ?? "",
                        MatchTime = m.MatchTime,
                        Handicap = hcpVal,
                        HomeWinEuClose = euroOk ? euro.Close : null
                    },
                    HandicapKnownAt = hcpAt
                });
            }

            var request = new MilkReplayRequest
            {
                Decision = day.DecisionAt,
                Matches = included,
                Odds = odds,
                OuLines = ou,
                Samples = samples,
                WaterCertifiedAsOf = true,
                ScoreDiagCertifiedAsOf = true,
                PriceSpanCertifiedAsOf = true,
                EuroCloseCertifiedAsOf = true,
                SpfWater = waterMap,
                ScoreDiags = diagMap,
                PriceSpans = spanMap
            };

            var report = MilkReplay.Run(request);
            sw.Stop();

            var officialKeys = OfficialPickKeys(official);
            var replayOfficialPicks = report.Official.Picks.Select(PickDto).ToList();
            var replayFollowPicks = report.Follow.Picks.Select(PickDto).ToList();
            var replayKeySet = report.Official.Picks
                .Select(p => Key(p.MatchId, p.PlayType, p.OptionCode))
                .ToHashSet(StringComparer.Ordinal);
            var agree = officialKeys.Intersect(replayKeySet, StringComparer.Ordinal).Count();
            var officialOnly = officialKeys.Except(replayKeySet, StringComparer.Ordinal).ToList();
            var replayOnly = replayKeySet.Except(officialKeys, StringComparer.Ordinal).ToList();

            var posthoc = EvaluatePosthoc(report.Official.Picks, outcomes);

            var dayOut = new
            {
                hostname = Environment.MachineName,
                salesDate = day.SalesDate,
                runId = day.RunId,
                decisionAt = day.DecisionAt.ToString("yyyy-MM-dd HH:mm:ss.fffffff"),
                trainFrom = report.TrainFrom.ToString("yyyy-MM-dd HH:mm:ss.fffffff"),
                trainUntil = report.TrainUntil.ToString("yyyy-MM-dd HH:mm:ss.fffffff"),
                samplesLoaded = samples.Count,
                samplesUsed = report.SamplesUsed,
                sourceMatches = matches.Count,
                includedMatchCount = included.Count,
                skipped,
                exchangeGapsNoted = day.ExchangeGapMatchIds,
                note = day.Note,
                comparableToOfficial = report.ComparableToOfficial,
                exchangeScope = report.ExchangeScope,
                trainIngestLimit = report.TrainIngestLimit,
                handicapPolicy = "JC handicap taken only from dbo.BettingCard Board=jc when CapturedAt <= DecisionAt and CapturedAt <= MatchTime. Current FbMatch.Handicap was not used.",
                sideInputs = "Water/ScoreDiag/PriceSpan/EuroClose certified as-of DecisionAt from historical tables. asian/teamProfiles/teamForms remain null (MilkReplay.Run never passes them). See certificationLedger.",
                certificationLedger = certLedger,
                coverage = report.Matches.Select(r => new
                {
                    r.MatchId,
                    r.MatchNo,
                    r.Coverage,
                    r.Missing,
                    r.MissingUses,
                    quoteUntil = r.QuoteUntil.ToString("yyyy-MM-dd HH:mm:ss.fffffff"),
                    oddsCapturedAt = r.OddsCapturedAt?.ToString("yyyy-MM-dd HH:mm:ss.fffffff"),
                    r.AffectsOfficialPicks
                }).ToList(),
                coverageCounts = new
                {
                    full = report.Matches.Count(x => x.Coverage == "full"),
                    partial = report.Matches.Count(x => x.Coverage == "partial"),
                    impossible = report.Matches.Count(x => x.Coverage == "impossible")
                },
                officialPath = new
                {
                    pickCount = report.Official.Picks.Count,
                    picks = replayOfficialPicks,
                    funnel = FunnelDto(report.Official),
                    coherence = report.Official.CoherenceNotes.Select(n => new
                    {
                        n.MatchId,
                        n.AnchorPlayType,
                        n.AnchorOptionCode,
                        n.AnchorModelPct,
                        n.AnchorDirection,
                        n.GridHomePct,
                        n.GridDrawPct,
                        n.GridAwayPct,
                        n.GridDirection,
                        n.DirectionsDiffer,
                        n.DroppedByCoherence
                    }).ToList(),
                    finalNotes = report.Official.SelectionNotes.Where(n => n.InFinal).Select(NoteDto).ToList()
                },
                followPath = new
                {
                    pickCount = report.Follow.Picks.Count,
                    picks = replayFollowPicks,
                    note = "Follow is the perMatch=true path on the same inputs. Do not mix with official-path picks into one hit rate."
                },
                officialPublishedComparison = new
                {
                    publishedPickCount = officialKeys.Count,
                    replayOfficialPickCount = replayKeySet.Count,
                    agreementCount = agree,
                    officialOnly,
                    replayOnly,
                    note = "Agreement is coverage of pick keys (MatchId|PlayType|OptionCode), not hit rate and not proof of improvement. Published picks were produced by the live 2026-11-16 18:48 pack with then-current side inputs; this replay uses football-replay-diag-20261116c and uncertified side inputs."
                },
                posthocEvaluation = posthoc,
                elapsedMs = sw.ElapsedMilliseconds
            };

            var outPath = Path.Combine(outDir, "replay-" + day.SalesDate + ".json");
            File.WriteAllText(outPath, JsonSerializer.Serialize(dayOut, JsonOpts), new UTF8Encoding(false));

            var picksPath = Path.Combine(outDir, "replay-picks-" + day.SalesDate + ".tsv");
            WritePicksTsv(picksPath, report.Official.Picks);

            dayReports.Add(new DayReport
            {
                SalesDate = day.SalesDate,
                RunId = day.RunId,
                DecisionAt = day.DecisionAt,
                Included = included.Count,
                Skipped = skipped.Count,
                OfficialPickCount = report.Official.Picks.Count,
                FollowPickCount = report.Follow.Picks.Count,
                SamplesUsed = report.SamplesUsed,
                Comparable = report.ComparableToOfficial,
                Full = report.Matches.Count(x => x.Coverage == "full"),
                Partial = report.Matches.Count(x => x.Coverage == "partial"),
                Impossible = report.Matches.Count(x => x.Coverage == "impossible"),
                Agreement = agree,
                Published = officialKeys.Count,
                ElapsedMs = sw.ElapsedMilliseconds,
                OutPath = outPath,
                Posthoc = posthoc
            });

            Console.WriteLine("OK {0} officialPicks={1} followPicks={2} samplesUsed={3} elapsedMs={4}",
                day.SalesDate, report.Official.Picks.Count, report.Follow.Picks.Count, report.SamplesUsed, sw.ElapsedMilliseconds);
        }

        var summaryPath = Path.Combine(outDir, "summary.txt");
        var sb = new StringBuilder();
        sb.AppendLine("hostname=" + Environment.MachineName);
        sb.AppendLine("finishedAt=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " Asia/Shanghai");
        sb.AppendLine("pack=football-replay-diag-20261116c (evaluation only, not deployable)");
        sb.AppendLine("entry=MilkReplay.Run (offline-only; not wired into SaveMilkAsync)");
        sb.AppendLine("sideInputsCertified=true (spf-water, score-diag, price-span, euro-close, handicap) where historical as-of rows existed");
        sb.AppendLine("handicapSource=dbo.BettingCard Board=jc CapturedAt<=DecisionAt");
        sb.AppendLine();
        foreach (var d in dayReports)
        {
            sb.AppendLine("---- " + d.SalesDate + " ----");
            sb.AppendLine("runId=" + d.RunId);
            sb.AppendLine("decisionAt=" + d.DecisionAt.ToString("yyyy-MM-dd HH:mm:ss.fffffff"));
            sb.AppendLine("includedMatches=" + d.Included + " skipped=" + d.Skipped);
            sb.AppendLine("coverage full/partial/impossible=" + d.Full + "/" + d.Partial + "/" + d.Impossible);
            sb.AppendLine("comparableToOfficial=" + d.Comparable);
            sb.AppendLine("officialPathPickCount=" + d.OfficialPickCount);
            sb.AppendLine("followPathPickCount=" + d.FollowPickCount);
            sb.AppendLine("samplesUsed=" + d.SamplesUsed);
            sb.AppendLine("publishedPickAgreement=" + d.Agreement + "/" + d.Published);
            sb.AppendLine("elapsedMs=" + d.ElapsedMs);
            sb.AppendLine("json=" + d.OutPath);
            if (d.Posthoc is PosthocEval ev)
            {
                sb.AppendLine("posthocEvaluablePicks=" + ev.Evaluable + " hits=" + ev.Hits + " unevaluated=" + ev.Unevaluated);
                sb.AppendLine("posthocNote=" + ev.Note);
            }
            sb.AppendLine();
        }
        sb.AppendLine("Distinction: coverage is input completeness at DecisionAt. Pick counts are engine output on those inputs. Hit/yield below is post-hoc on later FbMatch scores, not produced by MilkReplay.Run, and is not a claim of improvement over production.");
        File.WriteAllText(summaryPath, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine("summary=" + summaryPath);
        return 0;
    }

    private static List<ScoreSample> LoadSamples(string path)
    {
        var rows = LoadTsv(path);
        var list = new List<ScoreSample>(rows.Count);
        foreach (var r in rows)
        {
            list.Add(new ScoreSample
            {
                MatchId = ParseLong(r, "MatchId") ?? 0,
                LeagueName = Get(r, "LeagueName"),
                HomeTeam = Get(r, "HomeTeam") ?? "",
                AwayTeam = Get(r, "AwayTeam") ?? "",
                HomeScore = (int)(ParseLong(r, "HomeScore") ?? 0),
                AwayScore = (int)(ParseLong(r, "AwayScore") ?? 0),
                MatchTime = ParseTime(r, "MatchTime")
            });
        }
        return list;
    }

    private static List<SrcMatch> LoadMatches(string path)
    {
        var rows = LoadTsv(path);
        var list = new List<SrcMatch>();
        foreach (var r in rows)
        {
            list.Add(new SrcMatch
            {
                MatchId = ParseLong(r, "MatchId") ?? 0,
                MatchNo = Get(r, "MatchNo"),
                LeagueName = Get(r, "LeagueName"),
                HomeTeam = Get(r, "HomeTeam"),
                AwayTeam = Get(r, "AwayTeam"),
                MatchTime = ParseTime(r, "MatchTime")
            });
        }
        return list;
    }

    private static List<JcOddsHistoryPoint> LoadOdds(string path)
    {
        var rows = LoadTsv(path);
        var list = new List<JcOddsHistoryPoint>(rows.Count);
        foreach (var r in rows)
        {
            list.Add(new JcOddsHistoryPoint
            {
                Id = ParseLong(r, "Id") ?? 0,
                MatchId = ParseLong(r, "MatchId"),
                PlayType = Get(r, "PlayType") ?? "",
                OptionCode = Get(r, "OptionCode") ?? "",
                OptionName = Get(r, "OptionName") ?? "",
                Odds = ParseDec(r, "Odds") ?? 0m,
                CapturedAt = ParseTime(r, "CapturedAt") ?? DateTime.MinValue
            });
        }
        return list;
    }

    private static List<OuLineObservation> LoadOu(string path)
    {
        var rows = LoadTsv(path);
        var list = new List<OuLineObservation>(rows.Count);
        foreach (var r in rows)
        {
            list.Add(new OuLineObservation
            {
                MatchId = ParseLong(r, "MatchId") ?? 0,
                TotalLine = ParseDec(r, "TotalLine"),
                CapturedAt = ParseTime(r, "CapturedAt") ?? DateTime.MinValue
            });
        }
        return list;
    }

    private static Dictionary<long, Outcome> LoadOutcomes(string path)
    {
        var map = new Dictionary<long, Outcome>();
        if (!File.Exists(path)) return map;
        foreach (var r in LoadTsv(path))
        {
            var id = ParseLong(r, "MatchId");
            if (id is null) continue;
            map[id.Value] = new Outcome
            {
                MatchId = id.Value,
                HomeScore = ParseLong(r, "HomeScore"),
                AwayScore = ParseLong(r, "AwayScore"),
                Status = Get(r, "Status")
            };
        }
        return map;
    }

    private static HashSet<string> OfficialPickKeys(List<Dictionary<string, string>> rows)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            var mid = ParseLong(r, "MatchId");
            var play = Get(r, "PlayType");
            var code = Get(r, "OptionCode") ?? Get(r, "PickCode");
            if (mid is null || string.IsNullOrWhiteSpace(play) || string.IsNullOrWhiteSpace(code)) continue;
            set.Add(Key(mid, play, code));
        }
        return set;
    }

    private static PosthocEval EvaluatePosthoc(IReadOnlyList<MilkPick> picks, Dictionary<long, Outcome> outcomes)
    {
        int evaluable = 0, hits = 0, unevaluated = 0, missingResult = 0;
        var details = new List<object>();
        foreach (var p in picks)
        {
            if (p.MatchId is not long mid || !outcomes.TryGetValue(mid, out var o) || o.HomeScore is null || o.AwayScore is null)
            {
                missingResult++;
                unevaluated++;
                details.Add(new { p.MatchId, p.MatchNo, p.PlayType, p.OptionCode, hit = (bool?)null, reason = "missing-fulltime-score" });
                continue;
            }

            var hs = (int)o.HomeScore.Value;
            var ascore = (int)o.AwayScore.Value;
            bool? hit = p.PlayType.ToUpperInvariant() switch
            {
                "SPF" => p.OptionCode switch
                {
                    "3" => hs > ascore,
                    "1" => hs == ascore,
                    "0" => hs < ascore,
                    _ => (bool?)null
                },
                "JQ" => HitJq(p.OptionCode, hs + ascore),
                "BF" => HitBf(p.OptionCode, hs, ascore),
                "BQ" => null, // half-time score not exported; do not invent
                "NSPF" => null, // handicap unknown as-of; do not invent
                _ => null
            };

            if (hit is null)
            {
                unevaluated++;
                details.Add(new { p.MatchId, p.MatchNo, p.PlayType, p.OptionCode, hit = (bool?)null, reason = "not-evaluable-with-available-outcomes" });
            }
            else
            {
                evaluable++;
                if (hit.Value) hits++;
                details.Add(new { p.MatchId, p.MatchNo, p.PlayType, p.OptionCode, p.Odds, hit, homeScore = hs, awayScore = ascore });
            }
        }

        double? rate = evaluable > 0 ? (double)hits / evaluable : null;
        return new PosthocEval
        {
            Evaluable = evaluable,
            Hits = hits,
            Unevaluated = unevaluated,
            MissingResult = missingResult,
            HitRateAmongEvaluable = rate,
            Details = details,
            Note = "Post-hoc only, using current FbMatch full-time scores after the decision. Not produced by MilkReplay.Run. BQ needs half-time scores (not used here). NSPF needs a certified as-of handicap (cleared in this run). This is not yield, not an official comparable, and not evidence of improvement. Do not treat a short window as a hit-rate result."
        };
    }

    private static bool? HitJq(string code, int total)
    {
        if (code == "7") return total >= 7;
        if (int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return total == n;
        return null;
    }

    private static bool? HitBf(string code, int hs, int ascore)
    {
        var norm = code.Replace("-", ":");
        var parts = norm.Split(':');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)) return null;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var a)) return null;
        // Common "other" buckets: 4:x / x:4 style may exist; only exact numeric codes are evaluated here.
        return hs == h && ascore == a;
    }

    private static void WritePicksTsv(string path, IReadOnlyList<MilkPick> picks)
    {
        var utf8 = new UTF8Encoding(false);
        using var w = new StreamWriter(path, false, utf8);
        w.WriteLine("MatchId\tMatchNo\tLeagueName\tHomeTeam\tAwayTeam\tPlayType\tOptionCode\tOptionName\tOdds\tModelProbability\tModelPctPure\tEv\tModelEv");
        foreach (var p in picks)
        {
            w.WriteLine(string.Join("\t", new[]
            {
                p.MatchId?.ToString(CultureInfo.InvariantCulture) ?? "",
                p.MatchNo ?? "",
                p.LeagueName ?? "",
                p.HomeTeam ?? "",
                p.AwayTeam ?? "",
                p.PlayType ?? "",
                p.OptionCode ?? "",
                p.OptionName ?? "",
                p.Odds.ToString(CultureInfo.InvariantCulture),
                p.ModelProbability.ToString(CultureInfo.InvariantCulture),
                p.ModelPctPure.ToString(CultureInfo.InvariantCulture),
                p.Ev.ToString(CultureInfo.InvariantCulture),
                p.ModelEv?.ToString(CultureInfo.InvariantCulture) ?? ""
            }));
        }
    }

    private static object PickDto(MilkPick p) => new
    {
        p.MatchId,
        p.MatchNo,
        p.LeagueName,
        p.HomeTeam,
        p.AwayTeam,
        p.PlayType,
        p.OptionCode,
        p.OptionName,
        p.Odds,
        p.ModelProbability,
        p.ModelPctPure,
        p.Ev,
        p.ModelEv,
        p.ModelVersion
    };

    private static object NoteDto(MilkSelectionNote n) => new
    {
        n.MatchId,
        n.MatchNo,
        n.PlayType,
        n.OptionCode,
        n.Odds,
        n.ModelPctPure,
        n.BlendedPct,
        n.MarketPct,
        n.Ev,
        n.KeptByGates,
        n.GateReject,
        n.InFinal,
        n.SampleBand,
        n.HomeRateSource,
        n.AwayRateSource,
        n.HomeGames,
        n.AwayGames
    };

    private static object FunnelDto(MilkBuildResult built)
    {
        var f = built.Funnel;
        return new
        {
            f.ScoresReady,
            f.ScoresNote,
            type = f.GetType().FullName,
            raw = f.ToString()
        };
    }

    private static List<Dictionary<string, string>> LoadTsv(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("missing snapshot", path);
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Length == 0) return new List<Dictionary<string, string>>();
        var header = lines[0].Split('\t');
        var list = new List<Dictionary<string, string>>(lines.Length - 1);
        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var parts = lines[i].Split('\t');
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < header.Length; c++)
            {
                var v = c < parts.Length ? parts[c] : "";
                row[header[c]] = v;
            }
            list.Add(row);
        }
        return list;
    }


    private static bool CertOk(Dictionary<string, string> row, string expectedPrefix)
    {
        var c = Get(row, "Certification") ?? "";
        return c.Equals(expectedPrefix, StringComparison.OrdinalIgnoreCase)
            || c.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Get(Dictionary<string, string> row, string key)
        => row.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    private static long? ParseLong(Dictionary<string, string> row, string key)
    {
        var s = Get(row, key);
        if (s is null) return null;
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;
        return null;
    }

    private static decimal? ParseDec(Dictionary<string, string> row, string key)
    {
        var s = Get(row, key);
        if (s is null) return null;
        if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var n)) return n;
        return null;
    }

    private static DateTime? ParseTime(Dictionary<string, string> row, string key)
    {
        var s = Get(row, key);
        if (s is null) return null;
        var formats = new[]
        {
            "yyyy-MM-ddTHH:mm:ss.fffffff",
            "yyyy-MM-ddTHH:mm:ss.fff",
            "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-dd HH:mm:ss.fffffff",
            "yyyy-MM-dd HH:mm:ss.fff",
            "yyyy-MM-dd HH:mm:ss"
        };
        if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
            return dt;
        return null;
    }

    private static string Key(long? matchId, string? play, string? code)
        => (matchId?.ToString(CultureInfo.InvariantCulture) ?? "") + "|" + (play ?? "") + "|" + (code ?? "");

    private sealed class DaySpec
    {
        public DaySpec(string salesDate, string runId, DateTime decisionAt, long[] skip, long[] exchangeGaps, string note)
        {
            SalesDate = salesDate;
            RunId = runId;
            DecisionAt = decisionAt;
            SkipMatchIds = skip;
            ExchangeGapMatchIds = exchangeGaps;
            Note = note;
        }
        public string SalesDate { get; }
        public string RunId { get; }
        public DateTime DecisionAt { get; }
        public long[] SkipMatchIds { get; }
        public long[] ExchangeGapMatchIds { get; }
        public string Note { get; }
    }

    private sealed class SrcMatch
    {
        public long MatchId { get; set; }
        public string? MatchNo { get; set; }
        public string? LeagueName { get; set; }
        public string? HomeTeam { get; set; }
        public string? AwayTeam { get; set; }
        public DateTime? MatchTime { get; set; }
    }

    private sealed class Outcome
    {
        public long MatchId { get; set; }
        public long? HomeScore { get; set; }
        public long? AwayScore { get; set; }
        public string? Status { get; set; }
    }

    private sealed class PosthocEval
    {
        public int Evaluable { get; set; }
        public int Hits { get; set; }
        public int Unevaluated { get; set; }
        public int MissingResult { get; set; }
        public double? HitRateAmongEvaluable { get; set; }
        public List<object> Details { get; set; } = new();
        public string Note { get; set; } = "";
    }

    private sealed class DayReport
    {
        public string SalesDate { get; set; } = "";
        public string RunId { get; set; } = "";
        public DateTime DecisionAt { get; set; }
        public int Included { get; set; }
        public int Skipped { get; set; }
        public int OfficialPickCount { get; set; }
        public int FollowPickCount { get; set; }
        public int SamplesUsed { get; set; }
        public bool Comparable { get; set; }
        public int Full { get; set; }
        public int Partial { get; set; }
        public int Impossible { get; set; }
        public int Agreement { get; set; }
        public int Published { get; set; }
        public long ElapsedMs { get; set; }
        public string OutPath { get; set; } = "";
        public PosthocEval? Posthoc { get; set; }
    }
}
