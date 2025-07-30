using Newtonsoft.Json.Linq;
using PortfolioSignalWorker.Models;

namespace PortfolioSignalWorker.Services;

public class YahooFinanceService
{
    private readonly HttpClient _http;

    public YahooFinanceService()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    }

    public async Task<JObject> GetQuoteAsync(string symbol)
    {
        try
        {
            var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{symbol}";
            var response = await _http.GetStringAsync(url);
            var data = JObject.Parse(response);

            // Controllo più robusto per errori Yahoo Finance
            var chart = data["chart"];
            var error = chart?["error"];

            // Verifica se error ha sia values che non è empty
            if (error != null && error.HasValues && error["code"] != null)
            {
                var errorCode = error["code"]?.Value<string>();
                var description = error["description"]?.Value<string>();
                throw new Exception($"Yahoo Finance error for {symbol}: {errorCode} - {description}");
            }

            // Controllo se result è null o vuoto
            var results = chart?["result"];
            if (results == null || !results.HasValues || results.Count() == 0)
            {
                throw new Exception($"No data found for symbol {symbol} - may be delisted or invalid");
            }

            var result = results[0];
            var meta = result?["meta"];

            if (meta == null || !meta.HasValues)
            {
                throw new Exception($"No metadata found for symbol {symbol}");
            }

            // Estrai i dati più recenti
            var currentPrice = meta["regularMarketPrice"]?.Value<double>() ?? 0;
            var previousClose = meta["previousClose"]?.Value<double>() ?? 0;
            var change = currentPrice - previousClose;
            var changePercent = previousClose != 0 ? (change / previousClose) * 100 : 0;

            // Verifica che abbiamo dati validi
            if (currentPrice <= 0)
            {
                throw new Exception($"Invalid price data for symbol {symbol}");
            }

            return new JObject
            {
                ["c"] = currentPrice,           // current price
                ["pc"] = previousClose,         // previous close
                ["d"] = change,                 // change
                ["dp"] = changePercent,         // change percent
                ["h"] = meta["regularMarketDayHigh"]?.Value<double>() ?? 0,    // high
                ["l"] = meta["regularMarketDayLow"]?.Value<double>() ?? 0,     // low
                ["o"] = meta["regularMarketOpen"]?.Value<double>() ?? 0,       // open
                ["v"] = meta["regularMarketVolume"]?.Value<long>() ?? 0        // volume
            };
        }
        catch (Exception ex)
        {
            throw new Exception($"Error fetching quote for {symbol}: {ex.Message}", ex);
        }
    }

    public async Task<JObject> GetHistoricalDataAsync(string symbol, int days = 50)
    {
        try
        {
            var endTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var startTime = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds();

            var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{symbol}?period1={startTime}&period2={endTime}&interval=1d";
            var response = await _http.GetStringAsync(url);
            var data = JObject.Parse(response);

            // Controllo errori Yahoo Finance
            var error = data["chart"]?["error"];
            if (error != null && error.HasValues && error["code"] != null)
            {
                var errorCode = error["code"]?.Value<string>();
                var description = error["description"]?.Value<string>();
                throw new Exception($"Yahoo Finance error for {symbol}: {errorCode} - {description}");
            }

            var result = data["chart"]?["result"]?[0];
            var indicators = result?["indicators"]?["quote"]?[0];
            var timestamps = result?["timestamp"]?.ToObject<List<long>>() ?? new List<long>();

            if (indicators == null)
            {
                throw new Exception($"No historical data found for symbol {symbol}");
            }

            var closes = indicators["close"]?.ToObject<List<double?>>()?.Where(x => x.HasValue).Select(x => x.Value).ToList() ?? new List<double>();
            var opens = indicators["open"]?.ToObject<List<double?>>()?.Where(x => x.HasValue).Select(x => x.Value).ToList() ?? new List<double>();
            var highs = indicators["high"]?.ToObject<List<double?>>()?.Where(x => x.HasValue).Select(x => x.Value).ToList() ?? new List<double>();
            var lows = indicators["low"]?.ToObject<List<double?>>()?.Where(x => x.HasValue).Select(x => x.Value).ToList() ?? new List<double>();
            var volumes = indicators["volume"]?.ToObject<List<long?>>()?.Where(x => x.HasValue).Select(x => x.Value).ToList() ?? new List<long>();

            return new JObject
            {
                ["c"] = JArray.FromObject(closes),      // close prices
                ["o"] = JArray.FromObject(opens),       // open prices  
                ["h"] = JArray.FromObject(highs),       // high prices
                ["l"] = JArray.FromObject(lows),        // low prices
                ["v"] = JArray.FromObject(volumes),     // volumes
                ["t"] = JArray.FromObject(timestamps),  // timestamps
                ["s"] = "ok"                            // status
            };
        }
        catch (Exception ex)
        {
            throw new Exception($"Error fetching historical data for {symbol}: {ex.Message}", ex);
        }
    }

    public async Task<StockIndicator> GetIndicatorsAsync(string symbol)
    {
        try
        {
            // Get historical data for calculations
            var historicalData = await GetHistoricalDataAsync(symbol, 50);
            var currentQuote = await GetQuoteAsync(symbol);

            // Extract closing prices
            var closePrices = historicalData["c"]?.ToObject<List<double>>() ?? new List<double>();

            if (closePrices.Count < 26)
            {
                throw new InvalidOperationException($"Insufficient data for {symbol}. Got {closePrices.Count} days, need at least 26.");
            }

            // Calculate indicators
            var rsi = CalculateRSI(closePrices);
            var (macd, signal, histogram) = CalculateMACD(closePrices);

            // Check for MACD crossover
            var (prevMacd, prevSignal, prevHistogram) = CalculateMACD(closePrices.Take(closePrices.Count - 1).ToList());
            var crossUp = histogram > 0 && prevHistogram <= 0;

            // Get current data from quote
            var price = currentQuote["c"]?.Value<double>() ?? 0;
            var volume = currentQuote["v"]?.Value<long>() ?? 0;
            var previousClose = currentQuote["pc"]?.Value<double>() ?? 0;
            var change = currentQuote["d"]?.Value<double>() ?? 0;
            var changePercent = currentQuote["dp"]?.Value<double>() ?? 0;
            var high = currentQuote["h"]?.Value<double>() ?? 0;
            var low = currentQuote["l"]?.Value<double>() ?? 0;
            var open = currentQuote["o"]?.Value<double>() ?? 0;

            // Calculate additional indicators
            var pricePosition = CalculatePricePosition(price, high, low);
            var volatility = CalculateSimpleVolatility(price, previousClose);

            return new StockIndicator
            {
                Symbol = symbol,
                RSI = Math.Round(rsi, 2),
                MACD = Math.Round(macd, 4),
                MACD_Signal = Math.Round(signal, 4),
                MACD_Histogram = Math.Round(histogram, 4),
                MACD_Histogram_CrossUp = crossUp,
                Price = price,
                Volume = volume,

                // Yahoo Finance specific data
                PreviousClose = previousClose,
                Change = change,
                ChangePercent = changePercent,
                DayHigh = high,
                DayLow = low,
                Open = open,
                PricePosition = pricePosition,
                DailyVolatility = volatility,

                CreatedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            throw new Exception($"Error calculating indicators for {symbol}: {ex.Message}", ex);
        }
    }

    // Metodi di calcolo identici a quelli del FinnhubService
    private static double CalculateRSI(List<double> prices, int period = 14)
    {
        if (prices.Count < period + 1)
            return 50;

        var gains = new List<double>();
        var losses = new List<double>();

        for (int i = 1; i < prices.Count; i++)
        {
            var change = prices[i] - prices[i - 1];
            gains.Add(change > 0 ? change : 0);
            losses.Add(change < 0 ? Math.Abs(change) : 0);
        }

        var avgGain = gains.Take(period).Average();
        var avgLoss = losses.Take(period).Average();

        for (int i = period; i < gains.Count; i++)
        {
            avgGain = (avgGain * (period - 1) + gains[i]) / period;
            avgLoss = (avgLoss * (period - 1) + losses[i]) / period;
        }

        if (avgLoss == 0)
            return 100;

        var rs = avgGain / avgLoss;
        return 100 - (100 / (1 + rs));
    }

    private static (double macd, double signal, double histogram) CalculateMACD(List<double> prices, int fastPeriod = 12, int slowPeriod = 26, int signalPeriod = 9)
    {
        if (prices.Count < slowPeriod)
            return (0, 0, 0);

        var fastEMA = CalculateEMA(prices, fastPeriod);
        var slowEMA = CalculateEMA(prices, slowPeriod);
        var macd = fastEMA - slowEMA;

        var macdValues = new List<double>();
        for (int i = slowPeriod - 1; i < prices.Count; i++)
        {
            var fastEmaAtI = CalculateEMA(prices.Take(i + 1).ToList(), fastPeriod);
            var slowEmaAtI = CalculateEMA(prices.Take(i + 1).ToList(), slowPeriod);
            macdValues.Add(fastEmaAtI - slowEmaAtI);
        }

        var signal = macdValues.Count >= signalPeriod ? CalculateEMA(macdValues, signalPeriod) : 0;
        var histogram = macd - signal;

        return (macd, signal, histogram);
    }

    private static double CalculateEMA(List<double> prices, int period)
    {
        if (prices.Count < period)
            return prices.LastOrDefault();

        var multiplier = 2.0 / (period + 1);
        var ema = prices.Take(period).Average();

        for (int i = period; i < prices.Count; i++)
        {
            ema = (prices[i] * multiplier) + (ema * (1 - multiplier));
        }

        return ema;
    }

    private static double CalculatePricePosition(double currentPrice, double high, double low)
    {
        if (high == low) return 50; // Neutro se non c'è range
        return ((currentPrice - low) / (high - low)) * 100;
    }

    private static double CalculateSimpleVolatility(double currentPrice, double previousClose)
    {
        if (previousClose == 0) return 0;
        return Math.Abs((currentPrice - previousClose) / previousClose) * 100;
    }

    public void Dispose()
    {
        _http?.Dispose();
    }
}