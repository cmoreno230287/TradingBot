from __future__ import annotations

import argparse
import json
import threading
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any
from urllib.parse import parse_qs, unquote, urlparse

try:
    import MetaTrader5 as mt5
except ImportError as exc:
    mt5 = None
    MT5_IMPORT_ERROR = exc
else:
    MT5_IMPORT_ERROR = None


MT5_INITIALIZE_LOCK = threading.Lock()
MT5_INITIALIZED = False
MT5_TERMINAL_PATH: str | None = None
MT5_INITIALIZE_TIMEOUT_MS = 60000

TIMEFRAMES = {
    "M1": lambda: mt5.TIMEFRAME_M1,
    "M5": lambda: mt5.TIMEFRAME_M5,
    "H1": lambda: mt5.TIMEFRAME_H1,
    "D1": lambda: mt5.TIMEFRAME_D1,
}

TRADE_RETCODE_HINTS = {
    10018: "MT5 market is closed for this symbol.",
    10019: "MT5 rejected the order because there is not enough margin.",
    10020: "MT5 price changed before the order could be accepted.",
    10021: "MT5 has no quote available for this symbol.",
    10022: "MT5 rejected the pending order expiration mode or expiration time.",
    10024: "MT5 rejected the request because there are too many trade requests.",
    10027: "MT5 Algo Trading/AutoTrading is disabled in the client terminal. Enable the Algo Trading button and allow algorithmic trading in Tools > Options > Expert Advisors.",
    10030: "MT5 rejected the order filling mode for this symbol.",
}


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def parse_datetime(value: str) -> datetime:
    normalized = value.replace("Z", "+00:00")
    parsed = datetime.fromisoformat(normalized)
    return parsed if parsed.tzinfo else parsed.replace(tzinfo=timezone.utc)


def to_jsonable(value: Any) -> Any:
    if hasattr(value, "_asdict"):
        return {key: to_jsonable(item) for key, item in value._asdict().items()}
    if isinstance(value, (list, tuple)):
        return [to_jsonable(item) for item in value]
    if isinstance(value, dict):
        return {key: to_jsonable(item) for key, item in value.items()}
    return value


def ensure_mt5() -> None:
    global MT5_INITIALIZED

    if mt5 is None:
        raise RuntimeError(
            "Python package 'MetaTrader5' is not installed. Install it with: pip install -r tools/mt5-bridge/requirements.txt"
        ) from MT5_IMPORT_ERROR

    with MT5_INITIALIZE_LOCK:
        if MT5_INITIALIZED:
            terminal = mt5.terminal_info()
            if terminal is not None and getattr(terminal, "connected", False):
                return

            mt5.shutdown()
            MT5_INITIALIZED = False

        if MT5_TERMINAL_PATH:
            initialized = mt5.initialize(path=MT5_TERMINAL_PATH, timeout=MT5_INITIALIZE_TIMEOUT_MS)
        else:
            initialized = mt5.initialize(timeout=MT5_INITIALIZE_TIMEOUT_MS)

        if not initialized:
            code, message = mt5.last_error()
            path_hint = f" path='{MT5_TERMINAL_PATH}'" if MT5_TERMINAL_PATH else ""
            raise RuntimeError(
                f"MT5 terminal initialization failed{path_hint}: {code} {message}. "
                "Make sure FTMO-MT5 is open, logged in, not frozen by a modal dialog, and pass --terminal-path if multiple MT5 terminals are installed."
            )

        MT5_INITIALIZED = True


def account_info() -> dict[str, Any]:
    ensure_mt5()
    account = mt5.account_info()
    if account is None:
        code, message = mt5.last_error()
        raise RuntimeError(f"MT5 account is not available. Make sure FTMO-MT5 is open and logged in: {code} {message}")
    return to_jsonable(account)


def deal_profit(deal: dict[str, Any]) -> float:
    return float(deal.get("profit") or 0) + float(deal.get("commission") or 0) + float(deal.get("swap") or 0) + float(deal.get("fee") or 0)


