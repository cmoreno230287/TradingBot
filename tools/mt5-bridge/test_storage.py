import tempfile
import threading
import unittest
from pathlib import Path
from unittest.mock import patch

import live_storage
from live_storage import BoundedStorage
import test_recovery as fixtures
import ftmo_guard as guard


class StorageTests(unittest.TestCase):
    def test_late_write_blocks_new_io_and_cannot_overwrite_newer_state(self):
        storage = BoundedStorage(.02)
        release = threading.Event()
        finished = threading.Event()
        writes = []
        def save(path, state):
            release.wait(2)
            writes.append(state)
            finished.set()
        state = {"attempts": {"a": {"status": "unknown"}}, "halted": False}
        with patch.object(live_storage, "save_state", side_effect=save):
            try:
                with self.assertRaises(TimeoutError): storage.save(Path("unused"), state)
                state["attempts"]["a"]["status"] = "accepted"
                with self.assertRaisesRegex(RuntimeError, "in flight"): storage.save(Path("unused"), state)
                release.set()
                self.assertTrue(finished.wait(1))
                # Acquire the worker's gate before issuing the next write; no timing assumptions.
                self.assertTrue(storage.gate.acquire(timeout=1))
                storage.gate.release()
                storage.save(Path("unused"), state)
                self.assertEqual([s["attempts"]["a"]["status"] for s in writes], ["unknown", "accepted"])
            finally: release.set()

    def test_kill_actions_precede_all_journal_access_even_on_directory_failure(self):
        fixture = fixtures.EmergencyHistoryTests()
        fixture.setUp()
        try:
            fixture.payload["policy"]["killSwitch"] = True
            def unavailable(*_):
                self.assertEqual([o["ticket"] for o in fixture.orders], [43])
                raise PermissionError("directory denied")
            with patch.object(guard, "state_path", side_effect=unavailable):
                result = guard.protect(fixture.mt5, fixture.payload, fixture.cancel)
            self.assertIn("cancelled:42", result["actions"])
            self.assertTrue(result["errors"])
            self.assertNotIn(123, guard.PROTECTION_HEALTH)
        finally: fixture.doCleanups()

    def test_storage_errors_do_not_stop_owned_protection(self):
        for error in (PermissionError("denied"), OSError(28, "disk full"), RuntimeError("corrupt journal")):
            with self.subTest(error=error):
                fixture = fixtures.EmergencyHistoryTests()
                fixture.setUp()
                try:
                    with patch.object(live_storage, "load_state", side_effect=error):
                        result = guard.protect(fixture.mt5, fixture.payload, fixture.cancel)
                    self.assertIn("cancelled:42", result["actions"])
                    self.assertTrue(result["halted"])
                finally: fixture.doCleanups()

    def test_stalled_journal_allows_repeated_protection_without_new_workers(self):
        fixture = fixtures.EmergencyHistoryTests()
        fixture.setUp()
        storage = BoundedStorage(.02)
        release = threading.Event()
        started = []
        def hung(path):
            started.append(True)
            release.wait(2)
            return {"attempts": {}, "halted": False}
        try:
            with patch.object(live_storage, "load_state", side_effect=hung), patch.object(guard, "load_state", storage.load):
                result = guard.protect(fixture.mt5, fixture.payload, fixture.cancel)
                self.assertIn("cancelled:42", result["actions"])
                guard.protect(fixture.mt5, fixture.payload, fixture.cancel)
                self.assertEqual(len(started), 1)
                self.assertNotIn(123, guard.PROTECTION_HEALTH)
        finally:
            release.set()
            self.assertTrue(storage.gate.acquire(timeout=1))
            storage.gate.release()
            fixture.doCleanups()

    def test_emergency_does_not_replace_corrupted_journal(self):
        fixture = fixtures.EmergencyHistoryTests()
        fixture.setUp()
        try:
            fixture.payload["policy"]["killSwitch"] = True
            path = Path(fixture.temp.name) / "state.json"
            path.write_text("broken")
            result = guard.protect(fixture.mt5, fixture.payload, fixture.cancel)
            self.assertIn("cancelled:42", result["actions"])
            self.assertEqual(path.read_text(), "broken")
        finally: fixture.doCleanups()

    def test_partial_close_continues_with_failed_journal(self):
        fixture = fixtures.EmergencyHistoryTests()
        fixture.setUp()
        try:
            with patch.object(live_storage, "load_state", side_effect=PermissionError("storage denied")):
                fixture.test_partial_close_during_outage_retries_remaining_volume()
        finally: fixture.doCleanups()
