using System.Diagnostics;

namespace TradingBot.Application;

/// <summary>In-memory progress, independent of disk and event reporting.</summary>
public sealed class LiveProcessHealth
{
    private readonly object sync = new();
    private long? active, completed, successful;
    private string? error;
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");
    public void Begin() { lock (sync) active = Stopwatch.GetTimestamp(); }
    public void Complete(LiveProtectionSnapshot snapshot)
    {
        lock (sync)
        {
            completed = Stopwatch.GetTimestamp();
            active = null;
            error = snapshot.Errors is { Length: > 0 } ? "Protection actions incomplete" :
                !snapshot.AccountingAvailable ? "Accounting unavailable" : null;
            if (error is null) successful = completed;
        }
    }
    public void Fail() { lock (sync) { completed = Stopwatch.GetTimestamp(); active = null; error = "Protection request failed; entries paused"; } }
    public object Snapshot(long accountId, string startupId)
    {
        lock (sync) return new {
            processId = Environment.ProcessId, instanceId = InstanceId, startupId, accountId,
            operationAgeSeconds = Age(active), cycleAgeSeconds = Age(completed),
            protectionAgeSeconds = Age(successful), degraded = error is not null, error
        };
    }
    private static double? Age(long? value) => value is { } stamp ? Stopwatch.GetElapsedTime(stamp).TotalSeconds : null;
}
