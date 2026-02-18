using Newtonsoft.Json.Linq;
using PortfolioSignalWorker.Models;

namespace PortfolioSignalWorker.Services;

public class YahooFinanceService
{
    private readonly HttpClient _http;
    private readonly ILogger<YahooFinanceService> _logger;

    public YahooFinanceService(ILogger<YahooFinanceService> logger)
    {
        _logger = logger;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    }

    public async Task<JObject> GetHistoricalDataAsync(string symbol, int days = 50)
    {
        try
        {
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

            var result = data["chart"]?["result"]?[0];

            if (result == null)
            {
                _logger.LogWarning($"[YAHOO] ❌ {symbol} result is NULL!");
                throw new Exception($"No result from Yahoo for {symbol}");
            }

            var indicators = result["indicators"]?["quote"]?[0];
            var timestamps = result["timestamp"]?.ToObject<List<long>>() ?? new List<long>();

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

            _logger.LogInformation($"[YAHOO] ✅ {symbol} SUCCESS! closes: {closes.Count}, volumes: {volumes.Count}");

            return new JObject
            {
                ["c"] = JArray.FromObject(closes),
                ["o"] = JArray.FromObject(opens),
                ["h"] = JArray.FromObject(highs),
                ["l"] = JArray.FromObject(lows),
                ["v"] = JArray.FromObject(volumes),
                ["t"] = JArray.FromObject(timestamps),
                ["s"] = "ok"
            };
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

            // Usa GetHistoricalDataAsync per ottenere l'ultimo dato
            var historicalData = await GetHistoricalDataAsync(symbol, 1);

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

        var gains = new List<double>();
        var losses = new List<double>();

        for (int i = 1; i < prices.Count; i++)
        {
            var change = prices[i] - prices[i - 1];
            gains.Add(change > 0 ? change : 0);
            losses.Add(change < 0 ? Math.Abs(change) : 0);
        }

        var avgGain = gains.TakeLast(period).Average();
        var avgLoss = losses.TakeLast(period).Average();

        if (avgLoss == 0) return 100;

        var rs = avgGain / avgLoss;
        var rsi = 100 - (100 / (1 + rs));

        return rsi;
    }

    public (double macd, double signal, double histogram) CalculateMACD(List<double> prices)
    {
        if (prices.Count < 26) return (0, 0, 0);

        var ema12 = CalculateEMA(prices, 12);
        var ema26 = CalculateEMA(prices, 26);
        var macd = ema12 - ema26;

        var macdLine = new List<double>();
        for (int i = 25; i < prices.Count; i++)
        {
            var e12 = CalculateEMA(prices.Take(i + 1).ToList(), 12);
            var e26 = CalculateEMA(prices.Take(i + 1).ToList(), 26);
            macdLine.Add(e12 - e26);
        }

        var signal = CalculateEMA(macdLine, 9);
        var histogram = macd - signal;

        return (macd, signal, histogram);
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
}