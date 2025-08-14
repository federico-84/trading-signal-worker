using MongoDB.Bson;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;
using PortfolioSignalWorker.Models;
using static PortfolioSignalWorker.Models.TradingSignal;

namespace PortfolioSignalWorker.Services
{
    public class TakeProfitPerformanceTracker
    {
        private readonly IMongoCollection<TakeProfitPerformanceRecord> _performanceCollection;
        private readonly IMongoCollection<StockIndicator> _indicatorCollection;
        private readonly YahooFinanceService _yahooFinance;
        private readonly ILogger<TakeProfitPerformanceTracker> _logger;
        private readonly DataDrivenTakeProfitConfig _config;

        public TakeProfitPerformanceTracker(
            IMongoDatabase database,
            YahooFinanceService yahooFinance,
            ILogger<TakeProfitPerformanceTracker> logger,
            DataDrivenTakeProfitConfig config = null)
        {
            _performanceCollection = database.GetCollection<TakeProfitPerformanceRecord>("TakeProfitPerformance");
            _indicatorCollection = database.GetCollection<StockIndicator>("Indicators");
            _yahooFinance = yahooFinance;
            _logger = logger;
            _config = config ?? new DataDrivenTakeProfitConfig();

            CreateIndexes();
        }

        private void CreateIndexes()
        {
            // Index per symbol e data
            var symbolDateIndex = Builders<TakeProfitPerformanceRecord>.IndexKeys
                .Ascending(x => x.Symbol)
                .Descending(x => x.CreatedAt);
            _performanceCollection.Indexes.CreateOne(new CreateIndexModel<TakeProfitPerformanceRecord>(symbolDateIndex));

            // Index per strategia
            var strategyIndex = Builders<TakeProfitPerformanceRecord>.IndexKeys
                .Ascending(x => x.TakeProfitStrategy)
                .Ascending(x => x.IsCompleted);
            _performanceCollection.Indexes.CreateOne(new CreateIndexModel<TakeProfitPerformanceRecord>(strategyIndex));

            // TTL Index per cleanup automatico (mantieni 6 mesi)
            var ttlIndex = Builders<TakeProfitPerformanceRecord>.IndexKeys.Ascending(x => x.CreatedAt);
            var ttlOptions = new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(180) };
            _performanceCollection.Indexes.CreateOne(new CreateIndexModel<TakeProfitPerformanceRecord>(ttlIndex, ttlOptions));
        }

