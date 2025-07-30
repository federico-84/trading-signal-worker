using MongoDB.Driver;
using Newtonsoft.Json.Linq;
using PortfolioSignalWorker.Models;

namespace PortfolioSignalWorker.Services
{
    public class SymbolSelectionService
    {
        private readonly IMongoCollection<WatchlistSymbol> _watchlistCollection;
        private readonly IMongoCollection<CoreSymbol> _coreSymbolsCollection;      // NUOVO
        private readonly IMongoCollection<RotationSymbol> _rotationSymbolsCollection; // NUOVO
        private readonly YahooFinanceService _yahooFinance;
        private readonly ILogger<SymbolSelectionService> _logger;

        public SymbolSelectionService(
            IMongoDatabase database,
            YahooFinanceService yahooFinance,
            ILogger<SymbolSelectionService> logger)
        {
            _watchlistCollection = database.GetCollection<WatchlistSymbol>("WatchlistSymbols");
            _coreSymbolsCollection = database.GetCollection<CoreSymbol>("CoreSymbols");           // NUOVO
            _rotationSymbolsCollection = database.GetCollection<RotationSymbol>("RotationSymbols"); // NUOVO
            _yahooFinance = yahooFinance;
            _logger = logger;

            CreateIndexes();
        }

        private void CreateIndexes()
        {
            // Watchlist indexes
            var symbolIndex = Builders<WatchlistSymbol>.IndexKeys.Ascending(x => x.Symbol);
            _watchlistCollection.Indexes.CreateOne(new CreateIndexModel<WatchlistSymbol>(symbolIndex,
                new CreateIndexOptions { Unique = true }));

            var tierIndex = Builders<WatchlistSymbol>.IndexKeys
                .Ascending(x => x.Tier)
                .Ascending(x => x.NextAnalysis);
            _watchlistCollection.Indexes.CreateOne(new CreateIndexModel<WatchlistSymbol>(tierIndex));

            // Core symbols indexes
            var coreSymbolIndex = Builders<CoreSymbol>.IndexKeys.Ascending(x => x.Symbol);
            _coreSymbolsCollection.Indexes.CreateOne(new CreateIndexModel<CoreSymbol>(coreSymbolIndex,
                new CreateIndexOptions { Unique = true }));

            // Rotation symbols indexes  
            var rotationSymbolIndex = Builders<RotationSymbol>.IndexKeys.Ascending(x => x.Symbol);
            _rotationSymbolsCollection.Indexes.CreateOne(new CreateIndexModel<RotationSymbol>(rotationSymbolIndex,
                new CreateIndexOptions { Unique = true }));
        }

        // Removed: InitializeDefaultSymbolsIfEmpty, PopulateDefaultCoreSymbols, PopulateDefaultRotationSymbols
        // All symbols must be manually inserted into MongoDB collections

