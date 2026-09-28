import copy
import json
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

import ftmo_guard as guard


def camel(value):
    if isinstance(value, dict):
        return {k[0].lower() + k[1:]: camel(v) for k, v in value.items()}
    return value


class GuardTests(unittest.TestCase):
    def setUp(self):
        profile = json.loads((Path(__file__).resolve().parents[2] / "TradingBot.CLI/appsettings.ftmo.example.json").read_text())
        now = datetime.now(timezone.utc)
        start = now.replace(hour=0, minute=0, second=0, microsecond=0)
        self.payload = {"policy": camel(profile["FtmoProtection"]), "dayStartUtc": start.isoformat(),
                        "nextDayStartUtc": (start + timedelta(days=1)).isoformat(), "symbol": "EURUSD",
                        "magicNumber": 77, "maxSpreadPips": 1.5, "lots": 2.13,
                        "signal": {"setupId": "stable-id", "direction": 0, "isValidSetup": True,
                                   "entryPrice": 1.1, "stopLoss": 1.099, "takeProfit": 1.102, "riskReward": 2}}
        self.payload["policy"]["flattenBeforeResetMinutes"] = 0  # Independent of test clock.
        self.payload.update(entryEnabled=True, accountId=123)
        self.payload["policy"].update(enabled=True, rulesConfirmed=True, accountVariant="Challenge")
        guard.PROTECTION_HEALTH[123] = now.timestamp()
        self.addCleanup(guard.PROTECTION_HEALTH.clear)
        self.addCleanup(guard.PROTECTION_RESULTS.clear)
        self.data = {"accountId": 123, "entryTimes": [], "account": {"balance": 100000, "equity": 100000,
                     "dailyStartingBalance": 100000, "existingRisk": 0, "activeTrades": 0,
                     "tradesToday": 0, "consecutiveLosses": 0, "lastLossUtc": None,
                     "currency": "USD", "exposureKnown": True}, "positions": [], "orders": [],
                     "instrument": {"lossPerLot": 100, "volumeMinimum": .01, "volumeMaximum": 100,
                                    "volumeStep": .01, "pipValuePerLot": 10}, "bid": 1.101, "ask": 1.1011}
        self.temp = tempfile.TemporaryDirectory()
        self.path = Path(self.temp.name) / "state.json"
        self.state_patch = patch.object(guard, "state_path", return_value=self.path)
        self.snapshot_patch = patch.object(guard, "snapshot", side_effect=lambda *_: copy.deepcopy(self.data))
        self.state_patch.start()
        self.snapshot_patch.start()
        self.tick_patch = patch.object(guard, "verify_unconsumed_entry")
        self.tick_patch.start()
        self.addCleanup(self.tick_patch.stop)
        self.addCleanup(self.temp.cleanup)
        self.addCleanup(self.state_patch.stop)
        self.addCleanup(self.snapshot_patch.stop)

    def test_accept_and_duplicate_after_restart(self):
        calls = []
        def create(payload):
            calls.append(payload)
            return 200, {"brokerOrderId": "1"}
        self.assertEqual(guard.submit(None, self.payload, create)[0], 200)
        self.assertTrue(calls[0]["requireExpiration"])
        with self.assertRaisesRegex(RuntimeError, "already submitted"):
            guard.submit(None, self.payload, create)
        self.assertEqual(len(calls), 1)
        self.assertEqual(json.loads(self.path.read_text())["attempts"]["stable-id"]["status"], "accepted")

    def test_entry_permission_and_heartbeat_fail_closed(self):
        self.payload["entryEnabled"] = False
        with self.assertRaisesRegex(RuntimeError, "permission"):
            guard.submit(None, self.payload, lambda _: self.fail("Must not submit"))
        self.payload["entryEnabled"] = True
        guard.PROTECTION_HEALTH.clear()
        with self.assertRaisesRegex(RuntimeError, "heartbeat"):
            guard.submit(None, self.payload, lambda _: self.fail("Must not submit"))

    def test_news_coverage_is_rechecked_at_submission(self):
        self.payload["useNewsFilter"] = True
        with self.assertRaisesRegex(RuntimeError, "News calendar"):
            guard.submit(None, self.payload, lambda _: self.fail("Must not submit"))
        now = datetime.now(timezone.utc)
        self.payload.update(newsCoverageFromUtc=(now - timedelta(hours=1)).isoformat(),
                            newsCoverageUntilUtc=(now + timedelta(hours=1)).isoformat(),
                            newsBlackoutWindowsUtc=[now.isoformat() + "/" + now.isoformat()])
        with self.assertRaisesRegex(RuntimeError, "News blackout"):
            guard.submit(None, self.payload, lambda _: self.fail("Must not submit"))

    def test_unknown_smc_submission_is_reconciled_by_broker_comment(self):
        def create(_):
            raise TimeoutError("lost response")
        with self.assertRaises(TimeoutError):
            guard.submit(None, self.payload, create)
        state = guard.load_state(self.path)
        comment = "FTMO" + state["attempts"]["stable-id"]["clientOrderId"]
        self.data["orders"] = [{"ticket": 8, "magic": 77, "symbol": "EURUSD", "time_setup": datetime.now(timezone.utc).timestamp(), "comment": comment,
                                "sl": 1.099, "tp": 1.102, "time_expiration": datetime.now(timezone.utc).timestamp() + 600}]
        guard.protect(None, self.payload, lambda _: self.fail("Recognized fresh order must remain"))
        self.assertEqual(guard.load_state(self.path)["attempts"]["stable-id"]["status"], "reconciled")

    def test_unresolved_attempt_blocks_other_setups(self):
        def create(_):
            raise TimeoutError("lost response")
        with self.assertRaises(TimeoutError):
            guard.submit(None, self.payload, create)
        self.payload["signal"]["setupId"] = "different-id"
        with self.assertRaisesRegex(RuntimeError, "Unresolved"):
            guard.submit(None, self.payload, lambda _: self.fail("Must reconcile first"))

    def test_shared_risk_cases(self):
        cases = json.loads((Path(__file__).resolve().parents[2] / "TradingBot.Tests/Fixtures/risk-cases.json").read_text())
        for case in cases:
            account = dict(self.data["account"], equity=case["equity"], existingRisk=case["existingRisk"])
            account.update({key: case[key] for key in ("dailyStartingBalance", "exposureKnown", "currency") if key in case})
            policy = dict(self.payload["policy"], killSwitch=case.get("killSwitch", False))
            self.assertEqual(guard.protection_status(account, policy)[0], case["code"])

    def test_expired_smc_signal_rejected(self):
        now = datetime.now(timezone.utc)
        self.payload["signal"]["smcContext"] = {"fvgConfirmedAt": (now - timedelta(minutes=10)).isoformat(),
            "submitBefore": (now - timedelta(minutes=5)).isoformat(), "sweepExtreme": 1.0991}
        with self.assertRaisesRegex(RuntimeError, "submission window"):
            guard.submit(None, self.payload, lambda _: self.fail("Must not submit"))

    def test_smc_requires_metadata(self):
        self.payload["signal"]["setupId"] = "FTMO-SMC|EURUSD|test"
        with self.assertRaisesRegex(RuntimeError, "timing metadata"):
            guard.submit(None, self.payload, lambda _: self.fail("Must not submit"))

    def test_crossed_limit_and_broker_distance_rejected(self):
        self.data["ask"] = 1.1
        with self.assertRaisesRegex(RuntimeError, "already crossed"):
            guard.submit(None, self.payload, lambda _: self.fail("Must not submit"))
        self.data["ask"] = 1.1011
        self.data["instrument"]["minimumStopDistance"] = .002
        with self.assertRaisesRegex(RuntimeError, "minimum stop"):
            guard.submit(None, self.payload, lambda _: self.fail("Must not submit"))

    def test_tick_alignment_rejected(self):
        self.data["instrument"]["tickSize"] = .0003
        with self.assertRaisesRegex(RuntimeError, "tick size"):
            guard.submit(None, self.payload, lambda _: self.fail("Must not submit"))

    def test_pending_sweep_invalidation_after_restart(self):
        now = datetime.now(timezone.utc)
        self.payload["signal"]["smcContext"] = {"fvgConfirmedAt": (now - timedelta(minutes=1)).isoformat(),
            "submitBefore": (now + timedelta(minutes=4)).isoformat(), "sweepExtreme": 1.0991}
        guard.submit(None, self.payload, lambda _: (200, {"brokerOrderId": "1"}))
        self.data["bid"] = 1.0990
        self.data["orders"] = [{"ticket": 1, "magic": 77, "time_setup": now.timestamp()}]
        calls = []
        def cancel(ticket):
            calls.append(ticket)
            return 200, {}
        guard.protect(None, self.payload, cancel)
        self.assertEqual(calls, ["1"])

    def test_expiry_cancels_without_new_strategy_signal(self):
        self.data["orders"] = [{"ticket": 1, "magic": 77, "time_setup": 0}]
        calls = []
        guard.protect(None, self.payload, lambda ticket: (calls.append(ticket) or 200, {}))
        self.assertEqual(calls, ["1"])

    def test_unknown_ticket_after_smc_timeout_is_cancelled_not_retried(self):
        now = datetime.now(timezone.utc)
        self.payload["signal"]["smcContext"] = {"fvgConfirmedAt": (now - timedelta(minutes=1)).isoformat(),
            "submitBefore": (now + timedelta(minutes=4)).isoformat(), "sweepExtreme": 1.0991}
        def create(_):
            raise TimeoutError("lost response")
        with self.assertRaises(TimeoutError):
            guard.submit(None, self.payload, create)
        self.data["orders"] = [{"ticket": 8, "magic": 77, "time_setup": now.timestamp()}]
        calls = []
        def cancel(ticket):
            calls.append(ticket)
            self.data["orders"] = []
            return 200, {}
        guard.protect(None, self.payload, cancel)
        self.assertEqual(calls, ["8"])
        with self.assertRaisesRegex(RuntimeError, "already submitted"):
            guard.submit(None, self.payload, lambda _: self.fail("Must not retry"))

    def test_ambiguous_broker_failure_never_retries(self):
        def create(_):
            raise TimeoutError("response lost after broker submission")
        with self.assertRaises(TimeoutError):
            guard.submit(None, self.payload, create)
        with self.assertRaisesRegex(RuntimeError, "already submitted"):
            guard.submit(None, self.payload, lambda _: self.fail("Must not resubmit"))

    def test_order_rejection_reserved(self):
        result = guard.submit(None, self.payload, lambda _: (400, {"error": "ambiguous"}))[1]
        self.assertFalse(result["accepted"])
        self.assertFalse(result["retryable"])
        self.assertEqual(json.loads(self.path.read_text())["attempts"]["stable-id"]["status"], "unknown")

    def test_final_account_change_rejects(self):
        self.data["account"]["equity"] = 99100
        self.payload["lots"] = 2.10
        with self.assertRaisesRegex(RuntimeError, "headroom"):
            guard.submit(None, self.payload, lambda _: self.fail("Must not submit"))

    def test_final_aggregate_risk_rejects(self):
        self.data["account"]["existingRisk"] = 300
        with self.assertRaisesRegex(RuntimeError, "aggregate"):
            guard.submit(None, self.payload, lambda _: self.fail("Must not submit"))

    def test_simultaneous_account_exposure_rejects(self):
        self.data["account"]["activeTrades"] = 1
        with self.assertRaisesRegex(RuntimeError, "active/daily"):
            guard.submit(None, self.payload, lambda _: self.fail("Must not submit"))

    def test_kill_switch_persists_and_cancels_only_owned(self):
        self.payload["policy"]["killSwitch"] = True
        self.data["orders"] = [{"ticket": 1, "magic": 77}, {"ticket": 2, "magic": 88}]
        calls = []
        def cancel(ticket):
            calls.append(ticket)
            self.data["orders"] = [o for o in self.data["orders"] if str(o["ticket"]) != ticket]
            return 200, {}
        result = guard.protect(None, self.payload, cancel)
        self.assertTrue(result["halted"])
        self.assertEqual(calls, ["1"])
        self.payload["policy"]["killSwitch"] = False
        with self.assertRaisesRegex(RuntimeError, "Persisted"):
            guard.submit(None, self.payload, lambda _: self.fail("Must not submit"))

    def test_daily_halt_survives_recovery_and_expires_at_reset(self):
        self.data["account"]["equity"] = 98999
        self.assertTrue(guard.protect(None, self.payload, lambda _: (200, {}))["halted"])
        self.data["account"]["equity"] = 100000
        self.assertTrue(guard.protect(None, self.payload, lambda _: (200, {}))["halted"])
        state = guard.load_state(self.path)
        state["dailyHaltUntil"] = 0
        guard.save_state(self.path, state)
        self.assertFalse(guard.protect(None, self.payload, lambda _: (200, {}))["halted"])

    def test_unknown_exposure_fails_closed(self):
        self.data["account"]["exposureKnown"] = False
        with self.assertRaisesRegex(RuntimeError, "Unknown"):
            guard.submit(None, self.payload, lambda _: self.fail("Must not submit"))

    def test_stale_daily_boundaries_rejected(self):
        self.payload["dayStartUtc"] = "2020-01-01T00:00:00Z"
        with self.assertRaisesRegex(RuntimeError, "boundaries"):
            guard.windows(self.payload)

    def test_corrupt_state_fails_closed(self):
        self.path.write_text("invalid json")
        with self.assertRaises(json.JSONDecodeError):
            guard.submit(None, self.payload, lambda _: self.fail("Must not submit"))

    def test_close_rejection_blocks_cycle(self):
        self.payload["policy"]["killSwitch"] = True
        self.data["positions"] = [{"ticket": 1, "magic": 77, "type": 0, "symbol": "EURUSD", "volume": 1, "time": 0}]
        mt5 = SimpleNamespace(symbol_info_tick=lambda _: SimpleNamespace(bid=1.1, ask=1.1001),
                              symbol_info=lambda _: SimpleNamespace(filling_mode=1),
                              order_send=lambda _: None, POSITION_TYPE_BUY=0, ORDER_FILLING_FOK=0,
                              ORDER_FILLING_IOC=1, TRADE_ACTION_DEAL=1, ORDER_TYPE_SELL=1, ORDER_TYPE_BUY=0)
        result = guard.protect(mt5, self.payload, lambda _: (200, {}))
        self.assertEqual(result["haltCode"], "ProtectionIncomplete")
        self.assertTrue(result["errors"])
        self.assertNotIn(123, guard.PROTECTION_HEALTH)

    def test_confirmed_non_execution_retry_is_bounded_and_persisted(self):
        self.payload["policy"]["maximumSubmissionAttempts"] = 2
        self.payload["policy"]["submissionRetryDelaySeconds"] = 0
        reject = lambda _: (400, {"error": "preflight unavailable", "definitelyNotSent": True, "retryable": True})
        self.assertTrue(guard.submit(None, self.payload, reject)[1]["retryable"])
        self.assertTrue(guard.submit(None, self.payload, reject)[1]["retryable"])
        with self.assertRaisesRegex(RuntimeError, "already submitted"):
            guard.submit(None, self.payload, lambda _: self.fail("Retry cap bypassed"))
        self.assertEqual(guard.load_state(self.path)["attempts"]["stable-id"]["attemptCount"], 2)

    def test_unsupported_loss_model_never_submits(self):
        self.payload["policy"]["lossModel"] = "Trailing"
        with self.assertRaisesRegex(RuntimeError, "Unsupported"):
            guard.submit(None, self.payload, lambda _: self.fail("Unsupported model traded"))

    def test_valid_json_with_invalid_journal_shape_fails_closed(self):
        self.path.write_text('{"attempts": [], "halted": false}')
        with self.assertRaisesRegex(RuntimeError, "journal"):
            guard.submit(None, self.payload, lambda _: self.fail("Invalid journal traded"))

    def test_status_reports_stale_and_unresolved_without_terminal_calls(self):
        guard.PROTECTION_HEALTH.clear()
        guard.save_state(self.path, {"halted": False, "attempts": {"x": {"status": "unknown"}}})
        status = guard.health(self.payload)
        self.assertFalse(status["entryEligible"])
        self.assertFalse(status["protectionFresh"])
        self.assertEqual(status["unresolvedSubmissions"], 1)

    def test_cancellation_failure_does_not_skip_newly_filled_positions(self):
        self.payload["policy"]["killSwitch"] = True
        self.data["orders"] = [{"ticket": 1, "magic": 77}]
        positions = [{"ticket": n, "identifier": n, "magic": 77, "type": 0,
                      "symbol": "EURUSD", "volume": 1, "time": 0} for n in (2, 3)]
        calls = []
        def send(request):
            calls.append(request["position"])
            return SimpleNamespace(retcode=999 if request["position"] == 2 else 10009)
        mt5 = SimpleNamespace(positions_get=lambda: [SimpleNamespace(_asdict=lambda p=p: p) for p in positions],
            symbol_info_tick=lambda _: SimpleNamespace(bid=1.1, ask=1.1001),
            symbol_info=lambda _: SimpleNamespace(filling_mode=1), order_send=send,
            POSITION_TYPE_BUY=0, ORDER_FILLING_FOK=0, ORDER_FILLING_IOC=1, TRADE_ACTION_DEAL=1,
            ORDER_TYPE_SELL=1, ORDER_TYPE_BUY=0, TRADE_RETCODE_DONE=10009)
        result = guard.protect(mt5, self.payload, lambda _: (400, {"error": "order filled"}))
        self.assertEqual(calls, [2, 3])
        self.assertEqual(result["haltCode"], "ProtectionIncomplete")
        self.assertIn("closed:3", result["actions"])
        self.assertEqual(len(result["errors"]), 2)

    def test_reconcile_closed_position_includes_entry_and_exit_costs(self):
        guard.submit(None, self.payload, lambda _: (200, {"brokerOrderId": "8"}))
        self.data["deals"] = [dict(magic=77, order=8, position_id=9, entry=0, time=1, profit=0, commission=-3),
                              dict(magic=77, order=10, position_id=9, entry=1, time=2, profit=100, commission=-3, swap=-2)]
        guard.protect(None, self.payload, lambda _: (200, {}))
        record = guard.load_state(self.path)["attempts"]["stable-id"]
        self.assertEqual(record["status"], "closed")
        self.assertEqual(record["netProfit"], 92)
        self.assertEqual(guard.health(self.payload)["performance"]["overall"]["closedPositions"], 1)

    def test_reconcile_filled_position_does_not_mark_partial_exit_closed(self):
        guard.submit(None, self.payload, lambda _: (200, {"brokerOrderId": "8"}))
        self.data["deals"] = [dict(magic=77, order=8, position_id=9, entry=0, time=1),
                              dict(magic=77, order=10, position_id=9, entry=1, time=2, profit=100)]
        self.data["positions"] = [dict(ticket=9, identifier=9, magic=77, time=datetime.now(timezone.utc).timestamp(), sl=1.099, tp=1.102)]
        guard.protect(None, self.payload, lambda _: (200, {}))
        self.assertEqual(guard.load_state(self.path)["attempts"]["stable-id"]["status"], "filled")

    def test_late_entry_cost_changes_refresh_closed_result(self):
        self.test_reconcile_closed_position_includes_entry_and_exit_costs()
        self.data["deals"][0]["commission"] = -5
        guard.protect(None, self.payload, lambda _: (200, {}))
        self.assertEqual(guard.load_state(self.path)["attempts"]["stable-id"]["netProfit"], 90)

    def test_corrupt_journal_preserved_while_owned_orders_are_cancelled(self):
        self.path.write_text("corrupt")
        self.data["orders"] = [dict(ticket=1, magic=77, time_setup=0)]
        cancelled = []
        result = guard.protect(None, self.payload, lambda ticket: (cancelled.append(ticket) or 200, {}))
        self.assertEqual(cancelled, ["1"])
        self.assertEqual(self.path.read_text(), "corrupt")
        self.assertEqual(result["haltCode"], "ProtectionIncomplete")
        self.assertNotIn(123, guard.PROTECTION_HEALTH)

    def test_missing_native_expiration_cancels_recognized_order(self):
        guard.submit(None, self.payload, lambda _: (200, {"brokerOrderId": "8"}))
        self.data["orders"] = [dict(ticket=8, magic=77, time_setup=datetime.now(timezone.utc).timestamp(), sl=1.099, tp=1.102)]
        cancelled = []
        guard.protect(None, self.payload, lambda ticket: (cancelled.append(ticket) or 200, {}))
        self.assertEqual(cancelled, ["8"])

    def test_successful_cancel_response_needs_broker_absence_confirmation(self):
        self.data["orders"] = [dict(ticket=1, magic=77, time_setup=0)]
        result = guard.protect(None, self.payload, lambda _: (200, {}))
        self.assertEqual(result["haltCode"], "ProtectionIncomplete")
        self.assertTrue(any("not confirmed" in error for error in result["errors"]))
        self.assertNotIn(123, guard.PROTECTION_HEALTH)

    def test_target_requires_flat_account_and_distinct_prague_days(self):
        self.data["account"].update(balance=105000, equity=105000)
        self.data["entryTimes"] = ["2026-03-29T12:00:00Z", "2026-03-29T13:00:00Z"]
        self.assertFalse(guard.protect(None, self.payload, lambda _: (200, {}))["targetReached"])
        self.data["entryTimes"].append("2026-03-30T12:00:00Z")
        self.assertTrue(guard.protect(None, self.payload, lambda _: (200, {}))["targetReached"])
        self.data["account"]["activeTrades"] = 1
        self.assertFalse(guard.protect(None, self.payload, lambda _: (200, {}))["targetReached"])


