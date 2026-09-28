using System.Text.Json;
using System.Text.Json.Nodes;
using TradingBot.Application;
using TradingBot.Domain;
using TradingBot.Infrastructure.Broker;
using TradingBot.Infrastructure.Configuration;
using TradingBot.Infrastructure.Filters;
using TradingBot.Infrastructure.MarketData;
using TradingBot.Infrastructure.MetaTrader;
using TradingBot.Infrastructure.Sessions;
using TradingBot.Reporting;
using TradingBot.Shared;
using TradingBot.Strategies;


internal static partial class CliCommands
{
    internal static async Task<Result> ValidateActiveTradeLimitAsync(
        ServiceRegistry services,
        string cycleId,
        bool formattedOutput,
        CancellationToken cancellationToken,
        ActiveTradeSummary? knownSummary = null,
        bool printSummary = true)
    {
        var summaryResult = knownSummary is not null
            ? Result<ActiveTradeSummary>.Success(knownSummary)
            : await GetActiveTradeSummaryAsync(services, cancellationToken);
        if (!summaryResult.IsSuccess)
        {
            await services.Monitor.WriteAsync(cycleId, "active_trade_check_failed", new
            {
                summaryResult.Error
            }, cancellationToken);

            if (formattedOutput)
            {
                PrintFailure("Active Trade Check", summaryResult.Error!);
            }

            return Result.Failure(summaryResult.Error!);
        }

        var summary = summaryResult.Value!;
        await services.Monitor.WriteAsync(cycleId, "active_trade_checked", new
        {
            summary.OpenPositions,
            summary.PendingOrders,
            summary.TotalActiveTrades,
            maxActiveTrades = GetMaxActiveTrades(services.Options)
        }, cancellationToken);

        if (formattedOutput && printSummary)
        {
            PrintActiveTradeSummary(summary, GetMaxActiveTrades(services.Options));
        }

        var maxActiveTrades = GetMaxActiveTrades(services.Options);
        if (summary.TotalActiveTrades >= maxActiveTrades)
        {
            await services.Monitor.WriteAsync(cycleId, "active_trade_limit_reached", new
            {
                summary.TotalActiveTrades,
                maxActiveTrades
            }, cancellationToken);

            return Result.Failure($"Active trade limit reached. Active={summary.TotalActiveTrades}, MaxAllowed={maxActiveTrades}.");
        }

        return Result.Success();
    }

    internal static async Task<Result<ActiveTradeSummary>> ReportBrokerOrderStatusAsync(
        ServiceRegistry services,
        string cycleId,
        bool formattedOutput,
        CancellationToken cancellationToken)
    {
        var summaryResult = await GetActiveTradeSummaryAsync(services, cancellationToken);
        if (!summaryResult.IsSuccess)
        {
            await services.Monitor.WriteAsync(cycleId, "broker_order_status_failed", new
            {
                summaryResult.Error
            }, cancellationToken);

            if (formattedOutput)
            {
                PrintFailure("Broker Orders", summaryResult.Error!);
            }

            return Result<ActiveTradeSummary>.Failure(summaryResult.Error!);
        }

        var summary = summaryResult.Value!;
        await services.Monitor.WriteAsync(cycleId, "broker_order_status_checked", new
        {
            summary.OpenPositions,
            summary.PendingOrders,
            summary.TotalActiveTrades,
            maxActiveTrades = GetMaxActiveTrades(services.Options)
        }, cancellationToken);

        if (formattedOutput)
        {
            PrintBrokerOrderStatus(summary, GetMaxActiveTrades(services.Options));
        }

        return Result<ActiveTradeSummary>.Success(summary);
    }

    internal static async Task<Result> ValidateMarketExecutionGuardAsync(
        ServiceRegistry services,
        string cycleId,
        bool formattedOutput,
        CancellationToken cancellationToken)
    {
        if (!IsBroker(services.Options, "MT5"))
        {
            return Result.Success();
        }

        var snapshot = await services.MT5Bridge.GetMarketExecutionSnapshotAsync(services.Options.Symbol, cancellationToken);
        if (!snapshot.IsSuccess)
        {
            await services.Monitor.WriteAsync(cycleId, "market_guard_failed", new
            {
                snapshot.Error
            }, cancellationToken);

            if (formattedOutput)
            {
                PrintFailure("Market Execution Guard", snapshot.Error!);
            }

            return Result.Failure(snapshot.Error!);
        }

        var value = snapshot.Value!;
        await services.Monitor.WriteAsync(cycleId, "market_guard_checked", new
        {
            value.Bid,
            value.Ask,
            value.SpreadPips,
            maxSpreadPips = services.Options.MaxSpreadPips,
            maxSlippagePoints = services.Options.MT5.MaxSlippagePoints
        }, cancellationToken);

        if (formattedOutput)
        {
            PrintMarketExecutionGuard(value, services.Options.MaxSpreadPips, services.Options.MT5.MaxSlippagePoints);
        }

        if (value.Bid <= 0m || value.Ask <= 0m || value.Ask < value.Bid)
        {
            await services.Monitor.WriteAsync(cycleId, "market_guard_rejected", new
            {
                reason = "Invalid MT5 quote.",
                value.Bid,
                value.Ask
            }, cancellationToken);

            return Result.Failure($"Invalid MT5 quote. Bid={value.Bid}, Ask={value.Ask}.");
        }

        if (value.SpreadPips > services.Options.MaxSpreadPips)
        {
            await services.Monitor.WriteAsync(cycleId, "market_guard_rejected", new
            {
                reason = "Spread exceeded maximum.",
                value.SpreadPips,
                maxSpreadPips = services.Options.MaxSpreadPips
            }, cancellationToken);

            return Result.Failure($"Spread guard blocked order creation. Spread={decimal.Round(value.SpreadPips, 2)} pips, MaxAllowed={services.Options.MaxSpreadPips} pips.");
        }

        return Result.Success();
    }

