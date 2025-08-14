using MongoDB.Driver;
using Newtonsoft.Json.Linq;
using PortfolioSignalWorker.Models;

namespace PortfolioSignalWorker.Services
{
    public class DataDrivenTakeProfitService
    {
        private readonly IMongoCollection<StockIndicator> _indicatorCollection;
        private readonly IMongoCollection<TradingSignal> _signalCollection;
        private readonly YahooFinanceService _yahooFinance;
        private readonly ILogger<DataDrivenTakeProfitService> _logger;

        public DataDrivenTakeProfitService(
            IMongoDatabase database,
            YahooFinanceService yahooFinance,
            ILogger<DataDrivenTakeProfitService> logger)
        {
            _indicatorCollection = database.GetCollection<StockIndicator>("Indicators");
            _signalCollection = database.GetCollection<TradingSignal>("TradingSignals");
            _yahooFinance = yahooFinance;
            _logger = logger;
        }

        public async Task<DataDrivenTakeProfitResult> CalculateOptimalTakeProfit(
            string symbol,
            double currentPrice,
            double stopLoss,
            SignalType signalType,
            double confidence)
        {
            try
            {
                _logger.LogInformation($"Calculating data-driven take profit for {symbol} at ${currentPrice:F2}");

                // 1. Analisi storica dei movimenti del prezzo
                var historicalAnalysis = await AnalyzeHistoricalMovements(symbol, 60); // 60 giorni

                // 2. Analisi della volatilità e dei pattern
                var volatilityAnalysis = await AnalyzeVolatilityPatterns(symbol, currentPrice);

                // 3. Analisi dei livelli tecnici (supporti/resistenze)
                var technicalLevels = await AnalyzeTechnicalLevels(symbol, currentPrice);

                // 4. Analisi della performance storica dei segnali simili
                var signalPerformance = await AnalyzeSignalPerformance(symbol, signalType, confidence);

                // 5. Calcola multiple opzioni di Take Profit
                var takeProfitOptions = CalculateTakeProfitScenarios(
                    currentPrice,
                    stopLoss,
                    historicalAnalysis,
                    volatilityAnalysis,
                    technicalLevels,
                    signalPerformance,
                    confidence);

                // 6. Seleziona l'opzione ottimale
                var optimalTakeProfit = SelectOptimalTakeProfit(takeProfitOptions, confidence);

                _logger.LogInformation($"✅ Data-driven TP for {symbol}: ${optimalTakeProfit.Price:F2} " +
                    $"({optimalTakeProfit.Percentage:F1}%) - Strategy: {optimalTakeProfit.Strategy}");

                return new DataDrivenTakeProfitResult
                {
                    Symbol = symbol,
                    CurrentPrice = currentPrice,
                    OptimalTakeProfit = optimalTakeProfit,
                    AlternativeTargets = takeProfitOptions.Where(tp => tp != optimalTakeProfit).ToList(),
                    HistoricalAnalysis = historicalAnalysis,
                    VolatilityAnalysis = volatilityAnalysis,
                    TechnicalLevels = technicalLevels,
                    SignalPerformanceAnalysis = signalPerformance,
                    CalculatedAt = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating data-driven take profit for {symbol}", symbol);

                // Fallback to confidence-based calculation
                return CreateFallbackTakeProfit(symbol, currentPrice, stopLoss, confidence);
            }
        }

        private async Task<HistoricalMovementAnalysis> AnalyzeHistoricalMovements(string symbol, int days)
        {
            try
            {
                var historicalData = await _yahooFinance.GetHistoricalDataAsync(symbol, days);
                var closes = historicalData["c"]?.ToObject<List<double>>() ?? new List<double>();
                var highs = historicalData["h"]?.ToObject<List<double>>() ?? new List<double>();
                var lows = historicalData["l"]?.ToObject<List<double>>() ?? new List<double>();

                if (closes.Count < 30)
                {
                    return new HistoricalMovementAnalysis { IsDataSufficient = false };
                }

                var dailyMoves = new List<double>();
                var upMoves = new List<double>();
                var downMoves = new List<double>();

                // Calcola movimenti giornalieri
                for (int i = 1; i < closes.Count; i++)
                {
                    var movePercent = ((closes[i] - closes[i - 1]) / closes[i - 1]) * 100;
                    dailyMoves.Add(Math.Abs(movePercent));

                    if (movePercent > 0)
                        upMoves.Add(movePercent);
                    else if (movePercent < 0)
                        downMoves.Add(Math.Abs(movePercent));
                }

                // Calcola range intraday
                var intradayRanges = new List<double>();
                for (int i = 0; i < Math.Min(highs.Count, lows.Count); i++)
                {
                    if (closes[i] > 0)
                    {
                        var range = ((highs[i] - lows[i]) / closes[i]) * 100;
                        intradayRanges.Add(range);
                    }
                }

                // Analizza trend multi-day
                var multiDayMoves = CalculateMultiDayMoves(closes);

                return new HistoricalMovementAnalysis
                {
                    IsDataSufficient = true,
                    AverageDailyMove = dailyMoves.Average(),
                    MedianDailyMove = CalculateMedian(dailyMoves),
                    Percentile75Move = CalculatePercentile(dailyMoves, 75),
                    Percentile90Move = CalculatePercentile(dailyMoves, 90),
                    AverageUpMove = upMoves.Any() ? upMoves.Average() : 0,
                    AverageDownMove = downMoves.Any() ? downMoves.Average() : 0,
                    AverageIntradayRange = intradayRanges.Any() ? intradayRanges.Average() : 0,
                    MaxIntradayRange = intradayRanges.Any() ? intradayRanges.Max() : 0,
                    TypicalMultiDayMove = multiDayMoves.Average(),
                    LargeMultiDayMove = CalculatePercentile(multiDayMoves, 75),
                    BullishBias = upMoves.Count > downMoves.Count,
                    ConsecutiveUpDays = CalculateMaxConsecutiveUpDays(closes),
                    DataPoints = closes.Count
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error analyzing historical movements for {symbol}", symbol);
                return new HistoricalMovementAnalysis { IsDataSufficient = false };
            }
        }

        private List<double> CalculateMultiDayMoves(List<double> closes)
        {
            var multiDayMoves = new List<double>();

            // Calcola movimenti su 3, 5, 10 giorni
            var periods = new[] { 3, 5, 10 };

            foreach (var period in periods)
            {
                for (int i = period; i < closes.Count; i++)
                {
                    var move = ((closes[i] - closes[i - period]) / closes[i - period]) * 100;
                    multiDayMoves.Add(Math.Abs(move));
                }
            }

            return multiDayMoves;
        }

        private int CalculateMaxConsecutiveUpDays(List<double> closes)
        {
            int maxConsecutive = 0;
            int currentConsecutive = 0;

            for (int i = 1; i < closes.Count; i++)
            {
                if (closes[i] > closes[i - 1])
                {
                    currentConsecutive++;
                    maxConsecutive = Math.Max(maxConsecutive, currentConsecutive);
                }
                else
                {
                    currentConsecutive = 0;
                }
            }

            return maxConsecutive;
        }

        private async Task<VolatilityAnalysis> AnalyzeVolatilityPatterns(string symbol, double currentPrice)
        {
            try
            {
                // Ottieni indicatori recenti per volatilità
                var recentIndicators = await _indicatorCollection
                    .Find(Builders<StockIndicator>.Filter.Eq(x => x.Symbol, symbol))
                    .SortByDescending(x => x.CreatedAt)
                    .Limit(30)
                    .ToListAsync();

                if (recentIndicators.Count < 10)
                {
                    return new VolatilityAnalysis
                    {
                        IsDataSufficient = false,
                        RecommendedTakeProfitRange = (8.0, 12.0) // Fallback conservativo
                    };
                }

                // Calcola volatilità RSI
                var rsiValues = recentIndicators.Select(x => x.RSI).ToList();
                var rsiVolatility = CalculateStandardDeviation(rsiValues);

                // Calcola volatilità prezzi
                var priceChanges = new List<double>();
                for (int i = 1; i < recentIndicators.Count; i++)
                {
                    var change = Math.Abs((recentIndicators[i - 1].Price - recentIndicators[i].Price) / recentIndicators[i].Price) * 100;
                    priceChanges.Add(change);
                }

                var priceVolatility = priceChanges.Any() ? priceChanges.Average() : 2.0;

                // Determina regime di volatilità
                var volatilityRegime = DetermineVolatilityRegime(priceVolatility, rsiVolatility);

                // Calcola range raccomandato basato su volatilità
                var (minTP, maxTP) = CalculateTakeProfitRangeFromVolatility(priceVolatility, volatilityRegime);

                return new VolatilityAnalysis
                {
                    IsDataSufficient = true,
                    AveragePriceVolatility = priceVolatility,
                    RSIVolatility = rsiVolatility,
                    VolatilityRegime = volatilityRegime,
                    RecommendedTakeProfitRange = (minTP, maxTP),
                    IsHighVolatilityPeriod = priceVolatility > 4.0,
                    DataPoints = recentIndicators.Count
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error analyzing volatility for {symbol}", symbol);
                return new VolatilityAnalysis
                {
                    IsDataSufficient = false,
                    RecommendedTakeProfitRange = (8.0, 12.0)
                };
            }
        }

        private VolatilityRegime DetermineVolatilityRegime(double priceVolatility, double rsiVolatility)
        {
            return (priceVolatility, rsiVolatility) switch
            {
                ( < 2.0, < 8.0) => VolatilityRegime.Low,
                ( < 4.0, < 12.0) => VolatilityRegime.Normal,
                ( < 7.0, < 20.0) => VolatilityRegime.High,
                _ => VolatilityRegime.Extreme
            };
        }

        private (double min, double max) CalculateTakeProfitRangeFromVolatility(double priceVolatility, VolatilityRegime regime)
        {
            return regime switch
            {
                VolatilityRegime.Low => (4.0, 8.0),      // Bassa volatilità = target conservativi
                VolatilityRegime.Normal => (6.0, 12.0),  // Normale = range standard
                VolatilityRegime.High => (8.0, 18.0),    // Alta volatilità = target più ambiziosi
                VolatilityRegime.Extreme => (10.0, 25.0), // Estrema = target molto ambiziosi
                _ => (6.0, 12.0)
            };
        }

        private async Task<TechnicalLevelsAnalysis> AnalyzeTechnicalLevels(string symbol, double currentPrice)
        {
            try
            {
                // Ottieni dati storici per calcoli tecnici
                var historicalData = await _yahooFinance.GetHistoricalDataAsync(symbol, 90);
                var highs = historicalData["h"]?.ToObject<List<double>>() ?? new List<double>();
                var lows = historicalData["l"]?.ToObject<List<double>>() ?? new List<double>();
                var closes = historicalData["c"]?.ToObject<List<double>>() ?? new List<double>();
                var volumes = historicalData["v"]?.ToObject<List<long>>() ?? new List<long>();

                if (closes.Count < 30)
                {
                    return new TechnicalLevelsAnalysis { IsDataSufficient = false };
                }

                // Calcola supporti e resistenze multiple
                var resistanceLevels = FindResistanceLevels(highs, closes, currentPrice);
                var supportLevels = FindSupportLevels(lows, closes, currentPrice);

                // Calcola livelli di volume profile
                var volumeProfile = CalculateVolumeProfile(closes, volumes, currentPrice);

                // Calcola livelli pivot
                var pivotLevels = CalculatePivotLevels(highs.Take(20).ToList(), lows.Take(20).ToList(), closes.Take(20).ToList());

                // Trova il miglior target di resistenza
                var primaryResistance = SelectPrimaryResistanceTarget(resistanceLevels, currentPrice);
                var secondaryResistance = SelectSecondaryResistanceTarget(resistanceLevels, primaryResistance, currentPrice);

                return new TechnicalLevelsAnalysis
                {
                    IsDataSufficient = true,
                    PrimaryResistance = primaryResistance,
                    SecondaryResistance = secondaryResistance,
                    ResistanceLevels = resistanceLevels.Take(5).ToList(),
                    SupportLevels = supportLevels.Take(3).ToList(),
                    VolumeWeightedResistance = volumeProfile.resistance,
                    PivotResistance = pivotLevels.resistance,
                    StrengthScore = CalculateResistanceStrength(resistanceLevels, currentPrice),
                    RecommendedTarget = primaryResistance?.Level ?? (currentPrice * 1.10)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error analyzing technical levels for {symbol}", symbol);
                return new TechnicalLevelsAnalysis
                {
                    IsDataSufficient = false,
                    RecommendedTarget = currentPrice * 1.10
                };
            }
        }

        private List<TakeProfitOption> CalculateTakeProfitScenarios(
            double currentPrice,
            double stopLoss,
            HistoricalMovementAnalysis historical,
            VolatilityAnalysis volatility,
            TechnicalLevelsAnalysis technical,
            SignalPerformanceAnalysis performance,
            double confidence)
        {
            var options = new List<TakeProfitOption>();
            var risk = currentPrice - stopLoss;

            // 1. CONSERVATIVE: Basato su movimenti medi giornalieri
            if (historical.IsDataSufficient)
            {
                var conservativePercent = Math.Min(historical.AverageDailyMove * 1.5, 8.0);
                options.Add(new TakeProfitOption
                {
                    Price = currentPrice * (1 + conservativePercent / 100),
                    Percentage = conservativePercent,
                    RiskRewardRatio = (currentPrice * conservativePercent / 100) / risk,
                    Strategy = "Conservative (Avg Daily Move)",
                    ProbabilityOfSuccess = 70.0,
                    DataSource = "Historical Daily Movements"
                });
            }

            // 2. VOLATILITY-BASED: Basato su regime di volatilità
            if (volatility.IsDataSufficient)
            {
                var volBasedPercent = (volatility.RecommendedTakeProfitRange.min + volatility.RecommendedTakeProfitRange.max) / 2;
                options.Add(new TakeProfitOption
                {
                    Price = currentPrice * (1 + volBasedPercent / 100),
                    Percentage = volBasedPercent,
                    RiskRewardRatio = (currentPrice * volBasedPercent / 100) / risk,
                    Strategy = $"Volatility-Based ({volatility.VolatilityRegime})",
                    ProbabilityOfSuccess = volatility.VolatilityRegime switch
                    {
                        VolatilityRegime.Low => 75.0,
                        VolatilityRegime.Normal => 65.0,
                        VolatilityRegime.High => 55.0,
                        VolatilityRegime.Extreme => 45.0,
                        _ => 60.0
                    },
                    DataSource = "Volatility Analysis"
                });
            }

            // 3. TECHNICAL RESISTANCE: Basato su resistenze tecniche
            if (technical.IsDataSufficient && technical.PrimaryResistance != null)
            {
                var resistancePercent = ((technical.PrimaryResistance.Level - currentPrice) / currentPrice) * 100;
                if (resistancePercent > 2.0 && resistancePercent < 30.0) // Ragionevole
                {
                    options.Add(new TakeProfitOption
                    {
                        Price = technical.PrimaryResistance.Level,
                        Percentage = resistancePercent,
                        RiskRewardRatio = (technical.PrimaryResistance.Level - currentPrice) / risk,
                        Strategy = "Technical Resistance",
                        ProbabilityOfSuccess = Math.Min(85.0 - (resistancePercent * 2), 75.0),
                        DataSource = "Technical Analysis"
                    });
                }
            }

            // 4. HISTORICAL PERFORMANCE: Basato su segnali simili passati
            if (performance.IsDataSufficient)
            {
                var perfPercent = performance.AverageSuccessfulReturn;
                options.Add(new TakeProfitOption
                {
                    Price = currentPrice * (1 + perfPercent / 100),
                    Percentage = perfPercent,
                    RiskRewardRatio = (currentPrice * perfPercent / 100) / risk,
                    Strategy = "Historical Performance",
                    ProbabilityOfSuccess = performance.SuccessRate,
                    DataSource = "Signal Performance History"
                });
            }

            // 5. CONFIDENCE-ADJUSTED: Basato su confidence del segnale
            var confidenceMultiplier = confidence switch
            {
                >= 90 => 2.0,
                >= 80 => 1.5,
                >= 70 => 1.2,
                _ => 1.0
            };

            var confidencePercent = Math.Min(12.0 * confidenceMultiplier, 25.0);
            options.Add(new TakeProfitOption
            {
                Price = currentPrice * (1 + confidencePercent / 100),
                Percentage = confidencePercent,
                RiskRewardRatio = (currentPrice * confidencePercent / 100) / risk,
                Strategy = $"Confidence-Adjusted ({confidence:F0}%)",
                ProbabilityOfSuccess = confidence * 0.8, // Leggermente conservativo
                DataSource = "Signal Confidence"
            });

            // 6. MULTI-DAY TARGET: Basato su movimenti multi-giorno
            if (historical.IsDataSufficient)
            {
                var multiDayPercent = Math.Min(historical.TypicalMultiDayMove, 20.0);
                options.Add(new TakeProfitOption
                {
                    Price = currentPrice * (1 + multiDayPercent / 100),
                    Percentage = multiDayPercent,
                    RiskRewardRatio = (currentPrice * multiDayPercent / 100) / risk,
                    Strategy = "Multi-Day Target",
                    ProbabilityOfSuccess = 50.0,
                    DataSource = "Multi-Day Movement Analysis"
                });
            }

            // Filtra opzioni irragionevoli
            return options
                .Where(o => o.Percentage >= 3.0 && o.Percentage <= 30.0)
                .Where(o => o.RiskRewardRatio >= 1.0)
                .OrderByDescending(o => o.ProbabilityOfSuccess)
                .ToList();
        }

        private TakeProfitOption SelectOptimalTakeProfit(List<TakeProfitOption> options, double confidence)
        {
            if (!options.Any())
            {
                // Fallback estremo
                return new TakeProfitOption
                {
                    Percentage = 10.0,
                    Strategy = "Fallback Default",
                    ProbabilityOfSuccess = 60.0,
                    DataSource = "Default"
                };
            }

            // Scoring algorithm che considera:
            // 1. Probabilità di successo (40%)
            // 2. Risk/Reward ratio (30%)
            // 3. Ragionevolezza del target (20%)
            // 4. Confidence del segnale originale (10%)

            var scoredOptions = options.Select(option => new
            {
                Option = option,
                Score = CalculateOptionScore(option, confidence)
            }).OrderByDescending(x => x.Score);

            var best = scoredOptions.First().Option;

            _logger.LogInformation($"Selected take profit strategy: {best.Strategy} " +
                $"({best.Percentage:F1}% target, {best.ProbabilityOfSuccess:F0}% probability)");

            return best;
        }

        private double CalculateOptionScore(TakeProfitOption option, double confidence)
        {
            var probabilityScore = option.ProbabilityOfSuccess * 0.4;
            var riskRewardScore = Math.Min(option.RiskRewardRatio * 20, 100) * 0.3;
            var reasonablenessScore = CalculateReasonablenessScore(option.Percentage) * 0.2;
            var confidenceScore = (confidence / 100.0) * 100 * 0.1;

            return probabilityScore + riskRewardScore + reasonablenessScore + confidenceScore;
        }

        private double CalculateReasonablenessScore(double percentage)
        {
            // Preferisce target tra 6-15%
            return percentage switch
            {
                >= 6.0 and <= 15.0 => 100.0,
                >= 4.0 and < 6.0 => 80.0,
                > 15.0 and <= 20.0 => 70.0,
                >= 3.0 and < 4.0 => 60.0,
                > 20.0 and <= 25.0 => 50.0,
                _ => 30.0
            };
        }

        // ... Helper methods per calcoli statistici e tecnici
        private double CalculateMedian(List<double> values)
        {
            var sorted = values.OrderBy(x => x).ToList();
            int count = sorted.Count;
            return count % 2 == 0
                ? (sorted[count / 2 - 1] + sorted[count / 2]) / 2.0
                : sorted[count / 2];
        }

        private double CalculatePercentile(List<double> values, int percentile)
        {
            var sorted = values.OrderBy(x => x).ToList();
            int index = (int)Math.Ceiling((percentile / 100.0) * sorted.Count) - 1;
            return sorted[Math.Min(Math.Max(index, 0), sorted.Count - 1)];
        }

        private double CalculateStandardDeviation(List<double> values)
        {
            if (values.Count < 2) return 0;

            var mean = values.Average();
            var squaredDifferences = values.Select(x => Math.Pow(x - mean, 2));
            return Math.Sqrt(squaredDifferences.Average());
        }

        // Placeholder methods - implementare secondo necessità
        private List<ResistanceLevel> FindResistanceLevels(List<double> highs, List<double> closes, double currentPrice)
        {
            // Implementazione semplificata
            return new List<ResistanceLevel>();
        }

        private List<SupportLevel> FindSupportLevels(List<double> lows, List<double> closes, double currentPrice)
        {
            return new List<SupportLevel>();
        }

        private (double resistance, double support) CalculateVolumeProfile(List<double> closes, List<long> volumes, double currentPrice)
        {
            return (currentPrice * 1.05, currentPrice * 0.95);
        }

        private (double resistance, double support) CalculatePivotLevels(List<double> highs, List<double> lows, List<double> closes)
        {
            return (highs.Max() * 0.98, lows.Min() * 1.02);
        }

        private ResistanceLevel SelectPrimaryResistanceTarget(List<ResistanceLevel> levels, double currentPrice)
        {
            return levels.FirstOrDefault();
        }

        private ResistanceLevel SelectSecondaryResistanceTarget(List<ResistanceLevel> levels, ResistanceLevel primary, double currentPrice)
        {
            return levels.Skip(1).FirstOrDefault();
        }

        private double CalculateResistanceStrength(List<ResistanceLevel> levels, double currentPrice)
        {
            return 0.7;
        }

        private async Task<SignalPerformanceAnalysis> AnalyzeSignalPerformance(string symbol, SignalType signalType, double confidence)
        {
            // Implementazione semplificata - placeholder
            return new SignalPerformanceAnalysis
            {
                IsDataSufficient = false,
                AverageSuccessfulReturn = 12.0,
                SuccessRate = 65.0
            };
        }

        private DataDrivenTakeProfitResult CreateFallbackTakeProfit(string symbol, double currentPrice, double stopLoss, double confidence)
        {
            var fallbackPercentage = confidence switch
            {
                >= 90 => 18.0,
                >= 80 => 14.0,
                >= 70 => 11.0,
                _ => 8.0
            };

            return new DataDrivenTakeProfitResult
            {
                Symbol = symbol,
                CurrentPrice = currentPrice,
                OptimalTakeProfit = new TakeProfitOption
                {
                    Price = currentPrice * (1 + fallbackPercentage / 100),
                    Percentage = fallbackPercentage,
                    Strategy = "Confidence-Based Fallback",
                    ProbabilityOfSuccess = confidence * 0.8,
                    DataSource = "Fallback"
                },
                IsFallback = true,
                CalculatedAt = DateTime.UtcNow
            };
        }
    }
}