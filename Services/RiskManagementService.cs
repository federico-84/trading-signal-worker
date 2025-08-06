using MongoDB.Driver;
using PortfolioSignalWorker.Models;

namespace PortfolioSignalWorker.Services
{
    public class RiskManagementService
    {
        private readonly IMongoCollection<StockIndicator> _indicatorCollection;
        private readonly YahooFinanceService _yahooFinance;
        private readonly ILogger<RiskManagementService> _logger;
        private readonly RiskParameters _riskParams;

        public RiskManagementService(
            IMongoDatabase database,
            YahooFinanceService yahooFinance,
            ILogger<RiskManagementService> logger,
            IConfiguration config)
        {
            _indicatorCollection = database.GetCollection<StockIndicator>("Indicators");
            _yahooFinance = yahooFinance;
            _logger = logger;

            // Carica parametri di rischio dalla configurazione
            _riskParams = new RiskParameters
            {
                DefaultStopLossPercent = config.GetValue<double>("Risk:DefaultStopLossPercent", 5.0),
                DefaultTakeProfitPercent = config.GetValue<double>("Risk:DefaultTakeProfitPercent", 15.0),
                MaxPositionSizePercent = config.GetValue<double>("Risk:MaxPositionSizePercent", 5.0),
                PortfolioValue = config.GetValue<double>("Risk:PortfolioValue", 10000),
                MinRiskRewardRatio = config.GetValue<double>("Risk:MinRiskRewardRatio", 2.0),
                UseATRForStopLoss = config.GetValue<bool>("Risk:UseATRForStopLoss", true),
                ATRMultiplier = config.GetValue<double>("Risk:ATRMultiplier", 2.0)
            };
        }

        public async Task<TradingSignal> EnhanceSignalWithRiskManagement(TradingSignal signal)
        {
            try
            {
                _logger.LogInformation($"Calculating risk management for {signal.Symbol} at ${signal.Price}");

                // 1. Calcola ATR per volatilità
                var atr = await CalculateATR(signal.Symbol);
                signal.ATR = atr;

                // 2. Calcola supporti e resistenze
                var (support, resistance) = await CalculateSupportResistance(signal.Symbol);
                signal.SupportLevel = support;
                signal.ResistanceLevel = resistance;

                // 3. Calcola Stop Loss e Take Profit intelligenti
                var levels = CalculateRiskLevels(signal.Price, atr, support, resistance, signal.Confidence);

                signal.StopLoss = levels.StopLoss;
                signal.TakeProfit = levels.TakeProfit;
                signal.StopLossPercent = levels.StopLossPercent;
                signal.TakeProfitPercent = levels.TakeProfitPercent;
                signal.RiskRewardRatio = levels.RiskRewardRatio;

                // 4. Calcola position sizing
                var positionSizing = CalculatePositionSizing(signal.Price, levels.StopLoss);
                signal.SuggestedShares = positionSizing.shares;
                signal.PositionValue = positionSizing.value;
                signal.MaxRiskAmount = positionSizing.maxRisk;
                signal.PotentialGainAmount = positionSizing.potentialGain;
                signal.MaxPositionSize = _riskParams.MaxPositionSizePercent;

                // 5. Aggiungi contesto di mercato
                await EnhanceWithMarketContext(signal);

                // 6. Strategie di entrata/uscita
                signal.EntryStrategy = GenerateEntryStrategy(signal, levels);
                signal.ExitStrategy = GenerateExitStrategy(signal, levels);

                _logger.LogInformation($"✅ Risk management calculated for {signal.Symbol}: " +
                    $"SL: ${levels.StopLoss:F2} ({levels.StopLossPercent:F1}%), " +
                    $"TP: ${levels.TakeProfit:F2} ({levels.TakeProfitPercent:F1}%), " +
                    $"R/R: 1:{levels.RiskRewardRatio:F1}");

                return signal;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating risk management for {symbol}", signal.Symbol);

                // Fallback ai valori di default
                ApplyDefaultRiskLevels(signal);
                return signal;
            }
        }

        private async Task<double> CalculateATR(string symbol, int periods = 14)
        {
            try
            {
                // Ottieni dati storici per ATR
                var historicalData = await _yahooFinance.GetHistoricalDataAsync(symbol, periods + 5);

                var highs = historicalData["h"]?.ToObject<List<double>>() ?? new List<double>();
                var lows = historicalData["l"]?.ToObject<List<double>>() ?? new List<double>();
                var closes = historicalData["c"]?.ToObject<List<double>>() ?? new List<double>();

                if (highs.Count < periods || lows.Count < periods || closes.Count < periods)
                {
                    _logger.LogWarning($"Insufficient data for ATR calculation: {symbol}");
                    return 0;
                }

                var trueRanges = new List<double>();

                for (int i = 1; i < Math.Min(highs.Count, Math.Min(lows.Count, closes.Count)); i++)
                {
                    var high = highs[i];
                    var low = lows[i];
                    var prevClose = closes[i - 1];

                    var tr1 = high - low;
                    var tr2 = Math.Abs(high - prevClose);
                    var tr3 = Math.Abs(low - prevClose);

                    var trueRange = Math.Max(tr1, Math.Max(tr2, tr3));
                    trueRanges.Add(trueRange);
                }

                // ATR come media semplice delle True Ranges
                return trueRanges.Take(periods).Average();
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error calculating ATR for {symbol}: {ex.Message}");
                return 0;
            }
        }

