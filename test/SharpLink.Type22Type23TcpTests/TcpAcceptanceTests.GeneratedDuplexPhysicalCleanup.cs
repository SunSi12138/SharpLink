using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.Type22Type23TcpTests;

public sealed partial class TcpAcceptanceTests
{
    [Test]
    public Task GeneratedDuplexDeadlineJoinsActualIncomingDisposeBeforePhysicalOwnersReturn()
        => ExerciseGeneratedDuplexPhysicalCleanupAsync(closeSession: false);

    [Test]
    public Task GeneratedDuplexSessionCloseCannotEraseCapturedPhysicalInboundOwner()
        => ExerciseGeneratedDuplexPhysicalCleanupAsync(closeSession: true);

    private static async Task ExerciseGeneratedDuplexPhysicalCleanupAsync(bool closeSession)
    {
        // Endpoints predate this case's fresh collectible ALC. DI still uses an actual scoped
        // Microsoft provider once that ALC's scope Type is known, never a scripted scope/child.
        var services = new DeferredPhysicalScopeFactory();
        var serverClock = new ManualTimeProvider();
        var clientClock = closeSession ? null : new ManualTimeProvider();
        var harness = new Harness(services: services, physicalServerClock: serverClock, physicalClientClock: clientClock);
        PersistedPlugin? plugin = null;
        PhysicalPluginProbe? fixture = null;
        var inputRelease = NewGate();
        Task<Exception?>? terminal = null;
        Task? close = null;
        Task<SharpLinkAssemblyUnregisterResult>? retirement = null;
        var terminalValidated = false;
        Exception? bodyFailure = null;
        Action? failureDiagnostics = null;
        var diagnosticStage = "before physical capture";
        var cleanupFailures = new List<Exception>();
        try
        {
            plugin = new PersistedPlugin();
            fixture = new PhysicalPluginProbe(plugin);
            fixture.Reset(holdDecode: !closeSession);
            services.Configure(fixture.ScopeType);
            var clientContracts = harness.Client.RegisterAssembly(plugin.Contracts);
            AssertPhysicalRegistration(clientContracts, "real client contract registration");
            var serverContracts = harness.Server.RegisterAssembly(plugin.Contracts);
            AssertPhysicalRegistration(serverContracts, "real server contract registration");
            var serverServices = harness.Server.RegisterAssembly(plugin.Services);
            AssertPhysicalRegistration(serverServices, "real server service registration");
            var staticProxy = await harness.PrepareAsync();
            var clientConnection = CaptureOnlyReadyConnection(harness.Client);
            var clientSession = clientConnection.Session;
            var serverConnection = harness.Connection;
            var serverSession = serverConnection.Session;
            Ensure(ReferenceEquals(serverSession.RuntimeContext.TimeProvider, serverClock) &&
                !ReferenceEquals(clientSession.RuntimeContext.TimeProvider, serverClock), "original server clock is independent from the client clock");
            if (clientClock is not null)
            {
                var clientHeartbeat = harness.Client.GetHeartbeatConfigurationSnapshot();
                Ensure(ReferenceEquals(clientSession.RuntimeContext.TimeProvider, clientClock) &&
                    !ReferenceEquals(clientClock, serverClock), "original client owns its independent clock from construction");
                Ensure(clientHeartbeat.Interval == TimeSpan.FromMinutes(2) &&
                    clientHeartbeat.Timeout == TimeSpan.FromMinutes(3),
                    "actual client heartbeat interval/timeout exceed its sole 61-second advance");
            }
            Ensure(ReadPhysicalMember<TimeSpan>(harness.Server, "_heartbeatCheckInterval") == TimeSpan.FromMinutes(2) &&
                ReadPhysicalMember<TimeSpan>(harness.Server, "_heartbeatTimeout") == TimeSpan.FromMinutes(3),
                "real configured heartbeat interval/timeout exceed the sole 61-second advance");
            var module = CapturePhysicalModule(harness.Server, plugin.Services);
            var originalRequest = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            var priorAccepted = harness.Server.AfterRequestAcceptedForTesting;
            harness.Server.AfterRequestAcceptedForTesting = (connection, requestId) =>
            {
                priorAccepted?.Invoke(connection, requestId);
                Ensure(ReferenceEquals(connection, serverConnection), "acceptance on original real connection");
                originalRequest.TrySetResult(requestId);
            };
            var start = typeof(TcpAcceptanceTests).GetMethod(nameof(StartPhysicalDuplexAsync),
                BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(fixture.ContractType, fixture.PayloadType);
            var started = await (Task<PhysicalDuplexStart>)InvokePhysical(start, null,
                harness.Client, fixture.ContractType, inputRelease.Task, clientClock)!;
            terminal = started.Terminal;
            var originalClientDeadline = started.Deadline;
            await fixture.Task("InputEntered").WaitAsync(Budget);
            await fixture.Task("MoveEntered").WaitAsync(Budget);
            var requestId = await originalRequest.Task.WaitAsync(Budget);
            Ensure(serverConnection.CallCancellations.TryGetValue(requestId, out var state), "original call state remains mapped");
            var originalState = state!.CaptureLease(requestId); // snapshot only: NO TryAcquire/additional pin
            var originalToken = state.InvocationToken;
            var originalDeadline = state.Deadline;
            var route = PhysicalInboundObservation.Capture(serverSession, requestId, fixture.PayloadType);
            Ensure(ReferenceEquals(ReadPhysicalMember<object>(route.Child, "_codec").GetType().Assembly, plugin.Contracts) &&
                ReadPhysicalMember<object>(route.Child, "_codec").GetType() == fixture.CodecType,
                "actual generated typed child uses the original ALC's attributed owner-local codec");
            var incomingWrapper = fixture.Value<object>("IncomingEnumerator");
            Ensure(incomingWrapper.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Any(field => ReferenceEquals(field.GetValue(incomingWrapper), route.Child)),
                "service saved the actual generated wrapper around this exact typed child");
            if (!closeSession)
                failureDiagnostics = () => Console.Error.WriteLine(CapturePhysicalRouteFailure(
                    diagnosticStage, requestId, originalState, originalToken, clientConnection, clientSession,
                    serverConnection, serverSession, route, fixture, terminal));
            AssertPhysicalCallOwners(harness, module, originalState, originalToken);
            Ensure(state.Reason == ServerCallCancellationReason.None, "no preselected original terminal");
            if (clientClock is not null)
                AssertOriginalClientDeadline(clientConnection, requestId, originalClientDeadline, clientClock);
            else
                Ensure(!originalClientDeadline.HasValue, "intentional-close case has no client business deadline");
            inputRelease.TrySetResult();
            await fixture.CodecTask("DecodeEntered").WaitAsync(Budget);

            if (!closeSession)
            {
                Ensure(originalDeadline.HasValue && !originalDeadline.IsExpired(serverClock) &&
                    originalDeadline.GetRemaining(serverClock) > TimeSpan.FromSeconds(50) &&
                    originalDeadline.GetRemaining(serverClock) <= TimeSpan.FromSeconds(60),
                    "original server-mapped 60-second deadline, not a shorter client-default/method timeout");
                Ensure(((IStreamDispatchState)route.Mailbox).HasActiveDispatches, "real codec owns a child dispatch");
                var deadlineClock = clientClock ?? throw new InvalidOperationException("Case A requires its original client clock.");
                var originalClientTimestamp = deadlineClock.GetTimestamp();
                serverClock.Advance(TimeSpan.FromSeconds(61)); // first: only the original server deadline expires
                await WaitUntilAsync(() => state.Reason == ServerCallCancellationReason.DeadlineExceeded,
                    "original independent server deadline did not select DeadlineExceeded");
                AssertOriginalServerDeadline(originalState, originalToken, originalDeadline, serverClock, serverConnection);
                Ensure(deadlineClock.GetTimestamp() == originalClientTimestamp && !terminal.IsCompleted,
                    "server deadline selected before any original client deadline advancement or terminal delivery");
                AssertOriginalClientDeadline(clientConnection, requestId, originalClientDeadline, deadlineClock);
                AssertSamePhysicalSessions(harness, clientConnection, clientSession, serverConnection, serverSession);
                AssertPhysicalCallOwners(harness, module, originalState, originalToken);
                Ensure(((IStreamDispatchState)route.Mailbox).HasActiveDispatches &&
                    !fixture.Task("EnumeratorDisposeEntered").IsCompleted,
                    "actual codec dispatch and outgoing MoveNext remain held across both deadline selections");
                // This expires the ORIGINAL local client deadline, not a promised remote wire error.
                // Advance callbacks may throw; preserve that body failure and all independent cleanup failures.
                deadlineClock.Advance(TimeSpan.FromSeconds(61));
                Ensure(originalClientDeadline.IsExpired(deadlineClock), "unchanged original client deadline is now expired");
                AssertPhysicalTerminal(await terminal.WaitAsync(Budget), SharpLinkErrorCode.DeadlineExceeded);
                AssertOriginalServerDeadline(originalState, originalToken, originalDeadline, serverClock, serverConnection);
                AssertSamePhysicalSessions(harness, clientConnection, clientSession, serverConnection, serverSession);
                Ensure(((IStreamDispatchState)route.Mailbox).HasActiveDispatches,
                    "original codec dispatch remains held after the client's independent local terminal");
                terminalValidated = true;
                Ensure(!fixture.Task("EnumeratorDisposeEntered").IsCompleted,
                    "logical terminal cannot dispose outgoing enumerator concurrently with held actual MoveNext");
                diagnosticStage = "logical-map removal while codec and outgoing MoveNext remain held";
                await WaitUntilAsync(() => !serverConnection.CallCancellations.TryGetValue(requestId, out _),
                    "original logical request map did not retire while its physical owners remained held");
                AssertOriginalServerDeadline(originalState, originalToken, originalDeadline, serverClock, serverConnection);
                AssertPhysicalCallOwners(harness, module, originalState, originalToken);
                AssertSamePhysicalSessions(harness, clientConnection, clientSession, serverConnection, serverSession);
                route.AssertRetainedUntilLocalCompletion();
                Ensure(route.IsStillMapped(serverSession, requestId) && !route.PeerTerminalReceived &&
                    ((IStreamDispatchState)route.Mailbox).HasActiveDispatches &&
                    !fixture.Task("EnumeratorDisposeEntered").IsCompleted,
                    "establish absent logical map before actual peer terminal; original route/codec/outgoing MoveNext remain held");
            }
            else
            {
                Ensure(!originalDeadline.HasValue, "intentional-close case has no accidental business deadline");
                await WaitUntilAsync(() => route.PeerTerminalReceived, "real finite input peer terminal did not arrive");
                Ensure(route.IsStillMapped(serverSession, requestId), "peer EOF retained original mailbox before physical completion");
                close = serverConnection.CloseAsync().AsTask(); // actual production connection close, not private stream cleanup
                await close.WaitAsync(Budget);
                AssertPhysicalTerminal(await terminal.WaitAsync(Budget), SharpLinkErrorCode.ConnectionClosed);
                terminalValidated = true;
                Ensure(!serverSession.IsConnected && serverSession.StreamManager.IsTerminated &&
                    !route.IsStillMapped(serverSession, requestId), "actual Session CompleteAll removed original lookup");
                Ensure(state.Reason == ServerCallCancellationReason.ConnectionClosed, "intentional original connection terminal");
            }

            retirement = harness.Server.UnregisterAssemblyAsync(plugin.Services, Budget).AsTask();
            Ensure(!retirement.IsCompleted, "real module drain cannot release held outgoing physical owner");
            AssertPhysicalCallOwners(harness, module, originalState, originalToken);
            Ensure(!fixture.Task("ServiceDisposeEntered").IsCompleted && !fixture.ScopeTask("DisposeEntered").IsCompleted,
                "no early service/scope disposal");
            fixture.Release("ReleaseMove");
            await fixture.Task("IncomingDisposeObserved").WaitAsync(Budget);
            if (!closeSession)
            {
                Ensure(fixture.Value<bool>("IncomingDisposeWasIncomplete"), "actual incoming Dispose must be asynchronous under held decode");
                var originalDisposal = fixture.Value<Task>("IncomingDisposeTask");
                Ensure(!originalDisposal.IsCompleted && fixture.Value<int>("IncomingDisposeCalls") == 1,
                    "observe the sole original incomplete Dispose task, not a synthetic proxy");
                route.AssertActualDisposing();
                AssertOriginalServerDeadline(originalState, originalToken, originalDeadline, serverClock, serverConnection);
                AssertPhysicalCallOwners(harness, module, originalState, originalToken);
                Ensure(!fixture.Task("DuplexFinalDisposeEntered").IsCompleted && !retirement.IsCompleted &&
                    !fixture.Task("ServiceDisposeEntered").IsCompleted && !fixture.ScopeTask("DisposeEntered").IsCompleted,
                    "actual child Dispose keeps outgoing tail and all physical owners live");
                diagnosticStage = "original incoming Dispose before codec release";
                Ensure(!serverConnection.CallCancellations.TryGetValue(requestId, out _) &&
                    !route.PeerTerminalReceived, "future peer terminal will take the observed no-map path");
                route.AssertRetainedUntilLocalCompletion();
                fixture.ReleaseCodec();
                await originalDisposal.WaitAsync(Budget);
                Ensure(ReferenceEquals(originalDisposal, fixture.Value<Task>("IncomingDisposeTask")), "same original Dispose task observed once");
            }
            await fixture.Task("DuplexFinalDisposeEntered").WaitAsync(Budget);
            Ensure(fixture.Value<int>("IncomingDisposed") == 1 && fixture.Value<int>("IncomingDisposeCalls") == 1 &&
                fixture.Value<int>("EnumeratorDisposed") == 0, "incoming Dispose finished once; actual outgoing Dispose still held");
            if (!closeSession)
            {
                diagnosticStage = "real peer terminal on original retained no-map route";
                await WaitUntilAsync(() => route.PeerTerminalReceived,
                    "actual peer terminal did not reach the original retained route after logical map retirement");
                Ensure(!serverConnection.CallCancellations.TryGetValue(requestId, out _),
                    "original logical request remains absent; saved physical state is not a logical map entry");
                route.AssertRetainedUntilLocalCompletion();
                Ensure(route.IsStillMapped(serverSession, requestId) && !route.Entry.IsDetached &&
                    route.Entry.HasActiveDispatches && route.HasOriginalChild,
                    "actual peer completion retains original indexed mailbox and captured child until outgoing tail joins");
                AssertOriginalServerDeadline(originalState, originalToken, originalDeadline, serverClock, serverConnection);
                AssertSamePhysicalSessions(harness, clientConnection, clientSession, serverConnection, serverSession);
            }
            else
            {
                // Case B keeps its separate early-removal proof through actual Session CompleteAll.
                await WaitUntilAsync(() => !route.IsStillMapped(serverSession, requestId),
                    "actual peer terminal or actual Session close did not remove original route");
                Ensure(route.Entry.IsDetached && route.Entry.HasActiveDispatches && route.HasOriginalChild,
                    "removed lookup retains the original captured entry/mailbox/typed child before outgoing tail finishes");
            }
            AssertPhysicalCallOwners(harness, module, originalState, originalToken);
            Ensure(!retirement.IsCompleted && !fixture.Task("ServiceDisposeEntered").IsCompleted,
                "held outgoing tail prevents physical owner completion regardless of route-map retention/removal");
            diagnosticStage = "outgoing release then inbound removal/join before held service disposal";
            fixture.Release("ReleaseDuplexFinalDispose");
            await fixture.Task("ServiceDisposeEntered").WaitAsync(Budget);
            if (!closeSession)
            {
                Ensure(!serverConnection.CallCancellations.TryGetValue(requestId, out _) &&
                    !route.IsStillMapped(serverSession, requestId) && route.PeerTerminalReceived &&
                    route.Entry.IsDetached && !route.Entry.HasActiveDispatches &&
                    route.ChildPhysicalCompleted && !route.HasOriginalChild,
                    "outgoing-first abandonment removed original route and joined child before service/scope release");
                AssertOriginalServerDeadline(originalState, originalToken, originalDeadline, serverClock, serverConnection);
                AssertSamePhysicalSessions(harness, clientConnection, clientSession, serverConnection, serverSession);
            }
            Ensure(fixture.Value<int>("EnumeratorDisposed") == 1 && !fixture.ScopeTask("DisposeEntered").IsCompleted &&
                !retirement.IsCompleted, "service cleanup follows complete actual outgoing tail");
            AssertPhysicalCallOwners(harness, module, originalState, originalToken);
            // Stop reading pooled state for failure diagnostics before allowing physical owners to return.
            failureDiagnostics = null;
            fixture.Release("ReleaseServiceDispose");
            await fixture.ScopeTask("DisposeEntered").WaitAsync(Budget);
            Ensure(fixture.Value<int>("ServiceDisposed") == 1 && fixture.ScopeValue<int>("Disposed") == 0 &&
                !retirement.IsCompleted, "actual async DI scope still owns module/capacity after service disposal");
            AssertPhysicalCallOwners(harness, module, originalState, originalToken);
            fixture.ReleaseScope();
            AssertReleased(await retirement.WaitAsync(Budget), "actual Duplex service module");
            await WaitUntilAsync(() => harness.Server.ActiveCallCountForDiagnostics == 0 && serverConnection.ActiveCalls == 0 &&
                module.RemainingCalls == 0 && module.RemainingStreams == 0 && route.ChildPhysicalCompleted,
                "actual final child/service/scope/module/capacity joins did not complete");
            Ensure(fixture.ScopeValue<int>("Disposed") == 1 && fixture.Value<int>("TokenChecks") == 3 &&
                !route.Entry.HasActiveDispatches && !route.HasOriginalChild,
                "captured original entry and original mailbox child physically retire exactly once");
            if (!closeSession)
            {
                await harness.AssertQuiescentAsync();
                AssertSamePhysicalSessions(harness, clientConnection, clientSession, serverConnection, serverSession);
                Ensure(await staticProxy.Service.UnaryAsync(40, default) == 41, "unaffected real unary after physical joins");
                await harness.AssertQuiescentAsync();
                AssertSamePhysicalSessions(harness, clientConnection, clientSession, serverConnection, serverSession);
            }
            else
            {
                Ensure(!serverSession.IsConnected && serverSession.StreamManager.IsTerminated &&
                    serverSession.StreamManager.ActiveStreamCount == 0 && close!.IsCompletedSuccessfully,
                    "intentional close has a closed-session oracle, never reconnect/same-session health");
            }
            AssertReleased(await harness.Server.UnregisterAssemblyAsync(plugin.Contracts, Budget), "server contract");
            AssertReleased(await harness.Client.UnregisterAssemblyAsync(plugin.Contracts, Budget), "client contract");
        }
        catch (Exception failure)
        {
            bodyFailure = failure; // Preserve the original identity/stack, including a failed bounded wait.
            if (failureDiagnostics is not null)
            {
                try { failureDiagnostics(); }
                catch (Exception diagnosticFailure) { cleanupFailures.Add(diagnosticFailure); }
            }
        }
        finally
        {
            // No assertion failure may strand a synchronous codec worker or prevent another
            // original owner from being joined. Cleanup failures never replace the body failure.
            inputRelease.TrySetResult();
            if (fixture is not null)
            {
                TryPhysicalCleanup(fixture.ReleaseCodec, cleanupFailures);
                foreach (var gate in new[] { "ReleaseMove", "ReleaseEnumeratorDispose", "ReleaseDuplexFinalDispose", "ReleaseServiceDispose" })
                    TryPhysicalCleanup(() => fixture.Release(gate), cleanupFailures);
                TryPhysicalCleanup(fixture.ReleaseScope, cleanupFailures);
            }
            if (terminal is not null)
                await TryPhysicalCleanupAsync(async () =>
                {
                    var error = await terminal.WaitAsync(Budget);
                    if (!terminalValidated && error is not null) ExceptionDispatchInfo.Capture(error).Throw();
                }, cleanupFailures);
            if (close is not null) await TryPhysicalCleanupAsync(() => close.WaitAsync(Budget), cleanupFailures);
            if (retirement is not null) await TryPhysicalCleanupAsync(async () => { await retirement.WaitAsync(Budget); }, cleanupFailures);
            // Approved A/B-only helper independently attempts client, CTS cancellation, server,
            // actual Run task and CTS disposal. No accepted teardown-error filter.
            await TryPhysicalCleanupAsync(() => harness.DisposePhysicalFixtureAsync().AsTask(), cleanupFailures);
            await TryPhysicalCleanupAsync(() => services.DisposeAsync().AsTask().WaitAsync(Budget), cleanupFailures);
            if (plugin is not null) TryPhysicalCleanup(plugin.Dispose, cleanupFailures);
        }
        if (bodyFailure is not null && cleanupFailures.Count == 0) ExceptionDispatchInfo.Capture(bodyFailure).Throw();
        if (bodyFailure is not null) cleanupFailures.Insert(0, bodyFailure);
        if (cleanupFailures.Count != 0) throw new AggregateException("Actual generated Duplex body/independent cleanup failures.", cleanupFailures);
    }

    // Failure-only, sequential read-only observations, not an atomic lifecycle snapshot or an owner.
    private static string CapturePhysicalRouteFailure(string stage, long requestId,
        ServerCallCancellationLease original, CancellationToken originalToken, ClientConnection client, RpcSession clientSession,
        ServerConnectionState server, RpcSession serverSession, PhysicalInboundObservation route,
        PhysicalPluginProbe fixture, Task<Exception?>? terminal)
    {
        var mapped = server.CallCancellations.TryGetValue(requestId, out var current);
        var sameState = mapped && ReferenceEquals(current, original.State);
        var terminalOutcome = terminal?.IsCompletedSuccessfully == true
            ? terminal.Result is SharpLinkException error ? $"{error.GetType().FullName}/{error.Code}"
                : terminal.Result?.GetType().FullName ?? "success"
            : "not synchronously observable";
        var incomingDispose = fixture.Value<Task?>("IncomingDisposeTask");
        return $"PhysicalDuplexFailure stage={stage}; requestId={requestId}; " +
            $"logicalMapPresent={mapped}; logicalMapSameState={sameState}; " +
            $"savedRequestId={original.State.RequestId}; savedGeneration={original.Generation}; " +
            $"currentSavedGeneration={original.State.CaptureLease(requestId).Generation}; savedReason={original.State.Reason}; " +
            $"sameInvocationToken={original.State.InvocationToken == originalToken}; " +
            $"clientSessionSame={ReferenceEquals(client.Session, clientSession)}; clientConnected={clientSession.IsConnected}; " +
            $"clientStreamsTerminated={clientSession.StreamManager.IsTerminated}; " +
            $"serverSessionSame={ReferenceEquals(server.Session, serverSession)}; serverConnected={serverSession.IsConnected}; " +
            $"serverStreamsTerminated={serverSession.StreamManager.IsTerminated}; " +
            route.CaptureFailureState(serverSession, requestId) +
            $"; terminalTask={terminal?.Status}; terminalOutcome={terminalOutcome}; " +
            $"incomingDisposeTask={incomingDispose?.Status}; finalOutgoingDisposeEntered={fixture.Task("DuplexFinalDisposeEntered").IsCompleted}; " +
            $"serviceDisposeEntered={fixture.Task("ServiceDisposeEntered").IsCompleted}; " +
            $"scopeDisposeEntered={fixture.ScopeTask("DisposeEntered").IsCompleted}; producerTask=not captured";
    }

    private readonly record struct PhysicalDuplexStart(Task<Exception?> Terminal, RpcDeadline Deadline);

    private static async Task<PhysicalDuplexStart> StartPhysicalDuplexAsync<TContract, TPayload>(
        SharpLinkClient client, Type contract, Task inputRelease, ManualTimeProvider? clientClock) where TContract : IService
    {
        var proxy = client.CreateProxy<TContract>();
        await proxy.PrepareAsync(Budget);
        var descriptor = proxy.Service!.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(RpcMethodDescriptor))
            .Select(field => (RpcMethodDescriptor)field.GetValue(null)!)
            .Single(value => value.Kind == RpcMethodKind.DuplexStreaming);
        Ensure(descriptor.ClientStreamCount == 1 && !descriptor.HasMethodTimeout && descriptor.MethodTimeout is null,
            "actual generated Duplex descriptor has exactly one input and no shorter method timeout");
        var input = PhysicalInput<TPayload>(inputRelease);
        var originalContext = SharpLinkCallContext.Current;
        Task<int[]> operation;
        var originalDeadline = default(RpcDeadline);
        if (clientClock is not null)
        {
            // Capture once BEFORE the generated call; the same provider preserves this exact local boundary.
            originalDeadline = RpcDeadline.Create(TimeSpan.FromSeconds(60), clientClock);
            using (SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot(
                "actual-duplex-independent-deadlines", authentication: null, originalDeadline, clientClock)))
                operation = CollectCoreAsync((IAsyncEnumerable<int>)InvokePhysical(
                    contract.GetMethod("BlockDuplexPhysicalTailAsync")!, proxy.Service, input, CancellationToken.None)!);
        }
        else
            operation = CollectCoreAsync((IAsyncEnumerable<int>)InvokePhysical(
                contract.GetMethod("BlockDuplexPhysicalTailAsync")!, proxy.Service, input, CancellationToken.None)!);
        Ensure(ReferenceEquals(originalContext, SharpLinkCallContext.Current), "original deadline scope ends before test clock advances");
        return new PhysicalDuplexStart(ObservePhysicalTerminalAsync(operation), originalDeadline);
    }

    private static async IAsyncEnumerable<T> PhysicalInput<T>(Task release,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await release.WaitAsync(cancellationToken);
        var payload = Activator.CreateInstance<T>();
        typeof(T).GetProperty("Value")!.SetValue(payload, 1234);
        yield return payload;
    }

    private static async Task<Exception?> ObservePhysicalTerminalAsync(Task operation)
    {
        try { await operation.ConfigureAwait(false); return null; }
        catch (Exception failure) { return failure; } // retained raw outcome; caller validates, never implicit success
    }
    private static void AssertPhysicalRegistration(SharpLinkAssemblyRegistrationResult result, string operation)
        => Ensure(result.Succeeded,
            $"{operation}; Code={result.Error?.Code.ToString() ?? "<none>"}; " +
            $"Message={result.Error?.Message ?? "<none>"}; Error={result.Error?.ToString() ?? "<none>"}");

    private static void AssertPhysicalTerminal(Exception? error, SharpLinkErrorCode code)
    {
        if (error is not SharpLinkException failure || failure.Code != code)
            throw new InvalidOperationException($"Expected original generated terminal {code}, not success or a wrapper.", error);
    }
    private static void AssertOriginalClientDeadline(ClientConnection connection, long requestId,
        RpcDeadline original, ManualTimeProvider clock)
        => Ensure(ReferenceEquals(connection.Session.RuntimeContext.TimeProvider, clock) &&
            connection.PendingCalls.TryGetProducerDeadline(requestId, out var current) && current.Equals(original) &&
            original.HasValue && !original.IsExpired(clock) && original.GetRemaining(clock) == TimeSpan.FromSeconds(60),
            "original full-ID client slot retains the exact unexpired inherited 60-second deadline without reanchoring");

    private static void AssertOriginalServerDeadline(ServerCallCancellationLease original, CancellationToken token,
        RpcDeadline deadline, ManualTimeProvider clock, ServerConnectionState connection)
    {
        Ensure(original.State.RequestId == original.RequestId &&
            original.State.CaptureLease(original.RequestId).Generation == original.Generation &&
            original.State.InvocationToken == token && original.State.Deadline.Equals(deadline) && deadline.IsExpired(clock) &&
            original.State.Reason == ServerCallCancellationReason.DeadlineExceeded &&
            !connection.ConnectionToken.IsCancellationRequested && connection.Session.IsConnected &&
            !connection.Session.StreamManager.IsTerminated, "exact original server deadline won; no heartbeat/connection replacement");
    }
    private static void AssertPhysicalCallOwners(Harness harness, SharpLinkDynamicModule module,
        ServerCallCancellationLease original, CancellationToken token)
    {
        Ensure(module.RemainingCalls == 1 && module.RemainingStreams == 1 &&
            harness.Server.ActiveCallCountForDiagnostics == 1 && harness.Connection.ActiveCalls == 1,
            "actual original module and both physical call-capacity owners remain occupied");
        Ensure(original.State.RequestId == original.RequestId &&
            original.State.CaptureLease(original.RequestId).Generation == original.Generation &&
            original.State.InvocationToken == token && !token.WaitHandle.SafeWaitHandle.IsClosed,
            "original pooled call generation/CTS live without any test-acquired lease");
    }
    private static void AssertSamePhysicalSessions(Harness harness, ClientConnection client, RpcSession clientSession,
        ServerConnectionState server, RpcSession serverSession)
        => Ensure(ReferenceEquals(client, CaptureOnlyReadyConnection(harness.Client)) &&
            ReferenceEquals(client.Session, clientSession) && ReferenceEquals(harness.Connection, server) &&
            ReferenceEquals(server.Session, serverSession) && clientSession.IsConnected && serverSession.IsConnected,
            "both actual original TCP Sessions remain healthy; reconnect cannot pass");

    private static SharpLinkDynamicModule CapturePhysicalModule(SharpLinkServer server, Assembly assembly)
    {
        lock (ReadPhysicalMember<Lock>(server, "_registryGate"))
        {
            var modules = ReadPhysicalMember<ServerServiceModuleRegistry.DynamicModuleTable>(server, "_dynamicModules");
            Ensure(modules.TryGetValue(assembly, out var module), "actual original production service module");
            return module!;
        }
    }
    private static T ReadPhysicalMember<T>(object owner, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = owner.GetType();
        var field = type.GetField(name, flags);
        var value = field is not null ? field.GetValue(owner) : type.GetProperty(name, flags)!.GetValue(owner);
        return (T)value!;
    }
    private static object? InvokePhysical(MethodInfo method, object? instance, params object?[] arguments)
    {
        try { return method.Invoke(instance, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static void TryPhysicalCleanup(Action cleanup, List<Exception> failures)
    { try { cleanup(); } catch (Exception error) { failures.Add(error); } }
    private static async Task TryPhysicalCleanupAsync(Func<Task> cleanup, List<Exception> failures)
    { try { await cleanup(); } catch (Exception error) { failures.Add(error); } }

    private sealed class DeferredPhysicalScopeFactory : IServiceProvider, IServiceScopeFactory, IAsyncDisposable
    {
        private ServiceProvider? _provider;
        internal void Configure(Type scopeType)
            // The whole persisted Services assembly includes DynamicPluginService(TimeProvider).
            // Match the existing loader fixture's dependency; this is NOT the server runtime clock.
            => _provider = new ServiceCollection().AddSingleton<TimeProvider>(TimeProvider.System)
                .AddScoped(scopeType).BuildServiceProvider();
        public object? GetService(Type serviceType)
            => serviceType == typeof(IServiceScopeFactory) ? this : _provider?.GetService(serviceType);
        public IServiceScope CreateScope()
            => (_provider ?? throw new InvalidOperationException("Actual scope provider not configured.")).CreateScope();
        public ValueTask DisposeAsync() => _provider?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private sealed class PhysicalPluginProbe
    {
        private readonly Type _service;
        internal Type ContractType { get; }
        internal Type PayloadType { get; }
        internal Type CodecType { get; }
        internal Type ScopeType { get; }
        internal PhysicalPluginProbe(PersistedPlugin plugin)
        {
            ContractType = plugin.Contracts.GetType("SharpLink.DynamicPlugin.IPhysicalTailOwnershipService", true)!;
            PayloadType = plugin.Contracts.GetType("SharpLink.DynamicPlugin.PhysicalTailPayload", true)!;
            CodecType = plugin.Contracts.GetType("SharpLink.DynamicPlugin.PhysicalTailPayloadCodec", true)!;
            _service = plugin.Services.GetType("SharpLink.DynamicPlugin.PhysicalTailOwnershipService", true)!;
            ScopeType = plugin.Services.GetType("SharpLink.DynamicPlugin.PhysicalTailScopeProbe", true)!;
        }
        internal void Reset(bool holdDecode)
        { Release("Reset"); InvokePhysical(CodecType.GetMethod("Reset")!, null, holdDecode); }
        internal T Value<T>(string name) => (T)_service.GetProperty(name)!.GetValue(null)!;
        internal Task Task(string name) => Value<Task>(name);
        internal Task CodecTask(string name) => (Task)CodecType.GetProperty(name)!.GetValue(null)!;
        internal T ScopeValue<T>(string name) => (T)ScopeType.GetProperty(name)!.GetValue(null)!;
        internal Task ScopeTask(string name) => ScopeValue<Task>(name);
        internal void Release(string name) => InvokePhysical(_service.GetMethod(name)!, null);
        internal void ReleaseCodec() => InvokePhysical(CodecType.GetMethod("ReleaseDecode")!, null);
        internal void ReleaseScope() => InvokePhysical(ScopeType.GetMethod("ReleaseDispose")!, null);
    }

    private sealed class PhysicalInboundObservation
    {
        private readonly object _entry;
        private readonly long _activeLease;
        internal IStreamDispatchState Entry => (IStreamDispatchState)_entry;
        internal object Mailbox { get; }
        internal object Child { get; }
        private PhysicalInboundObservation(object entry, object mailbox, object child)
        { _entry = entry; Mailbox = mailbox; Child = child; _activeLease = ReadPhysicalMember<long>(child, "_leaseState"); }
        internal static PhysicalInboundObservation Capture(RpcSession session, long requestId, Type payloadType)
        {
            var entry = FindEntry(session, requestId) ?? throw new InvalidOperationException("Original incoming route missing.");
            var mailbox = ReadPhysicalMember<object>(entry, "Dispatcher");
            Ensure(mailbox is PreAdmissionStreamDispatcher, "actual stable admission mailbox");
            lock (ReadPhysicalMember<Lock>(mailbox, "_gate"))
            {
                var child = ReadPhysicalMember<object>(mailbox, "_dispatcher");
                Ensure(child.GetType() == typeof(PooledAsyncStreamDispatcher<>).MakeGenericType(payloadType) &&
                    ReferenceEquals(ReadPhysicalMember<object>(child, "_dispatchState"), mailbox),
                    "actual generated pooled typed child is bound to original mailbox, not captured parent entry");
                var observed = new PhysicalInboundObservation(entry, mailbox, child);
                Ensure((observed._activeLease & 3) == 1, "original typed child lease active");
                return observed;
            }
        }
        internal bool PeerTerminalReceived => ReadPhysicalMember<bool>(_entry, "PeerTerminalReceived");
        internal bool IsStillMapped(RpcSession session, long requestId) => ReferenceEquals(FindEntry(session, requestId), _entry);
        internal bool HasOriginalChild
        { get { lock (ReadPhysicalMember<Lock>(Mailbox, "_gate")) return ReferenceEquals(ReadPhysicalMember<object?>(Mailbox, "_dispatcher"), Child); } }
        internal bool ChildPhysicalCompleted
        { get { lock (ReadPhysicalMember<Lock>(Mailbox, "_gate")) return ReadPhysicalMember<bool>(Mailbox, "_childPhysicalCompleted"); } }
        internal void AssertRetainedUntilLocalCompletion()
        {
            lock (ReadPhysicalMember<Lock>(Mailbox, "_gate"))
                Ensure(ReadPhysicalMember<bool>(Mailbox, "_retainUntilLocalCompletion") &&
                    !ReadPhysicalMember<bool>(Mailbox, "_localAbandonRequested"),
                    "original admission mailbox retains peer completion until its own local abandon cut");
        }
        internal string CaptureFailureState(RpcSession session, long requestId)
        {
            var mapped = IsStillMapped(session, requestId);
            lock (ReadPhysicalMember<Lock>(Mailbox, "_gate"))
                return $"streamMapSameEntry={mapped}; peerTerminal={PeerTerminalReceived}; " +
                    $"entryDetached={Entry.IsDetached}; entryActive={Entry.HasActiveDispatches}; " +
                    $"retainUntilLocalCompletion={ReadPhysicalMember<bool>(Mailbox, "_retainUntilLocalCompletion")}; " +
                    $"localAbandon={ReadPhysicalMember<bool>(Mailbox, "_localAbandonRequested")}; " +
                    $"discarding={ReadPhysicalMember<bool>(Mailbox, "_discarding")}; " +
                    $"childClosed={ReadPhysicalMember<bool>(Mailbox, "_childClosed")}; " +
                    $"childDetachRequested={ReadPhysicalMember<bool>(Mailbox, "_childDetachRequested")}; " +
                    $"childDetached={ReadPhysicalMember<bool>(Mailbox, "_childDetached")}; " +
                    $"childPhysicalCompleted={ReadPhysicalMember<bool>(Mailbox, "_childPhysicalCompleted")}; " +
                    $"activeChildDispatches={ReadPhysicalMember<int>(Mailbox, "_activeChildDispatches")}; " +
                    $"originalChildRetained={ReferenceEquals(ReadPhysicalMember<object?>(Mailbox, "_dispatcher"), Child)}";
            // Never dereference the saved pooled child here: it may already have detached/returned.
        }
        internal void AssertActualDisposing()
        {
            lock (ReadPhysicalMember<Lock>(Mailbox, "_gate"))
            {
                var disposing = ReadPhysicalMember<long>(Child, "_leaseState");
                Ensure(disposing == unchecked(_activeLease + 1) && (disposing & 3) == 2 &&
                    ReadPhysicalMember<bool>(Child, "_disposed") &&
                    ReadPhysicalMember<long>(Child, "_disposeFinalizedLeaseState") != disposing &&
                    ReferenceEquals(ReadPhysicalMember<object>(Child, "_dispatchState"), Mailbox) &&
                    ReadPhysicalMember<int>(Mailbox, "_activeChildDispatches") > 0,
                    "original actual pooled child is inside asynchronous Dispose awaiting the real owned decode");
            }
        }
        private static object? FindEntry(RpcSession session, long requestId)
        {
            var map = ReadPhysicalMember<object?>(session.StreamManager, "_dispatchersByRequestId");
            if (map is null) return null;
            object?[] arguments = [requestId, null];
            if (!(bool)InvokePhysical(map.GetType().GetMethod("TryGetValue")!, map, arguments)!) return null;
            var routes = arguments[1]!;
            lock (ReadPhysicalMember<Lock>(routes, "_gate"))
            {
                var named = ReadPhysicalMember<IDictionary?>(routes, "_byStreamId");
                return named is not null && named.Contains((ushort)1) ? named[(ushort)1] : null;
            }
        }
    }
}
