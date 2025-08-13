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

            // 🚨 CRITICAL PRE-VALIDATION: RIGETTA input impossibili
            var criticalErrors = new List<string>();

            if (currentPriceEUR <= 0)
                criticalErrors.Add($"Invalid currentPriceEUR: €{currentPriceEUR:F2}");

            if (supportEUR > 0 && supportEUR >= currentPriceEUR)
                criticalErrors.Add($"IMPOSSIBLE: Support €{supportEUR:F2} >= Current €{currentPriceEUR:F2}");

            if (resistanceEUR > 0 && resistanceEUR <= currentPriceEUR)
                criticalErrors.Add($"IMPOSSIBLE: Resistance €{resistanceEUR:F2} <= Current €{currentPriceEUR:F2}");

            // 🚨 Se input critici sono sbagliati, RIGETTA immediatamente
            if (criticalErrors.Any())
            {
                _logger.LogError($"🚨🚨🚨 CRITICAL INPUT VALIDATION FAILED 🚨🚨🚨");
                foreach (var error in criticalErrors)
                {
                    _logger.LogError($"   ❌ {error}");
                }
                throw new InvalidOperationException($"Critical validation failed: {string.Join(", ", criticalErrors)}");
            }

            // ===== STOP LOSS CALCULATION =====
            if (_riskParams.UseATRForStopLoss && atrEUR > 0)
            {
                var atrStopDistance = atrEUR * _riskParams.ATRMultiplier;
                var atrBasedStop = currentPriceEUR - atrStopDistance;

                _logger.LogDebug($"📊 ATR Stop: €{currentPriceEUR:F2} - €{atrStopDistance:F3} = €{atrBasedStop:F2}");

                // Usa support come floor se valido
                if (supportEUR > 0 && supportEUR < currentPriceEUR * 0.95) // Support almeno 5% sotto
                {
                    var supportFloor = supportEUR * 0.98; // 2% sotto support per safety
                    result.StopLoss = Math.Max(atrBasedStop, supportFloor);
                    result.CalculationMethod = $"ATR+Support: max(€{atrBasedStop:F2}, €{supportFloor:F2})";
                }
                else
                {
                    result.StopLoss = atrBasedStop;
                    result.CalculationMethod = "ATR-based";
                }
            }
            else
            {
                var stopLossPercent = GetDynamicStopLossPercent(confidence);
                result.StopLoss = currentPriceEUR * (1 - stopLossPercent / 100);
                result.CalculationMethod = $"Percentage-based ({stopLossPercent:F1}%)";
            }

            // ===== TAKE PROFIT CALCULATION (INTELLIGENTE) =====
            var takeProfitPercent = GetDynamicTakeProfitPercent(confidence);
            var percentageBasedTP = currentPriceEUR * (1 + takeProfitPercent / 100);

            _logger.LogDebug($"📊 Take Profit Options:");
            _logger.LogDebug($"   Percentage-based ({takeProfitPercent:F1}%): €{percentageBasedTP:F2}");
            _logger.LogDebug($"   Resistance available: €{resistanceEUR:F2}");

            // 🎯 INTELLIGENTE: Usa il MINORE tra percentage e resistance
            if (resistanceEUR > 0 && resistanceEUR > currentPriceEUR * 1.03) // Resistance almeno 3% sopra
            {
                var resistanceBasedTP = resistanceEUR * 0.95; // 5% sotto resistance per sicurezza

                if (percentageBasedTP <= resistanceBasedTP)
                {
                    // Percentage TP è sotto resistance → OK
                    result.TakeProfit = percentageBasedTP;
                    result.CalculationMethod += $" + percentage TP (under resistance)";
                    _logger.LogDebug($"✅ Using percentage TP: €{percentageBasedTP:F2} < resistance €{resistanceBasedTP:F2}");
                }
                else
                {
                    // Percentage TP sopra resistance → USA RESISTANCE
                    result.TakeProfit = resistanceBasedTP;
                    result.CalculationMethod += $" + RESISTANCE-LIMITED (was €{percentageBasedTP:F2})";
                    _logger.LogInformation($"🎯 TP LIMITED by resistance: €{percentageBasedTP:F2} → €{resistanceBasedTP:F2}");
                }
            }
            else if (resistanceEUR > 0) // Resistance troppo vicina
            {
                // Se resistance è molto vicina, usa target conservativo
                result.TakeProfit = Math.Min(percentageBasedTP, currentPriceEUR * 1.03); // Max +3%
                result.CalculationMethod += $" + CONSERVATIVE (resistance too close)";
                _logger.LogWarning($"⚠️ Conservative TP due to close resistance: €{result.TakeProfit:F2}");
            }
            else // Nessuna resistance valida
            {
                // Senza resistance, usa target molto conservativo
                result.TakeProfit = Math.Min(percentageBasedTP, currentPriceEUR * 1.06); // Max +6%
                result.CalculationMethod += $" + NO-RESISTANCE (conservative)";
                _logger.LogWarning($"🚨 No resistance guidance, conservative TP: €{result.TakeProfit:F2}");
            }

            // ===== CRITICAL VALIDATION =====
            if (result.StopLoss >= currentPriceEUR)
            {
                _logger.LogError($"🚨 CRITICAL: Stop Loss €{result.StopLoss:F2} >= Entry €{currentPriceEUR:F2}");
                throw new InvalidOperationException("Stop Loss above entry price - invalid signal");
            }

            if (result.TakeProfit <= currentPriceEUR)
            {
                _logger.LogError($"🚨 CRITICAL: Take Profit €{result.TakeProfit:F2} <= Entry €{currentPriceEUR:F2}");
                throw new InvalidOperationException("Take Profit below entry price - invalid signal");
            }

            // ===== CALCOLA PERCENTUALI E R/R =====
            result.StopLossPercent = ((currentPriceEUR - result.StopLoss) / currentPriceEUR) * 100;
            result.TakeProfitPercent = ((result.TakeProfit - currentPriceEUR) / currentPriceEUR) * 100;

            var risk = currentPriceEUR - result.StopLoss;
            var reward = result.TakeProfit - currentPriceEUR;
            result.RiskRewardRatio = risk > 0 ? reward / risk : 0;

            // ===== R/R MINIMUM CHECK =====
            if (result.RiskRewardRatio < _riskParams.MinRiskRewardRatio)
            {
                // 🚨 IMPORTANTE: Non aumentare TP oltre resistance!
                var maxAllowedTP = resistanceEUR > 0 ? resistanceEUR * 0.95 : currentPriceEUR * 1.10;
                var idealTP = currentPriceEUR + (risk * _riskParams.MinRiskRewardRatio);

                if (idealTP <= maxAllowedTP)
                {
                    // Possiamo migliorare R/R senza superare resistance
                    result.TakeProfit = idealTP;
                    result.TakeProfitPercent = ((idealTP - currentPriceEUR) / currentPriceEUR) * 100;
                    result.RiskRewardRatio = _riskParams.MinRiskRewardRatio;
                    result.CalculationMethod += $" + R/R adjusted to {_riskParams.MinRiskRewardRatio:F1}";
                }
                else
                {
                    // Non possiamo migliorare R/R senza superare resistance
                    _logger.LogWarning($"⚠️ R/R {result.RiskRewardRatio:F1} < target {_riskParams.MinRiskRewardRatio:F1}, but limited by resistance");
                }
            }

            // ===== FINAL VALIDATION =====
            if (result.TakeProfitPercent > 20.0) // Sanity check: mai più di +20%
            {
                _logger.LogError($"🚨 UNREALISTIC: Take Profit {result.TakeProfitPercent:F1}% > 20%");
                throw new InvalidOperationException("Take Profit too ambitious - unrealistic target");
            }

            result.SupportLevel = supportEUR;
            result.ResistanceLevel = resistanceEUR;
            result.Reasoning = $"EUR from {originalCurrency}: {result.CalculationMethod}";

            // ===== SUCCESS LOG =====
            _logger.LogInformation($"✅ RISK LEVELS CALCULATED:");
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

            _logger.LogDebug($"📊 Position Sizing Calculation:");
            _logger.LogDebug($"   Risk per share: €{riskPerShare:F2}");
            _logger.LogDebug($"   Max risk amount: €{maxRiskAmount:F2}");
            _logger.LogDebug($"   Share price: €{currentPriceEUR:F2}");

            // Calcola shares basato sul rischio
            var suggestedShares = riskPerShare > 0 ? (int)(maxRiskAmount / riskPerShare) : 0;

            // 🔧 FIX: Assicurati di comprare almeno 1 share se possibile
            if (suggestedShares == 0 && currentPriceEUR <= _riskParams.PortfolioValue * 0.05) // Max 5% del portfolio per 1 share
            {
                suggestedShares = 1;
                _logger.LogInformation($"✅ Force minimum 1 share for affordable stock (€{currentPriceEUR:F2})");
            }

            // Verifica limiti di portafoglio
            var maxPositionValue = _riskParams.PortfolioValue * (_riskParams.MaxPositionSizePercent / 100);
            var calculatedPositionValue = suggestedShares * currentPriceEUR;

            if (calculatedPositionValue > maxPositionValue)
            {
                suggestedShares = (int)(maxPositionValue / currentPriceEUR);
                calculatedPositionValue = suggestedShares * currentPriceEUR;
                _logger.LogDebug($"🔧 Position limited by max portfolio %: {suggestedShares} shares");
            }

            // 🚨 VALIDATION: Zero shares = segnale inutile
            if (suggestedShares <= 0)
            {
                _logger.LogError($"🚨 POSITION SIZING FAILED: 0 shares suggested");
                _logger.LogError($"   Share price: €{currentPriceEUR:F2}");
                _logger.LogError($"   Risk per share: €{riskPerShare:F2}");
                _logger.LogError($"   Max risk: €{maxRiskAmount:F2}");
                _logger.LogError($"   Portfolio value: €{_riskParams.PortfolioValue:F2}");

                // Return zero values per validation catch
                return (0, 0, 0, 0);
            }

            // Ricalcola i valori finali
            calculatedPositionValue = suggestedShares * currentPriceEUR;
            var actualRisk = suggestedShares * riskPerShare;
            var potentialGain = suggestedShares * (currentPriceEUR * 0.05); // Stima 5% gain

            _logger.LogInformation($"✅ Position Sizing Result:");
            _logger.LogInformation($"   Shares: {suggestedShares}");
            _logger.LogInformation($"   Position value: €{calculatedPositionValue:F2}");
            _logger.LogInformation($"   Actual risk: €{actualRisk:F2}");
            _logger.LogInformation($"   Potential gain: €{potentialGain:F2}");

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

        // 🚨 EMERGENCY FIX nel ValidateRiskLevels method

        private bool ValidateRiskLevels(TradingSignal signal)
        {
            var validationErrors = new List<string>();

            // 🚨 EMERGENCY CHECK 1: Resistance DEVE essere sopra prezzo
            if (signal.ResistanceLevel > 0 && signal.ResistanceLevel <= signal.Price)
            {
                validationErrors.Add($"CRITICAL ERROR: Resistance €{signal.ResistanceLevel:F2} <= Price €{signal.Price:F2} - matematicamente impossibile");
            }

            // 🚨 EMERGENCY CHECK 2: Support DEVE essere sotto prezzo  
            if (signal.SupportLevel > 0 && signal.SupportLevel >= signal.Price)
            {
                validationErrors.Add($"CRITICAL ERROR: Support €{signal.SupportLevel:F2} >= Price €{signal.Price:F2} - matematicamente impossibile");
            }

            // 🚨 EMERGENCY CHECK 3: Take Profit sopra resistance = IMPOSSIBILE
            if (signal.ResistanceLevel > 0 && signal.TakeProfit > signal.ResistanceLevel)
            {
                validationErrors.Add($"CRITICAL ERROR: Take Profit €{signal.TakeProfit:F2} > Resistance €{signal.ResistanceLevel:F2} - target irraggiungibile");
            }

            // 🚨 EMERGENCY CHECK 4: Entry troppo vicino a resistance = BAD TIMING
            if (signal.ResistanceLevel > 0 && signal.Price >= signal.ResistanceLevel * 0.95)
            {
                validationErrors.Add($"BAD TIMING: Entry €{signal.Price:F2} >= 95% di resistance €{signal.ResistanceLevel:F2} - troppo vicino alla resistenza");
            }

            // 🚨 EMERGENCY CHECK 5: Position sizing = 0 è INUTILE
            if (signal.SuggestedShares <= 0)
            {
                validationErrors.Add($"USELESS SIGNAL: Position sizing {signal.SuggestedShares} shares - segnale non tradabile");
            }

            // 🚨 EMERGENCY CHECK 6: Take Profit > +20% = UNREALISTIC  
            if (signal.TakeProfitPercent > 20.0)
            {
                validationErrors.Add($"UNREALISTIC TARGET: Take Profit {signal.TakeProfitPercent:F1}% > 20% - target troppo ambizioso");
            }

            // Resto delle validation esistenti...
            if (signal.Type == SignalType.Buy && signal.StopLoss >= signal.Price)
            {
                validationErrors.Add($"Stop Loss {signal.StopLoss:F2} >= Price {signal.Price:F2} per segnale BUY");
            }

            if (signal.Type == SignalType.Buy && signal.TakeProfit <= signal.Price)
            {
                validationErrors.Add($"Take Profit {signal.TakeProfit:F2} <= Price {signal.Price:F2} per segnale BUY");
            }

            if (signal.StopLossPercent <= 0)
            {
                validationErrors.Add($"Stop Loss Percent {signal.StopLossPercent:F2} <= 0");
            }

            if (signal.TakeProfitPercent <= 0)
            {
                validationErrors.Add($"Take Profit Percent {signal.TakeProfitPercent:F2} <= 0");
            }

            if (signal.RiskRewardRatio <= 0 || signal.RiskRewardRatio > 10)
            {
                validationErrors.Add($"Risk/Reward ratio {signal.RiskRewardRatio:F2} fuori range [0.1-10]");
            }

            // 🚨 LOG DETTAGLIATO per debug
            if (validationErrors.Any())
            {
                _logger.LogError($"🚨🚨🚨 SIGNAL REJECTED for {signal.Symbol} 🚨🚨🚨");
                _logger.LogError($"Entry: €{signal.Price:F2}, Support: €{signal.SupportLevel:F2}, Resistance: €{signal.ResistanceLevel:F2}");
                _logger.LogError($"SL: €{signal.StopLoss:F2} ({signal.StopLossPercent:F1}%), TP: €{signal.TakeProfit:F2} ({signal.TakeProfitPercent:F1}%)");
                _logger.LogError($"Position: {signal.SuggestedShares} shares, R/R: 1:{signal.RiskRewardRatio:F1}");
                _logger.LogError($"VALIDATION ERRORS:");

                foreach (var error in validationErrors)
                {
                    _logger.LogError($"  ❌ {error}");
                }
                return false;
            }

            _logger.LogInformation($"✅ SIGNAL VALIDATION PASSED for {signal.Symbol}");
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
                var historicalData = await _yahooFinance.GetHistoricalDataAsync(symbol, 90);
                var highs = historicalData["h"]?.ToObject<List<double>>() ?? new List<double>();
                var lows = historicalData["l"]?.ToObject<List<double>>() ?? new List<double>();
                var closes = historicalData["c"]?.ToObject<List<double>>() ?? new List<double>();

                // 🔍 DEBUG: Log dati grezzi per capire il problema
                _logger.LogInformation($"🔍 S/R RAW DATA for {symbol}:");
                _logger.LogInformation($"   Data points: {highs.Count} highs, {lows.Count} lows, {closes.Count} closes");

                if (highs.Count >= 5 && lows.Count >= 5 && closes.Count >= 5)
                {
                    _logger.LogInformation($"   Recent highs: {string.Join(", ", highs.Take(5).Select(h => h.ToString("F2")))}");
                    _logger.LogInformation($"   Recent lows: {string.Join(", ", lows.Take(5).Select(l => l.ToString("F2")))}");
                    _logger.LogInformation($"   Recent closes: {string.Join(", ", closes.Take(5).Select(c => c.ToString("F2")))}");
                }

                if (highs.Count < 30 || lows.Count < 30)
                {
                    _logger.LogError($"🚨 Insufficient data for S/R calculation: {symbol} (got {highs.Count}, need 30+)");
                    return (0, 0);
                }

                var currentPrice = closes.First();
                _logger.LogInformation($"💰 Current price for {symbol}: {currentPrice:F2}");

                // 🔧 SUPPORTO: DEVE essere sotto current price
                var validLows = lows.Where(low => low > 0 && low < currentPrice * 0.95).ToList();

                _logger.LogDebug($"🟢 Support candidates (< {currentPrice * 0.95:F2}): {validLows.Count}");
                if (validLows.Count >= 3)
                {
                    _logger.LogDebug($"   Top candidates: {string.Join(", ", validLows.OrderByDescending(l => l).Take(3).Select(l => l.ToString("F2")))}");
                }

                double support = 0;
                if (validLows.Any())
                {
                    // Trova il supporto più alto tra quelli validi
                    support = validLows.Max();
                    _logger.LogInformation($"✅ Support found: {support:F2} ({((support / currentPrice - 1) * 100):F1}% below current)");
                }
                else
                {
                    support = currentPrice * 0.85; // Fallback: 15% sotto
                    _logger.LogWarning($"⚠️ No valid support found, using fallback: {support:F2}");
                }

                // 🔧 RESISTENZA: DEVE essere sopra current price
                var validHighs = highs.Where(high => high > 0 && high > currentPrice * 1.02).ToList();

                _logger.LogDebug($"🔴 Resistance candidates (> {currentPrice * 1.02:F2}): {validHighs.Count}");
                if (validHighs.Count >= 3)
                {
                    _logger.LogDebug($"   Top candidates: {string.Join(", ", validHighs.OrderBy(h => h).Take(3).Select(h => h.ToString("F2")))}");
                }

                double resistance = 0;
                if (validHighs.Any())
                {
                    // Trova la resistenza più vicina (minima tra quelle valide)
                    resistance = validHighs.Min();
                    _logger.LogInformation($"✅ Resistance found: {resistance:F2} ({((resistance / currentPrice - 1) * 100):F1}% above current)");
                }
                else
                {
                    // 🚨 NESSUNA RESISTENZA VALIDA
                    var historicalMax = highs.Max();
                    _logger.LogWarning($"⚠️ No valid resistance found. Historical max: {historicalMax:F2}, Current: {currentPrice:F2}");

                    if (currentPrice >= historicalMax * 0.95) // Entro 5% del massimo storico
                    {
                        _logger.LogWarning($"🚨 {symbol} near/at historical max - no clear resistance");
                        resistance = 0; // Nessuna resistenza chiara
                    }
                    else
                    {
                        resistance = currentPrice * 1.05; // Fallback conservativo: +5%
                        _logger.LogWarning($"⚠️ Using conservative resistance fallback: {resistance:F2}");
                    }
                }

                // 🚨 VALIDATION FINALE CRITICA  
                var errors = new List<string>();

                if (support > 0 && support >= currentPrice)
                {
                    errors.Add($"Support {support:F2} >= Current {currentPrice:F2}");
                }

                if (resistance > 0 && resistance <= currentPrice)
                {
                    errors.Add($"Resistance {resistance:F2} <= Current {currentPrice:F2}");
                }

                if (support > 0 && resistance > 0 && support >= resistance)
                {
                    errors.Add($"Support {support:F2} >= Resistance {resistance:F2}");
                }

                if (errors.Any())
                {
                    _logger.LogError($"🚨 S/R VALIDATION FAILED for {symbol}:");
                    foreach (var error in errors)
                    {
                        _logger.LogError($"   ❌ {error}");
                    }

                    // 🔍 DEBUG: Mostra più dati per capire il problema
                    _logger.LogError($"🔍 DEBUG INFO:");
                    _logger.LogError($"   All highs range: {highs.Min():F2} - {highs.Max():F2}");
                    _logger.LogError($"   All lows range: {lows.Min():F2} - {lows.Max():F2}");
                    _logger.LogError($"   Current price: {currentPrice:F2}");
                    _logger.LogError($"   Valid highs count: {validHighs.Count}");
                    _logger.LogError($"   Valid lows count: {validLows.Count}");

                    return (0, 0); // Ritorna livelli nulli
                }

                // ✅ SUCCESS
                _logger.LogInformation($"✅ VALID S/R for {symbol}: Support={support:F2}, Resistance={resistance:F2}, Current={currentPrice:F2}");
                return (support, resistance);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error calculating S/R for {symbol}", symbol);
                return (0, 0);
            }
        }

        private double GetDynamicStopLossPercent(double confidence)
        {
            return confidence switch
            {
                >= 90 => 3.0,   // Molto fiducioso = SL stretto
                >= 80 => 4.0,
                >= 70 => 5.0,
                >= 60 => 6.0,
                _ => 7.0        // Poco fiducioso = SL più largo
            };
        }

        private double GetDynamicTakeProfitPercent(double confidence)
        {
            return confidence switch
            {
                >= 90 => 10.0,  // 🔧 Ridotto da 20% a 10%
                >= 80 => 8.0,   // 🔧 Ridotto da 15% a 8%
                >= 70 => 6.0,   // 🔧 Ridotto da 12% a 6%
                >= 60 => 5.0,   // 🔧 Ridotto da 10% a 5%
                _ => 4.0        // 🔧 Ridotto da 8% a 4%
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
