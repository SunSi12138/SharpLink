using System.Reflection;
using System.Text.Json;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("Server", "MoveNext")]
    [Arguments("Server", "Dispose")]
    [Arguments("Duplex", "MoveNext")]
    [Arguments("Duplex", "Dispose")]
    public async Task PublicDeadlineShouldRetainPhysicalOutputTail(string shape, string tail)
    {
        // Only the client clock is frozen: the receiver's original real two-second
        // deadline must produce the wire terminal, rather than the client's timer.
        await using var harness = await DynamicHarness.CreateAsync(
            clientTimeProvider: new PhysicalTailClientClock(), requestTimeout: TimeSpan.FromSeconds(2));
        using var plugin = PluginBundle.Load($"physical-tail-{shape}-{tail}");
        var service = plugin.ServiceAssembly.GetType("SharpLink.DynamicPlugin.PhysicalTailAuditService", true)!;
        var codec = plugin.GetContractType("SharpLink.DynamicPlugin.TailAuditCodec");
        var contract = plugin.GetContractType("SharpLink.DynamicPlugin.IPhysicalTailAudit");
        var item = plugin.GetContractType("SharpLink.DynamicPlugin.TailAuditItem");
        service.GetProperty("Mode")!.SetValue(null, tail);
        await RegisterAllAsync(harness, plugin);
        await WaitForRemoteContractManifestAsync(harness.Client, contract);
        var proxy = GetProxy(harness.Client, contract);
        var consume = typeof(RuntimeAssemblyIntegrationTests).GetMethod(nameof(ConsumePhysicalTail),
            BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(item);
        Task Invoke(string method, params object[] arguments)
            => (Task)consume.Invoke(null, [contract.GetMethod(method)!.Invoke(proxy, arguments)!])!;
        Task Signal(string name) => ((TaskCompletionSource)service.GetField(name)!.GetValue(null)!).Task;
        int Count(string name) => (int)service.GetField(name)!.GetValue(null)!;
        int CodecCount(string name) => (int)codec.GetField(name)!.GetValue(null)!;
        CancellationToken Token(string name) => (CancellationToken)service.GetField(name)!.GetValue(null)!;
        var observations = new List<object>();
        var timer = Stopwatch.StartNew();
        SharpLinkDynamicModule? module = null;
        Task? call = null;
        Exception? terminal = null;
        SharpLinkAssemblyUnregisterResult? removal = null;
        bool held = false, premature = false, releasedAfter = false;
        object Snapshot(string phase) => new
        {
            phase, elapsedMs = timer.Elapsed.TotalMilliseconds, shape, tail,
            moduleState = module?.State.ToString(), calls = module?.RemainingCalls, streams = module?.RemainingStreams,
            activeMove = Count("ActiveMove"), activeDispose = Count("ActiveDispose"),
            serviceDisposed = Count("ServiceDisposed"), tailSawDisposedService = Count("TailSawDisposedService"),
            moveExited = Signal("MoveExited").IsCompleted, disposeExited = Signal("DisposeExited").IsCompleted,
            callToken = PhysicalTailTokenSnapshot(Token("CallToken")),
            enumeratorToken = PhysicalTailTokenSnapshot(Token("EnumeratorToken")),
            codecCreated = CodecCount("Created"), codecDisposed = CodecCount("Disposed"),
            codecSerialized = CodecCount("Serialized"), codecDeserialized = CodecCount("Deserialized"),
            framework = ((SharpLinkServer)harness.Server).FrameworkTaskSnapshotForDiagnostics,
            deferred = ((SharpLinkServer)harness.Server).DeferredTaskSnapshotForDiagnostics,
            serverActive = ServerCallAdmissionDiagnostics.ActiveCallCount(harness.Server)
        };
        try
        {
            await Invoke("WarmupAsync", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(CodecCount("Serialized") == 1 && CodecCount("Deserialized") == 1,
                "healthy public generated stream must exercise the actual custom codec on both endpoints");
            module = GetDynamicModule(harness.Server, plugin.ServiceAssembly);
            call = shape == "Server" ? Invoke("ServerAsync", CancellationToken.None)
                : Invoke("DuplexAsync", PhysicalTailEmptyInput(), CancellationToken.None);
            await Signal("MoveEntered").WaitAsync(TimeSpan.FromSeconds(5));
            observations.Add(Snapshot("user-move-entered"));
            try { await call.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) { terminal = error; }
            Ensure(terminal is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
                $"receiver must deliver DeadlineExceeded before user gate release, actual {terminal}");
            await Signal("DisposeEntered").WaitAsync(TimeSpan.FromSeconds(5));
            if (tail == "Dispose") await Signal("MoveExited").WaitAsync(TimeSpan.FromSeconds(5));
            else await Signal("DisposeExited").WaitAsync(TimeSpan.FromSeconds(5));
            held = tail == "MoveNext" ? Count("ActiveMove") == 1 && !Signal("MoveExited").IsCompleted
                : Count("ActiveDispose") == 1 && !Signal("DisposeExited").IsCompleted;
            Ensure(held, "specified real producer tail must remain physically held after logical deadline");
            observations.Add(Snapshot("logical-deadline-tail-held"));
            removal = await harness.Server.UnregisterAssemblyAsync(plugin.ServiceAssembly, TimeSpan.FromSeconds(2))
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            observations.Add(new { phase = "unregister-result", elapsedMs = timer.Elapsed.TotalMilliseconds, result = removal });
            observations.Add(Snapshot("unregister-tail-still-held"));
            premature = removal.Value.ReferencesReleased || Count("ServiceDisposed") != 0 ||
                module.RemainingCalls == 0 || module.RemainingStreams == 0;
        }
        finally
        {
            service.GetMethod("Release")!.Invoke(null, null);
            try
            {
                if (call is not null)
                {
                    await Signal("MoveExited").WaitAsync(TimeSpan.FromSeconds(5));
                    await Signal("DisposeExited").WaitAsync(TimeSpan.FromSeconds(5));
                }
                if (module is not null && removal is not null)
                {
                    await WaitUntilAsync(() => module.State == SharpLinkDynamicModuleState.Released);
                    releasedAfter = module.RemainingCalls == 0 && module.RemainingStreams == 0 && Count("ServiceDisposed") == 1;
                }
                observations.Add(Snapshot("released-user-gates"));
                if (removal is null)
                    observations.Add(new { phase = "cleanup-service", result = await harness.Server.UnregisterAssemblyAsync(plugin.ServiceAssembly, TimeSpan.FromSeconds(2)) });
                observations.Add(new { phase = "cleanup-server-contract", result = await harness.Server.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.FromSeconds(2)) });
                observations.Add(new { phase = "cleanup-client-contract", result = await harness.Client.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.FromSeconds(2)) });
                observations.Add(Snapshot("contract-codecs-cleaned"));
            }
            finally
            {
                Console.WriteLine("PHYSICAL_TAIL_BASELINE_V1 " + JsonSerializer.Serialize(new
                {
                    subject = "2c77ffb55b10c111abea8fac0c128c1ac2dc93ad", runtime = Environment.Version.ToString(),
                    shape, tail, held, premature,
                    releasedAfter, terminal = terminal?.ToString(), observations
                }));
            }
        }
        Ensure(held && releasedAfter, "baseline phases and post-release convergence must be complete");
        Ensure(!premature, "PHYSICAL_OUTPUT_TAIL_V1: logical deadline must not release the original service module while its output tail remains physical");
    }

    private static async Task ConsumePhysicalTail<T>(object source)
    {
        await foreach (var _ in (IAsyncEnumerable<T>)source) { }
    }
    private static async IAsyncEnumerable<int> PhysicalTailEmptyInput()
    {
        await Task.CompletedTask;
        yield break;
    }
    private static object PhysicalTailTokenSnapshot(CancellationToken token)
    {
        var source = typeof(CancellationToken).GetField("_source", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(token);
        var disposed = source is null ? (bool?)null : (bool)typeof(CancellationTokenSource)
            .GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;
        return new { token.CanBeCanceled, token.IsCancellationRequested, sourceExists = source is not null, disposed };
    }
    private sealed class PhysicalTailClientClock : TimeProvider
    {
        public override long GetTimestamp() => 0;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }
}
