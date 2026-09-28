import threading
import unittest
import time
from types import SimpleNamespace
from unittest.mock import patch

from live_gate import ProtectionGate
from live_metrics import performance
from supervise_bridge import unhealthy
import mt5_bridge as bridge


class OperationsTests(unittest.TestCase):
    def test_queued_protection_precedes_new_submission(self):
        gate = ProtectionGate()
        order = []
        def run(protection):
            with gate.acquire(protection, timeout=2):
                order.append("protection" if protection else "entry")
        with gate.acquire():
            entry = threading.Thread(target=run, args=(False,))
            protector = threading.Thread(target=run, args=(True,))
            entry.start()
            protector.start()
            with gate.condition:
                self.assertTrue(gate.condition.wait_for(lambda: gate.protectors == 1, timeout=1))
        entry.join(timeout=3)
        protector.join(timeout=3)
        self.assertEqual(order, ["protection", "entry"])
        self.assertFalse(entry.is_alive() or protector.is_alive())

    def test_busy_gate_times_out_without_queuing_a_late_order(self):
        gate = ProtectionGate()
        with gate.acquire(protection=True):
            with self.assertRaises(TimeoutError):
                with gate.acquire(timeout=.01):
                    self.fail("Busy gate admitted entry")
            self.assertEqual(gate.health()["operation"], "protection")
        self.assertIsNone(gate.health()["operation"])

    def test_performance_excludes_partial_positions_and_counts_costs(self):
        state = {"attempts": {str(i): {"status": status, "closedAt": i, "netProfit": pnl,
                 "monetaryRisk": 100} for i, (status, pnl) in enumerate(
                     [("closed", 190), ("closed", -110), ("closed", -110), ("filled", 1000)])}}
        result = performance(state)["overall"]
        self.assertEqual(result["closedPositions"], 3)
        self.assertEqual(result["netProfit"], -30)
        self.assertEqual(result["expectancy"], -10)
        self.assertEqual(result["realizedDrawdown"], 220)
        self.assertEqual(result["maximumLosingStreak"], 2)
        self.assertAlmostEqual(result["winRate"], 1 / 3)
        self.assertAlmostEqual(result["averageRealizedR"], -.1)

    def test_watchdog_detects_stall_and_wrong_process(self):
        response = {"processId": 123, "gate": {"operationAgeSeconds": 46}}
        self.assertTrue(unhealthy(response, 123, 45))
        response["gate"]["operationAgeSeconds"] = 0
        self.assertFalse(unhealthy(response, 123, 45))
        self.assertTrue(unhealthy(response, 456, 45))
        response["maximumRequestAgeSeconds"] = 46
        self.assertTrue(unhealthy(response, 123, 45))

    def test_request_health_includes_calls_outside_protection_gate(self):
        with patch.object(bridge, "ACTIVE_REQUESTS", {1: time.monotonic() - 60}):
            state = bridge.request_health()
        self.assertEqual(state["activeRequests"], 1)
        self.assertGreaterEqual(state["maximumRequestAgeSeconds"], 60)


class FinalSendTests(unittest.TestCase):
    def setUp(self):
        self.now = time.time()
        self.sent = []
        self.tick = SimpleNamespace(bid=1.101, ask=1.1011, time=self.now)
        self.mt5 = SimpleNamespace(TRADE_ACTION_PENDING=5, ORDER_TYPE_BUY_LIMIT=2, ORDER_TYPE_SELL_LIMIT=3,
            ORDER_TIME_GTC=0, ORDER_TIME_SPECIFIED=2, ORDER_FILLING_RETURN=2,
            TRADE_RETCODE_DONE=10009, TRADE_RETCODE_PLACED=10008,
            symbol_info_tick=lambda _: self.tick,
            order_check=lambda _: SimpleNamespace(retcode=0),
            order_send=lambda request: self.sent.append(request) or SimpleNamespace(
                _asdict=lambda: {"retcode": 10009, "order": 8}))
        for context in (patch.object(bridge, "mt5", self.mt5), patch.object(bridge, "ensure_mt5"),
                        patch.object(bridge, "select_symbol", return_value=SimpleNamespace(volume_min=.01, volume_max=100, volume_step=.01)),
                        patch.object(bridge, "account_info", return_value={"login": 123})):
            context.start()
            self.addCleanup(context.stop)
        self.payload = {"symbol": "EURUSD", "side": "BUY", "price": 1.1, "lots": 1,
                        "stopLoss": 1.099, "takeProfit": 1.102, "requireExpiration": True,
                        "expirationMinutes": 15, "sendBeforeTimestamp": self.now + 20,
                        "maximumQuoteAgeSeconds": 30, "maxSpreadPips": 1.5}

    def test_expired_preflight_never_sends(self):
        self.payload["sendBeforeTimestamp"] = self.now - 1
        status, result = bridge.MT5BridgeHandler.create_order(None, self.payload)
        self.assertEqual(status, 400)
        self.assertTrue(result["definitelyNotSent"])
        self.assertEqual(self.sent, [])

    def test_crossed_quote_after_preflight_never_sends(self):
        def check(_):
            self.tick = SimpleNamespace(bid=1.0998, ask=1.0999, time=self.now)
            return SimpleNamespace(retcode=0)
        self.mt5.order_check = check
        self.assertEqual(bridge.MT5BridgeHandler.create_order(None, self.payload)[0], 400)
        self.assertEqual(self.sent, [])

    def test_nonfinite_final_quote_never_sends(self):
        def check(_):
            self.tick = SimpleNamespace(bid=float("nan"), ask=1.1011, time=self.now)
            return SimpleNamespace(retcode=0)
        self.mt5.order_check = check
        self.assertEqual(bridge.MT5BridgeHandler.create_order(None, self.payload)[0], 400)
        self.assertEqual(self.sent, [])

    def test_accepted_request_retains_sl_tp_expiration_and_limit_type(self):
        status, result = bridge.MT5BridgeHandler.create_order(None, self.payload)
        self.assertEqual(status, 200)
        self.assertEqual(len(self.sent), 1)
        request = self.sent[0]
        self.assertEqual((request["sl"], request["tp"], request["type"]), (1.099, 1.102, self.mt5.ORDER_TYPE_BUY_LIMIT))
        self.assertGreater(request["expiration"], self.now)
        self.assertTrue(result["expirationApplied"])


if __name__ == "__main__":
    unittest.main()