class TickCoverageTests(unittest.TestCase):
    def test_entry_costs_are_allocated_once_across_partial_exits(self):
        entries = [{"volume": .4, "commission": -1.4, "fee": -.2}, {"volume": .6, "commission": -2.1, "fee": -.3}]
        first = guard.allocated_entry_costs(entries, .25)
        second = guard.allocated_entry_costs(entries, .75)
        self.assertAlmostEqual(first[0] + second[0], -3.5)
        self.assertAlmostEqual(first[1] + second[1], -.5)

    def test_consumed_buy_and_sell_entries_rejected(self):
        now = datetime.now(timezone.utc).timestamp()
        for direction, entry, bid, ask in [(0, 1.1, 1.0998, 1.0999), (1, 1.1, 1.1001, 1.1002)]:
            signal = {"direction": direction, "entryPrice": entry, "smcContext": {"fvgConfirmedAt": datetime.fromtimestamp(now - 1, timezone.utc).isoformat()}}
            payload = {"symbol": "EURUSD", "signal": signal, "policy": {"maximumQuoteAgeSeconds": 30}}
            mt5 = SimpleNamespace(COPY_TICKS_INFO=1, copy_ticks_range=lambda *_: [{"time": now, "bid": bid, "ask": ask}])
            with self.assertRaisesRegex(RuntimeError, "consumed"):
                guard.verify_unconsumed_entry(mt5, payload, now)

    def test_missing_ticks_rejected_and_untouched_gap_accepted(self):
        now = datetime.now(timezone.utc).timestamp()
        payload = {"symbol": "EURUSD", "signal": {"direction": 0, "entryPrice": 1.1,
                   "smcContext": {"fvgConfirmedAt": datetime.fromtimestamp(now - 1, timezone.utc).isoformat()}},
                   "policy": {"maximumQuoteAgeSeconds": 30}}
        mt5 = SimpleNamespace(COPY_TICKS_INFO=1, copy_ticks_range=lambda *_: [])
        with self.assertRaisesRegex(RuntimeError, "coverage unavailable"):
            guard.verify_unconsumed_entry(mt5, payload, now)
        mt5.copy_ticks_range = lambda *_: [{"time": now, "bid": 1.101, "ask": 1.1011}]
        guard.verify_unconsumed_entry(mt5, payload, now)