    internal static async Task<Result> ValidatePendingLimitOrderPlacementAsync(
        ServiceRegistry services,
        string cycleId,
        TradeSignal signal,
        bool formattedOutput,
        CancellationToken cancellationToken)
    {
        if (!IsBroker(services.Options, "MT5"))
        {
            return Result.Success();
        }

        var snapshot = await services.MT5Bridge.GetMarketExecutionSnapshotAsync(signal.Symbol, cancellationToken);
        if (!snapshot.IsSuccess)
        {
            await services.Monitor.WriteAsync(cycleId, "pending_limit_guard_failed", new
            {
                snapshot.Error
            }, cancellationToken);

            if (formattedOutput)
            {
                PrintFailure("Pending Limit Guard", snapshot.Error!);
            }

            return Result.Failure(snapshot.Error!);
        }

        var value = snapshot.Value!;
        await services.Monitor.WriteAsync(cycleId, "pending_limit_guard_checked", new
        {
            signal.Symbol,
            direction = signal.Direction.ToString(),
            entryPrice = signal.EntryPrice,
            value.Bid,
            value.Ask
        }, cancellationToken);

        var entryDistancePips = CalculatePendingEntryDistancePips(signal.Direction, signal.EntryPrice, value, services.Options);
        await services.Monitor.WriteAsync(cycleId, "pending_order_distance_checked", new
        {
            signal.Symbol,
            direction = signal.Direction.ToString(),
            entryPrice = signal.EntryPrice,
            value.Bid,
            value.Ask,
            entryDistancePips,
            maxEntryDistancePips = services.Options.MT5.MaxEntryDistancePips
        }, cancellationToken);

        if (formattedOutput)
        {
            PrintPendingEntryDistance(entryDistancePips, services.Options.MT5.MaxEntryDistancePips);
        }

        string? rejectionReason = signal.Direction switch
        {
            TradeDirection.Buy when signal.EntryPrice >= value.Ask =>
                $"BUY LIMIT entry is no longer valid. Entry must be below current ask. Entry={signal.EntryPrice}, Ask={value.Ask}. No order created.",
            TradeDirection.Sell when signal.EntryPrice <= value.Bid =>
                $"SELL LIMIT entry is no longer valid. Entry must be above current bid. Entry={signal.EntryPrice}, Bid={value.Bid}. No order created.",
            _ when entryDistancePips > services.Options.MT5.MaxEntryDistancePips =>
                $"Pending entry is too far from current market. Distance={decimal.Round(entryDistancePips, 2)} pips, MaxAllowed={services.Options.MT5.MaxEntryDistancePips} pips. No order created.",
            _ => null
        };

        if (rejectionReason is null)
        {
            return Result.Success();
        }

        await services.Monitor.WriteAsync(cycleId, "pending_limit_guard_rejected", new
        {
            reason = rejectionReason,
            signal.Symbol,
            direction = signal.Direction.ToString(),
            entryPrice = signal.EntryPrice,
            value.Bid,
            value.Ask,
            entryDistancePips,
            maxEntryDistancePips = services.Options.MT5.MaxEntryDistancePips
        }, cancellationToken);

        if (formattedOutput)
        {
            PrintFailure("Pending Limit Guard", rejectionReason);
        }

        return Result.Failure(rejectionReason);
    }

    internal static async Task<Result> CleanupStalePendingOrdersAsync(
        ServiceRegistry services,
        string cycleId,
        bool formattedOutput,
        CancellationToken cancellationToken)
    {
        if (!IsBroker(services.Options, "MT5") || !services.Options.MT5.CancelStalePendingOrders)
        {
            return Result.Success();
        }

        var cleanup = await services.MT5Bridge.CancelStalePendingOrdersAsync(cancellationToken);
        if (!cleanup.IsSuccess)
        {
            await services.Monitor.WriteAsync(cycleId, "stale_order_cleanup_failed", new
            {
                cleanup.Error
            }, cancellationToken);

            if (formattedOutput)
            {
                PrintFailure("Stale Order Cleanup", cleanup.Error!);
            }

            return Result.Failure(cleanup.Error!);
        }

        if (formattedOutput)
        {
            PrintStaleOrderCleanup(cleanup.Value!);
        }

        await services.Monitor.WriteAsync(cycleId, "stale_order_cleanup_completed", new
        {
            cleanup.Value!.CheckedOrders,
            cleanup.Value.CancelledOrders,
            cleanup.Value.CancelledOrderIds
        }, cancellationToken);

        return Result.Success();
    }

