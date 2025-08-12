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
                        _logger.LogInformation($"🔍 Analyzing {watchlistSymbol.Symbol} - {modeDescription}");

                        // Get indicators
                        var indicator = await _yahooFinance.GetIndicatorsAsync(watchlistSymbol.Symbol);
                        _logger.LogDebug($"📊 {watchlistSymbol.Symbol}: Price=${indicator.Price:F2}, RSI={indicator.RSI:F1}, Volume={indicator.Volume:N0}");

                        // 🚀 Breakout analysis
                        var breakoutSignal = await _breakoutDetection.AnalyzeBreakoutPotentialAsync(watchlistSymbol.Symbol);
                        if (breakoutSignal != null)
                        {
                            _logger.LogDebug($"🎯 {watchlistSymbol.Symbol}: Breakout {breakoutSignal.BreakoutType} (Score: {breakoutSignal.BreakoutScore}/100)");
                        }

                        // Analyze for traditional signals
                        var signal = await _signalFilter.AnalyzeSignalAsync(watchlistSymbol.Symbol, indicator);
                        if (signal != null)
                        {
                            _logger.LogDebug($"📈 {watchlistSymbol.Symbol}: Traditional signal {signal.Type} (Confidence: {signal.Confidence}%)");
                        }

                        // Save indicator
                        await _mongo.SaveIndicatorAsync(indicator);

                        // 🚀 Gestione breakout signals
                        if (breakoutSignal != null && ShouldSendBreakoutSignal(breakoutSignal, analysisMode))
                        {
                            _logger.LogInformation($"🎯 Processing breakout signal for {watchlistSymbol.Symbol}: {breakoutSignal.BreakoutType} ({breakoutSignal.BreakoutScore}/100)");

                            var breakoutDocument = MongoService.ConvertToDocument(breakoutSignal);
                            await _mongo.SaveBreakoutSignalAsync(breakoutDocument);

                            var breakoutTradingSignal = ConvertBreakoutToTradingSignal(breakoutSignal);
                            breakoutTradingSignal = await _riskManagement.EnhanceSignalWithRiskManagement(breakoutTradingSignal);

                            if (await _signalFilter.ValidateSignalComprehensiveAsync(breakoutTradingSignal))
                            {
                                await _mongo.SaveSignalAsync(breakoutTradingSignal);
                                await _mongo.MarkBreakoutSignalAsSentAsync(breakoutDocument.Id, breakoutTradingSignal.Id);

                                var breakoutMessage = FormatBreakoutMessage(breakoutSignal, breakoutTradingSignal, analysisMode, watchlistSymbol.Market ?? "US");
                                await _telegram.SendMessageAsync(breakoutMessage);
                                await _signalFilter.MarkSignalAsSentAsync(breakoutTradingSignal.Id);

                                signalsSentCount++;
                                _logger.LogInformation($"✅ BREAKOUT signal sent for {watchlistSymbol.Symbol}");
                                continue;
                            }
                            else
                            {
                                _logger.LogWarning($"⚠️ Breakout signal validation failed for {watchlistSymbol.Symbol}");
                            }
                        }

                        // Traditional signal processing
                        if (signal != null)
                        {
                            if (!await _signalFilter.ValidateSignalComprehensiveAsync(signal))
                            {
                                _logger.LogDebug($"⚠️ Traditional signal validation failed for {watchlistSymbol.Symbol}");
                                continue;
                            }

                            var shouldSend = _smartMarketHours.ShouldSendSignal(signal, analysisMode);
                            var enhancedShouldSend = shouldSend && ApplyEnhancedFilters(signal, analysisMode);

                            if (enhancedShouldSend)
                            {
                                signal = await _riskManagement.EnhanceSignalWithRiskManagement(signal);

                                if (!await _signalFilter.ValidateSignalComprehensiveAsync(signal))
                                {
                                    _logger.LogWarning($"🚨 Signal post-risk-management validation failed for {watchlistSymbol.Symbol}");
                                    continue;
                                }

                                await _mongo.SaveSignalAsync(signal);

                                var message = FormatHybridMessage(signal, analysisMode, watchlistSymbol.Market ?? "US");
                                await _telegram.SendMessageAsync(message);
                                await _signalFilter.MarkSignalAsSentAsync(signal.Id);

                                signalsSentCount++;
                                _logger.LogInformation($"✅ TRADITIONAL signal sent for {watchlistSymbol.Symbol}: {signal.Type} ({signal.Confidence}%)");
                            }
                            else
                            {
                                var reason = !shouldSend ?
                                    $"below {analysisMode} threshold ({signal.Confidence}% < {_smartMarketHours.GetConfidenceThreshold(analysisMode)}%)" :
                                    "failed enhanced quality filters";

                                _logger.LogDebug($"⏸️ Signal for {watchlistSymbol.Symbol} skipped: {reason}");
                            }
                        }

                        // Update next analysis time
                        var nextAnalysisDelay = _smartMarketHours.GetAnalysisFrequency(analysisMode, watchlistSymbol.Tier);
                        var nextAnalysis = DateTime.UtcNow.Add(nextAnalysisDelay);
                        await _symbolSelection.UpdateSymbolNextAnalysis(watchlistSymbol.Symbol, nextAnalysis);

                        processedCount++;

                        // Rate limiting
                        var delay = analysisMode switch
                        {
                            AnalysisMode.FullAnalysis => 800,
                            AnalysisMode.PreMarketWatch => 1000,
                            AnalysisMode.OffHoursMonitor => 1200,
                            _ => 1000
                        };

                        await Task.Delay(delay, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing {symbol} in {mode} mode", watchlistSymbol.Symbol, analysisMode);
                    }
                }

                _logger.LogInformation($"📈 Cycle completed: {processedCount} processed, {signalsSentCount} signals sent");

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
    private bool ApplyEnhancedFilters(TradingSignal signal, AnalysisMode analysisMode)
    {
        var filters = new List<(bool condition, string reason)>();

        // 1. Filtro confidence dinamico per modalità
        var minConfidence = analysisMode switch
        {
            AnalysisMode.FullAnalysis => 70,      // Mercato aperto: più permissivo
            AnalysisMode.PreMarketWatch => 80,    // Pre-market: più selettivo  
            AnalysisMode.OffHoursMonitor => 90,   // Off-hours: solo i migliori
            _ => 70
        };

        filters.Add((signal.Confidence >= minConfidence, $"Confidence {signal.Confidence}% < {minConfidence}% for {analysisMode}"));

        // 2. Filtro tipo segnale
        var allowedTypes = analysisMode switch
        {
            AnalysisMode.FullAnalysis => new[] { SignalType.Buy, SignalType.Warning },
            AnalysisMode.PreMarketWatch => new[] { SignalType.Buy },  // Solo BUY in pre-market
            AnalysisMode.OffHoursMonitor => new[] { SignalType.Buy }, // Solo BUY off-hours
            _ => new[] { SignalType.Buy }
        };

        filters.Add((allowedTypes.Contains(signal.Type), $"Signal type {signal.Type} not allowed for {analysisMode}"));

        // 3. Filtro volume relativo al simbolo
        var minVolumeBySymbol = signal.Symbol switch
        {
            var s when s.StartsWith("TSLA") => 15_000_000,  // Tesla: volume alto
            var s when s.EndsWith(".MI") => 200_000,        // Borsa Milano: volume medio
            var s when s.EndsWith(".AS") => 500_000,        // Amsterdam: volume medio
            _ => 1_000_000  // Default US stocks
        };

        filters.Add((signal.Volume >= minVolumeBySymbol, $"Volume {signal.Volume:N0} < {minVolumeBySymbol:N0} for {signal.Symbol}"));

        // 4. Filtro RSI ragionevole per tipo segnale
        if (signal.Type == SignalType.Buy)
        {
            filters.Add((signal.RSI < 40, $"RSI {signal.RSI:F1} too high for BUY signal (should be < 40)"));
        }
        else if (signal.Type == SignalType.Warning)
        {
            filters.Add((signal.RSI < 30, $"RSI {signal.RSI:F1} not oversold enough for WARNING (should be < 30)"));
        }

        // 5. Filtro Position Sizing (se presente)
        if (signal.SuggestedShares.HasValue)
        {
            filters.Add((signal.SuggestedShares > 0, $"Suggested Shares {signal.SuggestedShares} <= 0"));
        }

        // 6. Filtro Risk/Reward (se presente)
        if (signal.RiskRewardRatio.HasValue)
        {
            filters.Add((signal.RiskRewardRatio >= 1.5, $"Risk/Reward {signal.RiskRewardRatio:F1} < 1.5"));
        }

        // Verifica tutti i filtri
        var failedFilters = filters.Where(f => !f.condition).ToList();

        if (failedFilters.Any())
        {
            _logger.LogDebug($"Enhanced filters FAILED for {signal.Symbol}:");
            foreach (var (_, reason) in failedFilters)
            {
                _logger.LogDebug($"  ❌ {reason}");
            }
            return false;
        }

        _logger.LogDebug($"✅ Enhanced filters PASSED for {signal.Symbol} ({filters.Count} checks)");
        return true;
    }
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

    private int CalculateSymbolPriority(WatchlistSymbol symbol)
    {
        var priority = (int)symbol.VolatilityLevel * 10; // Base volatility score

        if (symbol.IsBreakoutCandidate) priority += 20;
        if (symbol.ConsecutiveHighVolDays >= 3) priority += 15;
        if (symbol.AverageVolatilityPercent > 10) priority += 10;

        return priority;
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