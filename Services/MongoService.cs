using MongoDB.Bson;
using MongoDB.Driver;
using PortfolioSignalWorker.Models;

namespace PortfolioSignalWorker.Services;

public class MongoService
{
    private readonly IMongoCollection<StockIndicator> _indicatorCollection;
    private readonly IMongoCollection<TradingSignal> _signalCollection;
    private readonly IMongoCollection<BreakoutSignalDocument> _breakoutSignalCollection; // 🆕 NUOVO
    private readonly IMongoCollection<BreakoutAnalyticsDocument> _breakoutAnalyticsCollection; // 🆕 NUOVO
    private readonly IMongoDatabase _database;
    private readonly IMongoCollection<WatchlistSymbol> _watchlistCollection;

    public MongoService(IConfiguration config)
    {
        var connectionString = config["Mongo:ConnectionString"] ?? config["MONGO_DEBUG"] ?? Environment.GetEnvironmentVariable("Mongo__ConnectionString");
        //Console.WriteLine($"Connection string: {connectionString}");
        var client = new MongoClient(connectionString);

        _database = client.GetDatabase(config["Mongo:Database"]);
        _indicatorCollection = _database.GetCollection<StockIndicator>("Indicators");
        _signalCollection = _database.GetCollection<TradingSignal>("TradingSignals");
        _breakoutSignalCollection = _database.GetCollection<BreakoutSignalDocument>("BreakoutSignals"); // 🆕 NUOVO
        _breakoutAnalyticsCollection = _database.GetCollection<BreakoutAnalyticsDocument>("BreakoutAnalytics"); // 🆕 NUOVO
        _watchlistCollection = _database.GetCollection<WatchlistSymbol>("WatchlistSymbols");

        CreateIndexes();
    }

    private void CreateIndexes()
    {
        // === Indicators ===
        TryCreateIndex(() =>
        {
            var indicatorSymbolIndex = Builders<StockIndicator>.IndexKeys
                .Ascending(x => x.Symbol)
                .Descending(x => x.CreatedAt);
            _indicatorCollection.Indexes.CreateOne(new CreateIndexModel<StockIndicator>(
                indicatorSymbolIndex,
                new CreateIndexOptions { Name = "ix_indicators_symbol_createdat" }));
        }, "ix_indicators_symbol_createdat");

        TryCreateIndex(() =>
        {
            var indicatorTtlIndex = Builders<StockIndicator>.IndexKeys.Ascending(x => x.CreatedAt);
            _indicatorCollection.Indexes.CreateOne(new CreateIndexModel<StockIndicator>(
                indicatorTtlIndex,
                new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(90), Name = "ix_indicators_ttl" }));
        }, "ix_indicators_ttl");

        // === TradingSignals ===
        TryCreateIndex(() =>
        {
            var signalAntiSpamIndex = Builders<TradingSignal>.IndexKeys
                .Ascending(x => x.Symbol)
                .Ascending(x => x.Sent)
                .Descending(x => x.CreatedAt);
            _signalCollection.Indexes.CreateOne(new CreateIndexModel<TradingSignal>(
                signalAntiSpamIndex,
                new CreateIndexOptions { Name = "ix_signals_symbol_sent_createdat" }));
        }, "ix_signals_symbol_sent_createdat");

