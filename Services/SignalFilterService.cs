using MongoDB.Bson;
using MongoDB.Driver;
using PortfolioSignalWorker.Models;

namespace PortfolioSignalWorker.Services
{
    public class SignalFilterService
    {
        private readonly IMongoCollection<StockIndicator> _indicatorCollection;
        private readonly IMongoCollection<TradingSignal> _signalCollection;
        private readonly ILogger<SignalFilterService> _logger;

        public SignalFilterService(IMongoDatabase database, ILogger<SignalFilterService> logger)
        {
            _indicatorCollection = database.GetCollection<StockIndicator>("Indicators");
            _signalCollection = database.GetCollection<TradingSignal>("TradingSignals");
            _logger = logger;
        }

        public async Task<TradingSignal?> AnalyzeSignalAsync(string symbol, StockIndicator currentIndicator)
        {
            // 1. Ottieni storico ultimi 20 periodi
            var historicalData = await GetHistoricalDataAsync(symbol, 20);

            // MODIFICA: Ridotto requisito minimo da 5 a 1 per permettere l'avvio
            if (historicalData.Count < 1)
            {
                _logger.LogWarning($"Nessun dato storico per {symbol}. Primo avvio - genero comunque segnale base");

                // Per il primo avvio, genera segnali basici solo su indicatori correnti
                return await GenerateBasicSignalAsync(symbol, currentIndicator);
            }

            _logger.LogInformation($"Analizzando {symbol} con {historicalData.Count} record storici");

            // 2. Calcola medie mobili e conferme
            var enhancedIndicator = await EnhanceIndicatorAsync(currentIndicator, historicalData);

            // 3. Applica filtri anti-rumore
            var signal = await GenerateFilteredSignalAsync(symbol, enhancedIndicator, historicalData);

            return signal;
        }

        // NUOVO: Segnali basici per quando non abbiamo storico
        private async Task<TradingSignal?> GenerateBasicSignalAsync(string symbol, StockIndicator current)
        {
            // Controlla segnali duplicati recenti
            if (await HasRecentSignalAsync(symbol, TimeSpan.FromHours(2)))
            {
                _logger.LogDebug($"Segnale recente già inviato per {symbol}");
                return null;
            }

            // Segnale BASIC BUY: Solo RSI oversold + MACD cross
            if (current.RSI < 30 && current.MACD_Histogram_CrossUp)
            {
                return new TradingSignal
                {
                    Symbol = symbol,
                    Type = SignalType.Buy,
                    Confidence = 60, // Confidence ridotta senza storico
                    Reason = "BASIC BUY: RSI oversold + MACD cross (no historical data)",
                    RSI = current.RSI,
                    MACD_Histogram = current.MACD_Histogram,
                    Price = current.Price,
                    Volume = current.Volume,
                    SignalHash = GenerateSignalHash(symbol, "BASIC_BUY", current.RSI)
                };
            }

            // Segnale WARNING: RSI estremamente oversold
            if (current.RSI < 25)
            {
                return new TradingSignal
                {
                    Symbol = symbol,
                    Type = SignalType.Warning,
                    Confidence = 50, // Confidence ridotta
                    Reason = "WARNING: RSI molto oversold (no historical data)",
                    RSI = current.RSI,
                    MACD_Histogram = current.MACD_Histogram,
                    Price = current.Price,
                    Volume = current.Volume,
                    SignalHash = GenerateSignalHash(symbol, "WARNING", current.RSI)
                };
            }

            return null; // Nessun segnale
        }

        private async Task<List<StockIndicator>> GetHistoricalDataAsync(string symbol, int periods)
        {
            var filter = Builders<StockIndicator>.Filter.Eq(x => x.Symbol, symbol);
            var sort = Builders<StockIndicator>.Sort.Descending(x => x.CreatedAt);

            var results = await _indicatorCollection
                .Find(filter)
                .Sort(sort)
                .Limit(periods)
                .ToListAsync();

            _logger.LogDebug($"Retrieved {results.Count} historical records for {symbol}");
            return results;
        }

