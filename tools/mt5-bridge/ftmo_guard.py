"""Opt-in FTMO execution guard. No terminal calls occur at import time."""
from __future__ import annotations

import hashlib
import math
from decimal import Decimal
from datetime import datetime, timezone
from zoneinfo import ZoneInfo
from live_state import state_path
from live_storage import STORAGE
from live_gate import ProtectionGate
from live_reconciliation import reconcile
from live_metrics import performance

LOCK = ProtectionGate()
PROTECTION_HEALTH = {}  # Process-local; a bridge restart requires a fresh protection cycle.
PROTECTION_RESULTS = {}

# All journal access, including submissions/status, shares one bounded writer.
load_state = STORAGE.load
save_state = STORAGE.save


def timestamp(value):
    return datetime.fromisoformat(value.replace("Z", "+00:00")).timestamp()


def net(deal):
    return sum(float(deal.get(k) or 0) for k in ("profit", "commission", "swap", "fee"))


def allocated_entry_costs(entries, closed_volume):
    volume = sum(float(d.get("volume") or 0) for d in entries)
    if volume <= 0 or closed_volume <= 0:
        return 0.0, 0.0
    share = min(1.0, closed_volume / volume)
    return (sum(float(d.get("commission") or 0) for d in entries) * share,
            sum(float(d.get("fee") or 0) for d in entries) * share)


def checked(values, name):
    if values is None:
        raise RuntimeError(f"FTMO cannot read {name}; execution blocked.")
    return [v._asdict() for v in values]


def windows(payload):
    now = datetime.now(timezone.utc).timestamp()
    start, end = timestamp(payload["dayStartUtc"]), timestamp(payload["nextDayStartUtc"])
    if not start <= now < end or not 23 * 3600 <= end - start <= 25 * 3600:
        raise RuntimeError("Stale or invalid FTMO daily reset boundaries.")
    return now, start, end


def current_exposure(mt5, payload):
    """Identity and broker exposure only: no history, sizing or calendar dependency."""
    account = mt5.account_info()
    if account is None:
        raise RuntimeError("FTMO account unavailable.")
    if not all(math.isfinite(float(v)) for v in (account.balance, account.equity)) or account.balance <= 0:
        raise RuntimeError("Invalid FTMO account balance/equity.")
    try:
        configured_id = int(payload.get("accountId", 0))
        connected_id = int(account.login)
    except (TypeError, ValueError, OverflowError) as error:
        raise RuntimeError(f"Invalid account identity in protection request: configured={payload.get('accountId')!r}; connected={getattr(account, 'login', None)!r}.") from error
    if configured_id <= 0 or configured_id != connected_id:
        raise RuntimeError(f"MT5 account does not match configured account: request={configured_id}; connected={connected_id}; payloadKeys={sorted(payload.keys())}.")
    positions = checked(mt5.positions_get(), "positions")
    orders = checked(mt5.orders_get(), "orders")
    return account, positions, orders


def degraded_snapshot(account, positions, orders, error):
    # Unknown accounting is explicit. These placeholders must never authorize entries.
    return {"account": {"balance": account.balance, "equity": account.equity,
            "dailyStartingBalance": 0, "existingRisk": 0, "activeTrades": len(positions) + len(orders),
            "tradesToday": 0, "consecutiveLosses": 0, "lastLossUtc": None, "tradingDays": 0,
            "exposureKnown": False, "currency": account.currency},
            "accountId": account.login, "positions": positions, "orders": orders,
            "deals": [], "entryTimes": [], "bid": 0, "ask": 0, "instrument": {},
            "accountingAvailable": False, "accountingError": str(error)}