        TryCreateIndex(() =>
        {
            var signalTtlIndex = Builders<TradingSignal>.IndexKeys.Ascending(x => x.CreatedAt);
            _signalCollection.Indexes.CreateOne(new CreateIndexModel<TradingSignal>(
                signalTtlIndex,
                new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(365), Name = "ix_signals_ttl" }));
        }, "ix_signals_ttl");

        // === WatchlistSymbols ===
        TryCreateIndex(() =>
        {
            var watchlistDueIndex = Builders<WatchlistSymbol>.IndexKeys
                .Ascending(x => x.IsActive)
                .Ascending(x => x.NextAnalysis);
            _watchlistCollection.Indexes.CreateOne(new CreateIndexModel<WatchlistSymbol>(
                watchlistDueIndex,
                new CreateIndexOptions { Name = "ix_watchlist_isactive_nextanalysis" }));
        }, "ix_watchlist_isactive_nextanalysis");

        TryCreateIndex(() =>
        {
            var watchlistSymbolIndex = Builders<WatchlistSymbol>.IndexKeys.Ascending(x => x.Symbol);
            _watchlistCollection.Indexes.CreateOne(new CreateIndexModel<WatchlistSymbol>(
                watchlistSymbolIndex,
                new CreateIndexOptions { Unique = true, Name = "ix_watchlist_symbol_unique" }));
        }, "ix_watchlist_symbol_unique");

        // === BreakoutSignals ===
        TryCreateIndex(() =>
        {
            var idx = Builders<BreakoutSignalDocument>.IndexKeys
                .Ascending(x => x.Symbol).Descending(x => x.AnalyzedAt);
            _breakoutSignalCollection.Indexes.CreateOne(new CreateIndexModel<BreakoutSignalDocument>(idx));
        }, "breakout_symbol_analyzedat");

        TryCreateIndex(() =>
        {
            var idx = Builders<BreakoutSignalDocument>.IndexKeys
                .Descending(x => x.BreakoutScore).Descending(x => x.AnalyzedAt);
            _breakoutSignalCollection.Indexes.CreateOne(new CreateIndexModel<BreakoutSignalDocument>(idx));
        }, "breakout_score_analyzedat");

        TryCreateIndex(() =>
        {
            var idx = Builders<BreakoutSignalDocument>.IndexKeys
                .Ascending(x => x.BreakoutType).Descending(x => x.AnalyzedAt);
            _breakoutSignalCollection.Indexes.CreateOne(new CreateIndexModel<BreakoutSignalDocument>(idx));
        }, "breakout_type_analyzedat");

        TryCreateIndex(() =>
        {
            var idx = Builders<BreakoutSignalDocument>.IndexKeys.Ascending(x => x.AnalyzedAt);
            _breakoutSignalCollection.Indexes.CreateOne(new CreateIndexModel<BreakoutSignalDocument>(
                idx, new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(60) }));
        }, "breakout_ttl");
    }

    /// <summary>
    /// Crea un indice ignorando l'errore "already exists with a different name" —
    /// si verifica quando il DB ha già l'indice con il nome auto-generato di MongoDB.
    /// </summary>
    private static void TryCreateIndex(Action createAction, string indexName)
    {
        try
        {
            createAction();
        }
        catch (MongoCommandException ex) when (ex.Message.Contains("already exists"))
        {
            Console.WriteLine($"[Indexes] '{indexName}' already exists with a different name — skipping (harmless).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Indexes] Warning: could not create '{indexName}': {ex.Message}");
        }
    }

    // ===== EXISTING METHODS =====
    public async Task<long> GetWatchlistCount()
    {
        return await _watchlistCollection.CountDocumentsAsync(Builders<WatchlistSymbol>.Filter.Empty);
    }

    public async Task SaveIndicatorAsync(StockIndicator indicator)
    {
        await _indicatorCollection.InsertOneAsync(indicator);
    }

    public async Task SaveSignalAsync(TradingSignal signal)
    {
        await _signalCollection.InsertOneAsync(signal);
    }

    public IMongoDatabase GetDatabase() => _database;

    // ===== 🚀 NUOVO: BREAKOUT SIGNAL METHODS =====

    /// <summary>
    /// Salva un breakout signal in MongoDB
    /// </summary>
    public async Task SaveBreakoutSignalAsync(BreakoutSignalDocument breakoutSignal)
    {
        try
        {
            await _breakoutSignalCollection.InsertOneAsync(breakoutSignal);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving breakout signal: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Marca un breakout signal come inviato e lo collega al trading signal
    /// </summary>
    public async Task MarkBreakoutSignalAsSentAsync(ObjectId breakoutSignalId, ObjectId tradingSignalId)
    {
        try
        {
            var filter = Builders<BreakoutSignalDocument>.Filter.Eq(x => x.Id, breakoutSignalId);
            var update = Builders<BreakoutSignalDocument>.Update
                .Set(x => x.SignalSent, true)
                .Set(x => x.SignalSentAt, DateTime.UtcNow)
                .Set(x => x.TradingSignalId, tradingSignalId);

            await _breakoutSignalCollection.UpdateOneAsync(filter, update);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error marking breakout signal as sent: {ex.Message}");
        }
    }

    // ===== 🚀 NUOVO: HELPER METHODS =====

    /// <summary>
    /// Converte BreakoutSignal → BreakoutSignalDocument per MongoDB
    /// </summary>
    public static BreakoutSignalDocument ConvertToDocument(BreakoutSignal signal)
    {
        return new BreakoutSignalDocument
        {
            Symbol = signal.Symbol,
            AnalyzedAt = signal.AnalyzedAt,
            CurrentPrice = signal.CurrentPrice,
            BreakoutScore = signal.BreakoutScore,
            MaxPossibleScore = signal.MaxPossibleScore,
            BreakoutProbability = signal.BreakoutProbability,
            BreakoutType = signal.BreakoutType.ToString(),
            Reasons = signal.Reasons ?? new List<string>(),

            Consolidation = signal.Consolidation != null ? new ConsolidationPatternDocument
            {
                IsValid = signal.Consolidation.IsValid,
                DurationDays = signal.Consolidation.DurationDays,
                VolatilityPercent = signal.Consolidation.VolatilityPercent,
                HighLevel = signal.Consolidation.HighLevel,
                LowLevel = signal.Consolidation.LowLevel,
                IsCompressing = signal.Consolidation.IsCompressing,
                ConsolidationType = signal.Consolidation.ConsolidationType ?? "Unknown"
            } : null,

            Compression = signal.Compression != null ? new CompressionPatternDocument
            {
                IsDetected = signal.Compression.IsDetected,
                CompressionRatio = signal.Compression.CompressionRatio,
                CurrentVolatility = signal.Compression.CurrentVolatility,
                HistoricalVolatility = signal.Compression.HistoricalVolatility,
                CompressionStrength = signal.Compression.CompressionStrength
            } : null,

            VolumePattern = signal.VolumePattern != null ? new VolumePatternDocument
            {
                IsValid = signal.VolumePattern.IsValid,
                VolumeIncreaseRatio = signal.VolumePattern.VolumeIncreaseRatio,
                IsAccumulating = signal.VolumePattern.IsAccumulating,
                AverageVolume = signal.VolumePattern.AverageVolume,
                CurrentVolumeStrength = signal.VolumePattern.CurrentVolumeStrength,
                AccumulationScore = signal.VolumePattern.AccumulationScore
            } : null,

            KeyLevels = signal.KeyLevels != null ? new KeyLevelsDocument
            {
                PrimaryResistance = signal.KeyLevels.PrimaryResistance,
                SecondaryResistance = signal.KeyLevels.SecondaryResistance,
                PrimarySupport = signal.KeyLevels.PrimarySupport,
                SecondarySupport = signal.KeyLevels.SecondarySupport,
                CurrentPrice = signal.KeyLevels.CurrentPrice,
                DistanceToResistance = signal.KeyLevels.DistanceToResistance
            } : null,

            Positioning = signal.Positioning != null ? new PositioningAnalysisDocument
            {
                CurrentPrice = signal.Positioning.CurrentPrice,
                PositionInDayRange = signal.Positioning.PositionInDayRange,
                DistanceToResistance = signal.Positioning.DistanceToResistance,
                DistanceToSupport = signal.Positioning.DistanceToSupport,
                VolumeStrength = signal.Positioning.VolumeStrength,
                IsNearResistance = signal.Positioning.IsNearResistance
            } : null
        };
    }
}