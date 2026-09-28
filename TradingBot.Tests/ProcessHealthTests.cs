using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using TradingBot.Application;
using TradingBot.Infrastructure.MetaTrader;

internal static class ProcessHealthTests
{
    public static async Task Child(string[] args)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var health = new LiveProcessHealth();
        health.Begin();
        if (args[3] == "degraded") health.Fail();
        else if (args[3] == "halted") health.Complete(new(new(100000,100000,100000,0,0,0,0,null,0), true, "Daily risk halt"));
        await using var endpoint = new LiveHealthEndpoint(health, long.Parse(args[1]), args[2], () => stop.Cancel());
        try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
    }

    public static void Run()
    {
        foreach (var mode in new[] { "stalled", "degraded", "halted" })
        {
            Verify(mode).GetAwaiter().GetResult();
            Console.WriteLine("PASS Cross-process CLI health and authenticated shutdown: " + mode);
        }
    }

    private static async Task<JsonElement> Query(long account, string command)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var pipe = new NamedPipeClientStream(".", LiveHealthEndpoint.PipeName(account), PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(deadline.Token);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await writer.WriteLineAsync(command.AsMemory(), deadline.Token);
        return JsonDocument.Parse((await reader.ReadLineAsync(deadline.Token))!).RootElement.Clone();
    }

    private static async Task Verify(string mode)
    {
        var account = Random.Shared.NextInt64(8000000000, 9000000000);
        var nonce = Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(ProcessHealthTests).Assembly.Location);
        foreach (var arg in new[] { "--health-test-child", account.ToString(), nonce, mode }) start.ArgumentList.Add(arg);
        using var child = Process.Start(start)!;
        try
        {
            var first = await Query(account, "health");
            if (first.GetProperty("processId").GetInt32() != child.Id || first.GetProperty("startupId").GetString() != nonce)
                throw new Exception("Health identity mismatch.");
            if (mode == "stalled" && (first.GetProperty("operationAgeSeconds").ValueKind != JsonValueKind.Number || first.GetProperty("protectionAgeSeconds").ValueKind != JsonValueKind.Null))
                throw new Exception("Responsive endpoint fabricated protection heartbeat.");
            if (mode == "degraded" && !first.GetProperty("degraded").GetBoolean()) throw new Exception("Bridge outage not distinguishable.");
            if (mode == "halted" && (first.GetProperty("degraded").GetBoolean() || first.GetProperty("protectionAgeSeconds").ValueKind != JsonValueKind.Number))
                throw new Exception("Normal risk halt considered stalled.");
            await Query(account, "stop wrong-instance");
            if (child.HasExited) throw new Exception("Wrong nonce stopped child.");
            await Query(account, "stop " + nonce);
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await child.WaitForExitAsync(wait.Token);
            if (child.ExitCode != 0) throw new Exception("Graceful shutdown failed.");
        }
        finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
    }
}
