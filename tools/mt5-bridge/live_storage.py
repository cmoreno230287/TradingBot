"""Single-flight, bounded journal I/O. A timed-out operation retains its lease."""
import threading
import sys
import time
from copy import deepcopy
from live_state import load_state, save_state


class BoundedStorage:
    def __init__(self, timeout=2):
        self.timeout = timeout
        self.gate = threading.Lock()
        self.fallback_gate = threading.Lock()

    def call(self, action):
        deadline = time.monotonic() + self.timeout
        # Brief concurrent status reads are normal; only fail after the bounded budget.
        if not self.gate.acquire(timeout=self.timeout):
            raise RuntimeError("Journal I/O is still in flight; entries blocked.")
        done = threading.Event()
        result = []
        def work():
            try:
                result.append((True, action()))
            except BaseException as error:
                result.append((False, error))
            finally:
                self.gate.release()
                done.set()
        try:
            threading.Thread(target=work, daemon=True).start()
        except BaseException:
            self.gate.release()
            raise
        if not done.wait(max(0, deadline - time.monotonic())):
            self.report("Journal I/O timed out; entries blocked, protection continues.")
            raise TimeoutError("Journal I/O timed out; entries blocked.")
        success, value = result[0]
        if not success:
            self.report("Journal I/O failed; entries blocked, protection continues.")
            raise value
        return value

    def load(self, path):
        return self.call(lambda: load_state(path))

    def save(self, path, state):
        # A late write owns an immutable snapshot and completes before another I/O starts.
        snapshot = deepcopy(state)
        return self.call(lambda: save_state(path, snapshot))

    def report(self, message):
        if not self.fallback_gate.acquire(blocking=False):
            return
        def write():
            try:
                print(message, file=sys.stderr)
            except Exception:
                pass
            finally:
                self.fallback_gate.release()
        threading.Thread(target=write, daemon=True).start()


STORAGE = BoundedStorage()
