"""Failure recovery against fake broker data; no terminal connection or live orders."""
import tempfile
import time
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

import ftmo_guard as guard
from live_reconciliation import reconcile
import test_ftmo_guard as fixtures


class HistoryRecoveryTests(unittest.TestCase):
    def setUp(self):
        self.attempt = dict(status="unknown", timestamp=time.time() - 60, clientOrderId="lost",
                            accountId=123, magicNumber=77, symbol="EURUSD")
        self.state = {"attempts": {"setup": self.attempt}, "halted": False}
        self.data = dict(accountId=123, orders=[], positions=[], deals=[])
        self.order = dict(ticket=42, magic=77, symbol="EURUSD", comment="FTMOlost",
                          time_setup=time.time() - 50, state=6, position_id=0)
        self.rows = [self.order]
        self.mt5 = SimpleNamespace(history_orders_get=lambda *a, **kw:
                                   [SimpleNamespace(**o) for o in self.rows])

    def test_terminal_ticket_recovery_survives_restart(self):
        for terminal in (2, 5, 6):
            with self.subTest(terminal=terminal), tempfile.TemporaryDirectory() as root:
                self.attempt.update(status="unknown")
                self.attempt.pop("brokerOrderId", None)
                self.order["state"] = terminal
                path = Path(root) / "state.json"
                guard.save_state(path, self.state)
                loaded = guard.load_state(path)
                reconcile(loaded, self.data, 77, self.mt5)
                guard.save_state(path, loaded)
                result = guard.load_state(path)["attempts"]["setup"]
                self.assertEqual(result["brokerOrderId"], "42")
                self.assertEqual(result["status"], "rejected" if terminal == 5 else "cancelled")
                self.assertFalse(result["retryable"])

    def test_ambiguous_history_stays_unknown(self):
        self.rows.append(dict(self.order, ticket=43))
        reconcile(self.state, self.data, 77, self.mt5)
        self.assertEqual(self.attempt["status"], "unknown")
        self.assertNotIn("brokerOrderId", self.attempt)

    def test_wrong_identity_or_time_is_not_recovered(self):
        for change in (dict(magic=99), dict(symbol="GBPUSD"), dict(comment="other"), dict(time_setup=1)):
            with self.subTest(change=change):
                self.rows = [dict(self.order, **change)]
                reconcile(self.state, self.data, 77, self.mt5)
                self.assertEqual(self.attempt["status"], "unknown")
                self.assertNotIn("brokerOrderId", self.attempt)
        self.attempt["accountId"] = 456
        self.rows = [self.order]
        reconcile(self.state, self.data, 77, self.mt5)
        self.assertNotIn("brokerOrderId", self.attempt)

    def test_fill_evidence_does_not_require_order_history(self):
        self.mt5.history_orders_get = lambda *a, **kw: self.fail("Fill already identifies the order")
        self.data["deals"] = [dict(magic=77, symbol="EURUSD", comment="FTMOlost", order=42, position_id=7, entry=0)]
        self.data["positions"] = [dict(identifier=7)]
        reconcile(self.state, self.data, 77, self.mt5)
        self.assertEqual(self.attempt["status"], "filled")
        self.assertEqual(self.attempt["brokerOrderId"], "42")

    def test_cancelled_partial_fill_without_position_history_stays_unknown(self):
        self.order.update(position_id=7, state=2)
        reconcile(self.state, self.data, 77, self.mt5)
        self.assertEqual(self.attempt["status"], "unknown")

    def test_unavailable_order_history_stays_unknown(self):
        self.mt5.history_orders_get = lambda *a, **kw: None
        reconcile(self.state, self.data, 77, self.mt5)
        self.assertEqual(self.attempt["status"], "unknown")


class EmergencyHistoryTests(unittest.TestCase):
    def setUp(self):
        fixture = fixtures.SnapshotTests()
        fixture.setUp()
        self.mt5, self.payload = fixture.mt5, fixture.payload
        self.payload["magicNumber"] = 77
        self.orders = [dict(ticket=42, magic=77), dict(ticket=43, magic=99)]
        self.mt5.orders_get = lambda: [SimpleNamespace(_asdict=lambda o=o: o.copy()) for o in self.orders]
        self.mt5.history_deals_get = lambda *_: None
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        context = patch.object(guard, "state_path", return_value=Path(self.temp.name) / "state.json")
        context.start()
        self.addCleanup(context.stop)
        self.addCleanup(guard.PROTECTION_HEALTH.clear)
        self.addCleanup(guard.PROTECTION_RESULTS.clear)

    def cancel(self, ticket):
        self.orders[:] = [o for o in self.orders if str(o["ticket"]) != ticket]
        return 200, {}

    def test_kill_switch_skips_history_and_cancels_only_owned_orders(self):
        self.payload["policy"]["killSwitch"] = True
        self.payload["dayStartUtc"] = "invalid"
        self.mt5.history_deals_get = lambda *_: self.fail("Kill switch must skip history")
        result = guard.protect(self.mt5, self.payload, self.cancel)
        self.assertIn("cancelled:42", result["actions"])
        self.assertEqual([o["ticket"] for o in self.orders], [43])
        self.assertFalse(result["accountingAvailable"])
        self.assertNotIn(123, guard.PROTECTION_HEALTH)

    def test_history_outage_cancels_then_recovers_accounting(self):
        result = guard.protect(self.mt5, self.payload, self.cancel)
        self.assertTrue(result["halted"])
        self.assertFalse(result["accountingAvailable"])
        self.assertIn("cancelled:42", result["actions"])
        self.orders.clear()
        self.mt5.history_deals_get = lambda *_: []
        result = guard.protect(self.mt5, self.payload, self.cancel)
        self.assertTrue(result["accountingAvailable"])
        self.assertFalse(result["halted"])
        self.assertIn(123, guard.PROTECTION_HEALTH)

    def test_wrong_account_never_cancels(self):
        self.payload["accountId"] = 456
        with self.assertRaises(RuntimeError):
            guard.protect(self.mt5, self.payload, lambda _: self.fail("Wrong account"))

    def test_partial_close_during_outage_retries_remaining_volume(self):
        positions = [dict(ticket=7, identifier=7, magic=77, symbol="EURUSD", type=0, volume=1, sl=1, tp=2)]
        volumes = []
        self.mt5.positions_get = lambda: [SimpleNamespace(_asdict=lambda p=p: p.copy()) for p in positions]
        self.mt5.symbol_info = lambda _: SimpleNamespace(filling_mode=1)
        self.mt5.ORDER_FILLING_FOK = 0
        self.mt5.ORDER_FILLING_IOC = 1
        self.mt5.TRADE_ACTION_DEAL = 1
        self.mt5.TRADE_RETCODE_DONE = 10009
        def send(request):
            volumes.append(request["volume"])
            if len(volumes) == 1:
                positions[0]["volume"] = .4
                return SimpleNamespace(retcode=10010)
            positions.clear()
            return SimpleNamespace(retcode=10009)
        self.mt5.order_send = send
        first = guard.protect(self.mt5, self.payload, self.cancel)
        self.assertTrue(first["errors"])
        second = guard.protect(self.mt5, self.payload, self.cancel)
        self.assertEqual(volumes, [1, .4])
        self.assertIn("closed:7", second["actions"])
        self.assertFalse(second["accountingAvailable"])
