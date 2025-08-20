using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using PortfolioSignalWorker.Models;
using PortfolioSignalWorker.Services;
using static PortfolioSignalWorker.Services.SmartMarketHoursService;

public class SimplifiedEnhancedWorker : BackgroundService
{
    private readonly YahooFinanceService _yahooFinance;
    private readonly TelegramService _telegram;
    private readonly MongoService _mongo;
    private readonly SimplifiedEnhancedSignalFilterService _enhancedSignalFilter;
    private readonly SimplifiedEnhancedRiskManagementService _enhancedRiskManagement;
    private readonly SymbolSelectionService _symbolSelection;
    private readonly SmartMarketHoursService _smartMarketHours;
    private readonly ILogger<SimplifiedEnhancedWorker> _logger;

    // Performance tracking
    private int _totalAnalyzed = 0;
    private int _signalsSent = 0;
    private int _strongSignals = 0;
    private int _mediumSignals = 0;
    private int _warningSignals = 0;
    private int _dataDrivenSignals = 0; // 🆕 NUOVO: Counter per segnali data-driven
    private DateTime _lastOptimization = DateTime.MinValue;

    public SimplifiedEnhancedWorker(
        YahooFinanceService yahooFinance,
        TelegramService telegram,
        MongoService mongo,
        SimplifiedEnhancedSignalFilterService enhancedSignalFilter,
        SimplifiedEnhancedRiskManagementService enhancedRiskManagement,
        SymbolSelectionService symbolSelection,
        SmartMarketHoursService smartMarketHours,
        ILogger<SimplifiedEnhancedWorker> logger)
    {
        _yahooFinance = yahooFinance;
        _telegram = telegram;
        _mongo = mongo;
        _enhancedSignalFilter = enhancedSignalFilter;
        _enhancedRiskManagement = enhancedRiskManagement;
        _symbolSelection = symbolSelection;
        _smartMarketHours = smartMarketHours;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🚀 Enhanced Trading System v2.0 with DATA-DRIVEN Take Profit - Started!");
        _logger.LogInformation("✅ Multi-confluence signal analysis");
        _logger.LogInformation("✅ Smart risk management with ATR");
        _logger.LogInformation("✅ Structural support/resistance detection");
        _logger.LogInformation("✅ Enhanced market context analysis");
        _logger.LogInformation("🧠 DATA-DRIVEN Take Profit optimization"); // 🆕 NUOVO

        // Initialize watchlist
        var watchlistCount = await _mongo.GetWatchlistCount();
        if (watchlistCount == 0)
        {
            _logger.LogInformation("Initializing watchlist...");
            await _symbolSelection.InitializeWatchlist();
        }

        _smartMarketHours.LogCurrentMarketStatus();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cycleStartTime = DateTime.UtcNow;

                // Get symbols due for analysis
                var symbolsDue = await _symbolSelection.GetSymbolsDueForAnalysis();

                // Filter symbols based on market hours and enhanced criteria
                var symbolsToProcess = FilterSymbolsForEnhancedAnalysis(symbolsDue);

                if (!symbolsToProcess.Any())
                {
                    _logger.LogInformation("📊 No symbols ready for enhanced analysis");
                    await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);
                    continue;
                }

                _logger.LogInformation($"🎯 Analyzing {symbolsToProcess.Count} symbols with enhanced + data-driven logic");

                // Process symbols with enhanced analysis
                var processedCount = 0;
                var signalsSentThisCycle = 0;

