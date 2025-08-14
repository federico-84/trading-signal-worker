using MongoDB.Driver;
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
    private readonly BreakoutDetectionService _breakoutDetection;
    private readonly SimplifiedEnhancedRiskManagementService _riskManagement;
    private readonly ILogger<Worker> _logger;

    public Worker(
        YahooFinanceService yahooFinance,
        TelegramService telegram,
        MongoService mongo,
        SignalFilterService signalFilter,
        SymbolSelectionService symbolSelection,
        SmartMarketHoursService smartMarketHours, // UPGRADED
        SimplifiedEnhancedRiskManagementService riskManagement,
        BreakoutDetectionService breakoutDetection,
    ILogger<Worker> logger)
    {
        _yahooFinance = yahooFinance;
        _telegram = telegram;
        _mongo = mongo;
        _signalFilter = signalFilter;
        _symbolSelection = symbolSelection;
        _smartMarketHours = smartMarketHours; // UPGRADED
        _riskManagement = riskManagement;
        _breakoutDetection = breakoutDetection;
        _logger = logger;
    }

    // 🔧 AGGIUNGI questo debug logging nel Worker.cs - metodo ExecuteAsync
    // SOSTITUISCI la sezione di debug con questa versione più dettagliata:

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🚀 Worker started with HYBRID MARKET STRATEGY + Risk Management + Breakout Detection");
        await DebugWatchlistStatus();
        // Initialize watchlist on first run
        var watchlistCount = await _mongo.GetWatchlistCount();
        _logger.LogInformation($"📊 Current watchlist count: {watchlistCount}");

        if (watchlistCount == 0)
        {
            _logger.LogInformation("No watchlist found, initializing...");
            await _symbolSelection.InitializeWatchlist();

            // Re-check after initialization
            watchlistCount = await _mongo.GetWatchlistCount();
            _logger.LogInformation($"📊 Watchlist count after initialization: {watchlistCount}");
        }

        // Log mercati all'avvio
        _smartMarketHours.LogCurrentMarketStatus();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 🔍 DEBUG: Get symbols due for analysis with detailed logging
                _logger.LogInformation("🔍 Checking for symbols due for analysis...");
                var allSymbolsDue = await _symbolSelection.GetSymbolsDueForAnalysis();

                _logger.LogInformation($"📊 Total symbols due for analysis: {allSymbolsDue.Count}");

                if (allSymbolsDue.Count == 0)
                {
                    _logger.LogWarning("❌ No symbols due for analysis! Checking reasons:");

                    // Debug: Check total watchlist
                    var totalWatchlist = await _symbolSelection.GetWatchlistSummary();
                    _logger.LogInformation($"📋 Total active symbols in watchlist: {totalWatchlist.Count}");

                    if (totalWatchlist.Count == 0)
                    {
                        _logger.LogError("🚨 CRITICAL: Watchlist is EMPTY! Re-initializing...");
                        await _symbolSelection.InitializeWatchlist();
                    }
                    else
                    {
                        // Log some examples of symbols and their next analysis times
                        var now = DateTime.UtcNow;
                        var nextAnalysisTimes = totalWatchlist.Take(5)
                            .Select(s => new {
                                s.Symbol,
                                s.NextAnalysis,
                                MinutesUntil = (s.NextAnalysis - now).TotalMinutes
                            })
                            .ToList();

                        _logger.LogInformation("📋 Sample symbols and their next analysis times:");
                        foreach (var symbol in nextAnalysisTimes)
                        {
                            _logger.LogInformation($"  {symbol.Symbol}: {symbol.NextAnalysis:HH:mm:ss} (in {symbol.MinutesUntil:F1} min)");
                        }
                    }

                    // Wait longer before next check
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    continue;
                }

                // Log symbols due for analysis
                _logger.LogInformation($"📋 Symbols due: {string.Join(", ", allSymbolsDue.Take(5).Select(s => s.Symbol))}");

                // ===== HYBRID FILTERING: Filtra per modalità di analisi =====
                var symbolsToProcess = new List<(WatchlistSymbol symbol, AnalysisMode mode)>();
                var skippedCount = 0;

                foreach (var symbol in allSymbolsDue)
                {
                    var analysisMode = _smartMarketHours.GetAnalysisMode(symbol.Symbol);

                    _logger.LogDebug($"🔍 {symbol.Symbol}: Analysis mode = {analysisMode}");

                    if (analysisMode == AnalysisMode.Skip)
                    {
                        skippedCount++;
                        _logger.LogDebug($"⏭️ Skipping {symbol.Symbol}: {analysisMode}");
                        continue;
                    }

                    symbolsToProcess.Add((symbol, analysisMode));
                }

                _logger.LogInformation($"📊 HYBRID ANALYSIS: Processing {symbolsToProcess.Count} symbols " +
                    $"(skipped {skippedCount} out-of-hours)");

                // Log distribuzione per modalità
                var modeDistribution = symbolsToProcess
                    .GroupBy(x => x.mode)
                    .ToDictionary(g => g.Key, g => g.Count());

                foreach (var mode in modeDistribution)
                {
                    _logger.LogInformation($"  {mode.Key}: {mode.Value} symbols");
                }

                // ===== DEBUG: Se ancora 0 symbols, analizza perché =====
                if (symbolsToProcess.Count == 0)
                {
                    _logger.LogWarning($"⚠️ All {allSymbolsDue.Count} symbols were skipped due to market hours analysis");

                    // Log detailed market status for first few symbols
                    foreach (var symbol in allSymbolsDue.Take(3))
                    {
                        var mode = _smartMarketHours.GetAnalysisMode(symbol.Symbol);
                        var status = _smartMarketHours.GetModeDescription(mode, symbol.Symbol);
                        _logger.LogInformation($"  {symbol.Symbol}: {mode} - {status}");
                    }

                    // Check if it's a weekend or very late night
                    var now = DateTime.UtcNow;
                    var dayOfWeek = now.DayOfWeek;
                    var hour = now.Hour;

                    if (dayOfWeek == DayOfWeek.Saturday || dayOfWeek == DayOfWeek.Sunday)
                    {
                        _logger.LogInformation("📅 It's weekend - markets are closed, symbols skipped is normal");
                    }
                    else if (hour < 6 || hour > 22) // Very early or very late UTC
                    {
                        _logger.LogInformation($"🕐 It's {hour}:00 UTC - outside trading hours, symbols skipped is normal");
                    }
                    else
                    {
                        _logger.LogWarning("⚠️ Symbols skipped during potential trading hours - check SmartMarketHoursService logic");
                    }
                }

                // ===== PROCESS SYMBOLS CON MODALITÀ SPECIFICA =====
                var processedCount = 0;
                var signalsSentCount = 0;

                foreach (var (watchlistSymbol, analysisMode) in symbolsToProcess)
                {
                    try
                    {
                        var modeDescription = _smartMarketHours.GetModeDescription(analysisMode, watchlistSymbol.Symbol);
                        _logger.LogDebug($"Analyzing {watchlistSymbol.Symbol} - {modeDescription}");

                        // Get indicators
                        var indicator = await _yahooFinance.GetIndicatorsAsync(watchlistSymbol.Symbol);

                        // Analyze for signals
                        var signal = await _signalFilter.AnalyzeSignalAsync(watchlistSymbol.Symbol, indicator);

                        // Save indicator
                        await _mongo.SaveIndicatorAsync(indicator);

                        if (signal != null)
                        {
                            var shouldSend = _smartMarketHours.ShouldSendSignal(signal, analysisMode);

                            if (shouldSend)
                            {
                                // 🆕 NUOVO: Enhanced con risk management data-driven
                                signal = await _riskManagement.EnhanceSignalWithSmartRiskManagement(signal);

                                // Save signal
                                await _mongo.SaveSignalAsync(signal);

                                // Send message con contesto avanzato
                                var message = FormatAdvancedMessage(signal, analysisMode, watchlistSymbol.Market ?? "US");
                                await _telegram.SendMessageAsync(message);
                                await _signalFilter.MarkSignalAsSentAsync(signal.Id);

                                signalsSentCount++;
                                _logger.LogInformation($"✅ {analysisMode} signal sent for {watchlistSymbol.Symbol}: " +
                                    $"{signal.Type} ({signal.Confidence}%) - Strategy: {signal.TakeProfitStrategy ?? "Standard"}");
                            }
                            else
                            {
                                _logger.LogDebug($"⏸️ Signal for {watchlistSymbol.Symbol} below {analysisMode} threshold");
                            }
                        }

                        // Update next analysis time
                        var nextAnalysisDelay = _smartMarketHours.GetAnalysisFrequency(analysisMode, watchlistSymbol.Tier);
                        var nextAnalysis = DateTime.UtcNow.Add(nextAnalysisDelay);
                        await _symbolSelection.UpdateSymbolNextAnalysis(watchlistSymbol.Symbol, nextAnalysis);

                        processedCount++;

                        // 🔄 CORRETTO: Dynamic rate limiting
                        var delay = analysisMode switch
                        {
                            AnalysisMode.FullAnalysis => 600,      // 600ms durante mercato
                            AnalysisMode.PreMarketWatch => 800,   // 800ms pre-market
                            AnalysisMode.OffHoursMonitor => 1000, // 1s off-hours
                            _ => 800
                        };

                        await Task.Delay(delay, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing {symbol} in {mode} mode", watchlistSymbol.Symbol, analysisMode);
                    }
                }

                _logger.LogInformation($"📈 Cycle completed: {processedCount} processed, {signalsSentCount} signals sent");
                // 🆕 NUOVO: Aggiorna performance tracking ogni ora
                if (DateTime.Now.Minute == 0) // All'inizio di ogni ora
                {
                    try
                    {
                        await _riskManagement.UpdateDataDrivenPerformance();

                        // Report settimanale (lunedì mattina)
                        if (DateTime.Now.DayOfWeek == DayOfWeek.Monday && DateTime.Now.Hour == 9)
                        {
                            var insights = await _riskManagement.GetDataDrivenInsights();
                            if (insights.HasSufficientData)
                            {
                                var reportMessage = $"📊 WEEKLY TP INSIGHTS:\n" +
                                                   $"Analyzed: {insights.AnalyzedRecords} trades\n" +
                                                   $"Top 3 recommendations:\n" +
                                                   string.Join("\n", insights.Recommendations.Take(3).Select(r => $"• {r}"));

                                await _telegram.SendMessageAsync(reportMessage);
                                _logger.LogInformation("📈 Weekly Take Profit insights sent");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error updating take profit performance");
                    }
                }
                // Daily optimization (at midnight)
                if (DateTime.Now.Hour == 0 && DateTime.Now.Minute < 5)
                {
                    _logger.LogInformation("🔄 Starting daily watchlist optimization...");
                    await _symbolSelection.OptimizeWatchlist();
                    _smartMarketHours.LogCurrentMarketStatus();
                }

                // Wait before next cycle
                var anyMarketOpen = symbolsToProcess.Any(x => x.mode == AnalysisMode.FullAnalysis);
                var waitTime = anyMarketOpen ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(15);

                _logger.LogDebug($"💤 Waiting {waitTime.TotalMinutes} minutes before next cycle");
                await Task.Delay(waitTime, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in hybrid worker loop");
                await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            }
        }
    }
    private string FormatAdvancedMessage(TradingSignal signal, AnalysisMode mode, string market = "US")
    {
        var modeEmoji = mode switch
        {
            AnalysisMode.FullAnalysis => "🟢",
            AnalysisMode.PreMarketWatch => "🟡",
            AnalysisMode.OffHoursMonitor => "🟠",
            _ => "⚪"
        };

        var signalEmoji = signal.Type switch
        {
            SignalType.Buy when signal.Confidence >= 90 => "🚀",
            SignalType.Buy => "📈",
            SignalType.Warning => "⚠️",
            SignalType.Sell => "📉",
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

        // Titolo con indicatore data-driven
        var dataDrivenIndicator = !string.IsNullOrEmpty(signal.TakeProfitStrategy) ? " 🧠" : "";
        var title = $"{signalEmoji} {signal.Type.ToString().ToUpper()} {signal.Symbol} {marketFlag}{dataDrivenIndicator}";

        var message = $@"{title}

💪 Confidence: {signal.Confidence}%";

        // Aggiungi info strategia se disponibile
        if (!string.IsNullOrEmpty(signal.TakeProfitStrategy))
        {
            message += $@"
🧠 Strategy: {signal.TakeProfitStrategy}";

            if (signal.PredictedSuccessProbability.HasValue)
            {
                message += $" ({signal.PredictedSuccessProbability:F0}% success rate)";
            }
        }

        message += $@"

📊 TECHNICAL:
• RSI: {signal.RSI:F1}
• MACD: {signal.MACD_Histogram:F3}
• Entry: {currency}{signal.Price:F2}
• Volume: {FormatVolume(signal.Volume)}";

        if (signal.VolumeStrength.HasValue)
        {
            message += $" (Strength: {signal.VolumeStrength:F0}/10)";
        }

        message += "\n\n🛡️ RISK MANAGEMENT:";

        if (signal.StopLoss.HasValue)
        {
            message += $@"
• Stop Loss: {currency}{signal.StopLoss:F2} ({signal.StopLossPercent:F1}%)";
        }

        if (signal.TakeProfit.HasValue)
        {
            message += $@"
• Take Profit: {currency}{signal.TakeProfit:F2} ({signal.TakeProfitPercent:F1}%)";
        }

        if (signal.RiskRewardRatio.HasValue)
        {
            message += $@"
• Risk/Reward: 1:{signal.RiskRewardRatio:F1}";
        }

        // Strategie di entrata/uscita se disponibili
        if (!string.IsNullOrEmpty(signal.EntryStrategy))
        {
            message += $@"

📈 ENTRY: {signal.EntryStrategy}";
        }

        if (!string.IsNullOrEmpty(signal.ExitStrategy))
        {
            message += $@"

📉 EXIT: {signal.ExitStrategy}";
        }

        // Livelli tecnici se disponibili
        if (signal.SupportLevel.HasValue && signal.ResistanceLevel.HasValue &&
            signal.SupportLevel > 0 && signal.ResistanceLevel > 0)
        {
            message += $@"

📈 LEVELS:
• Support: {currency}{signal.SupportLevel:F2}
• Resistance: {currency}{signal.ResistanceLevel:F2}";
        }

        // Context di mercato
        if (!string.IsNullOrEmpty(signal.MarketCondition))
        {
            message += $@"

🌊 Market: {signal.MarketCondition}";
        }

        message += $@"

🕐 STATUS: {marketStatus}
🕐 {DateTime.Now:HH:mm} {modeEmoji} (Smart Strategy)";

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
    // 🔍 AGGIUNGI questo metodo temporaneo al Worker.cs per debug:

    // Metodo temporaneo per debug - chiamalo una volta per controllare la situazione
    private async Task DebugWatchlistStatus()
    {
        _logger.LogInformation("🔍 === DEBUG WATCHLIST STATUS ===");

        try
        {
            // 1. Check MongoDB collections
            var database = _mongo.GetDatabase();

            // Check WatchlistSymbols collection
            var watchlistCollection = database.GetCollection<WatchlistSymbol>("WatchlistSymbols");
            var totalWatchlist = await watchlistCollection.CountDocumentsAsync(FilterDefinition<WatchlistSymbol>.Empty);
            var activeWatchlist = await watchlistCollection.CountDocumentsAsync(
                Builders<WatchlistSymbol>.Filter.Eq(x => x.IsActive, true));

            _logger.LogInformation($"📊 WatchlistSymbols collection: {totalWatchlist} total, {activeWatchlist} active");

            // Check CoreSymbols collection
            var coreCollection = database.GetCollection<CoreSymbol>("CoreSymbols");
            var totalCore = await coreCollection.CountDocumentsAsync(FilterDefinition<CoreSymbol>.Empty);
            var activeCore = await coreCollection.CountDocumentsAsync(
                Builders<CoreSymbol>.Filter.Eq(x => x.IsActive, true));

            _logger.LogInformation($"📊 CoreSymbols collection: {totalCore} total, {activeCore} active");

            // Check RotationSymbols collection
            var rotationCollection = database.GetCollection<RotationSymbol>("RotationSymbols");
            var totalRotation = await rotationCollection.CountDocumentsAsync(FilterDefinition<RotationSymbol>.Empty);
            var activeRotation = await rotationCollection.CountDocumentsAsync(
                Builders<RotationSymbol>.Filter.Eq(x => x.IsActive, true));

            _logger.LogInformation($"📊 RotationSymbols collection: {totalRotation} total, {activeRotation} active");

            // 2. If no symbols in core/rotation, the problem is here
            if (activeCore == 0 && activeRotation == 0)
            {
                _logger.LogError("🚨 CRITICAL: No active symbols in CoreSymbols AND RotationSymbols collections!");
                _logger.LogError("💡 SOLUTION: Run the MongoDB insert script from insertsimbolMongo.txt");
                _logger.LogError("📋 You need to populate the MongoDB collections manually with symbols");
                return;
            }

            // 3. If core/rotation exist but watchlist is empty, initialization failed
            if ((activeCore > 0 || activeRotation > 0) && activeWatchlist == 0)
            {
                _logger.LogWarning("⚠️ Symbols exist in source collections but watchlist is empty");
                _logger.LogInformation("🔄 Attempting to initialize watchlist...");

                try
                {
                    await _symbolSelection.InitializeWatchlist();

                    // Re-check
                    var newWatchlistCount = await watchlistCollection.CountDocumentsAsync(
                        Builders<WatchlistSymbol>.Filter.Eq(x => x.IsActive, true));
                    _logger.LogInformation($"✅ Watchlist initialization completed: {newWatchlistCount} symbols added");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Watchlist initialization failed");
                }
            }

            // 4. Check sample symbols and their NextAnalysis times
            if (activeWatchlist > 0)
            {
                var sampleSymbols = await watchlistCollection
                    .Find(Builders<WatchlistSymbol>.Filter.Eq(x => x.IsActive, true))
                    .Limit(10)
                    .ToListAsync();

                _logger.LogInformation($"📋 Sample active symbols ({sampleSymbols.Count}):");

                var now = DateTime.UtcNow;
                foreach (var symbol in sampleSymbols)
                {
                    var minutesUntil = (symbol.NextAnalysis - now).TotalMinutes;
                    var isDue = symbol.NextAnalysis <= now;

                    _logger.LogInformation($"  {symbol.Symbol}: NextAnalysis={symbol.NextAnalysis:HH:mm:ss} " +
                        $"({minutesUntil:F1}min) {(isDue ? "✅ DUE" : "⏰ WAITING")}");
                }

                // Check how many are due NOW
                var symbolsDue = await watchlistCollection.CountDocumentsAsync(
                    Builders<WatchlistSymbol>.Filter.And(
                        Builders<WatchlistSymbol>.Filter.Eq(x => x.IsActive, true),
                        Builders<WatchlistSymbol>.Filter.Lte(x => x.NextAnalysis, now)
                    ));

                _logger.LogInformation($"📊 Symbols due for analysis RIGHT NOW: {symbolsDue}");
            }

            // 5. Check market hours status
            _logger.LogInformation("🕐 Market Hours Status:");
            _smartMarketHours.LogCurrentMarketStatus();

            // 6. Test a few symbols with market hours logic
            if (activeWatchlist > 0)
            {
                var testSymbols = await watchlistCollection
                    .Find(Builders<WatchlistSymbol>.Filter.Eq(x => x.IsActive, true))
                    .Limit(3)
                    .ToListAsync();

                _logger.LogInformation("🧪 Market Hours Test for sample symbols:");
                foreach (var symbol in testSymbols)
                {
                    var analysisMode = _smartMarketHours.GetAnalysisMode(symbol.Symbol);
                    var description = _smartMarketHours.GetModeDescription(analysisMode, symbol.Symbol);

                    _logger.LogInformation($"  {symbol.Symbol}: {analysisMode} - {description}");
                }
            }

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in debug watchlist status");
        }

        _logger.LogInformation("🔍 === END DEBUG WATCHLIST STATUS ===");
    }
     

    private bool ShouldSendBreakoutSignal(BreakoutSignal breakoutSignal, AnalysisMode analysisMode)
    {
        var minScoreByMode = analysisMode switch
        {
            AnalysisMode.FullAnalysis => 60,      // Durante mercato: più permissivo
            AnalysisMode.PreMarketWatch => 70,    // Pre-market: selettivo per setup
            AnalysisMode.OffHoursMonitor => 80,   // Off-hours: solo i migliori
            _ => 60
        };

        if (breakoutSignal.BreakoutScore < minScoreByMode)
        {
            _logger.LogDebug($"Breakout score {breakoutSignal.BreakoutScore} < {minScoreByMode} for {analysisMode}");
            return false;
        }

        // Solo breakout PROBABLE o IMMINENT
        if (breakoutSignal.BreakoutType == BreakoutType.Unlikely)
        {
            return false;
        }

        // Durante off-hours, solo IMMINENT
        if (analysisMode == AnalysisMode.OffHoursMonitor && breakoutSignal.BreakoutType != BreakoutType.Imminent)
        {
            return false;
        }

        return true;
    }
    private TradingSignal ConvertBreakoutToTradingSignal(BreakoutSignal breakoutSignal)
    {
        var confidence = breakoutSignal.BreakoutType switch
        {
            BreakoutType.Imminent => Math.Min(95, 70 + breakoutSignal.BreakoutScore * 0.3),
            BreakoutType.Probable => Math.Min(85, 60 + breakoutSignal.BreakoutScore * 0.25),
            BreakoutType.Possible => Math.Min(75, 50 + breakoutSignal.BreakoutScore * 0.2),
            _ => 50
        };

        // Simula RSI e MACD per compatibilità (verranno calcolati nel risk management)
        var estimatedRSI = breakoutSignal.Positioning?.DistanceToResistance < 5 ? 45 : 55; // Near resistance = lower RSI
        var estimatedMACD = breakoutSignal.VolumePattern?.IsAccumulating == true ? 0.1 : -0.1; // Positive if accumulating

        return new TradingSignal
        {
            Symbol = breakoutSignal.Symbol,
            Type = SignalType.Buy,
            Confidence = confidence,
            Reason = $"🎯 {breakoutSignal.BreakoutType.ToString().ToUpper()} BREAKOUT: {string.Join(", ", breakoutSignal.Reasons)}",
            RSI = estimatedRSI,
            MACD_Histogram = estimatedMACD,
            Price = breakoutSignal.CurrentPrice,
            Volume = (long)(breakoutSignal.VolumePattern?.AverageVolume ?? 1000000), // Estimate if not available
            SignalHash = $"BREAKOUT_{breakoutSignal.Symbol}_{DateTime.UtcNow:yyyyMMddHHmm}",
            CreatedAt = DateTime.UtcNow,

            // Breakout specific fields
            SupportLevel = breakoutSignal.KeyLevels?.PrimarySupport,
            ResistanceLevel = breakoutSignal.KeyLevels?.PrimaryResistance,
            VolumeStrength = breakoutSignal.VolumePattern?.CurrentVolumeStrength ?? 5,
            TrendStrength = Math.Min(10, breakoutSignal.BreakoutScore / 10)
        };
    }

    private string FormatBreakoutMessage(BreakoutSignal breakoutSignal, TradingSignal tradingSignal, AnalysisMode mode, string market = "US")
    {
        var breakoutEmoji = breakoutSignal.BreakoutType switch
        {
            BreakoutType.Imminent => "🚀",
            BreakoutType.Probable => "⚡",
            BreakoutType.Possible => "📈",
            _ => "📊"
        };

        var modeEmoji = mode switch
        {
            AnalysisMode.FullAnalysis => "🟢",
            AnalysisMode.PreMarketWatch => "🟡",
            AnalysisMode.OffHoursMonitor => "🟠",
            _ => "⚪"
        };

        var marketFlag = market switch
        {
            "EU" => "🇪🇺",
            "US" => "🇺🇸",
            _ => "🌍"
        };

        var currencySymbol = "€"; // Always Euro for TradeRepublic
        var marketStatus = _smartMarketHours.GetModeDescription(mode, breakoutSignal.Symbol);

        var message = $@"{breakoutEmoji} {breakoutSignal.BreakoutType.ToString().ToUpper()} BREAKOUT {breakoutSignal.Symbol} {marketFlag}

🎯 Breakout Score: {breakoutSignal.BreakoutScore}/100
💪 Confidence: {tradingSignal.Confidence}%
💰 Current Price: {currencySymbol}{tradingSignal.Price:F2}

🔍 BREAKOUT ANALYSIS:";

        // Add specific breakout reasons
        foreach (var reason in breakoutSignal.Reasons.Take(4))
        {
            message += $"\n✅ {reason}";
        }

        message += $@"

📊 PATTERN DETAILS:";

        if (breakoutSignal.Consolidation?.IsValid == true)
        {
            message += $@"
📐 Consolidation: {breakoutSignal.Consolidation.ConsolidationType} ({breakoutSignal.Consolidation.VolatilityPercent:F1}% range)";
        }

        if (breakoutSignal.Compression?.IsDetected == true)
        {
            message += $@"
🔥 Compression: {breakoutSignal.Compression.CompressionStrength:F0}% squeeze detected";
        }

        if (breakoutSignal.VolumePattern?.IsValid == true)
        {
            message += $@"
📈 Volume: {(breakoutSignal.VolumePattern.IsAccumulating ? "ACCUMULATING" : "INCREASING")} ({breakoutSignal.VolumePattern.VolumeIncreaseRatio:F1}x)";
        }

        // Risk management section
        if (tradingSignal.StopLoss.HasValue && tradingSignal.TakeProfit.HasValue)
        {
            message += $@"

🛡️ BREAKOUT RISK MANAGEMENT:
🔻 Stop Loss: {currencySymbol}{tradingSignal.StopLoss:F2} ({tradingSignal.StopLossPercent:F1}%)
🎯 Take Profit: {currencySymbol}{tradingSignal.TakeProfit:F2} ({tradingSignal.TakeProfitPercent:F1}%)
⚖️ Risk/Reward: 1:{tradingSignal.RiskRewardRatio:F1}";

            if (tradingSignal.SuggestedShares.HasValue && tradingSignal.PositionValue.HasValue)
            {
                message += $@"
💼 Position: {tradingSignal.SuggestedShares} shares = {currencySymbol}{tradingSignal.PositionValue:F0}";
            }
        }

        // Key levels
        if (breakoutSignal.KeyLevels?.PrimaryResistance > 0)
        {
            var distanceToResistance = ((breakoutSignal.KeyLevels.PrimaryResistance - breakoutSignal.CurrentPrice) / breakoutSignal.CurrentPrice) * 100;
            message += $@"

🎯 KEY LEVELS:
🔴 Primary Resistance: {currencySymbol}{breakoutSignal.KeyLevels.PrimaryResistance:F2} (+{distanceToResistance:F1}%)";

            if (breakoutSignal.KeyLevels.PrimarySupport > 0)
            {
                message += $@"
🟢 Primary Support: {currencySymbol}{breakoutSignal.KeyLevels.PrimarySupport:F2}";
            }
        }

        // Market timing advice
        if (mode == AnalysisMode.PreMarketWatch)
        {
            message += $@"

⏰ PRE-MARKET BREAKOUT SETUP:
📝 Position for market open gap
🎯 Watch for volume confirmation at open";
        }
        else if (mode == AnalysisMode.OffHoursMonitor)
        {
            message += $@"

🌙 OFF-HOURS BREAKOUT ALERT:
📝 Prepare for next trading session
⚠️ Confirm pattern holds at market open";
        }

        message += $@"

🕐 STATUS: {marketStatus}

🎯 Expected Move: {(tradingSignal.TakeProfitPercent ?? 10):F1}% breakout potential

🕐 {DateTime.Now:HH:mm} {modeEmoji} (Advanced Breakout Detection)";

        return message;
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

        var modePrefix = mode switch
        {
            AnalysisMode.PreMarketWatch => "PRE-MARKET SETUP",
            AnalysisMode.OffHoursMonitor => "OFF-HOURS ALERT",
            _ => ""
        };

        var signalEmoji = signal.Type switch
        {
            SignalType.Buy when signal.Confidence >= 90 => "🚀",
            SignalType.Buy => "📈",
            SignalType.Warning => "⚠️",
            SignalType.Sell => "📉",
            _ => "ℹ️"
        };

        var marketFlag = market switch
        {
            "EU" => "🇪🇺",
            "US" => "🇺🇸",
            _ => "🌍"
        };

        // 🌍 NUOVO: Gestione valute con conversione
        var currencySymbol = "€"; // Sempre Euro per TradeRepublic
        var marketStatus = _smartMarketHours.GetModeDescription(mode, signal.Symbol);

        // 🌍 NUOVO: Info conversione valuta
        var currencyInfo = "";
        if (signal.OriginalCurrency != "EUR" && signal.ExchangeRate != 1.0)
        {
            currencyInfo = $" (was {GetOriginalCurrencySymbol(signal.OriginalCurrency)}{signal.OriginalPrice:F2} @ {signal.ExchangeRate:F4})";
        }

        // Titolo con prefisso modalità
        var title = string.IsNullOrEmpty(modePrefix)
            ? $"{signalEmoji} {signal.Type.ToString().ToUpper()} {signal.Symbol} {marketFlag}"
            : $"{modeEmoji} {modePrefix} {signal.Symbol} {marketFlag}";

        var message = $@"{title}

💪 Confidence: {signal.Confidence}%
📊 RSI: {signal.RSI:F1}
⚡ MACD: {signal.MACD_Histogram:F3}
💰 Entry: {currencySymbol}{signal.Price:F2}{currencyInfo}
📊 Volume: {FormatVolume(signal.Volume)}

🕐 STATUS: {marketStatus}";

        // Risk management (sempre in Euro per TradeRepublic)
        if (signal.StopLoss.HasValue && signal.TakeProfit.HasValue)
        {
            message += $@"

🛡️ RISK MANAGEMENT (TradeRepublic):
🔻 Stop Loss: {currencySymbol}{signal.StopLoss:F2} ({signal.StopLossPercent:F1}%)
🎯 Take Profit: {currencySymbol}{signal.TakeProfit:F2} ({signal.TakeProfitPercent:F1}%)
⚖️ Risk/Reward: 1:{signal.RiskRewardRatio:F1}";

            // 🌍 NUOVO: Position sizing in Euro
            if (signal.SuggestedShares.HasValue && signal.PositionValue.HasValue)
            {
                message += $@"
💼 Position: {signal.SuggestedShares} shares = {currencySymbol}{signal.PositionValue:F0}";

                if (signal.MaxRiskAmount.HasValue && signal.PotentialGainAmount.HasValue)
                {
                    message += $@"
📉 Max Risk: {currencySymbol}{signal.MaxRiskAmount:F0}
📈 Potential Gain: {currencySymbol}{signal.PotentialGainAmount:F0}";
                }
            }
        }

        // Consigli specifici per modalità
        if (mode == AnalysisMode.PreMarketWatch)
        {
            var timeUntilOpen = _smartMarketHours.GetTimeUntilMarketOpen(signal.Symbol);
            var hours = (int)timeUntilOpen.TotalHours;
            var minutes = timeUntilOpen.Minutes;

            message += $@"

⏰ TIMING:
📝 Market opens in {hours}h {minutes}m
🎯 Prepare limit order for gap play";
        }
        else if (mode == AnalysisMode.OffHoursMonitor)
        {
            message += $@"

🌙 OFF-HOURS:
📝 Consider for next trading session
⚠️ Verify at market open";
        }

        // Levels sempre in Euro
        if (signal.SupportLevel.HasValue && signal.ResistanceLevel.HasValue &&
            signal.SupportLevel > 0 && signal.ResistanceLevel > 0)
        {
            message += $@"

📈 LEVELS:
🟢 Support: {currencySymbol}{signal.SupportLevel:F2}
🔴 Resistance: {currencySymbol}{signal.ResistanceLevel:F2}";
        }

        // 🌍 NUOVO: Info conversione valuta (se applicabile)
        if (signal.OriginalCurrency != "EUR")
        {
            message += $@"

💱 CURRENCY: {signal.OriginalCurrency}→EUR @ {signal.ExchangeRate:F4}";
        }

        message += $@"

💡 {signal.Reason}

🕐 {DateTime.Now:HH:mm} {modeEmoji} (Hybrid + Euro Conversion)";

        return message;
    }

    private string GetOriginalCurrencySymbol(string currencyCode)
    {
        return currencyCode?.ToUpper() switch
        {
            "USD" => "$",
            "GBP" => "£",
            "CHF" => "CHF",
            "JPY" => "¥",
            "CAD" => "CAD$",
            "AUD" => "AUD$",
            _ => currencyCode ?? ""
        };
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