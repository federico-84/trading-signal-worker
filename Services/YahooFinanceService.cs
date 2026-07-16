using Newtonsoft.Json.Linq;
using PortfolioSignalWorker.Models;

namespace PortfolioSignalWorker.Services;

public class YahooFinanceService
{
    private readonly HttpClient _http;
    private readonly ILogger<YahooFinanceService> _logger;

    // Short-lived in-memory cache: avoids double Yahoo calls within the same analysis cycle (~1s apart)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (JObject Data, DateTime FetchedAt)> _requestCache = new();

    public YahooFinanceService(ILogger<YahooFinanceService> logger)
    {
        _logger = logger;
        _http = new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    }

    public async Task<JObject> GetHistoricalDataAsync(string symbol, int days = 50)
    {
        try
        {
            // Return cached response if fetched within the last 5 minutes (avoids double calls per analysis cycle)
            var cacheKey = $"{symbol}_{days}";
            if (_requestCache.TryGetValue(cacheKey, out var cached) &&
                (DateTime.UtcNow - cached.FetchedAt).TotalMinutes < 5)
            {
                _logger.LogDebug($"[YAHOO] 💾 {symbol} reusing cached response (< 5min old)");
                return cached.Data;
            }

            var endTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var startTime = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds();

            var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{symbol}?period1={startTime}&period2={endTime}&interval=1d";

            _logger.LogDebug($"[YAHOO] 📡 Calling {symbol}");
            _logger.LogDebug($"[YAHOO] 🔗 URL: {url}");

            var response = await _http.GetStringAsync(url);

            _logger.LogDebug($"[YAHOO] ✅ {symbol} response: {response.Length} chars");

            var data = JObject.Parse(response);

            // Controllo errori Yahoo Finance
            var error = data["chart"]?["error"];
            if (error != null && error.HasValues && error["code"] != null)
            {
                var errorCode = error["code"]?.Value<string>();
                var description = error["description"]?.Value<string>();

                _logger.LogWarning($"[YAHOO] ⚠️ {symbol} Yahoo error: {errorCode} - {description}");

                throw new Exception($"Yahoo Finance error for {symbol}: {errorCode} - {description}");
            }

            var yahooResult = data["chart"]?["result"]?[0];

            if (yahooResult == null)
            {
                _logger.LogWarning($"[YAHOO] ❌ {symbol} result is NULL!");
                throw new Exception($"No result from Yahoo for {symbol}");
            }

            var indicators = yahooResult["indicators"]?["quote"]?[0];
            var timestamps = yahooResult["timestamp"]?.ToObject<List<long>>() ?? new List<long>();

            _logger.LogDebug($"[YAHOO] 📊 {symbol} timestamps: {timestamps.Count}");

            if (indicators == null)
            {
                _logger.LogWarning($"[YAHOO] ❌ {symbol} indicators is NULL!");
                throw new Exception($"No historical data found for symbol {symbol}");
            }

            var closes = indicators["close"]?.ToObject<List<double?>>()?.Where(x => x.HasValue).Select(x => x.Value).ToList() ?? new List<double>();
            var opens = indicators["open"]?.ToObject<List<double?>>()?.Where(x => x.HasValue).Select(x => x.Value).ToList() ?? new List<double>();
            var highs = indicators["high"]?.ToObject<List<double?>>()?.Where(x => x.HasValue).Select(x => x.Value).ToList() ?? new List<double>();
            var lows = indicators["low"]?.ToObject<List<double?>>()?.Where(x => x.HasValue).Select(x => x.Value).ToList() ?? new List<double>();
            var volumes = indicators["volume"]?.ToObject<List<long?>>()?.Where(x => x.HasValue).Select(x => x.Value).ToList() ?? new List<long>();

            if (closes.Count == 0)
            {
                _logger.LogWarning($"[YAHOO] ❌ {symbol} returned 0 data points (HTTP 200 but empty arrays) — symbol may be unavailable or use a different ticker");
                throw new SymbolNotFoundException(symbol);
            }

            _logger.LogInformation($"[YAHOO] ✅ {symbol} SUCCESS! closes: {closes.Count}, volumes: {volumes.Count}");

            var result = new JObject
            {
                ["c"] = JArray.FromObject(closes),
                ["o"] = JArray.FromObject(opens),
                ["h"] = JArray.FromObject(highs),
                ["l"] = JArray.FromObject(lows),
                ["v"] = JArray.FromObject(volumes),
                ["t"] = JArray.FromObject(timestamps),
                ["s"] = "ok"
            };

            // Store in request cache for 5 minutes
            _requestCache[cacheKey] = (result, DateTime.UtcNow);
            return result;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning($"[YAHOO] ❌ {symbol} returned 404 — symbol may be delisted or invalid");
            throw new SymbolNotFoundException(symbol);
        }
        catch (SymbolNotFoundException)
        {
            throw; // Propagate unchanged — do not wrap in generic Exception
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError($"[YAHOO] 🔴 {symbol} HTTP ERROR: {ex.Message}");
            throw new Exception($"Network error fetching data for {symbol}: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError($"[YAHOO] 🔴 {symbol} EXCEPTION: {ex.GetType().Name} - {ex.Message}");
            throw new Exception($"Error fetching historical data for {symbol}: {ex.Message}", ex);
        }
    }

    public async Task<StockIndicator> GetIndicatorsAsync(string symbol)
    {
        try
        {
            _logger.LogDebug($"[INDICATORS] 🔍 Starting GetIndicatorsAsync for {symbol}");

            // Get historical data for calculations
            var historicalData = await GetHistoricalDataAsync(symbol, 100);

            // Extract closing prices
            var closes = historicalData["c"]?.ToObject<List<double>>() ?? new List<double>();
            var volumes = historicalData["v"]?.ToObject<List<long>>() ?? new List<long>();
            var highs = historicalData["h"]?.ToObject<List<double>>() ?? new List<double>();
            var lows = historicalData["l"]?.ToObject<List<double>>() ?? new List<double>();
            var opens = historicalData["o"]?.ToObject<List<double>>() ?? new List<double>();
            var timestamps = historicalData["t"]?.ToObject<List<long>>() ?? new List<long>();

            _logger.LogDebug($"[INDICATORS] 📊 {symbol} data: closes={closes.Count}, volumes={volumes.Count}");

            if (closes.Count < 26)
            {
                _logger.LogWarning($"[INDICATORS] ⚠️ {symbol} insufficient data: {closes.Count} days (need 26)");
                throw new InvalidOperationException($"Insufficient data for {symbol}. Got {closes.Count} days, need at least 26.");
            }

            // Calculate indicators
            var rsi = CalculateRSI(closes);
            var (macd, signal, histogram) = CalculateMACD(closes);
            var (prevMacd, prevSignal, prevHistogram) = CalculateMACD(closes.Take(closes.Count - 1).ToList());
            var crossUp = histogram > 0 && prevHistogram <= 0;

            // Use last element as current
            var currentPrice = closes.Last();
            var currentVolume = volumes.Last();
            var previousClose = closes.Count > 1 ? closes[closes.Count - 2] : currentPrice;
            var change = currentPrice - previousClose;
            var changePercent = previousClose != 0 ? (change / previousClose) * 100 : 0;
            var high = highs.Last();
            var low = lows.Last();
            var open = opens.Last();

            var pricePosition = CalculatePricePosition(currentPrice, high, low);
            var volatility = CalculateSimpleVolatility(currentPrice, previousClose);

            _logger.LogInformation($"[INDICATORS] ✅ {symbol} calculated: RSI={rsi:F2}, MACD={histogram:F4}, Price=${currentPrice:F2}");

            return new StockIndicator
            {
                Symbol = symbol,
                RSI = Math.Round(rsi, 2),
                MACD = Math.Round(macd, 4),
                MACD_Signal = Math.Round(signal, 4),
                MACD_Histogram = Math.Round(histogram, 4),
                MACD_Histogram_CrossUp = crossUp,
                Price = currentPrice,
                Volume = currentVolume,
                PreviousClose = previousClose,
                Change = change,
                ChangePercent = changePercent,
                DayHigh = high,
                DayLow = low,
                Open = open,
                PricePosition = pricePosition,
                DailyVolatility = volatility,
                CreatedAt = DateTimeOffset.FromUnixTimeSeconds(timestamps.Last()).UtcDateTime
            };
        }
        catch (SymbolNotFoundException)
        {
            throw; // Propagate unchanged so the worker can deactivate the symbol
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[INDICATORS] 🔴 Error calculating indicators for {symbol}");
            throw new Exception($"Error calculating indicators for {symbol}: {ex.Message}", ex);
        }
    }

    // YahooFinanceService.cs - AGGIUNGI questo metodo

    public async Task<JObject> GetQuoteAsync(string symbol)
    {
        try
        {
            _logger.LogDebug($"[QUOTE] 📡 Getting quote for {symbol}");

            // Usa GetHistoricalDataAsync con almeno 5 giorni per avere previousClose valido
            var historicalData = await GetHistoricalDataAsync(symbol, 5);

            var closes = historicalData["c"]?.ToObject<List<double>>() ?? new List<double>();
            var volumes = historicalData["v"]?.ToObject<List<long>>() ?? new List<long>();
            var highs = historicalData["h"]?.ToObject<List<double>>() ?? new List<double>();
            var lows = historicalData["l"]?.ToObject<List<double>>() ?? new List<double>();
            var opens = historicalData["o"]?.ToObject<List<double>>() ?? new List<double>();

            if (closes.Count == 0)
            {
                throw new Exception($"No quote data available for {symbol}");
            }

            var currentPrice = closes.Last();
            var previousClose = closes.Count > 1 ? closes[closes.Count - 2] : currentPrice;
            var change = currentPrice - previousClose;
            var changePercent = previousClose != 0 ? (change / previousClose) * 100 : 0;

            return new JObject
            {
                ["c"] = currentPrice,           // current price
                ["pc"] = previousClose,         // previous close
                ["d"] = change,                 // change
                ["dp"] = changePercent,         // change percent
                ["h"] = highs.Last(),           // high
                ["l"] = lows.Last(),            // low
                ["o"] = opens.Last(),           // open
                ["v"] = volumes.Last()          // volume
            };
        }
        catch (SymbolNotFoundException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[QUOTE] 🔴 Error fetching quote for {symbol}");
            throw new Exception($"Error fetching quote for {symbol}: {ex.Message}", ex);
        }
    }

    // Esponi questi metodi come public per uso esterno
    public double CalculateRSI(List<double> prices, int period = 14)
    {
        if (prices.Count < period + 1) return 50;

        // Build per-bar gain/loss arrays
        var gains = new double[prices.Count - 1];
        var losses = new double[prices.Count - 1];
        for (int i = 0; i < prices.Count - 1; i++)
        {
            var change = prices[i + 1] - prices[i];
            gains[i] = change > 0 ? change : 0;
            losses[i] = change < 0 ? -change : 0;
        }

        // Seed with simple average of first 'period' values (Wilder's method)
        double avgGain = gains.Take(period).Average();
        double avgLoss = losses.Take(period).Average();

        // Wilder's smoothing: avgGain = (prevAvgGain * (period-1) + currentGain) / period
        for (int i = period; i < gains.Length; i++)
        {
            avgGain = (avgGain * (period - 1) + gains[i]) / period;
            avgLoss = (avgLoss * (period - 1) + losses[i]) / period;
        }

        if (avgLoss == 0) return 100;

        var rs = avgGain / avgLoss;
        return 100 - (100 / (1 + rs));
    }

    public (double macd, double signal, double histogram) CalculateMACD(List<double> prices)
    {
        if (prices.Count < 26) return (0, 0, 0);

        const double k12 = 2.0 / 13; // multiplier EMA12
        const double k26 = 2.0 / 27; // multiplier EMA26
        const double k9  = 2.0 / 10; // multiplier EMA9 (signal line)

        // Seed EMA12 con media semplice dei primi 12 prezzi
        double ema12 = prices.Take(12).Average();
        // Aggiorna EMA12 per i prezzi 12..25 (per sincronizzarsi con EMA26 al periodo 25)
        for (int i = 12; i <= 25; i++)
            ema12 = ema12 + k12 * (prices[i] - ema12);

        // Seed EMA26 con media semplice dei primi 26 prezzi
        double ema26 = prices.Take(26).Average();

        // Primo valore MACD al periodo 25 (entrambe le EMA sono "al" prezzo[25])
        var macdLine = new List<double>(prices.Count - 25);
        macdLine.Add(ema12 - ema26);

        // Costruisci il resto della MACD line in O(n) con aggiornamenti incrementali
        for (int i = 26; i < prices.Count; i++)
        {
            ema12 = ema12 + k12 * (prices[i] - ema12);
            ema26 = ema26 + k26 * (prices[i] - ema26);
            macdLine.Add(ema12 - ema26);
        }

        double currentMacd = macdLine[^1];

        if (macdLine.Count < 9)
            return (currentMacd, currentMacd, 0);

        // Signal line: EMA9 della MACD line, anch'essa incrementale
        double signalEma = macdLine.Take(9).Average();
        for (int i = 9; i < macdLine.Count; i++)
            signalEma = signalEma + k9 * (macdLine[i] - signalEma);

        return (currentMacd, signalEma, currentMacd - signalEma);
    }

    private double CalculateEMA(List<double> prices, int period)
    {
        if (prices.Count == 0) return 0;
        if (prices.Count < period) return prices.Average();

        var multiplier = 2.0 / (period + 1);
        var ema = prices.Take(period).Average();

        for (int i = period; i < prices.Count; i++)
        {
            ema = (prices[i] - ema) * multiplier + ema;
        }

        return ema;
    }

    private double CalculatePricePosition(double current, double high, double low)
    {
        if (high == low) return 50.0; // ⬅️ CAMBIA da "Middle" a 50.0

        var position = ((current - low) / (high - low)) * 100;
        return position; // ⬅️ Ora ritorna double
    }

    private double CalculateSimpleVolatility(double current, double previous)
    {
        if (previous == 0) return 0;
        return Math.Abs((current - previous) / previous) * 100;
    }

    public async Task<string?> TryGetIsinAsync(string symbol)
    {
        try
        {
            var url = $"https://query2.finance.yahoo.com/v10/finance/quoteSummary/{symbol}?modules=assetProfile";
            var response = await _http.GetStringAsync(url);
            var data = JObject.Parse(response);

            var profile = data["quoteSummary"]?["result"]?[0]?["assetProfile"];
            var isin = profile?["isin"]?.Value<string>();

            if (!string.IsNullOrEmpty(isin))
            {
                _logger.LogInformation($"[ISIN] ✅ {symbol} → {isin}");
                return isin;
            }

            _logger.LogDebug($"[ISIN] ℹ️ {symbol}: not available from Yahoo (populate manually in MongoDB)");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug($"[ISIN] ⚠️ {symbol}: lookup failed — {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// Lanciata quando un simbolo restituisce 404 da Yahoo Finance
/// (simbolo delisted, non esistente, o ticker errato).
/// </summary>
public class SymbolNotFoundException : Exception
{
    public string Symbol { get; }

    public SymbolNotFoundException(string symbol)
        : base($"Symbol '{symbol}' not found on Yahoo Finance (404) — may be delisted or invalid")
    {
        Symbol = symbol;
    }
}