        public async Task InitializeWatchlist()
        {
            _logger.LogInformation("Initializing watchlist from MongoDB symbol collections...");

            // 1. Load symbols from MongoDB collections (NO DEFAULT POPULATION)
            var coreSymbols = await _coreSymbolsCollection
                .Find(Builders<CoreSymbol>.Filter.Eq(x => x.IsActive, true))
                .SortBy(x => x.Priority)
                .ToListAsync();

            var rotationSymbols = await _rotationSymbolsCollection
                .Find(Builders<RotationSymbol>.Filter.Eq(x => x.IsActive, true))
                .SortBy(x => x.Priority)
                .ToListAsync();

            if (coreSymbols.Count == 0 && rotationSymbols.Count == 0)
            {
                _logger.LogWarning("No symbols found in MongoDB collections! Please populate CoreSymbols and RotationSymbols collections manually.");
                return;
            }

            _logger.LogInformation($"Loaded from MongoDB: {coreSymbols.Count} core symbols, {rotationSymbols.Count} rotation symbols");

            // 2. Clear existing watchlist
            await _watchlistCollection.DeleteManyAsync(Builders<WatchlistSymbol>.Filter.Empty);

            var watchlistSymbols = new List<WatchlistSymbol>();

            // 4. Add Core Symbols (never rotate)
            _logger.LogInformation("Adding core symbols...");
            for (int i = 0; i < coreSymbols.Count; i++)
            {
                var coreSymbol = coreSymbols[i];

                if (await IsValidTicker(coreSymbol.Symbol))
                {
                    var tier = i < 20 ? SymbolTier.Tier1_Priority :
                              i < 40 ? SymbolTier.Tier2_Standard :
                                       SymbolTier.Tier3_Monitor;

                    var watchlistSymbol = new WatchlistSymbol
                    {
                        Symbol = coreSymbol.Symbol,
                        Tier = tier,
                        MonitoringFrequency = GetMonitoringFrequency(tier),
                        NextAnalysis = DateTime.UtcNow.Add(TimeSpan.FromMinutes(i * 2)),
                        IsCore = true,          // NEVER ROTATE
                        CanRotate = false,      // PROTECTED
                        OverallScore = 95,      // High score for core
                        Notes = coreSymbol.Notes,
                        Market = coreSymbol.Market
                    };

                    await EnrichSymbolData(watchlistSymbol);
                    watchlistSymbols.Add(watchlistSymbol);
                    _logger.LogInformation($"✅ Core symbol {coreSymbol.Symbol} ({coreSymbol.Market}) added");
                }
                else
                {
                    _logger.LogWarning($"❌ Core symbol {coreSymbol.Symbol} failed validation - SKIPPED");
                }

                await Task.Delay(300); // Rate limiting
            }

            // 5. Validate and score rotation candidates
            _logger.LogInformation("Analyzing rotation candidates...");
            var validCandidates = new List<(string symbol, string market, double score, string reason)>();

            foreach (var rotationSymbol in rotationSymbols.Take(50)) // Limit for performance
            {
                try
                {
                    if (await IsValidTicker(rotationSymbol.Symbol))
                    {
                        var (symbol, score, reason) = await AnalyzeSymbolScore(rotationSymbol.Symbol);
                        validCandidates.Add((symbol, rotationSymbol.Market, score, reason));
                        _logger.LogDebug($"✅ {rotationSymbol.Symbol} ({rotationSymbol.Market}) scored {score:F1}");
                    }
                    else
                    {
                        _logger.LogWarning($"❌ Rotation symbol {rotationSymbol.Symbol} failed validation");
                    }
                    await Task.Delay(500);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Error analyzing rotation symbol {rotationSymbol.Symbol}: {ex.Message}");
                }
            }

            // 6. Select top rotation symbols (25 total)
            var topRotation = validCandidates
                .OrderByDescending(x => x.score)
                .Take(25)
                .ToList();

            // 7. Add rotation symbols
            for (int i = 0; i < topRotation.Count; i++)
            {
                var (symbol, market, score, reason) = topRotation[i];

                var rotationWatchlistSymbol = new WatchlistSymbol
                {
                    Symbol = symbol,
                    Tier = SymbolTier.Tier3_Monitor, // Start in Tier 3
                    MonitoringFrequency = TimeSpan.FromHours(4),
                    NextAnalysis = DateTime.UtcNow.Add(TimeSpan.FromMinutes((coreSymbols.Count + i) * 2)),
                    IsCore = false,         // CAN ROTATE
                    CanRotate = true,       // ELIGIBLE FOR ROTATION
                    OverallScore = score,
                    Notes = $"{market} rotation: {reason}",
                    MinHistoryDays = 14,
                    Market = market
                };

                await EnrichSymbolData(rotationWatchlistSymbol);
                watchlistSymbols.Add(rotationWatchlistSymbol);
                _logger.LogInformation($"✅ Rotation symbol {symbol} ({market}) added (Score: {score:F1})");
            }

            // 8. Insert into watchlist
            await _watchlistCollection.InsertManyAsync(watchlistSymbols);

            _logger.LogInformation($"Watchlist initialized from MongoDB:");
            _logger.LogInformation($"  Core symbols: {coreSymbols.Count}");
            _logger.LogInformation($"  Rotation symbols: {topRotation.Count}");
            _logger.LogInformation($"  Total symbols: {watchlistSymbols.Count}");

            LogTierDistribution(watchlistSymbols);
        }

