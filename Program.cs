using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.WindowsServices;
using PortfolioSignalWorker.Models;
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

        services.AddSingleton<DataDrivenTakeProfitService>(provider =>
        {
            var mongo = provider.GetRequiredService<MongoService>();
            var yahooFinance = provider.GetRequiredService<YahooFinanceService>();
            var logger = provider.GetRequiredService<ILogger<DataDrivenTakeProfitService>>();
            return new DataDrivenTakeProfitService(mongo.GetDatabase(), yahooFinance, logger);
        });

        // Take Profit Performance Tracker
        services.AddSingleton<TakeProfitPerformanceTracker>(provider =>
        {
            var mongo = provider.GetRequiredService<MongoService>();
            var yahooFinance = provider.GetRequiredService<YahooFinanceService>();
            var logger = provider.GetRequiredService<ILogger<TakeProfitPerformanceTracker>>();
            return new TakeProfitPerformanceTracker(mongo.GetDatabase(), yahooFinance, logger);
        });


        // ===== CORE SERVICES =====
        services.AddSingleton<YahooFinanceService>();
        services.AddSingleton<TelegramService>();
        services.AddSingleton<MongoService>();
        services.AddSingleton<SmartMarketHoursService>();

        // ===== ENHANCED SERVICES (SIMPLIFIED) =====

        // Enhanced Signal Filter (without portfolio complexity)
        services.AddSingleton<SimplifiedEnhancedSignalFilterService>(provider =>
        {
            var mongo = provider.GetRequiredService<MongoService>();
            var yahooFinance = provider.GetRequiredService<YahooFinanceService>();
            var logger = provider.GetRequiredService<ILogger<SimplifiedEnhancedSignalFilterService>>();
            return new SimplifiedEnhancedSignalFilterService(mongo.GetDatabase(), yahooFinance, logger);
        });

        // Enhanced Risk Management (without portfolio complexity)
        services.AddSingleton<SimplifiedEnhancedRiskManagementService>(provider =>
        {
            var mongo = provider.GetRequiredService<MongoService>();
            var yahooFinance = provider.GetRequiredService<YahooFinanceService>();
            var logger = provider.GetRequiredService<ILogger<SimplifiedEnhancedRiskManagementService>>();
            var config = provider.GetRequiredService<IConfiguration>();
            var dataDrivenTP = provider.GetRequiredService<DataDrivenTakeProfitService>();
            var performanceTracker = provider.GetRequiredService<TakeProfitPerformanceTracker>();
            return new SimplifiedEnhancedRiskManagementService(mongo.GetDatabase(), yahooFinance, logger, config,dataDrivenTP, performanceTracker);
        });

        // Symbol Selection Service (existing)
        services.AddSingleton<SymbolSelectionService>(provider =>
        {
            var mongo = provider.GetRequiredService<MongoService>();
            var yahooFinance = provider.GetRequiredService<YahooFinanceService>();
            var logger = provider.GetRequiredService<ILogger<SymbolSelectionService>>();
            return new SymbolSelectionService(mongo.GetDatabase(), yahooFinance, logger);
        });

        // ===== WORKER SERVICE =====

        // Enhanced Worker (Simplified)
        services.AddHostedService<SimplifiedEnhancedWorker>();
    })
    .ConfigureLogging((context, logging) =>
    {
        logging.ClearProviders();
        logging.AddConsole();

        // Enhanced console formatting
        logging.AddConsole(options =>
        {
            options.TimestampFormat = "[yyyy-MM-dd HH:mm:ss] ";
            options.IncludeScopes = false;
        });

        // Add Windows Event Log for service
        if (WindowsServiceHelpers.IsWindowsService())
        {
            logging.AddEventLog(options =>
            {
                options.SourceName = "EnhancedTradingSystem";
                options.LogName = "Application";
            });
        }

        // Set log levels
        logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);
        logging.AddFilter("Microsoft", LogLevel.Warning);

        // Enhanced services
        logging.AddFilter("PortfolioSignalWorker.Services.SimplifiedEnhancedSignalFilterService", LogLevel.Information);
        logging.AddFilter("PortfolioSignalWorker.Services.SimplifiedEnhancedRiskManagementService", LogLevel.Information);
        logging.AddFilter("PortfolioSignalWorker.SimplifiedEnhancedWorker", LogLevel.Information);

#if DEBUG
        logging.SetMinimumLevel(LogLevel.Debug);
#else
        logging.SetMinimumLevel(LogLevel.Information);
#endif
    });

