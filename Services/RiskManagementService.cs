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
        private readonly CurrencyConversionService _currencyService;

        public RiskManagementService(
                IMongoDatabase database,
                YahooFinanceService yahooFinance,
                ILogger<RiskManagementService> logger,
                CurrencyConversionService currencyService,
                IConfiguration config)
        {
            _indicatorCollection = database.GetCollection<StockIndicator>("Indicators");
            _yahooFinance = yahooFinance;
            _logger = logger;
            _currencyService = currencyService;

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

                // 🌍 NUOVO: Determina valuta del simbolo
                var symbolCurrency = _currencyService.GetSymbolCurrency(signal.Symbol);
                _logger.LogDebug($"Symbol {signal.Symbol} currency detected: {symbolCurrency}");

                // 1. Calcola ATR per volatilità
                var atr = await CalculateATR(signal.Symbol);
                signal.ATR = atr;

                // 🌍 NUOVO: Converti ATR in Euro se necessario
                if (symbolCurrency != "EUR")
                {
                    atr = await _currencyService.ConvertToEuroAsync(atr, symbolCurrency);
                    signal.ATR = atr;
                    _logger.LogDebug($"ATR converted to EUR: {atr:F4}");
                }

                // 2. Calcola supporti e resistenze
                var (support, resistance) = await CalculateSupportResistance(signal.Symbol);

                // 🌍 NUOVO: Converti Support/Resistance in Euro
                if (symbolCurrency != "EUR")
                {
                    support = await _currencyService.ConvertToEuroAsync(support, symbolCurrency);
                    resistance = await _currencyService.ConvertToEuroAsync(resistance, symbolCurrency);
                    _logger.LogDebug($"S/R converted to EUR: Support=€{support:F2}, Resistance=€{resistance:F2}");
                }

                // Validazione supporto/resistenza
                var currentPriceEUR = symbolCurrency != "EUR" ?
                    await _currencyService.ConvertToEuroAsync(signal.Price, symbolCurrency) :
                    signal.Price;

                if (support >= currentPriceEUR)
                {
                    _logger.LogWarning($"Invalid support €{support:F2} >= price €{currentPriceEUR:F2} for {signal.Symbol} - adjusting");
                    support = currentPriceEUR * 0.95;
                }

                if (resistance <= currentPriceEUR)
                {
                    _logger.LogWarning($"Invalid resistance €{resistance:F2} <= price €{currentPriceEUR:F2} for {signal.Symbol} - adjusting");
                    resistance = currentPriceEUR * 1.05;
                }

                signal.SupportLevel = support;
                signal.ResistanceLevel = resistance;

                // 3. Calcola Stop Loss e Take Profit in EURO
                var levels = await CalculateRiskLevelsInEuro(currentPriceEUR, atr, support, resistance, signal.Confidence, symbolCurrency);

                signal.StopLoss = levels.StopLoss;
                signal.TakeProfit = levels.TakeProfit;
                signal.StopLossPercent = levels.StopLossPercent;
                signal.TakeProfitPercent = levels.TakeProfitPercent;
                signal.RiskRewardRatio = levels.RiskRewardRatio;

                // 🌍 NUOVO: Aggiungi info valuta al segnale
                signal.Currency = "EUR";
                signal.OriginalCurrency = symbolCurrency;
                signal.ExchangeRate = symbolCurrency != "EUR" ?
                    await _currencyService.GetExchangeRateAsync(symbolCurrency, "EUR") : 1.0;

                // 4. Validazione critica
                if (!ValidateRiskLevels(signal))
                {
                    _logger.LogError($"Risk levels validation FAILED for {signal.Symbol} - applying safe defaults");
                    await ApplyDefaultRiskLevelsInEuro(signal, currentPriceEUR);
                    return signal;
                }

                // 5. Calcola position sizing in EURO
                var positionSizing = CalculatePositionSizingInEuro(currentPriceEUR, levels.StopLoss);
                signal.SuggestedShares = positionSizing.shares;
                signal.PositionValue = positionSizing.value;
                signal.MaxRiskAmount = positionSizing.maxRisk;
                signal.PotentialGainAmount = positionSizing.potentialGain;
                signal.MaxPositionSize = _riskParams.MaxPositionSizePercent;

                // 6. Resto della logica esistente...
                await EnhanceWithMarketContext(signal);
                signal.EntryStrategy = GenerateEntryStrategyEuro(signal, levels);
                signal.ExitStrategy = GenerateExitStrategyEuro(signal, levels);

                _logger.LogInformation($"✅ Risk management calculated for {signal.Symbol}: " +
                    $"Entry: €{currentPriceEUR:F2}, SL: €{levels.StopLoss:F2} ({levels.StopLossPercent:F1}%), " +
                    $"TP: €{levels.TakeProfit:F2} ({levels.TakeProfitPercent:F1}%), R/R: 1:{levels.RiskRewardRatio:F1}");

                return signal;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating risk management for {symbol}", signal.Symbol);
                await ApplyDefaultRiskLevelsInEuro(signal, signal.Price);
                return signal;
            }
        }

        // 3. 🆕 NUOVO: Metodo per calcolare livelli in Euro
        private async Task<LevelCalculationResult> CalculateRiskLevelsInEuro(
            double currentPriceEUR,
            double atrEUR,
            double supportEUR,
            double resistanceEUR,
            double confidence,
            string originalCurrency)
        {
            var result = new LevelCalculationResult();

            // Stop Loss basato su ATR (in Euro)
            if (_riskParams.UseATRForStopLoss && atrEUR > 0)
            {
                var atrStopDistance = atrEUR * _riskParams.ATRMultiplier;
                var atrBasedStop = currentPriceEUR - atrStopDistance;

                if (supportEUR > 0 && supportEUR < currentPriceEUR && supportEUR < atrBasedStop)
                {
                    result.StopLoss = Math.Max(atrBasedStop, supportEUR * 0.98);
                    result.CalculationMethod = $"ATR-based (€{atrEUR:F3}) con supporto floor";
                }
                else
                {
                    result.StopLoss = atrBasedStop;
                    result.CalculationMethod = $"ATR-based (€{atrEUR:F3})";
                }
            }
            else
            {
                var stopLossPercent = GetDynamicStopLossPercent(confidence);
                result.StopLoss = currentPriceEUR * (1 - stopLossPercent / 100);
                result.CalculationMethod = $"Fixed percentage ({stopLossPercent:F1}%)";
            }

            // Take Profit (in Euro)
            var takeProfitPercent = GetDynamicTakeProfitPercent(confidence);
            var calculatedTakeProfit = currentPriceEUR * (1 + takeProfitPercent / 100);

            if (resistanceEUR > currentPriceEUR && calculatedTakeProfit > resistanceEUR)
            {
                result.TakeProfit = resistanceEUR * 0.98;
                result.CalculationMethod += " + resistance ceiling";
            }
            else
            {
                result.TakeProfit = calculatedTakeProfit;
            }

            // Validazione critica
            if (result.StopLoss >= currentPriceEUR)
            {
                _logger.LogError($"CRITICAL: Stop Loss €{result.StopLoss:F2} >= Price €{currentPriceEUR:F2} - FORCED CORRECTION");
                result.StopLoss = currentPriceEUR * 0.95;
                result.CalculationMethod += " [FORCED CORRECTION]";
            }

            if (result.TakeProfit <= currentPriceEUR)
            {
                _logger.LogError($"CRITICAL: Take Profit €{result.TakeProfit:F2} <= Price €{currentPriceEUR:F2} - FORCED CORRECTION");
                result.TakeProfit = currentPriceEUR * 1.10;
                result.CalculationMethod += " [FORCED CORRECTION]";
            }

            // Calcola percentuali
            result.StopLossPercent = ((currentPriceEUR - result.StopLoss) / currentPriceEUR) * 100;
            result.TakeProfitPercent = ((result.TakeProfit - currentPriceEUR) / currentPriceEUR) * 100;

            // Risk/Reward
            var risk = currentPriceEUR - result.StopLoss;
            var reward = result.TakeProfit - currentPriceEUR;
            result.RiskRewardRatio = risk > 0 ? reward / risk : 0;

            // Verifica R/R minimo
            if (result.RiskRewardRatio < _riskParams.MinRiskRewardRatio)
            {
                var newTakeProfit = currentPriceEUR + (risk * _riskParams.MinRiskRewardRatio);
                result.TakeProfit = newTakeProfit;
                result.TakeProfitPercent = ((newTakeProfit - currentPriceEUR) / currentPriceEUR) * 100;
                result.RiskRewardRatio = _riskParams.MinRiskRewardRatio;
                result.CalculationMethod += " + R/R adjusted";
            }

            result.SupportLevel = supportEUR;
            result.ResistanceLevel = resistanceEUR;
            result.Reasoning = $"EUR conversion from {originalCurrency}, {result.CalculationMethod}";

            return result;
        }

        // 4. 🆕 NUOVO: Position Sizing in Euro
        private (int shares, double value, double maxRisk, double potentialGain) CalculatePositionSizingInEuro(
            double currentPriceEUR,
            double stopLossEUR)
        {
            var riskPerShare = Math.Abs(currentPriceEUR - stopLossEUR);
            var maxRiskAmount = _riskParams.PortfolioValue * (_riskParams.MaxPositionSizePercent / 100);

            var suggestedShares = riskPerShare > 0 ? (int)(maxRiskAmount / riskPerShare) : 0;
            var maxPositionValue = _riskParams.PortfolioValue * (_riskParams.MaxPositionSizePercent / 100);
            var calculatedPositionValue = suggestedShares * currentPriceEUR;

            if (calculatedPositionValue > maxPositionValue)
            {
                suggestedShares = (int)(maxPositionValue / currentPriceEUR);
                calculatedPositionValue = suggestedShares * currentPriceEUR;
            }

            if (suggestedShares == 0 && maxPositionValue > currentPriceEUR)
            {
                suggestedShares = 1;
                calculatedPositionValue = currentPriceEUR;
            }

            var actualRisk = suggestedShares * riskPerShare;
            var potentialGain = suggestedShares * (currentPriceEUR * 0.15);

            return (suggestedShares, calculatedPositionValue, actualRisk, potentialGain);
        }

        // 5. 🆕 NUOVO: Strategy generation in Euro
        private string GenerateEntryStrategyEuro(TradingSignal signal, LevelCalculationResult levels)
        {
            var strategies = new List<string>();
            var currencySymbol = "€";

            if (signal.Confidence >= 85)
            {
                strategies.Add($"Market order at current price €{signal.Price:F2}");
            }
            else
            {
                strategies.Add($"Limit order at €{signal.Price * 0.995:F2} (0.5% below current)");
            }

            if (signal.VolumeStrength >= 7)
            {
                strategies.Add("High volume - enter immediately");
            }
            else
            {
                strategies.Add("Wait for volume confirmation");
            }

            if (signal.SupportLevel > 0)
            {
                strategies.Add($"Above support (€{signal.SupportLevel:F2}) - good entry");
            }

            return string.Join(" | ", strategies);
        }

        private string GenerateExitStrategyEuro(TradingSignal signal, LevelCalculationResult levels)
        {
            var strategies = new List<string>();

            strategies.Add($"Stop Loss: €{levels.StopLoss:F2} ({levels.StopLossPercent:F1}%)");
            strategies.Add($"Take Profit: €{levels.TakeProfit:F2} ({levels.TakeProfitPercent:F1}%)");

            if (signal.ResistanceLevel > 0 && signal.ResistanceLevel > signal.Price)
            {
                strategies.Add($"Watch resistance at €{signal.ResistanceLevel:F2}");
            }

            if (signal.Confidence >= 90)
            {
                strategies.Add("Consider partial profit-taking at 50% of TP");
            }

            return string.Join(" | ", strategies);
        }

        // 6. 🆕 NUOVO: Default levels in Euro
        private async Task ApplyDefaultRiskLevelsInEuro(TradingSignal signal, double currentPriceEUR)
        {
            signal.StopLoss = currentPriceEUR * (1 - _riskParams.DefaultStopLossPercent / 100);
            signal.TakeProfit = currentPriceEUR * (1 + _riskParams.DefaultTakeProfitPercent / 100);
            signal.StopLossPercent = _riskParams.DefaultStopLossPercent;
            signal.TakeProfitPercent = _riskParams.DefaultTakeProfitPercent;
            signal.RiskRewardRatio = _riskParams.DefaultTakeProfitPercent / _riskParams.DefaultStopLossPercent;

            var positionSizing = CalculatePositionSizingInEuro(currentPriceEUR, signal.StopLoss.Value);
            signal.SuggestedShares = positionSizing.shares;
            signal.PositionValue = positionSizing.value;
            signal.MaxRiskAmount = positionSizing.maxRisk;
            signal.PotentialGainAmount = positionSizing.potentialGain;

            signal.Currency = "EUR";
            signal.EntryStrategy = "Default market order";
            signal.ExitStrategy = $"SL: €{signal.StopLoss:F2} | TP: €{signal.TakeProfit:F2}";

            _logger.LogWarning($"Applied DEFAULT Euro risk levels for {signal.Symbol}");
        }

        // 🔧 NUOVO: Metodo di validazione critico
        private bool ValidateRiskLevels(TradingSignal signal)
        {
            var validationErrors = new List<string>();

            // 1. Stop Loss deve essere SOTTO il prezzo per segnali BUY
            if (signal.Type == SignalType.Buy && signal.StopLoss >= signal.Price)
            {
                validationErrors.Add($"Stop Loss {signal.StopLoss:F2} >= Price {signal.Price:F2} per segnale BUY");
            }

            // 2. Take Profit deve essere SOPRA il prezzo per segnali BUY
            if (signal.Type == SignalType.Buy && signal.TakeProfit <= signal.Price)
            {
                validationErrors.Add($"Take Profit {signal.TakeProfit:F2} <= Price {signal.Price:F2} per segnale BUY");
            }

            // 3. Percentuali devono essere positive
            if (signal.StopLossPercent <= 0)
            {
                validationErrors.Add($"Stop Loss Percent {signal.StopLossPercent:F2} <= 0");
            }

            if (signal.TakeProfitPercent <= 0)
            {
                validationErrors.Add($"Take Profit Percent {signal.TakeProfitPercent:F2} <= 0");
            }

            // 4. Risk/Reward deve essere ragionevole
            if (signal.RiskRewardRatio <= 0 || signal.RiskRewardRatio > 10)
            {
                validationErrors.Add($"Risk/Reward ratio {signal.RiskRewardRatio:F2} fuori range [0.1-10]");
            }

            // 5. Supporto deve essere sotto prezzo
            if (signal.SupportLevel >= signal.Price)
            {
                validationErrors.Add($"Support {signal.SupportLevel:F2} >= Price {signal.Price:F2}");
            }

            // 6. Resistenza deve essere sopra prezzo
            if (signal.ResistanceLevel <= signal.Price)
            {
                validationErrors.Add($"Resistance {signal.ResistanceLevel:F2} <= Price {signal.Price:F2}");
            }

            if (validationErrors.Any())
            {
                _logger.LogError($"VALIDATION ERRORS for {signal.Symbol}:");
                foreach (var error in validationErrors)
                {
                    _logger.LogError($"  ❌ {error}");
                }
                return false;
            }

            _logger.LogDebug($"✅ Risk levels validation PASSED for {signal.Symbol}");
            return true;
        }

        private async Task<double> CalculateATR(string symbol, int periods = 14)
        {
            try
            {
                var historicalData = await _yahooFinance.GetHistoricalDataAsync(symbol, periods + 5);

                var highs = historicalData["h"]?.ToObject<List<double>>() ?? new List<double>();
                var lows = historicalData["l"]?.ToObject<List<double>>() ?? new List<double>();
                var closes = historicalData["c"]?.ToObject<List<double>>() ?? new List<double>();

                _logger.LogDebug($"ATR calculation for {symbol}: highs={highs.Count}, lows={lows.Count}, closes={closes.Count}");

                if (highs.Count < periods || lows.Count < periods || closes.Count < periods)
                {
                    _logger.LogWarning($"Insufficient data for ATR calculation: {symbol} (need {periods}, got {Math.Min(highs.Count, Math.Min(lows.Count, closes.Count))})");
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

                if (trueRanges.Count < periods)
                {
                    _logger.LogWarning($"Insufficient True Ranges for ATR: {symbol} ({trueRanges.Count}/{periods})");
                    return trueRanges.Any() ? trueRanges.Average() : 0;
                }

                var atr = trueRanges.Take(periods).Average();
                _logger.LogDebug($"ATR calculated for {symbol}: {atr:F4}");
                return atr;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating ATR for {symbol}", symbol);
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

                if (highs.Count < 10 || lows.Count < 10 || closes.Count < 10)
                {
                    _logger.LogWarning($"Insufficient data for S/R calculation: {symbol}");
                    return (0, 0);
                }

                var currentPrice = closes.First(); // Prezzo più recente

                // 🔧 FIX CRITICO: Calcolo corretto supporto/resistenza
                var recentLows = lows.Take(20).Where(low => low > 0).ToList();
                var recentHighs = highs.Take(20).Where(high => high > 0).ToList();

                // Supporto = il PIÙ ALTO tra i minimi che è SOTTO il prezzo corrente
                var validSupports = recentLows
                    .Where(low => low < currentPrice * 0.98) // 2% buffer sotto
                    .OrderByDescending(x => x) // Ordina dal più alto al più basso
                    .Take(3) // Prendi i 3 più alti
                    .ToList();

                var support = validSupports.Any() ? validSupports.First() : currentPrice * 0.95;

                // Resistenza = il PIÙ BASSO tra i massimi che è SOPRA il prezzo corrente
                var validResistances = recentHighs
                    .Where(high => high > currentPrice * 1.02) // 2% buffer sopra
                    .OrderBy(x => x) // Ordina dal più basso al più alto
                    .Take(3) // Prendi i 3 più bassi
                    .ToList();

                var resistance = validResistances.Any() ? validResistances.First() : currentPrice * 1.05;

                // 🔧 VALIDAZIONE FINALE POST-CALCOLO
                if (support >= currentPrice)
                {
                    _logger.LogWarning($"Support {support:F2} >= current price {currentPrice:F2} for {symbol} - forcing correction");
                    support = currentPrice * 0.95;
                }

                if (resistance <= currentPrice)
                {
                    _logger.LogWarning($"Resistance {resistance:F2} <= current price {currentPrice:F2} for {symbol} - forcing correction");
                    resistance = currentPrice * 1.05;
                }

                _logger.LogDebug($"S/R calculated for {symbol}: Support={support:F2} ({((support - currentPrice) / currentPrice * 100):F1}%), " +
                                $"Resistance={resistance:F2} ({((resistance - currentPrice) / currentPrice * 100):F1}%), Current={currentPrice:F2}");

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

            // 🔧 ASSICURATI CHE STIAMO CALCOLANDO PER UN SEGNALE BUY
            // Stop Loss deve essere SOTTO il prezzo, Take Profit SOPRA

            // METODO 1: Stop Loss basato su ATR (più dinamico)
            if (_riskParams.UseATRForStopLoss && atr > 0)
            {
                var atrStopDistance = atr * _riskParams.ATRMultiplier;
                var atrBasedStop = currentPrice - atrStopDistance; // SOTTO il prezzo

                // Usa il supporto come floor solo se è logico
                if (support > 0 && support < currentPrice && support < atrBasedStop)
                {
                    result.StopLoss = Math.Max(atrBasedStop, support * 0.98); // Leggermente sotto il supporto
                    result.CalculationMethod = $"ATR-based (ATR={atr:F3}) con supporto floor";
                }
                else
                {
                    result.StopLoss = atrBasedStop;
                    result.CalculationMethod = $"ATR-based puro (ATR={atr:F3})";
                }
            }
            else
            {
                // METODO 2: Stop Loss percentuale fisso (SEMPRE SOTTO IL PREZZO)
                var stopLossPercent = GetDynamicStopLossPercent(confidence);
                result.StopLoss = currentPrice * (1 - stopLossPercent / 100); // (1 - %) per andare sotto
                result.CalculationMethod = $"Fixed percentage ({stopLossPercent:F1}%)";
            }

            // Take Profit intelligente (SEMPRE SOPRA IL PREZZO)
            var takeProfitPercent = GetDynamicTakeProfitPercent(confidence);
            var calculatedTakeProfit = currentPrice * (1 + takeProfitPercent / 100); // (1 + %) per andare sopra

            // Usa resistenza come ceiling solo se è logica
            if (resistance > currentPrice && calculatedTakeProfit > resistance)
            {
                result.TakeProfit = resistance * 0.98; // Leggermente sotto resistenza
                result.CalculationMethod += " + resistance ceiling";
            }
            else
            {
                result.TakeProfit = calculatedTakeProfit;
            }

            // 🔧 VALIDAZIONE CRITICA POST-CALCOLO
            if (result.StopLoss >= currentPrice)
            {
                _logger.LogError($"CRITICAL: Stop Loss {result.StopLoss:F2} >= Price {currentPrice:F2} - FORCED CORRECTION");
                result.StopLoss = currentPrice * 0.95; // 5% sotto come ultima risorsa
                result.CalculationMethod += " [FORCED CORRECTION]";
            }

            if (result.TakeProfit <= currentPrice)
            {
                _logger.LogError($"CRITICAL: Take Profit {result.TakeProfit:F2} <= Price {currentPrice:F2} - FORCED CORRECTION");
                result.TakeProfit = currentPrice * 1.10; // 10% sopra come ultima risorsa
                result.CalculationMethod += " [FORCED CORRECTION]";
            }

            // Calcola percentuali effettive (DEVONO ESSERE POSITIVE)
            result.StopLossPercent = ((currentPrice - result.StopLoss) / currentPrice) * 100;
            result.TakeProfitPercent = ((result.TakeProfit - currentPrice) / currentPrice) * 100;

            // Assicurati che le percentuali siano positive
            if (result.StopLossPercent <= 0)
            {
                _logger.LogError($"CRITICAL: Stop Loss Percent {result.StopLossPercent:F2} <= 0");
                result.StopLossPercent = 5.0; // Default
                result.StopLoss = currentPrice * 0.95;
            }

            if (result.TakeProfitPercent <= 0)
            {
                _logger.LogError($"CRITICAL: Take Profit Percent {result.TakeProfitPercent:F2} <= 0");
                result.TakeProfitPercent = 10.0; // Default
                result.TakeProfit = currentPrice * 1.10;
            }

            // Risk/Reward ratio
            var risk = currentPrice - result.StopLoss;
            var reward = result.TakeProfit - currentPrice;
            result.RiskRewardRatio = risk > 0 ? reward / risk : 0;

            // Verifica R/R minimo
            if (result.RiskRewardRatio < _riskParams.MinRiskRewardRatio)
            {
                var newTakeProfit = currentPrice + (risk * _riskParams.MinRiskRewardRatio);
                result.TakeProfit = newTakeProfit;
                result.TakeProfitPercent = ((newTakeProfit - currentPrice) / currentPrice) * 100;
                result.RiskRewardRatio = _riskParams.MinRiskRewardRatio;
                result.CalculationMethod += " + R/R adjusted";
            }

            result.SupportLevel = support;
            result.ResistanceLevel = resistance;
            result.Reasoning = $"Method: {result.CalculationMethod}, R/R: 1:{result.RiskRewardRatio:F1}";

            return result;
        }

        private double GetDynamicStopLossPercent(double confidence)
        {
            return confidence switch
            {
                >= 90 => 3.0,
                >= 80 => 4.0,
                >= 70 => 5.0,
                >= 60 => 6.0,
                _ => 7.0
            };
        }

        private double GetDynamicTakeProfitPercent(double confidence)
        {
            return confidence switch
            {
                >= 90 => 20.0,
                >= 80 => 15.0,
                >= 70 => 12.0,
                >= 60 => 10.0,
                _ => 8.0
            };
        }

        private (int shares, double value, double maxRisk, double potentialGain) CalculatePositionSizing(
            double currentPrice,
            double stopLoss)
        {
            var riskPerShare = Math.Abs(currentPrice - stopLoss);
            var maxRiskAmount = _riskParams.PortfolioValue * (_riskParams.MaxPositionSizePercent / 100);

            var suggestedShares = riskPerShare > 0 ? (int)(maxRiskAmount / riskPerShare) : 0;
            var maxPositionValue = _riskParams.PortfolioValue * (_riskParams.MaxPositionSizePercent / 100);
            var calculatedPositionValue = suggestedShares * currentPrice;

            if (calculatedPositionValue > maxPositionValue)
            {
                suggestedShares = (int)(maxPositionValue / currentPrice);
                calculatedPositionValue = suggestedShares * currentPrice;
            }

            // 🔧 FIX: Assicurati che suggestedShares sia almeno 1 se ha senso
            if (suggestedShares == 0 && maxPositionValue > currentPrice)
            {
                suggestedShares = 1;
                calculatedPositionValue = currentPrice;
            }

            var actualRisk = suggestedShares * riskPerShare;
            var potentialGain = suggestedShares * (currentPrice * 0.15);

            return (suggestedShares, calculatedPositionValue, actualRisk, potentialGain);
        }

        private async Task EnhanceWithMarketContext(TradingSignal signal)
        {
            try
            {
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

                    signal.MarketCondition = avgRSI switch
                    {
                        < 35 => "Oversold",
                        > 65 => "Overbought",
                        _ => priceChange > 5 ? "Bullish" : priceChange < -5 ? "Bearish" : "Sideways"
                    };

                    var volumeRatio = signal.Volume / Math.Max(avgVolume, 1);
                    signal.VolumeStrength = Math.Min(10, Math.Max(1, volumeRatio * 5));
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

            if (signal.Confidence >= 85)
            {
                strategies.Add("Market order at current price");
            }
            else
            {
                strategies.Add($"Limit order at ${signal.Price * 0.995:F2} (0.5% below current)");
            }

            if (signal.VolumeStrength >= 7)
            {
                strategies.Add("High volume - enter immediately");
            }
            else
            {
                strategies.Add("Wait for volume confirmation");
            }

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

            if (signal.ResistanceLevel > 0 && signal.ResistanceLevel > signal.Price)
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

            _logger.LogWarning($"Applied DEFAULT risk levels for {signal.Symbol} due to calculation failures");
        }
    }
}
