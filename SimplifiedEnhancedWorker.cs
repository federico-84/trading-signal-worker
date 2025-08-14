using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
            _logger.LogDebug($"🔍 Got indicators: RSI={indicator.RSI:F1}, MACD={indicator.MACD_Histogram:F3}");

            // 2. Enhanced signal analysis with confluence
            _logger.LogDebug($"🔍 Starting enhanced analysis for {watchlistSymbol.Symbol}");
            var signal = await _enhancedSignalFilter.AnalyzeEnhancedSignalAsync(watchlistSymbol.Symbol, indicator);

            if (signal != null)
            {
                _logger.LogInformation($"🎯 Signal generated for {watchlistSymbol.Symbol}: {signal.Type} (Confidence: {signal.Confidence}%)");
            }
            else
            {
                _logger.LogDebug($"🔍 No signal generated for {watchlistSymbol.Symbol}");
            }

            // 3. Save indicator data
            await _mongo.SaveIndicatorAsync(indicator);

            if (signal != null)
            {
                // 4. Validate signal based on analysis mode
                var shouldSend = ValidateSignalForMode(signal, analysisMode);

                if (shouldSend)
                {
                    // 🆕 5. Apply DATA-DRIVEN enhanced risk management
                    signal = await _enhancedRiskManagement.EnhanceSignalWithDataDrivenRiskManagement(signal);

                    // 6. Final quality checks
                    if (PassesFinalQualityChecks(signal, watchlistSymbol))
                    {
                        // 7. Save and send
                        await _mongo.SaveSignalAsync(signal);

                        var message = FormatDataDrivenEnhancedMessage(signal, analysisMode, watchlistSymbol);
                        await _telegram.SendMessageAsync(message);
                        await _enhancedSignalFilter.MarkSignalAsSentAsync(signal.Id);

                        _signalsSent++;

                        // 🆕 NUOVO: Log con info data-driven
                        var dataDrivenInfo = !string.IsNullOrEmpty(signal.TakeProfitStrategy) ?
                            $" | Strategy: {signal.TakeProfitStrategy}" : "";

                        _logger.LogInformation($"✅ Enhanced {signal.Type} signal sent for {watchlistSymbol.Symbol}: " +
                            $"Confidence: {signal.Confidence}%, R/R: 1:{signal.RiskRewardRatio:F1}{dataDrivenInfo}");

                        // Update symbol performance
                        await UpdateSymbolAnalysisTime(watchlistSymbol.Symbol, analysisMode, true);

                        return (true, signal);
                    }
                    else
                    {
                        _logger.LogDebug($"⚠️ Signal for {watchlistSymbol.Symbol} failed quality checks");
                    }
                }
                else
                {
                    var threshold = _smartMarketHours.GetConfidenceThreshold(analysisMode);
                    _logger.LogDebug($"⏸️ Signal for {watchlistSymbol.Symbol} below {analysisMode} threshold " +
                        $"({signal.Confidence}% < {threshold}%)");
                }
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
        // Enhanced validations for full market analysis

        // 1. Risk/Reward ratio check
        if (signal.RiskRewardRatio.HasValue && signal.RiskRewardRatio < 2.0)
        {
            _logger.LogDebug($"Signal rejected: poor R/R ratio ({signal.RiskRewardRatio:F1})");
            return false;
        }

        // 2. Volume check for buy signals
        if (signal.Type == SignalType.Buy && signal.VolumeStrength < 4)
        {
            _logger.LogDebug($"Buy signal rejected: insufficient volume ({signal.VolumeStrength})");
            return false;
        }

        // 3. Trend check for high confidence signals
        if (signal.Confidence >= 85 && signal.TrendStrength < 5)
        {
            _logger.LogDebug($"High confidence signal rejected: weak trend ({signal.TrendStrength})");
            return false;
        }

        return true;
    }

    private bool PassesFinalQualityChecks(TradingSignal signal, WatchlistSymbol symbol)
    {
        // 1. Price level sanity checks
        if (signal.StopLoss >= signal.Price || signal.TakeProfit <= signal.Price)
        {
            _logger.LogWarning($"Invalid price levels: Entry=${signal.Price:F2}, SL=${signal.StopLoss:F2}, TP=${signal.TakeProfit:F2}");
            return false;
        }

        // 2. Reasonable stop loss (not too wide)
        if (signal.StopLossPercent > 15)
        {
            _logger.LogWarning($"Stop loss too wide: {signal.StopLossPercent:F1}%");
            return false;
        }

        // 3. Check for spam (too many recent signals)
        // This would require a database check - simplified for now
        return true;
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
        // Calculate next analysis time based on mode and performance
        var baseFrequency = _smartMarketHours.GetAnalysisFrequency(mode, SymbolTier.Tier2_Standard);

        // Adjust frequency based on whether signal was generated
        if (signalGenerated)
        {
            baseFrequency = TimeSpan.FromTicks((long)(baseFrequency.Ticks * 1.5)); // Wait longer after signal
        }

        var nextAnalysis = DateTime.UtcNow.Add(baseFrequency);
        await _symbolSelection.UpdateSymbolNextAnalysis(symbol, nextAnalysis);
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
        _logger.LogInformation($"  Total signals sent: {_signalsSent}");
        _logger.LogInformation($"  Strong signals: {_strongSignals}");
        _logger.LogInformation($"  Medium signals: {_mediumSignals}");
        _logger.LogInformation($"  Warning signals: {_warningSignals}");
        _logger.LogInformation($"  Data-driven signals: {_dataDrivenSignals}"); // 🆕 NUOVO

        if (_totalAnalyzed > 0)
        {
            var signalRate = (_signalsSent * 100.0) / _totalAnalyzed;
            var dataDrivenRate = (_dataDrivenSignals * 100.0) / Math.Max(_signalsSent, 1);
            _logger.LogInformation($"  Signal generation rate: {signalRate:F1}%");
            _logger.LogInformation($"  Data-driven rate: {dataDrivenRate:F1}%"); // 🆕 NUOVO
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