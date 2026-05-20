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
            File.WriteAllText(path, JsonSerializer.Serialize(defaults, SerializerOptions));
            return defaults;
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<TradingBotOptions>(json, SerializerOptions) ?? new TradingBotOptions();
    }
}
