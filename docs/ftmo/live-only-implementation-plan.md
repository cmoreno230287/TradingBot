# Live-only protected FTMO SMC implementation plan

Implementation update: source changes and a local release package are complete; see [validation](live-only-validation.md) and [current operations](live-only-operations.md). Deployment and real-account execution were not performed.

Status: proposed, 2026-09-22. Supersedes `next-implementation-plan.md` for this request. Creating this plan does not remove code, enable orders or modify the published runtime.

## Target

Explicit user requirements: **live trading enabled by default** and **complete removal of the simulation feature**. These defaults are part of the planned implementation; this document update does not activate the current bot.

One operational path: current MT5 market data -> protected SMC analysis -> account risk validation -> actual MT5 order -> broker reconciliation and protection. No backtest, historical trade simulation, replay or paper-trading execution mode. Read-only status and entry-paused protection remain operational controls, not simulated trading modes. Automated tests with fake adapters remain development tooling and must never contact a real account.

Use the user-confirmed USD 100,000 starting balance, 0.25% risk and existing loss safeguards. Account variant, account ID, restrictions and challenge start must be confirmed before activation. Historical price bars needed for current H1/M5 analysis and actual broker deal history remain necessary; removing simulation must not remove them.

## 1. Remove simulation and offline research from the product

- Remove `backtest`, `ftmo-backtest`, `backtest-learning` and the historical setup-research command `find-recent-setups`, their handlers and help entries.
- Remove the `TradingBot.Backtesting` project from solution/build references and remove simulation-only models, factories and configuration after tracing consumers. Move any genuinely shared reporting calculation to Reporting before deleting its old owner.
- Remove `FtmoCommands.BacktestAsync` and `OfflineCsvMarketDataProvider`. Eliminate unconditional historical-provider construction during live CLI startup.
- Remove the standalone `download-history` research command and offline cache infrastructure only where no current-data or actual-history consumer depends on it. Preserve MT5 candle retrieval for current strategy lookback and broker deal retrieval for risk/reconciliation.
- Remove simulator-specific tests/references; retain strategy, sizing, reset, order and integration contract tests. Update configuration serialization/validation tests for the smaller schema.
- Remove backtest options from active examples and runtime serialization. Existing configurations with obsolete fields should receive a clear migration diagnostic rather than silently imply those features still work.
- Preserve existing user data, result files and reports. Mark old research documents historical; do not erase results or reset the broker-state ledger as cleanup.

Acceptance: solution builds without Backtesting; live startup requires no offline CSV cache or backtest-source configuration; removed commands cannot execute; normal candle analysis and real trade-history reporting still work.

## 2. Consolidate the live configuration and entry gate

- Prepare one production-oriented protected SMC profile, with clearly documented ownership of strategy, execution and risk settings. Keep alternative legacy/experimental engines unselected; removing them is outside the simulation-removal scope.
- Set `LiveTradingEnabled=true` by default in code and the live profile. Consolidate the redundant broker permission flag; if retained for compatibility, default `MT5.AllowLiveOrderCreation=true` in the live profile as well. Normal `start` runs the actual protected trading loop without requiring a per-order confirmation flag.
- Preserve an explicitly configured `false` during configuration migration. An omitted setting receives the new live-enabled default; document that change. Account identity and rule confirmation must remain explicit rather than being fabricated by migration.
- Enforce a single effective entry permission across CLI and bridge. `LiveTradingEnabled=false`, missing account confirmation or mismatched account identity must block new orders.
- Disabling entries must not disable monitoring/cancellation/flattening of existing owned exposure. State these semantics explicitly, including shutdown behavior.
- Show effective profile, account identifier, strategy and risk settings at startup without credentials. Confirm the broker account matches the intended account; demo testing is not a required product mode in this revised plan.

Acceptance: a correctly configured account starts real protected trading with the default live-enabled settings and no extra enablement step; explicit disablement blocks new orders; account/risk checks still apply; paused entries leave protection available. Migration does not select an account or start a process.

## 3. Extract the live coordinator and isolate protection scheduling

