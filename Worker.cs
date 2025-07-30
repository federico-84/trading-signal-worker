using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PortfolioSignalWorker.Models;
using PortfolioSignalWorker.Services;

public class Worker : BackgroundService
{
    private readonly YahooFinanceService _yahooFinance; // CAMBIATO DA FinnhubService
    private readonly TelegramService _telegram;
    private readonly MongoService _mongo;
    private readonly SignalFilterService _signalFilter;
    private readonly SymbolSelectionService _symbolSelection;
    private readonly MarketHoursService _marketHours; // AGGIUNTO
    private readonly ILogger<Worker> _logger;

    public Worker(
        YahooFinanceService yahooFinance, // CAMBIATO DA FinnhubService
        TelegramService telegram,
        MongoService mongo,
        SignalFilterService signalFilter,
        SymbolSelectionService symbolSelection,
        MarketHoursService marketHours, // AGGIUNTO
        ILogger<Worker> logger)
    {
        _yahooFinance = yahooFinance; // CAMBIATO DA _finnhub
        _telegram = telegram;
        _mongo = mongo;
        _signalFilter = signalFilter;
        _symbolSelection = symbolSelection;
        _marketHours = marketHours; // AGGIUNTO
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Worker started with Yahoo Finance service and dynamic symbol selection");

        // Initialize watchlist on first run
        var watchlistCount = await _mongo.GetWatchlistCount();
        if (watchlistCount == 0)
        {
            _logger.LogInformation("No watchlist found, initializing with 50 best symbols using Yahoo Finance...");
            await _symbolSelection.InitializeWatchlist();
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Get symbols due for analysis
                var symbolsDue = await _symbolSelection.GetSymbolsDueForAnalysis();

                // TEMPORARILY DISABLE market hours filtering for debugging
                // Filter symbols based on market hours (optional optimization)
                // if (_marketHours != null)
                // {
                //     symbolsDue = symbolsDue.Where(s => _marketHours.ShouldAnalyzeSymbol(s.Symbol)).ToList();
                // }

                _logger.LogInformation($"Processing {symbolsDue.Count} symbols due for analysis with Yahoo Finance");
                _logger.LogInformation($"Next 5 symbols: {string.Join(", ", symbolsDue.Take(5).Select(s => s.Symbol))}");

                foreach (var watchlistSymbol in symbolsDue)
                {
                    try
                    {
                        // Log market info for European stocks
                        if (watchlistSymbol.Symbol.Contains("."))
                        {
                            var marketStatus = _marketHours?.GetMarketStatus(watchlistSymbol.Symbol) ?? "Unknown";
                            _logger.LogDebug($"Analyzing {watchlistSymbol.Symbol} - {marketStatus}");
                        }

                        // Get indicators using Yahoo Finance service
                        var indicator = await _yahooFinance.GetIndicatorsAsync(watchlistSymbol.Symbol);

                        // PRIMO: Analyze for signals (questo enrichirà l'indicator)
                        var signal = await _signalFilter.AnalyzeSignalAsync(watchlistSymbol.Symbol, indicator);

                        // SECONDO: Save indicator DOPO l'enrichment
                        await _mongo.SaveIndicatorAsync(indicator);

                        if (signal != null)
                        {
                            await _mongo.SaveSignalAsync(signal);
                            var message = FormatSignalMessage(signal, watchlistSymbol.Market ?? "US");
                            await _telegram.SendMessageAsync(message);
                            await _signalFilter.MarkSignalAsSentAsync(signal.Id);

                            _logger.LogInformation($"Signal sent for {watchlistSymbol.Symbol}: {signal.Type} ({signal.Confidence}%)");
                        }

                        // Update next analysis time
                        var nextAnalysis = DateTime.UtcNow.Add(watchlistSymbol.MonitoringFrequency);
                        await _symbolSelection.UpdateSymbolNextAnalysis(watchlistSymbol.Symbol, nextAnalysis);

                        // Rate limiting - Yahoo Finance è più permissivo ma comunque limitiamo
                        await Task.Delay(800, stoppingToken); // Ridotto da 1500ms
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing {symbol} with Yahoo Finance", watchlistSymbol.Symbol);
                    }
                }

                // Daily optimization (at midnight)
                if (DateTime.Now.Hour == 0 && DateTime.Now.Minute < 5)
                {
                    _logger.LogInformation("Starting daily watchlist optimization...");
                    await _symbolSelection.OptimizeWatchlist();
                }

                // Wait before next cycle
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in main worker loop");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }

    private string FormatSignalMessage(TradingSignal signal, string market = "US")
    {
        var emoji = signal.Type switch
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

        // Determine currency based on symbol
        var currency = GetCurrencySymbol(signal.Symbol, market);

        return $@"{emoji} {signal.Type.ToString().ToUpper()} {signal.Symbol} {marketFlag}

💪 Confidence: {signal.Confidence}%
📊 RSI: {signal.RSI:F1}
⚡ MACD: {signal.MACD_Histogram:F3}
💰 Price: {currency}{signal.Price:F2}

{signal.Reason}

🕐 {DateTime.Now:HH:mm} (Yahoo Finance)";
    }

    private string GetCurrencySymbol(string symbol, string market)
    {
        // European symbols → Euro
        if (symbol.Contains(".MI") || symbol.Contains(".AS") ||
            symbol.Contains(".DE") || symbol.Contains(".PA"))
            return "€";

        // Swiss symbols → Franchi
        if (symbol.Contains(".SW"))
            return "CHF ";

        // UK symbols → Sterline
        if (symbol.Contains(".L"))
            return "£";

        // US symbols → Dollari
        return "$";
    }
}