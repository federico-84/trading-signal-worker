using MongoDB.Driver;
using PortfolioSignalWorker.Models;

namespace PortfolioSignalWorker.Services
{
    public class SimplifiedEnhancedRiskManagementService
    {
        private readonly IMongoCollection<StockIndicator> _indicatorCollection;
        private readonly YahooFinanceService _yahooFinance;
        private readonly ILogger<SimplifiedEnhancedRiskManagementService> _logger;
        private readonly EnhancedRiskSettings _settings;

        public SimplifiedEnhancedRiskManagementService(
            IMongoDatabase database,
            YahooFinanceService yahooFinance,
            ILogger<SimplifiedEnhancedRiskManagementService> logger,
            IConfiguration config)
        {
            _indicatorCollection = database.GetCollection<StockIndicator>("Indicators");
            _yahooFinance = yahooFinance;
            _logger = logger;

            // Load settings from config
            _settings = new EnhancedRiskSettings
            {
                DefaultStopLossPercent = config.GetValue<double>("Risk:DefaultStopLossPercent", 5.0),
                DefaultTakeProfitPercent = config.GetValue<double>("Risk:DefaultTakeProfitPercent", 15.0),
                MinRiskRewardRatio = config.GetValue<double>("Risk:MinRiskRewardRatio", 2.5),
                UseATRForStopLoss = config.GetValue<bool>("Risk:UseATRForStopLoss", true),
                ATRMultiplierLow = config.GetValue<double>("Risk:ATRMultiplierLow", 2.0),
                ATRMultiplierNormal = config.GetValue<double>("Risk:ATRMultiplierNormal", 2.5),
                ATRMultiplierHigh = config.GetValue<double>("Risk:ATRMultiplierHigh", 3.0),
                ATRMultiplierExtreme = config.GetValue<double>("Risk:ATRMultiplierExtreme", 4.0)
            };
        }

        public async Task<TradingSignal> EnhanceSignalWithSmartRiskManagement(TradingSignal signal)
        {
            try
            {
                _logger.LogInformation($"🎯 Calculating smart risk management for {signal.Symbol} at ${signal.Price}");

                // 1. Analisi volatilità (ATR)
                var atrData = await CalculateATRAnalysis(signal.Symbol);
                signal.ATR = atrData.atr;

                // 2. Analisi supporti e resistenze strutturali
                var levels = await CalculateStructuralLevels(signal.Symbol, signal.Price);
                signal.SupportLevel = levels.support;
                signal.ResistanceLevel = levels.resistance;

                // 3. Calcolo stop loss intelligente
                var stopLossResult = CalculateIntelligentStopLoss(signal.Price, atrData, levels, signal.Confidence);
                signal.StopLoss = stopLossResult.price;
                signal.StopLossPercent = stopLossResult.percent;

                // 4. Calcolo take profit ottimizzato
                var takeProfitResult = CalculateOptimizedTakeProfit(signal.Price, stopLossResult, levels, signal.Confidence);
                signal.TakeProfit = takeProfitResult.price;
                signal.TakeProfitPercent = takeProfitResult.percent;

                // 5. Risk/Reward ratio
                var risk = signal.Price - signal.StopLoss.Value;
                var reward = signal.TakeProfit.Value - signal.Price;
                signal.RiskRewardRatio = risk > 0 ? reward / risk : 0;

                // 6. Assicurati che R/R sia accettabile
                if (signal.RiskRewardRatio < _settings.MinRiskRewardRatio)
                {
                    // Aggiusta take profit per raggiungere R/R minimo
                    signal.TakeProfit = signal.Price + (risk * _settings.MinRiskRewardRatio);
                    signal.TakeProfitPercent = ((signal.TakeProfit.Value - signal.Price) / signal.Price) * 100;
                    signal.RiskRewardRatio = _settings.MinRiskRewardRatio;

                    _logger.LogInformation($"⚖️ Adjusted take profit for {signal.Symbol} to meet R/R ratio: ${signal.TakeProfit:F2}");
                }

                // 7. Genera strategie entry/exit
                signal.EntryStrategy = GenerateSmartEntryStrategy(signal, atrData, levels);
                signal.ExitStrategy = GenerateSmartExitStrategy(signal, atrData, levels);

                // 8. Contesto di mercato avanzato
                await EnhanceWithAdvancedMarketContext(signal);

                _logger.LogInformation($"✅ Smart risk management for {signal.Symbol}: " +
                    $"SL: ${signal.StopLoss:F2} ({signal.StopLossPercent:F1}%), " +
                    $"TP: ${signal.TakeProfit:F2} ({signal.TakeProfitPercent:F1}%), " +
                    $"R/R: 1:{signal.RiskRewardRatio:F1}");

                return signal;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in smart risk management for {symbol}", signal.Symbol);
                ApplyFallbackRiskLevels(signal);
                return signal;
            }
        }

