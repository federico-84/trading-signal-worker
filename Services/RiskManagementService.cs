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
                    _logger.LogError($"🚨 SIGNAL REJECTED for {signal.Symbol} - invalid risk/reward setup");
                    throw new InvalidOperationException($"Signal validation failed for {signal.Symbol}"); // ❌ RIGETTA!
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

            // 🔍 DEBUG: Log tutti gli input
            _logger.LogInformation($"🔍 CalculateRiskLevelsInEuro INPUT:");
            _logger.LogInformation($"   💰 Current Price EUR: €{currentPriceEUR:F2}");
            _logger.LogInformation($"   📊 ATR EUR: €{atrEUR:F3}");
            _logger.LogInformation($"   🟢 Support EUR: €{supportEUR:F2}");
            _logger.LogInformation($"   🔴 Resistance EUR: €{resistanceEUR:F2}");
            _logger.LogInformation($"   🎯 Confidence: {confidence:F1}%");
            _logger.LogInformation($"   💱 Original Currency: {originalCurrency}");

            // 🚨 PRE-VALIDATION: Controlla che gli input siano logici
            var inputErrors = new List<string>();

            if (currentPriceEUR <= 0)
                inputErrors.Add($"Invalid currentPriceEUR: €{currentPriceEUR:F2}");

            if (supportEUR > 0 && supportEUR >= currentPriceEUR)
                inputErrors.Add($"Invalid supportEUR: €{supportEUR:F2} >= currentPrice €{currentPriceEUR:F2}");

            if (resistanceEUR > 0 && resistanceEUR <= currentPriceEUR)
                inputErrors.Add($"Invalid resistanceEUR: €{resistanceEUR:F2} <= currentPrice €{currentPriceEUR:F2}");

            if (inputErrors.Any())
            {
                _logger.LogError($"🚨 INPUT VALIDATION FAILED:");
                foreach (var error in inputErrors)
                {
                    _logger.LogError($"   ❌ {error}");
                }
            }

            // Stop Loss basato su ATR (in Euro)
            if (_riskParams.UseATRForStopLoss && atrEUR > 0)
            {
                var atrStopDistance = atrEUR * _riskParams.ATRMultiplier;
                var atrBasedStop = currentPriceEUR - atrStopDistance;

                _logger.LogDebug($"📊 ATR Stop Calculation:");
                _logger.LogDebug($"   ATR Distance: €{atrEUR:F3} * {_riskParams.ATRMultiplier} = €{atrStopDistance:F3}");
                _logger.LogDebug($"   ATR Stop: €{currentPriceEUR:F2} - €{atrStopDistance:F3} = €{atrBasedStop:F2}");

                // 🔧 FIX: Validazione più rigorosa del supporto
                if (supportEUR > 0 && supportEUR < currentPriceEUR * 0.98) // Support deve essere almeno 2% sotto
                {
                    var supportFloor = supportEUR * 0.98;
                    result.StopLoss = Math.Max(atrBasedStop, supportFloor);
                    result.CalculationMethod = $"ATR+Support: max(€{atrBasedStop:F2}, €{supportFloor:F2}) = €{result.StopLoss:F2}";

                    _logger.LogDebug($"✅ Using support floor: €{supportFloor:F2}, Final SL: €{result.StopLoss:F2}");
                }
                else
                {
                    result.StopLoss = atrBasedStop;
                    result.CalculationMethod = $"ATR-only: €{atrBasedStop:F2}";

                    if (supportEUR > 0)
                    {
                        _logger.LogWarning($"⚠️ Support €{supportEUR:F2} invalid (not < €{currentPriceEUR * 0.98:F2}), using ATR only");
                    }
                }
            }
            else
            {
                var stopLossPercent = GetDynamicStopLossPercent(confidence);
                result.StopLoss = currentPriceEUR * (1 - stopLossPercent / 100);
                result.CalculationMethod = $"Percentage: €{currentPriceEUR:F2} * (1 - {stopLossPercent:F1}%) = €{result.StopLoss:F2}";

                _logger.LogDebug($"📊 Percentage Stop: {stopLossPercent:F1}% = €{result.StopLoss:F2}");
            }

            // Take Profit (in Euro)
            var takeProfitPercent = GetDynamicTakeProfitPercent(confidence);
            var calculatedTakeProfit = currentPriceEUR * (1 + takeProfitPercent / 100);

            _logger.LogDebug($"📊 Take Profit Calculation:");
            _logger.LogDebug($"   Percentage: {takeProfitPercent:F1}%");
            _logger.LogDebug($"   Calculated: €{currentPriceEUR:F2} * (1 + {takeProfitPercent:F1}%) = €{calculatedTakeProfit:F2}");

            // 🔧 FIX: Validazione più rigorosa della resistenza
            if (resistanceEUR > currentPriceEUR * 1.02 && calculatedTakeProfit > resistanceEUR * 0.98)
            {
                result.TakeProfit = resistanceEUR * 0.98;
                result.CalculationMethod += $" + resistance ceiling €{resistanceEUR * 0.98:F2}";

                _logger.LogDebug($"✅ Using resistance ceiling: €{resistanceEUR * 0.98:F2}");
            }
            else
            {
                result.TakeProfit = calculatedTakeProfit;

                if (resistanceEUR > 0 && resistanceEUR <= currentPriceEUR * 1.02)
                {
                    _logger.LogWarning($"⚠️ Resistance €{resistanceEUR:F2} invalid (not > €{currentPriceEUR * 1.02:F2})");
                }
            }

            // 🚨 VALIDAZIONE CRITICA con log dettagliato
            _logger.LogInformation($"🔍 PRE-VALIDATION Results:");
            _logger.LogInformation($"   Entry: €{currentPriceEUR:F2}");
            _logger.LogInformation($"   Stop Loss: €{result.StopLoss:F2}");
            _logger.LogInformation($"   Take Profit: €{result.TakeProfit:F2}");

            if (result.StopLoss >= currentPriceEUR)
            {
                _logger.LogError($"🚨 CRITICAL: Stop Loss €{result.StopLoss:F2} >= Price €{currentPriceEUR:F2}");
                _logger.LogError($"🔧 FORCED CORRECTION: Setting SL to 95% of entry price");

                result.StopLoss = currentPriceEUR * 0.95;
                result.CalculationMethod += " [EMERGENCY CORRECTION: SL was above entry!]";
            }

            if (result.TakeProfit <= currentPriceEUR)
            {
                _logger.LogError($"🚨 CRITICAL: Take Profit €{result.TakeProfit:F2} <= Price €{currentPriceEUR:F2}");
                _logger.LogError($"🔧 FORCED CORRECTION: Setting TP to 110% of entry price");

                result.TakeProfit = currentPriceEUR * 1.10;
                result.CalculationMethod += " [EMERGENCY CORRECTION: TP was below entry!]";
            }

            // Calcola percentuali
            result.StopLossPercent = ((currentPriceEUR - result.StopLoss) / currentPriceEUR) * 100;
            result.TakeProfitPercent = ((result.TakeProfit - currentPriceEUR) / currentPriceEUR) * 100;

            // Risk/Reward
            var risk = currentPriceEUR - result.StopLoss;
            var reward = result.TakeProfit - currentPriceEUR;
            result.RiskRewardRatio = risk > 0 ? reward / risk : 0;

            _logger.LogDebug($"📊 Risk/Reward Calculation:");
            _logger.LogDebug($"   Risk: €{currentPriceEUR:F2} - €{result.StopLoss:F2} = €{risk:F2}");
            _logger.LogDebug($"   Reward: €{result.TakeProfit:F2} - €{currentPriceEUR:F2} = €{reward:F2}");
            _logger.LogDebug($"   R/R Ratio: €{reward:F2} / €{risk:F2} = {result.RiskRewardRatio:F2}");

            // Verifica R/R minimo
            if (result.RiskRewardRatio < _riskParams.MinRiskRewardRatio)
            {
                var oldTP = result.TakeProfit;
                var newTakeProfit = currentPriceEUR + (risk * _riskParams.MinRiskRewardRatio);
                result.TakeProfit = newTakeProfit;
                result.TakeProfitPercent = ((newTakeProfit - currentPriceEUR) / currentPriceEUR) * 100;
                result.RiskRewardRatio = _riskParams.MinRiskRewardRatio;
                result.CalculationMethod += $" + R/R adjusted ({oldTP:F2}→{newTakeProfit:F2})";

                _logger.LogDebug($"🔧 R/R Adjusted: €{oldTP:F2} → €{newTakeProfit:F2} for R/R {_riskParams.MinRiskRewardRatio:F1}");
            }

            result.SupportLevel = supportEUR;
            result.ResistanceLevel = resistanceEUR;
            result.Reasoning = $"EUR from {originalCurrency}: {result.CalculationMethod}";

            // 🔍 FINAL LOG
            _logger.LogInformation($"✅ CalculateRiskLevelsInEuro RESULT:");
            _logger.LogInformation($"   Entry: €{currentPriceEUR:F2}");
            _logger.LogInformation($"   Stop Loss: €{result.StopLoss:F2} (-{result.StopLossPercent:F1}%)");
            _logger.LogInformation($"   Take Profit: €{result.TakeProfit:F2} (+{result.TakeProfitPercent:F1}%)");
            _logger.LogInformation($"   Risk/Reward: 1:{result.RiskRewardRatio:F1}");
            _logger.LogInformation($"   Method: {result.CalculationMethod}");

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

            // ===== 🚨 AGGIUNGI QUESTI DUE CHECK QUI =====

            // 4.5 Entry price deve essere SOTTO la resistenza (lascia spazio per crescere)
            if (signal.ResistanceLevel > 0 && signal.Price >= signal.ResistanceLevel * 0.98)
            {
                validationErrors.Add($"Entry price €{signal.Price:F2} >= Resistance €{signal.ResistanceLevel:F2} * 0.98 - troppo vicino/sopra resistenza");
            }

            // 4.6 Entry price deve essere SOPRA il supporto (non in caduta libera)
            if (signal.SupportLevel > 0 && signal.Price <= signal.SupportLevel * 1.02)
            {
                validationErrors.Add($"Entry price €{signal.Price:F2} <= Support €{signal.SupportLevel:F2} * 1.02 - troppo vicino/sotto supporto");
            }

            // ===== FINE AGGIUNTE =====

            // 5. Supporto deve essere sotto prezzo (check tecnico)
            if (signal.SupportLevel > 0 && signal.SupportLevel >= signal.Price)
            {
                validationErrors.Add($"Support {signal.SupportLevel:F2} >= Price {signal.Price:F2} - configurazione tecnica invalida");
            }

            // 6. Resistenza deve essere sopra prezzo (check tecnico)  
            if (signal.ResistanceLevel > 0 && signal.ResistanceLevel <= signal.Price)
            {
                validationErrors.Add($"Resistance {signal.ResistanceLevel:F2} <= Price {signal.Price:F2} - configurazione tecnica invalida");
            }

            if (validationErrors.Any())
            {
                _logger.LogError($"🚨 VALIDATION ERRORS for {signal.Symbol}:");
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
                // Usa più dati storici per contesto migliore
                var historicalData = await _yahooFinance.GetHistoricalDataAsync(symbol, 90); // 90 giorni invece di 30
                var highs = historicalData["h"]?.ToObject<List<double>>() ?? new List<double>();
                var lows = historicalData["l"]?.ToObject<List<double>>() ?? new List<double>();
                var closes = historicalData["c"]?.ToObject<List<double>>() ?? new List<double>();

                if (highs.Count < 50 || lows.Count < 50)
                {
                    _logger.LogWarning($"Insufficient data for S/R calculation: {symbol}");
                    return (0, 0);
                }

                var currentPrice = closes.First();
                _logger.LogDebug($"🔍 Enhanced S/R calculation for {symbol}: Current={currentPrice:F2}, analyzing {highs.Count} periods");

                // 🔧 SUPPORTO: Trova livelli significativi SOTTO il prezzo
                var recentLows = lows.Where(low => low > 0).ToList();
                var significantLows = recentLows
                    .GroupBy(price => Math.Round(price, 1)) // Raggruppa prezzi simili
                    .Where(group => group.Count() >= 2) // Almeno 2 occorrenze = livello significativo
                    .Select(group => group.Key)
                    .Where(price => price < currentPrice * 0.95) // Almeno 5% sotto
                    .OrderByDescending(price => price)
                    .ToList();

                var support = significantLows.FirstOrDefault();
                if (support == 0)
                {
                    support = recentLows.Where(l => l < currentPrice * 0.95).DefaultIfEmpty(currentPrice * 0.90).Max();
                }

                // 🔧 RESISTENZA: Logica CONSERVATIVA per evitare TP irrealistici
                var recentHighs = highs.Where(high => high > 0).ToList();

                // Trova resistenze significative (toccate più volte)
                var significantHighs = recentHighs
                    .GroupBy(price => Math.Round(price, 1))
                    .Where(group => group.Count() >= 2) // Almeno 2 tocchi = resistenza vera
                    .Select(group => new { Price = group.Key, Count = group.Count() })
                    .Where(item => item.Price > currentPrice * 1.02) // Almeno 2% sopra
                    .OrderBy(item => item.Price) // Inizia dalla più vicina
                    .ToList();

                double resistance;

                if (significantHighs.Any())
                {
                    // Usa la resistenza significativa più vicina
                    resistance = significantHighs.First().Price;
                    var resistanceCount = significantHighs.First().Count;

                    _logger.LogInformation($"✅ Significant resistance found: €{resistance:F2} (touched {resistanceCount} times)");
                }
                else
                {
                    // 🚨 FALLBACK CONSERVATIVO: Se non c'è resistenza chiara, usa target modesto
                    var maxRecent = recentHighs.Take(30).Max(); // Massimo ultimi 30 giorni
                    var conservativeTarget = Math.Min(
                        currentPrice * 1.05, // Max 5% sopra
                        maxRecent * 0.95      // 5% sotto il massimo recente
                    );

                    resistance = conservativeTarget;
                    _logger.LogWarning($"⚠️ No significant resistance found, using conservative target: €{resistance:F2}");
                }

                // 🔧 VALIDAZIONE FINALE con controllo storico
                var historicalMax = recentHighs.Max();
                if (resistance > historicalMax * 0.98) // TP troppo vicino al massimo storico
                {
                    resistance = historicalMax * 0.95; // 5% sotto il massimo storico
                    _logger.LogWarning($"🚨 Resistance adjusted: Too close to historical max €{historicalMax:F2}, using €{resistance:F2}");
                }

                // Double check finale
                if (support >= currentPrice || resistance <= currentPrice)
                {
                    _logger.LogError($"🚨 S/R validation failed: S={support:F2}, P={currentPrice:F2}, R={resistance:F2}");
                    support = currentPrice * 0.90;
                    resistance = currentPrice * 1.05; // Target modesto
                }

                _logger.LogInformation($"✅ Conservative S/R for {symbol}: " +
                    $"Support=€{support:F2} ({((support / currentPrice - 1) * 100):+F1}%), " +
                    $"Resistance=€{resistance:F2} ({((resistance / currentPrice - 1) * 100):+F1}%), " +
                    $"Historical Max=€{historicalMax:F2}");

                return (support, resistance);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating S/R for {symbol}", symbol);
                return (0, 0);
            }
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
    }
}