// Build and configure the host
var host = builder.Build();

// Enhanced startup logging
var logger = host.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("🚀 Enhanced Trading System v2.0 SIMPLIFIED - Starting...");
logger.LogInformation("=====================================");
logger.LogInformation("✅ Multi-confluence signal analysis");
logger.LogInformation("✅ Smart ATR-based risk management");
logger.LogInformation("✅ Structural support/resistance");
logger.LogInformation("✅ Enhanced market context");
logger.LogInformation("✅ Adaptive analysis frequency");
logger.LogInformation("=====================================");

try
{
    await host.RunAsync();
}
catch (Exception ex)
{
    logger.LogCritical(ex, "💥 Enhanced Trading System crashed during startup");
    throw;
}
finally
{
    logger.LogInformation("🛑 Enhanced Trading System shutting down gracefully");
}


//using Microsoft.Extensions.DependencyInjection;
//using Microsoft.Extensions.Hosting.WindowsServices;
//using PortfolioSignalWorker.Services;

//// Determine content root for Windows Service
//var contentRoot = WindowsServiceHelpers.IsWindowsService()
//    ? AppContext.BaseDirectory
//    : Directory.GetCurrentDirectory();

//var builder = Host.CreateDefaultBuilder(args)
//    .UseContentRoot(contentRoot)
//    .UseWindowsService()
//    .ConfigureServices((context, services) =>
//    {
//        // Core Services
//        services.AddSingleton<YahooFinanceService>();
//        services.AddSingleton<TelegramService>();
//        services.AddSingleton<MongoService>();
//        services.AddSingleton<SmartMarketHoursService>();
//        services.AddSingleton<CurrencyConversionService>();
//        services.AddSingleton<BreakoutDetectionService>(provider =>
//        {
//            var yahooFinance = provider.GetRequiredService<YahooFinanceService>();
//            var mongo = provider.GetRequiredService<MongoService>();
//            var logger = provider.GetRequiredService<ILogger<BreakoutDetectionService>>();
//            return new BreakoutDetectionService(yahooFinance, mongo.GetDatabase(), logger);
//        });

//        // Signal Processing
//        services.AddSingleton<SignalFilterService>(provider =>
//        {
//            var mongo = provider.GetRequiredService<MongoService>();
//            var logger = provider.GetRequiredService<ILogger<SignalFilterService>>();
//            return new SignalFilterService(mongo.GetDatabase(), logger);
//        });

//        // Symbol Selection Service
//        services.AddSingleton<SymbolSelectionService>(provider =>
//        {
//            var mongo = provider.GetRequiredService<MongoService>();
//            var yahooFinance = provider.GetRequiredService<YahooFinanceService>();
//            var logger = provider.GetRequiredService<ILogger<SymbolSelectionService>>();
//            return new SymbolSelectionService(mongo.GetDatabase(), yahooFinance, logger);
//        });

//        // ===== NUOVO: RISK MANAGEMENT SERVICE =====
//        services.AddSingleton<RiskManagementService>(provider =>
//        {
//            var mongo = provider.GetRequiredService<MongoService>();
//            var yahooFinance = provider.GetRequiredService<YahooFinanceService>();
//            var currencyService = provider.GetRequiredService<CurrencyConversionService>(); // 🆕 NUOVO
//            var logger = provider.GetRequiredService<ILogger<RiskManagementService>>();
//            var config = provider.GetRequiredService<IConfiguration>();
//            return new RiskManagementService(
//                mongo.GetDatabase(),
//                yahooFinance,
//                logger,
//                currencyService,  // 🆕 NUOVO parametro
//                config);
//        });

//        // Worker Service
//        services.AddHostedService<Worker>();
//    })
//    .ConfigureLogging((context, logging) =>
//    {
//        logging.ClearProviders();
//        logging.AddConsole();

//        // Add Windows Event Log for service
//        if (WindowsServiceHelpers.IsWindowsService())
//        {
//            logging.AddEventLog();
//        }

//        logging.SetMinimumLevel(LogLevel.Information);
//    });

//var host = builder.Build();
//await host.RunAsync();