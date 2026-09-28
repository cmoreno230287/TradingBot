namespace TradingBot.Application;

/// <summary>Bound event I/O without allowing timed-out, noncooperative writes to overlap.</summary>
public sealed class ResilientLiveEvents(ILiveTradingEvents sink, TimeSpan timeout)
{
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly object stateLock = new();
    private bool healthy = true;
    private long failures, recoveryEpoch;
    private static int fallbackBusy;
    public bool IsHealthy { get { lock (stateLock) return healthy; } }
    public long RecoveryEpoch { get { lock (stateLock) return recoveryEpoch; } }

    public Task<bool> WriteAsync(string name, object value, CancellationToken token) =>
        RunAsync(t => sink.WriteAsync(name, value, t), token);

    public async Task<bool> RecoverAsync(CancellationToken token)
    {
        long observed;
        lock (stateLock) { if (healthy) return true; observed = failures; }
        if (!await WriteAsync("live_evidence_probe", new { checkedAtUtc = DateTimeOffset.UtcNow }, token)) return false;
        lock (stateLock)
        {
            if (failures != observed) return false;
            healthy = true;
            recoveryEpoch++;
            return true;
        }
    }

    private async Task<bool> RunAsync(Func<CancellationToken, Task> action, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        var operationToken = deadline.Token;
        try
        {
            await writer.WaitAsync(operationToken);
            // Invoke on a worker as file APIs or event sinks can block before returning a Task.
            var operation = Task.Run(async () =>
            {
                try { await action(operationToken); }
                finally { writer.Release(); }
            });
            _ = operation.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await operation.WaitAsync(operationToken);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            lock (stateLock) { healthy = false; failures++; }
            Fallback(error is OperationCanceledException ? "Live event persistence timed out; entries paused." :
                $"Live event persistence failed; entries paused: {error.Message}");
            return false;
        }
    }

    private static void Fallback(string message)
    {
        // Even stderr may be redirected to a stalled device. At most one fallback can be in flight.
        if (Interlocked.CompareExchange(ref fallbackBusy, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            try { Console.Error.WriteLine(message); }
            catch { /* No recursive logging from the fallback. */ }
            finally { Volatile.Write(ref fallbackBusy, 0); }
        });
    }
}
