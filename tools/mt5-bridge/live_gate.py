"""Serialize guarded mutations, prefer protection, and bound time waiting for the bridge.

Native MT5 calls cannot safely be interrupted by a Python thread. A held gate remains
held until that call returns; health expires and queued entries time out meanwhile.
"""
import threading
import time
from contextlib import contextmanager


class GateBusyError(TimeoutError):
    pass


class ProtectionGate:
    def __init__(self):
        self.condition = threading.Condition()
        self.busy = False
        self.protectors = 0
        self.operation = None
        self.started = None

    @contextmanager
    def acquire(self, protection=False, timeout=2):
        deadline = time.monotonic() + timeout
        with self.condition:
            if protection:
                self.protectors += 1
                self.condition.notify_all()
            try:
                while self.busy or (not protection and self.protectors):
                    remaining = deadline - time.monotonic()
                    if remaining <= 0:
                        raise GateBusyError("Bridge operation busy; protection takes priority.")
                    self.condition.wait(remaining)
                self.busy = True
                self.operation = "protection" if protection else "entry"
                self.started = time.monotonic()
            finally:
                if protection:
                    self.protectors -= 1
                self.condition.notify_all()
        try:
            yield
        finally:
            with self.condition:
                self.busy = False
                self.operation = self.started = None
                self.condition.notify_all()

    def health(self):
        with self.condition:
            return {"operation": self.operation,
                    "operationAgeSeconds": time.monotonic() - self.started if self.started is not None else 0,
                    "waitingProtection": self.protectors}
