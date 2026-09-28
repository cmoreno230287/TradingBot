using System.Text.Json;
using System.Text.Json.Nodes;
using TradingBot.Application;
using TradingBot.Domain;
using TradingBot.Infrastructure.Broker;
using TradingBot.Infrastructure.Configuration;
using TradingBot.Infrastructure.Filters;
using TradingBot.Infrastructure.MarketData;
using TradingBot.Infrastructure.MetaTrader;
using TradingBot.Infrastructure.Sessions;
using TradingBot.Reporting;
using TradingBot.Shared;
using TradingBot.Strategies;


internal static partial class CliCommands
{
    internal static bool TryReadDecimalArg(string[] args, string name, out decimal value)
    {
        value = 0m;
        var prefix = $"{name}=";
        var raw = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return raw is not null
            && decimal.TryParse(raw[prefix.Length..], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    internal static bool HasFlagArg(string[] args, string name, string expectedValue)
    {
        var prefix = $"{name}=";
        var raw = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return raw is not null && string.Equals(raw[prefix.Length..], expectedValue, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool HasStartOnceArg(string[] args) =>
        args.Skip(1).Any(arg => string.Equals(arg, "once", StringComparison.OrdinalIgnoreCase));

    internal static bool IsTrackingMode(string[] args)
    {
        var mode = ReadKeyValueArg(args, "mode");
        return string.Equals(mode, "traking", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "tracking", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? ReadKeyValueArg(string[] args, string name)
    {
        var prefix = $"{name}=";
        var raw = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return raw is null ? null : raw[prefix.Length..];
    }

}