- Move execution orchestration from CLI into an application service with typed broker interfaces and a testable clock. CLI remains command parsing and dependency wiring.
- Schedule protection separately from signal analysis and reporting, preventing overlapping monitor cycles and accumulated stale analysis requests.
- Block new entries whenever the last successful protection/account snapshot is too old. Add request deadlines, actual monitoring-latency metrics and visible unhealthy-state reasons.
- Serialize account mutations, keep bridge critical sections bounded and account for MT5 calls that can block. Separate .NET tasks alone cannot guarantee protection progress inside a blocked bridge.
- Preserve native broker SL/TP/expiry and document terminal/network failure behavior. Do not promise monitoring while the terminal is unreachable.

Acceptance: delayed analysis/reporting does not stall the monitor scheduler in integration tests; stale monitoring blocks orders; concurrent operations cannot create duplicate exposure.

## 4. Harden live account protection and order reconciliation

- Use structured halt reasons/states rather than string-prefix classification. Persist daily halt until Prague reset and total/kill halts until deliberate reconciliation.
- Audit account-wide equity, floating P/L, commissions, swap, remaining stop exposure, pending reservations, partial fills/exits and day-boundary calculations.
- Keep independent final broker-side risk checks. Test C# sizing and Python submission decisions against common JSON boundary scenarios without a trading simulator.
- Maintain durable reserved/accepted/rejected/unknown/reconciled order states. Reconcile orders, positions and deals after timeout/restart; never blindly retry an ambiguous request.
- Handle cancellation/fill races, unmatched tickets and partial fills; protect only owned orders while counting foreign exposure in account limits.

Acceptance: deterministic tests cover duplicate attempts, lost responses, partial execution, corrupt state, reset/DST, floating losses and cancellation failure. Existing attempt history is retained across migration.

## 5. Validate live signals, data and news readiness

- Preserve completed-bar SMC confirmation, ordered sweep/break/FVG, expiry, entry/stop/target checks and supported timeframe validation.
- Validate H1 and M5 continuity using an explicit market-session calendar. Distinguish closures from missing data.
- Define and verify consumed-FVG checks between confirmation and submission using suitable broker data; reject entries when required coverage is unavailable.
- Treat populated/current news blackout coverage as distinct from an enabled boolean. Begin with an explicit manually maintained coverage workflow rather than silently adding a service dependency.
- Log signal timestamps, rejection codes, risk decisions, order tickets, fills, cancellations, spread/slippage and net realized performance. Distinguish unique setups, repeated analysis and order attempts.
- Add read-only status/account-readiness commands. Replace offline performance evaluation with reporting on actual broker fills; do not automatically tune the strategy or raise risk based on a small sample.

Acceptance: malformed/stale/unverifiable inputs block entries with useful reasons; empty calendar data is not presented as verified news protection; status sends no orders; reported P/L reconciles to actual broker deals including costs.

## 6. Package and activate the live-only release

- Complete build, unit and mocked broker-contract tests. No simulator or paper-trading release is produced.
- Prepare a versioned artifact containing .NET CLI, both Python bridge modules and the intended configuration. Include migration notes, checksums, state compatibility and rollback instructions.
- Back up runtime binaries/configuration; preserve reports, account state and order-attempt history. CLI publishing alone does not deploy Python.
- At startup, validate connectivity/account identity, configuration, quote freshness and protection health before the first order. Live entry permissions are enabled by default; validation is an automatic pre-order gate, not another manual enablement step. Do not create a test order just to check installation.
- Activate actual broker execution only for the explicitly intended account once its required details and permission gates are satisfied. Observe the first strategy-generated orders and reconcile results; do not promise that they will occur on demand.
- Rollback must preserve compatible reconciliation state and safety halts, not delete state to force a restart.

Acceptance: published artifact has no simulation commands/dependency and defaults to live trading; component versions and effective account/profile are verified; protection and status operate; any submitted order is an actual broker order through the protected path. Publishing alone does not launch trading.

## Delivery order and limits

Deliver simulation removal/configuration migration first, then entry gates and coordinator, then protection/data/reconciliation changes, then the release package. Keep changes reviewable and preserve current working-tree work.

No backtest, replay, paper-trading or mandatory demo phase remains. Engineering validation still uses isolated automated tests. Previous negative/inconclusive strategy evidence remains relevant: removing simulation does not establish profitability or FTMO readiness. Future performance observations will be actual account outcomes.
