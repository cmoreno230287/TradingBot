using TradingBot.Domain;

namespace TradingBot.Application;

public sealed record LiveProtectionSnapshot(FtmoAccountState Account, bool Halted, string? Reason,
    string HaltCode = "Running", string[]? Actions = null, string[]? Errors = null, bool TargetReached = false,
    bool AccountingAvailable = true, string? AccountingError = null);
public sealed record LiveOrderSnapshot(FtmoAccountState Account, FtmoInstrument Instrument);
public sealed record LiveSubmission(bool Accepted, string? OrderId, string? Error, bool Retryable = false, string ExecutionState = "unknown");

public interface ILiveTradingBroker
{
    Task<LiveProtectionSnapshot> ProtectAsync(CancellationToken cancellationToken);
    Task<LiveOrderSnapshot> SnapshotAsync(TradeSignal signal, CancellationToken cancellationToken);
    Task<LiveSubmission> SubmitAsync(TradeSignal signal, decimal lots, CancellationToken cancellationToken);
}

public interface ILiveTradingEvents
{
    Task WriteAsync(string name, object value, CancellationToken cancellationToken);
    Task TrackAsync(CancellationToken cancellationToken);
    Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class LiveEntryPolicy
{
    public static string? BlockReason(TradingBotOptions options)
    {
        if (!options.LiveTradingEnabled || !options.MT5.AllowLiveOrderCreation) return "New entries are disabled.";
        if (!options.FtmoProtection.Enabled) return "Protected execution is required.";
        if (options.MT5.AccountId <= 0) return "A specific MT5 account ID is required.";
        if (!options.FtmoProtection.RulesConfirmed) return "Account rules remain unconfirmed.";
        if (options.FtmoProtection.LossModel != "Static" || options.FtmoProtection.ResetTimeZone != "Europe/Prague")
            return "Unsupported challenge loss model/reset timezone.";
        if (options.FtmoProtection.AccountVariant is not ("Challenge" or "Verification" or "FreeTrial"))
            return "Confirm a supported account variant and its static loss rules.";
        return null;
    }
}

/// <summary>Independent protection, entry and reporting schedules; no simulated execution path.</summary>
public sealed class LiveTradingCoordinator(TradingBotOptions options, IStrategyEngine strategy,
    ILiveTradingBroker broker, ILiveTradingEvents events, TimeProvider? timeProvider = null, LiveProcessHealth? processHealth = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ResilientLiveEvents evidence = new(events, TimeSpan.FromSeconds(options.FtmoProtection.EvidenceTimeoutSeconds));
    private sealed record Health(DateTimeOffset CheckedAt, LiveProtectionSnapshot Snapshot, long EvidenceEpoch);
    private Health? health;
    private readonly HashSet<string> attempts = new(StringComparer.Ordinal);
    private readonly HashSet<string> validSetups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Count, DateTimeOffset Next)> retries = new(StringComparer.Ordinal);
    private Task<TradeSignal>? inFlightAnalysis;
    private Task<bool>? killFileProbe;
    private volatile bool killFileAvailable = true;

    public async Task RunAsync(bool once, CancellationToken cancellationToken)
    {
        if (once)
        {
            await ProtectAsync(cancellationToken);
            await events.RefreshAsync(cancellationToken);
            await AnalyzeAsync(cancellationToken);
            return;
        }
        // Each loop starts independently; event failures pause entries without stopping protection.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        async Task Supervise(Func<CancellationToken, Task> action, int interval)
        {
            try { await LoopAsync(action, interval, stop.Token); }
            finally { await stop.CancelAsync(); }
        }
        await Task.WhenAll(
            Task.Run(() => Supervise(ProtectAsync, options.FtmoProtection.MonitorIntervalSeconds), stop.Token),
            Task.Run(() => Supervise(AnalyzeAsync, options.FtmoProtection.EntryAnalysisIntervalSeconds), stop.Token),
            Task.Run(() => Supervise(events.TrackAsync, 60), stop.Token),
            Task.Run(() => Supervise(events.RefreshAsync, 30), stop.Token));
    }

