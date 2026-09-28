"""Opt-in process watchdog. Run explicitly; importing this file never starts MT5."""
import argparse
import json
import subprocess
import sys
import socket
import time
from pathlib import Path
from urllib.request import urlopen


def unhealthy(response, process_id, maximum_operation_seconds):
    return response.get("processId") != process_id or response["gate"]["operationAgeSeconds"] > maximum_operation_seconds \
        or response.get("maximumRequestAgeSeconds", 0) > maximum_operation_seconds


def stop(child):
    if child.poll() is None:
        child.terminate()
        try:
            child.wait(timeout=10)
        except subprocess.TimeoutExpired:
            child.kill()
            child.wait(timeout=10)


def main():
    parser = argparse.ArgumentParser(description="Supervise the local bridge; durable unknown orders are never replayed.")
    parser.add_argument("--port", type=int, default=5010)
    parser.add_argument("--terminal-path", default="")
    parser.add_argument("--maximum-operation-seconds", type=int, default=45)
    args = parser.parse_args()
    if args.maximum_operation_seconds < 20 or not 1 <= args.port <= 65535:
        parser.error("Use a valid port and an operation timeout of at least 20 seconds.")
    command = [sys.executable, "-B", str(Path(__file__).with_name("mt5_bridge.py")), "--host", "127.0.0.1",
               "--port", str(args.port), "--terminal-path", args.terminal_path]
    child = None
    backoff = 2
    try:
        while True:
            # Never compete with a bridge that this supervisor did not start.
            with socket.socket() as probe:
                probe.bind(("127.0.0.1", args.port))
            child = subprocess.Popen(command)
            started = time.monotonic()
            failures = 0
            while child.poll() is None:
                time.sleep(5)
                try:
                    with urlopen(f"http://127.0.0.1:{args.port}/live-health", timeout=3) as response:
                        state = json.load(response)
                    if state.get("processId") != child.pid:
                        raise RuntimeError("Port belongs to another bridge process; supervision stopped.")
                    failed = unhealthy(state, child.pid, args.maximum_operation_seconds)
                except (OSError, ValueError):
                    failed = True
                failures = failures + 1 if failed else 0
                if failures >= 3 and time.monotonic() - started > 75:
                    print("Bridge watchdog: stalled worker; restart with durable reconciliation.", flush=True)
                    stop(child)
                    break
            if child.returncode == 2:
                raise RuntimeError("Bridge configuration/dependencies rejected; automatic restart stopped.")
            if time.monotonic() - started > 300:
                backoff = 2
            time.sleep(backoff)
            backoff = min(60, backoff * 2)
    except KeyboardInterrupt:
        pass
    finally:
        if child is not None:
            stop(child)


if __name__ == "__main__":
    main()
