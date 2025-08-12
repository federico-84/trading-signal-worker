using Newtonsoft.Json.Linq;

namespace PortfolioSignalWorker.Services
{
    public class CurrencyConversionService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<CurrencyConversionService> _logger;
        private readonly Dictionary<string, double> _exchangeRateCache;
        private DateTime _lastCacheUpdate;
        private readonly TimeSpan _cacheValidityDuration = TimeSpan.FromHours(1); // Cache per 1 ora

        public CurrencyConversionService(ILogger<CurrencyConversionService> logger)
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "TradingBot/1.0");
            _logger = logger;
            _exchangeRateCache = new Dictionary<string, double>();
            _lastCacheUpdate = DateTime.MinValue;
        }

        public async Task<double> ConvertToEuroAsync(double amount, string fromCurrency)
        {
            if (fromCurrency.ToUpper() == "EUR")
                return amount; // Già in Euro

            try
            {
                var rate = await GetExchangeRateAsync(fromCurrency, "EUR");
                var convertedAmount = amount * rate;

                _logger.LogDebug($"Currency conversion: {amount:F2} {fromCurrency} = €{convertedAmount:F2} (rate: {rate:F4})");

                return convertedAmount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to convert {amount} {fromCurrency} to EUR - using 1:1 fallback");
                return amount; // Fallback - restituisce valore originale
            }
        }

        public async Task<double> GetExchangeRateAsync(string fromCurrency, string toCurrency = "EUR")
        {
            fromCurrency = fromCurrency.ToUpper();
            toCurrency = toCurrency.ToUpper();

            if (fromCurrency == toCurrency)
                return 1.0;

            var cacheKey = $"{fromCurrency}_{toCurrency}";

            // Controlla cache
            if (IsCacheValid() && _exchangeRateCache.ContainsKey(cacheKey))
            {
                _logger.LogDebug($"Using cached exchange rate: {fromCurrency}/{toCurrency} = {_exchangeRateCache[cacheKey]:F4}");
                return _exchangeRateCache[cacheKey];
            }

            try
            {
                // Prova prima ExchangeRate-API (gratuito)
                var rate = await GetRateFromExchangeRateApi(fromCurrency, toCurrency);

                // Cache il risultato
                _exchangeRateCache[cacheKey] = rate;
                _lastCacheUpdate = DateTime.UtcNow;

                _logger.LogInformation($"Updated exchange rate: {fromCurrency}/{toCurrency} = {rate:F4}");
                return rate;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get exchange rate for {fromCurrency}/{toCurrency}");

                // Fallback rates approssimativi (aggiorna periodicamente questi valori)
                return GetFallbackRate(fromCurrency, toCurrency);
            }
        }

        private async Task<double> GetRateFromExchangeRateApi(string fromCurrency, string toCurrency)
        {
            // ExchangeRate-API gratuito (1500 richieste/mese)
            var url = $"https://api.exchangerate-api.com/v4/latest/{fromCurrency}";

            var response = await _httpClient.GetStringAsync(url);
            var data = JObject.Parse(response);

            var rates = data["rates"] as JObject;
            if (rates?.ContainsKey(toCurrency) == true)
            {
                return rates[toCurrency].Value<double>();
            }

            throw new Exception($"Currency {toCurrency} not found in API response");
        }

        private double GetFallbackRate(string fromCurrency, string toCurrency)
        {
            // Tassi di cambio approssimativi di backup (aggiorna questi manualmente)
            var fallbackRates = new Dictionary<string, double>
            {
                ["USD_EUR"] = 0.92,  // 1 USD = 0.92 EUR circa
                ["GBP_EUR"] = 1.17,  // 1 GBP = 1.17 EUR circa  
                ["CHF_EUR"] = 1.08,  // 1 CHF = 1.08 EUR circa
                ["JPY_EUR"] = 0.0063, // 1 JPY = 0.0063 EUR circa
                ["CAD_EUR"] = 0.68,  // 1 CAD = 0.68 EUR circa
                ["AUD_EUR"] = 0.61,  // 1 AUD = 0.61 EUR circa
            };

            var key = $"{fromCurrency}_{toCurrency}";
            if (fallbackRates.ContainsKey(key))
            {
                var rate = fallbackRates[key];
                _logger.LogWarning($"Using FALLBACK exchange rate: {fromCurrency}/{toCurrency} = {rate:F4}");
                return rate;
            }

            // Se non abbiamo il tasso, proviamo l'inverso
            var inverseKey = $"{toCurrency}_{fromCurrency}";
            if (fallbackRates.ContainsKey(inverseKey))
            {
                var rate = 1.0 / fallbackRates[inverseKey];
                _logger.LogWarning($"Using INVERSE fallback rate: {fromCurrency}/{toCurrency} = {rate:F4}");
                return rate;
            }

            _logger.LogError($"No fallback rate available for {fromCurrency}/{toCurrency} - using 1.0");
            return 1.0; // Ultima risorsa
        }

        private bool IsCacheValid()
        {
            return DateTime.UtcNow - _lastCacheUpdate < _cacheValidityDuration;
        }

        public string GetSymbolCurrency(string symbol)
        {
            // Determina la valuta base dal simbolo
            return symbol switch
            {
                // US Stocks (USD)
                var s when !s.Contains(".") => "USD",

                // European Stocks
                var s when s.EndsWith(".MI") => "EUR",  // Milano
                var s when s.EndsWith(".AS") => "EUR",  // Amsterdam
                var s when s.EndsWith(".DE") => "EUR",  // Frankfurt
                var s when s.EndsWith(".PA") => "EUR",  // Paris
                var s when s.EndsWith(".MC") => "EUR",  // Madrid
                var s when s.EndsWith(".SW") => "CHF",  // Swiss
                var s when s.EndsWith(".L") => "GBP",   // London

                // Altri mercati
                var s when s.EndsWith(".T") => "JPY",   // Tokyo
                var s when s.EndsWith(".TO") => "CAD",  // Toronto
                var s when s.EndsWith(".AX") => "AUD",  // Australia

                // Default
                _ => "USD"
            };
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }

    // Classe helper per le informazioni valuta
    public class CurrencyInfo
    {
        public string Code { get; set; }
        public string Symbol { get; set; }
        public double RateToEUR { get; set; }
        public DateTime LastUpdated { get; set; }
    }
}