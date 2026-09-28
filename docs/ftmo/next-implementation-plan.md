# Protected FTMO SMC reliability and validation plan

Superseded by [the live-only implementation plan](live-only-implementation-plan.md) following the user's decision to remove simulation.

Status: proposed implementation plan following code review, 2026-09-22. This document does not change execution or strategy settings.

## Scope and constraints

Improve the protected `FtmoSmc` path. Preserve legacy V2 and the published runtime during development. Initial balance is user-confirmed USD 100,000; retain 0.25% risk, one active account-wide exposure and existing loss safeguards. The account variant remains unconfirmed. The supplied objectives remain recorded separately from assumptions; no automatic conversion to another FTMO rule model.

Current engineering tests do not establish profitability. The latest local-liquidity development result was seven fills and +$795.22; comparison had one fill and -$249.60. No tested protected candidate reached the target. Existing comparison data cannot become an untouched holdout again.

## 1. Unify execution permission and configuration ownership

Affected: `TradingBot.Application/TradingBotOptions.cs`, `OptionsValidator.cs`, `TradingBot.CLI/FtmoCommands.cs`, bridge request handling and protected profiles.

- Define explicit offline, monitor-only and trading modes. Monitor-only must specify whether owned-position cancellation/flattening is permitted; it must never create exposure.
- Make `LiveTradingEnabled=false` veto new orders in the protected path, including a final bridge-side permission check. Keep account ID and rule confirmation requirements. Disabling entries must not silently disable protection of existing exposure.
- Report effective engine, profile, execution mode and risk source at startup without credentials. Retire conflicting aliases through an explicit migration/validation policy.
- Record account variant as unconfirmed until supplied; reject unsupported rule models before trading activation.

Acceptance: a permission matrix proves that any missing entry authorization blocks submission, monitor-only makes zero create calls, protection stays available, and an existing legacy configuration is not silently switched.

## 2. Match live and simulated risk semantics

Affected: `FtmoProtection.cs`, `FtmoBacktestingEngine.cs`, `tools/mt5-bridge/ftmo_guard.py`, .NET/Python tests.

- Define typed halt reasons and explicit states: running, daily halt until Prague reset, persistent total/kill halt, target reached, and reconciliation required. Stop classifying behavior by string prefixes.
- Add persistent-until-reset daily halt behavior to the simulation, including intrabar safety events and later equity recovery.
- Audit floating P/L, fee reserves, partial fills/exits, pending exposure, target/minimum-days handling and midnight accounting. Document intentional conservative differences rather than hiding them.
- Create common JSON risk scenarios consumed by C# and Python tests. Keep broker-side revalidation; avoid removing it merely to eliminate duplicated calculations.

Acceptance: both implementations agree on eligibility, monetary boundaries and halt/reset transitions across the same fixtures, including 23/25-hour Prague days. Backtest cannot reopen on a daily-halted day after recovery. Baseline reports are regenerated after semantic changes.

## 3. Separate protection scheduling from strategy analysis

Affected: new application execution coordinator and broker interfaces; CLI reduced to configuration/wiring; infrastructure adapter and bridge scheduling.

- Introduce a testable clock, cancellation and typed broker requests/responses. Replace scattered JsonElement field access in orchestration.
- Schedule protection independently from analysis and trade reporting. Avoid overlapping protection cycles, skip accumulated analysis work, and serialize account mutations.
- Require a recent successful protection snapshot before accepting new entries. If monitoring becomes unhealthy, block new orders and expose the failure.
- Account for the bridge's account lock and blocking terminal calls: independent .NET tasks alone do not guarantee independent protection. Keep critical sections bounded and prioritize protection where feasible.
- Add explicit request deadlines, heartbeat and actual protection-latency metrics. Do not promise monitoring during terminal/network failure; native SL/expiry remain required.

Acceptance: a deliberately stalled analysis/reporting request does not stall the protection scheduler. Stale protection blocks entries. Concurrent submission/protection tests produce no duplicate exposure. Shutdown and timeout behavior are tested with fake adapters; actual MT5 blocking behavior is reserved for demo validation.

## 4. Harden market data and pending-order lifecycle

Affected: protected strategy/data validation, signal metadata, bridge state and tests.