        private async Task<(double support, double resistance)> CalculateSupportResistance(string symbol)
        {
            try
            {
                var historicalData = await _yahooFinance.GetHistoricalDataAsync(symbol, 30);
                var highs = historicalData["h"]?.ToObject<List<double>>() ?? new List<double>();
                var lows = historicalData["l"]?.ToObject<List<double>>() ?? new List<double>();
                var closes = historicalData["c"]?.ToObject<List<double>>() ?? new List<double>();

                if (highs.Count < 20 || lows.Count < 20)
                {
                    return (0, 0);
                }

                var currentPrice = closes.First(); // Prezzo più recente

                // CORREZIONE: Trova i livelli di supporto e resistenza REALI
                // Supporto = il massimo tra i minimi recenti che sia SOTTO il prezzo corrente
                var recentLows = lows.Take(20).ToList();
                var validSupports = recentLows.Where(low => low < currentPrice * 0.98).ToList(); // 2% buffer
                var support = validSupports.Any() ? validSupports.Max() : currentPrice * 0.95;

                // Resistenza = il minimo tra i massimi recenti che sia SOPRA il prezzo corrente  
                var recentHighs = highs.Take(20).ToList();
                var validResistances = recentHighs.Where(high => high > currentPrice * 1.02).ToList(); // 2% buffer
                var resistance = validResistances.Any() ? validResistances.Min() : currentPrice * 1.05;

                // VALIDAZIONE FINALE
                if (support >= currentPrice)
                {
                    support = currentPrice * 0.95; // 5% sotto il prezzo corrente
                    _logger.LogWarning($"Support adjusted to {support:F2} for {symbol} (was above current price)");
                }

                if (resistance <= currentPrice)
                {
                    resistance = currentPrice * 1.05; // 5% sopra il prezzo corrente  
                    _logger.LogWarning($"Resistance adjusted to {resistance:F2} for {symbol} (was below current price)");
                }

                _logger.LogDebug($"S/R for {symbol}: Support={support:F2}, Resistance={resistance:F2}, Current={currentPrice:F2}");

                return (support, resistance);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating S/R for {symbol}", symbol);
                return (0, 0);
            }
        }