        private async Task<StockIndicator> EnhanceIndicatorAsync(StockIndicator current, List<StockIndicator> historical)
        {
            // RSI Media Mobile 5 e 14 periodi (solo se abbiamo abbastanza dati)
            if (historical.Count >= 5)
            {
                current.RSI_SMA_5 = historical.Take(5).Average(x => x.RSI);
            }
            else
            {
                current.RSI_SMA_5 = current.RSI; // Fallback al valore corrente
            }

            if (historical.Count >= 14)
            {
                current.RSI_SMA_14 = historical.Take(14).Average(x => x.RSI);
            }
            else if (historical.Count > 0)
            {
                current.RSI_SMA_14 = historical.Average(x => x.RSI); // Media di quello che abbiamo
            }
            else
            {
                current.RSI_SMA_14 = current.RSI; // Fallback
            }

            // RSI Confermato: < 30 per almeno 2 periodi consecutivi (adattivo)
            if (historical.Count >= 2)
            {
                var last2RSI = historical.Take(2).Select(x => x.RSI).ToList();
                current.RSI_Confirmed = current.RSI < 30 && last2RSI.All(rsi => rsi < 30);
            }
            else if (historical.Count >= 1)
            {
                // Con un solo periodo, richiedi RSI ancora più basso
                current.RSI_Confirmed = current.RSI < 25 && historical.First().RSI < 30;
            }
            else
            {
                // Senza storico, richiedi RSI molto basso
                current.RSI_Confirmed = current.RSI < 20;
            }

            // MACD Cross confermato: cross up per almeno 1 periodo (adattivo)
            if (historical.Count >= 1)
            {
                var previousMACDCross = historical.First().MACD_Histogram_CrossUp;
                current.MACD_Confirmed = current.MACD_Histogram_CrossUp || previousMACDCross;
            }
            else
            {
                // Senza storico, usa solo cross corrente
                current.MACD_Confirmed = current.MACD_Histogram_CrossUp;
            }

            // Volume Spike: volume > 150% della media (adattivo)
            if (historical.Count >= 20)
            {
                var avgVolume = historical.Take(20).Average(x => x.Volume);
                current.VolumeSpike = current.Volume > (avgVolume * 1.5);
            }
            else if (historical.Count >= 5)
            {
                var avgVolume = historical.Average(x => x.Volume);
                current.VolumeSpike = current.Volume > (avgVolume * 1.3); // Soglia ridotta
            }
            else
            {
                // Senza storico, considera volume spike se molto alto
                current.VolumeSpike = current.Volume > 10_000_000; // Soglia assoluta
            }

            return current;
        }
        // Nel SignalFilterService.cs - sostituisci il metodo GenerateFilteredSignalAsync