        // REST OF METHODS REMAIN THE SAME...
        private async Task<(string symbol, double score, string reason)> AnalyzeSymbolScore(string symbol)
        {
            try
            {
                var quote = await _yahooFinance.GetQuoteAsync(symbol);

                double score = 0;
                var reasons = new List<string>();

                // 1. Liquidity Score (0-25 points)
                var volume = quote["v"]?.Value<long>() ?? 0;
                if (volume > 5_000_000) { score += 25; reasons.Add("High volume"); }
                else if (volume > 1_000_000) { score += 15; reasons.Add("Good volume"); }
                else if (volume > 100_000) { score += 5; reasons.Add("Low volume"); }

                // 2. Volatility Sweet Spot (0-25 points)  
                var high = quote["h"]?.Value<double>() ?? 0;
                var low = quote["l"]?.Value<double>() ?? 0;
                var current = quote["c"]?.Value<double>() ?? 0;

                if (current > 0)
                {
                    var dayRange = ((high - low) / current) * 100;
                    if (dayRange >= 2 && dayRange <= 8) { score += 25; reasons.Add("Optimal volatility"); }
                    else if (dayRange >= 1 && dayRange <= 12) { score += 15; reasons.Add("Good volatility"); }
                    else if (dayRange >= 0.5) { score += 5; reasons.Add("Some volatility"); }
                }

                // 3. Price Range Preference (0-20 points)
                var isEuropean = symbol.Contains(".");
                if (isEuropean)
                {
                    if (current >= 10 && current <= 200) { score += 20; reasons.Add("Good price range"); }
                    else if (current >= 5 && current <= 500) { score += 15; reasons.Add("Acceptable price"); }
                    else if (current >= 1) { score += 10; reasons.Add("Tradeable price"); }
                }
                else
                {
                    if (current >= 20 && current <= 500) { score += 20; reasons.Add("Good price range"); }
                    else if (current >= 5 && current <= 1000) { score += 15; reasons.Add("Acceptable price"); }
                    else if (current >= 1) { score += 10; reasons.Add("Tradeable price"); }
                }

                // 4. Price Movement (0-15 points)
                var changePercent = quote["dp"]?.Value<double>() ?? 0;
                if (Math.Abs(changePercent) >= 1.5 && Math.Abs(changePercent) <= 8)
                { score += 15; reasons.Add("Good price movement"); }
                else if (Math.Abs(changePercent) >= 0.5)
                { score += 10; reasons.Add("Some price movement"); }

                // 5. Position in daily range (0-15 points)
                if (current > 0 && high > low)
                {
                    var position = ((current - low) / (high - low)) * 100;
                    if (position >= 30 && position <= 70)
                    { score += 15; reasons.Add("Good position in range"); }
                    else if (position >= 20 && position <= 80)
                    { score += 10; reasons.Add("Acceptable position"); }
                }

                var reasonString = string.Join(", ", reasons);
                return (symbol, score, reasonString);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error analyzing {symbol}: {ex.Message}");
                return (symbol, 0, $"Analysis failed: {ex.Message}");
            }
        }