        private LevelCalculationResult CalculateRiskLevels(
    double currentPrice,
    double atr,
    double support,
    double resistance,
    double confidence)
        {
            var result = new LevelCalculationResult();

            // METODO 1: Stop Loss basato su ATR (più dinamico)
            if (_riskParams.UseATRForStopLoss && atr > 0)
            {
                var atrStopDistance = atr * _riskParams.ATRMultiplier;
                var atrBasedStop = currentPrice - atrStopDistance;

                // 🔥 CORREZIONE CRITICA: Valida che support sia sotto il prezzo corrente
                if (support > 0 && support < currentPrice)
                {
                    // Support valido: usa il maggiore tra ATR stop e support
                    result.StopLoss = Math.Max(atrBasedStop, support * 0.98);
                    result.CalculationMethod = $"ATR-based (ATR={atr:F3}, multiplier={_riskParams.ATRMultiplier}) with support floor";
                }
                else
                {
                    // Support non valido: usa solo ATR
                    result.StopLoss = atrBasedStop;
                    result.CalculationMethod = $"ATR-based only (ATR={atr:F3}, multiplier={_riskParams.ATRMultiplier}) - invalid support";

                    if (support > currentPrice)
                    {
                        _logger.LogWarning($"Invalid support {support:F2} > price {currentPrice:F2} - ignoring support level");
                    }
                }

                // 🛡️ VALIDAZIONE FINALE: StopLoss non può mai essere >= currentPrice per BUY signal
                if (result.StopLoss >= currentPrice)
                {
                    _logger.LogError($"CRITICAL ERROR: StopLoss {result.StopLoss:F2} >= Price {currentPrice:F2} - forcing fallback");
                    result.StopLoss = currentPrice * 0.95; // 5% sotto come fallback sicuro
                    result.CalculationMethod = "FALLBACK: 5% below current price (invalid calculation detected)";
                }
            }
            else
            {
                // METODO 2: Stop Loss percentuale fisso
                var stopLossPercent = GetDynamicStopLossPercent(confidence);
                result.StopLoss = currentPrice * (1 - stopLossPercent / 100);
                result.CalculationMethod = $"Fixed percentage ({stopLossPercent:F1}%)";
            }

            // Take Profit intelligente basato su confidence e resistenza
            var takeProfitPercent = GetDynamicTakeProfitPercent(confidence);
            var calculatedTakeProfit = currentPrice * (1 + takeProfitPercent / 100);

            // 🔥 CORREZIONE: Valida che resistance sia sopra il prezzo corrente
            if (resistance > currentPrice && calculatedTakeProfit > resistance * 0.95)
            {
                result.TakeProfit = resistance * 0.95; // 5% sotto resistenza
                result.CalculationMethod += " + resistance-adjusted TP";
            }
            else
            {
                result.TakeProfit = calculatedTakeProfit;

                if (resistance > 0 && resistance <= currentPrice)
                {
                    _logger.LogWarning($"Invalid resistance {resistance:F2} <= price {currentPrice:F2} - ignoring resistance level");
                }
            }

            // 🛡️ VALIDAZIONE FINALE: TakeProfit deve essere > currentPrice per BUY signal
            if (result.TakeProfit <= currentPrice)
            {
                _logger.LogError($"CRITICAL ERROR: TakeProfit {result.TakeProfit:F2} <= Price {currentPrice:F2} - forcing fallback");
                result.TakeProfit = currentPrice * 1.10; // 10% sopra come fallback sicuro
            }

            // Calcola percentuali effettive
            result.StopLossPercent = ((currentPrice - result.StopLoss) / currentPrice) * 100;
            result.TakeProfitPercent = ((result.TakeProfit - currentPrice) / currentPrice) * 100;

            // Risk/Reward ratio
            var risk = currentPrice - result.StopLoss;
            var reward = result.TakeProfit - currentPrice;
            result.RiskRewardRatio = risk > 0 ? reward / risk : 0;

            // Verifica che il R/R sia accettabile
            if (result.RiskRewardRatio < _riskParams.MinRiskRewardRatio)
            {
                // Aggiusta il take profit per migliorare R/R
                result.TakeProfit = currentPrice + (risk * _riskParams.MinRiskRewardRatio);
                result.TakeProfitPercent = ((result.TakeProfit - currentPrice) / currentPrice) * 100;
                result.RiskRewardRatio = _riskParams.MinRiskRewardRatio;
                result.CalculationMethod += " + R/R adjusted";
            }

            result.SupportLevel = support;
            result.ResistanceLevel = resistance;
            result.Reasoning = $"SL: {result.CalculationMethod}, TP: {takeProfitPercent:F1}% target, R/R: 1:{result.RiskRewardRatio:F1}";

            // 📊 LOG DI DEBUG per monitorare i calcoli
            _logger.LogDebug($"Risk levels for symbol: Price={currentPrice:F2}, SL={result.StopLoss:F2} ({result.StopLossPercent:F1}%), TP={result.TakeProfit:F2} ({result.TakeProfitPercent:F1}%), R/R=1:{result.RiskRewardRatio:F1}");

            return result;
        }

        private double GetDynamicStopLossPercent(double confidence)
        {
            // Stop loss più stretto per segnali ad alta confidence
            return confidence switch
            {
                >= 90 => 3.0,   // 3% per segnali molto forti
                >= 80 => 4.0,   // 4% per segnali forti
                >= 70 => 5.0,   // 5% per segnali buoni
                >= 60 => 6.0,   // 6% per segnali medi
                _ => 7.0        // 7% per segnali deboli
            };
        }

        private double GetDynamicTakeProfitPercent(double confidence)
        {
            // Take profit più ambizioso per segnali ad alta confidence
            return confidence switch
            {
                >= 90 => 20.0,  // 20% per segnali molto forti
                >= 80 => 15.0,  // 15% per segnali forti
                >= 70 => 12.0,  // 12% per segnali buoni
                >= 60 => 10.0,  // 10% per segnali medi
                _ => 8.0        // 8% per segnali deboli
            };
        }

        private (int shares, double value, double maxRisk, double potentialGain) CalculatePositionSizing(
            double currentPrice,
            double stopLoss)
        {
            var riskPerShare = currentPrice - stopLoss;
            var maxRiskAmount = _riskParams.PortfolioValue * (_riskParams.MaxPositionSizePercent / 100);

            // Calcola numero azioni basato sul rischio massimo
            var suggestedShares = riskPerShare > 0 ? (int)(maxRiskAmount / riskPerShare) : 0;

            // Limita la posizione al max % del portafoglio
            var maxPositionValue = _riskParams.PortfolioValue * (_riskParams.MaxPositionSizePercent / 100);
            var calculatedPositionValue = suggestedShares * currentPrice;

            if (calculatedPositionValue > maxPositionValue)
            {
                suggestedShares = (int)(maxPositionValue / currentPrice);
                calculatedPositionValue = suggestedShares * currentPrice;
            }

            var actualRisk = suggestedShares * riskPerShare;
            var potentialGain = suggestedShares * (currentPrice * 0.15); // Stima 15% gain

            return (suggestedShares, calculatedPositionValue, actualRisk, potentialGain);
        }