        private async Task<TradingSignal?> GenerateFilteredSignalAsync(
            string symbol,
            StockIndicator enhanced,
            List<StockIndicator> historical)
        {
            // FILTRO 1: Anti-spam (ridotto a 2 ore per breakout)
            if (await HasRecentSignalAsync(symbol, TimeSpan.FromHours(2)))
            {
                _logger.LogDebug($"Segnale recente già inviato per {symbol}");
                return null;
            }

            // 🚀 NUOVO FILTRO 0: IMMINENT BREAKOUT - Sta per esplodere (massima priorità)
            if (IsImminentBreakout(enhanced, historical))
            {
                var confidence = CalculateConfidence(historical.Count, 95);
                return new TradingSignal
                {
                    Symbol = symbol,
                    Type = SignalType.Buy,
                    Confidence = confidence,
                    Reason = $"🚀 IMMINENT BREAKOUT: {GetBreakoutTriggers(enhanced, historical)} ({historical.Count} periods)",
                    RSI = enhanced.RSI,
                    MACD_Histogram = enhanced.MACD_Histogram,
                    Price = enhanced.Price,
                    Volume = enhanced.Volume,
                    SignalHash = GenerateSignalHash(symbol, "IMMINENT_BREAKOUT", enhanced.Price)
                };
            }

            // 🔥 NUOVO FILTRO 1: BREAKOUT SETUP - Consolidamento pre-esplosione
            if (IsBreakoutSetup(enhanced, historical))
            {
                var confidence = CalculateConfidence(historical.Count, 85);
                return new TradingSignal
                {
                    Symbol = symbol,
                    Type = SignalType.Buy,
                    Confidence = confidence,
                    Reason = $"💥 BREAKOUT SETUP: {GetSetupTriggers(enhanced, historical)} ({historical.Count} periods)",
                    RSI = enhanced.RSI,
                    MACD_Histogram = enhanced.MACD_Histogram,
                    Price = enhanced.Price,
                    Volume = enhanced.Volume,
                    SignalHash = GenerateSignalHash(symbol, "BREAKOUT_SETUP", enhanced.Price)
                };
            }

            // 🔥 NUOVO APPROCCIO: Solo setup di BUY in formazione

            // FILTRO 2: PERFECT BUY SETUP - Tutti i criteri perfetti
            if (IsPerfectBuySetup(enhanced, historical))
            {
                var confidence = CalculateConfidence(historical.Count, 90);
                return new TradingSignal
                {
                    Symbol = symbol,
                    Type = SignalType.Buy,
                    Confidence = confidence,
                    Reason = $"PERFECT SETUP: RSI oversold + MACD convergenza + Volume forte ({historical.Count} periods)",
                    RSI = enhanced.RSI,
                    MACD_Histogram = enhanced.MACD_Histogram,
                    Price = enhanced.Price,
                    Volume = enhanced.Volume,
                    SignalHash = GenerateSignalHash(symbol, "PERFECT_BUY", enhanced.RSI)
                };
            }

            // FILTRO 3: GOOD BUY SETUP - Criteri buoni (2 su 3)
            if (IsGoodBuySetup(enhanced, historical))
            {
                var confidence = CalculateConfidence(historical.Count, 75);
                return new TradingSignal
                {
                    Symbol = symbol,
                    Type = SignalType.Buy,
                    Confidence = confidence,
                    Reason = $"GOOD SETUP: RSI < 30 + {GetSetupReason(enhanced, historical)} ({historical.Count} periods)",
                    RSI = enhanced.RSI,
                    MACD_Histogram = enhanced.MACD_Histogram,
                    Price = enhanced.Price,
                    Volume = enhanced.Volume,
                    SignalHash = GenerateSignalHash(symbol, "GOOD_BUY", enhanced.RSI)
                };
            }

            // FILTRO 4: EARLY SETUP WARNING - Sta sviluppando un setup (1 su 3 + RSI in discesa)
            if (IsEarlySetupWarning(enhanced, historical))
            {
                var confidence = CalculateConfidence(historical.Count, 60);
                return new TradingSignal
                {
                    Symbol = symbol,
                    Type = SignalType.Warning,
                    Confidence = confidence,
                    Reason = $"EARLY SETUP: RSI in discesa verso 30 + {GetEarlySetupReason(enhanced, historical)} ({historical.Count} periods)",
                    RSI = enhanced.RSI,
                    MACD_Histogram = enhanced.MACD_Histogram,
                    Price = enhanced.Price,
                    Volume = enhanced.Volume,
                    SignalHash = GenerateSignalHash(symbol, "EARLY_SETUP", enhanced.RSI)
                };
            }

            // 🔥 DEBUG: Log quando non viene generato alcun segnale con i nuovi criteri
            _logger.LogDebug($"❌ No setup for {symbol}: RSI={enhanced.RSI:F1} (<30?), MACD={enhanced.MACD_Histogram:F3} (~0?), VolumeGrowing={IsVolumeGrowing(enhanced, historical)}");

            return null;
        }

        // 🔥 NUOVO: Setup perfetto (tutti i criteri)
        private bool IsPerfectBuySetup(StockIndicator enhanced, List<StockIndicator> historical)
        {
            var rsiOversold = enhanced.RSI < 30;
            var macdNearZero = IsMACD_NearZero(enhanced, historical);
            var volumeGrowing = IsVolumeGrowing(enhanced, historical);

            var result = rsiOversold && macdNearZero && volumeGrowing;

            if (result)
            {
                _logger.LogInformation($"🎯 PERFECT BUY SETUP: {enhanced.Symbol} - RSI:{enhanced.RSI:F1}, MACD:{enhanced.MACD_Histogram:F3}, Volume crescente");
            }

            return result;
        }