        public async Task<bool> AddSymbolToActiveWatchlist(string symbol, bool isCore = false, string market = "US")
        {
            try
            {
                // Check if symbol already in watchlist
                var existing = await _watchlistCollection
                    .Find(Builders<WatchlistSymbol>.Filter.Eq(x => x.Symbol, symbol))
                    .FirstOrDefaultAsync();

                if (existing != null)
                {
                    _logger.LogWarning($"Symbol {symbol} already exists in active watchlist");
                    return false;
                }

                // Validate symbol
                if (!await IsValidTicker(symbol))
                {
                    _logger.LogWarning($"Symbol {symbol} failed validation, not adding to watchlist");
                    return false;
                }

                // Create new watchlist symbol
                var watchlistSymbol = new WatchlistSymbol
                {
                    Symbol = symbol,
                    Tier = SymbolTier.Tier3_Monitor, // Start conservatively
                    MonitoringFrequency = TimeSpan.FromHours(4),
                    NextAnalysis = DateTime.UtcNow.Add(TimeSpan.FromMinutes(2)), // Analyze soon
                    IsCore = isCore,
                    CanRotate = !isCore,
                    OverallScore = isCore ? 95 : 70, // High score for core, medium for rotation
                    Notes = $"Hot-added at {DateTime.Now:yyyy-MM-dd HH:mm}",
                    Market = market,
                    MinHistoryDays = 14
                };

                await EnrichSymbolData(watchlistSymbol);
                await _watchlistCollection.InsertOneAsync(watchlistSymbol);

                _logger.LogInformation($"✅ HOT-ADDED {symbol} ({market}) to active watchlist - will be analyzed in 2 minutes");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error hot-adding symbol {symbol} to watchlist", symbol);
                return false;
            }
        }

