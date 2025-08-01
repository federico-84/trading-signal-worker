using MongoDB.Driver;
using PortfolioSignalWorker.Models;

namespace PortfolioSignalWorker.Services;

public class MongoService
{
    private readonly IMongoCollection<StockIndicator> _indicatorCollection;
    private readonly IMongoCollection<TradingSignal> _signalCollection;
    private readonly IMongoDatabase _database;
    private readonly IMongoCollection<WatchlistSymbol> _watchlistCollection;

    public MongoService(IConfiguration config)
    {
        //var client = new MongoClient(config["Mongo:ConnectionString"]);

        var connectionString = config["Mongo:ConnectionString"] ?? config["MONGO_DEBUG"] ?? Environment.GetEnvironmentVariable("Mongo__ConnectionString");
        Console.WriteLine($"Connection string: {connectionString}");
        var client = new MongoClient(connectionString);

        _database = client.GetDatabase(config["Mongo:Database"]);
        _indicatorCollection = _database.GetCollection<StockIndicator>("Indicators");
        _signalCollection = _database.GetCollection<TradingSignal>("TradingSignals");
        _watchlistCollection = _database.GetCollection<WatchlistSymbol>("WatchlistSymbols");

        CreateIndexes();
    }

    private void CreateIndexes()
    {
        // Index per performance query
        var symbolIndex = Builders<StockIndicator>.IndexKeys
            .Ascending(x => x.Symbol)
            .Descending(x => x.CreatedAt);
        _indicatorCollection.Indexes.CreateOne(new CreateIndexModel<StockIndicator>(symbolIndex));

        // TTL Index per pulizia automatica (mantieni 30 giorni)
        var ttlIndex = Builders<StockIndicator>.IndexKeys.Ascending(x => x.CreatedAt);
        var ttlOptions = new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(30) };
        _indicatorCollection.Indexes.CreateOne(new CreateIndexModel<StockIndicator>(ttlIndex, ttlOptions));

        // Index per segnali
        var signalIndex = Builders<TradingSignal>.IndexKeys
            .Ascending(x => x.Symbol)
            .Descending(x => x.CreatedAt);
        _signalCollection.Indexes.CreateOne(new CreateIndexModel<TradingSignal>(signalIndex));
    }
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
}