# TradingBot.CLI

Clean Architecture .NET 10 console application for a Smart Money Concepts EURUSD trading bot.

## Commands

```bash
dotnet run --project TradingBot.CLI -- start
dotnet run --project TradingBot.CLI -- stop
dotnet run --project TradingBot.CLI -- analyze
dotnet run --project TradingBot.CLI -- backtest --from 2025-05-15 --to 2026-05-15
dotnet run --project TradingBot.CLI -- find-recent-setups count=10 max_days=30
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

Historical backtesting data is selected from the first enabled entry in `Backtesting.DataSources`. Current provider values are `cTrader` and `MT5`.

`find-recent-setups` starts with the previous trading day, scans backward by trading day through the first enabled historical provider, and writes up to 2000 valid setups to `reports/recent-setups`. `max_days` defaults to `30`; use `max_days=0` for an unbounded scan.
