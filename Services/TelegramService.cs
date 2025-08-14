// TelegramService.cs - VERSIONE ITALIANA CON RISK/REWARD DETTAGLIATO

using PortfolioSignalWorker.Models;
using System.Text;
using static PortfolioSignalWorker.Services.SmartMarketHoursService;

namespace PortfolioSignalWorker.Services
{
    public class TelegramService
    {
        private readonly HttpClient _http;
        private readonly string _botToken;
        private readonly string _chatId;
        private readonly ILogger<TelegramService> _logger;

        public TelegramService(IConfiguration config, ILogger<TelegramService> logger)
        {
            _botToken = config["Telegram:BotToken"];
            _chatId = config["Telegram:ChatId"];
            _http = new HttpClient();
            _logger = logger;
        }

        public async Task SendMessageAsync(string message)
        {
            try
            {
                var url = $"https://api.telegram.org/bot{_botToken}/sendMessage";
                var payload = new
                {
                    chat_id = _chatId,
                    text = message,
                    parse_mode = "HTML" // Abilita formattazione HTML
                };

                var content = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json"
                );

                var response = await _http.PostAsync(url, content);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"Errore invio Telegram: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore nell'invio del messaggio Telegram");
            }
        }

        /// <summary>
        /// Formatta il messaggio di trading in italiano con spiegazione dettagliata del Risk/Reward
        /// </summary>
        public string FormatTradingSignalMessage(TradingSignal signal, AnalysisMode analysisMode, WatchlistSymbol watchlistSymbol)
        {
            var signalEmoji = GetSignalEmoji(signal.Type);
            var modeEmoji = GetModeEmoji(analysisMode);
            var marketFlag = GetMarketFlag(watchlistSymbol.Market ?? "US");
            var statusEmoji = GetConfidenceEmoji(signal.Confidence);

            // Determina se è data-driven
            var isDataDriven = !string.IsNullOrEmpty(signal.TakeProfitStrategy);
            var dataDrivenIndicator = isDataDriven ? " 🧠" : "";

            var title = $"{modeEmoji} {signalEmoji} <b>{GetSignalTypeInItalian(signal.Type)} {signal.Symbol}</b> {marketFlag}{dataDrivenIndicator}";

            var message = new StringBuilder();
            message.AppendLine(title);
            message.AppendLine();

            // SEZIONE PRINCIPALE
            message.AppendLine($"🎯 <b>Affidabilità:</b> {signal.Confidence}% {statusEmoji}");
            message.AppendLine($"📊 <b>Mercato:</b> {GetMarketConditionInItalian(signal.MarketCondition)}");

            if (isDataDriven && signal.PredictedSuccessProbability.HasValue)
            {
                message.AppendLine($"🧠 <b>Probabilità Successo:</b> {signal.PredictedSuccessProbability:F0}%");
            }

            message.AppendLine();

            // DATI TECNICI
            message.AppendLine("📈 <b>ANALISI TECNICA:</b>");
            message.AppendLine($"• RSI: {signal.RSI:F1} {GetRSIDescription(signal.RSI)}");
            message.AppendLine($"• MACD: {signal.MACD_Histogram:F3}");
            message.AppendLine($"• Volume: {FormatVolume(signal.Volume)} (Forza: {signal.VolumeStrength:F1}/10)");
            message.AppendLine($"• Trend: {signal.TrendStrength:F1}/10 {GetTrendDescription(signal.TrendStrength ?? 0)}");
            message.AppendLine();

            // PREZZI
            message.AppendLine("💰 <b>PREZZI (in Euro):</b>");
            message.AppendLine($"• <b>Entrata:</b> €{signal.Price:F2}");

            // Mostra valuta originale se diversa da EUR
            if (signal.OriginalCurrency != "EUR" && signal.ExchangeRate != 1.0)
            {
                var originalPrice = signal.Price / signal.ExchangeRate;
                message.AppendLine($"  <i>(Originale: {originalPrice:F2} {signal.OriginalCurrency}, Cambio: 1 {signal.OriginalCurrency} = {signal.ExchangeRate:F4} EUR)</i>");
            }

            message.AppendLine();

            // GESTIONE DEL RISCHIO - SEZIONE DETTAGLIATA
            message.AppendLine("🛡️ <b>GESTIONE DEL RISCHIO:</b>");
            message.AppendLine();

            message.AppendLine($"🔻 <b>Stop Loss:</b> €{signal.StopLoss:F2} (-{signal.StopLossPercent:F1}%)");
            message.AppendLine($"🎯 <b>Take Profit:</b> €{signal.TakeProfit:F2} (+{signal.TakeProfitPercent:F1}%)");
            message.AppendLine();

            // SPIEGAZIONE DETTAGLIATA RISK/REWARD
            message.AppendLine("⚖️ <b>ANALISI RISK/REWARD:</b>");

            var riskAmount = signal.Price - signal.StopLoss.Value;
            var rewardAmount = signal.TakeProfit.Value - signal.Price;
            var riskPercent = signal.StopLossPercent ?? 0;
            var rewardPercent = signal.TakeProfitPercent ?? 0;

            message.AppendLine($"• <b>Rischio:</b> €{riskAmount:F2} ({riskPercent:F1}% del capitale investito)");
            message.AppendLine($"• <b>Profitto potenziale:</b> €{rewardAmount:F2} ({rewardPercent:F1}% di guadagno)");
            message.AppendLine();

            if (signal.RiskRewardRatio.HasValue && signal.RiskRewardRatio > 0)
            {
                message.AppendLine($"📊 <b>Rapporto R/R: 1:{signal.RiskRewardRatio:F1}</b>");
                message.AppendLine($"   {GetRiskRewardExplanation(signal.RiskRewardRatio)}");
            }
            else
            {
                message.AppendLine("📊 <b>Rapporto R/R:</b> Non disponibile");
                message.AppendLine($"   {GetRiskRewardExplanation(signal.RiskRewardRatio)}");
            }
            message.AppendLine();

            // CALCOLO ESEMPIO PRATICO
            message.AppendLine("💡 <b>ESEMPIO PRATICO:</b>");
            message.AppendLine(GetPracticalExample(signal));
            message.AppendLine();

            // LIVELLI TECNICI
            if (signal.SupportLevel.HasValue && signal.ResistanceLevel.HasValue &&
                signal.SupportLevel > 0 && signal.ResistanceLevel > 0)
            {
                message.AppendLine("📊 <b>LIVELLI CHIAVE:</b>");
                message.AppendLine($"🟢 <b>Supporto:</b> €{signal.SupportLevel:F2}");
                message.AppendLine($"🔴 <b>Resistenza:</b> €{signal.ResistanceLevel:F2}");
                message.AppendLine();
            }

            // STRATEGIA
            if (!string.IsNullOrEmpty(signal.EntryStrategy))
            {
                message.AppendLine($"🎯 <b>STRATEGIA ENTRATA:</b> {TranslateStrategy(signal.EntryStrategy)}");
            }

            if (!string.IsNullOrEmpty(signal.ExitStrategy))
            {
                message.AppendLine($"🚪 <b>STRATEGIA USCITA:</b> {TranslateStrategy(signal.ExitStrategy)}");
            }

            if (isDataDriven && !string.IsNullOrEmpty(signal.TakeProfitStrategy))
            {
                message.AppendLine($"🧠 <b>Strategia TP:</b> {signal.TakeProfitStrategy}");
            }

            message.AppendLine();

            // FOOTER
            var marketStatus = GetMarketStatusInItalian(analysisMode);
            message.AppendLine($"🕐 <b>Stato:</b> {marketStatus}");
            message.AppendLine($"💡 <b>Motivo:</b> {TranslateReason(signal.Reason)}");
            message.AppendLine();
            message.AppendLine($"⏰ {DateTime.Now:dd/MM HH:mm} | Sistema Enhanced v2.0 🇮🇹");

            return message.ToString();
        }

        #region Helper Methods

        private string GetSignalEmoji(SignalType type) => type switch
        {
            SignalType.Buy => "📈",
            SignalType.Sell => "📉",
            SignalType.Warning => "⚠️",
            _ => "ℹ️"
        };

        private string GetModeEmoji(AnalysisMode mode) => mode switch
        {
            AnalysisMode.FullAnalysis => "🔥",
            AnalysisMode.PreMarketWatch => "🌅",
            AnalysisMode.OffHoursMonitor => "🌙",
            _ => "📊"
        };

        private string GetMarketFlag(string market) => market switch
        {
            "EU" => "🇪🇺",
            "US" => "🇺🇸",
            _ => "🌍"
        };

        private string GetConfidenceEmoji(double confidence) => confidence switch
        {
            >= 90 => "🔥🔥🔥",
            >= 80 => "🔥🔥",
            >= 70 => "🔥",
            >= 60 => "👍",
            _ => "⚠️"
        };

        private string GetSignalTypeInItalian(SignalType type) => type switch
        {
            SignalType.Buy => "ACQUISTO",
            SignalType.Sell => "VENDITA",
            SignalType.Warning => "ATTENZIONE",
            SignalType.Hold => "MANTIENI",
            _ => "SEGNALE"
        };

        private string GetMarketConditionInItalian(string condition) => condition switch
        {
            "Bullish" => "Rialzista 🐂",
            "Bearish" => "Ribassista 🐻",
            "Sideways" => "Laterale ↔️",
            "Volatile" => "Volatile 📊",
            _ => condition ?? "Neutrale"
        };

        private string GetRSIDescription(double rsi) => rsi switch
        {
            <= 30 => "(Ipervenduto 🟢)",
            >= 70 => "(Ipercomprato 🔴)",
            _ => "(Neutrale ⚪)"
        };

        private string GetTrendDescription(double trend) => trend switch
        {
            >= 8 => "(Molto forte 💪)",
            >= 6 => "(Forte 👍)",
            >= 4 => "(Moderato ⚖️)",
            _ => "(Debole 📉)"
        };

        private string GetRiskRewardExplanation(double? ratio)
        {
            if (!ratio.HasValue || ratio <= 0)
                return "❓ RAPPORTO NON CALCOLABILE - Verificare i livelli di Stop Loss e Take Profit";

            if (ratio >= 3.0)
                return "⭐ ECCELLENTE! Per ogni €1 di rischio, potenziale di €" + ratio.Value.ToString("F1") + " di profitto";
            else if (ratio >= 2.0)
                return "✅ BUONO! Rapporto rischio/profitto favorevole";
            else if (ratio >= 1.5)
                return "⚠️ ACCETTABILE, ma rischio moderato";
            else
                return "🚨 ALTO RISCHIO! Il profitto potenziale è basso rispetto al rischio";
        }

        private string GetPracticalExample(TradingSignal signal)
        {
            var investment = 1000; // Esempio con €1000
            var shares = (int)(investment / signal.Price);
            var actualInvestment = shares * signal.Price;

            var riskAmount = shares * (signal.Price - signal.StopLoss.Value);
            var profitAmount = shares * (signal.TakeProfit.Value - signal.Price);

            return $"Con €{investment} → {shares} azioni (€{actualInvestment:F0})\n" +
                   $"• ❌ Perdita massima: €{riskAmount:F0}\n" +
                   $"• ✅ Profitto potenziale: €{profitAmount:F0}";
        }

        private string TranslateStrategy(string strategy)
        {
            if (string.IsNullOrEmpty(strategy)) return "";

            return strategy
                .Replace("market order", "ordine a mercato")
                .Replace("limit order", "ordine limite")
                .Replace("stop loss", "stop loss")
                .Replace("take profit", "take profit")
                .Replace("Default", "Standard")
                .Replace("ATR-based", "basato su ATR")
                .Replace("resistance", "resistenza")
                .Replace("support", "supporto");
        }

        private string TranslateReason(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return "";

            return reason
                .Replace("RSI oversold", "RSI ipervenduto")
                .Replace("RSI overbought", "RSI ipercomprato")
                .Replace("MACD bullish crossover", "MACD attraversamento rialzista")
                .Replace("MACD bearish crossover", "MACD attraversamento ribassista")
                .Replace("Strong volume", "Volume forte")
                .Replace("High confidence", "Alta affidabilità")
                .Replace("Technical breakout", "Breakout tecnico")
                .Replace("Support bounce", "Rimbalzo su supporto")
                .Replace("Resistance rejection", "Rifiuto resistenza");
        }

        private string GetMarketStatusInItalian(AnalysisMode mode) => mode switch
        {
            AnalysisMode.FullAnalysis => "Mercato Aperto 🟢",
            AnalysisMode.PreMarketWatch => "Pre-Mercato 🌅",
            AnalysisMode.OffHoursMonitor => "Mercato Chiuso 🌙",
            _ => "Monitoraggio 📊"
        };

        private string FormatVolume(long volume)
        {
            return volume switch
            {
                >= 1_000_000 => $"{volume / 1_000_000.0:F1}M",
                >= 1_000 => $"{volume / 1_000.0:F0}K",
                _ => volume.ToString()
            };
        }

        #endregion
    }
}