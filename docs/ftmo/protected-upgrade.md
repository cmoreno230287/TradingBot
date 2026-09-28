# Protected SMC upgrade — 2026-09-22

Scope: protected `FtmoSmc`, not legacy V2. User confirmed initial balance USD 100,000. Account variant remains unconfirmed; `RulesConfirmed=false` and live order creation remain disabled. Published runtime is unchanged.

## Predeclared validation plan

Compare three fixed profiles on 2025 development data after correctness changes: strict baseline, existing local-liquidity research profile, and that research profile with 25% FVG retracement from the near edge instead of 50%. Risk remains 0.25%, target 2R, same costs, sessions and loss controls. Evaluate the selected development candidate on the previously seen January–May 2026 comparison and higher costs. No parameter search on comparison results; no fresh holdout claim.

## Implementation

- The protected engine already required closed H1/M5 bars, confirmed pivots, minimum FVG size, bounded stops, sweep invalidation and ordered sweep/break. Legacy V2 review findings were not blindly applied to this engine.
- Reject a pivot level already broken before the sweep and duplicate H1 timestamps. Validate the supported H1/M5 timeframes and disallow the ignored legacy daily-bias switch.
- Preserve fresh-only FVG detection. The third FVG candle must be the latest completed M5 candle; there are no later completed candles that could have consumed that gap. Do not add a retrospective entry into an old FVG.
- Add `EntryRetracementFraction` (default 0.5, strictly between 0 and 1) to the protected sweep engine. Buy = upper minus width times fraction; sell = lower plus width times fraction. Continuation engine remains midpoint-only.
- Attach analyzed, sweep, break, FVG confirmation and submission-deadline timestamps plus sweep extreme to valid protected sweep signals. Deadline is the next M5 boundary after FVG confirmation. Both shared risk and bridge submission enforce it. Pending lifetime remains separately 15 minutes.
- Bridge checks limit side, stop distances and tick alignment before reserving/sending. Existing broker order_check, fresh quote, spread, loss headroom, volume and exposure guards remain active.
- Persist signal context with order reservations/tickets. Cancel an owned pending order when a monitoring quote breaches the sweep extreme. After an ambiguous SMC submission, cancel unmatched owned pending exposure rather than resubmit. Dedicated magic number/bridge remains required. Existing native stops, expiry, account exposure reconciliation and write-ahead duplicate protection remain active across restarts.
- Link risk logs to setup IDs/context and emit activity counters scoped explicitly to process lifetime. Broker account trades today is separate from accepted orders and unique technical setups. Rejected-proposal counts are attempts, not unique signals. Filled trades remain reconciled via broker history/tracking.

Monitoring cancellation is best effort: a broker may fill a pending order before cancellation or while disconnected. Native SL/expiry still apply. OHLC backtests do not assume monitoring can cancel ahead of a gap fill; they retain conservative fill/stop handling. Closed-bar signal correctness does not establish tick-level execution or profitability.

## Results and selection

| Profile / period | Unique setups | Orders | Fills | Net USD | Profit factor | Max equity drawdown USD |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Strict / 2025 | 2 | 2 | 1 | -249.26 | 0 | 249.26 |
| Local liquidity, midpoint / 2025 | 14 | 14 | 7 | 795.22 | 2.063 | 627.54 |
| Local liquidity, shallow / 2025 | 13 | 13 | 7 | 730.03 | 1.977 | 565.40 |
| Selected midpoint / Jan–May 2026 | 3 | 3 | 1 | -249.60 | 0 | 249.60 |
| Selected midpoint / same period, higher costs | 3 | 3 | 1 | -249.66 | 0 | 249.66 |
| Selected midpoint / May 22–June 3, 2026 | 0 | 0 | 0 | 0 | N/A | 0 |

Midpoint was retained before comparison evaluation: shallow entry did not increase fills or net return. The earlier local-liquidity development result was 15 setups, 8 fills and $542.80; rejecting previously broken structure removed an invalid setup. This is a correctness fix, not evidence that further filtering will improve profitability.

Every run ended TARGET_NOT_REACHED. No modeled external daily/total limit violation occurred. The previously untested additional date window is only 2,574 bars with 16 missing weekday bars and zero trades; it cannot establish out-of-sample profitability. Development/comparison still contain 792/200 missing weekday bars respectively. Costs were unchanged at spread 1 pip, commission $7/lot, slippage reserve 1 pip; stress used 1.5 pips, $10 and 2 pips.

Decision: keep `appsettings.ftmo.example.json` as the strict baseline, keep `appsettings.ftmo-smc-research.example.json` as the opt-in frequency candidate, and retain the shallow experiment only as a generated research artifact. No live gates were enabled. Source strategy/protection fixes apply when either protected profile is used. The new continuation engine remains research-only and was not tuned in this work.

## Validation and remaining operational work

Build and automated .NET/Python suites cover ordered signals, future/forming-candle exclusion, expiry, buy/sell retracement, invalidation, unsupported settings, duplicate/restart handling, ambiguous submissions, broker distance/tick checks, loss headroom, floating exposure, costs and Prague DST reset behavior. Tests use mocks; no broker orders were sent.

Before demo execution: confirm account variant and restrictions, actual account ID, challenge start, broker costs and populated news blackout windows. Historical news data remains unavailable. Intrabar FVG retouches between confirmation and submission are not reconstructed from ticks: the engine checks completed bars and the bridge checks the current quote. This is a remaining live-validation limitation, not a guarantee of an untouched gap at submission.

No release was deployed because comparison evidence does not establish positive expectancy. Future deployment must include the .NET app **and** updated `mt5_bridge.py`/`ftmo_guard.py`; publishing the CLI alone does not deploy Python. Preserve account state/attempts across upgrades, back up runtime binaries/configuration, verify the selected profile with `help`, and use a confirmed demo account for forward validation before reassessing Challenge use. Restore the previous binaries/configuration for rollback while retaining the attempts ledger.

Full local runs are in `reports/ftmo/protected-upgrade/`; machine-readable summaries and profile fingerprints are in [protected-upgrade-results.json](protected-upgrade-results.json). Reproduce any run with `ftmo-backtest config=<profile> from=<ISO UTC> to=<exclusive ISO UTC> output=<path>`.
