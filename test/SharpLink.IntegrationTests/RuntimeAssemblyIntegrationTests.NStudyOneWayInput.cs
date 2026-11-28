using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public async Task NStudyOneWayInputKeepsLocalCompletionAndDrainsNormally(string transport)
    {
        const int streamWindow = 128;
        const int connectionWindow = 256;
        const int itemsPerStream = 16;
        const int itemBytes = 64;
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = guard.Token;
        var trace = new NStudyTrace(transport);
        var senderCompression = new NStudyCountingCompression();
        var receiverCompression = new NStudyCountingCompression();
        using var contracts = PluginBundle.Load($"n-oneway-input-{transport}", loadService: false);
        var staticLoad = SharpLinkAssemblyManifestLoader.TryLoad(
            typeof(IShutdownCleanupProbe).Assembly, out var staticManifest);
        var dynamicLoad = SharpLinkAssemblyManifestLoader.TryLoad(contracts.ContractAssembly, out var manifest);
        Ensure(staticLoad.Succeeded && dynamicLoad.Succeeded, "generated manifests load");
        var source = new FixedGeneratedManifestSource([staticManifest!]);
        var bCid = staticManifest!.Contracts.Single(c => c.ContractType == typeof(IShutdownCleanupProbe)).ContractId;
        var aCid = manifest!.Contracts.Single(c => c.ContractType == contracts.ContractType).ContractId;
        await using var provider = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
        var builder = NewNStudyServer(source, provider);
        void ConfigureWindows(SharpLinkRuntimeOptions options, NStudyCountingCompression compression)
        {
            options.FlowControl.StreamReceiveWindowBytes = streamWindow;
            options.FlowControl.ConnectionReceiveWindowBytes = connectionWindow;
            options.Compression.Providers.Add(compression);
        }
        builder.UseRuntime(options => ConfigureWindows(options, receiverCompression))
            .UseAdmissionControl(options =>
            {
                options.AddContract(aCid, rule => rule.UseConcurrency(1));
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 64 * 1024;
                options.MaxQueueDelay = TimeSpan.FromSeconds(15);
                options.QueueOneWayCalls = true;
            });
        string? memoryName = transport == "shared-memory" ? $"sharplink-n-oneway-{Guid.NewGuid():N}" : null;
        if (memoryName is null)
            builder.UseTcp(0, IPAddress.Loopback);
        else
            builder.UseSharedMemory(memoryName);
        var port = memoryName is null ? ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port : 0;
        await using var server = (SharpLinkServer)builder.Build();
        await using var client = NewNStudyClient(source, port, memoryName, settings => settings
            .UseRuntime(options => ConfigureWindows(options, senderCompression))
            .UseRequestCompressionPolicy(new SharpLinkCompressionSendPolicy
            {
                MinimumPayloadBytes = 32,
                MinimumSavingsBytes = 1,
                MinimumSavingsRatio = 0
            }));
        var path = Path.Combine(Path.GetDirectoryName(contracts.ContractAssembly.Location)!,
            "SharpLink.DynamicPlugin.Services.dll");
        var oldLoader = new SharedContractServiceLoadContext(contracts.ContractAssembly);
        var newLoader = new SharedContractServiceLoadContext(contracts.ContractAssembly);
        Type? oldType = null;
        var oldInput = new NStudyInput();
        var activeInput = new NStudyInput();
        try
        {
            var oldAssembly = oldLoader.LoadFromAssemblyPath(path);
            var newAssembly = newLoader.LoadFromAssemblyPath(path);
            oldType = oldAssembly.GetType("SharpLink.DynamicPlugin.DynamicPluginService", throwOnError: true)!;
            var newType = newAssembly.GetType("SharpLink.DynamicPlugin.DynamicPluginService", throwOnError: true)!;
            Ensure(server.RegisterAssembly(contracts.ContractAssembly).Succeeded &&
                   server.RegisterAssembly(oldAssembly).Succeeded &&
                   client.RegisterAssembly(contracts.ContractAssembly).Succeeded, "R1 and exact contract register");
            await server.StartAsync(ct);
            await client.ConnectAsync(ct);
            var (b, bBinding) = await PrepareNStudySiblingAsync(client, bCid, ct);
            var receiver = CreateNStudyExactGenerated(client, contracts.ContractType);
            var timeout = TimeSpan.FromSeconds(5);
            var a1 = await receiver.PrepareExact(new(bBinding.Authority, aCid, manifest.RpcAssemblyHash,
                server.ReadNStudyTarget(oldAssembly, aCid)), timeout, ct);
            var connection = NStudyClientConnection(client);
            var session = connection.Session;
            var peer = NStudyServerConnection(server);
            var flow = ((RpcSessionProtocolState)typeof(RpcSession).GetField("_protocolState",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!).FlowController!;
            Ensure(a1.Authority == bBinding.Authority && bBinding.Hash == staticManifest.RpcAssemblyHash &&
                   a1.Authority.PhysicalSession == peer.NStudyPhysicalIdentity &&
                   peer.AuthenticationContext?.Subject == "n-validation" &&
                   (memoryName is null ? session.RemoteEndPoint is IPEndPoint :
                       session.Input is SharedMemoryPipeReader) &&
                   session.NegotiatedOptions?.StreamReceiveWindowBytes == streamWindow &&
                   flow.SendConnectionCredit == connectionWindow && session.CompressionProfile == senderCompression.WireProfile,
                "actual authenticated transport enables the bounded windows and compression");
            var kernel = server.CurrentAdmissionProgramForTests!.Kernel;
            var oldModule = GetDynamicModule(server, oldAssembly);
            var a = receiver.Service;
            Task Notify(IAsyncEnumerable<int> input, CancellationToken token) =>
                InvokeValueTaskAsync(a, contracts.ContractType, "NotifyStreamAsync", input, token).AsTask();
            Task NotifyBatch() => InvokeValueTaskAsync(a, contracts.ContractType, "NotifyBatchAsync",
                Enumerable.Repeat((byte)1, 512).ToArray(), NStudyBatchItems(itemsPerStream, itemBytes, ct),
                NStudyBatchItems(itemsPerStream, itemBytes, ct), ct).AsTask();
            var busy = InvokeValueTaskAsync<int>(a, contracts.ContractType, "BlockAsync", ct).AsTask();
            await ReadNStudyTask(oldType, "BlockStarted").WaitAsync(ct);

            await Notify(Values(1, 2), ct).WaitAsync(ct);
            Ensure(await b.TouchAsync(1, ct) == 101 && !busy.IsCompleted &&
                   kernel.QueuedCalls == 1 && peer.Session.StreamManager.ActiveStreamCount == 1 &&
                   NStudyCount(oldType, "Notifications") == 0 && connection.PendingCalls.NStudyProducerCount == 0,
                "OneWay finishes local input while the ordinary remote handler is still awaiting admission");
            var creditBeforeReject = flow.SendConnectionCredit;
            var rejectedCounter = typeof(SharpLinkServer).GetField("_rejectedOneWayCalls",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var rejectedBefore = (long)rejectedCounter.GetValue(server)!;
            trace.Record("QueuedOneWayCompletesLocallyBeforeItsHandlerStarts", a1);

            await NotifyBatch().WaitAsync(ct);
            Ensure(await b.TouchAsync(2, ct) == 102, "unrelated B survives the normal quota rejection");
            await WaitForNStudyAsync(() => flow.SendConnectionCredit == creditBeforeReject, ct);
            Ensure(kernel.QueuedCalls == 1 && peer.Session.StreamManager.ActiveStreamCount == 1 &&
                   flow.ActiveSendStreamCount == 0 &&
                   senderCompression.Compressed == 1 + 2 * itemsPerStream && receiverCompression.Decoded == 0 &&
                   (long)rejectedCounter.GetValue(server)! == rejectedBefore + 1 &&
                   NStudyCount(oldType, "NotifiedBatchBytes") == 0 && !busy.IsCompleted,
                "queue-full OneWay discards the intact compressed request and >window input without decompression");
            trace.Record("FullQuotaDiscardsCompressedInputAndReturnsCredit", a1);

            oldType.GetMethod("ReleaseBlock")!.Invoke(null, null);
            Ensure(await busy.WaitAsync(ct) == 42, "ordinary occupying call returns");
            await WaitForNStudyAsync(() => NStudyCount(oldType, "Notifications") == 3 &&
                kernel.ActivePermits == 0 && peer.Session.StreamManager.ActiveStreamCount == 0 &&
                flow.SendConnectionCredit == connectionWindow, ct);
            trace.Record("OriginallyQueuedInputReachesItsExactHandler", a1);

            var running = Notify(oldInput, ct);
            await oldInput.Started.WaitAsync(ct);
            await WaitForNStudyAsync(() => NStudyCount(oldType, "Notifications") == 6, ct);
            var replacing = server.ReplaceAssemblyAsync(oldAssembly, newAssembly, TimeSpan.FromSeconds(15), ct).AsTask();
            await WaitForNStudyAsync(() => session.NStudyPreparedCount == 1, ct);
            var a2 = await receiver.PrepareExact(new(a1.Authority, aCid, manifest.RpcAssemblyHash,
                server.ReadNStudyTarget(newAssembly, aCid)), timeout, ct);
            Ensure(peer.NStudyBindings.Snapshot() == (2, 1, 1) && a1.Authority == a2.Authority &&
                   NStudyWire.SlotIndex(a1.BindingId) != NStudyWire.SlotIndex(a2.BindingId) &&
                   session.CaptureNStudyBinding(bCid) == bBinding.BindingId &&
                   await b.TouchAsync(3, ct) == 103, "the new binding cannot take an active OneWay input slot");
            oldInput.ReleaseItems();
            await oldInput.Disposing.WaitAsync(ct);
            Ensure(!running.IsCompleted && !replacing.IsCompleted && oldModule.RemainingStreams == 1 &&
                   NStudyCount(oldType, "Disposed") == 0, "R1 and original input retain physical ownership during cleanup");
            trace.Record("ReplacePreservesRunningOneWayInputAcrossAsyncCleanup", a1, a2);
            oldInput.ReleaseDisposal();
            await running.WaitAsync(ct);
            EnsureReplacementReleased(await replacing.WaitAsync(ct), "physical R1 OneWay input exits");
            Ensure(oldInput.ExitCount == 1 && NStudyCount(oldType, "Disposed") == 1 &&
                   NStudyCount(oldType, "Notifications") == 13, "only the exact original R1 receives its full input");
            trace.Record("OldOneWayInputAndModuleExitExactlyOnce", a1, a2);

            server.DisableAdmissionControl();
            await NotifyBatch().WaitAsync(ct);
            await WaitForNStudyAsync(() => NStudyCount(newType, "NotifiedBatchBytes") == 512 + 2 * itemsPerStream * itemBytes &&
                flow.SendConnectionCredit == connectionWindow && peer.Session.StreamManager.ActiveStreamCount == 0 &&
                peer.NStudyBindings.Snapshot() == (2, 0, 0), ct);
            Ensure(receiverCompression.Decoded == 1 + 2 * itemsPerStream,
                "new R2 decodes the actual compressed head and both typed inputs under the same windows");
            trace.Record("R2DecodesTwoCompressedInputsOnTheOriginalSession", a2);

            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var cancelled = Notify(activeInput, cancel.Token);
            await activeInput.Started.WaitAsync(ct);
            await WaitForNStudyAsync(() => NStudyCount(newType, "Notifications") == 3, ct);
            var cancellationHandle = activeInput.ProducerToken.WaitHandle.SafeWaitHandle;
            var activeRequestId = peer.LastAcceptedRequestId;
            cancel.Cancel();
            await activeInput.Disposing.WaitAsync(ct);
            await WaitForNStudyAsync(() => !peer.CallCancellations.TryGetValue(activeRequestId, out _), ct);
            Ensure(connection.PendingCalls.ActiveCount == 0 && connection.PendingCalls.NStudyProducerCount == 1 &&
                   !cancellationHandle.IsClosed && !cancelled.IsCompleted &&
                   peer.Session.StreamManager.ActiveStreamCount == 1,
                "one discard route and original CTS outlive the OneWay logical cancellation");
            trace.Record("CancelledOneWayKeepsOriginalCtsAndBoundedDiscardRoute", a2);
            activeInput.ReleaseDisposal();
            await ExpectNStudyCallerCancellation(cancelled, cancel.Token, ct);
            await WaitForNStudyAsync(() => peer.Session.StreamManager.ActiveStreamCount == 0 &&
                peer.NStudyBindings.Snapshot() == (2, 0, 0) && flow.SendConnectionCredit == connectionWindow &&
                flow.ActiveSendStreamCount == 0 && connection.ActiveCallCount == 0, ct);
            Ensure(cancellationHandle.IsClosed && activeInput.ExitCount == 1 &&
                   ReferenceEquals(session, NStudyClientSession(client)) && await b.TouchAsync(4, ct) == 104,
                "physical exit closes the CTS and drains the remaining transport route without replacing the Session");
            trace.Record("CancelledOneWayEndsInputAndRestoresCreditOnSameSession", a2);
        }
        finally
        {
            oldInput.ReleaseItems();
            oldInput.ReleaseDisposal();
            activeInput.ReleaseItems();
            activeInput.ReleaseDisposal();
            oldType?.GetMethod("ReleaseBlock")!.Invoke(null, null);
            oldLoader.Unload();
            newLoader.Unload();
        }
    }

    private static async IAsyncEnumerable<byte[]> NStudyBatchItems(int count, int itemBytes,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var item = Enumerable.Repeat((byte)0x27, itemBytes).ToArray();
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
            await Task.Yield();
        }
    }

    private sealed class NStudyCountingCompression : ISharpLinkCompressionProvider
    {
        private readonly TestCompressionProvider _inner = new();
        private int _compressed;
        private int _decoded;
        public string WireProfile => _inner.WireProfile;
        internal int Compressed => Volatile.Read(ref _compressed);
        internal int Decoded => Volatile.Read(ref _decoded);

        public bool TryCompress(ReadOnlySequence<byte> input, IBufferWriter<byte> output, int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            var ok = _inner.TryCompress(input, output, maxOutputBytes, cancellationToken);
            if (ok)
                Interlocked.Increment(ref _compressed);
            return ok;
        }

        public void Decompress(ReadOnlySequence<byte> input, IBufferWriter<byte> output, int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            _inner.Decompress(input, output, maxOutputBytes, cancellationToken);
            Interlocked.Increment(ref _decoded);
        }
    }
}