        /// <summary>
        /// Registra una nuova strategia di take profit per tracking
        /// </summary>
        public async Task<ObjectId> TrackNewTakeProfit(
            string symbol,
            SignalType signalType,
            double confidence,
            double entryPrice,
            double stopLoss,
            TakeProfitOption takeProfitOption)
        {
            try
            {
                var record = new TakeProfitPerformanceRecord
                {
                    Symbol = symbol,
                    OriginalSignalType = signalType,
                    OriginalConfidence = confidence,
                    EntryPrice = entryPrice,
                    StopLoss = stopLoss,
                    TakeProfitStrategy = takeProfitOption.Strategy,
                    TakeProfitPrice = takeProfitOption.Price,
                    TakeProfitPercentage = takeProfitOption.Percentage,
                    PredictedProbability = takeProfitOption.ProbabilityOfSuccess,
                    IsCompleted = false,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                await _performanceCollection.InsertOneAsync(record);

                _logger.LogInformation($"📊 Tracking new TP strategy for {symbol}: " +
                    $"{takeProfitOption.Strategy} - Target: ${takeProfitOption.Price:F2}");

                return record.Id;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error tracking new take profit for {symbol}", symbol);
                return ObjectId.Empty;
            }
        }

        /// <summary>
        /// Aggiorna tutti i take profit attivi controllando se sono stati raggiunti
        /// </summary>
        public async Task UpdateActiveTracking()
        {
            try
            {
                var activeRecords = await _performanceCollection
                    .Find(Builders<TakeProfitPerformanceRecord>.Filter.Eq(x => x.IsCompleted, false))
                    .ToListAsync();

                _logger.LogInformation($"Updating {activeRecords.Count} active take profit tracking records");

                var updateTasks = activeRecords
                    .GroupBy(r => r.Symbol)
                    .Select(group => UpdateSymbolTracking(group.Key, group.ToList()));

                await Task.WhenAll(updateTasks);

                // Cleanup vecchi record non completati (> 30 giorni)
                await CleanupStaleRecords();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating take profit tracking");
            }
        }

        private async Task UpdateSymbolTracking(string symbol, List<TakeProfitPerformanceRecord> records)
        {
            try
            {
                var currentQuote = await _yahooFinance.GetQuoteAsync(symbol);
                var currentPrice = currentQuote["c"]?.Value<double>() ?? 0;

                if (currentPrice <= 0)
                {
                    _logger.LogWarning($"Invalid price for {symbol}: {currentPrice}");
                    return;
                }

                var updatedRecords = new List<TakeProfitPerformanceRecord>();

                foreach (var record in records)
                {
                    var result = AnalyzeTradingResult(record, currentPrice);

                    if (result.HasValue)  // 🔄 CORRETTO: Usa .HasValue per enum nullable
                    {
                        record.IsCompleted = true;
                        record.ActualResult = result;
                        record.CompletedAt = DateTime.UtcNow;
                        record.UpdatedAt = DateTime.UtcNow;
                        record.HoldingPeriodDays = (DateTime.UtcNow - record.CreatedAt).Days;

                        // Calcola return effettivo - 🔄 CORRETTO: Usa enum values
                        record.ActualReturn = result switch
                        {
                            SimplifiedTakeProfitResult.Hit => ((record.TakeProfitPrice - record.EntryPrice) / record.EntryPrice) * 100,
                            SimplifiedTakeProfitResult.StoppedOut => ((record.StopLoss - record.EntryPrice) / record.EntryPrice) * 100,
                            SimplifiedTakeProfitResult.PartialHit => ((currentPrice - record.EntryPrice) / record.EntryPrice) * 100,
                            _ => 0
                        };

                        updatedRecords.Add(record);

                        _logger.LogInformation($"📈 TP {result} for {symbol}: " +
                            $"Strategy={record.TakeProfitStrategy}, " +
                            $"Return={record.ActualReturn:F1}%, " +
                            $"Days={record.HoldingPeriodDays}");
                    }
                }

                // Bulk update per efficienza
                if (updatedRecords.Any())
                {
                    var updates = updatedRecords.Select(record =>
                        new ReplaceOneModel<TakeProfitPerformanceRecord>(
                            Builders<TakeProfitPerformanceRecord>.Filter.Eq(x => x.Id, record.Id),
                            record));

                    await _performanceCollection.BulkWriteAsync(updates);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating tracking for symbol {symbol}", symbol);
            }
        }

        // 🔄 CORRETTO: Ritorna enum nullable invece di classe
        private SimplifiedTakeProfitResult? AnalyzeTradingResult(TakeProfitPerformanceRecord record, double currentPrice)
        {
            var entryPrice = record.EntryPrice;
            var takeProfitPrice = record.TakeProfitPrice;
            var stopLoss = record.StopLoss;

            // Controlla se take profit è stato raggiunto (assume buy signal)
            if (currentPrice >= takeProfitPrice)
            {
                return SimplifiedTakeProfitResult.Hit;
            }

            // Controlla se stop loss è stato attivato
            if (currentPrice <= stopLoss)
            {
                return SimplifiedTakeProfitResult.StoppedOut;
            }

            // Controlla se il trade è scaduto (più di 30 giorni)
            var daysSinceEntry = (DateTime.UtcNow - record.CreatedAt).Days;
            if (daysSinceEntry > _config.PerformanceTrackingDays)
            {
                return SimplifiedTakeProfitResult.Expired;
            }

            // Trade ancora attivo
            return null;
        }

        /// <summary>
        /// Genera statistiche di performance per strategia
        /// </summary>
        public async Task<List<TakeProfitStrategyStatistics>> GetStrategyStatistics(int days = 90)
        {
            try
            {
                var cutoffDate = DateTime.UtcNow.AddDays(-days);

                var completedRecords = await _performanceCollection
                    .Find(Builders<TakeProfitPerformanceRecord>.Filter.And(
                        Builders<TakeProfitPerformanceRecord>.Filter.Eq(x => x.IsCompleted, true),
                        Builders<TakeProfitPerformanceRecord>.Filter.Gte(x => x.CreatedAt, cutoffDate)
                    ))
                    .ToListAsync();

                var strategyStats = completedRecords
                    .GroupBy(r => r.TakeProfitStrategy)
                    .Select(group => CalculateStrategyStatistics(group.Key, group.ToList()))
                    .OrderByDescending(s => s.SuccessRate)
                    .ToList();

                _logger.LogInformation($"Generated statistics for {strategyStats.Count} strategies over {days} days");

                return strategyStats;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating strategy statistics");
                return new List<TakeProfitStrategyStatistics>();
            }
        }

        private TakeProfitStrategyStatistics CalculateStrategyStatistics(
            string strategyName,
            List<TakeProfitPerformanceRecord> records)
        {
            // 🔄 CORRETTO: Usa enum values
            var successful = records.Where(r =>
                r.ActualResult == SimplifiedTakeProfitResult.Hit ||
                r.ActualResult == SimplifiedTakeProfitResult.PartialHit).ToList();

            var failed = records.Where(r =>
                r.ActualResult == SimplifiedTakeProfitResult.StoppedOut).ToList();

            var stats = new TakeProfitStrategyStatistics
            {
                StrategyName = strategyName,
                TotalTrades = records.Count,
                SuccessfulTrades = successful.Count,
                LastUpdated = DateTime.UtcNow
            };

            if (records.Any(r => r.ActualReturn.HasValue))
            {
                var returns = records.Where(r => r.ActualReturn.HasValue).Select(r => r.ActualReturn.Value).ToList();

                stats.AverageReturn = returns.Average();
                stats.BestTrade = returns.Max();
                stats.WorstTrade = returns.Min();

                if (successful.Any(r => r.ActualReturn.HasValue))
                {
                    stats.AverageSuccessfulReturn = successful
                        .Where(r => r.ActualReturn.HasValue)
                        .Average(r => r.ActualReturn.Value);
                }

                if (failed.Any(r => r.ActualReturn.HasValue))
                {
                    stats.AverageFailedReturn = failed
                        .Where(r => r.ActualReturn.HasValue)
                        .Average(r => r.ActualReturn.Value);
                }
            }

            if (records.Any(r => r.HoldingPeriodDays.HasValue))
            {
                stats.AverageHoldingPeriod = records
                    .Where(r => r.HoldingPeriodDays.HasValue)
                    .Average(r => r.HoldingPeriodDays.Value);
            }

            stats.AveragePredictedProbability = records.Average(r => r.PredictedProbability);

            // Calcola accuratezza predizioni
            var accuracyScores = records.Select(r => r.StrategyAccuracy).ToList();
            stats.ActualVsPredictedAccuracy = accuracyScores.Any() ? accuracyScores.Average() * 100 : 0;

            return stats;
        }

        /// <summary>
        /// Ottieni performance per simbolo specifico
        /// </summary>
        public async Task<List<TakeProfitPerformanceRecord>> GetSymbolPerformance(string symbol, int days = 30)
        {
            var cutoffDate = DateTime.UtcNow.AddDays(-days);

            return await _performanceCollection
                .Find(Builders<TakeProfitPerformanceRecord>.Filter.And(
                    Builders<TakeProfitPerformanceRecord>.Filter.Eq(x => x.Symbol, symbol),
                    Builders<TakeProfitPerformanceRecord>.Filter.Gte(x => x.CreatedAt, cutoffDate)
                ))
                .SortByDescending(x => x.CreatedAt)
                .ToListAsync();
        }

        /// <summary>
        /// Analizza quali strategie performano meglio per confidence ranges specifici
        /// </summary>
        public async Task<Dictionary<string, List<ConfidenceRangePerformance>>> AnalyzePerformanceByConfidence()
        {
            var completedRecords = await _performanceCollection
                .Find(Builders<TakeProfitPerformanceRecord>.Filter.Eq(x => x.IsCompleted, true))
                .ToListAsync();

            var confidenceRanges = new[]
            {
                (60.0, 70.0),
                (70.0, 80.0),
                (80.0, 90.0),
                (90.0, 100.0)
            };

            var result = new Dictionary<string, List<ConfidenceRangePerformance>>();

            foreach (var strategy in completedRecords.GroupBy(r => r.TakeProfitStrategy))
            {
                var performanceByRange = new List<ConfidenceRangePerformance>();

                foreach (var (minConf, maxConf) in confidenceRanges)
                {
                    var recordsInRange = strategy
                        .Where(r => r.OriginalConfidence >= minConf && r.OriginalConfidence < maxConf)
                        .ToList();

                    if (recordsInRange.Any())
                    {
                        // 🔄 CORRETTO: Usa enum values
                        var successful = recordsInRange.Count(r =>
                            r.ActualResult == SimplifiedTakeProfitResult.Hit ||
                            r.ActualResult == SimplifiedTakeProfitResult.PartialHit);

                        var avgReturn = recordsInRange
                            .Where(r => r.ActualReturn.HasValue)
                            .Select(r => r.ActualReturn.Value)
                            .DefaultIfEmpty(0)
                            .Average();

                        performanceByRange.Add(new ConfidenceRangePerformance
                        {
                            MinConfidence = minConf,
                            MaxConfidence = maxConf,
                            SignalCount = recordsInRange.Count,
                            SuccessRate = (double)successful / recordsInRange.Count * 100,
                            AverageReturn = avgReturn
                        });
                    }
                }

                result[strategy.Key] = performanceByRange;
            }

            return result;
        }

        /// <summary>
        /// Genera report dettagliato di performance
        /// </summary>
        public async Task<string> GeneratePerformanceReport(int days = 30)
        {
            var stats = await GetStrategyStatistics(days);
            var confidenceAnalysis = await AnalyzePerformanceByConfidence();

            var report = new List<string>
            {
                "=== TAKE PROFIT PERFORMANCE REPORT ===",
                $"Period: Last {days} days",
                $"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm}",
                "",
                "STRATEGY PERFORMANCE:",
            };

            foreach (var stat in stats.Take(10)) // Top 10 strategie
            {
                report.Add($"  {stat}");
            }

            report.Add("");
            report.Add("CONFIDENCE ANALYSIS:");

            foreach (var strategy in confidenceAnalysis.Take(5))
            {
                report.Add($"  Strategy: {strategy.Key}");
                foreach (var range in strategy.Value)
                {
                    report.Add($"    {range.GetRangeDescription()}: " +
                              $"{range.SuccessRate:F1}% ({range.SignalCount} signals) " +
                              $"Avg: {range.AverageReturn:F1}%");
                }
                report.Add("");
            }

            // Aggiungi raccomandazioni
            report.Add("RECOMMENDATIONS:");
            var bestStrategy = stats.FirstOrDefault();
            if (bestStrategy != null)
            {
                report.Add($"  • Best performing strategy: {bestStrategy.StrategyName} ({bestStrategy.SuccessRate:F1}%)");

                if (bestStrategy.AverageReturn > 8.0)
                    report.Add($"  • {bestStrategy.StrategyName} shows strong returns ({bestStrategy.AverageReturn:F1}% avg)");

                if (bestStrategy.SuccessRate > 70.0)
                    report.Add($"  • High reliability strategy identified: {bestStrategy.StrategyName}");
            }

            var worstStrategy = stats.LastOrDefault();
            if (worstStrategy != null && worstStrategy.SuccessRate < 40.0)
            {
                report.Add($"  • Consider reviewing: {worstStrategy.StrategyName} ({worstStrategy.SuccessRate:F1}% success rate)");
            }

            // Analizza trend generali
            var avgSuccessRate = stats.Average(s => s.SuccessRate);
            var avgReturn = stats.Average(s => s.AverageReturn);

            report.Add($"  • Overall average success rate: {avgSuccessRate:F1}%");
            report.Add($"  • Overall average return: {avgReturn:F1}%");

            if (avgSuccessRate > 60.0)
                report.Add("  • System performance is above target (>60%)");
            else
                report.Add("  • System performance needs improvement (<60%)");

            return string.Join(Environment.NewLine, report);
        }

        /// <summary>
        /// Pulisce record vecchi e non completati
        /// </summary>
        private async Task CleanupStaleRecords()
        {
            try
            {
                var staleDate = DateTime.UtcNow.AddDays(-_config.PerformanceTrackingDays);

                var staleFilter = Builders<TakeProfitPerformanceRecord>.Filter.And(
                    Builders<TakeProfitPerformanceRecord>.Filter.Eq(x => x.IsCompleted, false),
                    Builders<TakeProfitPerformanceRecord>.Filter.Lt(x => x.CreatedAt, staleDate)
                );

                // Prima marca come scaduti
                var staleRecords = await _performanceCollection.Find(staleFilter).ToListAsync();

                foreach (var record in staleRecords)
                {
                    record.IsCompleted = true;
                    record.ActualResult = SimplifiedTakeProfitResult.Expired;  // 🔄 CORRETTO: Usa enum
                    record.CompletedAt = DateTime.UtcNow;
                    record.UpdatedAt = DateTime.UtcNow;
                    record.HoldingPeriodDays = (DateTime.UtcNow - record.CreatedAt).Days;
                    record.Notes = "Auto-expired after tracking period";
                }

                if (staleRecords.Any())
                {
                    var updates = staleRecords.Select(record =>
                        new ReplaceOneModel<TakeProfitPerformanceRecord>(
                            Builders<TakeProfitPerformanceRecord>.Filter.Eq(x => x.Id, record.Id),
                            record));

                    await _performanceCollection.BulkWriteAsync(updates);

                    _logger.LogInformation($"🧹 Cleaned up {staleRecords.Count} stale tracking records");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during stale records cleanup");
            }
        }

        /// <summary>
        /// Ottieni insight per miglioramento delle strategie
        /// </summary>
        public async Task<TakeProfitInsights> GenerateStrategicInsights()
        {
            try
            {
                var recentRecords = await _performanceCollection
                    .Find(Builders<TakeProfitPerformanceRecord>.Filter.And(
                        Builders<TakeProfitPerformanceRecord>.Filter.Eq(x => x.IsCompleted, true),
                        Builders<TakeProfitPerformanceRecord>.Filter.Gte(x => x.CreatedAt, DateTime.UtcNow.AddDays(-60))
                    ))
                    .ToListAsync();

                if (!recentRecords.Any())
                {
                    return new TakeProfitInsights { HasSufficientData = false };
                }

                var insights = new TakeProfitInsights
                {
                    HasSufficientData = true,
                    AnalyzedRecords = recentRecords.Count,
                    AnalysisPeriodDays = 60,
                    GeneratedAt = DateTime.UtcNow
                };

                // 1. Analisi per range di confidence
                insights.ConfidenceInsights = AnalyzeConfidenceEffectiveness(recentRecords);

                // 2. Analisi per tipo di segnale
                insights.SignalTypeInsights = AnalyzeSignalTypeEffectiveness(recentRecords);

                // 3. Analisi timing (holding period)
                insights.TimingInsights = AnalyzeTimingEffectiveness(recentRecords);

                // 4. Analisi per range di prezzi
                insights.PriceRangeInsights = AnalyzePriceRangeEffectiveness(recentRecords);

                // 5. Identificazione pattern
                insights.PatternInsights = IdentifySuccessPatterns(recentRecords);

                // 6. Raccomandazioni strategiche
                insights.Recommendations = GenerateStrategicRecommendations(insights);

                return insights;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating strategic insights");
                return new TakeProfitInsights { HasSufficientData = false };
            }
        }

        private List<ConfidenceInsight> AnalyzeConfidenceEffectiveness(List<TakeProfitPerformanceRecord> records)
        {
            var ranges = new[] { (60.0, 70.0), (70.0, 80.0), (80.0, 90.0), (90.0, 100.0) };

            return ranges.Select(range =>
            {
                var recordsInRange = records
                    .Where(r => r.OriginalConfidence >= range.Item1 && r.OriginalConfidence < range.Item2)
                    .ToList();

                if (!recordsInRange.Any())
                    return null;

                // 🔄 CORRETTO: Usa enum values
                var successful = recordsInRange.Count(r => r.ActualResult == SimplifiedTakeProfitResult.Hit);
                var avgReturn = recordsInRange.Where(r => r.ActualReturn.HasValue).Average(r => r.ActualReturn.Value);
                var avgHoldingPeriod = recordsInRange.Where(r => r.HoldingPeriodDays.HasValue).Average(r => r.HoldingPeriodDays.Value);

                return new ConfidenceInsight
                {
                    MinConfidence = range.Item1,
                    MaxConfidence = range.Item2,
                    TotalSignals = recordsInRange.Count,
                    SuccessRate = (double)successful / recordsInRange.Count * 100,
                    AverageReturn = avgReturn,
                    AverageHoldingPeriod = avgHoldingPeriod
                };
            }).Where(insight => insight != null).ToList();
        }

        private List<SignalTypeInsight> AnalyzeSignalTypeEffectiveness(List<TakeProfitPerformanceRecord> records)
        {
            return records
                .GroupBy(r => r.OriginalSignalType)
                .Select(group =>
                {
                    var groupRecords = group.ToList();
                    // 🔄 CORRETTO: Usa enum values
                    var successful = groupRecords.Count(r => r.ActualResult == SimplifiedTakeProfitResult.Hit);

                    return new SignalTypeInsight
                    {
                        SignalType = group.Key,
                        TotalSignals = groupRecords.Count,
                        SuccessRate = (double)successful / groupRecords.Count * 100,
                        AverageReturn = groupRecords.Where(r => r.ActualReturn.HasValue).Average(r => r.ActualReturn.Value),
                        BestStrategy = groupRecords
                            .GroupBy(r => r.TakeProfitStrategy)
                            .OrderByDescending(s => s.Count(x => x.ActualResult == SimplifiedTakeProfitResult.Hit))
                            .First().Key
                    };
                }).ToList();
        }

        private TimingInsight AnalyzeTimingEffectiveness(List<TakeProfitPerformanceRecord> records)
        {
            var recordsWithTiming = records.Where(r => r.HoldingPeriodDays.HasValue).ToList();

            if (!recordsWithTiming.Any())
                return new TimingInsight();

            // 🔄 CORRETTO: Usa enum values
            var successful = recordsWithTiming.Where(r => r.ActualResult == SimplifiedTakeProfitResult.Hit).ToList();
            var failed = recordsWithTiming.Where(r => r.ActualResult == SimplifiedTakeProfitResult.StoppedOut).ToList();

            return new TimingInsight
            {
                AverageSuccessfulHoldingPeriod = successful.Any() ? successful.Average(r => r.HoldingPeriodDays.Value) : 0,
                AverageFailedHoldingPeriod = failed.Any() ? failed.Average(r => r.HoldingPeriodDays.Value) : 0,
                OptimalHoldingPeriodRange = DetermineOptimalHoldingPeriod(successful),
                QuickWinsPercentage = successful.Count(r => r.HoldingPeriodDays.Value <= 3) / (double)successful.Count * 100
            };
        }

        private (int min, int max) DetermineOptimalHoldingPeriod(List<TakeProfitPerformanceRecord> successful)
        {
            if (!successful.Any()) return (1, 7);

            var periods = successful.Select(r => r.HoldingPeriodDays.Value).OrderBy(p => p).ToList();
            var q25 = periods[periods.Count / 4];
            var q75 = periods[(periods.Count * 3) / 4];

            return (q25, q75);
        }

        private List<PriceRangeInsight> AnalyzePriceRangeEffectiveness(List<TakeProfitPerformanceRecord> records)
        {
            var ranges = new[]
            {
                (0.0, 50.0, "Low Price (<$50)"),
                (50.0, 200.0, "Medium Price ($50-$200)"),
                (200.0, 500.0, "High Price ($200-$500)"),
                (500.0, double.MaxValue, "Very High Price (>$500)")
            };

            return ranges.Select(range =>
            {
                var recordsInRange = records
                    .Where(r => r.EntryPrice >= range.Item1 && r.EntryPrice < range.Item2)
                    .ToList();

                if (!recordsInRange.Any())
                    return null;

                // 🔄 CORRETTO: Usa enum values
                var successful = recordsInRange.Count(r => r.ActualResult == SimplifiedTakeProfitResult.Hit);

                return new PriceRangeInsight
                {
                    RangeName = range.Item3,
                    TotalSignals = recordsInRange.Count,
                    SuccessRate = (double)successful / recordsInRange.Count * 100,
                    AverageReturn = recordsInRange.Where(r => r.ActualReturn.HasValue).Average(r => r.ActualReturn.Value)
                };
            }).Where(insight => insight != null).ToList();
        }

        private List<string> IdentifySuccessPatterns(List<TakeProfitPerformanceRecord> records)
        {
            var patterns = new List<string>();
            // 🔄 CORRETTO: Usa enum values
            var successful = records.Where(r => r.ActualResult == SimplifiedTakeProfitResult.Hit).ToList();

            if (!successful.Any()) return patterns;

            // Pattern 1: Strategia più efficace
            var bestStrategy = successful
                .GroupBy(r => r.TakeProfitStrategy)
                .OrderByDescending(g => g.Count())
                .First();

            patterns.Add($"Most successful strategy: {bestStrategy.Key} ({bestStrategy.Count()} wins)");

            // Pattern 2: Range di confidence ottimale
            var avgSuccessfulConfidence = successful.Average(r => r.OriginalConfidence);
            patterns.Add($"Optimal confidence range: {avgSuccessfulConfidence:F1}% average");

            // Pattern 3: Pattern temporali
            var avgSuccessfulDays = successful.Where(r => r.HoldingPeriodDays.HasValue).Average(r => r.HoldingPeriodDays.Value);
            patterns.Add($"Successful trades average holding period: {avgSuccessfulDays:F1} days");

            // Pattern 4: Range di take profit efficaci
            var avgSuccessfulTP = successful.Average(r => r.TakeProfitPercentage);
            patterns.Add($"Most effective take profit range: {avgSuccessfulTP:F1}% average");

            return patterns;
        }

        private List<string> GenerateStrategicRecommendations(TakeProfitInsights insights)
        {
            var recommendations = new List<string>();

            // Raccomandazione basata su confidence
            var bestConfidenceRange = insights.ConfidenceInsights
                .OrderByDescending(c => c.SuccessRate)
                .FirstOrDefault();

            if (bestConfidenceRange != null)
            {
                recommendations.Add($"Focus on signals with {bestConfidenceRange.MinConfidence:F0}%-{bestConfidenceRange.MaxConfidence:F0}% confidence " +
                                  $"({bestConfidenceRange.SuccessRate:F1}% success rate)");
            }

            // Raccomandazione su timing
            if (insights.TimingInsights.OptimalHoldingPeriodRange.min > 0)
            {
                recommendations.Add($"Optimal holding period: {insights.TimingInsights.OptimalHoldingPeriodRange.min}-" +
                                  $"{insights.TimingInsights.OptimalHoldingPeriodRange.max} days");
            }

            // Raccomandazione su strategie
            var bestSignalType = insights.SignalTypeInsights
                .OrderByDescending(s => s.SuccessRate)
                .FirstOrDefault();

            if (bestSignalType != null)
            {
                recommendations.Add($"Best performing signal type: {bestSignalType.SignalType} with {bestSignalType.BestStrategy} strategy");
            }

            return recommendations;
        }
    }
}