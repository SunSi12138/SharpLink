using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.Runtime;
using SharpLink.Server;

namespace WakeupNativeEvidence;

[RpcContract]
public interface INativeEcho : IService
{
    ValueTask<int> EchoAsync(int value, CancellationToken cancellationToken);
}

[RpcService]
public sealed class NativeEchoService : INativeEcho
{
    public ValueTask<int> EchoAsync(int value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(value ^ 0x13579);
    }
}

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length != 3)
            throw new ArgumentException("cell expected-process-architecture new-phase-jsonl");
        var cell = args[0];
        if (cell is not ("default-c1" or "default-c8" or "timed-c1" or "timed-c8"))
            throw new ArgumentException("Unknown frozen cell.");
        if (RuntimeInformation.ProcessArchitecture.ToString() != args[1] ||
            RuntimeInformation.OSArchitecture != RuntimeInformation.ProcessArchitecture ||
            Environment.Version.ToString() != "10.0.14")
            throw new InvalidOperationException("Architecture/runtime pin mismatch; emulated execution is not allowed.");
        using var ledger = new StreamWriter(new FileStream(args[2], FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        ledger.AutoFlush = true;
        Phase.Ledger = ledger;
        Phase.Cell = cell;
        Phase.Mark("process-start", 0);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(165));
        var builder = SharpLinkServerBuilder.Create().UseTcp(0, IPAddress.Loopback);
        var timed = cell.StartsWith("timed", StringComparison.Ordinal);
        if (timed) builder.UseRpcSessionFlush(64 * 1024, TimeSpan.FromTicks(2500));
        var port = ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
        await using var server = builder.Build();
        await server.StartAsync();
        var clientBuilder = SharpClientBuilder.Create().UseRequestTimeout().UseTcp("127.0.0.1", port);
        if (timed) clientBuilder.UseRpcSessionFlush(64 * 1024, TimeSpan.FromTicks(2500));
        await using var client = clientBuilder.Build();
        await client.ConnectAsync(deadline.Token);
        var rpc = client.Get<INativeEcho>();
        var workers = cell.EndsWith("c8", StringComparison.Ordinal) ? 8 : 1;
        var completed = 0;
        Phase.Mark("warmup-start", completed);
        completed += await Calls(rpc, workers, 20_000, deadline.Token);
        Phase.Mark("warmup-end", completed);
        // Frozen quiet interval, not an adaptive wait for a desired JIT outcome.
        await Task.Delay(250, deadline.Token);
        Phase.Mark("exercise-start", completed);
        completed += await Calls(rpc, workers, 20_000, deadline.Token);
        Phase.Mark("exercise-end", completed);
        await client.StopAsync();
        await server.StopAsync(TimeSpan.FromSeconds(10));
        if (server.LifecycleState != SharpLinkServerLifecycleState.Stopped || completed != 40_000)
            throw new InvalidOperationException("Incomplete RPC/physical-stop ledger.");
        Phase.Mark("process-end", completed);
        Console.WriteLine(JsonSerializer.Serialize(new {
            complete = true, diagnosticOnly = true, cell, completed,
            runtime = Environment.Version.ToString(), processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            osArchitecture = RuntimeInformation.OSArchitecture.ToString(), os = RuntimeInformation.OSDescription,
            processPath = Environment.ProcessPath, processorCount = Environment.ProcessorCount,
            serverGC = GCSettings.IsServerGC, latencyMode = GCSettings.LatencyMode.ToString(),
            abi = SharpLinkGeneratedManifestVersions.AbiIdentity,
            modules = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .OrderBy(a => a.GetName().Name, StringComparer.Ordinal).Select(a => new {
                    name = a.GetName().Name, location = a.Location, mvid = a.ManifestModule.ModuleVersionId,
                    version = a.GetName().Version?.ToString(),
                    informationalVersion = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                    sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))).ToLowerInvariant()
                })
        }));
    }

    private static async Task<int> Calls(INativeEcho rpc, int workers, int calls, CancellationToken token)
    {
        var tasks = new Task<int>[workers];
        for (var worker = 0; worker < workers; worker++)
        {
            var workerId = worker;
            tasks[worker] = Task.Run(async () => {
                var completed = 0;
                for (var index = workerId; index < calls; index += workers)
                {
                    var result = await rpc.EchoAsync(index, token).ConfigureAwait(false);
                    if (result != (index ^ 0x13579)) throw new InvalidOperationException("RPC checksum mismatch.");
                    completed++;
                }
                return completed;
            }, token);
        }
        return (await Task.WhenAll(tasks)).Sum();
    }
}

[EventSource(Name = "SharpLink-Wakeup-Native")]
internal sealed class NativeEvents : EventSource
{
    internal static readonly NativeEvents Log = new();
    [Event(1, Level = EventLevel.Informational)]
    public void Boundary(string boundary, string cell, int completed, long ticks, long frequency)
        => WriteEvent(1, boundary, cell, completed, ticks, frequency);
}
internal static class Phase
{
    internal static StreamWriter Ledger = null!;
    internal static string Cell = null!;
    internal static void Mark(string boundary, int completed)
    {
        var ticks = Stopwatch.GetTimestamp();
        NativeEvents.Log.Boundary(boundary, Cell, completed, ticks, Stopwatch.Frequency);
        Ledger.WriteLine(JsonSerializer.Serialize(new { boundary, cell = Cell, completed, ticks,
            frequency = Stopwatch.Frequency, pid = Environment.ProcessId }));
    }
}