def snapshot(mt5, payload, allow_degraded=False):
    account, positions, orders = current_exposure(mt5, payload)
    if allow_degraded and payload["policy"]["killSwitch"]:
        return degraded_snapshot(account, positions, orders, "History bypassed for emergency protection.")
    now, start, end = windows(payload)
    policy = payload["policy"]
    history_start = min(start, timestamp(policy["challengeStartUtc"]))
    try:
        deals = checked(mt5.history_deals_get(datetime.fromtimestamp(history_start, timezone.utc),
                                            datetime.fromtimestamp(now, timezone.utc)), "deal history")
    except Exception as error:
        if not allow_degraded:
            raise
        return degraded_snapshot(account, positions, orders, error)
    daily_net = sum(net(d) for d in deals if d["time"] >= start)
    entries = [d for d in deals if d.get("type") in (mt5.DEAL_TYPE_BUY, mt5.DEAL_TYPE_SELL)
               and d.get("entry") in (mt5.DEAL_ENTRY_IN, mt5.DEAL_ENTRY_INOUT)]
    # Count positions, not partial-fill deals. Account-wide, independent of magic/symbol.
    today_ids = {d["position_id"] for d in entries if d["time"] >= start}
    active_ids = {p["identifier"] for p in positions}
    grouped = {}
    for d in deals:
        if d.get("type") in (mt5.DEAL_TYPE_BUY, mt5.DEAL_TYPE_SELL):
            grouped.setdefault(d["position_id"], []).append(d)
    closed = []
    for position_id, items in grouped.items():
        exits = [d for d in items if d.get("entry") in (mt5.DEAL_ENTRY_OUT, mt5.DEAL_ENTRY_OUT_BY, mt5.DEAL_ENTRY_INOUT)]
        if exits and position_id not in active_ids:
            closed.append((max(d["time"] for d in exits), sum(net(d) for d in items)))
    closed.sort(reverse=True)
    streak = 0
    for time, pnl in closed:
        if time < start or pnl >= 0:
            break
        streak += 1
    losses = [t for t, pnl in closed if pnl < 0]
    exposure = 0.0
    known = True
    for value, pending in [(p, False) for p in positions] + [(o, True) for o in orders]:
        sl = float(value.get("sl") or 0)
        info = mt5.symbol_info(value["symbol"])
        if not sl or info is None:
            known = False
            continue
        kind = value["type"]
        buy = kind in ((mt5.ORDER_TYPE_BUY_LIMIT, mt5.ORDER_TYPE_BUY_STOP, mt5.ORDER_TYPE_BUY_STOP_LIMIT)
                       if pending else (mt5.POSITION_TYPE_BUY,))
        volume = float(value.get("volume_current") if pending else value["volume"])
        price = value["price_open"] if pending else value["price_current"]
        result = mt5.order_calc_profit(mt5.ORDER_TYPE_BUY if buy else mt5.ORDER_TYPE_SELL,
                                       value["symbol"], volume, price, sl)
        if result is None or not math.isfinite(float(result)):
            known = False
            continue
        # EURUSD reserve conversion; reject foreign-symbol exposure rather than guess pip units.
        if value["symbol"] != payload["symbol"]:
            known = False
        pip_value = float(info.trade_tick_value_loss) * 0.0001 / float(info.trade_tick_size) if info.trade_tick_size else 0
        if not math.isfinite(pip_value) or pip_value <= 0:
            known = False
        exposure += max(0, -result) + volume * (policy["commissionPerLot"] + pip_value * policy["slippageReservePips"])
    info = mt5.symbol_info(payload["symbol"])
    tick = mt5.symbol_info_tick(payload["symbol"])
    if payload.get("signal") and (info is None or tick is None):
        raise RuntimeError("FTMO symbol metadata/quote unavailable.")
    quote_age = now - tick.time if tick else 0
    if payload.get("signal") and (not all(math.isfinite(float(v)) for v in (tick.bid, tick.ask, tick.time))
            or quote_age < -5 or quote_age > policy["maximumQuoteAgeSeconds"] or tick.bid <= 0 or tick.ask < tick.bid):
        raise RuntimeError("FTMO stale or malformed quote.")
    signal = payload.get("signal")
    loss_per_lot = 0
    if signal:
        if not all(math.isfinite(float(v)) and float(v) > 0 for v in
                   (info.volume_min, info.volume_max, info.volume_step, info.trade_tick_value_loss, info.trade_tick_size)):
            raise RuntimeError("Invalid broker sizing metadata.")
        value = mt5.order_calc_profit(mt5.ORDER_TYPE_BUY if signal["direction"] == 0 else mt5.ORDER_TYPE_SELL,
                                     payload["symbol"], 1.0, signal["entryPrice"], signal["stopLoss"])
        if value is None or not math.isfinite(float(value)):
            raise RuntimeError("Broker cannot calculate stop-loss risk.")
        loss_per_lot = max(0, -value)
    return {
        "accountingAvailable": True, "accountingError": None,
        "account": {"balance": account.balance, "equity": account.equity,
                    "dailyStartingBalance": account.balance - daily_net,
                    "existingRisk": exposure, "activeTrades": len(positions) + len(orders),
                    "tradesToday": len(today_ids), "consecutiveLosses": streak,
                    "lastLossUtc": datetime.fromtimestamp(max(losses), timezone.utc).isoformat() if losses else None,
                    "tradingDays": 0, "exposureKnown": known, "currency": account.currency},
        "instrument": {"lossPerLot": loss_per_lot, "volumeMinimum": info.volume_min if info else 0,
                       "volumeMaximum": info.volume_max if info else 0, "volumeStep": info.volume_step if info else 0,
                       "minimumStopDistance": getattr(info, "trade_stops_level", 0) * getattr(info, "point", 0),
                       "tickSize": info.trade_tick_size if info else 0,
                       "pipValuePerLot": float(info.trade_tick_value_loss) * 0.0001 / float(info.trade_tick_size) if info and info.trade_tick_size else 0},
        "entryTimes": [datetime.fromtimestamp(d["time"], timezone.utc).isoformat() for d in entries
                       if d["time"] >= timestamp(policy["challengeStartUtc"])],
        "bid": tick.bid if tick else 0, "ask": tick.ask if tick else 0, "accountId": account.login,
        "positions": positions, "orders": orders, "deals": deals,
    }


