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
        => await VerifyPhysicalOutputTailAsync(shape, tail, "Singleton");

    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PerCallOutputDeadlineShouldRetainPhysicalOwner(bool cleanupFailure)
        => await VerifyPhysicalOutputTailAsync("Server", "MoveNext", "Call", cleanupFailure);

    [Test]
    [NotInParallel]
    public async Task CallerCancellationShouldRetainPhysicalOutputTail()
        => await VerifyPhysicalOutputTailAsync("Server", "MoveNext", "Singleton", callerCancel: true);

    [Test]
    [NotInParallel]
    public async Task HeldOutputTailShouldNotExtendServerStopBudget()
        => await VerifyPhysicalOutputTailAsync("Server", "MoveNext", "Singleton", stopWhileHeld: true);

    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CompilerOutputIteratorShouldRunItsPhysicalFinallyOnce(bool lateFault)
        => await VerifyPhysicalOutputTailAsync("Compiler", "MoveNext", "Singleton", compilerLateFault: lateFault);

    private static async Task VerifyPhysicalOutputTailAsync(
        string shape, string tail, string lifetime, bool cleanupFailure = false, bool callerCancel = false, bool stopWhileHeld = false, bool compilerLateFault = false)
    {
        // Pause the client's time AND timers until receiver expiry is independently
        // observed. Server deadline selection does not promise a wire DeadlineExceeded.
        var clientClock = new SharpLink.UnitTests.Runtime.ManualTimeProvider(DateTimeOffset.UnixEpoch);
        await using var harness = await DynamicHarness.CreateAsync(
            clientTimeProvider: clientClock, requestTimeout: TimeSpan.FromSeconds(2));
        var weak = await ExercisePhysicalOutputTailAsync(harness, clientClock, shape, tail, lifetime, cleanupFailure, callerCancel, stopWhileHeld, compilerLateFault)
            .ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        for (var attempt = 0; attempt < 10 && weak.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Yield();
        }
        Ensure(!weak.IsAlive, "retired physical-output plugin must collect after its original physical owners exit");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> ExercisePhysicalOutputTailAsync(
        DynamicHarness harness, SharpLink.UnitTests.Runtime.ManualTimeProvider clientClock,
        string shape, string tail, string lifetime, bool cleanupFailure, bool callerCancel, bool stopWhileHeld, bool compilerLateFault)
    {
        using var plugin = PluginBundle.Load($"physical-tail-{shape}-{tail}");
        var service = plugin.ServiceAssembly.GetType("SharpLink.DynamicPlugin.PhysicalTailAuditServiceBase", true)!;
        var codec = plugin.GetContractType("SharpLink.DynamicPlugin.TailAuditCodec");
        var contract = plugin.GetContractType(lifetime == "Call"
            ? "SharpLink.DynamicPlugin.IPhysicalTailCallAudit" : "SharpLink.DynamicPlugin.IPhysicalTailAudit");
        var item = plugin.GetContractType("SharpLink.DynamicPlugin.TailAuditItem");
        service.GetProperty("Mode")!.SetValue(null, tail);
        service.GetProperty("ThrowOnServiceDispose")!.SetValue(null, cleanupFailure);
        service.GetProperty("CompilerLateFault")!.SetValue(null, compilerLateFault);
        using var callerCancellation = new CancellationTokenSource();
        await RegisterAllAsync(harness, plugin);
        var originalServerContract = GetDynamicModule(harness.Server, plugin.ContractAssembly);
        await WaitForRemoteContractManifestAsync(harness.Client, contract);
        var proxy = await PrepareGeneratedServiceAsync(harness.Client, contract);
        var consume = typeof(RuntimeAssemblyIntegrationTests).GetMethod(nameof(ConsumePhysicalTail),
            BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(item);
        Task Invoke(string method, params object[] arguments)
            => (Task)consume.Invoke(null, [contract.GetMethod(method)!.Invoke(proxy, arguments)!])!;
        Task Signal(string name) => ((TaskCompletionSource)service.GetProperty(name)!.GetValue(null)!).Task;
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
        long? serverRequestId = null, serverInitialRemainingTicks = null;
        var serverCancellationCause = new TaskCompletionSource<PhysicalTailServerCancellation>(TaskCreationOptions.RunContinuationsAsynchronously);
        object Snapshot(string phase) => new
        {
            phase,
            elapsedMs = timer.Elapsed.TotalMilliseconds,
            shape,
            tail,
            lifetime,
            cleanupFailure,
            callerCancel,
            stopWhileHeld,
            moduleState = module?.State.ToString(),
            calls = module?.RemainingCalls,
            streams = module?.RemainingStreams,
            activeMove = Count("ActiveMove"),
            activeDispose = Count("ActiveDispose"),
            serviceDisposed = Count("ServiceDisposed"),
            tailSawDisposedService = Count("TailSawDisposedService"),
            otherServiceDisposed = Count("OtherServiceDisposed"),
            enumeratorDisposals = Count("EnumeratorDisposals"),
            compilerFinallyEntries = Count("CompilerFinallyEntries"),
            compilerLateFault,
            serviceDisposeFailures = Count("ServiceDisposeFailures"),
            moveExited = Signal("MoveExited").IsCompleted,
            disposeExited = Signal("DisposeExited").IsCompleted,
            callToken = PhysicalTailTokenSnapshot(Token("CallToken")),
            enumeratorToken = PhysicalTailTokenSnapshot(Token("EnumeratorToken")),
            codecCreated = CodecCount("Created"),
            codecDisposed = CodecCount("Disposed"),
            codecSerialized = CodecCount("Serialized"),
            codecDeserialized = CodecCount("Deserialized"),
            framework = ((SharpLinkServer)harness.Server).FrameworkTaskSnapshotForDiagnostics,
            deferred = ((SharpLinkServer)harness.Server).DeferredTaskSnapshotForDiagnostics,
            serverActive = ServerCallAdmissionDiagnostics.ActiveCallCount(harness.Server),
            clientPending = ((SharpLinkClient)harness.Client).PendingCallCount,
            clientClockTimestamp = clientClock.GetTimestamp(),
            clientClockTimers = clientClock.ActiveTimerCount,
            serverRequestId,
            serverInitialRemainingTicks,
            serverCancellationCause = serverCancellationCause.Task.IsCompletedSuccessfully ? serverCancellationCause.Task.Result : null
        };
        try
        {
            await Invoke("WarmupAsync", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(CodecCount("Serialized") == 1 && CodecCount("Deserialized") == 1,
                "healthy public generated stream must exercise the actual custom codec on both endpoints");
            module = GetDynamicModule(harness.Server, plugin.ServiceAssembly);
            var callToken = callerCancel ? callerCancellation.Token : CancellationToken.None;
            call = shape == "Compiler" ? Invoke("CompilerAsync", callToken)
                : shape == "Server" ? Invoke("ServerAsync", callToken)
                : Invoke("DuplexAsync", PhysicalTailEmptyInput(), callToken);
            await Signal("MoveEntered").WaitAsync(TimeSpan.FromSeconds(5));
            var connections = ((ServerConnectionRegistry)typeof(SharpLinkServer).GetField("_connectionRegistry",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(harness.Server)!).SnapshotActive();
            Ensure(connections.Length == 1, "deadline sampling must observe the sole real public connection");
            var connection = connections[0];
            serverRequestId = connection.LastAcceptedRequestId;
            Ensure(connection.CallCancellations.TryGetValue(serverRequestId.Value, out var callState),
                "real accepted call must retain its tracked cancellation state at user entry");
            Ensure(callState!.InvocationToken == Token("CallToken"), "sample belongs to the original service call token");
            var deadline = typeof(ServerCallCancellationState).GetProperty("Deadline")!.GetValue(callState)!;
            var hasDeadline = (bool)deadline.GetType().GetProperty("HasValue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(deadline)!;
            var remaining = (TimeSpan)deadline.GetType().GetMethod("GetRemaining", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(deadline, [TimeProvider.System])!;
            serverInitialRemainingTicks = remaining.Ticks;
            Ensure(hasDeadline && remaining.Ticks > 0 && remaining.Ticks <= 20_000_000,
                "actual server must have received a live time budget no greater than the declared two seconds");
            var sampledRequestId = serverRequestId.Value;
            var originalToken = Token("CallToken");
            using var causeObservation = originalToken.Register(() =>
                serverCancellationCause.TrySetResult(new PhysicalTailServerCancellation(
                    callState.RequestId == sampledRequestId,
                    callState.InvocationToken == originalToken,
                    callState.Reason.ToString(),
                    (bool)deadline.GetType().GetMethod("IsExpired", BindingFlags.Instance | BindingFlags.NonPublic,
                        binder: null, types: [typeof(TimeProvider)], modifiers: null)!.Invoke(deadline, [TimeProvider.System])!)));
            observations.Add(Snapshot("user-move-entered"));
            // All receiver/caller terminal observations share the original single 5s
            // watchdog. Splitting their causes does not grant another timeout budget.
            var terminalObservation = Stopwatch.StartNew();
            TimeSpan TerminalBudget()
            {
                var remainingBudget = TimeSpan.FromSeconds(5) - terminalObservation.Elapsed;
                Ensure(remainingBudget > TimeSpan.Zero, "shared terminal-observation watchdog exhausted");
                return remainingBudget;
            }
            if (callerCancel) callerCancellation.Cancel();
            var receiverCause = await serverCancellationCause.Task.WaitAsync(TerminalBudget());
            // The same public caller token can complete the PendingCall first, or
            // wake the response enumerator first. Its await-foreach cleanup then
            // reports ConsumerAbandoned. Preserve either original wire cause;
            // neither is a receiver deadline or module-drain selection.
            Ensure(receiverCause.RequestIdMatches && receiverCause.TokenMatches &&
                (callerCancel ? receiverCause.Reason is "RemoteCancel" or "ConsumerAbandoned" && !receiverCause.DeadlineExpired
                    : receiverCause.Reason == "DeadlineExceeded" && receiverCause.DeadlineExpired),
                "receiver must retain the original selected deadline or explicit caller-cancel cause");
            if (tail == "Dispose")
            {
                await Signal("MoveExited").WaitAsync(TerminalBudget());
                await Signal("DisposeEntered").WaitAsync(TerminalBudget());
            }
            else
                Ensure(!Signal("DisposeEntered").IsCompleted, "DisposeAsync must not overlap the held MoveNext");
            held = tail == "MoveNext" ? Count("ActiveMove") == 1 && !Signal("MoveExited").IsCompleted
                : Count("ActiveDispose") == 1 && !Signal("DisposeExited").IsCompleted;
            Ensure(held && clientClock.GetTimestamp() == 0 && (callerCancel || !call.IsCompleted &&
                ((SharpLinkClient)harness.Client).PendingCallCount == 1),
                "receiver deadline and real held tail must precede any client clock advancement/local terminal");
            observations.Add(Snapshot("receiver-deadline-tail-held-before-client-advance"));
            // Only now select the caller's already-original 2s local deadline. A normal
            // client Cancel emitted afterwards cannot have caused the prior receiver phase.
            if (!callerCancel) clientClock.Advance(TimeSpan.FromTicks(20_000_000));
            try { await call.WaitAsync(TerminalBudget()); }
            catch (Exception error) { terminal = error; }
            Ensure(callerCancel ? terminal is OperationCanceledException : terminal is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
                $"caller local deadline must finish before user gate release, actual {terminal}");
            observations.Add(Snapshot("logical-deadline-tail-held"));
            // The same real connection must still dispatch healthy work while this
            // invocation owns a held output tail. A connection-pool replacement is not a control.
            await Invoke("WarmupAsync", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            var currentConnections = ((ServerConnectionRegistry)typeof(SharpLinkServer).GetField("_connectionRegistry",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(harness.Server)!).SnapshotActive();
            Ensure(currentConnections.Length == 1 && ReferenceEquals(currentConnections[0], connection) &&
                connection.LastAcceptedRequestId > sampledRequestId,
                "healthy second RPC must use the same original physical Session");
            observations.Add(Snapshot("same-session-healthy-rpc"));
            removal = await harness.Server.UnregisterAssemblyAsync(plugin.ServiceAssembly, TimeSpan.FromSeconds(2))
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            observations.Add(new { phase = "unregister-result", elapsedMs = timer.Elapsed.TotalMilliseconds, result = removal });
            observations.Add(Snapshot("unregister-tail-still-held"));
            premature = removal.Value.ReferencesReleased || Count("ServiceDisposed") != 0 ||
                module.RemainingCalls != 1 || module.RemainingStreams != 1;
            Ensure(!removal.Value.ReferencesReleased && removal.Value.RemainingCalls == 1 && removal.Value.RemainingStreams == 1,
                "PHYSICAL_OUTPUT_TAIL_V1: held output must survive the original grace with one original call/stream owner");
            Ensure(Count("ServiceDisposed") == 0, "service must not dispose before the real output tail exits");
            Ensure(!PhysicalTailSourceDisposed(Token("CallToken")) && !PhysicalTailSourceDisposed(Token("EnumeratorToken")),
                "both original and linked CTS must remain undisposed while the physical tail is held");
            if (shape == "Compiler")
            {
                service.GetMethod("ReleaseMove")!.Invoke(null, null);
                await Signal("MoveExited").WaitAsync(TimeSpan.FromSeconds(5));
                await Signal("DisposeEntered").WaitAsync(TimeSpan.FromSeconds(5));
                Ensure(Count("CompilerFinallyEntries") == 1 && Count("ActiveDispose") == 1 &&
                    Count("ServiceDisposed") == 0 && module.RemainingCalls == 1 && module.RemainingStreams == 1 &&
                    !PhysicalTailSourceDisposed(Token("CallToken")) && !PhysicalTailSourceDisposed(Token("EnumeratorToken")),
                    "compiler finally must run once while the original physical owners remain live");
                observations.Add(Snapshot("compiler-finally-held-after-late-move"));
            }
            if (stopWhileHeld)
            {
                // Stock Stop owns 2s grace + the unchanged default 5s framework cleanup
                // budget. A 9s watchdog leaves scheduling headroom, never a longer Stop budget.
                harness.ExpectServerStopFailure("Server reached a faulted terminal state during shutdown.");
                Exception? stopFailure = null;
                try { await harness.Server.StopAsync(TimeSpan.FromSeconds(2)).AsTask().WaitAsync(TimeSpan.FromSeconds(9)); }
                catch (SharpLinkException error) when (error.Message == "Server reached a faulted terminal state during shutdown.")
                { stopFailure = error; }
                observations.Add(new
                {
                    phase = "bounded-stop-with-held-tail",
                    failure = stopFailure?.ToString(),
                    diagnostics = ((SharpLinkServer)harness.Server).LastStopDiagnostics
                });
                observations.Add(Snapshot("stop-returned-tail-still-held"));
                Ensure(((SharpLinkServer)harness.Server).LastStopDiagnostics?.GlobalActiveCalls == 1 &&
                    module.RemainingCalls == 1 && module.RemainingStreams == 1 && Count("ServiceDisposed") == 0 &&
                    !PhysicalTailSourceDisposed(Token("CallToken")) && !PhysicalTailSourceDisposed(Token("EnumeratorToken")),
                    "bounded Stop must report the old physical call and defer its resources, not wait for or reclaim the held tail");
            }
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
                    await WaitUntilAsync(() => module.State == SharpLinkDynamicModuleState.Released &&
                        PhysicalTailSourceDisposed(Token("CallToken")) && PhysicalTailSourceDisposed(Token("EnumeratorToken")));
                    releasedAfter = module.RemainingCalls == 0 && module.RemainingStreams == 0 && Count("ServiceDisposed") == 1;
                }
                observations.Add(Snapshot("released-user-gates"));
                if (removal is null)
                    observations.Add(new { phase = "cleanup-service", result = await harness.Server.UnregisterAssemblyAsync(plugin.ServiceAssembly, TimeSpan.FromSeconds(2)) });
                if (stopWhileHeld)
                    await WaitUntilAsync(() => originalServerContract.State == SharpLinkDynamicModuleState.Released);
                else
                    observations.Add(new { phase = "cleanup-server-contract", result = await harness.Server.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.FromSeconds(2)) });
                observations.Add(new { phase = "cleanup-client-contract", result = await harness.Client.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.FromSeconds(2)) });
                observations.Add(Snapshot("contract-codecs-cleaned"));
            }
            finally
            {
                Console.WriteLine("PHYSICAL_TAIL_BASELINE_V1 " + JsonSerializer.Serialize(new
                {
                    subject = Environment.GetEnvironmentVariable("SHARPLINK_OUTPUT_PRODUCT") ?? Environment.GetEnvironmentVariable("GITHUB_SHA"),
                    fixtureSubject = Environment.GetEnvironmentVariable("GITHUB_SHA"),
                    runtime = Environment.Version.ToString(),
                    shape,
                    tail,
                    lifetime,
                    cleanupFailure,
                    callerCancel,
                    stopWhileHeld,
                    held,
                    premature,
                    releasedAfter,
                    terminal = terminal?.ToString(),
                    observations
                }));
            }
        }
        Ensure(held && releasedAfter, "baseline phases and post-release convergence must be complete");
        Ensure(!premature, "PHYSICAL_OUTPUT_TAIL_V1: logical deadline must not release the original service module while its output tail remains physical");
        Ensure(Count("TailSawDisposedService") == 0, "real producer tail must never observe its own service disposed");
        Ensure(Count("EnumeratorDisposals") == 1, "ordered enumerator disposal must run exactly once");
        Ensure(CodecCount("Serialized") == 2 && CodecCount("Deserialized") == 2,
            "only the two healthy warmup streams may serialize; late producer results must not publish");
        if (shape == "Compiler") Ensure(Count("CompilerFinallyEntries") == 1, "compiler iterator finally must not be skipped or repeated");
        Ensure(Count("ServiceDisposeFailures") == (cleanupFailure ? 1 : 0), "requested late cleanup failure must occur exactly once without skipping release");
        if (lifetime == "Call") Ensure(Count("OtherServiceDisposed") == 2, "both real per-call warmup scopes must retire independently");
        proxy = null!;
        service = codec = contract = item = null!;
        consume = null!;
        call = null;
        terminal = null;
        module = null;
        originalServerContract = null!;
        return plugin.Unload();
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
    private static bool PhysicalTailSourceDisposed(CancellationToken token)
    {
        var source = typeof(CancellationToken).GetField("_source", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(token)!;
        return (bool)typeof(CancellationTokenSource).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;
    }

    private static object PhysicalTailTokenSnapshot(CancellationToken token)
    {
        var source = typeof(CancellationToken).GetField("_source", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(token);
        var disposed = source is null ? (bool?)null : (bool)typeof(CancellationTokenSource)
            .GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;
        return new { token.CanBeCanceled, token.IsCancellationRequested, sourceExists = source is not null, disposed };
    }
    private sealed record PhysicalTailServerCancellation(bool RequestIdMatches, bool TokenMatches, string Reason, bool DeadlineExpired);
}
