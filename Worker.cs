using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PortfolioSignalWorker.Models;
using PortfolioSignalWorker.Services;
using static PortfolioSignalWorker.Services.SmartMarketHoursService;

public class Worker : BackgroundService
{
    private readonly YahooFinanceService _yahooFinance;
    private readonly TelegramService _telegram;
    private readonly MongoService _mongo;
    private readonly SignalFilterService _signalFilter;
    private readonly SymbolSelectionService _symbolSelection;
    private readonly SmartMarketHoursService _smartMarketHours; // UPGRADED
    private readonly RiskManagementService _riskManagement;
    private readonly ILogger<Worker> _logger;

    public Worker(
        YahooFinanceService yahooFinance,
        TelegramService telegram,
        MongoService mongo,
        SignalFilterService signalFilter,
        SymbolSelectionService symbolSelection,
        SmartMarketHoursService smartMarketHours, // UPGRADED
        RiskManagementService riskManagement,
        ILogger<Worker> logger)
    {
        _yahooFinance = yahooFinance;
        _telegram = telegram;
        _mongo = mongo;
        _signalFilter = signalFilter;
        _symbolSelection = symbolSelection;
        _smartMarketHours = smartMarketHours; // UPGRADED
        _riskManagement = riskManagement;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🚀 Worker started with DYNAMIC VOLATILE STRATEGY + Risk Management");

        // Initialize watchlist on first run
        var watchlistCount = await _mongo.GetWatchlistCount();
        if (watchlistCount == 0)
        {
            _logger.LogInformation("No watchlist found, initializing...");
            await _symbolSelection.InitializeWatchlist();

            // 🚀 NUOVO: Popola simboli volatili dopo inizializzazione
            _logger.LogInformation("💥 Populating volatile symbols...");
            await _symbolSelection.PopulateVolatileSymbols();
        }
        else
        {
            // 🚀 NUOVO: Anche se watchlist esiste, esegui migrazione per volatilità
            _logger.LogInformation("🔄 Checking for database migration needs...");
            await _symbolSelection.MigrateWatchlistForVolatility();

            // 🚀 NUOVO: E popola simboli volatili se non esistono
            _logger.LogInformation("💥 Ensuring volatile symbols are present...");
            await _symbolSelection.PopulateVolatileSymbols();
        }

        // 🚀 NUOVO: Forza classificazione volatilità all'avvio
        _logger.LogInformation("📊 Initializing volatility classification...");
        await InitializeVolatilityClassification();

        // Log mercati all'avvio
        _smartMarketHours.LogCurrentMarketStatus();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Get symbols due for analysis con PRIORITIZZAZIONE
                var allSymbolsDue = await _symbolSelection.GetSymbolsDueForAnalysis();

                // 🚀 SMART PRIORITIZATION: Ordina per volatilità e breakout potential
                var prioritizedSymbols = allSymbolsDue
                    .OrderByDescending(s => s.VolatilityLevel) // Esplosivi prima
                    .ThenByDescending(s => s.IsBreakoutCandidate) // Breakout candidates
                    .ThenByDescending(s => s.ConsecutiveHighVolDays) // Giorni consecutivi volatili
                    .ThenBy(s => s.NextAnalysis) // Poi per timing normale
                    .ToList();

                // ===== HYBRID FILTERING: Filtra per modalità di analisi =====
                var symbolsToProcess = new List<(WatchlistSymbol symbol, AnalysisMode mode, int priority)>();
                var skippedCount = 0;

                foreach (var symbol in prioritizedSymbols)
                {
                    var analysisMode = _smartMarketHours.GetAnalysisMode(symbol.Symbol);

                    if (analysisMode == AnalysisMode.Skip)
                    {
                        skippedCount++;
                        continue;
                    }

                    // 🎯 PRIORITY SCORING per logging
                    var priority = CalculateSymbolPriority(symbol);
                    symbolsToProcess.Add((symbol, analysisMode, priority));
                }

                _logger.LogInformation($"📊 SMART ANALYSIS: Processing {symbolsToProcess.Count} symbols " +
                    $"(skipped {skippedCount}) - {symbolsToProcess.Count(x => x.symbol.VolatilityLevel == VolatilityLevel.Explosive)} explosive");

                // Log distribuzione per modalità
                var modeDistribution = symbolsToProcess
                    .GroupBy(x => x.mode)
                    .ToDictionary(g => g.Key, g => g.Count());

                foreach (var mode in modeDistribution)
                {
                    _logger.LogInformation($"  {mode.Key}: {mode.Value} symbols");
                }

                // Log prioritization
                LogVolatilityDistribution(symbolsToProcess);

                // ===== PROCESS SYMBOLS CON MODALITÀ SPECIFICA =====
                var processedCount = 0;
                var signalsSentCount = 0;

                foreach (var (watchlistSymbol, analysisMode, priority) in symbolsToProcess)
                {
                    try
                    {
                        var volatilityInfo = $"{watchlistSymbol.VolatilityLevel}" +
                            (watchlistSymbol.IsBreakoutCandidate ? " 🚀" : "") +
                            (watchlistSymbol.ConsecutiveHighVolDays > 0 ? $" ({watchlistSymbol.ConsecutiveHighVolDays}d)" : "");

                        var modeDescription = _smartMarketHours.GetModeDescription(analysisMode, watchlistSymbol.Symbol);
                        _logger.LogDebug($"Analyzing {watchlistSymbol.Symbol} [{volatilityInfo}] - {modeDescription} (Priority: {priority})");

                        // Get indicators
                        var indicator = await _yahooFinance.GetIndicatorsAsync(watchlistSymbol.Symbol);

                        // Analyze for signals
                        var signal = await _signalFilter.AnalyzeSignalAsync(watchlistSymbol.Symbol, indicator);

                        // Save indicator
                        await _mongo.SaveIndicatorAsync(indicator);

                        if (signal != null)
                        {
                            // ===== HYBRID DECISION: Dovrei inviare questo segnale? =====
                            var shouldSend = _smartMarketHours.ShouldSendSignal(signal, analysisMode);

                            if (shouldSend)
                            {
                                // Enhance con risk management
                                signal = await _riskManagement.EnhanceSignalWithRiskManagement(signal);

                                // Save signal
                                await _mongo.SaveSignalAsync(signal);

                                // Send message con contesto modalità
                                var message = FormatHybridMessage(signal, analysisMode, watchlistSymbol.Market ?? "US");
                                await _telegram.SendMessageAsync(message);
                                await _signalFilter.MarkSignalAsSentAsync(signal.Id);

                                signalsSentCount++;
                                _logger.LogInformation($"✅ {analysisMode} signal sent for {watchlistSymbol.Symbol}: " +
                                    $"{signal.Type} ({signal.Confidence}%) - {(signal.Reason?.Contains("BREAKOUT") == true ? "🚀 BREAKOUT" : "Standard")}");
                            }
                            else
                            {
                                _logger.LogDebug($"⏸️ Signal for {watchlistSymbol.Symbol} below {analysisMode} threshold " +
                                    $"({signal.Confidence}% < {_smartMarketHours.GetConfidenceThreshold(analysisMode)}%)");
                            }
                        }

                        // Update next analysis time basato sulla modalità e volatilità
                        var nextAnalysisDelay = _smartMarketHours.GetDynamicMonitoringFrequency(watchlistSymbol, analysisMode);
                        var nextAnalysis = DateTime.UtcNow.Add(nextAnalysisDelay);
                        await _symbolSelection.UpdateSymbolNextAnalysis(watchlistSymbol.Symbol, nextAnalysis);

                        processedCount++;

                        // DYNAMIC DELAY basato su volatilità
                        var delay = _smartMarketHours.GetDynamicProcessingDelay(watchlistSymbol, analysisMode);
                        await Task.Delay(delay, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing volatile symbol {symbol} in {mode} mode",
                            watchlistSymbol.Symbol, analysisMode);
                    }
                }

                _logger.LogInformation($"📈 Volatile cycle completed: {processedCount} processed, {signalsSentCount} signals sent");

                // Update frequenze dinamiche ogni ora
                if (DateTime.Now.Minute == 0)
                {
                    await _symbolSelection.UpdateDynamicFrequencies();
                }

                // Daily optimization (at midnight)
                if (DateTime.Now.Hour == 0 && DateTime.Now.Minute < 5)
                {
                    _logger.LogInformation("🔄 Starting daily watchlist optimization...");
                    await _symbolSelection.OptimizeWatchlist();

                    // Log market status per nuovo giorno
                    _smartMarketHours.LogCurrentMarketStatus();
                }

                // Wait time dinamico basato su presenza di simboli esplosivi
                var anyExplosiveActive = symbolsToProcess.Any(x =>
                    x.symbol.VolatilityLevel == VolatilityLevel.Explosive && x.mode == AnalysisMode.FullAnalysis);

                var waitTime = anyExplosiveActive ?
                    TimeSpan.FromMinutes(2) :  // Cicli rapidi se ci sono esplosivi attivi
                    TimeSpan.FromMinutes(5);   // Cicli normali

                _logger.LogDebug($"💤 Waiting {waitTime.TotalMinutes} minutes before next cycle (explosive active: {anyExplosiveActive})");
                await Task.Delay(waitTime, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in volatile hybrid worker loop");
                await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            }
        }
    }

    // 🚀 NUOVO: Metodo per inizializzare la classificazione volatilità
    private async Task InitializeVolatilityClassification()
    {
        try
        {
            var allSymbols = await _symbolSelection.GetWatchlistSummary();

            _logger.LogInformation($"📊 Classifying volatility for {allSymbols.Count} symbols...");

            var classifiedCount = 0;
            foreach (var symbol in allSymbols)
            {
                try
                {
                    // Classifica solo se non è mai stato fatto o è vecchio (>24h)
                    if (symbol.LastVolatilityUpdate == default(DateTime) ||
                        (DateTime.UtcNow - symbol.LastVolatilityUpdate).TotalHours > 24)
                    {
                        await _symbolSelection.ClassifySymbolVolatility(symbol);
                        classifiedCount++;

                        // Rate limiting per evitare troppi call API
                        await Task.Delay(1000);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to classify {symbol.Symbol}: {ex.Message}");
                }
            }

            _logger.LogInformation($"✅ Volatility classification completed: {classifiedCount}/{allSymbols.Count} symbols classified");

            // Log riassunto dopo classificazione
            await LogVolatilitySummary();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during volatility initialization");
        }
    }

    // 🚀 NUOVO: Log riassunto volatilità
    private async Task LogVolatilitySummary()
    {
        try
        {
            var allSymbols = await _symbolSelection.GetWatchlistSummary();

            var volatilityGroups = allSymbols
                .GroupBy(s => s.VolatilityLevel)
                .ToDictionary(g => g.Key, g => g.ToList());

            var breakoutCandidates = allSymbols.Where(s => s.IsBreakoutCandidate).ToList();
            var validSymbols = allSymbols.Where(s => s.AverageVolatilityPercent > 0).ToList();
            var avgVolatility = validSymbols.Any() ? validSymbols.Average(s => s.AverageVolatilityPercent) : 0.0;

            _logger.LogInformation("🎯 VOLATILITY SUMMARY:");
            foreach (var group in volatilityGroups.OrderByDescending(x => x.Key))
            {
                var topSymbols = group.Value.OrderByDescending(s => s.AverageVolatilityPercent).Take(3);
                var symbolNames = string.Join(", ", topSymbols.Select(s => $"{s.Symbol}({s.AverageVolatilityPercent:F1}%)"));

                _logger.LogInformation($"  {group.Key}: {group.Value.Count} symbols - Top: {symbolNames}");
            }

            if (breakoutCandidates.Any())
            {
                var candidateNames = string.Join(", ", breakoutCandidates.Select(s => s.Symbol));
                _logger.LogInformation($"  🚀 BREAKOUT CANDIDATES: {candidateNames}");
            }

            _logger.LogInformation($"  📊 Average Volatility: {avgVolatility:F1}%");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error logging volatility summary");
        }
    }

    // Logging della distribuzione volatilità
    private void LogVolatilityDistribution(List<(WatchlistSymbol symbol, AnalysisMode mode, int priority)> symbols)
    {
        var volatilityGroups = symbols.GroupBy(x => x.symbol.VolatilityLevel)
            .ToDictionary(g => g.Key, g => g.Count());

        var breakoutCandidates = symbols.Count(x => x.symbol.IsBreakoutCandidate);
        var avgVolatility = symbols.Where(x => x.symbol.AverageVolatilityPercent > 0).Average(x => x.symbol.AverageVolatilityPercent);

        _logger.LogInformation("🎯 Volatility Distribution:");
        foreach (var group in volatilityGroups.OrderByDescending(x => x.Key))
        {
            var delay = _smartMarketHours.GetDynamicProcessingDelay(new WatchlistSymbol { VolatilityLevel = group.Key }, AnalysisMode.FullAnalysis);
            _logger.LogInformation($"  {group.Key}: {group.Value} symbols ({delay}ms delay)");
        }

        _logger.LogInformation($"  Breakout Candidates: {breakoutCandidates}");
        _logger.LogInformation($"  Average Volatility: {avgVolatility:F1}%");
    }
    // Calcola priorità per logging e debug
    private int CalculateSymbolPriority(WatchlistSymbol symbol)
    {
        var priority = (int)symbol.VolatilityLevel * 10; // Base volatility score

        if (symbol.IsBreakoutCandidate) priority += 20;
        if (symbol.ConsecutiveHighVolDays >= 3) priority += 15;
        if (symbol.AverageVolatilityPercent > 10) priority += 10;

        return priority;
    }

    // Delay dinamico per rate limiting
    private int GetDynamicDelay(WatchlistSymbol symbol, AnalysisMode mode)
    {
        return (symbol.VolatilityLevel, mode) switch
        {
            (VolatilityLevel.Explosive, AnalysisMode.FullAnalysis) => 200,     // Esplosivi super rapidi
            (VolatilityLevel.High, AnalysisMode.FullAnalysis) => 400,         // Volatili rapidi  
            (VolatilityLevel.Standard, AnalysisMode.FullAnalysis) => 600,     // Standard normale
            (VolatilityLevel.Low, _) => 1000,                                 // Lenti più lenti

            (VolatilityLevel.Explosive, AnalysisMode.PreMarketWatch) => 300,  // Pre-market esplosivi
            (_, AnalysisMode.PreMarketWatch) => 800,

            (_, AnalysisMode.OffHoursMonitor) => 1200,                        // Off-hours più lenti

            _ => 600 // Default
        };
    }
    public async Task UpdateDynamicFrequencies()
    {
        var allSymbols = await _symbolSelection.GetWatchlistSummary();

        foreach (var symbol in allSymbols)
        {
            // Ricalcola volatilità ogni 24 ore
            if ((DateTime.UtcNow - symbol.LastVolatilityUpdate).TotalHours > 24)
            {
                await _symbolSelection.ClassifySymbolVolatility(symbol);
            }

            // Aggiorna frequenza monitoring
            var currentMode = _smartMarketHours.GetAnalysisMode(symbol.Symbol);
            var newFrequency = _smartMarketHours.GetDynamicMonitoringFrequency(symbol, currentMode);

            // Aggiorna NextAnalysis solo se frequenza è cambiata significativamente
            var timeDiff = Math.Abs((newFrequency - symbol.MonitoringFrequency).TotalMinutes);
            if (timeDiff > 5) // Differenza > 5 minuti
            {
                symbol.MonitoringFrequency = newFrequency;
                var nextAnalysis = DateTime.UtcNow.Add(newFrequency);
                await _symbolSelection.UpdateSymbolNextAnalysis(symbol.Symbol, nextAnalysis);

                _logger.LogInformation($"🔄 Updated frequency: {symbol.Symbol} = {newFrequency} (volatility: {symbol.VolatilityLevel})");
            }
        }
    }

    private string FormatHybridMessage(TradingSignal signal, AnalysisMode mode, string market = "US")
    {
        var modeEmoji = mode switch
        {
            AnalysisMode.FullAnalysis => "🟢",
            AnalysisMode.PreMarketWatch => "🟡",
            AnalysisMode.OffHoursMonitor => "🟠",
            _ => "⚪"
        };

        // 🚀 NUOVO: Emoji specifici per breakout
        var signalEmoji = signal.Reason?.Contains("IMMINENT BREAKOUT") == true ? "🚀💥" :
                         signal.Reason?.Contains("BREAKOUT SETUP") == true ? "💥🔥" :
                         signal.Type switch
                         {
                             SignalType.Buy when signal.Confidence >= 90 => "🚀",
                             SignalType.Buy => "📈",
                             SignalType.Warning => "⚠️",
                             _ => "ℹ️"
                         };

        var marketFlag = market switch
        {
            "EU" => "🇪🇺",
            "US" => "🇺🇸",
            _ => "🌍"
        };

        var currency = GetCurrencySymbol(signal.Symbol, market);
        var marketStatus = _smartMarketHours.GetModeDescription(mode, signal.Symbol);

        // 🔥 TITOLO SPECIALE per breakout
        string title;
        if (signal.Reason?.Contains("IMMINENT BREAKOUT") == true)
        {
            title = $"🚀💥 IMMINENT BREAKOUT {signal.Symbol} {marketFlag}";
        }
        else if (signal.Reason?.Contains("BREAKOUT SETUP") == true)
        {
            title = $"💥🔥 BREAKOUT SETUP {signal.Symbol} {marketFlag}";
        }
        else
        {
            var modePrefix = mode switch
            {
                AnalysisMode.PreMarketWatch => "PRE-MARKET SETUP",
                AnalysisMode.OffHoursMonitor => "OFF-HOURS ALERT",
                _ => ""
            };

            title = string.IsNullOrEmpty(modePrefix)
                ? $"{signalEmoji} {signal.Type.ToString().ToUpper()} {signal.Symbol} {marketFlag}"
                : $"{modeEmoji} {modePrefix} {signal.Symbol} {marketFlag}";
        }

        var message = $@"{title}

💪 Confidence: {signal.Confidence}%
📊 RSI: {signal.RSI:F1}
⚡ MACD: {signal.MACD_Histogram:F3}
💰 Entry: {currency}{signal.Price:F2}
📊 Volume: {FormatVolume(signal.Volume)}

🕐 STATUS: {marketStatus}";

        // 🚀 SEZIONE SPECIALE per breakout
        if (signal.Reason?.Contains("BREAKOUT") == true)
        {
            message += $@"

🚀 BREAKOUT ALERT:
⚡ Multiple technical triggers firing
🎯 High probability explosive move
⏰ ENTRY WINDOW: Next 1-4 hours";
        }

        // Risk management (solo per segnali con livelli di trading)
        if (signal.StopLoss.HasValue && signal.TakeProfit.HasValue)
        {
            message += $@"

🛡️ RISK MANAGEMENT:
🔻 Stop Loss: {currency}{signal.StopLoss:F2} ({signal.StopLossPercent:F1}%)
🎯 Take Profit: {currency}{signal.TakeProfit:F2} ({signal.TakeProfitPercent:F1}%)
⚖️ Risk/Reward: 1:{signal.RiskRewardRatio:F1}";

            // 💥 BREAKOUT SPECIFICO: Take profit più alto
            if (signal.Reason?.Contains("BREAKOUT") == true)
            {
                var breakoutTarget = signal.Price * 1.15; // 15% target per breakout
                message += $@"
🚀 BREAKOUT TARGET: {currency}{breakoutTarget:F2} (15%+ potential)";
            }
        }

        // Timing advice per breakout
        if (signal.Reason?.Contains("IMMINENT BREAKOUT") == true)
        {
            message += $@"

⚡ URGENT TIMING:
🔥 Enter within next 1-2 hours
📈 Expected move: 10-25%+ 
⚠️ Use limit orders near current price";
        }
        else if (signal.Reason?.Contains("BREAKOUT SETUP") == true)
        {
            message += $@"

📋 SETUP TIMING:
🎯 Position before breakout (1-3 days)
📊 Watch for volume confirmation
✅ Good risk/reward setup";
        }

        // Levels
        if (signal.SupportLevel.HasValue && signal.ResistanceLevel.HasValue &&
            signal.SupportLevel > 0 && signal.ResistanceLevel > 0)
        {
            message += $@"

📈 KEY LEVELS:
🟢 Support: {currency}{signal.SupportLevel:F2}
🔴 Resistance: {currency}{signal.ResistanceLevel:F2}";

            // Per breakout, enfatizza la resistenza da rompere
            if (signal.Reason?.Contains("BREAKOUT") == true)
            {
                message += $@"
💥 BREAKOUT LEVEL: {currency}{signal.ResistanceLevel:F2}";
            }
        }

        message += $@"

💡 {signal.Reason}

🕐 {DateTime.Now:HH:mm} {modeEmoji} (Breakout Hunter)";

        return message;
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