        private async Task EnhanceWithMarketContext(TradingSignal signal)
        {
            try
            {
                // Analizza l'andamento recente per determinare il contesto di mercato
                var recentIndicators = await _indicatorCollection
                    .Find(Builders<StockIndicator>.Filter.Eq(x => x.Symbol, signal.Symbol))
                    .SortByDescending(x => x.CreatedAt)
                    .Limit(10)
                    .ToListAsync();

                if (recentIndicators.Count >= 5)
                {
                    var avgRSI = recentIndicators.Take(5).Average(x => x.RSI);
                    var avgVolume = recentIndicators.Take(5).Average(x => x.Volume);
                    var priceChange = recentIndicators.Count >= 2 ?
                        ((recentIndicators[0].Price - recentIndicators.Last().Price) / recentIndicators.Last().Price) * 100 : 0;

                    // Market Condition
                    signal.MarketCondition = avgRSI switch
                    {
                        < 35 => "Oversold",
                        > 65 => "Overbought",
                        _ => priceChange > 5 ? "Bullish" : priceChange < -5 ? "Bearish" : "Sideways"
                    };

                    // Volume Strength (1-10) basato sul volume del segnale
                    var volumeRatio = signal.Volume / Math.Max(avgVolume, 1);
                    signal.VolumeStrength = Math.Min(10, Math.Max(1, volumeRatio * 5));

                    // Trend Strength (1-10)
                    signal.TrendStrength = Math.Min(10, Math.Max(1, (Math.Abs(priceChange) / 2) + 5));
                }
                else
                {
                    signal.MarketCondition = "Unknown";
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

        private string GenerateEntryStrategy(TradingSignal signal, LevelCalculationResult levels)
        {
            var strategies = new List<string>();

            // Strategia basata su confidence
            if (signal.Confidence >= 85)
            {
                strategies.Add("Market order at current price");
            }
            else
            {
                strategies.Add($"Limit order at ${signal.Price * 0.995:F2} (0.5% below current)");
            }

            // Strategia basata su volume
            if (signal.VolumeStrength >= 7)
            {
                strategies.Add("High volume - enter immediately");
            }
            else
            {
                strategies.Add("Wait for volume confirmation");
            }

            // Strategia basata su supporto
            if (signal.SupportLevel > 0 && signal.Price > signal.SupportLevel * 1.02)
            {
                strategies.Add($"Above support (${signal.SupportLevel:F2}) - good entry");
            }

            return string.Join(" | ", strategies);
        }

        private string GenerateExitStrategy(TradingSignal signal, LevelCalculationResult levels)
        {
            var strategies = new List<string>();

            strategies.Add($"Stop Loss: ${levels.StopLoss:F2} ({levels.StopLossPercent:F1}%)");
            strategies.Add($"Take Profit: ${levels.TakeProfit:F2} ({levels.TakeProfitPercent:F1}%)");

            if (signal.ResistanceLevel > 0)
            {
                strategies.Add($"Watch resistance at ${signal.ResistanceLevel:F2}");
            }

            if (signal.Confidence >= 90)
            {
                strategies.Add("Consider partial profit-taking at 50% of TP");
            }

            return string.Join(" | ", strategies);
        }

        private void ApplyDefaultRiskLevels(TradingSignal signal)
        {
            signal.StopLoss = signal.Price * (1 - _riskParams.DefaultStopLossPercent / 100);
            signal.TakeProfit = signal.Price * (1 + _riskParams.DefaultTakeProfitPercent / 100);
            signal.StopLossPercent = _riskParams.DefaultStopLossPercent;
            signal.TakeProfitPercent = _riskParams.DefaultTakeProfitPercent;
            signal.RiskRewardRatio = _riskParams.DefaultTakeProfitPercent / _riskParams.DefaultStopLossPercent;

            var positionSizing = CalculatePositionSizing(signal.Price, signal.StopLoss.Value);
            signal.SuggestedShares = positionSizing.shares;
            signal.PositionValue = positionSizing.value;
            signal.MaxRiskAmount = positionSizing.maxRisk;
            signal.PotentialGainAmount = positionSizing.potentialGain;

            signal.EntryStrategy = "Default market order";
            signal.ExitStrategy = $"SL: ${signal.StopLoss:F2} | TP: ${signal.TakeProfit:F2}";
        }
    }
}