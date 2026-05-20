# TradingBot.CLI

Clean Architecture .NET 10 console application for a Smart Money Concepts EURUSD trading bot.

## Commands

```bash
dotnet run --project TradingBot.CLI -- start
dotnet run --project TradingBot.CLI -- stop
dotnet run --project TradingBot.CLI -- analyze
dotnet run --project TradingBot.CLI -- backtest --from 2025-05-15 --to 2026-05-15
dotnet run --project TradingBot.CLI -- download-history --from 2025-05-15 --to 2026-05-15 timeframe=M5
dotnet run --project TradingBot.CLI -- ctrader-connect
dotnet run --project TradingBot.CLI -- ctrader-authorize
dotnet run --project TradingBot.CLI -- ctrader-request-token
dotnet run --project TradingBot.CLI -- ctrader-refresh-token
dotnet run --project TradingBot.CLI -- ctrader-accounts-list
dotnet run --project TradingBot.CLI -- ctrader-account-details
dotnet run --project TradingBot.CLI -- ctrader-symbols symbol=EURUSD
dotnet run --project TradingBot.CLI -- ctrader-createorder entry_point=1.05000 quantity=0.01 confirm_live_order=true
dotnet run --project TradingBot.CLI -- analyze-and-createorder confirm_live_order=true
```

`start` validates configuration, runs `ctrader-connect`, then runs `analyze-and-createorder` repeatedly using `AnalysisExecutionIntervalSeconds` from `TradingBot.CLI/appsettings.json`.

Live-environment cTrader order creation requires `AllowLiveOrderCreation=true` in `TradingBot.CLI/appsettings.json` plus the explicit `confirm_live_order=true` command argument.

Historical backtesting data is selected through the provider-neutral `Backtesting.DataSource` setting. Current values are `Sample` and `cTrader`; future providers such as TradingView can be added behind the same `IHistoricalMarketDataProvider` abstraction.