def closed_deals(from_time: datetime, to_time: datetime) -> list[dict[str, Any]]:
    ensure_mt5()
    deals = mt5.history_deals_get(from_time, to_time)
    if deals is None:
        code, message = mt5.last_error()
        raise RuntimeError(f"Unable to fetch MT5 deal history: {code} {message}")

    values = [to_jsonable(deal) for deal in deals]
    return [
        deal
        for deal in values
        if int(deal.get("entry", -1)) in {mt5.DEAL_ENTRY_OUT, mt5.DEAL_ENTRY_INOUT, mt5.DEAL_ENTRY_OUT_BY}
    ]


def consecutive_losses(deals: list[dict[str, Any]]) -> int:
    count = 0
    ordered = sorted(deals, key=lambda item: int(item.get("time", 0)), reverse=True)
    for deal in ordered:
        profit = deal_profit(deal)
        if profit < 0:
            count += 1
            continue
        if profit > 0:
            break
    return count


def consecutive_losing_days(deals: list[dict[str, Any]]) -> int:
    daily: dict[str, float] = {}
    for deal in deals:
        closed_at = datetime.fromtimestamp(int(deal.get("time", 0)), timezone.utc)
        key = closed_at.date().isoformat()
        daily[key] = daily.get(key, 0.0) + deal_profit(deal)

    count = 0
    for key in sorted(daily.keys(), reverse=True):
        if daily[key] < 0:
            count += 1
            continue
        if daily[key] > 0:
            break
    return count


def account_risk_state() -> dict[str, Any]:
    account = account_info()
    now = datetime.now(timezone.utc)
    day_start = now.replace(hour=0, minute=0, second=0, microsecond=0)
    week_start = day_start - timedelta(days=day_start.weekday())
    history_start = day_start - timedelta(days=45)
    all_recent_deals = closed_deals(history_start, now)
    daily_deals = [deal for deal in all_recent_deals if int(deal.get("time", 0)) >= int(day_start.timestamp())]
    weekly_deals = [deal for deal in all_recent_deals if int(deal.get("time", 0)) >= int(week_start.timestamp())]
    daily_realized = sum(deal_profit(deal) for deal in daily_deals)
    weekly_realized = sum(deal_profit(deal) for deal in weekly_deals)
    trading_days = len(
        {
            datetime.fromtimestamp(int(deal.get("time", 0)), timezone.utc).date().isoformat()
            for deal in all_recent_deals
        }
    )

    return {
        "balance": account.get("balance", 0),
        "equity": account.get("equity", 0),
        "dailyRealizedProfitLoss": daily_realized,
        "weeklyRealizedProfitLoss": weekly_realized,
        "consecutiveLosses": consecutive_losses(daily_deals),
        "consecutiveLosingDays": consecutive_losing_days(all_recent_deals),
        "tradingDays": trading_days,
        "dailyStartingBalance": float(account.get("balance", 0)) - daily_realized,
        "server": account.get("server"),
        "login": account.get("login"),
        "currency": account.get("currency"),
    }


def select_symbol(symbol: str) -> Any:
    ensure_mt5()
    if not mt5.symbol_select(symbol, True):
        code, message = mt5.last_error()
        raise RuntimeError(f"Unable to select MT5 symbol '{symbol}': {code} {message}")

    info = mt5.symbol_info(symbol)
    if info is None:
        code, message = mt5.last_error()
        raise RuntimeError(f"MT5 symbol '{symbol}' was not found: {code} {message}")
    return info


def normalize_volume(symbol_info: Any, requested_lots: float) -> float:
    minimum = float(getattr(symbol_info, "volume_min", 0.01) or 0.01)
    maximum = float(getattr(symbol_info, "volume_max", requested_lots) or requested_lots)
    step = float(getattr(symbol_info, "volume_step", 0.01) or 0.01)
    volume = max(minimum, min(maximum, requested_lots))
    steps = round((volume - minimum) / step)
    return round(minimum + steps * step, 8)


