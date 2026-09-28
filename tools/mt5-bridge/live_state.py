"""Atomic execution journal. Invalid existing state must never become an empty journal."""
import json
import os
from pathlib import Path


def state_path(payload, account_id):
    root = Path("data/ftmo-bridge-state")
    return root / f"{int(account_id)}.json"


def load_state(path):
    state = json.loads(path.read_text(encoding="utf-8")) if path.exists() else {"attempts": {}, "halted": False}
    if not isinstance(state, dict) or not isinstance(state.get("attempts"), dict) or not isinstance(state.get("halted"), bool):
        raise RuntimeError("Execution journal is invalid; manual recovery is required.")
    allowed = {"reserved", "unknown", "accepted", "reconciled", "filled", "closed", "cancelled", "rejected"}
    if any(not isinstance(a, dict) or a.get("status") not in allowed for a in state["attempts"].values()):
        raise RuntimeError("Execution journal contains an invalid lifecycle state.")
    return state


def save_state(path, state):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(".tmp")
    with temporary.open("w", encoding="utf-8") as output:
        json.dump(state, output, allow_nan=False)
        output.flush()
        os.fsync(output.fileno())
    temporary.replace(path)
