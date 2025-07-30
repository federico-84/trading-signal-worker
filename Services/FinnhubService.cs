using Newtonsoft.Json.Linq;
using PortfolioSignalWorker.Models;

namespace PortfolioSignalWorker.Services;

public class FinnhubService
{
    private readonly HttpClient _http;
    private readonly string _apiKey;

    public FinnhubService(IConfiguration config)
    {
        _apiKey = config["Finnhub:ApiKey"];
        _http = new HttpClient();
    }
    public async Task<JObject> GetQuoteAsync(string symbol)
    {
        var url = $"https://finnhub.io/api/v1/quote?symbol={symbol}&token={_apiKey}";
        var response = await _http.GetStringAsync(url);
        return JObject.Parse(response);
    }

    public async Task<JObject> GetCompanyProfileAsync(string symbol)
    {
        var url = $"https://finnhub.io/api/v1/stock/profile2?symbol={symbol}&token={_apiKey}";
        var response = await _http.GetStringAsync(url);
        return JObject.Parse(response);
    }
    public async Task<StockIndicator> GetIndicatorsAsync(string symbol)
    {
        // Get technical indicators
        var indicatorUrl = $"https://finnhub.io/api/v1/indicator?symbol={symbol}&resolution=D&indicator=rsi,macd&timeperiod=14&token={_apiKey}";
        var indicatorResponse = await _http.GetStringAsync(indicatorUrl);
        var indicatorData = JObject.Parse(indicatorResponse);

        // Get current quote for price and volume
        var quoteUrl = $"https://finnhub.io/api/v1/quote?symbol={symbol}&token={_apiKey}";
        var quoteResponse = await _http.GetStringAsync(quoteUrl);
        var quoteData = JObject.Parse(quoteResponse);

        // Parse indicators
        var rsi = indicatorData["rsi"]?["rsi"]?.Last?.Value<double>() ?? 50;
        var macd = indicatorData["macd"]?["macd"]?.Last?.Value<double>() ?? 0;
        var signal = indicatorData["macd"]?["signal"]?.Last?.Value<double>() ?? 0;
        var histogram = macd - signal;

        // Check for MACD crossover
        var prevMacd = indicatorData["macd"]?["macd"]?.Reverse().Skip(1).FirstOrDefault()?.Value<double>() ?? 0;
        var prevSignal = indicatorData["macd"]?["signal"]?.Reverse().Skip(1).FirstOrDefault()?.Value<double>() ?? 0;
        var prevHistogram = prevMacd - prevSignal;
        var crossUp = histogram > 0 && prevHistogram <= 0;

        // Parse quote data
        var price = quoteData["c"]?.Value<double>() ?? 0;
        var volume = quoteData["v"]?.Value<long>() ?? 0;

        return new StockIndicator
        {
            Symbol = symbol,
            RSI = rsi,
            MACD = macd,
            MACD_Signal = signal,
            MACD_Histogram = histogram,
            MACD_Histogram_CrossUp = crossUp,
            Price = price,
            Volume = volume
        };
    }
}