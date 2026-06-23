# TradingBot MT5 Bridge

This local bridge exposes the installed MT5 terminal to `TradingBot.CLI` over HTTP.

## Prerequisites

1. Open FTMO-MT5.
2. Log in to the account you want to test.
3. Keep the terminal running.
4. Install Python 3.10+ for Windows.

## Setup

From the repository root:

```powershell
cd C:\Projects\TradingBot
python -m venv .venv-mt5
.\.venv-mt5\Scripts\Activate.ps1
pip install -r tools\mt5-bridge\requirements.txt
```

## Run

```powershell
python tools\mt5-bridge\mt5_bridge.py --host 127.0.0.1 --port 5010
```

If MT5 initialization fails with `IPC timeout`, start the bridge with the exact FTMO terminal executable:

```powershell
python tools\mt5-bridge\mt5_bridge.py --host 127.0.0.1 --port 5010 --terminal-path "C:\Program Files\FTMO Global Markets MT5 Terminal\terminal64.exe"
```

Keep this process running in one terminal, then test the bot from another terminal:

```powershell
dotnet run --project TradingBot.CLI -- mt5-test-connection
dotnet run --project TradingBot.CLI -- mt5-account-details
dotnet run --project TradingBot.CLI -- mt5-symbols symbol=EURUSD
```

## Endpoints

- `GET /health`
- `GET /account`
- `GET /account-risk`
- `GET /symbols/{symbol}`
- `GET /candles?symbol=EURUSD&timeframe=M5&from=2026-05-15T00:00:00Z&to=2026-05-16T00:00:00Z`
- `GET /positions?symbol=EURUSD&magicNumber=20260520`
- `GET /orders?symbol=EURUSD&magicNumber=20260520`
- `POST /orders`
- `POST /orders/{id}/cancel`

## Notes

- The bridge uses the account already logged into the MT5 terminal.
- Order creation is still controlled by `TradingBot.CLI/appsettings.json`.
- Pending orders use the `MT5:PendingOrderExpirationHours` value sent by the bot. The default is 12 hours.
- Keep `MT5:AllowLiveOrderCreation` as `false` until connection, account, symbols, candles, and active trade checks pass.