def order_type_for(side: str, price: float, bid: float, ask: float) -> int:
    side = side.upper()
    if side == "BUY":
        if price >= ask:
            raise RuntimeError(f"BUY LIMIT price must be below current ask. price={price}, ask={ask}")
        return mt5.ORDER_TYPE_BUY_LIMIT
    if side == "SELL":
        if price <= bid:
            raise RuntimeError(f"SELL LIMIT price must be above current bid. price={price}, bid={bid}")
        return mt5.ORDER_TYPE_SELL_LIMIT
    raise RuntimeError(f"Unsupported order side '{side}'. Use BUY or SELL.")


def payload_value(payload: dict[str, Any], *names: str, required: bool = False, default: Any = None) -> Any:
    for name in names:
        if name in payload and payload[name] is not None:
            return payload[name]
    if required:
        received = ", ".join(sorted(payload.keys())) if payload else "none"
        raise RuntimeError(f"Missing required order field. Expected one of: {', '.join(names)}. Received fields: {received}")
    return default


def order_comment(client_order_id: str) -> str:
    digits = "".join(character for character in client_order_id if character.isdigit())
    suffix = digits[-12:] if digits else datetime.now(timezone.utc).strftime("%H%M%S")
    return f"TBOT{suffix}"[:16]


def trade_rejection_reason(response: dict[str, Any]) -> str:
    retcode = int(response.get("retcode", 0) or 0)
    comment = str(response.get("comment") or "").strip()
    hint = TRADE_RETCODE_HINTS.get(retcode)
    parts = [f"MT5 retcode {retcode}"]
    if comment:
        parts.append(comment)
    if hint:
        parts.append(hint)
    return ". ".join(parts)