    internal static async Task<Result<PendingOrderInvalidationResult>> InvalidatePendingOrdersAsync(
        ServiceRegistry services,
        string cycleId,
        TradeSignal signal,
        bool formattedOutput,
        CancellationToken cancellationToken)
    {
        if (!IsBroker(services.Options, "MT5"))
        {
            return Result<PendingOrderInvalidationResult>.Success(new PendingOrderInvalidationResult(0, 0, []));
        }

        var shouldCheckSetupInvalid = services.Options.MT5.CancelPendingOrderWhenSetupInvalid && !signal.IsValidSetup;
        var shouldCheckDirection = services.Options.MT5.CancelPendingOrderWhenDirectionChanges && signal.IsValidSetup;
        var shouldCheckDistance = services.Options.MT5.CancelPendingOrderWhenEntryDistanceExceedsPips > 0m;
        if (!shouldCheckSetupInvalid && !shouldCheckDirection && !shouldCheckDistance)
        {
            return Result<PendingOrderInvalidationResult>.Success(new PendingOrderInvalidationResult(0, 0, []));
        }

        var ordersResult = await services.MT5Bridge.GetPendingOrdersAsync(cancellationToken);
        if (!ordersResult.IsSuccess)
        {
            await services.Monitor.WriteAsync(cycleId, "pending_order_invalidation_failed", new
            {
                ordersResult.Error
            }, cancellationToken);

            if (formattedOutput)
            {
                PrintFailure("Pending Order Invalidation", ordersResult.Error!);
            }

            return Result<PendingOrderInvalidationResult>.Failure(ordersResult.Error!);
        }

        var orders = ordersResult.Value!;
        if (orders.Count == 0)
        {
            await services.Monitor.WriteAsync(cycleId, "pending_order_invalidation_checked", new
            {
                checkedOrders = 0,
                cancelledOrders = 0
            }, cancellationToken);
            return Result<PendingOrderInvalidationResult>.Success(new PendingOrderInvalidationResult(0, 0, []));
        }

        MarketExecutionSnapshot? market = null;
        if (shouldCheckDistance)
        {
            var snapshot = await services.MT5Bridge.GetMarketExecutionSnapshotAsync(services.Options.Symbol, cancellationToken);
            if (!snapshot.IsSuccess)
            {
                await services.Monitor.WriteAsync(cycleId, "pending_order_invalidation_failed", new
                {
                    snapshot.Error
                }, cancellationToken);

                if (formattedOutput)
                {
                    PrintFailure("Pending Order Invalidation", snapshot.Error!);
                }

                return Result<PendingOrderInvalidationResult>.Failure(snapshot.Error!);
            }

            market = snapshot.Value!;
        }

        var cancelled = new List<PendingOrderCancellation>();
        foreach (var order in orders)
        {
            var distancePips = market is null
                ? (decimal?)null
                : CalculatePendingEntryDistancePips(order.Direction, order.EntryPrice, market, services.Options);
            var reason = ResolvePendingOrderCancellationReason(services.Options, signal, order, distancePips);
            if (reason is null)
            {
                continue;
            }

            var cancelResult = await services.MT5Bridge.CancelPendingOrderAsync(order.Ticket, cancellationToken);
            if (!cancelResult.IsSuccess)
            {
                await services.Monitor.WriteAsync(cycleId, "pending_order_cancellation_failed", new
                {
                    order.Ticket,
                    reason,
                    cancelResult.Error
                }, cancellationToken);

                if (formattedOutput)
                {
                    PrintFailure("Pending Order Cancellation", cancelResult.Error!);
                }

                return Result<PendingOrderInvalidationResult>.Failure(cancelResult.Error!);
            }

            cancelled.Add(new PendingOrderCancellation(order.Ticket, reason, distancePips));
        }

        var result = new PendingOrderInvalidationResult(orders.Count, cancelled.Count, cancelled);
        await services.Monitor.WriteAsync(cycleId, "pending_order_invalidation_checked", new
        {
            result.CheckedOrders,
            result.CancelledOrders,
            cancellations = result.Cancellations.Select(item => new
            {
                item.Ticket,
                item.Reason,
                item.EntryDistancePips
            }).ToArray()
        }, cancellationToken);

        if (formattedOutput)
        {
            PrintPendingOrderInvalidation(result);
        }

        return Result<PendingOrderInvalidationResult>.Success(result);
    }

    internal static async Task<Result<ActiveTradeSummary>> GetActiveTradeSummaryAsync(ServiceRegistry services, CancellationToken cancellationToken)
    {
        if (IsBroker(services.Options, "MT5"))
        {
            return await services.MT5Bridge.GetActiveTradesAsync(cancellationToken);
        }

        var accessToken = services.Options.CTrader.AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return Result<ActiveTradeSummary>.Failure("cTrader access token is required for active trade check.");
        }

