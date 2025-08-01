using Microsoft.Extensions.Hosting.WindowsServices;
using PortfolioSignalWorker.Services;

// Determine content root for Windows Service
var contentRoot = WindowsServiceHelpers.IsWindowsService()
    ? AppContext.BaseDirectory
    : Directory.GetCurrentDirectory();

var builder = Host.CreateDefaultBuilder(args)
    .UseContentRoot(contentRoot)
    .UseWindowsService()
    .ConfigureServices((context, services) =>
    {
        // Core Services
        services.AddSingleton<YahooFinanceService>();
        services.AddSingleton<TelegramService>();
        services.AddSingleton<MongoService>();
        services.AddSingleton<SmartMarketHoursService>();

        // Signal Processing
        services.AddSingleton<SignalFilterService>(provider =>
        {
            var mongo = provider.GetRequiredService<MongoService>();
            var logger = provider.GetRequiredService<ILogger<SignalFilterService>>();
            return new SignalFilterService(mongo.GetDatabase(), logger);
        });

        // Symbol Selection Service
        services.AddSingleton<SymbolSelectionService>(provider =>
        {
            var mongo = provider.GetRequiredService<MongoService>();
            var yahooFinance = provider.GetRequiredService<YahooFinanceService>();
            var logger = provider.GetRequiredService<ILogger<SymbolSelectionService>>();
            return new SymbolSelectionService(mongo.GetDatabase(), yahooFinance, logger);
        });

        // ===== NUOVO: RISK MANAGEMENT SERVICE =====
        services.AddSingleton<RiskManagementService>(provider =>
        {
            var mongo = provider.GetRequiredService<MongoService>();
            var yahooFinance = provider.GetRequiredService<YahooFinanceService>();
            var logger = provider.GetRequiredService<ILogger<RiskManagementService>>();
            var config = provider.GetRequiredService<IConfiguration>();
            return new RiskManagementService(mongo.GetDatabase(), yahooFinance, logger, config);
        });

        // Worker Service
        services.AddHostedService<Worker>();
    })
    .ConfigureLogging((context, logging) =>
    {
        logging.ClearProviders();
        logging.AddConsole();

        // Add Windows Event Log for service
        if (WindowsServiceHelpers.IsWindowsService())
        {
            logging.AddEventLog();
        }

        logging.SetMinimumLevel(LogLevel.Information);
    });

var host = builder.Build();
await host.RunAsync();