        public async Task CheckForNewSymbolsAndAdd()
        {
            try
            {
                // Get current watchlist symbols
                var currentSymbols = await _watchlistCollection
                    .Find(Builders<WatchlistSymbol>.Filter.Empty)
                    .Project(x => x.Symbol)
                    .ToListAsync();

                // Check for new core symbols
                var newCoreSymbols = await _coreSymbolsCollection
                    .Find(Builders<CoreSymbol>.Filter.And(
                        Builders<CoreSymbol>.Filter.Eq(x => x.IsActive, true),
                        Builders<CoreSymbol>.Filter.Nin(x => x.Symbol, currentSymbols)
                    ))
                    .ToListAsync();

                // Add new core symbols
                foreach (var coreSymbol in newCoreSymbols)
                {
                    await AddSymbolToActiveWatchlist(coreSymbol.Symbol, isCore: true, coreSymbol.Market);
                    await Task.Delay(1000); // Small delay between additions
                }

                // Check for new rotation symbols (limit to prevent spam)
                var newRotationSymbols = await _rotationSymbolsCollection
                    .Find(Builders<RotationSymbol>.Filter.And(
                        Builders<RotationSymbol>.Filter.Eq(x => x.IsActive, true),
                        Builders<RotationSymbol>.Filter.Nin(x => x.Symbol, currentSymbols)
                    ))
                    .SortBy(x => x.Priority)
                    .Limit(5) // Max 5 new rotation symbols at once
                    .ToListAsync();

                // Add new rotation symbols
                foreach (var rotationSymbol in newRotationSymbols)
                {
                    await AddSymbolToActiveWatchlist(rotationSymbol.Symbol, isCore: false, rotationSymbol.Market);
                    await Task.Delay(1000);
                }

                if (newCoreSymbols.Count > 0 || newRotationSymbols.Count > 0)
                {
                    _logger.LogInformation($"Hot-added {newCoreSymbols.Count} core and {newRotationSymbols.Count} rotation symbols");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking for new symbols");
            }
        }
        private async Task<bool> IsValidTicker(string symbol)
        {
            try
            {
                var quote = await _yahooFinance.GetQuoteAsync(symbol);
                var price = quote["c"]?.Value<double>() ?? 0;
                return price > 0;
            }
            catch
            {
                return false;
            }
        }

        private TimeSpan GetMonitoringFrequency(SymbolTier tier)
        {
            return tier switch
            {
                SymbolTier.Tier1_Priority => TimeSpan.FromMinutes(30),
                SymbolTier.Tier2_Standard => TimeSpan.FromHours(2),
                SymbolTier.Tier3_Monitor => TimeSpan.FromHours(4),
                _ => TimeSpan.FromHours(4)
            };
        }

        private async Task EnrichSymbolData(WatchlistSymbol symbol)
        {
            try
            {
                var quote = await _yahooFinance.GetQuoteAsync(symbol.Symbol);

                symbol.CompanyName = symbol.Symbol;
                symbol.Sector = "Unknown";
                symbol.Exchange = GetExchangeFromSymbol(symbol.Symbol);
                symbol.CurrentPrice = quote?["c"]?.Value<double>() ?? 0;
                symbol.MarketCap = 0;
                symbol.Beta = 1.0;
                symbol.AverageDailyVolume = quote?["v"]?.Value<long>() ?? 0;
                symbol.LastUpdated = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to enrich data for {symbol.Symbol}: {ex.Message}");
            }
        }

        private string GetExchangeFromSymbol(string symbol)
        {
            if (symbol.Contains(".MI")) return "Milan";
            if (symbol.Contains(".AS")) return "Amsterdam";
            if (symbol.Contains(".DE")) return "Frankfurt";
            if (symbol.Contains(".PA")) return "Paris";
            if (symbol.Contains(".SW")) return "Swiss";
            if (symbol.Contains(".L")) return "London";
            if (symbol.Contains(".MC")) return "Madrid";
            return "NASDAQ/NYSE";
        }

        private void LogTierDistribution(List<WatchlistSymbol> symbols)
        {
            var tierCounts = symbols.GroupBy(s => s.Tier)
                .ToDictionary(g => g.Key, g => g.Count());

            var marketCounts = symbols.GroupBy(s => s.Market ?? "US")
                .ToDictionary(g => g.Key, g => g.Count());

            _logger.LogInformation("Tier Distribution:");
            foreach (var tier in tierCounts)
            {
                var frequency = GetMonitoringFrequency(tier.Key);
                _logger.LogInformation($"  {tier.Key}: {tier.Value} symbols (every {frequency})");
            }

            _logger.LogInformation("Market Distribution:");
            foreach (var market in marketCounts)
            {
                _logger.LogInformation($"  {market.Key}: {market.Value} symbols");
            }
        }

        // ALL OTHER METHODS REMAIN THE SAME (GetSymbolsDueForAnalysis, UpdateSymbolNextAnalysis, etc.)
        public async Task<List<WatchlistSymbol>> GetSymbolsDueForAnalysis()
        {
            var now = DateTime.UtcNow;
            var filter = Builders<WatchlistSymbol>.Filter.And(
                Builders<WatchlistSymbol>.Filter.Eq(x => x.IsActive, true),
                Builders<WatchlistSymbol>.Filter.Lte(x => x.NextAnalysis, now)
            );

            var sort = Builders<WatchlistSymbol>.Sort
                .Ascending(x => x.Tier)
                .Ascending(x => x.NextAnalysis);

            return await _watchlistCollection
                .Find(filter)
                .Sort(sort)
                .ToListAsync();
        }

        public async Task UpdateSymbolNextAnalysis(string symbol, DateTime nextAnalysis)
        {
            var filter = Builders<WatchlistSymbol>.Filter.Eq(x => x.Symbol, symbol);
            var update = Builders<WatchlistSymbol>.Update
                .Set(x => x.LastAnalyzed, DateTime.UtcNow)
                .Set(x => x.NextAnalysis, nextAnalysis);

            await _watchlistCollection.UpdateOneAsync(filter, update);
        }

        public async Task<List<WatchlistSymbol>> GetWatchlistSummary()
        {
            return await _watchlistCollection
                .Find(Builders<WatchlistSymbol>.Filter.Eq(x => x.IsActive, true))
                .SortByDescending(x => x.OverallScore)
                .ToListAsync();
        }

        public async Task OptimizeWatchlist()
        {
            _logger.LogInformation("Starting SAFE watchlist optimization (core symbols protected)...");

            // Get ONLY rotation symbols (core symbols are protected)
            var rotationSymbols = await _watchlistCollection
                .Find(Builders<WatchlistSymbol>.Filter.And(
                    Builders<WatchlistSymbol>.Filter.Eq(x => x.CanRotate, true),
                    Builders<WatchlistSymbol>.Filter.Eq(x => x.IsCore, false)
                ))
                .ToListAsync();

            // Update days in watchlist for all symbols
            await UpdateDaysInWatchlist();

            // Find underperformers among ROTATION symbols only
            var underperformers = rotationSymbols
                .Where(s => s.SignalsGenerated >= 10 &&
                           s.SuccessRate < 40 &&
                           s.DaysInWatchlist >= s.MinHistoryDays)
                .OrderBy(s => s.SuccessRate)
                .Take(3) // Max 3 rotations per week
                .ToList();

            if (underperformers.Any())
            {
                _logger.LogInformation($"Rotating {underperformers.Count} underperforming symbols (CORE PROTECTED)");

                // Remove underperformers from watchlist
                var symbolsToRemove = underperformers.Select(s => s.Symbol).ToList();
                var removeFilter = Builders<WatchlistSymbol>.Filter.In(x => x.Symbol, symbolsToRemove);
                await _watchlistCollection.DeleteManyAsync(removeFilter);

                // Find new candidates from rotation pool
                var newCandidates = await FindReplacementCandidates(underperformers.Count);

                if (newCandidates.Any())
                {
                    await _watchlistCollection.InsertManyAsync(newCandidates);
                    _logger.LogInformation($"Added {newCandidates.Count} new rotation symbols");
                }
            }
            else
            {
                _logger.LogInformation("No rotation needed - all symbols performing adequately");
            }

            // Rebalance tiers (including core symbols - they can move between tiers)
            await RebalanceTiers();
        }

        private async Task<List<WatchlistSymbol>> FindReplacementCandidates(int count)
        {
            // Get current symbols to avoid duplicates
            var currentSymbols = await _watchlistCollection
                .Find(Builders<WatchlistSymbol>.Filter.Empty)
                .Project(x => x.Symbol)
                .ToListAsync();

            // Get unused rotation symbols from MongoDB
            var unusedRotationSymbols = await _rotationSymbolsCollection
                .Find(Builders<RotationSymbol>.Filter.And(
                    Builders<RotationSymbol>.Filter.Eq(x => x.IsActive, true),
                    Builders<RotationSymbol>.Filter.Nin(x => x.Symbol, currentSymbols)
                ))
                .SortBy(x => x.Priority)
                .ToListAsync();

            var newSymbols = new List<WatchlistSymbol>();
            var analyzed = 0;

            foreach (var rotationSymbol in unusedRotationSymbols.Take(count * 3)) // Analyze 3x needed for selection
            {
                if (newSymbols.Count >= count) break;

                try
                {
                    var (_, score, reason) = await AnalyzeSymbolScore(rotationSymbol.Symbol);

                    if (score >= 30) // Minimum threshold for rotation candidates
                    {
                        var newSymbol = new WatchlistSymbol
                        {
                            Symbol = rotationSymbol.Symbol,
                            Tier = SymbolTier.Tier3_Monitor, // New symbols start in Tier 3
                            MonitoringFrequency = TimeSpan.FromHours(4),
                            NextAnalysis = DateTime.UtcNow.Add(TimeSpan.FromMinutes(analyzed * 5)),
                            IsCore = false,
                            CanRotate = true,
                            OverallScore = score,
                            Notes = $"{rotationSymbol.Market} replacement: {reason}",
                            MinHistoryDays = 14,
                            Market = rotationSymbol.Market
                        };

                        await EnrichSymbolData(newSymbol);
                        newSymbols.Add(newSymbol);
                    }

                    analyzed++;
                    await Task.Delay(500); // Rate limiting
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to analyze replacement candidate {rotationSymbol.Symbol}: {ex.Message}");
                }
            }

            return newSymbols.Take(count).ToList();
        }

        private async Task UpdateDaysInWatchlist()
        {
            var allSymbols = await _watchlistCollection.Find(Builders<WatchlistSymbol>.Filter.Empty).ToListAsync();

            foreach (var symbol in allSymbols)
            {
                var daysInWatchlist = (DateTime.UtcNow - symbol.AddedDate).Days;

                if (symbol.DaysInWatchlist != daysInWatchlist)
                {
                    var filter = Builders<WatchlistSymbol>.Filter.Eq(x => x.Id, symbol.Id);
                    var update = Builders<WatchlistSymbol>.Update.Set(x => x.DaysInWatchlist, daysInWatchlist);
                    await _watchlistCollection.UpdateOneAsync(filter, update);
                }
            }
        }

        private async Task RebalanceTiers()
        {
            var allSymbols = await _watchlistCollection
                .Find(Builders<WatchlistSymbol>.Filter.Empty)
                .ToListAsync();

            // Sort by success rate and average return
            var sortedSymbols = allSymbols
                .OrderByDescending(s => s.SuccessRate)
                .ThenByDescending(s => s.AvgReturn)
                .ToList();

            // Reassign tiers
            for (int i = 0; i < sortedSymbols.Count; i++)
            {
                var symbol = sortedSymbols[i];
                var newTier = AssignTier(i, symbol.OverallScore);
                var newFrequency = GetMonitoringFrequency(newTier);

                if (symbol.Tier != newTier)
                {
                    var filter = Builders<WatchlistSymbol>.Filter.Eq(x => x.Id, symbol.Id);
                    var update = Builders<WatchlistSymbol>.Update
                        .Set(x => x.Tier, newTier)
                        .Set(x => x.MonitoringFrequency, newFrequency);

                    await _watchlistCollection.UpdateOneAsync(filter, update);

                    _logger.LogInformation($"{symbol.Symbol} moved from Tier {symbol.Tier} to Tier {newTier}");
                }
            }
        }

        private SymbolTier AssignTier(int rank, double score)
        {
            // Top 20 = Tier 1 (every 30 min)
            if (rank < 20 && score >= 70) return SymbolTier.Tier1_Priority;

            // Next 20 = Tier 2 (every 2 hours)  
            if (rank < 40 && score >= 50) return SymbolTier.Tier2_Standard;

            // Remaining = Tier 3 (every 4 hours)
            return SymbolTier.Tier3_Monitor;
        }

        public async Task UpdateSymbolPerformance(string symbol, bool signalSuccess, double returnPercent = 0)
        {
            var filter = Builders<WatchlistSymbol>.Filter.Eq(x => x.Symbol, symbol);
            var symbolDoc = await _watchlistCollection.Find(filter).FirstOrDefaultAsync();

            if (symbolDoc != null)
            {
                symbolDoc.SignalsGenerated++;
                if (signalSuccess)
                {
                    symbolDoc.SuccessfulSignals++;
                    symbolDoc.AvgReturn = ((symbolDoc.AvgReturn * (symbolDoc.SuccessfulSignals - 1)) + returnPercent) / symbolDoc.SuccessfulSignals;
                }

                symbolDoc.SuccessRate = (double)symbolDoc.SuccessfulSignals / symbolDoc.SignalsGenerated * 100;
                symbolDoc.LastUpdated = DateTime.UtcNow;

                await _watchlistCollection.ReplaceOneAsync(filter, symbolDoc);
            }
        }
    }
}