class SnapshotTests(unittest.TestCase):
    def setUp(self):
        profile = json.loads((Path(__file__).resolve().parents[2] / "TradingBot.CLI/appsettings.ftmo.example.json").read_text())
        now = datetime.now(timezone.utc)
        start = now.replace(hour=0, minute=0, second=0, microsecond=0)
        self.payload = {"policy": camel(profile["FtmoProtection"]), "dayStartUtc": start.isoformat(),
                        "nextDayStartUtc": (start + timedelta(days=1)).isoformat(), "symbol": "EURUSD", "accountId": 123}
        self.entry = {"type": 0, "entry": 0, "position_id": 11, "time": start.timestamp() + 1,
                      "profit": 0, "commission": -3.5, "swap": 0, "fee": 0}
        self.deals = [self.entry]
        self.positions = []
        def row(value):
            return SimpleNamespace(_asdict=lambda: value.copy())
        self.mt5 = SimpleNamespace(
            account_info=lambda: SimpleNamespace(balance=100000, equity=99900, login=123, currency="USD"),
            positions_get=lambda: [row(p) for p in self.positions], orders_get=lambda: [],
            history_deals_get=lambda *_: [row(d) for d in self.deals],
            symbol_info=lambda _: SimpleNamespace(volume_min=.01, volume_max=100, volume_step=.01,
                                                  trade_tick_value_loss=1, trade_tick_size=.00001),
            symbol_info_tick=lambda _: SimpleNamespace(time=now.timestamp(), bid=1.101, ask=1.1011),
            order_calc_profit=lambda side, symbol, lots, entry, stop: (stop - entry) * lots * 100000 * (1 if side == 0 else -1),
            DEAL_TYPE_BUY=0, DEAL_TYPE_SELL=1, DEAL_ENTRY_IN=0, DEAL_ENTRY_OUT=1,
            DEAL_ENTRY_INOUT=2, DEAL_ENTRY_OUT_BY=3, POSITION_TYPE_BUY=0,
            ORDER_TYPE_BUY=0, ORDER_TYPE_SELL=1, ORDER_TYPE_BUY_LIMIT=2,
            ORDER_TYPE_BUY_STOP=4, ORDER_TYPE_BUY_STOP_LIMIT=6)

    def test_daily_balance_includes_entry_commissions(self):
        data = guard.snapshot(self.mt5, self.payload)
        self.assertEqual(data["account"]["dailyStartingBalance"], 100003.5)
        self.assertEqual(data["account"]["tradesToday"], 1)
        self.assertEqual(len(data["entryTimes"]), 1)

    def test_partial_fills_count_one_trade(self):
        self.deals.append(dict(self.entry))
        self.assertEqual(guard.snapshot(self.mt5, self.payload)["account"]["tradesToday"], 1)

    def test_stop_exposure_uses_current_price_and_cost_reserve(self):
        self.positions = [{"identifier": 11, "type": 0, "symbol": "EURUSD", "volume": 1,
                           "price_current": 1.101, "price_open": 1.1, "sl": 1.099}]
        data = guard.snapshot(self.mt5, self.payload)
        self.assertAlmostEqual(data["account"]["existingRisk"], 217)
        self.assertTrue(data["account"]["exposureKnown"])

    def test_missing_stop_is_unknown_exposure(self):
        self.positions = [{"identifier": 11, "sl": 0, "symbol": "EURUSD"}]
        self.assertFalse(guard.snapshot(self.mt5, self.payload)["account"]["exposureKnown"])

    def test_missing_history_fails_closed(self):
        self.mt5.history_deals_get = lambda *_: None
        with self.assertRaisesRegex(RuntimeError, "deal history"):
            guard.snapshot(self.mt5, self.payload)

    def test_wrong_account_fails_closed(self):
        self.payload["accountId"] = 456
        with self.assertRaisesRegex(RuntimeError, "configured account"):
            guard.snapshot(self.mt5, self.payload)

    def test_stale_quote_blocks_orders_but_allows_protection(self):
        self.mt5.symbol_info_tick = lambda _: SimpleNamespace(time=0, bid=1.1, ask=1.1001)
        guard.snapshot(self.mt5, self.payload)
        self.payload["signal"] = {"direction": 0, "entryPrice": 1.1, "stopLoss": 1.099}
        with self.assertRaisesRegex(RuntimeError, "stale"):
            guard.snapshot(self.mt5, self.payload)

    def test_nonfinite_broker_metadata_blocks_submission_snapshot(self):
        self.payload["signal"] = {"direction": 0, "entryPrice": 1.1, "stopLoss": 1.099}
        self.mt5.symbol_info = lambda _: SimpleNamespace(volume_min=.01, volume_max=100, volume_step=.01,
            trade_tick_value_loss=float("nan"), trade_tick_size=.00001)
        with self.assertRaisesRegex(RuntimeError, "sizing metadata"):
            guard.snapshot(self.mt5, self.payload)


if __name__ == "__main__":
    unittest.main()
