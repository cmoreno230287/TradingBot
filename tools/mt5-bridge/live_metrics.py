"""Performance of fully reconciled positions, including all recorded execution costs."""


def summarize(attempts):
    trades = sorted((a for a in attempts if a.get("status") == "closed"), key=lambda a: a.get("closedAt", 0))
    pnl = [a["netProfit"] for a in trades]
    winners, losers = [p for p in pnl if p > 0], [p for p in pnl if p < 0]
    equity = peak = drawdown = 0
    streak = longest = 0
    for value in pnl:
        equity += value
        peak = max(peak, equity)
        drawdown = max(drawdown, peak - equity)
        streak = streak + 1 if value < 0 else 0
        longest = max(longest, streak)
    r_values = [a["netProfit"] / a["monetaryRisk"] for a in trades if a.get("monetaryRisk", 0) > 0]
    return {"closedPositions": len(pnl), "winRate": len(winners) / len(pnl) if pnl else None,
            "netProfit": sum(pnl), "expectancy": sum(pnl) / len(pnl) if pnl else None,
            "profitFactor": sum(winners) / -sum(losers) if losers else None,
            "averageRealizedR": sum(r_values) / len(r_values) if r_values else None,
            "rSampleSize": len(r_values), "realizedDrawdown": drawdown, "maximumLosingStreak": longest}


def performance(state):
    attempts = list(state["attempts"].values())
    groups = {}
    for attempt in attempts:
        signal = attempt.get("signal", {})
        diagnostics = signal.get("diagnostics") or {}
        atr = diagnostics.get("atrPips")
        spread = attempt.get("spreadPips")
        volatility = "unknown" if atr is None else "below5" if atr < 5 else "5to10" if atr < 10 else "10plus"
        spread_band = "unknown" if spread is None else "upTo1" if spread <= 1 else "above1"
        key = (attempt.get("strategyVersion", "legacy"), str(signal.get("session", "unknown")), str(attempt.get("direction", "unknown")),
               diagnostics.get("setupType", "unknown"), volatility, spread_band)
        groups.setdefault(key, []).append(attempt)
    return {"scope": "journal-owned fully closed positions; realized drawdown only", "overall": summarize(attempts),
            "groups": [{"strategyVersion": key[0], "session": key[1], "direction": key[2], "setupType": key[3],
                        "atrPipsBand": key[4], "spreadPipsBand": key[5], **summarize(values)}
                       for key, values in groups.items()]}