        // 🔥 NUOVO: Setup buono (2 criteri su 3, ma RSI sempre < 30)
        private bool IsGoodBuySetup(StockIndicator enhanced, List<StockIndicator> historical)
        {
            if (enhanced.RSI >= 30) return false; // RSI sempre obbligatorio

            var macdNearZero = IsMACD_NearZero(enhanced, historical);
            var volumeGrowing = IsVolumeGrowing(enhanced, historical);

            var criteriaCount = (macdNearZero ? 1 : 0) + (volumeGrowing ? 1 : 0);
            var result = criteriaCount >= 1; // RSI + almeno 1 altro criterio

            if (result)
            {
                _logger.LogInformation($"📈 GOOD BUY SETUP: {enhanced.Symbol} - RSI:{enhanced.RSI:F1} + {criteriaCount} altri criteri");
            }

            return result;
        }

        // 🔥 NUOVO: Early warning (RSI si avvicina a 30)
        private bool IsEarlySetupWarning(StockIndicator enhanced, List<StockIndicator> historical)
        {
            // RSI tra 30-40 ma in discesa verso 30
            var rsiApproaching = enhanced.RSI > 30 && enhanced.RSI < 40;
            var rsiDescending = IsRSI_Descending(enhanced, historical);

            if (!rsiApproaching || !rsiDescending) return false;

            // Almeno un altro criterio che si sta sviluppando
            var macdImproving = IsMACD_Improving(enhanced, historical);
            var volumeIncreasing = IsVolumeIncreasing(enhanced, historical);

            var result = macdImproving || volumeIncreasing;

            if (result)
            {
                _logger.LogInformation($"⚠️ EARLY SETUP: {enhanced.Symbol} - RSI:{enhanced.RSI:F1} scende verso 30");
            }

            return result;
        }

        // 🔥 CRITERIO 1: MACD vicino allo zero (histogram tra -0.1 e +0.1)
        private bool IsMACD_NearZero(StockIndicator enhanced, List<StockIndicator> historical)
        {
            var nearZero = Math.Abs(enhanced.MACD_Histogram) <= 0.1;

            // Bonus: MACD sta migliorando (diventando meno negativo o positivo)
            if (historical.Count >= 2)
            {
                var prevMACD = historical.Take(2).Skip(1).FirstOrDefault()?.MACD_Histogram ?? enhanced.MACD_Histogram;
                var improving = enhanced.MACD_Histogram > prevMACD; // Sta migliorando
                return nearZero && improving;
            }

            return nearZero;
        }

        // 🔥 CRITERIO 2: Volume in crescita (ultimi 3 periodi)
        private bool IsVolumeGrowing(StockIndicator enhanced, List<StockIndicator> historical)
        {
            if (historical.Count < 3) return enhanced.Volume > 1000000; // Fallback per poco storico

            var recent3Volumes = historical.Take(3).Select(x => x.Volume).ToList();
            var avgRecentVolume = recent3Volumes.Average();

            // Volume attuale > media degli ultimi 3 giorni * 1.2 (20% più alto)
            var volumeGrowing = enhanced.Volume > avgRecentVolume * 1.2;

            // Bonus: trend crescente negli ultimi 3 periodi
            if (recent3Volumes.Count == 3)
            {
                var trendGrowing = recent3Volumes[0] > recent3Volumes[1] && recent3Volumes[1] >= recent3Volumes[2];
                return volumeGrowing || trendGrowing;
            }

            return volumeGrowing;
        }

        // 🔥 HELPER: RSI in discesa
        private bool IsRSI_Descending(StockIndicator enhanced, List<StockIndicator> historical)
        {
            if (historical.Count < 2) return false;

            var recent2RSI = historical.Take(2).Select(x => x.RSI).ToList();
            return enhanced.RSI < recent2RSI[0] && recent2RSI[0] <= recent2RSI[1];
        }

        // 🔥 HELPER: MACD in miglioramento
        private bool IsMACD_Improving(StockIndicator enhanced, List<StockIndicator> historical)
        {
            if (historical.Count < 2) return false;

            var prevMACD = historical.First().MACD_Histogram;
            return enhanced.MACD_Histogram > prevMACD; // Meno negativo o più positivo
        }