def health(payload):
    account_id = payload.get("accountId", 0)
    state = load_state(state_path(payload, account_id))
    checked_at = PROTECTION_HEALTH.get(account_id)
    age = datetime.now(timezone.utc).timestamp() - checked_at if checked_at else None
    fresh = age is not None and age <= payload["policy"].get("maximumProtectionAgeSeconds", 20)
    unresolved = sum(a.get("status") in ("reserved", "unknown") for a in state["attempts"].values())
    result = PROTECTION_RESULTS.get(account_id, {})
    return {"protectionFresh": fresh, "protectionAgeSeconds": age, "unresolvedSubmissions": unresolved,
            "entryEligible": fresh and not unresolved and not result.get("halted", True) and not state["halted"],
            "persistedHalt": state["halted"], "lastProtection": result, "gate": LOCK.health(),
            "performance": performance(state),
            "lifecycleCounts": {s: sum(a["status"] == s for a in state["attempts"].values())
                                for s in {a["status"] for a in state["attempts"].values()}}}


def protection_reason(account, p):
    return protection_status(account, p)[1]


def protection_status(account, p):
    if p["killSwitch"]:
        return "KillHalt", "Kill switch"
    if not account["exposureKnown"] or min(account["balance"], account["equity"], account["dailyStartingBalance"]) <= 0:
        return "UnknownExposure", "Unknown exposure or account state"
    if account["currency"] != p["currency"]:
        return "CurrencyMismatch", "Account currency mismatch"
    daily = min(p["maximumDailyLossAmount"] - p["dailySafetyBufferAmount"], p["initialBalance"] * p["internalDailyLossPercent"] / 100)
    if account["equity"] <= p["initialBalance"] - p["maximumLossAmount"] + p["totalSafetyBufferAmount"]:
        return "TotalHalt", "Total loss safety threshold"
    if account["equity"] - account["existingRisk"] <= account["dailyStartingBalance"] - daily:
        return "DailyHalt", "Daily loss/exposure safety threshold"
    if account["equity"] - account["existingRisk"] <= p["initialBalance"] - p["maximumLossAmount"] + p["totalSafetyBufferAmount"]:
        return "TotalExposure", "Total exposure safety threshold"
    return "Running", None


