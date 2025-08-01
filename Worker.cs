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
        _logger.LogInformation("🚀 Worker started with HYBRID MARKET STRATEGY + Risk Management");

        // Initialize watchlist on first run
        var watchlistCount = await _mongo.GetWatchlistCount();
        if (watchlistCount == 0)
        {
            _logger.LogInformation("No watchlist found, initializing...");
            await _symbolSelection.InitializeWatchlist();
        }

        // Log mercati all'avvio
        _smartMarketHours.LogCurrentMarketStatus();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Get symbols due for analysis
                var allSymbolsDue = await _symbolSelection.GetSymbolsDueForAnalysis();

                // ===== HYBRID FILTERING: Filtra per modalità di analisi =====
                var symbolsToProcess = new List<(WatchlistSymbol symbol, AnalysisMode mode)>();
                var skippedCount = 0;

                foreach (var symbol in allSymbolsDue)
                {
                    var analysisMode = _smartMarketHours.GetAnalysisMode(symbol.Symbol);

                    if (analysisMode == AnalysisMode.Skip)
                    {
                        skippedCount++;
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
                                    $"{signal.Type} ({signal.Confidence}%)");
                            }
                            else
                            {
                                _logger.LogDebug($"⏸️ Signal for {watchlistSymbol.Symbol} below {analysisMode} threshold " +
                                    $"({signal.Confidence}% < {_smartMarketHours.GetConfidenceThreshold(analysisMode)}%)");
                            }
                        }

                        // Update next analysis time basato sulla modalità
                        var nextAnalysisDelay = _smartMarketHours.GetAnalysisFrequency(analysisMode, watchlistSymbol.Tier);
                        var nextAnalysis = DateTime.UtcNow.Add(nextAnalysisDelay);
                        await _symbolSelection.UpdateSymbolNextAnalysis(watchlistSymbol.Symbol, nextAnalysis);

                        processedCount++;

                        // Dynamic rate limiting basato sulla modalità
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
                        _logger.LogError(ex, "Error processing {symbol} in {mode} mode",
                            watchlistSymbol.Symbol, analysisMode);
                    }
                }

                _logger.LogInformation($"📈 Cycle completed: {processedCount} processed, {signalsSentCount} signals sent");

                // Daily optimization (at midnight)
                if (DateTime.Now.Hour == 0 && DateTime.Now.Minute < 5)
                {
                    _logger.LogInformation("🔄 Starting daily watchlist optimization...");
                    await _symbolSelection.OptimizeWatchlist();

                    // Log market status per nuovo giorno
                    _smartMarketHours.LogCurrentMarketStatus();
                }

                // Wait before next cycle (più lungo se tutti i mercati sono chiusi)
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

        var currency = GetCurrencySymbol(signal.Symbol, market);
        var marketStatus = _smartMarketHours.GetModeDescription(mode, signal.Symbol);

        // Titolo con prefisso modalità
        var title = string.IsNullOrEmpty(modePrefix)
            ? $"{signalEmoji} {signal.Type.ToString().ToUpper()} {signal.Symbol} {marketFlag}"
            : $"{modeEmoji} {modePrefix} {signal.Symbol} {marketFlag}";

        var message = $@"{title}

💪 Confidence: {signal.Confidence}%
📊 RSI: {signal.RSI:F1}
⚡ MACD: {signal.MACD_Histogram:F3}
💰 Entry: {currency}{signal.Price:F2}
📊 Volume: {FormatVolume(signal.Volume)}

🕐 STATUS: {marketStatus}";

        // Risk management (solo per segnali completi)
        if (signal.StopLoss.HasValue && signal.TakeProfit.HasValue)
        {
            message += $@"

🛡️ RISK MANAGEMENT:
🔻 Stop Loss: {currency}{signal.StopLoss:F2} ({signal.StopLossPercent:F1}%)
🎯 Take Profit: {currency}{signal.TakeProfit:F2} ({signal.TakeProfitPercent:F1}%)
⚖️ Risk/Reward: 1:{signal.RiskRewardRatio:F1}";
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

        // Levels per tutti
        if (signal.SupportLevel.HasValue && signal.ResistanceLevel.HasValue &&
            signal.SupportLevel > 0 && signal.ResistanceLevel > 0)
        {
            message += $@"

📈 LEVELS:
🟢 Support: {currency}{signal.SupportLevel:F2}
🔴 Resistance: {currency}{signal.ResistanceLevel:F2}";
        }

        message += $@"

💡 {signal.Reason}

🕐 {DateTime.Now:HH:mm} {modeEmoji} (Hybrid Strategy)";

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