        // 🔥 HELPER: Volume in aumento
        private bool IsVolumeIncreasing(StockIndicator enhanced, List<StockIndicator> historical)
        {
            if (historical.Count < 1) return false;

            var prevVolume = historical.First().Volume;
            return enhanced.Volume > prevVolume * 1.1; // 10% più alto del periodo precedente
        }

        // 🔥 HELPER: Descrizioni per i motivi
        private string GetSetupReason(StockIndicator enhanced, List<StockIndicator> historical)
        {
            var reasons = new List<string>();

            if (IsMACD_NearZero(enhanced, historical))
                reasons.Add("MACD vicino a zero");

            if (IsVolumeGrowing(enhanced, historical))
                reasons.Add("Volume crescente");

            return string.Join(" + ", reasons);
        }

        private string GetEarlySetupReason(StockIndicator enhanced, List<StockIndicator> historical)
        {
            var reasons = new List<string>();

            if (IsMACD_Improving(enhanced, historical))
                reasons.Add("MACD migliora");

            if (IsVolumeIncreasing(enhanced, historical))
                reasons.Add("Volume aumenta");

            return string.Join(" + ", reasons);
        }
        // 🚀 IMMINENT BREAKOUT: Tutti i segnali di esplosione imminente
        private bool IsImminentBreakout(StockIndicator enhanced, List<StockIndicator> historical)
        {
            if (historical.Count < 5) return false; // Serve storico per pattern

            // 1. VOLUME SPIKE ESPLOSIVO (3x la media)
            var volumeExplosive = IsVolumeExplosive(enhanced, historical);

            // 2. PRICE COMPRESSION seguito da espansione
            var priceCompression = IsPriceCompressing(enhanced, historical) && IsPriceExpanding(enhanced, historical);

            // 3. MACD GOLDEN CROSS (da negativo a positivo)
            var macdGoldenCross = IsMACD_GoldenCross(enhanced, historical);

            // 4. RSI MOMENTUM (da oversold verso 50+)
            var rsiMomentum = IsRSI_MomentumBuilding(enhanced, historical);

            // Serve almeno 2 su 4 segnali forti
            var signals = new[] { volumeExplosive, priceCompression, macdGoldenCross, rsiMomentum };
            var activeSignals = signals.Count(x => x);

            var result = activeSignals >= 2;

            if (result)
            {
                _logger.LogWarning($"🚀🚀 IMMINENT BREAKOUT DETECTED: {enhanced.Symbol} - {activeSignals}/4 signals active!");
            }

            return result;
        }

        // 💥 BREAKOUT SETUP: Consolidamento e preparazione
        private bool IsBreakoutSetup(StockIndicator enhanced, List<StockIndicator> historical)
        {
            if (historical.Count < 10) return false;

            // 1. TIGHT CONSOLIDATION (range stretto negli ultimi giorni)
            var tightConsolidation = IsTightConsolidation(enhanced, historical, 5);

            // 2. VOLUME BUILDING (volume gradualmente crescente)
            var volumeBuilding = IsVolumeBuilding(enhanced, historical, 5);

            // 3. TECHNICAL SETUP (RSI recovering, MACD improving)
            var technicalSetup = IsTechnicalSetupForming(enhanced, historical);

            // 4. ABOVE KEY SUPPORT (prezzo sopra supporto importante)
            var aboveSupport = IsPriceAboveKeySupport(enhanced, historical);

            // Serve almeno 3 su 4 per setup valido
            var signals = new[] { tightConsolidation, volumeBuilding, technicalSetup, aboveSupport };
            var activeSignals = signals.Count(x => x);

            var result = activeSignals >= 3;

            if (result)
            {
                _logger.LogInformation($"💥 BREAKOUT SETUP: {enhanced.Symbol} - {activeSignals}/4 setup criteria met");
            }

            return result;
        }

        // === DETECTION METHODS ===