def protect(mt5, payload, cancel):
    with LOCK.acquire(protection=True):
        PROTECTION_HEALTH.pop(payload.get("accountId"), None)
        data = snapshot(mt5, payload, True)
        p, account = payload["policy"], data["account"]
        errors = []
        state_valid = True
        emergency = p["killSwitch"]
        path = None
        try:
            # A kill request performs broker actions before touching any journal storage.
            if emergency:
                state = {"attempts": {}, "halted": True, "haltCode": "KillHalt", "haltReason": "Kill switch"}
            else:
                path = state_path(payload, data["accountId"])
                state = load_state(path)
        except Exception as error:
            state_valid = False
            state = {"attempts": {}, "halted": False}
            errors.append(f"journal: {error}")
        def persist():
            if state_valid and not emergency:
                try:
                    save_state(path, state)
                except Exception as error:
                    errors.append(f"journal write: {error}")
        try:
            if data.get("accountingAvailable", True):
                reconcile(state, data, payload["magicNumber"], mt5)
        except Exception as error:
            errors.append(f"reconciliation: {error}")
        persist()
        code, reason = protection_status(account, p)
        if not data.get("accountingAvailable", True) and code == "UnknownExposure":
            code, reason = "AccountingUnavailable", "Historical accounting unavailable; entries paused and owned exposure protected."
        if code in ("TotalHalt", "KillHalt"):
            state["halted"] = True
            state["haltReason"] = reason
            state["haltCode"] = code
            persist()
        if state["halted"]:
            reason = state.get("haltReason", "Persisted protection halt")
            code = state.get("haltCode", "PersistentHalt")
        if p["killSwitch"]:
            # Emergency cancellation/close must not depend on daily-accounting boundaries.
            now = datetime.now(timezone.utc).timestamp()
            start, end = now, now + 86400
        else:
            now, start, end = windows(payload)
        if code == "DailyHalt":
            state["dailyHaltUntil"] = end
            persist()
        if state.get("dailyHaltUntil", 0) > now:
            reason = reason or "Persisted daily safety halt"
            if code == "Running":
                code = "DailyHalt"
        if now >= end - p["flattenBeforeResetMinutes"] * 60:
            if not reason:
                code = "PreReset"
            reason = reason or "Pre-reset flattening window"
        if account["balance"] >= p["initialBalance"] + p["profitTargetAmount"]:
            if not reason:
                code = "TargetHalt"
            reason = reason or "Profit target: preserve capital"
        if errors:
            code, reason = "JournalUnavailable", "Execution journal/reconciliation is unavailable; protect owned exposure."
        actions = []
        for order in data["orders"]:
            if order.get("magic") != payload["magicNumber"]:
                continue
            attempt = next((a for a in state["attempts"].values()
                            if str(a.get("brokerOrderId", "")) == str(order["ticket"])), {})
            context = attempt.get("smcContext")
            # A lost response can leave a broker order without a recorded ticket. Never resubmit;
            # cancel unmatched owned pending exposure while that SMC reservation is unresolved.
            unreconciled = not attempt
            invalidated = context and (data["bid"] > 0 and data["bid"] <= context["sweepExtreme"]
                                      if attempt.get("direction") == 0 else data["ask"] >= context["sweepExtreme"])
            expected = attempt.get("signal")
            native_invalid = expected and (order.get("sl", 0) <= 0 or order.get("tp", 0) <= 0
                or order.get("time_expiration", 0) <= now
                or abs(order["tp"] - expected["takeProfit"]) > 1e-8
                or (order["sl"] - expected["stopLoss"]) * (1 if attempt.get("direction") == 0 else -1) < -1e-8)
            if reason or invalidated or unreconciled or native_invalid or now - order["time_setup"] >= p["pendingLifetimeMinutes"] * 60:
                try:
                    status, result = cancel(str(order["ticket"]))
                    if status != 200:
                        raise RuntimeError("FTMO pending cancellation failed: " + str(result))
                    actions.append(f"cancelled:{order['ticket']}")
                except Exception as error:
                    errors.append(f"order:{order['ticket']}: {error}")
        # An order may fill while cancellation is in flight. Always refresh before closing.
        if mt5 is not None:
            try:
                data["positions"] = checked(mt5.positions_get(), "positions after cancellation")
            except Exception as error:
                errors.append(str(error))
        for position in data["positions"]:
            if position.get("magic") != payload["magicNumber"]:
                continue
            attempt = next((a for a in state["attempts"].values() if a.get("positionId") == position.get("identifier")
                            and a.get("positionId") is not None), {})
            expected = attempt.get("signal")
            native_invalid = position.get("sl", 0) <= 0 or position.get("tp", 0) <= 0 or expected and \
                ((position["sl"] - expected["stopLoss"]) * (1 if attempt.get("direction") == 0 else -1) < -1e-8
                 or abs(position["tp"] - expected["takeProfit"]) > 1e-8)
            if not reason and not native_invalid and now - position["time"] < p["maximumHoldingMinutes"] * 60:
                continue
            try:
                tick = mt5.symbol_info_tick(position["symbol"])
                info = mt5.symbol_info(position["symbol"])
                if tick is None or info is None or tick.bid <= 0 or tick.ask < tick.bid:
                    raise RuntimeError("No valid quote/metadata for protective close.")
                buy = position["type"] == mt5.POSITION_TYPE_BUY
                fill = mt5.ORDER_FILLING_FOK if info.filling_mode & 1 else mt5.ORDER_FILLING_IOC
                result = mt5.order_send({"action": mt5.TRADE_ACTION_DEAL, "position": position["ticket"],
                                        "symbol": position["symbol"], "volume": position["volume"],
                                        "type": mt5.ORDER_TYPE_SELL if buy else mt5.ORDER_TYPE_BUY,
                                        "price": tick.bid if buy else tick.ask, "deviation": 20,
                                        "magic": payload["magicNumber"], "comment": "FTMO protection",
                                        "type_filling": fill})
                if result is None or result.retcode != mt5.TRADE_RETCODE_DONE:
                    raise RuntimeError("Close failed/partial; remaining volume will be reconciled next cycle.")
                actions.append(f"closed:{position['ticket']}")
            except Exception as error:
                errors.append(f"position:{position['ticket']}: {error}")
        if actions or errors:
            try:
                data = snapshot(mt5, payload, True)
                account = data["account"]
                live_orders = {str(o["ticket"]) for o in data["orders"]}
                live_positions = {str(o["ticket"]) for o in data["positions"]}
                for action in actions:
                    kind, ticket = action.split(":")
                    if ticket in (live_orders if kind == "cancelled" else live_positions):
                        errors.append(f"Broker has not confirmed {action}; reconcile next cycle.")
                if data.get("accountingAvailable", True):
                    reconcile(state, data, payload["magicNumber"], mt5)
                post_code, post_reason = protection_status(account, p)
                if post_reason and not reason:
                    code, reason = post_code, post_reason
                if post_code in ("TotalHalt", "KillHalt"):
                    state.update(halted=True, haltCode=post_code, haltReason=post_reason)
                if post_code == "DailyHalt":
                    state["dailyHaltUntil"] = end
                persist()
            except Exception as error:
                errors.append(f"post-action reconciliation: {error}")
        if emergency:
            # Merge after actions; never replace a corrupt journal with an empty one.
            try:
                path = state_path(payload, data["accountId"])
                durable = load_state(path)
                durable.update(halted=True, haltCode="KillHalt", haltReason="Kill switch")
                save_state(path, durable)
            except Exception as error:
                errors.append(f"emergency journal: {error}")
                STORAGE.report("Emergency actions attempted; halt persistence unavailable. Entries remain blocked.")
        if not errors and data.get("accountingAvailable", True):
            PROTECTION_HEALTH[data["accountId"]] = now
        if not reason and any(a.get("status") == "unknown" for a in state["attempts"].values()):
            code, reason = "ReconciliationRequired", "Unknown submission requires broker reconciliation; entries paused."
        if errors:
            code, reason = "ProtectionIncomplete", "One or more protective actions failed."
        result = {"halted": bool(reason), "reason": reason, "haltCode": code, "actions": actions, "errors": errors,
                  "accountingAvailable": data.get("accountingAvailable", True), "accountingError": data.get("accountingError"),
                  "account": account, "entryTimes": data["entryTimes"],
                  "targetReached": not errors and not any(a["status"] in ("unknown", "reserved") for a in state["attempts"].values())
                  and account["exposureKnown"] and account["activeTrades"] == 0 and account["balance"] >= p["initialBalance"] + p["profitTargetAmount"]
                  and len({datetime.fromisoformat(t.replace("Z", "+00:00")).astimezone(ZoneInfo("Europe/Prague")).date()
                           for t in data["entryTimes"]}) >= p["minimumTradingDays"]}
        PROTECTION_RESULTS[data["accountId"]] = result
        return result