        var result = await services.CTraderJsonApi.GetActiveTradeSummaryAsync(accessToken, cancellationToken);
        return result.IsSuccess
            ? Result<ActiveTradeSummary>.Success(new ActiveTradeSummary(result.Value!.OpenPositions, result.Value.PendingOrders))
            : Result<ActiveTradeSummary>.Failure(result.Error!);
    }

    internal static int GetMaxActiveTrades(TradingBotOptions options) =>
        options.LowRiskRollout.Enabled
            ? Math.Min(IsBroker(options, "MT5") ? options.MT5.MaxActiveTrades : options.MaxActiveTrades, options.LowRiskRollout.MaxActiveTrades)
            : IsBroker(options, "MT5") ? options.MT5.MaxActiveTrades : options.MaxActiveTrades;

    internal static decimal CalculatePendingEntryDistancePips(
        TradeDirection direction,
        decimal entryPrice,
        MarketExecutionSnapshot snapshot,
        TradingBotOptions options)
    {
        if (options.PipSize <= 0m)
        {
            return 0m;
        }

        var distance = direction switch
        {
            TradeDirection.Buy => snapshot.Ask - entryPrice,
            TradeDirection.Sell => entryPrice - snapshot.Bid,
            _ => 0m
        };

        return Math.Max(0m, distance / options.PipSize);
    }

    internal static string? ResolvePendingOrderCancellationReason(
        TradingBotOptions options,
        TradeSignal signal,
        PendingBrokerOrder order,
        decimal? entryDistancePips)
    {
        if (options.MT5.CancelPendingOrderWhenSetupInvalid && !signal.IsValidSetup)
        {
            return $"Current setup is no longer valid. {signal.SetupReason}";
        }

        if (options.MT5.CancelPendingOrderWhenDirectionChanges
            && signal.IsValidSetup
            && order.Direction != signal.Direction)
        {
            return $"Pending order direction {order.Direction.ToString().ToUpperInvariant()} no longer matches current setup direction {signal.Direction.ToString().ToUpperInvariant()}.";
        }

        if (entryDistancePips is not null
            && entryDistancePips.Value > options.MT5.CancelPendingOrderWhenEntryDistanceExceedsPips)
        {
            return $"Pending entry is {decimal.Round(entryDistancePips.Value, 2)} pips from market, above cancel threshold {options.MT5.CancelPendingOrderWhenEntryDistanceExceedsPips} pips.";
        }

        return null;
    }

    internal static async Task<Result<TradeTrackingCycleResult>> TrackClosedTradesAsync(
        ServiceRegistry services,
        CancellationToken cancellationToken)
    {
        if (!IsBroker(services.Options, "MT5"))
        {
            return Result<TradeTrackingCycleResult>.Success(new TradeTrackingCycleResult(0, 0, services.Options.TradeTracking.Directory));
        }

        var to = DateTimeOffset.UtcNow;
        var from = to.AddDays(-services.Options.TradeTracking.LookbackDays);
        var trades = await services.MT5Bridge.GetClosedTradesAsync(from, to, cancellationToken);
        if (!trades.IsSuccess)
        {
            return Result<TradeTrackingCycleResult>.Failure(trades.Error!);
        }

        var written = await services.TradeTracker.AppendAsync(trades.Value!, cancellationToken);
        return Result<TradeTrackingCycleResult>.Success(new TradeTrackingCycleResult(
            trades.Value!.Count,
            written,
            services.Options.TradeTracking.Directory));
    }

    internal static async Task<Result<DailyTradingStopStatus>> ValidateDailyTradingStopAsync(
        ServiceRegistry services,
        string cycleId,
        CancellationToken cancellationToken)
    {
        var options = services.Options.DailyTradingStop;
        var now = DateTimeOffset.Now;
        var dayStart = new DateTimeOffset(now.Date, now.Offset);
        var dayEnd = dayStart.AddDays(1);

        if (!options.Enabled)
        {
            return Result<DailyTradingStopStatus>.Success(new DailyTradingStopStatus(
                false,
                0,
                0,
                options.MaxWinningTradesPerDay,
                options.MaxLosingTradesPerDay,
                dayStart,
                dayEnd,
                "Daily trading stop is disabled."));
        }

        if (!IsBroker(services.Options, "MT5"))
        {
            return Result<DailyTradingStopStatus>.Success(new DailyTradingStopStatus(
                false,
                0,
                0,
                options.MaxWinningTradesPerDay,
                options.MaxLosingTradesPerDay,
                dayStart,
                dayEnd,
                "Daily trading stop currently checks MT5 closed trades only."));
        }

        var trades = await services.MT5Bridge.GetClosedTradesAsync(dayStart, dayEnd, cancellationToken);
        if (!trades.IsSuccess)
        {
            return Result<DailyTradingStopStatus>.Failure(trades.Error!);
        }

        var todaysTrades = trades.Value!
            .Where(trade => trade.ClosedAt >= dayStart && trade.ClosedAt < dayEnd)
            .ToArray();
        var winningTrades = todaysTrades.Count(trade => trade.NetProfit > 0m);
        var losingTrades = todaysTrades.Count(trade => trade.NetProfit < 0m);
        var isWinLimitReached = winningTrades >= options.MaxWinningTradesPerDay;
        var isLossLimitReached = losingTrades >= options.MaxLosingTradesPerDay;
        var reason = isWinLimitReached
            ? $"Daily trading stopped after {winningTrades} winning trade(s)."
            : isLossLimitReached
                ? $"Daily trading stopped after {losingTrades} losing trade(s)."
                : "Daily trading stop limits not reached.";

        await services.Monitor.WriteAsync(cycleId, "daily_trading_stop_checked", new
        {
            winningTrades,
            losingTrades,
            options.MaxWinningTradesPerDay,
            options.MaxLosingTradesPerDay,
            dayStart,
            now,
            isHalted = isWinLimitReached || isLossLimitReached
        }, cancellationToken);

        return Result<DailyTradingStopStatus>.Success(new DailyTradingStopStatus(
            isWinLimitReached || isLossLimitReached,
            winningTrades,
            losingTrades,
            options.MaxWinningTradesPerDay,
            options.MaxLosingTradesPerDay,
            dayStart,
            dayEnd,
            reason));
    }

    internal static async Task<ForexMarketSessionStatus> ValidateForexMarketSessionAsync(
        ServiceRegistry services,
        string cycleId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var options = services.Options.ForexMarketSessions;
        var status = BuildForexMarketSessionStatus(services.Options, now);

        await services.Monitor.WriteAsync(cycleId, "forex_market_session_checked", new
        {
            status.IsOpen,
            session = status.Session.ToString(),
            status.NewYorkTime,
            status.Reason,
            options.TradingDays,
            options.LondonSessionNYTime,
            options.NewYorkSessionNYTime
        }, cancellationToken);

        return status;
    }

    internal static ForexMarketSessionStatus BuildForexMarketSessionStatus(TradingBotOptions botOptions, DateTimeOffset now)
    {
        var options = botOptions.ForexMarketSessions;
        var newYorkTime = ToNewYorkTime(now);
        if (!options.Enabled)
        {
            return new ForexMarketSessionStatus(
                true,
                SessionName.Closed,
                newYorkTime,
                "Forex market session guard is disabled.");
        }

        var tradingDays = options.TradingDays
            .Select(day => Enum.Parse<DayOfWeek>(day, ignoreCase: true))
            .ToHashSet();
        var isTradingDay = tradingDays.Contains(newYorkTime.DayOfWeek);
        var london = ParseSessionWindow(options.LondonSessionNYTime, "ForexMarketSessions:LondonSessionNYTime");
        var newYork = ParseSessionWindow(options.NewYorkSessionNYTime, "ForexMarketSessions:NewYorkSessionNYTime");
        var time = newYorkTime.TimeOfDay;
        var isLondonOpen = london.Contains(time);
        var isNewYorkOpen = newYork.Contains(time);
        var session = isLondonOpen && isNewYorkOpen
            ? SessionName.LondonNewYorkOverlap
            : isLondonOpen
                ? SessionName.London
                : isNewYorkOpen
                    ? SessionName.NewYork
                    : SessionName.Closed;
        var isOpen = isTradingDay && session != SessionName.Closed;
        var reason = isOpen
            ? $"Forex market session is open: {session}."
            : !isTradingDay
                ? $"Forex market session is closed because {newYorkTime.DayOfWeek} is not configured as a trading day."
                : "Forex market session is closed because current New York time is outside London and New York global sessions.";

        return new ForexMarketSessionStatus(isOpen, session, newYorkTime, reason);
    }

    internal static DateTimeOffset ToNewYorkTime(DateTimeOffset timestamp)
    {
        var nyZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        return TimeZoneInfo.ConvertTime(timestamp, nyZone);
    }

    internal static SessionWindow ParseSessionWindow(string value, string optionName)
    {
        var parts = value.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2
            || !TimeSpan.TryParse(parts[0], out var start)
            || !TimeSpan.TryParse(parts[1], out var end))
        {
            throw new InvalidOperationException($"{optionName} must use HH:mm-HH:mm format.");
        }

        return new SessionWindow(start, end);
    }

    internal static async Task<int> ExecuteAnalyzeAndCreateOrderAsync(
        ServiceRegistry services,
        bool requireLiveConfirmation,
        bool formattedOutput,
        bool showTokenRecoveryHint,
        CancellationToken cancellationToken)
    {
        if (!services.Options.LiveTradingEnabled)
        {
            Console.Error.WriteLine("LiveTradingEnabled=false blocks new orders.");
            return 2;
        }
        var cycleId = Guid.NewGuid().ToString("N");
        await services.Monitor.WriteAsync(cycleId, "cycle_started", new
        {
            requireLiveConfirmation,
            formattedOutput,
            analysisTimeUtc = DateTimeOffset.UtcNow,
            maxActiveTrades = GetMaxActiveTrades(services.Options),
            riskPercent = services.Options.RiskPercentPerTrade
        }, cancellationToken);

        if (IsBroker(services.Options, "MT5"))
        {
            if (!services.Options.MT5.AllowLiveOrderCreation && !requireLiveConfirmation)
            {
                await services.Monitor.WriteAsync(cycleId, "live_order_permission_rejected", new
                {
                    reason = "MT5 order creation requires MT5:AllowLiveOrderCreation=true or confirm_live_order=true."
                }, cancellationToken);
                Console.Error.WriteLine("MT5 order creation requires MT5:AllowLiveOrderCreation=true or confirm_live_order=true.");
                return 2;
            }
        }
        else if (!services.Options.CTrader.IsDemo && !services.Options.CTrader.AllowLiveOrderCreation && !requireLiveConfirmation)
        {
            await services.Monitor.WriteAsync(cycleId, "live_order_permission_rejected", new
            {
                reason = "Live-environment order creation requires CTrader:AllowLiveOrderCreation=true or confirm_live_order=true."
            }, cancellationToken);
            Console.Error.WriteLine("Live-environment order creation requires CTrader:AllowLiveOrderCreation=true or confirm_live_order=true.");
            return 2;
        }

        var analysisTime = DateTimeOffset.UtcNow;
        var forexMarketSession = await ValidateForexMarketSessionAsync(services, cycleId, analysisTime, cancellationToken);
        if (!forexMarketSession.IsOpen)
        {
            await services.Monitor.WriteAsync(cycleId, "forex_market_session_rejected", new
            {
                session = forexMarketSession.Session.ToString(),
                forexMarketSession.NewYorkTime,
                forexMarketSession.Reason
            }, cancellationToken);

            if (formattedOutput)
            {
                PrintCycleHeader(services.Options, new TradeSignal(
                    services.Options.Symbol,
                    TradeDirection.Buy,
                    0m,
                    0m,
                    0m,
                    0m,
                    forexMarketSession.Session,
                    false,
                    forexMarketSession.Reason));
                PrintForexMarketSession(forexMarketSession);
                PrintInvalidSetup(new TradeSignal(
                    services.Options.Symbol,
                    TradeDirection.Buy,
                    0m,
                    0m,
                    0m,
                    0m,
                    forexMarketSession.Session,
                    false,
                    forexMarketSession.Reason));
            }
            else
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    isOrderCreated = false,
                    reason = forexMarketSession.Reason,
                    forexMarketSession
                }, new JsonSerializerOptions { WriteIndented = true }));
            }

            await services.Monitor.WriteAsync(cycleId, "cycle_completed", new
            {
                isOrderCreated = false,
                reason = forexMarketSession.Reason,
                exitCode = 1
            }, cancellationToken);
            return 1;
        }

        var brokerResult = await PrepareConfiguredBrokerForMarketDataAsync(services, cancellationToken);
        if (!brokerResult.IsSuccess)
        {
            await services.Monitor.WriteAsync(cycleId, "broker_validation_failed", new
            {
                brokerResult.Error
            }, cancellationToken);

            if (formattedOutput)
            {
                PrintCycleHeader(services.Options, new TradeSignal(
                    services.Options.Symbol,
                    TradeDirection.Buy,
                    0m,
                    0m,
                    0m,
                    0m,
                    SessionName.Closed,
                    false,
                    "Broker session validation failed."));
                PrintFailure("Broker Session", brokerResult.Error!);
                if (!IsBroker(services.Options, "MT5") && showTokenRecoveryHint)
                {
                    PrintTokenRecoveryHint(brokerResult.Error!);
                }
                else if (!IsBroker(services.Options, "MT5"))
                {
                    PrintAuthorizationAlreadyRequested();
                }
            }
            else
            {
                Console.Error.WriteLine(brokerResult.Error);
            }

            await services.Monitor.WriteAsync(cycleId, "cycle_completed", new
            {
                isOrderCreated = false,
                reason = brokerResult.Error,
                exitCode = 3
            }, cancellationToken);
            return 3;
        }

        await services.Monitor.WriteAsync(cycleId, "broker_validation_succeeded", new { }, cancellationToken);

        var dailyTradingStop = await ValidateDailyTradingStopAsync(services, cycleId, cancellationToken);
        if (!dailyTradingStop.IsSuccess)
        {
            await services.Monitor.WriteAsync(cycleId, "daily_trading_stop_check_failed", new
            {
                dailyTradingStop.Error
            }, cancellationToken);

            if (formattedOutput)
            {
                PrintCycleHeader(services.Options, new TradeSignal(
                    services.Options.Symbol,
                    TradeDirection.Buy,
                    0m,
                    0m,
                    0m,
                    0m,
                    SessionName.Closed,
                    false,
                    "Daily trading stop check failed."));
                PrintFailure("Daily Trading Stop", dailyTradingStop.Error!);
            }
            else
            {
                Console.Error.WriteLine(dailyTradingStop.Error);
            }

            await services.Monitor.WriteAsync(cycleId, "cycle_completed", new
            {
                isOrderCreated = false,
                reason = dailyTradingStop.Error,
                exitCode = 4
            }, cancellationToken);
            return 4;
        }

        if (dailyTradingStop.Value!.IsHalted)
        {
            await services.Monitor.WriteAsync(cycleId, "daily_trading_halted", new
            {
                dailyTradingStop.Value.WinningTrades,
                dailyTradingStop.Value.LosingTrades,
                dailyTradingStop.Value.MaxWinningTrades,
                dailyTradingStop.Value.MaxLosingTrades,
                dailyTradingStop.Value.TradingDayStart,
                dailyTradingStop.Value.TradingDayEnd,
                dailyTradingStop.Value.Reason
            }, cancellationToken);

            if (formattedOutput)
            {
                PrintCycleHeader(services.Options, new TradeSignal(
                    services.Options.Symbol,
                    TradeDirection.Buy,
                    0m,
                    0m,
                    0m,
                    0m,
                    SessionName.Closed,
                    false,
                    dailyTradingStop.Value.Reason));
                PrintDailyTradingStop(dailyTradingStop.Value);
            }
            else
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    isOrderCreated = false,
                    reason = dailyTradingStop.Value.Reason,
                    dailyTradingStop = dailyTradingStop.Value
                }, new JsonSerializerOptions { WriteIndented = true }));
            }

            await services.Monitor.WriteAsync(cycleId, "cycle_completed", new
            {
                isOrderCreated = false,
                reason = dailyTradingStop.Value.Reason,
                exitCode = 1
            }, cancellationToken);
            return 1;
        }

        var signal = await services.Strategy.AnalyzeAsync(services.Options.Symbol, analysisTime, cancellationToken);
        await services.Monitor.WriteAsync(cycleId, "strategy_analyzed", new
        {
            signal.Symbol,
            direction = signal.Direction.ToString(),
            session = signal.Session.ToString(),
            signal.IsValidSetup,
            signal.SetupReason,
            signal.EntryPrice,
            signal.StopLoss,
            signal.TakeProfit,
            signal.RiskReward
        }, cancellationToken);

        ActiveTradeSummary? cycleActiveTradeSummary = null;
        if (formattedOutput)
        {
            PrintCycleHeader(services.Options, signal);
            PrintForexMarketSession(forexMarketSession);
            var brokerOrderStatus = await ReportBrokerOrderStatusAsync(services, cycleId, formattedOutput, cancellationToken);
            if (brokerOrderStatus.IsSuccess)
            {
                cycleActiveTradeSummary = brokerOrderStatus.Value;
            }
        }

        var pendingOrderInvalidation = await InvalidatePendingOrdersAsync(services, cycleId, signal, formattedOutput, cancellationToken);
        if (!pendingOrderInvalidation.IsSuccess)
        {
            if (!formattedOutput)
            {
                Console.Error.WriteLine(pendingOrderInvalidation.Error);
            }

            await services.Monitor.WriteAsync(cycleId, "cycle_completed", new
            {
                isOrderCreated = false,
                reason = pendingOrderInvalidation.Error,
                exitCode = 4
            }, cancellationToken);
            return 4;
        }

        if (pendingOrderInvalidation.Value!.CancelledOrders > 0)
        {
            cycleActiveTradeSummary = null;
        }

        if (!signal.IsValidSetup)
        {
            if (formattedOutput)
            {
                PrintInvalidSetup(signal);
            }
            else
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    isOrderCreated = false,
                    reason = "Strategy setup is not valid.",
                    signal = ToDto(signal)
                }, new JsonSerializerOptions { WriteIndented = true }));
            }

            await services.Monitor.WriteAsync(cycleId, "cycle_completed", new
            {
                isOrderCreated = false,
                reason = signal.SetupReason,
                exitCode = 1
            }, cancellationToken);
            return 1;
        }

        if (formattedOutput)
        {
            PrintValidSignal(signal);
        }

        var order = new OrderRequest(
            signal.Symbol,
            signal.Direction,
            OrderType.Limit,
            signal.EntryPrice,
            signal.StopLoss,
            signal.TakeProfit,
            0m,
            services.Options.RiskPercentPerTrade);

        var accountSnapshot = await GetAccountSnapshotAsync(services, cancellationToken);
        if (!accountSnapshot.IsSuccess)
        {
            await services.Monitor.WriteAsync(cycleId, "account_snapshot_failed", new
            {
                accountSnapshot.Error
            }, cancellationToken);

            if (formattedOutput)
            {
                PrintFailure("Account Risk State", accountSnapshot.Error!);
            }
            else
            {
                Console.Error.WriteLine(accountSnapshot.Error);
            }

            await services.Monitor.WriteAsync(cycleId, "cycle_completed", new
            {
                isOrderCreated = false,
                reason = accountSnapshot.Error,
                exitCode = 4
            }, cancellationToken);
            return 4;
        }

        var account = accountSnapshot.Value!;
        await services.Monitor.WriteAsync(cycleId, "account_snapshot_loaded", new
        {
            account.Balance,
            account.Equity,
            account.DailyRealizedProfitLoss,
            account.WeeklyRealizedProfitLoss,
            account.ConsecutiveLosses,
            account.ConsecutiveLosingDays,
            account.DailyStartingBalance,
            account.InitialBalance,
            account.TradingDays
        }, cancellationToken);

        var riskDecision = services.Risk.Evaluate(order, account);
        if (!riskDecision.IsAllowed)
        {
            await services.Monitor.WriteAsync(cycleId, "risk_rejected", new
            {
                riskDecision.Reason
            }, cancellationToken);

            if (formattedOutput)
            {
                PrintRiskRejected(riskDecision);
            }
            else
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    isOrderCreated = false,
                    reason = riskDecision.Reason,
                    signal = ToDto(signal)
                }, new JsonSerializerOptions { WriteIndented = true }));
            }

            await services.Monitor.WriteAsync(cycleId, "cycle_completed", new
            {
                isOrderCreated = false,
                reason = riskDecision.Reason,
                exitCode = 1
            }, cancellationToken);
            return 1;
        }

        await services.Monitor.WriteAsync(cycleId, "risk_accepted", new
        {
            riskDecision.PositionSize,
            riskDecision.Reason,
            services.Options.RiskPercentPerTrade
        }, cancellationToken);

        if (formattedOutput)
        {
            PrintRiskAccepted(riskDecision, services.Options.RiskPercentPerTrade);
        }

        var marketGuard = await ValidateMarketExecutionGuardAsync(services, cycleId, formattedOutput, cancellationToken);
        if (!marketGuard.IsSuccess)
        {
            if (!formattedOutput)
            {
                Console.Error.WriteLine(marketGuard.Error);
            }

            await services.Monitor.WriteAsync(cycleId, "cycle_completed", new
            {
                isOrderCreated = false,
                reason = marketGuard.Error,
                exitCode = 4
            }, cancellationToken);
            return 4;
        }

        var pendingLimitGuard = await ValidatePendingLimitOrderPlacementAsync(services, cycleId, signal, formattedOutput, cancellationToken);
        if (!pendingLimitGuard.IsSuccess)
        {
            if (!formattedOutput)
            {
                Console.Error.WriteLine(pendingLimitGuard.Error);
            }

            await services.Monitor.WriteAsync(cycleId, "cycle_completed", new
            {
                isOrderCreated = false,
                reason = pendingLimitGuard.Error,
                exitCode = 4
            }, cancellationToken);
            return 4;
        }

        var staleCleanup = await CleanupStalePendingOrdersAsync(services, cycleId, formattedOutput, cancellationToken);
        if (!staleCleanup.IsSuccess)
        {
            if (!formattedOutput)
            {
                Console.Error.WriteLine(staleCleanup.Error);
            }

            await services.Monitor.WriteAsync(cycleId, "cycle_completed", new
            {
                isOrderCreated = false,
                reason = staleCleanup.Error,
                exitCode = 4
            }, cancellationToken);
            return 4;
        }

        var activeTradeValidation = await ValidateActiveTradeLimitAsync(
            services,
            cycleId,
            formattedOutput,
            cancellationToken,
            cycleActiveTradeSummary,
            printSummary: cycleActiveTradeSummary is null);
        if (!activeTradeValidation.IsSuccess)
        {
            if (!formattedOutput)
            {
                Console.Error.WriteLine(activeTradeValidation.Error);
            }

            await services.Monitor.WriteAsync(cycleId, "cycle_completed", new
            {
                isOrderCreated = false,
                reason = activeTradeValidation.Error,
                exitCode = 4
            }, cancellationToken);
            return 4;
        }

        var orderResult = await CreateConfiguredBrokerOrderAsync(services, signal, riskDecision.PositionSize, cancellationToken);

        if (!orderResult.IsSuccess)
        {
            await services.Monitor.WriteAsync(cycleId, "order_creation_failed", new
            {
                orderResult.Error
            }, cancellationToken);

            if (formattedOutput)
            {
                PrintFailure("Order Creation", orderResult.Error!);
            }
            else
            {
                Console.Error.WriteLine(orderResult.Error);
            }

            await services.Monitor.WriteAsync(cycleId, "cycle_completed", new
            {
                isOrderCreated = false,
                reason = orderResult.Error,
                exitCode = 4
            }, cancellationToken);
            return 4;
        }

        if (formattedOutput)
        {
            PrintOrderCreated(orderResult.Value!, riskDecision);
        }
        else
        {
            var createdOrder = orderResult.Value!;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                isOrderCreated = true,
                signal = ToDto(signal),
                risk = new
                {
                    riskPercent = services.Options.RiskPercentPerTrade,
                    lotSize = riskDecision.PositionSize,
                    reason = riskDecision.Reason
                },
                order = new
                {
                    isAccepted = true,
                    createdOrder.ClientOrderId,
                    createdOrder.BrokerOrderId,
                    createdOrder.AccountId,
                    createdOrder.SymbolId,
                    createdOrder.TradeSide,
                    lots = createdOrder.Lots,
                    createdOrder.Volume,
                    createdOrder.LimitPrice,
                    createdOrder.StopLoss,
                    createdOrder.TakeProfit,
                    createdOrder.RiskReward
                }
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        await services.Monitor.WriteAsync(cycleId, "order_created", new
        {
            orderResult.Value!.ClientOrderId,
            orderResult.Value.BrokerOrderId,
            orderResult.Value.AccountId,
            orderResult.Value.TradeSide,
            orderResult.Value.Lots,
            orderResult.Value.Volume,
            orderResult.Value.LimitPrice,
            orderResult.Value.StopLoss,
            orderResult.Value.TakeProfit,
            orderResult.Value.RiskReward
        }, cancellationToken);

        await services.Monitor.WriteAsync(cycleId, "cycle_completed", new
        {
            isOrderCreated = true,
            exitCode = 0
        }, cancellationToken);

        return 0;
    }

    internal static async Task<Result<BrokerOrderCreationResult>> CreateConfiguredBrokerOrderAsync(
        ServiceRegistry services,
        TradeSignal signal,
        decimal lots,
        CancellationToken cancellationToken)
    {
        if (IsBroker(services.Options, "MT5"))
        {
            return await services.MT5Bridge.CreateLimitOrderAsync(signal, lots, cancellationToken);
        }

        var accessToken = services.Options.CTrader.AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return Result<BrokerOrderCreationResult>.Failure("cTrader access token is required for order creation.");
        }

        var result = await services.CTraderJsonApi.CreateLimitOrderAsync(
            signal.EntryPrice,
            lots,
            signal.Direction,
            signal.StopLoss,
            signal.TakeProfit,
            accessToken,
            cancellationToken);

        if (!result.IsSuccess)
        {
            return Result<BrokerOrderCreationResult>.Failure(result.Error!);
        }

        var value = result.Value!;
        return Result<BrokerOrderCreationResult>.Success(new BrokerOrderCreationResult(
            value.ClientOrderId,
            "",
            value.CtidTraderAccountId,
            value.SymbolId,
            value.TradeSide,
            lots,
            value.Volume,
            value.LimitPrice,
            value.StopLoss,
            value.TakeProfit,
            value.RiskReward));
    }

    internal static async Task<Result<AccountSnapshot>> GetAccountSnapshotAsync(ServiceRegistry services, CancellationToken cancellationToken)
    {
        var configuredInitialBalance = services.Options.ActiveFundedAccountChallenge?.InitialBalance ?? 0m;

        if (IsBroker(services.Options, "MT5"))
        {
            var snapshot = await services.MT5Bridge.GetAccountSnapshotAsync(cancellationToken);
            if (snapshot.IsSuccess && snapshot.Value!.InitialBalance <= 0m)
            {
                var value = snapshot.Value;
                snapshot = Result<AccountSnapshot>.Success(value with
                {
                    InitialBalance = configuredInitialBalance > 0m
                        ? configuredInitialBalance
                        : services.Options.AccountBalance
                });
            }

            return snapshot;
        }

        return Result<AccountSnapshot>.Success(new AccountSnapshot(
            services.Options.AccountBalance,
            services.Options.AccountBalance,
            0m,
            0m,
                    0,
                    0,
                    services.Options.AccountBalance,
                    configuredInitialBalance > 0m ? configuredInitialBalance : services.Options.AccountBalance,
                    0));
    }

}
