using System.IO.Pipes;
using System.Text.Json;
using TradingBot.Application;

namespace TradingBot.Infrastructure.MetaTrader;

/// <summary>Same-user local health endpoint. No file, broker or logging calls.</summary>
public sealed class LiveHealthEndpoint : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Task server;
    public static string PipeName(long accountId) => $"TradingBot.Health.{accountId}";
    public LiveHealthEndpoint(LiveProcessHealth health, long accountId, string startupId, Action shutdown)
    {
        var first = Create(accountId); // Bind before trading starts; failure cannot silently disable monitoring.
        server = Task.Run(async () => {
            var pipe = first;
            try {
                while (!stop.IsCancellationRequested) {
                    using (pipe) {
                        await pipe.WaitForConnectionAsync(stop.Token);
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                        deadline.CancelAfter(TimeSpan.FromSeconds(2));
                        try {
                            using var reader = new StreamReader(pipe, leaveOpen: true);
                            var command = await reader.ReadLineAsync(deadline.Token);
                            // The startup nonce prevents a stale supervisor from stopping a new instance.
                            if (command == "stop " + startupId && !string.IsNullOrEmpty(startupId)) shutdown();
                            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                            await writer.WriteLineAsync(JsonSerializer.Serialize(health.Snapshot(accountId, startupId)).AsMemory(), deadline.Token);
                        } catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }
                          catch (IOException) { }
                    }
                    pipe = Create(accountId);
                }
            } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            finally { pipe.Dispose(); }
        });
    }
    private static NamedPipeServerStream Create(long accountId) => new(PipeName(accountId), PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    public async ValueTask DisposeAsync() { stop.Cancel(); try { await server; } finally { stop.Dispose(); } }
}
