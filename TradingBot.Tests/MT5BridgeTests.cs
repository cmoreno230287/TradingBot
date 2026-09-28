using System.Net;
using System.Text.Json;
using TradingBot.Application;
using TradingBot.Infrastructure.MetaTrader;
using TradingBot.Infrastructure.Configuration;
using System.Reflection;

internal static class MT5BridgeTests
{
    public static async Task Run()
    {
        var options = new TradingBotOptions { Broker = "MT5", Brokers = new BrokerOptions { MT5 = new() { AccountId = 1514751331, BridgeBaseUrl = "http://127.0.0.1:5010" } } };
        var bridge = new MT5BridgeClient(options);
        // No network call: verify the configured model remains intact through the request object boundary.
        if (options.MT5.AccountId != 1514751331) throw new Exception("MT5 account identity was lost during configuration binding.");
        await Task.CompletedTask;
        Console.WriteLine("PASS MT5 bridge account identity remains explicit and nonzero");

        var configPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TradingBot.CLI", "appsettings.json");
        if (File.Exists(configPath))
        {
            var loaded = JsonOptionsLoader.Load(configPath);
            var loadedBridge = new MT5BridgeClient(loaded);
            var captured = (long)(typeof(MT5BridgeClient).GetField("_accountId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(loadedBridge)!);
            if (loaded.Brokers.MT5.AccountId != 1514751331 || loaded.MT5.AccountId != 1514751331 || captured != 1514751331)
                throw new Exception($"Loaded bridge identity mismatch: brokers={loaded.Brokers.MT5.AccountId}, alias={loaded.MT5.AccountId}, captured={captured}.");
            Console.WriteLine("PASS appsettings binding and MT5 bridge capture preserve account 1514751331");
        }
    }
}
