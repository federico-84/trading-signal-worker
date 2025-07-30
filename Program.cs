using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using PortfolioSignalWorker.Services;

var builder = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((context, config) =>
    {
        // AGGIUNTO: Carica le variabili d'ambiente di Railway
        config.AddEnvironmentVariables();
    })
    .ConfigureServices((context, services) =>
    {
        // Core Services - AGGIORNATO per usare YahooFinanceService
        services.AddSingleton<YahooFinanceService>(); // CAMBIATO DA FinnhubService
        services.AddSingleton<TelegramService>();
        services.AddSingleton<MongoService>();
        services.AddSingleton<MarketHoursService>(); // AGGIUNTO per mercati europei

        // Signal Processing
        services.AddSingleton<SignalFilterService>(provider =>
        {
            var mongo = provider.GetRequiredService<MongoService>();
            var logger = provider.GetRequiredService<ILogger<SignalFilterService>>();
            return new SignalFilterService(mongo.GetDatabase(), logger);
        });

        // Symbol Selection Service - AGGIORNATO per usare YahooFinanceService
        services.AddSingleton<SymbolSelectionService>(provider =>
        {
            var mongo = provider.GetRequiredService<MongoService>();
            var yahooFinance = provider.GetRequiredService<YahooFinanceService>(); // CAMBIATO DA FinnhubService
            var logger = provider.GetRequiredService<ILogger<SymbolSelectionService>>();
            return new SymbolSelectionService(mongo.GetDatabase(), yahooFinance, logger); // CAMBIATO DA finnhub
        });

        // Worker Service
        services.AddHostedService<Worker>();
    })
    .ConfigureLogging((context, logging) =>
    {
        logging.ClearProviders();
        logging.AddConsole();
        logging.SetMinimumLevel(LogLevel.Information);
    });

var host = builder.Build();

// Debug delle variabili d'ambiente (temporaneo)
Console.WriteLine("=== DEBUG ENVIRONMENT VARIABLES ===");
Console.WriteLine($"Mongo__ConnectionString: {Environment.GetEnvironmentVariable("Mongo__ConnectionString")}");
Console.WriteLine($"Telegram__BotToken: {Environment.GetEnvironmentVariable("Telegram__BotToken")}");
Console.WriteLine($"Telegram__ChatId: {Environment.GetEnvironmentVariable("Telegram__ChatId")}");
Console.WriteLine("=====================================");

await host.RunAsync();
