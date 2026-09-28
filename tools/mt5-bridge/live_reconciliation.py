"""Reconcile durable intents using broker tickets, comments and position identifiers."""
import math
from datetime import datetime, timezone, timedelta


def historical_identity(attempt, data, magic, mt5):
    """Recover only a unique broker ticket, never infer non-execution from absence."""
    if mt5 is None or not data.get("accountId") or attempt.get("accountId", data["accountId"]) != data["accountId"]:
        return None
    symbol = attempt.get("symbol") or (attempt.get("signal") or {}).get("symbol")
    submitted = attempt.get("timestamp")
    client_id = attempt.get("clientOrderId")
    if not symbol or not client_id or not isinstance(submitted, (int, float)) or not math.isfinite(submitted):
        return None
    if attempt.get("magicNumber", magic) != magic:
        return None
    now = datetime.now(timezone.utc)
    start = datetime.fromtimestamp(submitted, timezone.utc) - timedelta(seconds=5)
    if start > now:
        return None
    rows = mt5.history_orders_get(start, now)
    if rows is None:
        return None
    candidates = {}
    for row in rows:
        order = row._asdict() if hasattr(row, "_asdict") else vars(row)
        if order.get("magic") == magic and order.get("symbol") == symbol and order.get("comment") == "FTMO" + client_id \
                and start.timestamp() <= float(order.get("time_setup", 0)) <= now.timestamp() + 5:
            candidates[str(order["ticket"])] = order
    if len(candidates) != 1:
        return None
    return next(iter(candidates.values()))


def reconcile(state, data, magic, mt5):
    for attempt in state["attempts"].values():
        if attempt.get("status") in ("cancelled", "rejected"):
            continue
        comment = "FTMO" + attempt.get("clientOrderId", "")
        ticket = str(attempt.get("brokerOrderId", ""))
        recovered = None
        if attempt.get("accountId", data.get("accountId")) != data.get("accountId") or attempt.get("magicNumber", magic) != magic:
            attempt["status"] = "unknown"
            continue
        symbol = attempt.get("symbol") or (attempt.get("signal") or {}).get("symbol")
        orders = [o for o in data["orders"] if o.get("magic") == magic and
                  (str(o["ticket"]) == ticket if ticket else o.get("comment") == comment and o.get("symbol") == symbol)]
        if len(orders) > 1:
            attempt["status"] = "unknown"
            continue
        order = orders[0] if orders else None
        matched_deals = [d for d in data.get("deals", []) if d.get("magic") == magic and
                         d.get("comment") == comment and d.get("symbol") == symbol]
        deal_tickets = {str(d["order"]) for d in matched_deals if d.get("order")}
        if not ticket and len(deal_tickets) > 1:
            attempt["status"] = "unknown"
            continue
        if not ticket and len(deal_tickets) == 1:
            ticket = next(iter(deal_tickets))
            attempt["brokerOrderId"] = ticket
        if not order and not ticket:
            recovered = historical_identity(attempt, data, magic, mt5)
            if recovered:
                ticket = str(recovered["ticket"])
                attempt["brokerOrderId"] = ticket
                if recovered.get("position_id"):
                    attempt["positionId"] = recovered["position_id"]
        deals = [d for d in data.get("deals", []) if d.get("magic") == magic and
                 ((ticket and str(d.get("order")) == ticket) or
                  not ticket and d.get("comment") == comment and d.get("symbol") == symbol)]
        if order:
            attempt.update(status="reconciled", brokerOrderId=str(order["ticket"]))
            continue
        position_ids = {d.get("position_id") for d in deals}
        if attempt.get("positionId"):
            position_ids.add(attempt["positionId"])
        if position_ids:
            position = next((p for p in data["positions"] if p.get("identifier") in position_ids), None)
            related = [d for d in data.get("deals", []) if d.get("position_id") in position_ids]
            if position:
                attempt.update(status="filled", positionId=position["identifier"])
                continue
            exits = [d for d in related if d.get("entry") in (1, 2, 3)]
            if exits:
                attempt.update(status="closed", closedAt=max(d["time"] for d in exits),
                               netProfit=sum(sum(float(d.get(k) or 0) for k in ("profit", "commission", "swap", "fee")) for d in related))
                continue
        # Absence from active orders does not prove cancellation; require historical evidence.
        if ticket and mt5 is not None:
            history = [recovered] if recovered else mt5.history_orders_get(ticket=int(ticket))
            rows = [] if history is None else [o if isinstance(o, dict) else o._asdict() if hasattr(o, "_asdict") else vars(o) for o in history]
            terminal = next((o for o in rows if str(o.get("ticket")) == ticket and o.get("magic") == magic
                             and o.get("symbol") == symbol and o.get("state") in (2, 5, 6)), None)
            if terminal and not position_ids and not terminal.get("position_id"):
                attempt["status"] = "rejected" if terminal["state"] == 5 else "cancelled"
                attempt["brokerTerminalState"] = terminal["state"]
                attempt["retryable"] = False
                continue
        attempt["status"] = "unknown"
