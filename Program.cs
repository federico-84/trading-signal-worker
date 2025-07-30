using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using PortfolioSignalWorker.Services;

var builder = Host.CreateApplicationBuilder(args);

// AGGIUNGI QUESTA CONFIGURAZIONE PER LE VARIABILI D'AMBIENTE
builder.Configuration.AddEnvironmentVariables();

// Registra i servizi
builder.Services.AddSingleton<MongoService>();
builder.Services.AddSingleton<TelegramService>();
builder.Services.AddSingleton<YahooFinanceService>();
builder.Services.AddSingleton<FinnhubService>();
builder.Services.AddSingleton<SignalFilterService>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();

// Debug delle variabili d'ambiente
Console.WriteLine("=== DEBUG ENVIRONMENT VARIABLES ===");
Console.WriteLine($"Mongo__ConnectionString: {Environment.GetEnvironmentVariable("Mongo__ConnectionString")}");
Console.WriteLine($"MONGO__CONNECTIONSTRING: {Environment.GetEnvironmentVariable("MONGO__CONNECTIONSTRING")}");
Console.WriteLine($"Telegram__BotToken: {Environment.GetEnvironmentVariable("Telegram__BotToken")}");
Console.WriteLine("=====================================");

await host.RunAsync();
