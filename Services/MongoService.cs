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
        // Existing indexes per Indicators
        var symbolIndex = Builders<StockIndicator>.IndexKeys
            .Ascending(x => x.Symbol)
            .Descending(x => x.CreatedAt);
        _indicatorCollection.Indexes.CreateOne(new CreateIndexModel<StockIndicator>(symbolIndex));

        var ttlIndex = Builders<StockIndicator>.IndexKeys.Ascending(x => x.CreatedAt);
        var ttlOptions = new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(30) };
        _indicatorCollection.Indexes.CreateOne(new CreateIndexModel<StockIndicator>(ttlIndex, ttlOptions));

        // Existing indexes per TradingSignals
        var signalIndex = Builders<TradingSignal>.IndexKeys
            .Ascending(x => x.Symbol)
            .Descending(x => x.CreatedAt);
        _signalCollection.Indexes.CreateOne(new CreateIndexModel<TradingSignal>(signalIndex));

        // 🚀 NUOVO: Breakout signal indexes
        try
        {
            var breakoutSymbolIndex = Builders<BreakoutSignalDocument>.IndexKeys
                .Ascending(x => x.Symbol)
                .Descending(x => x.AnalyzedAt);
            _breakoutSignalCollection.Indexes.CreateOne(new CreateIndexModel<BreakoutSignalDocument>(breakoutSymbolIndex));

            var breakoutScoreIndex = Builders<BreakoutSignalDocument>.IndexKeys
                .Descending(x => x.BreakoutScore)
                .Descending(x => x.AnalyzedAt);
            _breakoutSignalCollection.Indexes.CreateOne(new CreateIndexModel<BreakoutSignalDocument>(breakoutScoreIndex));

            var breakoutTypeIndex = Builders<BreakoutSignalDocument>.IndexKeys
                .Ascending(x => x.BreakoutType)
                .Descending(x => x.AnalyzedAt);
            _breakoutSignalCollection.Indexes.CreateOne(new CreateIndexModel<BreakoutSignalDocument>(breakoutTypeIndex));

            // TTL per breakout signals (mantieni 60 giorni per analysis)
            var breakoutTtlIndex = Builders<BreakoutSignalDocument>.IndexKeys.Ascending(x => x.AnalyzedAt);
            var breakoutTtlOptions = new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(60) };
            _breakoutSignalCollection.Indexes.CreateOne(new CreateIndexModel<BreakoutSignalDocument>(breakoutTtlIndex, breakoutTtlOptions));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Could not create breakout indexes: {ex.Message}");
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