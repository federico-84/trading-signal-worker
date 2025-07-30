using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace PortfolioSignalWorker.Models;

public class StockIndicator
{
    [BsonId]
    public ObjectId Id { get; set; }

    public string Symbol { get; set; }
    public double RSI { get; set; }
    public double MACD { get; set; }
    public double MACD_Signal { get; set; }
    public double MACD_Histogram { get; set; }
    public bool MACD_Histogram_CrossUp { get; set; }
    public double Price { get; set; }
    public long Volume { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Campi per analisi storica (esistenti)
    public double RSI_SMA_5 { get; set; }      // RSI media 5 periodi
    public double RSI_SMA_14 { get; set; }     // RSI media 14 periodi
    public bool RSI_Confirmed { get; set; }    // RSI < 30 per almeno 2 periodi
    public bool MACD_Confirmed { get; set; }   // MACD cross confermato
    public bool VolumeSpike { get; set; }      // Volume > media 20 periodi

    // Nuove proprietà per Yahoo Finance
    public double PreviousClose { get; set; }
    public double Change { get; set; }
    public double ChangePercent { get; set; }
    public double DayHigh { get; set; }
    public double DayLow { get; set; }
    public double Open { get; set; }
    public double PricePosition { get; set; }    // 0-100% posizione nel range giornaliero
    public double DailyVolatility { get; set; }  // Volatilità giornaliera
}