    private async Task LoopAsync(Func<CancellationToken, Task> action, int seconds, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds), clock);
        while (!token.IsCancellationRequested)
        {
            try { await action(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception e) { await evidence.WriteAsync("live_cycle_failed", new { action = action.Method.Name, error = e.Message }, token); }
            try { if (!await timer.WaitForNextTickAsync(token)) return; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        }
    }

    private async Task ProtectAsync(CancellationToken token)
    {
        processHealth?.Begin();
        var start = clock.GetUtcNow();
        var evidenceEpoch = evidence.RecoveryEpoch;
        try
        {
            if (!options.FtmoProtection.KillSwitch)
            {
                // Storage cannot hold up the broker protection call indefinitely.
                killFileProbe ??= Task.Run(() => {
                    try { _ = File.GetAttributes(Path.Combine(options.FtmoProtection.StateDirectory, "kill.switch")); return true; }
                    catch (FileNotFoundException) { return false; }
                    catch (DirectoryNotFoundException) { return false; }
                });
                try {
                    options.FtmoProtection.KillSwitch |= await killFileProbe.WaitAsync(TimeSpan.FromSeconds(1), token);
                    killFileProbe = null;
                    killFileAvailable = true;
                } catch (TimeoutException) { killFileAvailable = false; }
                  catch (Exception) when (!token.IsCancellationRequested) { killFileProbe = null; killFileAvailable = false; }
            }
            var snapshot = await broker.ProtectAsync(token);
            processHealth?.Complete(snapshot);
            Volatile.Write(ref health, new Health(start, snapshot, evidenceEpoch));
            LiveConsole.WriteProtection(snapshot, clock.GetUtcNow() - start);
            await evidence.WriteAsync("live_protection", new { snapshot, checkedAt = start,
                latencyMilliseconds = (clock.GetUtcNow() - start).TotalMilliseconds }, token);
        }
        catch { processHealth?.Fail(); Volatile.Write(ref health, null); throw; }
    }

    private string? EntryBlock()
    {
        var permission = LiveEntryPolicy.BlockReason(options);
        if (permission is not null) return permission;
        if (!killFileAvailable) return "Kill-switch storage check unavailable; entries paused.";
        if (!evidence.IsHealthy) return "Required event persistence is unavailable; entries paused.";
        var current = Volatile.Read(ref health);
        if (current is null || clock.GetUtcNow() - current.CheckedAt > TimeSpan.FromSeconds(options.FtmoProtection.MaximumProtectionAgeSeconds))
            return "Protection snapshot is stale or unavailable.";
        if (current.EvidenceEpoch != evidence.RecoveryEpoch) return "Persistence recovered; awaiting fresh protection and reconciliation.";
        if (!current.Snapshot.AccountingAvailable) return "Historical risk accounting is unavailable.";
        return current.Snapshot.Halted ? current.Snapshot.Reason ?? "Protection halted." : null;
    }

    private async Task AnalyzeAsync(CancellationToken token)
    {
        await evidence.RecoverAsync(token);
        if (EntryBlock() is { } blocked) { LiveConsole.WriteBlocked(options, blocked); await evidence.WriteAsync("live_entry_blocked", new { reason = blocked }, token); return; }
        if (inFlightAnalysis is { IsCompleted: false })
        {
            await evidence.WriteAsync("live_entry_blocked", new { reason = "Previous analysis has not stopped; overlapping analysis is blocked." }, token);
            return;
        }
        _ = inFlightAnalysis?.Exception; // Observe a late failure from an analysis that ignored cancellation.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.FtmoProtection.AnalysisTimeoutSeconds));
        var analysisTime = clock.GetUtcNow();
        var analysisToken = deadline.Token;
        inFlightAnalysis = Task.Run(() => strategy.AnalyzeAsync(options.Symbol, analysisTime, analysisToken), analysisToken);
        var signal = await inFlightAnalysis.WaitAsync(analysisToken);
        LiveConsole.WriteAnalysis(options, signal, Volatile.Read(ref health)?.Snapshot);
        if (!await evidence.WriteAsync("live_signal", signal, token)) return;
        if (signal.IsValidSetup && signal.SetupId is { } setupId) validSetups.Add(setupId);
        await evidence.WriteAsync("live_activity", new { scope = "process", uniqueValidSetups = validSetups.Count,
            submissionAttempts = attempts.Count, accountTradesToday = Volatile.Read(ref health)?.Snapshot.Account.TradesToday }, token);
        if (!signal.IsValidSetup || string.IsNullOrWhiteSpace(signal.SetupId) || attempts.Contains(signal.SetupId)) return;
        if (retries.TryGetValue(signal.SetupId, out var retry) &&
            (retry.Count >= options.FtmoProtection.MaximumSubmissionAttempts || clock.GetUtcNow() < retry.Next)) return;
        if (EntryBlock() is { } afterAnalysis) { await evidence.WriteAsync("live_entry_blocked", new { reason = afterAnalysis }, token); return; }
        var snapshot = await broker.SnapshotAsync(signal, token);
        var decision = new FtmoRiskEngine(options.FtmoProtection).Evaluate(signal, snapshot.Account, snapshot.Instrument, clock.GetUtcNow());
        if (!await evidence.WriteAsync("live_risk", new { signal.SetupId, decision }, token)) return;
        if (!decision.Allowed) return;
        if (EntryBlock() is { } finalBlock) { await evidence.WriteAsync("live_entry_blocked", new { signal.SetupId, reason = finalBlock }, token); return; }
        // Reserve locally as well as durably at the bridge, including ambiguous responses.
        attempts.Add(signal.SetupId);
        retries[signal.SetupId] = (retry.Count + 1, clock.GetUtcNow().AddSeconds(options.FtmoProtection.SubmissionRetryDelaySeconds));
        var submission = await broker.SubmitAsync(signal, decision.Lots, token);
        if (!submission.Accepted && submission.Retryable) attempts.Remove(signal.SetupId);
        await evidence.WriteAsync(submission.Accepted ? "order_created" : "order_rejected", new { signal.SetupId, submission }, token);
    }
}



