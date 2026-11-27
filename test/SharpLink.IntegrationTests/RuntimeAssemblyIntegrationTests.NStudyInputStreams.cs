using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp", "client-stream")]
    [Arguments("tcp", "duplex")]
    [Arguments("shared-memory", "client-stream")]
    [Arguments("shared-memory", "duplex")]
    public async Task NStudyEarlyInputStreamsKeepExactBindingAndPhysicalOwners(string transport, string shape)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = guard.Token;
        var trace = new NStudyTrace(transport);
        using var contracts = PluginBundle.Load($"n-input-{transport}-{shape}", loadService: false);
        var staticLoad = SharpLinkAssemblyManifestLoader.TryLoad(
            typeof(IShutdownCleanupProbe).Assembly, out var staticManifest);
        var dynamicLoad = SharpLinkAssemblyManifestLoader.TryLoad(contracts.ContractAssembly, out var manifest);
        Ensure(staticLoad.Succeeded && dynamicLoad.Succeeded, "actual generated manifests load");
        var source = new FixedGeneratedManifestSource([staticManifest!]);
        var bCid = staticManifest!.Contracts.Single(c => c.ContractType == typeof(IShutdownCleanupProbe)).ContractId;
        var aCid = manifest!.Contracts.Single(c => c.ContractType == contracts.ContractType).ContractId;
        await using var provider = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
        var builder = NewNStudyServer(source, provider);
        // One normal busy A request makes the next A stream wait. B is not
        // limited by the A contract rule and uses the same physical connection.
        builder.UseAdmissionControl(options =>
        {
            options.AddContract(aCid, rule => rule.UseConcurrency(1));
            options.MaxQueuedCalls = 1;
            options.MaxQueuedBytes = 64 * 1024;
            options.MaxQueueDelay = TimeSpan.FromSeconds(15);
        });
        string? memoryName = transport == "shared-memory" ? $"sharplink-n-input-{Guid.NewGuid():N}" : null;
        if (memoryName is null)
            builder.UseTcp(0, IPAddress.Loopback);
        else
            builder.UseSharedMemory(memoryName);
        var port = memoryName is null ? ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port : 0;
        await using var server = (SharpLinkServer)builder.Build();
        await using var client = NewNStudyClient(source, port, memoryName);
        var path = Path.Combine(Path.GetDirectoryName(contracts.ContractAssembly.Location)!,
            "SharpLink.DynamicPlugin.Services.dll");
        var oldLoader = new SharedContractServiceLoadContext(contracts.ContractAssembly);
        var newLoader = new SharedContractServiceLoadContext(contracts.ContractAssembly);
        Type? oldType = null;
        var input = new NStudyInput();
        var cancelledInput = new NStudyInput();
        var activeCancelledInput = new NStudyInput();
        try
        {
            var oldAssembly = oldLoader.LoadFromAssemblyPath(path);
            var newAssembly = newLoader.LoadFromAssemblyPath(path);
            oldType = oldAssembly.GetType("SharpLink.DynamicPlugin.DynamicPluginService", throwOnError: true)!;
            var newType = newAssembly.GetType("SharpLink.DynamicPlugin.DynamicPluginService", throwOnError: true)!;
            Ensure(server.RegisterAssembly(contracts.ContractAssembly).Succeeded &&
                   server.RegisterAssembly(oldAssembly).Succeeded &&
                   client.RegisterAssembly(contracts.ContractAssembly).Succeeded, "actual R1 and contract are registered");
            await server.StartAsync(ct);
            await client.ConnectAsync(ct);
            var (unrelated, bBinding) = await PrepareNStudySiblingAsync(client, bCid, ct);
            var receiver = CreateNStudyExactGenerated(client, contracts.ContractType);
            var timeout = TimeSpan.FromSeconds(5);
            var r1 = server.ReadNStudyTarget(oldAssembly, aCid);
            var a1 = await receiver.PrepareExact(new(bBinding.Authority, aCid, manifest.RpcAssemblyHash, r1), timeout, ct);
            var connection = NStudyClientConnection(client);
            var serverConnection = NStudyServerConnection(server);
            Ensure(a1.Authority == bBinding.Authority && bBinding.Hash == staticManifest.RpcAssemblyHash &&
                   a1.Authority.PhysicalSession == serverConnection.NStudyPhysicalIdentity &&
                   serverConnection.AuthenticationContext?.Subject == "n-validation" &&
                   (memoryName is null ? connection.Session.RemoteEndPoint is IPEndPoint :
                       connection.Session.Input is SharedMemoryPipeReader), "normal authenticated physical transport");
            var kernel = server.CurrentAdmissionProgramForTests!.Kernel;
            var table = connection.PendingCalls;
            var proxy = receiver.Service;
            var oldModule = GetDynamicModule(server, oldAssembly);
            var busy = InvokeValueTaskAsync<int>(proxy, contracts.ContractType, "BlockAsync", ct).AsTask();
            await ((Task)oldType.GetProperty("BlockStarted")!.GetValue(null)!).WaitAsync(ct);

            using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var ordinaryCancelled = CallNStudyInput(proxy, contracts.ContractType, shape, cancelledInput, cancel.Token);
                await WaitForNStudyAsync(() => kernel.QueuedCalls == 1 &&
                    server.PreAdmissionStreamBytesForDiagnostics > 0, ct);
                Ensure(cancelledInput.Enumerators == 1 && !ordinaryCancelled.IsCompleted &&
                    oldModule.RemainingStreams == 0, "the queued producer runs without a server invocation");
                cancel.Cancel();
                await ExpectNStudyCallerCancellation(ordinaryCancelled, cancel.Token, ct);
                await cancelledInput.Disposing.WaitAsync(ct);
                cancelledInput.ReleaseDisposal();
                await WaitForNStudyAsync(() => kernel.QueuedCalls == 0 && table.NStudyProducerCount == 0 &&
                    server.PreAdmissionStreamBytesForDiagnostics == 0, ct);
                Ensure(cancelledInput.ExitCount == 1 && NStudyCount(oldType, "InputStreamExits") == 0 &&
                       !busy.IsCompleted && await unrelated.TouchAsync(1, ct) == 101,
                    "queued cancellation closes the producer without invoking the handler or affecting unrelated B");
                trace.Record("QueuedCancellationDrainsEarlyInput", a1);
            }

            var call = CallNStudyInput(proxy, contracts.ContractType, shape, input, ct);
            await WaitForNStudyAsync(() => kernel.QueuedCalls == 1 && server.PreAdmissionStreamBytesForDiagnostics > 0, ct);
            Ensure(input.Enumerators == 1 && !call.IsCompleted && oldModule.RemainingStreams == 0 &&
                   await unrelated.TouchAsync(2, ct) == 102, "B completes while the input stream awaits actual admission");
            trace.Record("QueuedInputBuffersWhileBContinues", a1);

            oldType.GetMethod("ReleaseBlock")!.Invoke(null, null);
            Ensure(await busy.WaitAsync(ct) == 42, "the ordinary blocking call returns and grants the queued stream");
            await WaitForNStudyAsync(() => NStudyCount(oldType, "InputItemsConsumed") == 1, ct);
            Ensure(input.Enumerators == 1 && oldModule.RemainingCalls == 1 && oldModule.RemainingStreams == 1,
                "the exact admitted R1 reader consumes buffered input from the original producer");
            trace.Record("AdmittedR1ConsumesEarlyInput", a1);

            var replacing = server.ReplaceAssemblyAsync(oldAssembly, newAssembly, TimeSpan.FromSeconds(15), ct).AsTask();
            await WaitForNStudyAsync(() => connection.Session.NStudyPreparedCount == 1, ct);
            var r2 = server.ReadNStudyTarget(newAssembly, aCid);
            var a2 = await receiver.PrepareExact(new(a1.Authority, aCid, manifest.RpcAssemblyHash, r2), timeout, ct);
            Ensure(r2.Registration != r1.Registration && a2.Authority == a1.Authority &&
                   NStudyWire.SlotIndex(a2.BindingId) != NStudyWire.SlotIndex(a1.BindingId) &&
                   serverConnection.NStudyBindings.Snapshot() == (2, 1, 1) &&
                   connection.Session.CaptureNStudyBinding(bCid) == bBinding.BindingId &&
                   await unrelated.TouchAsync(3, ct) == 103,
                "old input keeps its exact occupied slot across Replace and B is unaffected");
            trace.Record("R2BindsWhileR1InputStillOwnsItsOldSlot", a1, a2);

            input.ReleaseItems();
            await input.Disposing.WaitAsync(ct);
            Ensure(!replacing.IsCompleted && !call.IsCompleted && NStudyCount(oldType, "Disposed") == 0 &&
                   NStudyCount(oldType, "InputStreamExits") == 0 && table.NStudyProducerCount == 1 &&
                   oldModule.RemainingStreams == 1 && NStudyCount(newType, "Created") == 0,
                "the real final producer await keeps R1's receiver and physical ownership live");
            trace.Record("OldOwnersStayLiveDuringProducerCleanup", a1, a2);

            input.ReleaseDisposal();
            var result = await call.WaitAsync(ct);
            Ensure(result.SequenceEqual(shape == "client-stream" ? [10] : new[] { 6, 14 }),
                "original generated stream data finishes in R1 without rebinding");
            EnsureReplacementReleased(await replacing.WaitAsync(ct), "old physical input stream retired");
            await WaitForNStudyAsync(() => table.ActiveCount == 0 && table.NStudyProducerCount == 0 &&
                serverConnection.NStudyBindings.Snapshot() == (2, 0, 0) && kernel.ActivePermits == 0, ct);
            Ensure(input.ExitCount == 1 && NStudyCount(oldType, "InputStreamExits") == 1 &&
                   NStudyCount(oldType, "Disposed") == 1, "producer, original handler and old service exit once");
            trace.Record("R1OwnersExitOnceAndResourcesDrain", a1, a2);

            server.DisableAdmissionControl();
            var direct = await CallNStudyInput(proxy, contracts.ContractType, shape, Values(11, 13), ct).WaitAsync(ct);
            Ensure(direct.SequenceEqual(shape == "client-stream" ? [24] : new[] { 22, 26 }) &&
                   NStudyCount(newType, "InputStreamExits") == 1 &&
                   ReferenceEquals(connection.Session, NStudyClientSession(client)),
                "ordinary R2 input also succeeds on the direct admission path on the original Session");
            await WaitForNStudyAsync(() => table.ActiveCount == 0 && table.NStudyProducerCount == 0 &&
                serverConnection.NStudyBindings.Snapshot() == (2, 0, 0), ct);
            trace.Record("R2DirectInputCompletesOnTheOriginalSession", a2);

            using var cancelActive = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var live = CallNStudyInput(proxy, contracts.ContractType, shape, activeCancelledInput, cancelActive.Token);
            await activeCancelledInput.Started.WaitAsync(ct);
            await WaitForNStudyAsync(() => NStudyCount(newType, "InputItemsConsumed") == 3, ct);
            var originalToken = activeCancelledInput.ProducerToken;
            var handle = originalToken.WaitHandle;
            var safeHandle = handle.SafeWaitHandle;
            var activeId = serverConnection.LastAcceptedRequestId;
            trace.Record("ActiveR2InputDeliversAnItemBeforeCancellation", a2);
            cancelActive.Cancel();
            await ExpectNStudyCallerCancellation(live, cancelActive.Token, ct);
            await activeCancelledInput.Disposing.WaitAsync(ct);
            await WaitForNStudyAsync(() => NStudyCount(newType, "InputStreamExits") == 2 &&
                !serverConnection.CallCancellations.TryGetValue(activeId, out _), ct);
            Ensure(table.ActiveCount == 0 && table.NStudyProducerCount == 1 &&
                   originalToken.IsCancellationRequested && !safeHandle.IsClosed &&
                   handle.WaitOne(0) && activeCancelledInput.ExitCount == 0 &&
                   await unrelated.TouchAsync(4, ct) == 104,
                "logical cancellation and remote exit do not dispose the original producer CTS or interrupt B");
            trace.Record("ActiveCancellationFinishesLogicallyWhileProducerOwnsItsCts", a2);
            activeCancelledInput.ReleaseDisposal();
            await WaitForNStudyAsync(() => table.NStudyProducerCount == 0 &&
                connection.ActiveCallCount == 0 && serverConnection.ActiveCalls == 0 &&
                serverConnection.NStudyBindings.Snapshot() == (2, 0, 0) &&
                connection.Session.StreamManager.ActiveStreamCount == 0 &&
                serverConnection.Session.StreamManager.ActiveStreamCount == 0, ct);
            Ensure(activeCancelledInput.Enumerators == 1 && activeCancelledInput.ExitCount == 1 &&
                   safeHandle.IsClosed, "the source CTS closes only after the one physical exit");
            trace.Record("CancelledProducerExitsAndReleasesCtsAndRoutes", a2);
        }
        finally
        {
            input.ReleaseItems();
            input.ReleaseDisposal();
            cancelledInput.ReleaseItems();
            cancelledInput.ReleaseDisposal();
            activeCancelledInput.ReleaseItems();
            activeCancelledInput.ReleaseDisposal();
            oldType?.GetMethod("ReleaseBlock")!.Invoke(null, null);
            oldLoader.Unload();
            newLoader.Unload();
        }
    }

    private static async Task ExpectNStudyCallerCancellation(Task call, CancellationToken caller, CancellationToken guard)
    {
        try
        {
            await call.WaitAsync(guard);
            throw new InvalidOperationException("The call should report ordinary caller cancellation.");
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested && !guard.IsCancellationRequested) { }
    }

    private static async Task<int[]> CallNStudyInput(object proxy, Type contract, string shape,
        IAsyncEnumerable<int> source, CancellationToken ct)
    {
        return shape == "client-stream"
            ? [await InvokeValueTaskAsync<int>(proxy, contract, "ClientStreamAsync", source, ct)]
            : await CollectAsync(InvokeStream(proxy, contract, "DuplexAsync", source, ct));
    }

    private sealed class NStudyInput : IAsyncEnumerable<int>
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _items = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disposing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _dispose = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _enumerators;
        private int _exits;

        internal Task Started => _started.Task;
        internal Task Disposing => _disposing.Task;
        internal int Enumerators => Volatile.Read(ref _enumerators);
        internal int ExitCount => Volatile.Read(ref _exits);
        internal CancellationToken ProducerToken { get; private set; }
        internal void ReleaseItems() => _items.TrySetResult();
        internal void ReleaseDisposal() => _dispose.TrySetResult();

        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            ProducerToken = cancellationToken;
            Interlocked.Increment(ref _enumerators);
            return ReadAsync(cancellationToken).GetAsyncEnumerator();
        }

        private async IAsyncEnumerable<int> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            try
            {
                _started.TrySetResult();
                yield return 3;
                await _items.Task.WaitAsync(ct);
                yield return 7;
            }
            finally
            {
                _disposing.TrySetResult();
                await _dispose.Task;
                Interlocked.Increment(ref _exits);
            }
        }
    }
}