        // 🔥 VOLUME ESPLOSIVO (3x media)
        private bool IsVolumeExplosive(StockIndicator enhanced, List<StockIndicator> historical)
        {
            if (historical.Count < 5) return enhanced.Volume > 10_000_000;

            var avgVolume = historical.Take(10).Average(x => x.Volume);
            var isExplosive = enhanced.Volume > avgVolume * 3.0; // 3x la media

            if (isExplosive)
            {
                _logger.LogWarning($"📈📈 VOLUME EXPLOSIVE: {enhanced.Symbol} - {enhanced.Volume:N0} vs avg {avgVolume:N0} (3x+)");
            }

            return isExplosive;
        }

        // 📊 PRICE COMPRESSION (range ristretto)
        private bool IsPriceCompressing(StockIndicator enhanced, List<StockIndicator> historical, int periods = 5)
        {
            if (historical.Count < periods) return false;

            var recentPrices = historical.Take(periods).Select(x => x.Price).ToList();
            recentPrices.Insert(0, enhanced.Price);

            var highPrice = recentPrices.Max();
            var lowPrice = recentPrices.Min();
            var priceRange = ((highPrice - lowPrice) / lowPrice) * 100;

            // Range < 5% negli ultimi 5 giorni = consolidamento stretto
            return priceRange < 5.0;
        }

        // 📈 PRICE EXPANSION (breakout dal range)
        private bool IsPriceExpanding(StockIndicator enhanced, List<StockIndicator> historical)
        {
            if (historical.Count < 2) return false;

            var yesterdayPrice = historical.First().Price;
            var todayMove = Math.Abs((enhanced.Price - yesterdayPrice) / yesterdayPrice) * 100;

            // Movimento > 2% oggi = possibile inizio breakout
            return todayMove > 2.0;
        }

        // ⚡ MACD GOLDEN CROSS
        private bool IsMACD_GoldenCross(StockIndicator enhanced, List<StockIndicator> historical)
        {
            if (historical.Count < 3) return false;

            var today = enhanced.MACD_Histogram;
            var yesterday = historical.First().MACD_Histogram;
            var dayBefore = historical.Skip(1).First().MACD_Histogram;

            // Cross da negativo a positivo negli ultimi 2 giorni
            var goldenCross = (dayBefore < 0 && yesterday <= 0 && today > 0) ||
                             (yesterday < 0 && today > 0);

            if (goldenCross)
            {
                _logger.LogWarning($"⚡ MACD GOLDEN CROSS: {enhanced.Symbol} - {dayBefore:F3} → {yesterday:F3} → {today:F3}");
            }

            return goldenCross;
        }

        // 💪 RSI MOMENTUM BUILDING
        private bool IsRSI_MomentumBuilding(StockIndicator enhanced, List<StockIndicator> historical)
        {
            if (historical.Count < 3) return false;

            var rsiToday = enhanced.RSI;
            var rsiYesterday = historical.First().RSI;
            var rsiDayBefore = historical.Skip(1).First().RSI;

            // RSI in crescita da zona oversold verso zona neutra
            var momentumBuilding = rsiDayBefore < 40 && rsiYesterday < 50 && rsiToday > rsiYesterday &&
                                  (rsiToday - rsiDayBefore) > 5; // Guadagno di almeno 5 punti

            if (momentumBuilding)
            {
                _logger.LogInformation($"💪 RSI MOMENTUM: {enhanced.Symbol} - {rsiDayBefore:F1} → {rsiYesterday:F1} → {rsiToday:F1}");
            }

            return momentumBuilding;
        }

        // 📦 TIGHT CONSOLIDATION
        private bool IsTightConsolidation(StockIndicator enhanced, List<StockIndicator> historical, int periods)
        {
            if (historical.Count < periods) return false;

            var recentPrices = historical.Take(periods).Select(x => x.Price).ToList();
            var avgPrice = recentPrices.Average();
            var volatility = recentPrices.Select(p => Math.Abs((p - avgPrice) / avgPrice)).Average() * 100;

            // Volatilità media < 3% = consolidamento stretto
            return volatility < 3.0;
        }

        // 📈 VOLUME BUILDING (crescita graduale)
        private bool IsVolumeBuilding(StockIndicator enhanced, List<StockIndicator> historical, int periods)
        {
            if (historical.Count < periods) return false;

            var recentVolumes = historical.Take(periods).Select(x => x.Volume).ToList();
            var olderAvg = recentVolumes.Skip(periods / 2).Average();
            var recentAvg = recentVolumes.Take(periods / 2).Average();

            // Volume recente > volume più vecchio * 1.3
            return recentAvg > olderAvg * 1.3;
        }