                foreach (var (symbol, analysisMode) in symbolsToProcess)
                {
                    try
                    {
                        var result = await ProcessSymbolWithEnhancedLogic(symbol, analysisMode);

                        if (result.signalSent)
                        {
                            signalsSentThisCycle++;
                            UpdatePerformanceCounters(result.signal);
                        }

                        processedCount++;
                        _totalAnalyzed++;

                        // Adaptive delay based on analysis mode
                        var delay = analysisMode switch
                        {
                            AnalysisMode.FullAnalysis => 800,      // Market hours
                            AnalysisMode.PreMarketWatch => 1000,   // Pre-market
                            AnalysisMode.OffHoursMonitor => 1200,  // Off-hours
                            _ => 1000
                        };

                        await Task.Delay(delay, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing {symbol}", symbol.Symbol);
                    }
                }

                var cycleTime = (DateTime.UtcNow - cycleStartTime).TotalSeconds;
                _logger.LogInformation($"📈 Cycle completed in {cycleTime:F1}s: " +
                    $"{processedCount} analyzed, {signalsSentThisCycle} signals sent");

                // 🆕 NUOVO: Aggiorna performance tracking every hour
                await CheckHourlyMaintenance();

                // Daily optimization
                await CheckDailyOptimization();

                // Adaptive wait time
                var waitTime = CalculateWaitTime(symbolsToProcess);
                await Task.Delay(waitTime, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in main worker loop");
                await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            }
        }
    }

    private List<(WatchlistSymbol symbol, AnalysisMode mode)> FilterSymbolsForEnhancedAnalysis(
        List<WatchlistSymbol> symbolsDue)
    {
        var symbolsToProcess = new List<(WatchlistSymbol symbol, AnalysisMode mode)>();
        var skippedCount = 0;

        foreach (var symbol in symbolsDue)
        {
            var analysisMode = _smartMarketHours.GetAnalysisMode(symbol.Symbol);

            if (analysisMode == AnalysisMode.Skip)
            {
                skippedCount++;
                continue;
            }

            // Enhanced filtering: skip poor performers (except core symbols)
            if (!symbol.IsCore && symbol.SuccessRate < 25 && symbol.SignalsGenerated >= 8)
            {
                skippedCount++;
                _logger.LogDebug($"Skipping {symbol.Symbol} - poor performance (SR: {symbol.SuccessRate}%)");
                continue;
            }

            symbolsToProcess.Add((symbol, analysisMode));
        }

        // Log distribution
        if (symbolsToProcess.Any())
        {
            var distribution = symbolsToProcess.GroupBy(x => x.mode).ToDictionary(g => g.Key, g => g.Count());
            _logger.LogInformation($"📊 Analysis modes: {string.Join(", ", distribution.Select(d => $"{d.Key}:{d.Value}"))} (skipped: {skippedCount})");
        }

        return symbolsToProcess;
    }

    private async Task<(bool signalSent, TradingSignal signal)> ProcessSymbolWithEnhancedLogic(
    WatchlistSymbol watchlistSymbol, AnalysisMode analysisMode)
    {
        try
        {
            // 1. Get indicators with enhanced calculations
            _logger.LogDebug($"🔍 Getting indicators for {watchlistSymbol.Symbol}");
            var indicator = await _yahooFinance.GetIndicatorsAsync(watchlistSymbol.Symbol);

            // 2. Enhanced signal analysis with confluence
            _logger.LogDebug($"🔍 Starting enhanced analysis for {watchlistSymbol.Symbol}");
            var signal = await _enhancedSignalFilter.AnalyzeEnhancedSignalAsync(watchlistSymbol.Symbol, indicator);

            // 3. Save indicator data
            await _mongo.SaveIndicatorAsync(indicator);

            if (signal != null)
            {
                _logger.LogInformation($"🎯 Signal generated for {watchlistSymbol.Symbol}: {signal.Type} (Confidence: {signal.Confidence}%)");

                try
                {
                    // 🔧 NUOVO ORDINE: 4. Apply Risk Management PRIMA delle validazioni!
                    _logger.LogInformation($"🧠 Calculating DATA-DRIVEN risk management for {watchlistSymbol.Symbol}");
                    signal = await _enhancedRiskManagement.EnhanceSignalWithDataDrivenRiskManagement(signal);

                    if (signal == null)
                    {
                        _logger.LogWarning($"⚠️ Signal for {watchlistSymbol.Symbol} became NULL after risk management");
                        return (false, null);
                    }

                    // Log DOPO il risk management
                    _logger.LogInformation($"🎯 Risk Management completed for {watchlistSymbol.Symbol}: " +
                        $"SL: ${signal.StopLoss?.ToString("F2") ?? "NULL"} ({signal.StopLossPercent?.ToString("F1") ?? "NULL"}%), " +
                        $"TP: ${signal.TakeProfit?.ToString("F2") ?? "NULL"} ({signal.TakeProfitPercent?.ToString("F1") ?? "NULL"}%), " +
                        $"R/R: 1:{signal.RiskRewardRatio?.ToString("F1") ?? "NULL"}");

                    // 5. DOPO risk management: Log validation details
                    LogSignalValidationDetails(signal, analysisMode);

                    // 6. DOPO risk management: Validate signal based on analysis mode  
                    var shouldSend = ValidateSignalForMode(signal, analysisMode);
                    _logger.LogInformation($"🔍 ValidateSignalForMode result for {watchlistSymbol.Symbol}: {shouldSend}");

                    if (shouldSend)
                    {
                        // 7. Final quality checks
                        var passesQualityChecks = await PassesFinalQualityChecks(signal, watchlistSymbol);
                        _logger.LogInformation($"🔍 PassesFinalQualityChecks result for {watchlistSymbol.Symbol}: {passesQualityChecks}");

                        if (passesQualityChecks)
                        {
                            // LOG SUCCESS
                            _logger.LogInformation($"🚀 SENDING SIGNAL for {watchlistSymbol.Symbol}: {signal.Type} at {signal.Confidence}%");

                            try
                            {
                                // 8. Save and send
                                await _mongo.SaveSignalAsync(signal);
                                var message = FormatDataDrivenEnhancedMessage(signal, analysisMode, watchlistSymbol);
                                await _telegram.SendMessageAsync(message);
                                await _enhancedSignalFilter.MarkSignalAsSentAsync(signal.Id);

                                _signalsSent++;

                                // Log con info data-driven
                                var dataDrivenInfo = !string.IsNullOrEmpty(signal.TakeProfitStrategy) ?
                                    $" | Strategy: {signal.TakeProfitStrategy}" : "";

                                _logger.LogInformation($"✅ Enhanced {signal.Type} signal sent for {watchlistSymbol.Symbol}: " +
                                    $"Confidence: {signal.Confidence}%, R/R: 1:{signal.RiskRewardRatio?.ToString("F1") ?? "N/A"}{dataDrivenInfo}");

                                // Update symbol performance
                                await UpdateSymbolAnalysisTime(watchlistSymbol.Symbol, analysisMode, true);

                                return (true, signal);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, $"💥 Failed to send signal for {watchlistSymbol.Symbol}");
                                return (false, signal);
                            }
                        }
                        else
                        {
                            _logger.LogWarning($"❌ Signal for {watchlistSymbol.Symbol} FAILED PassesFinalQualityChecks");
                        }
                    }
                    else
                    {
                        _logger.LogWarning($"❌ Signal for {watchlistSymbol.Symbol} FAILED ValidateSignalForMode");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"💥 Error during risk management or validation for {watchlistSymbol.Symbol}");
                    return (false, signal);
                }
            }
            else
            {
                _logger.LogDebug($"🔍 No signal generated for {watchlistSymbol.Symbol}");
            }

            // Update next analysis time
            await UpdateSymbolAnalysisTime(watchlistSymbol.Symbol, analysisMode, false);
            return (false, signal);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in enhanced processing for {symbol}", watchlistSymbol.Symbol);
            return (false, null);
        }
    }

    private void LogSignalValidationDetails(TradingSignal signal, AnalysisMode mode)
    {
        if (signal == null)
        {
            _logger.LogWarning("🔍 VALIDATION DEBUG: signal is NULL!");
            return;
        }

        _logger.LogInformation($"🔍 QUALITY VALIDATION for {signal.Symbol ?? "UNKNOWN"}:");
        _logger.LogInformation($"   Type: {signal.Type}, Confidence: {signal.Confidence}%");
        _logger.LogInformation($"   Mode: {mode}");
        _logger.LogInformation($"   Price: ${signal.Price:F2}");

        // 🔧 MIGLIORATO: Controlli post risk-management
        _logger.LogInformation($"   StopLoss: ${(signal.StopLoss?.ToString("F2") ?? "MISSING")} ({(signal.StopLossPercent?.ToString("F1") ?? "NULL")}%)");
        _logger.LogInformation($"   TakeProfit: ${(signal.TakeProfit?.ToString("F2") ?? "MISSING")} ({(signal.TakeProfitPercent?.ToString("F1") ?? "NULL")}%)");
        _logger.LogInformation($"   R/R Ratio: {(signal.RiskRewardRatio?.ToString("F1") ?? "NULL")} (Required: 2.0+)");

        // 🔧 CRITICO: Log i valori che spesso causano fallimenti
        _logger.LogInformation($"   Volume Strength: {(signal.VolumeStrength?.ToString("F1") ?? "NULL")} (Required: 5+ for Buy)");
        _logger.LogInformation($"   Trend Strength: {(signal.TrendStrength?.ToString("F1") ?? "NULL")} (Required: 6+ if conf≥80%)");
        _logger.LogInformation($"   Market Condition: {signal.MarketCondition ?? "NULL"}");

        // Livelli tecnici
        _logger.LogInformation($"   Support Level: ${(signal.SupportLevel?.ToString("F2") ?? "NULL")}");
        _logger.LogInformation($"   Resistance Level: ${(signal.ResistanceLevel?.ToString("F2") ?? "NULL")}");

        var threshold = _smartMarketHours.GetConfidenceThreshold(mode);
        _logger.LogInformation($"   Mode Threshold: {threshold}%");

        // Motivo del segnale e strategia
        _logger.LogInformation($"   Reason: {signal.Reason ?? "No reason specified"}");
        _logger.LogInformation($"   TP Strategy: {signal.TakeProfitStrategy ?? "Standard"}");
    }

    private bool ValidateSignalForMode(TradingSignal signal, AnalysisMode mode)
    {
        var threshold = _smartMarketHours.GetConfidenceThreshold(mode);

        // Basic confidence check
        if (signal.Confidence < threshold) return false;

        // Mode-specific validations
        return mode switch
        {
            AnalysisMode.OffHoursMonitor =>
                signal.Confidence >= 85 &&
                (signal.Type == SignalType.Buy || signal.Type == SignalType.Warning) &&
                (signal.RSI < 25 || signal.RSI > 75), // Only extreme conditions

            AnalysisMode.PreMarketWatch =>
                signal.Confidence >= 75 &&
                (signal.Type == SignalType.Buy || signal.Type == SignalType.Warning),

            AnalysisMode.FullAnalysis =>
                ValidateFullAnalysisSignal(signal),

            _ => true
        };
    }
    private bool ValidateFullAnalysisSignal(TradingSignal signal)
    {
        var issues = new List<string>();

        // 1. Confidence SELETTIVA
        if (signal.Confidence < 65)
        {
            issues.Add($"Confidence {signal.Confidence}% < 65%");
        }

        // 2. Risk/Reward QUALITATIVO - RIGOROSO
        if (signal.RiskRewardRatio.HasValue && signal.RiskRewardRatio < 2.0)
        {
            issues.Add($"R/R {signal.RiskRewardRatio:F1} < 2.0");
        }
        else if (!signal.RiskRewardRatio.HasValue)
        {
            issues.Add("R/R MISSING");
        }

        // 3. Volume CONFERMATO per Buy signals - RIGOROSO
        if (signal.Type == SignalType.Buy && signal.VolumeStrength.HasValue && signal.VolumeStrength < 5)
        {
            issues.Add($"Volume {signal.VolumeStrength} < 5 (Buy signal)");
        }
        else if (signal.Type == SignalType.Buy && !signal.VolumeStrength.HasValue)
        {
            issues.Add("Volume MISSING (Buy signal)");
        }

        // 4. Trend POSITIVO per high confidence signals
        if (signal.Confidence >= 80 && signal.TrendStrength.HasValue && signal.TrendStrength < 6)
        {
            issues.Add($"Weak trend {signal.TrendStrength} < 6 (High confidence signal)");
        }

        // 5. Strong Buy deve avere trend rialzista
        if (signal.Confidence >= 85 && signal.MarketCondition?.Contains("Bearish") == true)
        {
            issues.Add($"Bearish market condition with {signal.Confidence}% confidence");
        }

        // 6. 🔧 NUOVO: Verifica che Stop Loss e Take Profit esistano DOPO risk management
        if (!signal.StopLoss.HasValue)
        {
            issues.Add("Stop Loss MISSING after risk management");
        }

        if (!signal.TakeProfit.HasValue)
        {
            issues.Add("Take Profit MISSING after risk management");
        }

        // 🔧 LOGGING DETTAGLIATO SEMPRE
        _logger.LogInformation($"🔍 ValidateFullAnalysisSignal for {signal.Symbol}:");
        _logger.LogInformation($"   ✅ CHECKS:");
        _logger.LogInformation($"      Confidence: {signal.Confidence}% (Required: ≥65%) = {(signal.Confidence >= 65 ? "✅ PASS" : "❌ FAIL")}");
        _logger.LogInformation($"      R/R Ratio: {signal.RiskRewardRatio?.ToString("F1") ?? "NULL"} (Required: ≥2.0) = {(signal.RiskRewardRatio >= 2.0 ? "✅ PASS" : "❌ FAIL")}");
        _logger.LogInformation($"      Volume: {signal.VolumeStrength?.ToString("F1") ?? "NULL"} (Required: ≥5 for Buy) = {(signal.Type != SignalType.Buy || signal.VolumeStrength >= 5 ? "✅ PASS" : "❌ FAIL")}");
        _logger.LogInformation($"      Trend: {signal.TrendStrength?.ToString("F1") ?? "NULL"} (Required: ≥6 if conf≥80%) = {(signal.Confidence < 80 || signal.TrendStrength >= 6 ? "✅ PASS" : "❌ FAIL")}");
        _logger.LogInformation($"      Market: {signal.MarketCondition ?? "NULL"} (No Bearish if conf≥85%) = {(signal.Confidence < 85 || !signal.MarketCondition?.Contains("Bearish") == true ? "✅ PASS" : "❌ FAIL")}");
        _logger.LogInformation($"      SL/TP: SL={signal.StopLoss?.ToString("F2") ?? "NULL"}, TP={signal.TakeProfit?.ToString("F2") ?? "NULL"} = {(signal.StopLoss.HasValue && signal.TakeProfit.HasValue ? "✅ PASS" : "❌ FAIL")}");

        // LOG RISULTATO
        if (issues.Any())
        {
            _logger.LogWarning($"❌ {signal.Symbol} FAILED ValidateFullAnalysisSignal:");
            foreach (var issue in issues)
            {
                _logger.LogWarning($"   • {issue}");
            }
            return false;
        }

        _logger.LogInformation($"✅ {signal.Symbol} PASSED ValidateFullAnalysisSignal");
        return true;
    }
    private async Task<bool> PassesFinalQualityChecks(TradingSignal signal, WatchlistSymbol symbol)
    {
        // 🔧 CONTROLLI QUALITÀ RIGOROSI - Solo segnali validi passano

        if (signal == null)
        {
            _logger.LogWarning("PassesFinalQualityChecks: signal is NULL");
            return false;
        }

        try
        {
            // 1. Price level VALIDATION - RIGOROSA
            if (signal.Price <= 0)
            {
                _logger.LogWarning($"Invalid price rejected: ${signal.Price:F2}");
                return false;
            }

            // 2. Stop Loss VALIDATION - RIGOROSA
            if (signal.StopLoss.HasValue)
            {
                if (signal.StopLoss >= signal.Price)
                {
                    _logger.LogWarning($"Invalid StopLoss rejected: Entry=${signal.Price:F2}, SL=${signal.StopLoss:F2}");
                    return false; // 🔧 RIGETTA segnali con SL invalidi
                }
            }
            else if (signal.Type == SignalType.Buy)
            {
                _logger.LogWarning($"Buy signal rejected: Missing StopLoss for {signal.Symbol}");
                return false; // 🔧 Buy DEVE avere stop loss
            }

            // 3. Take Profit VALIDATION - RIGOROSA
            if (signal.TakeProfit.HasValue)
            {
                if (signal.TakeProfit <= signal.Price)
                {
                    _logger.LogWarning($"Invalid TakeProfit rejected: Entry=${signal.Price:F2}, TP=${signal.TakeProfit:F2}");
                    return false; // 🔧 RIGETTA segnali con TP invalidi
                }
            }
            else if (signal.Type == SignalType.Buy)
            {
                _logger.LogWarning($"Buy signal rejected: Missing TakeProfit for {signal.Symbol}");
                return false; // 🔧 Buy DEVE avere take profit
            }

            // 4. Stop loss RAGIONEVOLE
            if (signal.StopLossPercent.HasValue && signal.StopLossPercent > 8) // 🔧 Max 8% per qualità
            {
                _logger.LogWarning($"Stop loss too wide rejected: {signal.StopLossPercent:F1}% > 8%");
                return false;
            }

            // 5. Take profit REALISTICO
            if (signal.TakeProfitPercent.HasValue && signal.TakeProfitPercent > 20) // 🔧 Max 20%
            {
                _logger.LogWarning($"Take profit too optimistic rejected: {signal.TakeProfitPercent:F1}% > 20%");
                return false;
            }

            // 🔧 RIMOSSO: Check daily limits globali 
            // var dailySignalCount = await GetDailySignalCount();
            // if (dailySignalCount >= 10) // RIMOSSO: Non più limite globale!
            // {
            //     _logger.LogWarning($"Daily signal limit reached: {dailySignalCount}/10 - maintaining quality");
            //     return false;
            // }

            // ✅ Il limite per simbolo è già gestito in IsSymbolEligibleForSignal nel SimplifiedEnhancedSignalFilterService
            // Non servono controlli aggiuntivi qui

            _logger.LogDebug($"✅ {signal.Symbol}: Passed RIGOROUS final quality checks (NO GLOBAL LIMIT)");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error in PassesFinalQualityChecks for {signal?.Symbol ?? "UNKNOWN"}");
            return false;
        }
    }
     

    // 🆕 NUOVO: Messaggio potenziato con info data-driven
    private string FormatDataDrivenEnhancedMessage(TradingSignal signal, AnalysisMode mode, WatchlistSymbol symbol)
    {
        return _telegram.FormatTradingSignalMessage(signal, mode, symbol);
        var modeEmoji = mode switch
        {
            AnalysisMode.FullAnalysis => "🟢",
            AnalysisMode.PreMarketWatch => "🟡",
            AnalysisMode.OffHoursMonitor => "🟠",
            _ => "⚪"
        };

        var signalEmoji = (signal.Type, signal.Confidence) switch
        {
            (SignalType.Buy, >= 90) => "🚀",
            (SignalType.Buy, >= 80) => "📈",
            (SignalType.Buy, _) => "📊",
            (SignalType.Warning, _) => "⚠️",
            (SignalType.Sell, _) => "📉",
            _ => "ℹ️"
        };

        var marketFlag = (symbol.Market ?? "US") switch
        {
            "EU" => "🇪🇺",
            "US" => "🇺🇸",
            _ => "🌍"
        };

        var currency = GetCurrencySymbol(signal.Symbol, symbol.Market ?? "US");
        var marketStatus = _smartMarketHours.GetModeDescription(mode, signal.Symbol);

        // 🆕 NUOVO: Indicator data-driven
        var dataDrivenIndicator = !string.IsNullOrEmpty(signal.TakeProfitStrategy) ? " 🧠" : "";
        var title = $"{modeEmoji} {signalEmoji} {signal.Type.ToString().ToUpper()} {signal.Symbol} {marketFlag}{dataDrivenIndicator}";

        var message = $@"{title}

🎯 Confidence: {signal.Confidence}% | {signal.MarketCondition}";

        // 🆕 NUOVO: Aggiungi info strategia se disponibile
        if (!string.IsNullOrEmpty(signal.TakeProfitStrategy))
        {
            message += $@"
🧠 TP Strategy: {signal.TakeProfitStrategy}";

            if (signal.PredictedSuccessProbability.HasValue)
            {
                message += $" ({signal.PredictedSuccessProbability:F0}% success rate)";
            }
        }

        message += $@"
📊 RSI: {signal.RSI:F1} | MACD: {signal.MACD_Histogram:F3}
💰 Entry: {currency}{signal.Price:F2}
📈 Volume: {FormatVolume(signal.Volume)} ({signal.VolumeStrength:F1}/10)
🎢 Trend: {signal.TrendStrength:F1}/10

🛡️ SMART RISK MANAGEMENT:
🔻 Stop Loss: {currency}{signal.StopLoss:F2} ({signal.StopLossPercent:F1}%)
🎯 Take Profit: {currency}{signal.TakeProfit:F2} ({signal.TakeProfitPercent:F1}%)
⚖️ Risk/Reward: 1:{signal.RiskRewardRatio:F1}";

        // Add support/resistance if available
        if (signal.SupportLevel.HasValue && signal.ResistanceLevel.HasValue &&
            signal.SupportLevel > 0 && signal.ResistanceLevel > 0)
        {
            message += $@"

📈 KEY LEVELS:
🟢 Support: {currency}{signal.SupportLevel:F2}
🔴 Resistance: {currency}{signal.ResistanceLevel:F2}";
        }

        // Add enhanced strategy info
        if (!string.IsNullOrEmpty(signal.EntryStrategy))
        {
            message += $@"

🎯 ENTRY: {signal.EntryStrategy}";
        }

        if (!string.IsNullOrEmpty(signal.ExitStrategy))
        {
            message += $@"

🚪 EXIT: {signal.ExitStrategy}";
        }

        message += $@"

🕐 STATUS: {marketStatus}
💡 {signal.Reason}

⏰ {DateTime.Now:HH:mm} {modeEmoji} Enhanced v2.0{dataDrivenIndicator}";

        return message;
    }

    private async Task UpdateSymbolAnalysisTime(string symbol, AnalysisMode mode, bool signalGenerated)
    {
        DateTime nextAnalysis;

        switch (mode)
        {
            case AnalysisMode.FullAnalysis:
                // 🟢 Mercato aperto - usa frequenza normale
                var frequency = _smartMarketHours.GetAnalysisFrequency(mode, SymbolTier.Tier2_Standard);
                if (signalGenerated)
                {
                    frequency = TimeSpan.FromTicks((long)(frequency.Ticks * 1.5)); // Wait longer after signal
                }
                nextAnalysis = DateTime.UtcNow.Add(frequency);
                break;

            case AnalysisMode.PreMarketWatch:
                // 🟡 Pre-market - analisi frequente
                nextAnalysis = DateTime.UtcNow.AddMinutes(signalGenerated ? 60 : 30);
                break;

            case AnalysisMode.OffHoursMonitor:
            case AnalysisMode.Skip:
                // 🔧 CRITICO: Quando mercato chiuso, analizza all'APERTURA del mercato!
                nextAnalysis = CalculateNextMarketOpenTime(symbol);
                break;

            default:
                nextAnalysis = DateTime.UtcNow.AddHours(2);
                break;
        }

        // 🔧 NUOVO: Log della decisione
        var timeDiff = nextAnalysis - DateTime.UtcNow;
        _logger.LogDebug($"⏰ {symbol} next analysis: {nextAnalysis:HH:mm:ss} " +
            $"(in {timeDiff.TotalMinutes:F0}m) - Mode: {mode}");

        await _symbolSelection.UpdateSymbolNextAnalysis(symbol, nextAnalysis);
    }

    private DateTime CalculateNextMarketOpenTime(string symbol)
    {
        try
        {
            var utcNow = DateTime.UtcNow;
            var isOpen = _smartMarketHours.IsMarketOpen(symbol);

            if (isOpen)
            {
                // Se il mercato è aperto ora, analizza tra 2 ore
                return utcNow.AddHours(2);
            }

            var timeUntilOpen = _smartMarketHours.GetTimeUntilMarketOpen(symbol);
            var marketOpenTime = utcNow.Add(timeUntilOpen);

            // 🔧 STRATEGIA INTELLIGENTE per NextAnalysis:
            if (timeUntilOpen.TotalHours <= 2)
            {
                // 🟡 Mercato apre tra meno di 2 ore → Analizza 5 minuti DOPO l'apertura
                var analysisTime = marketOpenTime.AddMinutes(5);
                _logger.LogInformation($"📅 {symbol}: Market opens in {timeUntilOpen.TotalMinutes:F0}m, " +
                    $"scheduling analysis for {analysisTime:HH:mm:ss} (5m after open)");
                return analysisTime;
            }
            else if (timeUntilOpen.TotalHours <= 12)
            {
                // 🟠 Mercato apre tra 2-12 ore → Analizza 30 minuti prima dell'apertura (pre-market)
                var preMarketTime = marketOpenTime.AddMinutes(-30);
                _logger.LogInformation($"📅 {symbol}: Market opens in {timeUntilOpen.TotalHours:F1}h, " +
                    $"scheduling pre-market analysis for {preMarketTime:HH:mm:ss}");
                return preMarketTime;
            }
            else
            {
                // 🚫 Mercato apre tra più di 12 ore (weekend) → Analizza 2 ore prima dell'apertura
                var weekendPrep = marketOpenTime.AddHours(-2);
                _logger.LogInformation($"📅 {symbol}: Market opens in {timeUntilOpen.TotalHours:F1}h (weekend), " +
                    $"scheduling weekend prep for {weekendPrep:HH:mm:ss}");
                return weekendPrep;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error calculating next market open for {symbol}");
            // Fallback: analizza tra 1 ora
            return DateTime.UtcNow.AddHours(1);
        }
    }

    // 🆕 NUOVO: Manutenzione oraria per performance tracking
    private async Task CheckHourlyMaintenance()
    {
        if (DateTime.Now.Minute == 0) // All'inizio di ogni ora
        {
            try
            {
                await _enhancedRiskManagement.UpdateDataDrivenPerformance();

                // Report settimanale (lunedì mattina)
                if (DateTime.Now.DayOfWeek == DayOfWeek.Monday && DateTime.Now.Hour == 9)
                {
                    var insights = await _enhancedRiskManagement.GetDataDrivenInsights();
                    if (insights.HasSufficientData)
                    {
                        var reportMessage = $"📊 WEEKLY TP INSIGHTS:\n" +
                                           $"Analyzed: {insights.AnalyzedRecords} trades\n" +
                                           $"Top recommendations:\n" +
                                           string.Join("\n", insights.Recommendations.Take(3).Select(r => $"• {r}"));

                        await _telegram.SendMessageAsync(reportMessage);
                        _logger.LogInformation("📈 Weekly Take Profit insights sent");
                    }
                }

                _logger.LogInformation("⏰ Hourly maintenance completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in hourly maintenance");
            }
        }
    }

    private async Task CheckDailyOptimization()
    {
        var now = DateTime.UtcNow;

        if ((now.Hour == 0 && now.Minute < 15) ||
            (now - _lastOptimization).TotalHours >= 24)
        {
            _logger.LogInformation("🔄 Starting daily optimization...");

            await _symbolSelection.OptimizeWatchlist();
            LogDailyPerformance();
            ResetDailyCounters();

            _lastOptimization = now;
            _smartMarketHours.LogCurrentMarketStatus();
        }
    }

    private TimeSpan CalculateWaitTime(List<(WatchlistSymbol symbol, AnalysisMode mode)> processedSymbols)
    {
        var hasActiveMarket = processedSymbols.Any(x => x.mode == AnalysisMode.FullAnalysis);

        if (hasActiveMarket)
        {
            return TimeSpan.FromMinutes(4); // Shorter wait during active market
        }
        else
        {
            return TimeSpan.FromMinutes(10); // Longer wait when markets closed
        }
    }

    // 🆕 AGGIORNATO: Counter per segnali data-driven
    private void UpdatePerformanceCounters(TradingSignal signal)
    {
        if (signal?.Type == SignalType.Buy)
        {
            if (signal.Confidence >= 90) _strongSignals++;
            else _mediumSignals++;
        }
        else if (signal?.Type == SignalType.Warning)
        {
            _warningSignals++;
        }

        // 🆕 NUOVO: Counter per segnali data-driven
        if (!string.IsNullOrEmpty(signal?.TakeProfitStrategy))
        {
            _dataDrivenSignals++;
        }
    }

    // 🆕 AGGIORNATO: Performance log con info data-driven
    private void LogDailyPerformance()
    {
        _logger.LogInformation("📊 DAILY PERFORMANCE SUMMARY:");
        _logger.LogInformation($"  Symbols analyzed: {_totalAnalyzed}");
        _logger.LogInformation($"  Total signals sent: {_signalsSent} (NO GLOBAL LIMIT ✅)");
        _logger.LogInformation($"  Strong signals: {_strongSignals}");
        _logger.LogInformation($"  Medium signals: {_mediumSignals}");
        _logger.LogInformation($"  Warning signals: {_warningSignals}");
        _logger.LogInformation($"  Data-driven signals: {_dataDrivenSignals}");

        if (_totalAnalyzed > 0)
        {
            var signalRate = (_signalsSent * 100.0) / _totalAnalyzed;
            var dataDrivenRate = (_dataDrivenSignals * 100.0) / Math.Max(_signalsSent, 1);
            _logger.LogInformation($"  Signal generation rate: {signalRate:F1}%");
            _logger.LogInformation($"  Data-driven rate: {dataDrivenRate:F1}%");
            _logger.LogInformation($"  🎯 LIMIT POLICY: Max 2 signals per symbol per day, NO global limit");
        }
    }

    // 🆕 AGGIORNATO: Reset con nuovo counter
    private void ResetDailyCounters()
    {
        _totalAnalyzed = 0;
        _signalsSent = 0;
        _strongSignals = 0;
        _mediumSignals = 0;
        _warningSignals = 0;
        _dataDrivenSignals = 0; // 🆕 NUOVO
    }

    private string GetCurrencySymbol(string symbol, string market)
    {
        if (symbol.Contains(".MI") || symbol.Contains(".AS") ||
            symbol.Contains(".DE") || symbol.Contains(".PA"))
            return "€";

        if (symbol.Contains(".SW"))
            return "CHF ";

        if (symbol.Contains(".L"))
            return "£";

        return "$";
    }

    private string FormatVolume(long volume)
    {
        return volume switch
        {
            >= 1_000_000_000 => $"{volume / 1_000_000_000.0:F1}B",
            >= 1_000_000 => $"{volume / 1_000_000.0:F1}M",
            >= 1_000 => $"{volume / 1_000.0:F1}K",
            _ => volume.ToString("N0")
        };
    }
}