        private async Task<ATRAnalysis> CalculateATRAnalysis(string symbol)
        {
            try
            {
                var historicalData = await _yahooFinance.GetHistoricalDataAsync(symbol, 25);
                var highs = historicalData["h"]?.ToObject<List<double>>() ?? new List<double>();
                var lows = historicalData["l"]?.ToObject<List<double>>() ?? new List<double>();
                var closes = historicalData["c"]?.ToObject<List<double>>() ?? new List<double>();

                if (highs.Count < 14 || lows.Count < 14 || closes.Count < 14)
                {
                    return new ATRAnalysis { atr = 0, volatilityClass = "UNKNOWN", normalizedATR = 0 };
                }

                var trueRanges = new List<double>();
                for (int i = 1; i < Math.Min(highs.Count, Math.Min(lows.Count, closes.Count)); i++)
                {
                    var tr1 = highs[i] - lows[i];
                    var tr2 = Math.Abs(highs[i] - closes[i - 1]);
                    var tr3 = Math.Abs(lows[i] - closes[i - 1]);
                    trueRanges.Add(Math.Max(tr1, Math.Max(tr2, tr3)));
                }

                var atr = trueRanges.Take(14).Average();
                var currentPrice = closes.First();
                var normalizedATR = (atr / currentPrice) * 100;

                var volatilityClass = normalizedATR switch
                {
                    < 1.0 => "LOW",
                    < 2.5 => "NORMAL",
                    < 4.0 => "HIGH",
                    _ => "EXTREME"
                };

                return new ATRAnalysis
                {
                    atr = atr,
                    volatilityClass = volatilityClass,
                    normalizedATR = normalizedATR
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error calculating ATR for {symbol}: {ex.Message}");
                return new ATRAnalysis { atr = 0, volatilityClass = "UNKNOWN", normalizedATR = 0 };
            }
        }

        private async Task<StructuralLevels> CalculateStructuralLevels(string symbol, double currentPrice)
        {
            try
            {
                var historicalData = await _yahooFinance.GetHistoricalDataAsync(symbol, 60);
                var highs = historicalData["h"]?.ToObject<List<double>>() ?? new List<double>();
                var lows = historicalData["l"]?.ToObject<List<double>>() ?? new List<double>();

                if (highs.Count < 30 || lows.Count < 30)
                {
                    return new StructuralLevels
                    {
                        support = currentPrice * 0.95,
                        resistance = currentPrice * 1.05
                    };
                }

                // Trova swing lows (supporti potenziali)
                var swingLows = FindSwingLows(lows, currentPrice);
                var swingHighs = FindSwingHighs(highs, currentPrice);

                // Trova il supporto più forte (più vicino ma sotto il prezzo corrente)
                var support = swingLows
                    .Where(level => level < currentPrice * 0.98)
                    .OrderByDescending(level => level)
                    .FirstOrDefault();

                // Trova la resistenza più forte (più vicina ma sopra il prezzo corrente)
                var resistance = swingHighs
                    .Where(level => level > currentPrice * 1.02)
                    .OrderBy(level => level)
                    .FirstOrDefault();

                // Fallback se non troviamo livelli validi
                if (support == 0) support = currentPrice * 0.95;
                if (resistance == 0) resistance = currentPrice * 1.05;

                _logger.LogDebug($"Structural levels for {symbol}: Support=${support:F2}, Resistance=${resistance:F2}");

                return new StructuralLevels { support = support, resistance = resistance };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating structural levels for {symbol}", symbol);
                return new StructuralLevels
                {
                    support = currentPrice * 0.95,
                    resistance = currentPrice * 1.05
                };
            }
        }

        private StopLossResult CalculateIntelligentStopLoss(
            double currentPrice,
            ATRAnalysis atrData,
            StructuralLevels levels,
            double confidence)
        {
            var stopOptions = new List<(double price, string method, double score)>();

            // 1. ATR-based stop (adattivo alla volatilità)
            if (atrData.atr > 0)
            {
                var atrMultiplier = atrData.volatilityClass switch
                {
                    "LOW" => _settings.ATRMultiplierLow,
                    "NORMAL" => _settings.ATRMultiplierNormal,
                    "HIGH" => _settings.ATRMultiplierHigh,
                    "EXTREME" => _settings.ATRMultiplierExtreme,
                    _ => _settings.ATRMultiplierNormal
                };

                var atrStop = currentPrice - (atrData.atr * atrMultiplier);
                var atrScore = 85 - (atrData.normalizedATR * 3); // Penalizza alta volatilità
                stopOptions.Add((atrStop, $"ATR ({atrData.volatilityClass})", Math.Max(50, atrScore)));
            }

            // 2. Structural support stop
            if (levels.support > 0 && levels.support < currentPrice)
            {
                var structuralStop = levels.support * 0.98; // 2% sotto il supporto
                var distanceFromSupport = ((currentPrice - levels.support) / currentPrice) * 100;
                var structuralScore = distanceFromSupport < 5 ? 95 : Math.Max(60, 95 - distanceFromSupport * 2);
                stopOptions.Add((structuralStop, "STRUCTURAL", structuralScore));
            }

            // 3. Confidence-based percentage stop
            var confidenceStopPercent = confidence switch
            {
                >= 90 => 3.0,  // Stop più stretto per alta confidence
                >= 80 => 4.0,
                >= 70 => 5.0,
                >= 60 => 6.0,
                _ => 7.0       // Stop più ampio per bassa confidence
            };

            var confidenceStop = currentPrice * (1 - confidenceStopPercent / 100);
            stopOptions.Add((confidenceStop, $"CONFIDENCE ({confidence:F0}%)", confidence));

            // 4. Seleziona il migliore
            var bestStop = stopOptions
                .Where(s => s.price < currentPrice && s.price > currentPrice * 0.85) // Sanity check
                .OrderByDescending(s => s.score)
                .ThenByDescending(s => s.price) // Preferisci stop più vicini a parità di score
                .FirstOrDefault();

            if (bestStop.price == 0)
            {
                bestStop = (currentPrice * 0.95, "FALLBACK", 50);
            }

            var stopPercent = ((currentPrice - bestStop.price) / currentPrice) * 100;

            _logger.LogDebug($"Stop loss for {currentPrice:F2}: ${bestStop.price:F2} ({stopPercent:F1}%) via {bestStop.method}");

            return new StopLossResult
            {
                price = bestStop.price,
                percent = stopPercent,
                method = bestStop.method
            };
        }

        private TakeProfitResult CalculateOptimizedTakeProfit(
            double currentPrice,
            StopLossResult stopLoss,
            StructuralLevels levels,
            double confidence)
        {
            var risk = currentPrice - stopLoss.price;
            var takeProfitOptions = new List<(double price, string method, double score)>();

            // 1. Risk/Reward based (enforcement di ratio minimo)
            var rrTarget = currentPrice + (risk * _settings.MinRiskRewardRatio);
            takeProfitOptions.Add((rrTarget, $"R/R ({_settings.MinRiskRewardRatio:F1}:1)", 80));

            // 2. Resistance-based
            if (levels.resistance > currentPrice)
            {
                var resistanceTarget = levels.resistance * 0.97; // 3% prima della resistenza per sicurezza
                var distanceToResistance = ((levels.resistance - currentPrice) / currentPrice) * 100;
                var resistanceScore = distanceToResistance > 8 ? 90 : Math.Max(60, distanceToResistance * 8);
                takeProfitOptions.Add((resistanceTarget, "RESISTANCE", resistanceScore));
            }

            // 3. Confidence-adjusted target
            var confidenceMultiplier = confidence switch
            {
                >= 90 => 3.5,  // Target più ambizioso per alta confidence
                >= 80 => 3.0,
                >= 70 => 2.8,
                >= 60 => 2.5,
                _ => 2.0
            };

            var confidenceTarget = currentPrice + (risk * confidenceMultiplier);
            takeProfitOptions.Add((confidenceTarget, $"CONFIDENCE ({confidence:F0}%)", confidence));

            // 4. Seleziona il miglior target
            var bestTarget = takeProfitOptions
                .Where(t => t.price > currentPrice && t.price <= currentPrice * 1.4) // Sanity check (max 40% gain)
                .OrderByDescending(t => t.score)
                .ThenBy(t => Math.Abs((t.price - currentPrice) / risk - _settings.MinRiskRewardRatio)) // Preferisci vicino al target R/R
                .FirstOrDefault();

            // Assicurati che rispetti il rapporto R/R minimo
            var minTarget = currentPrice + (risk * _settings.MinRiskRewardRatio);
            if (bestTarget.price < minTarget)
            {
                bestTarget = (minTarget, "MIN_RR_ENFORCED", 70);
            }

            var targetPercent = ((bestTarget.price - currentPrice) / currentPrice) * 100;

            _logger.LogDebug($"Take profit for {currentPrice:F2}: ${bestTarget.price:F2} ({targetPercent:F1}%) via {bestTarget.method}");

            return new TakeProfitResult
            {
                price = bestTarget.price,
                percent = targetPercent,
                method = bestTarget.method
            };
        }

        private string GenerateSmartEntryStrategy(TradingSignal signal, ATRAnalysis atrData, StructuralLevels levels)
        {
            var strategies = new List<string>();

            // Entry timing basato su confidence e volatilità
            if (signal.Confidence >= 85 && atrData.volatilityClass != "EXTREME")
            {
                strategies.Add("Market order - high confidence setup");
            }
            else
            {
                var limitPrice = signal.Price * 0.998; // 0.2% sotto il prezzo corrente
                strategies.Add($"Limit order at ${limitPrice:F2} - wait for slight pullback");
            }

            // Volume confirmation
            if (signal.VolumeStrength >= 7)
            {
                strategies.Add("Strong volume - enter immediately");
            }
            else
            {
                strategies.Add("Wait for volume confirmation (>1.5x avg)");
            }

            // Support proximity
            if (levels.support > 0 && signal.Price > levels.support * 1.02)
            {
                var supportDistance = ((signal.Price - levels.support) / levels.support) * 100;
                strategies.Add($"Near support ${levels.support:F2} ({supportDistance:F1}% above)");
            }

            // Volatility context
            if (atrData.volatilityClass == "HIGH" || atrData.volatilityClass == "EXTREME")
            {
                strategies.Add($"⚠️ {atrData.volatilityClass} volatility - reduce position size");
            }

            return string.Join(" | ", strategies);
        }

        private string GenerateSmartExitStrategy(TradingSignal signal, ATRAnalysis atrData, StructuralLevels levels)
        {
            var strategies = new List<string>();

            // Stop loss strategy
            strategies.Add($"🛑 Stop Loss: ${signal.StopLoss:F2} ({signal.StopLossPercent:F1}%)");

            // Take profit strategy
            strategies.Add($"🎯 Take Profit: ${signal.TakeProfit:F2} ({signal.TakeProfitPercent:F1}%)");

            // Trailing stop suggestion
            if (atrData.atr > 0)
            {
                var trailingDistance = atrData.atr * 1.5;
                strategies.Add($"📈 Trailing Stop: ${trailingDistance:F2} distance (1.5x ATR)");
            }

            // Partial profit taking per alta confidence
            if (signal.Confidence >= 90)
            {
                var partialTarget = signal.Price + ((signal.TakeProfit.Value - signal.Price) * 0.6);
                strategies.Add($"💰 Take 50% profit at ${partialTarget:F2}");
            }

            // Resistance warnings
            if (levels.resistance > 0 && levels.resistance < signal.TakeProfit.Value * 1.1)
            {
                strategies.Add($"⚠️ Watch resistance at ${levels.resistance:F2}");
            }

            return string.Join(" | ", strategies);
        }

        private async Task EnhanceWithAdvancedMarketContext(TradingSignal signal)
        {
            try
            {
                var recentIndicators = await _indicatorCollection
                    .Find(Builders<StockIndicator>.Filter.Eq(x => x.Symbol, signal.Symbol))
                    .SortByDescending(x => x.CreatedAt)
                    .Limit(15)
                    .ToListAsync();

                if (recentIndicators.Count >= 10)
                {
                    var avgRSI = recentIndicators.Take(10).Average(x => x.RSI);
                    var rsiTrend = recentIndicators.Take(5).Average(x => x.RSI) -
                                   recentIndicators.Skip(5).Take(5).Average(x => x.RSI);

                    // Enhanced market condition
                    signal.MarketCondition = (avgRSI, rsiTrend) switch
                    {
                        ( < 30, > 3) => "Oversold Recovery",
                        ( < 35, _) => "Oversold Zone",
                        ( > 70, < -3) => "Overbought Decline",
                        ( > 65, _) => "Overbought Zone",
                        (_, > 8) => "Strong Bullish Momentum",
                        (_, < -8) => "Strong Bearish Momentum",
                        _ => "Neutral Range"
                    };

                    // Enhanced volume strength (1-10 scale)
                    var avgVolume = recentIndicators.Take(10).Average(x => x.Volume);
                    var volumeRatio = avgVolume > 0 ? signal.Volume / avgVolume : 1;
                    signal.VolumeStrength = Math.Min(10, Math.Max(1, volumeRatio * 3));

                    // Enhanced trend strength
                    var priceChange = recentIndicators.Count >= 2 ?
                        ((recentIndicators[0].Price - recentIndicators.Last().Price) / recentIndicators.Last().Price) * 100 : 0;
                    signal.TrendStrength = Math.Min(10, Math.Max(1, (Math.Abs(priceChange) / 3) + 4));
                }
                else
                {
                    signal.MarketCondition = "Limited Data";
                    signal.VolumeStrength = 5;
                    signal.TrendStrength = 5;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error enhancing market context for {signal.Symbol}: {ex.Message}");
                signal.MarketCondition = "Unknown";
                signal.VolumeStrength = 5;
                signal.TrendStrength = 5;
            }
        }

        #region Helper Methods

        private List<double> FindSwingLows(List<double> lows, double currentPrice)
        {
            var swingLows = new List<double>();
            const int lookback = 3;

            for (int i = lookback; i < lows.Count - lookback; i++)
            {
                var isSwingLow = true;
                for (int j = i - lookback; j <= i + lookback; j++)
                {
                    if (j != i && lows[j] <= lows[i])
                    {
                        isSwingLow = false;
                        break;
                    }
                }

                if (isSwingLow && lows[i] < currentPrice * 0.99)
                {
                    swingLows.Add(lows[i]);
                }
            }

            return swingLows.Distinct().OrderByDescending(l => l).Take(3).ToList();
        }

        private List<double> FindSwingHighs(List<double> highs, double currentPrice)
        {
            var swingHighs = new List<double>();
            const int lookback = 3;

            for (int i = lookback; i < highs.Count - lookback; i++)
            {
                var isSwingHigh = true;
                for (int j = i - lookback; j <= i + lookback; j++)
                {
                    if (j != i && highs[j] >= highs[i])
                    {
                        isSwingHigh = false;
                        break;
                    }
                }

                if (isSwingHigh && highs[i] > currentPrice * 1.01)
                {
                    swingHighs.Add(highs[i]);
                }
            }

            return swingHighs.Distinct().OrderBy(h => h).Take(3).ToList();
        }

        private void ApplyFallbackRiskLevels(TradingSignal signal)
        {
            signal.StopLoss = signal.Price * (1 - _settings.DefaultStopLossPercent / 100);
            signal.TakeProfit = signal.Price * (1 + _settings.DefaultTakeProfitPercent / 100);
            signal.StopLossPercent = _settings.DefaultStopLossPercent;
            signal.TakeProfitPercent = _settings.DefaultTakeProfitPercent;
            signal.RiskRewardRatio = _settings.DefaultTakeProfitPercent / _settings.DefaultStopLossPercent;

            signal.EntryStrategy = "FALLBACK: Basic market order";
            signal.ExitStrategy = $"FALLBACK: SL ${signal.StopLoss:F2} | TP ${signal.TakeProfit:F2}";
        }

        #endregion
    }

    #region Data Models

    public class EnhancedRiskSettings
    {
        public double DefaultStopLossPercent { get; set; } = 5.0;
        public double DefaultTakeProfitPercent { get; set; } = 15.0;
        public double MinRiskRewardRatio { get; set; } = 2.5;
        public bool UseATRForStopLoss { get; set; } = true;

        public double ATRMultiplierLow { get; set; } = 2.0;
        public double ATRMultiplierNormal { get; set; } = 2.5;
        public double ATRMultiplierHigh { get; set; } = 3.0;
        public double ATRMultiplierExtreme { get; set; } = 4.0;
    }

    public class ATRAnalysis
    {
        public double atr { get; set; }
        public string volatilityClass { get; set; }
        public double normalizedATR { get; set; }
    }

    public class StructuralLevels
    {
        public double support { get; set; }
        public double resistance { get; set; }
    }

    public class StopLossResult
    {
        public double price { get; set; }
        public double percent { get; set; }
        public string method { get; set; }
    }

    public class TakeProfitResult
    {
        public double price { get; set; }
        public double percent { get; set; }
        public string method { get; set; }
    }

    #endregion
}