class MT5BridgeHandler(BaseHTTPRequestHandler):
    server_version = "TradingBotMT5Bridge/1.0"

    def log_message(self, format: str, *args: Any) -> None:
        return

    def log_error(self, format: str, *args: Any) -> None:
        print(f"{utc_now()} ERROR {self.address_string()} {format % args}")

    def log_request(self, code: int | str = "-", size: int | str = "-") -> None:
        return

    def do_GET(self) -> None:
        self._handle(lambda: self.route_get())

    def do_POST(self) -> None:
        self._handle(lambda: self.route_post())

    def do_DELETE(self) -> None:
        self._handle(lambda: self.route_delete())

    def _handle(self, action: Any) -> None:
        try:
            status, payload = action()
            self.send_json(status, payload)
        except Exception as exc:
            print(f"{utc_now()} ERROR {self.command} {self.path} failed: {exc}")
            self.send_json(500, {"error": str(exc)})

    def send_json(self, status: int, payload: Any) -> None:
        body = json.dumps(payload, separators=(",", ":"), default=str).encode("utf-8")
        try:
            self.send_response(status)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            if status >= 400:
                print(f"{utc_now()} ERROR {self.command} {self.path} -> {status}: {json.dumps(payload, separators=(',', ':'), default=str)}")
        except (BrokenPipeError, ConnectionAbortedError, ConnectionResetError):
            print(f"{utc_now()} ERROR client disconnected before response could be written for {self.command} {self.path}")

    def route_get(self) -> tuple[int, Any]:
        parsed = urlparse(self.path)
        path = parsed.path.strip("/")
        query = parse_qs(parsed.query)

        if path == "health":
            ensure_mt5()
            return 200, {
                "isConnected": True,
                "serverTimeUtc": utc_now(),
                "mt5Version": list(mt5.version() or []),
                "terminal": to_jsonable(mt5.terminal_info()),
                "account": account_info(),
            }

        if path == "account":
            return 200, account_info()

        if path == "account-risk":
            return 200, account_risk_state()

        if path.startswith("symbols/"):
            symbol = unquote(path.split("/", 1)[1])
            info = select_symbol(symbol)
            return 200, to_jsonable(info)

        if path.startswith("quote/"):
            symbol = unquote(path.split("/", 1)[1])
            return 200, self.get_quote(symbol)

        if path == "candles":
            return 200, self.get_candles(query)

        if path == "ticks":
            return 200, self.get_ticks(query)

        if path == "positions":
            return 200, {"positions": self.get_positions(query)}

        if path == "orders":
            return 200, {"orders": self.get_orders(query)}

        if path == "closed-trades":
            return 200, {"trades": self.get_closed_trades(query)}

        return 404, {"error": f"Unknown endpoint '{path}'."}

    def route_post(self) -> tuple[int, Any]:
        parsed = urlparse(self.path)
        path = parsed.path.strip("/")

        if path == "orders":
            length = int(self.headers.get("Content-Length", "0"))
            payload = json.loads(self.rfile.read(length).decode("utf-8") or "{}")
            return self.create_order(payload)

        if path.startswith("orders/") and path.endswith("/cancel"):
            ticket = path.split("/")[1]
            return self.cancel_order(ticket)

        return 404, {"error": f"Unknown endpoint '{path}'."}

    def route_delete(self) -> tuple[int, Any]:
        parsed = urlparse(self.path)
        path = parsed.path.strip("/")
        if path.startswith("orders/"):
            ticket = path.split("/")[1]
            return self.cancel_order(ticket)
        return 404, {"error": f"Unknown endpoint '{path}'."}

    def get_candles(self, query: dict[str, list[str]]) -> dict[str, Any]:
        symbol = query.get("symbol", ["EURUSD"])[0]
        timeframe_name = query.get("timeframe", ["M5"])[0].upper()
        from_value = query.get("from", [""])[0]
        to_value = query.get("to", [""])[0]

        if timeframe_name not in TIMEFRAMES:
            raise RuntimeError(f"Unsupported timeframe '{timeframe_name}'. Supported: {', '.join(TIMEFRAMES)}")

        select_symbol(symbol)
        rates = mt5.copy_rates_range(symbol, TIMEFRAMES[timeframe_name](), parse_datetime(from_value), parse_datetime(to_value))
        if rates is None:
            code, message = mt5.last_error()
            raise RuntimeError(f"Unable to fetch candles for {symbol} {timeframe_name}: {code} {message}")

        candles = [
            {
                "symbol": symbol,
                "timeframe": timeframe_name,
                "openedAt": datetime.fromtimestamp(int(rate["time"]), timezone.utc).isoformat(),
                "open": float(rate["open"]),
                "high": float(rate["high"]),
                "low": float(rate["low"]),
                "close": float(rate["close"]),
                "volume": float(rate["tick_volume"]),
            }
            for rate in rates
        ]

        return {"symbol": symbol, "timeframe": timeframe_name, "candles": candles}

    def get_ticks(self, query: dict[str, list[str]]) -> dict[str, Any]:
        symbol = query.get("symbol", ["EURUSD"])[0]
        from_value = query.get("from", [""])[0]
        to_value = query.get("to", [""])[0]
        select_symbol(symbol)
        ticks = mt5.copy_ticks_range(symbol, parse_datetime(from_value), parse_datetime(to_value), mt5.COPY_TICKS_ALL)
        if ticks is None:
            code, message = mt5.last_error()
            raise RuntimeError(f"Unable to fetch ticks for {symbol}: {code} {message}")

        values = [
            {
                "symbol": symbol,
                "timestamp": datetime.fromtimestamp(int(tick["time"]), timezone.utc).isoformat(),
                "bid": float(tick["bid"]),
                "ask": float(tick["ask"]),
                "last": float(tick["last"]),
                "volume": float(tick["volume"]),
            }
            for tick in ticks
        ]

        return {"symbol": symbol, "ticks": values}

    def get_quote(self, symbol: str) -> dict[str, Any]:
        info = select_symbol(symbol)
        tick = mt5.symbol_info_tick(symbol)
        if tick is None:
            code, message = mt5.last_error()
            raise RuntimeError(f"Unable to fetch current tick for {symbol}: {code} {message}")

        point = float(getattr(info, "point", 0.00001) or 0.00001)
        pip_size = 0.0001 if "JPY" not in symbol.upper() else 0.01
        bid = float(tick.bid)
        ask = float(tick.ask)
        return {
            "symbol": symbol,
            "bid": bid,
            "ask": ask,
            "spreadPoints": (ask - bid) / point if point else 0,
            "spreadPips": (ask - bid) / pip_size if pip_size else 0,
            "time": datetime.fromtimestamp(int(tick.time), timezone.utc).isoformat() if getattr(tick, "time", 0) else utc_now(),
        }

    def get_positions(self, query: dict[str, list[str]]) -> list[dict[str, Any]]:
        ensure_mt5()
        symbol = query.get("symbol", [None])[0]
        magic = query.get("magicNumber", [None])[0]
        positions = mt5.positions_get(symbol=symbol) if symbol else mt5.positions_get()
        if positions is None:
            code, message = mt5.last_error()
            raise RuntimeError(f"Unable to fetch MT5 positions: {code} {message}")

        values = [to_jsonable(position) for position in positions]
        return self.filter_magic(values, magic)

    def get_orders(self, query: dict[str, list[str]]) -> list[dict[str, Any]]:
        ensure_mt5()
        symbol = query.get("symbol", [None])[0]
        magic = query.get("magicNumber", [None])[0]
        older_than_minutes = query.get("olderThanMinutes", [None])[0]
        orders = mt5.orders_get(symbol=symbol) if symbol else mt5.orders_get()
        if orders is None:
            code, message = mt5.last_error()
            raise RuntimeError(f"Unable to fetch MT5 orders: {code} {message}")

        values = self.filter_magic([to_jsonable(order) for order in orders], magic)
        if older_than_minutes:
            cutoff = datetime.now(timezone.utc) - timedelta(minutes=int(older_than_minutes))
            values = [
                order
                for order in values
                if datetime.fromtimestamp(int(order.get("time_setup", 0)), timezone.utc) <= cutoff
            ]

        return values

    def get_closed_trades(self, query: dict[str, list[str]]) -> list[dict[str, Any]]:
        ensure_mt5()
        symbol = query.get("symbol", [None])[0]
        magic = query.get("magicNumber", [None])[0]
        from_value = query.get("from", [None])[0]
        to_value = query.get("to", [None])[0]
        from_time = parse_datetime(from_value) if from_value else datetime.now(timezone.utc) - timedelta(days=30)
        to_time = parse_datetime(to_value) if to_value else datetime.now(timezone.utc)
        deals = mt5.history_deals_get(from_time, to_time)
        if deals is None:
            code, message = mt5.last_error()
            raise RuntimeError(f"Unable to fetch MT5 closed trade history: {code} {message}")

        values = [to_jsonable(deal) for deal in deals]
        if symbol:
            values = [deal for deal in values if str(deal.get("symbol", "")).upper() == symbol.upper()]
        if magic:
            values = [deal for deal in values if str(deal.get("magic")) == str(magic)]

        grouped: dict[str, list[dict[str, Any]]] = {}
        for deal in values:
            position_id = str(deal.get("position_id") or deal.get("position") or "")
            if not position_id:
                continue
            grouped.setdefault(position_id, []).append(deal)

        reports: list[dict[str, Any]] = []
        for position_id, position_deals in grouped.items():
            ordered = sorted(position_deals, key=lambda item: int(item.get("time", 0)))
            entries = [deal for deal in ordered if int(deal.get("entry", -1)) in {mt5.DEAL_ENTRY_IN, mt5.DEAL_ENTRY_INOUT}]
            exits = [deal for deal in ordered if int(deal.get("entry", -1)) in {mt5.DEAL_ENTRY_OUT, mt5.DEAL_ENTRY_INOUT, mt5.DEAL_ENTRY_OUT_BY}]
            if not entries or not exits:
                continue

            entry = entries[0]
            for exit_deal in exits:
                exit_type = int(exit_deal.get("type", -1))
                direction = "BUY" if exit_type == mt5.DEAL_TYPE_SELL else "SELL" if exit_type == mt5.DEAL_TYPE_BUY else "UNKNOWN"
                opened_at = datetime.fromtimestamp(int(entry.get("time", 0)), timezone.utc)
                closed_at = datetime.fromtimestamp(int(exit_deal.get("time", 0)), timezone.utc)
                profit = float(exit_deal.get("profit") or 0)
                commission = float(exit_deal.get("commission") or 0)
                swap = float(exit_deal.get("swap") or 0)
                fee = float(exit_deal.get("fee") or 0)
                entry_price = float(entry.get("price") or 0)
                close_price = float(exit_deal.get("price") or 0)
                stop_loss = float(exit_deal.get("sl") or entry.get("sl") or 0)
                take_profit = float(exit_deal.get("tp") or entry.get("tp") or 0)
                risk = abs(entry_price - stop_loss) if stop_loss else 0
                reward = abs(take_profit - entry_price) if take_profit else 0
                reports.append({
                    "tradeId": str(exit_deal.get("ticket") or exit_deal.get("deal") or f"{position_id}-{int(exit_deal.get('time', 0))}"),
                    "positionId": position_id,
                    "symbol": exit_deal.get("symbol") or entry.get("symbol") or symbol or "",
                    "session": "Unknown",
                    "direction": direction,
                    "entryPrice": entry_price,
                    "closePrice": close_price,
                    "stopLossPrice": stop_loss,
                    "takeProfitPrice": take_profit,
                    "riskRewardRatio": round(reward / risk, 2) if risk else 0,
                    "openedAt": opened_at.isoformat(),
                    "closedAt": closed_at.isoformat(),
                    "volume": float(exit_deal.get("volume") or entry.get("volume") or 0),
                    "profit": profit,
                    "commission": commission,
                    "swap": swap,
                    "netProfit": profit + commission + swap + fee,
                    "magicNumber": int(exit_deal.get("magic") or entry.get("magic") or 0),
                    "comment": str(exit_deal.get("comment") or entry.get("comment") or ""),
                })

        return sorted(reports, key=lambda item: item["closedAt"])

    @staticmethod
    def filter_magic(values: list[dict[str, Any]], magic: str | None) -> list[dict[str, Any]]:
        if not magic:
            return values
        return [value for value in values if str(value.get("magic")) == str(magic)]

    def create_order(self, payload: dict[str, Any]) -> tuple[int, Any]:
        ensure_mt5()
        symbol = str(payload_value(payload, "symbol", "Symbol", default="EURUSD"))
        side = str(payload_value(payload, "side", "tradeSide", "TradeSide", default="BUY"))
        price = float(payload_value(payload, "price", "entryPrice", "entry_point", "limitPrice", "LimitPrice", required=True))
        lots = float(payload_value(payload, "lots", "quantity", "Quantity", default=0))
        stop_loss = float(payload_value(payload, "stopLoss", "StopLoss", "stop_loss", default=0))
        take_profit = float(payload_value(payload, "takeProfit", "TakeProfit", "take_profit", default=0))
        magic = int(payload_value(payload, "magicNumber", "MagicNumber", default=0))
        client_order_id = str(payload_value(payload, "clientOrderId", "ClientOrderId", default=""))
        deviation = int(payload_value(payload, "deviation", "maxSlippagePoints", "MaxSlippagePoints", default=20))

        info = select_symbol(symbol)
        tick = mt5.symbol_info_tick(symbol)
        if tick is None:
            code, message = mt5.last_error()
            raise RuntimeError(f"Unable to fetch current tick for {symbol}: {code} {message}")

        request = {
            "action": mt5.TRADE_ACTION_PENDING,
            "symbol": symbol,
            "volume": normalize_volume(info, lots),
            "type": order_type_for(side, price, float(tick.bid), float(tick.ask)),
            "price": price,
            "sl": stop_loss,
            "tp": take_profit,
            "deviation": deviation,
            "magic": magic,
            "comment": order_comment(client_order_id),
            "type_time": mt5.ORDER_TIME_GTC,
            "type_filling": mt5.ORDER_FILLING_RETURN,
        }

        expiration_minutes = int(payload.get("expirationMinutes") or 0)
        if expiration_minutes > 0:
            request["type_time"] = mt5.ORDER_TIME_SPECIFIED
            request["expiration"] = int((datetime.now(timezone.utc) + timedelta(minutes=expiration_minutes)).timestamp())

        expiration_requested = "expiration" in request
        result = mt5.order_send(request)
        if result is None:
            code, message = mt5.last_error()
            raise RuntimeError(f"MT5 order_send failed: {code} {message}")

        response = to_jsonable(result)
        accepted_retcodes = {mt5.TRADE_RETCODE_DONE, mt5.TRADE_RETCODE_PLACED}
        if int(response.get("retcode", 0)) == 10022 and expiration_requested:
            retry_request = dict(request)
            retry_request["type_time"] = mt5.ORDER_TIME_GTC
            retry_request.pop("expiration", None)
            result = mt5.order_send(retry_request)
            if result is None:
                code, message = mt5.last_error()
                raise RuntimeError(f"MT5 order_send retry without expiration failed: {code} {message}")

            request = retry_request
            response = to_jsonable(result)

        if int(response.get("retcode", 0)) not in accepted_retcodes:
            return 400, {
                "error": "MT5 rejected order creation.",
                "reason": trade_rejection_reason(response),
                "retcode": int(response.get("retcode", 0) or 0),
                "request": request,
                "result": response,
            }

        return 200, {
            "clientOrderId": client_order_id,
            "brokerOrderId": str(response.get("order") or response.get("deal") or ""),
            "accountId": account_info().get("login"),
            "symbol": symbol,
            "tradeSide": side.upper(),
            "lots": request["volume"],
            "volume": int(round(request["volume"] * 100000)),
            "limitPrice": price,
            "stopLoss": stop_loss,
            "takeProfit": take_profit,
            "riskReward": payload.get("riskReward"),
            "expirationApplied": expiration_requested and "expiration" in request,
            "raw": response,
        }

    def cancel_order(self, ticket: str) -> tuple[int, Any]:
        ensure_mt5()
        request = {"action": mt5.TRADE_ACTION_REMOVE, "order": int(ticket)}
        result = mt5.order_send(request)
        if result is None:
            code, message = mt5.last_error()
            raise RuntimeError(f"MT5 cancel order failed: {code} {message}")

        response = to_jsonable(result)
        if int(response.get("retcode", 0)) != mt5.TRADE_RETCODE_DONE:
            return 400, {"error": "MT5 rejected order cancellation.", "request": request, "result": response}

        return 200, {"isCancelled": True, "brokerOrderId": str(ticket), "raw": response}


def main() -> int:
    global MT5_TERMINAL_PATH

    parser = argparse.ArgumentParser(description="Local HTTP bridge between TradingBot and the installed MT5 terminal.")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=5010)
    parser.add_argument("--terminal-path", default="", help="Optional full path to terminal64.exe for the FTMO MT5 terminal.")
    args = parser.parse_args()
    MT5_TERMINAL_PATH = args.terminal_path.strip() or None

    print(f"Starting TradingBot MT5 bridge on http://{args.host}:{args.port}")
    print("Keep FTMO-MT5 open and logged in while this process is running.")
    if MT5_TERMINAL_PATH:
        print(f"Using MT5 terminal path: {MT5_TERMINAL_PATH}")

    try:
        ensure_mt5()
        print("MT5 terminal initialized successfully.")
    except Exception as exc:
        print(f"MT5 terminal initialization failed during startup: {exc}")
        print("The bridge will keep running and retry initialization on the next request.")

    server = ThreadingHTTPServer((args.host, args.port), MT5BridgeHandler)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nStopping TradingBot MT5 bridge.")
    finally:
        if mt5 is not None:
            mt5.shutdown()
        server.server_close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