- Validate H1 continuity against an explicit trading-session calendar; distinguish legitimate closures from unexpected gaps. Preserve existing closed-bar and M5 continuity checks.
- Give freshness failures structured reasons with last completed candle timestamps. Continue rejecting incomplete or malformed data.
- Define consumed-FVG semantics precisely and check intrabar touches between confirmation and submission using available tick or sufficiently granular data. If the required interval cannot be verified, block that entry rather than assume the gap remained untouched.
- Represent order attempts explicitly as reserved, accepted, rejected, unknown, reconciled and terminal. Preserve write-ahead reservations and never blindly retry an unknown submission.
- Reconcile tickets/history after restart, timeout or partial fills; retain ownership boundaries and native expiry. Avoid persistent ambiguous state indefinitely cancelling unrelated later owned orders.

Acceptance: tests cover buy/sell consumed gaps, real H1 gaps versus closures, native fill racing cancellation, partial fills, lost responses, restart with missing ticket, and foreign-magic isolation. OHLC simulation must not claim a cancellation occurred before an unknowable fill.

## 5. Make operational readiness observable

Affected: application coordinator, reporting, NewsFilter/configuration, operational documentation.

- Distinguish an enabled news option from populated, current calendar coverage. Establish an explicit manually maintained calendar workflow first; do not silently add an external service dependency.
- When the selected execution policy requires news coverage, unavailable/stale coverage blocks new entries. Preserve historical tests' explicit limitation when historical calendar data is absent.
- Emit structured strategy and risk reason codes, unique setups, order attempts, accepted orders, fills, cancellation causes, monitoring latency and connection state.
- Separate process counters from durable account/run statistics and deduplicate reconciled broker events.
- Add a read-only status command showing effective profile and readiness without sending orders.

Acceptance: an empty news list cannot be reported as verified news protection; stale protection and unknown order state are visible; counters distinguish repeated analysis from real setups and fills; status has no trading side effects.

## 6. Reassess strategy performance after reliability fixes

- Freeze a corrected baseline and fingerprint code/configuration/data/cost assumptions.
- Predeclare a small set of SMC experiments and evaluation criteria before running them. Hold monetary risk and account loss limits constant.
- Evaluate development expectancy, drawdown, frequency and fill rate jointly. Reject frequency-only gains with worse expected returns.
- Use comparison data for regression checks, then acquire an adequate previously unseen period for final evaluation. Use walk-forward tests and cost stress; disclose sample size and uncertainty. Do not keep retuning a failed holdout.
- Require positive net expectancy supported by enough independent trades and acceptable drawdown under declared costs before promotion. Fix exact promotion criteria before fresh evaluation, not after seeing results.

Acceptance: reproducible reports include rejected candidates and cost/data limitations. Failure to establish an edge keeps the profile research-only; no risk increase is used to manufacture target attainment.

## 7. Demo validation and controlled release

- Resolve account variant, demo account identity, costs, calendar coverage and rule restrictions before connecting a trading-enabled profile.
- Forward-test signal timing, entry rejection, fills/slippage, stops, expiry, resets, reconnects and restart recovery. Offline mocks do not substitute for this evidence.
- Prepare a versioned release manifest covering .NET, both Python bridge modules and the intended profile. CLI publishing alone is insufficient.
- Back up runtime binaries/configuration and account state. Deploy in monitor-only mode first; enable new orders only after readiness gates pass.
- Rollback restores compatible binaries/configuration without deleting the order-attempt ledger or silently resetting safety halts. Verify state-schema compatibility before release.

Acceptance: documented demo evidence, verified component versions/profile, retained reconciliation state, and a tested rollback procedure. Existing runtime remains untouched until this stage is justified.

## Delivery sequence

First delivery: phases 1–2, because clear permissions and matching risk semantics are prerequisites for trustworthy validation. Second: phase 3, with coordinator contract tests. Third: phases 4–5. Fourth: performance research and conditional demo/release work. Use small reviewable changes; retain the current project layout rather than rewrite the repository.

Open dependencies: account variant/rule confirmation; adequate unseen historical and news data; demo environment and execution evidence. These do not block offline engineering phases, but they do block claims of Challenge readiness.