def submit(mt5, payload, create):
    with LOCK.acquire():
        policy = payload["policy"]
        if policy.get("lossModel", "Static") != "Static" or policy.get("resetTimeZone", "Europe/Prague") != "Europe/Prague":
            raise RuntimeError("Unsupported challenge loss model/reset timezone.")
        if not payload.get("entryEnabled", False) or not policy.get("enabled", False) or not policy.get("rulesConfirmed", False) \
                or policy.get("accountVariant") not in ("Challenge", "Verification", "FreeTrial") or payload.get("accountId", 0) <= 0:
            raise RuntimeError("Live entry permission/account confirmation is missing.")
        data = snapshot(mt5, payload)
        if datetime.now(timezone.utc).timestamp() - PROTECTION_HEALTH.get(data["accountId"], 0) > policy.get("maximumProtectionAgeSeconds", 20):
            raise RuntimeError("Protection heartbeat is stale or unavailable.")
        p, a, instrument = payload["policy"], data["account"], data["instrument"]
        path = state_path(payload, data["accountId"])
        state = load_state(path)
        reason = protection_reason(a, p)
        if reason or state["halted"] or state.get("dailyHaltUntil", 0) > datetime.now(timezone.utc).timestamp():
            raise RuntimeError(reason or "Persisted FTMO halt")
        signal, lots = payload["signal"], float(payload["lots"])
        key = signal.get("setupId")
        previous = state["attempts"].get(key, {})
        retry = previous.get("status") == "rejected" and previous.get("retryable", False) \
            and previous.get("attemptCount", 1) < policy.get("maximumSubmissionAttempts", 3) \
            and datetime.now(timezone.utc).timestamp() >= previous.get("timestamp", 0) + policy.get("submissionRetryDelaySeconds", 15)
        if not key or key in state["attempts"] and not retry:
            raise RuntimeError("Missing or already submitted setup ID; uncertain submissions are never retried.")
        if any(a.get("status") in ("reserved", "unknown") for a in state["attempts"].values()):
            raise RuntimeError("Unresolved prior submission; reconcile before creating new exposure.")
        if a["activeTrades"] >= p["maximumActiveTrades"] or a["tradesToday"] + a["activeTrades"] >= p["maximumTradesPerDay"]:
            raise RuntimeError("FTMO active/daily trade limit.")
        if a["consecutiveLosses"] >= p["maximumConsecutiveLosses"]:
            raise RuntimeError("FTMO daily consecutive loss limit.")
        now, _, end = windows(payload)
        if payload.get("useNewsFilter", False):
            if payload.get("newsMaximumAgeMinutes", 0) and (not payload.get("newsGeneratedAtUtc")
                    or now - timestamp(payload["newsGeneratedAtUtc"]) > payload["newsMaximumAgeMinutes"] * 60
                    or timestamp(payload["newsGeneratedAtUtc"]) > now + 60):
                raise RuntimeError("News calendar generation time is stale or invalid.")
            start_coverage, end_coverage = payload.get("newsCoverageFromUtc"), payload.get("newsCoverageUntilUtc")
            if not start_coverage or not end_coverage or not timestamp(start_coverage) <= now < timestamp(end_coverage):
                raise RuntimeError("News calendar coverage is missing or expired.")
            for interval in payload.get("newsBlackoutWindowsUtc", []):
                start_event, end_event = map(timestamp, interval.split("/"))
                if start_event - payload.get("minutesBeforeHighImpactNews", 15) * 60 <= now \
                        <= end_event + payload.get("minutesAfterHighImpactNews", 30) * 60:
                    raise RuntimeError("News blackout blocks live submission.")
        context = signal.get("smcContext")
        if key.startswith("FTMO-SMC|") and not context:
            raise RuntimeError("Protected SMC requires signal timing metadata.")
        if context and not timestamp(context["fvgConfirmedAt"]) <= now < timestamp(context["submitBefore"]):
            raise RuntimeError("SMC submission window expired or unconfirmed.")
        if context:
            verify_unconsumed_entry(mt5, payload, now)
            # Tick retrieval can take time; do not size/send from the earlier account or quote.
            data = snapshot(mt5, payload)
            a, instrument = data["account"], data["instrument"]
            if protection_reason(a, p) or a["activeTrades"] >= p["maximumActiveTrades"] \
                    or a["tradesToday"] + a["activeTrades"] >= p["maximumTradesPerDay"] \
                    or a["consecutiveLosses"] >= p["maximumConsecutiveLosses"]:
                raise RuntimeError("Account risk changed during signal verification.")
        if a["lastLossUtc"] and now < timestamp(a["lastLossUtc"]) + p["lossCooldownMinutes"] * 60:
            raise RuntimeError("FTMO cooldown.")
        if now >= end - p["flattenBeforeResetMinutes"] * 60 or a["balance"] >= p["initialBalance"] + p["profitTargetAmount"]:
            raise RuntimeError("FTMO entry window/target stop.")
        sign = 1 if signal["direction"] == 0 else -1
        stop = (Decimal(str(signal["entryPrice"])) - Decimal(str(signal["stopLoss"]))) * sign
        reward = (Decimal(str(signal["takeProfit"])) - Decimal(str(signal["entryPrice"]))) * sign
        if not signal["isValidSetup"] or stop <= 0 or reward / stop < Decimal(str(p["minimumRiskReward"])):
            raise RuntimeError("FTMO invalid SL/TP or RR.")
        if sign == 1 and signal["entryPrice"] >= data["ask"] or sign == -1 and signal["entryPrice"] <= data["bid"]:
            raise RuntimeError("FTMO limit entry already crossed.")
        distance = instrument.get("minimumStopDistance", 0)
        quote_distance = data["ask"] - signal["entryPrice"] if sign == 1 else signal["entryPrice"] - data["bid"]
        if min(float(stop), float(reward), quote_distance) < distance:
            raise RuntimeError("FTMO broker minimum stop distance rejected.")
        tick_size = instrument.get("tickSize", 0)
        if tick_size > 0 and any(abs(float(signal[k]) / tick_size - round(float(signal[k]) / tick_size)) > 1e-6
                                 for k in ("entryPrice", "stopLoss", "takeProfit")):
            raise RuntimeError("FTMO price is not aligned to broker tick size.")
        if not math.isfinite(lots) or lots < instrument["volumeMinimum"] or lots > instrument["volumeMaximum"] \
                or abs(lots / instrument["volumeStep"] - round(lots / instrument["volumeStep"])) > 1e-7:
            raise RuntimeError("FTMO invalid broker volume.")
        if instrument["lossPerLot"] <= 0 or instrument["pipValuePerLot"] <= 0:
            raise RuntimeError("FTMO unknown instrument risk.")
        risk = lots * (instrument["lossPerLot"] + p["commissionPerLot"] + instrument["pipValuePerLot"] * p["slippageReservePips"])
        if risk > min(a["balance"], a["equity"]) * p["riskPercent"] / 100 + 1e-7:
            raise RuntimeError("FTMO final per-trade risk exceeded.")
        if risk + a["existingRisk"] > p["initialBalance"] * p["maximumAggregateRiskPercent"] / 100:
            raise RuntimeError("FTMO final aggregate risk exceeded.")
        daily = min(p["maximumDailyLossAmount"] - p["dailySafetyBufferAmount"], p["initialBalance"] * p["internalDailyLossPercent"] / 100)
        if a["equity"] - a["existingRisk"] - risk <= max(a["dailyStartingBalance"] - daily,
                p["initialBalance"] - p["maximumLossAmount"] + p["totalSafetyBufferAmount"]):
            raise RuntimeError("FTMO final loss headroom exceeded.")
        if (data["ask"] - data["bid"]) / 0.0001 > payload["maxSpreadPips"]:
            raise RuntimeError("FTMO spread guard.")
        # Durable write-ahead reservation before the broker call: safe even after response loss/restart.
        now = datetime.now(timezone.utc).timestamp()
        if now - PROTECTION_HEALTH.get(data["accountId"], 0) > policy.get("maximumProtectionAgeSeconds", 20) \
                or context and now >= timestamp(context["submitBefore"]):
            raise RuntimeError("Protection heartbeat or signal expired during validation.")
        client_id = hashlib.sha256(key.encode()).hexdigest()[:16]
        send_before = min(PROTECTION_HEALTH[data["accountId"]] + policy.get("maximumProtectionAgeSeconds", 20),
                          end - p["flattenBeforeResetMinutes"] * 60)
        if context:
            send_before = min(send_before, timestamp(context["submitBefore"]))
        if payload.get("useNewsFilter", False):
            send_before = min(send_before, timestamp(payload["newsCoverageUntilUtc"]))
            if payload.get("newsMaximumAgeMinutes", 0):
                send_before = min(send_before, timestamp(payload["newsGeneratedAtUtc"]) + payload["newsMaximumAgeMinutes"] * 60)
            for interval in payload.get("newsBlackoutWindowsUtc", []):
                blackout_start = timestamp(interval.split("/")[0]) - payload.get("minutesBeforeHighImpactNews", 15) * 60
                if blackout_start > now:
                    send_before = min(send_before, blackout_start)
        state["attempts"][key] = {"status": "reserved", "timestamp": now, "clientOrderId": client_id,
                                  "accountId": data["accountId"], "magicNumber": payload["magicNumber"], "symbol": payload["symbol"],
                                  "smcContext": context, "direction": signal["direction"],
                                  "attemptCount": previous.get("attemptCount", 0) + 1,
                                  "signal": signal, "lots": lots, "monetaryRisk": risk,
                                  "spreadPips": (data["ask"] - data["bid"]) / 0.0001,
                                  "strategyVersion": payload.get("strategyVersion", "unknown")}
        save_state(path, state)
        status, result = create({"symbol": payload["symbol"], "side": "BUY" if sign == 1 else "SELL",
                               "price": signal["entryPrice"], "stopLoss": signal["stopLoss"],
                               "takeProfit": signal["takeProfit"], "lots": lots, "magicNumber": payload["magicNumber"],
                               "clientOrderId": client_id,
                               "sendBeforeTimestamp": send_before, "maximumQuoteAgeSeconds": p["maximumQuoteAgeSeconds"],
                               "maxSpreadPips": payload["maxSpreadPips"],
                               "expirationMinutes": p["pendingLifetimeMinutes"], "requireExpiration": True,
                               "riskReward": signal["riskReward"]})
        definite = result.get("definitelyNotSent", False)
        state["attempts"][key]["status"] = "accepted" if status == 200 else "rejected" if definite else "unknown"
        state["attempts"][key]["retryable"] = bool(definite and result.get("retryable", False))
        if status == 200:
            state["attempts"][key]["brokerOrderId"] = str(result.get("brokerOrderId", ""))
        save_state(path, state)
        return 200, dict(result, accepted=status == 200,
                         retryable=state["attempts"][key].get("retryable", False),
                         executionState=state["attempts"][key]["status"])