        // 🔧 TECHNICAL SETUP FORMING
        private bool IsTechnicalSetupForming(StockIndicator enhanced, List<StockIndicator> historical)
        {
            var rsiRecovering = enhanced.RSI > 30 && enhanced.RSI < 60; // Zona di recovery
            var macdImproving = IsMACD_Improving(enhanced, historical);

            return rsiRecovering && macdImproving;
        }

        // 🏗️ PRICE ABOVE KEY SUPPORT
        private bool IsPriceAboveKeySupport(StockIndicator enhanced, List<StockIndicator> historical)
        {
            if (historical.Count < 10) return true; // Default per poco storico

            var recent10Lows = historical.Take(10).Select(x => x.Price * 0.98).ToList(); // Approssimazione support
            var keySupport = recent10Lows.Max(); // Support più alto (più significativo)

            return enhanced.Price > keySupport;
        }

        // === HELPER METHODS ===

        private string GetBreakoutTriggers(StockIndicator enhanced, List<StockIndicator> historical)
        {
            var triggers = new List<string>();

            if (IsVolumeExplosive(enhanced, historical)) triggers.Add("Volume 3x+");
            if (IsPriceCompressing(enhanced, historical) && IsPriceExpanding(enhanced, historical)) triggers.Add("Price breakout");
            if (IsMACD_GoldenCross(enhanced, historical)) triggers.Add("MACD golden cross");
            if (IsRSI_MomentumBuilding(enhanced, historical)) triggers.Add("RSI momentum");

            return string.Join(" + ", triggers);
        }

        private string GetSetupTriggers(StockIndicator enhanced, List<StockIndicator> historical)
        {
            var triggers = new List<string>();

            if (IsTightConsolidation(enhanced, historical, 5)) triggers.Add("Tight consolidation");
            if (IsVolumeBuilding(enhanced, historical, 5)) triggers.Add("Volume building");
            if (IsTechnicalSetupForming(enhanced, historical)) triggers.Add("Technical setup");
            if (IsPriceAboveKeySupport(enhanced, historical)) triggers.Add("Above support");

            return string.Join(" + ", triggers);
        }
        // NUOVO: Calcola confidence basata sui dati storici disponibili
        private int CalculateConfidence(int historicalCount, int baseConfidence)
        {
            if (historicalCount >= 20) return baseConfidence;         // Full confidence
            if (historicalCount >= 10) return baseConfidence - 10;   // -10%
            if (historicalCount >= 5) return baseConfidence - 20;    // -20%
            if (historicalCount >= 2) return baseConfidence - 30;    // -30%
            return baseConfidence - 40;                               // -40% per 0-1 record
        } 

        private async Task<bool> HasRecentSignalAsync(string symbol, TimeSpan timeWindow)
        {
            var cutoffTime = DateTime.UtcNow.Subtract(timeWindow);
            var filter = Builders<TradingSignal>.Filter.And(
                Builders<TradingSignal>.Filter.Eq(x => x.Symbol, symbol),
                Builders<TradingSignal>.Filter.Gte(x => x.CreatedAt, cutoffTime),
                Builders<TradingSignal>.Filter.Eq(x => x.Sent, true)
            );

            var count = await _signalCollection.CountDocumentsAsync(filter);
            return count > 0;
        }

        private string GenerateSignalHash(string symbol, string signalType, double rsi)
        {
            var hashInput = $"{symbol}_{signalType}_{Math.Round(rsi, 0)}_{DateTime.UtcNow:yyyyMMddHH}";
            return hashInput.GetHashCode().ToString();
        }

        public async Task MarkSignalAsSentAsync(ObjectId signalId)
        {
            var update = Builders<TradingSignal>.Update
                .Set(x => x.Sent, true)
                .Set(x => x.SentAt, DateTime.UtcNow);

            await _signalCollection.UpdateOneAsync(x => x.Id == signalId, update);
        }
    }
}