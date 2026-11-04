using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime;
using System.Text.Json;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;
namespace MethodFactsExperiment;

public static class Program
{
    public static async Task Main(string[] args)
    {
        FactsService.QueuedGate = null;
        var metadata = new {
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            runtimeVersion = Environment.Version.ToString(),
            coreFrameworkLocation = typeof(object).Assembly.Location,
            processPath = Environment.ProcessPath,
            allowedCpuList = OperatingSystem.IsLinux()
                ? File.ReadLines("/proc/self/status").Single(line => line.StartsWith("Cpus_allowed_list:")).Split(':')[1].Trim()
                : null,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            processorCount = Environment.ProcessorCount,
            serverGC = GCSettings.IsServerGC,
            latencyMode = GCSettings.LatencyMode.ToString(),
            identity = SharpLinkGeneratedManifestVersions.AbiIdentity,
            binaryIdentities = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")
                .OrderBy(path => path, StringComparer.Ordinal).Select(ReadAssemblyIdentity),
            modules = new[] { typeof(IRpcStub).Assembly, typeof(SharpLinkServerBuilder).Assembly, typeof(SharpClientBuilder).Assembly, Assembly.Load("SharpLink.Runtime"), Assembly.Load("SharpLink.Sdk") }
                .Select(assembly => new { name = assembly.GetName().Name, version = assembly.GetName().Version!.ToString(),
                    mvid = assembly.ManifestModule.ModuleVersionId, location = assembly.Location,
                    informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion }),
            extraPerformanceEnvironment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .Where(entry => System.Text.RegularExpressions.Regex.IsMatch((string)entry.Key, @"(?i)^(COMPlus_|DOTNET_(GC|Tiered|TC_|Jit|ReadyToRun|PROCESSOR_COUNT))"))
                .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value),
            performanceEnvironment = new[] { "DOTNET_TieredCompilation", "DOTNET_TieredPGO", "DOTNET_TC_QuickJit", "DOTNET_GCServer", "DOTNET_GCConcurrent", "DOTNET_PROCESSOR_COUNT", "COMPlus_TieredCompilation", "COMPlus_TieredPGO", "COMPlus_gcServer", "COMPlus_gcConcurrent" }
                .ToDictionary(key => key, Environment.GetEnvironmentVariable)
        };
        Console.WriteLine(JsonSerializer.Serialize(new { mode = args[0], metadata }));
        if (args[0] == "describe") return;
        if (args[0] != "diagnostic" || args.Length != 2) throw new ArgumentException("Use describe, or diagnostic <new phases.jsonl>.");
        using var stream = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var output = new StreamWriter(stream) { AutoFlush = true };
        PhaseLog.Output = output;
        Check.That(PhaseEvents.Log.IsEnabled(), "phase provider must be enabled before any RPC work");
        PhaseLog.Mark("process", "process-start", -1, 0);
        var samples = await Network.RunAsync(output);
        PhaseLog.Mark("process", "process-end", -1, 44000);
        Console.WriteLine(JsonSerializer.Serialize(new { complete = true, mode = "diagnostic-only", chunks = samples, completedCalls = 44000 }));
    }
    private static object ReadAssemblyIdentity(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var assembly = reader.GetAssemblyDefinition();
        return new { file = Path.GetFileName(path), bytes = stream.Length,
            name = reader.GetString(assembly.Name), version = assembly.Version.ToString(),
            mvid = reader.GetGuid(reader.GetModuleDefinition().Mvid) };
    }

}
internal static class Check
{ internal static void That(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); } }
internal sealed class Passthrough : ISharpLinkServerInterceptor
{ public ValueTask InvokeAsync(SharpLinkServerInvocationContext context, SharpLinkServerInvocationDelegate next) => next(context); }
internal static class Network
{
    private const int Warmup = 1000;
    private const int Operations = 2000;
    private const int Samples = 5;
    internal static async Task<int> RunAsync(StreamWriter output)
    {
        var completedSamples = 0;
        foreach (var cell in new[] {
            (Name: "I/T", Mode: "interceptor", Tracked: true),
            (Name: "P/T", Mode: "plain", Tracked: true),
            (Name: "I/U", Mode: "interceptor", Tracked: false),
            (Name: "P/U", Mode: "plain", Tracked: false) })
        {
            PhaseLog.Mark(cell.Name, "cell-start", -1, 0);
            var builder = SharpLinkServerBuilder.Create();
            if (cell.Mode == "interceptor") builder.AddInterceptor(new Passthrough());
            builder.UseTcp(0, IPAddress.Loopback);
            var port = ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
            await using var server = builder.Build(); await server.StartAsync();
            var cb = SharpClientBuilder.Create().DisableRequestTimeout().UseTcp("127.0.0.1", port);
            await using var client = cb.Build();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await client.ConnectAsync(timeout.Token);
            var rpc = client.Get<IFactsRpc>();
            await RunCalls(rpc, cell.Mode, cell.Tracked, 1, 1, Warmup, null, timeout.Token);
            PhaseLog.Mark(cell.Name, "warmup-end", -1, Warmup);
            for (var sample = 0; sample < Samples; sample++)
            {
                PhaseLog.Mark(cell.Name, "chunk-start", sample, Warmup + sample * Operations);
                await RunCalls(rpc, cell.Mode, cell.Tracked, 1, 1, Operations, null, timeout.Token);
                PhaseLog.Mark(cell.Name, "chunk-end", sample, Warmup + (sample + 1) * Operations);
                completedSamples++;
            }
            await client.StopAsync(); await server.StopAsync(TimeSpan.FromSeconds(10));
            Check.That(server.LifecycleState == SharpLinkServerLifecycleState.Stopped, "server stopped after all complete stream results");
            PhaseLog.Mark(cell.Name, "cell-end", -1, Warmup + Samples * Operations);
        }
        return completedSamples;
    }
    private static async Task RunCalls(IFactsRpc rpc, string mode, bool tracked, int items, int concurrency, int operations,
        long[]? ticks, CancellationToken token)
    {
        var tasks = new Task[concurrency];
        for (var worker = 0; worker < concurrency; worker++) tasks[worker] = Worker(worker);
        await Task.WhenAll(tasks);
        async Task Worker(int worker)
        {
            for (var i = worker; i < operations; i += concurrency)
            {
                var start = Stopwatch.GetTimestamp(); var count = 0; var sum = 0;
                await foreach (var value in mode == "queued" ? rpc.Queued(items) : tracked ? rpc.Tracked(items, token) : rpc.Plain(items)) { count++; sum += value; }
                if (count != items || sum != items*(items+1)/2) throw new InvalidOperationException("network item/completion checksum");
                if (ticks is not null) ticks[i] = Stopwatch.GetTimestamp() - start;
            }
        }
    }
}


[EventSource(Name = "SharpLink-MethodFacts-Phases")]
internal sealed class PhaseEvents : EventSource
{
    internal static readonly PhaseEvents Log = new();
    [Event(1, Level = EventLevel.Informational)]
    public void Phase(int sequence, string cell, string boundary, int chunk, int completed, long timestamp, long frequency)
        => WriteEvent(1, sequence, cell, boundary, chunk, completed, timestamp, frequency);
}
internal static class PhaseLog
{
    internal static StreamWriter Output = null!;
    private static int Sequence;
    internal static void Mark(string cell, string boundary, int chunk, int completed)
    {
        var sequence = Sequence++;
        var timestamp = Stopwatch.GetTimestamp();
        PhaseEvents.Log.Phase(sequence, cell, boundary, chunk, completed, timestamp, Stopwatch.Frequency);
        Output.WriteLine(JsonSerializer.Serialize(new { sequence, cell, boundary, chunk, completed, timestamp, frequency = Stopwatch.Frequency }));
    }
}