def verify_unconsumed_entry(mt5, payload, now):
    signal = payload["signal"]
    confirmed = timestamp(signal["smcContext"]["fvgConfirmedAt"])
    ticks = mt5.copy_ticks_range(payload["symbol"], datetime.fromtimestamp(confirmed, timezone.utc),
                                datetime.fromtimestamp(now, timezone.utc), mt5.COPY_TICKS_INFO)
    if ticks is None or len(ticks) == 0:
        raise RuntimeError("SMC tick coverage unavailable since confirmation.")
    max_age = payload["policy"]["maximumQuoteAgeSeconds"]
    if float(ticks[0]["time"]) > confirmed + max_age or float(ticks[-1]["time"]) < now - max_age:
        raise RuntimeError("SMC tick coverage incomplete or stale.")
    for tick in ticks:
        bid, ask = float(tick["bid"]), float(tick["ask"])
        if not math.isfinite(bid) or not math.isfinite(ask) or bid <= 0 or ask < bid:
            raise RuntimeError("SMC tick coverage contains invalid quotes.")
        if signal["direction"] == 0 and ask <= signal["entryPrice"] \
                or signal["direction"] == 1 and bid >= signal["entryPrice"]:
            raise RuntimeError("SMC FVG entry was already consumed after confirmation.")
