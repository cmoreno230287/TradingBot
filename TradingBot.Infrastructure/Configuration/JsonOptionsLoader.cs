using System.Text.Json;
using TradingBot.Application;

namespace TradingBot.Infrastructure.Configuration;

public static class JsonOptionsLoader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static TradingBotOptions Load(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new TradingBotOptions();
            defaults.Normalize();
            File.WriteAllText(path, JsonSerializer.Serialize(defaults, SerializerOptions));
            return defaults;
        }

        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.EnumerateObject().Any(p => p.Name.Equals("Backtesting", StringComparison.OrdinalIgnoreCase)
            || p.Name.Equals("MinimumBacktestTrades", StringComparison.OrdinalIgnoreCase)
            || p.Name.Equals("RecommendedBacktestTrades", StringComparison.OrdinalIgnoreCase)))
            Console.Error.WriteLine("Configuration migration: obsolete simulation settings are ignored; remove Backtesting and backtest trade-count fields.");
        var options = JsonSerializer.Deserialize<TradingBotOptions>(json, SerializerOptions) ?? new TradingBotOptions();
        options.Normalize();
        return options;
    }
}
