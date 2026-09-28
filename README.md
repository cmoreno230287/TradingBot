# TradingBot — live-only MT5
.NET 10 EURUSD Smart Money Concepts bot with protected account-wide risk checks.

Live entry is enabled by default. The source configuration selects protected SMC at 0.25% risk on a USD 100,000 initial balance. Orders still require the intended account, confirmed rules, fresh protection/data and current news coverage. No simulation, replay or paper-trading commands remain.

See [live operation, migration and recovery](docs/ftmo/live-only-operations.md) before starting.

See [unattended protection, recovery, calendar integration and strategy evidence](docs/ftmo/unattended-improvements.md) for the current source improvements and remaining configuration requirements.

```powershell
dotnet build TradingBot.sln
dotnet run --project TradingBot.Tests --no-build
dotnet run --project TradingBot.CLI -- help
dotnet run --project TradingBot.CLI -- status
dotnet run --project TradingBot.CLI -- analyze
```

`status` reads cached protection health, reconciliation and performance without placing or cancelling orders. `analyze` reads current market data. `start` runs actual protected trading and monitoring. All commands accept `config=<path>`. A sanitized starting profile is [appsettings.live.example.json](TradingBot.CLI/appsettings.live.example.json); account and rule confirmation are intentionally not fabricated.

```powershell
./tools/package-live.ps1
```

Packaging produces .NET and Python bridge files together with a checksum manifest under `reports/live-release/`. It does not deploy or start trading.

Historical research reports under `docs/ftmo/` are retained as archives. Their simulation commands no longer exist, and their results are not evidence of current live profitability.
