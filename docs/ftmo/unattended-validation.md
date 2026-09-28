# Source validation — unattended improvements, 2026-09-23

Completed source changes and a local Release package. No installed runtime files, account-rule confirmations, live terminal sessions or broker orders were changed/started by this work.

## Checks completed

- .NET executable suite: **76 checks passed**. Includes bounded retries, ambiguous submission suppression, noncooperative analysis isolation, sibling-loop supervision, automatic calendar HTTP refresh/failure/expiry, strategy parameter identity, shared risk boundaries, permissions and SMC rules.
- Python suites: **56 tests passed**. Includes cancellation/fill races, continuing after failed closes, durable recovery, entry/exit costs, corrupted journals, post-action broker confirmation, native expiration, final-send deadlines/quotes, priority scheduling, watchdog health decisions and performance calculations.
- Shared .NET/Python fixture: **11 risk boundary cases**.
- PowerShell scripts parsed successfully; all **9 Python modules** parsed successfully.
- Release publish succeeded to `reports/source-validation/unattended-release/`.
- Release component check found all eight required bridge/dependency files, with no Backtesting assembly. A local checksum manifest covers **27 files**.
- Packaged `help` succeeded using the sanitized live profile. Malformed configuration and removed `ftmo-backtest` both returned exit code **2**.
- Tracked diff whitespace check passed.

The shell disallows PowerShell script execution. Package validation used the equivalent `dotnet publish` command without changing that policy. The new `tzdata` dependency was installed only into `reports/source-validation/python-deps/` for Python tests; the installed runtime Python environment was not modified.

Test commands:

```powershell
dotnet run --project TradingBot.Tests --no-restore
python -B -m unittest discover -s tools/mt5-bridge -p 'test_*.py'
dotnet publish TradingBot.CLI/TradingBot.CLI.csproj -c Release --no-restore --nologo -o reports/source-validation/unattended-release
```

Python tests require the source requirements, including `tzdata`, available on the interpreter's module path. In this session the existing interpreter was used with an isolated source-only `PYTHONPATH` for that dependency. Test doubles replaced all broker calls; no real trading was used for verification.

## Remaining external validation/configuration

- Confirm the actual account variant, start date and supported challenge rules. Existing source confirmation gates remain intact.
- Supply a trusted calendar provider/feed matching the documented normalized contract, or implement its provider-specific mapping. No provider or credentials were supplied.
- Install dependencies and explicitly configure process supervision in the intended deployment environment. Watchdog decisions are unit-tested; real terminal/process failure recovery and unattended longevity have not been exercised here.
- Accumulate live strategy evidence before asserting a profitable edge, high win rate or probability of passing a challenge. Entry filters and risk percentage were not loosened.

See [implementation and operating details](unattended-improvements.md). The subsequent [reliability recovery validation](reliability-recovery-validation.md) records the history-independent protection, event-persistence recovery and historical-ticket reconciliation fixes. Calendar integration is deferred for that work.
