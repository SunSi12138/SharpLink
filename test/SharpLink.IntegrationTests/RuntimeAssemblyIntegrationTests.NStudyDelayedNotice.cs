using System.IO.Pipelines;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public Task NStudyCutRejectsAnActualOldCallBeforeItsNoticeArrives(string transport)
    {
        var flush = new NStudyHeldResponseFlush();
        return WithNStudyCoordinationPeersAsync(transport, NStudyCoordinationMode.Manual, twoPeers: false,
            (fixture, trace, ct) => VerifyNStudyDelayedNoticeAsync(fixture, trace, flush, ct),
            decorateServerTransport: inner => new NStudyHeldResponseListener(inner, flush));
    }

    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public Task NStudyBindAckDelayedAcrossRetirementCannotPublishTheOldRoute(string transport)
    {
        var flush = new NStudyHeldResponseFlush();
        return WithNStudyCoordinationPeersAsync(transport, NStudyCoordinationMode.Manual, twoPeers: false,
            (fixture, trace, ct) => VerifyLateBindAckAfterRetirementAsync(fixture, trace, flush, ct),
            decorateServerTransport: inner => new NStudyHeldResponseListener(inner, flush));
    }

    private static async Task VerifyLateBindAckAfterRetirementAsync(NStudyCoordinationFixture fixture,
        NStudyTrace trace, NStudyHeldResponseFlush flush, CancellationToken ct)
    {
        var (server, contracts, oldAssembly, newAssembly, _, _, first, _, _) = fixture;
        var (client, session, peer, a, b, receiver, sibling) = first;
        IRpcContractChannel channel = client.CreateContractChannel(client.GetLocalContract(contracts.ContractAssembly, a.Contract));
        var site = (SharpLinkClient.NStudyContractSite)channel;
        var permitPublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var counter = new NStudyDelayedNoticeCounter(a.Contract);
        server.ReplaceInterceptors([counter]);
        Ensure(await client.ReleaseNStudyContractAsync(a.Contract, ct) && await sibling.TouchAsync(1, ct) == 101 &&
               peer.NStudyBindings.Snapshot() == (1, 0, 0),
            "normal release leaves a single independent sibling binding before starting a new Prepare");
        var operations = session.NStudyBindOperationForDiagnostics;
        await WaitForNStudyAsync(() => peer.ActiveCalls == 0 && peer.Session.QueuedSendBytes == 0, ct);
        // If the Ack is applied before the ordered invalidation is read, hold only
        // local publication; the actual reader can consume both messages normally.
        site.BeforePublishingPreparationForTests = (_, token) => new(permitPublication.Task.WaitAsync(token));

        try
        {
            flush.Arm();
            var pending = channel.PrepareAsync(TimeSpan.FromSeconds(5), ct).AsTask();
            await flush.Entered.Task.WaitAsync(ct);
            var delayedToken = NStudyWire.BindingId(NStudyWire.SlotIndex(a.BindingId), NStudyWire.Incarnation(a.BindingId) + 1);
            Ensure(!pending.IsCompleted && session.NStudyPreparedCount == 1 &&
                   session.NStudyBindOperationForDiagnostics == operations + 1 &&
                   peer.NStudyBindings.TryResolve(delayedToken, out var delayedService) && delayedService.NStudyTarget == a.Target,
                "the normal Bind installed another R1 grant before its real Ack reached the client");
            EnsureReplacementReleased(await server.ReplaceAssemblyAsync(oldAssembly, newAssembly,
                TimeSpan.FromSeconds(5), ct), "publish R2 while the real BindAck is held in the writer");
            var target = server.ReadNStudyTarget(newAssembly, a.Contract);
            Ensure(target != a.Target && !peer.NStudyBindings.HasOpenTarget(a.Target) && !pending.IsCompleted,
                "group cut retires the granted R1 route before the delayed acknowledgement leaves the writer");
            trace.Record("RealBindAckIsStillHeldWhenItsRegistrationRetires", a with { BindingId = delayedToken }, b);

            flush.Release.TrySetResult();
            Ensure(await sibling.TouchAsync(2, ct) == 102,
                "the sibling response crosses the writer after both the original acknowledgement and retirement notice");
            permitPublication.TrySetResult();
            try
            {
                await pending.WaitAsync(ct);
                throw new InvalidOperationException("A retired grant must not be published after its invalidation.");
            }
            catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.Unavailable) { }
            Ensure(session.NStudyPreparedCount == 1 && session.CaptureNStudyBinding(b.Contract) == b.BindingId &&
                   peer.NStudyBindings.Snapshot() == (1, 0, 0) && counter.Calls == 0,
                "neither the client nor the server retained the retired A grant; B was not rebound");

            var ready = await receiver.PrepareExact(new(a.Authority, a.Contract, a.Hash, target),
                TimeSpan.FromSeconds(5), ct);
            Ensure(ready.Target == target && ready.BindingId != delayedToken &&
                   await InvokeValueTaskAsync<int>(receiver.Service, contracts.ContractType, "UnaryAsync", 3, ct) == 4 &&
                   ReferenceEquals(NStudyClientSession(client), session) && counter.Calls == 1,
                "a fresh preparation is required to reach R2, on the original physical connection");
            trace.Record("LateAckCannotPublishRetiredAAndFreshBindRestoresR2", ready, b);
        }
        finally
        {
            permitPublication.TrySetResult();
            flush.Release.TrySetResult();
            site.BeforePublishingPreparationForTests = null;
        }
    }

    [Test]
    [NotInParallel]
    [Arguments("tcp", false)]
    [Arguments("tcp", true)]
    [Arguments("shared-memory", false)]
    [Arguments("shared-memory", true)]
    public Task RetiredOneWayInputIsDrainedBeforeTheNoticeReachesTheClient(string transport, bool compressed)
    {
        var flush = new NStudyHeldResponseFlush();
        var sender = new NStudyCountingCompression();
        var receiver = new NStudyCountingCompression();

        void Configure(SharpLinkRuntimeOptions options, NStudyCountingCompression provider)
        {
            options.FlowControl.StreamReceiveWindowBytes = 128;
            options.FlowControl.ConnectionReceiveWindowBytes = 256;
            if (compressed)
                options.Compression.Providers.Add(provider);
        }

        return WithNStudyCoordinationPeersAsync(transport, NStudyCoordinationMode.Manual, twoPeers: false,
            (fixture, trace, ct) => VerifyRetiredOneWayInputAsync(fixture, trace, flush, sender, receiver, compressed, ct),
            configureClient: builder => builder.UseRuntime(options => Configure(options, sender))
                .UseRequestCompressionPolicy(new SharpLinkCompressionSendPolicy
                {
                    Enabled = compressed,
                    MinimumPayloadBytes = 32,
                    MinimumSavingsBytes = 1,
                    MinimumSavingsRatio = 0,
                }),
            configureServer: builder => builder.UseRuntime(options => Configure(options, receiver)),
            decorateServerTransport: inner => new NStudyHeldResponseListener(inner, flush));
    }

    private static async Task VerifyRetiredOneWayInputAsync(NStudyCoordinationFixture fixture, NStudyTrace trace,
        NStudyHeldResponseFlush flush, NStudyCountingCompression sender, NStudyCountingCompression decompressor,
        bool compressed, CancellationToken ct)
    {
        var (server, contracts, oldAssembly, newAssembly, oldType, newType, first, _, _) = fixture;
        var (client, session, peer, a1, _, receiver, sibling) = first;
        var route = session.CaptureNStudyRoute(a1.Contract);
        var counter = new NStudyDelayedNoticeCounter(a1.Contract);
        server.ReplaceInterceptors([counter]);
        var drain = new OneWayDrainObservation(client, server);
        await WaitForNStudyAsync(() => peer.ActiveCalls == 0 && peer.Session.QueuedSendBytes == 0, ct);

        try
        {
            flush.Arm();
            var earlier = sibling.TouchAsync(2, ct).AsTask();
            await flush.Entered.Task.WaitAsync(ct);
            EnsureReplacementReleased(await server.ReplaceAssemblyAsync(oldAssembly, newAssembly,
                TimeSpan.FromSeconds(5), ct), "R1 retired while the prior response delayed its notice");
            Ensure(route.IsOpenOn(session) && !peer.NStudyBindings.HasOpenTarget(a1.Target),
                "the client still has its R1 route while the server has sealed it");

            var previousId = peer.LastAcceptedRequestId;
            var payload = Enumerable.Repeat((byte)0x27, 512).ToArray();
            var stale = InvokeValueTaskAsync(receiver.Service, contracts.ContractType, "NotifyBatchAsync", payload,
                NStudyBatchItems(16, 64, CancellationToken.None), NStudyBatchItems(16, 64, CancellationToken.None), ct)
                .AsTask();
            await WaitForNStudyAsync(() => peer.LastAcceptedRequestId > previousId &&
                peer.Session.StreamManager.ActiveStreamCount == 2, ct);
            Ensure(!stale.IsCompleted && !earlier.IsCompleted && route.IsOpenOn(session) &&
                   counter.Calls == 0 && decompressor.Decoded == 0 && (sender.Compressed > 0) == compressed,
                "the real old head establishes discard routes without admission or decode");

            flush.Release.TrySetResult();
            Ensure(await earlier.WaitAsync(ct) == 102, "the original sibling response still succeeds");
            await stale.WaitAsync(ct);
            await drain.WaitForDrainAsync(TimeSpan.FromSeconds(5));
            Ensure(counter.Calls == 0 && decompressor.Decoded == 0 &&
                   NStudyCount(oldType, "NotifiedBatchBytes") == 0 && NStudyCount(newType, "NotifiedBatchBytes") == 0 &&
                   await sibling.TouchAsync(3, ct) == 103,
                "no generation executes or decodes the retired input; the same Session returns credit and stays usable");
            trace.Record("RetiredOneWayInputDrainsOnOriginalSessionWithoutDecode", a1);
        }
        finally { flush.Release.TrySetResult(); }
    }

    private static async Task VerifyNStudyDelayedNoticeAsync(NStudyCoordinationFixture fixture,
        NStudyTrace trace, NStudyHeldResponseFlush flush, CancellationToken ct)
    {
        var (server, contracts, oldAssembly, newAssembly, oldType, newType, first, _, _) = fixture;
        var (client, session, peer, a1, b1, receiver, b) = first;
        var a = receiver.Service;
        var route = session.CaptureNStudyRoute(a1.Contract);
        var binds = session.NStudyBindOperationForDiagnostics;
        var counter = new NStudyDelayedNoticeCounter(a1.Contract);
        server.ReplaceInterceptors([counter]);
        Ensure(await InvokeValueTaskAsync<int>(a, contracts.ContractType, "UnaryAsync", 1, ct) == 2 &&
               counter.Calls == 1, "old business route is working before the cut");
        await WaitForNStudyAsync(() => peer.ActiveCalls == 0 && peer.Session.QueuedSendBytes == 0, ct);
        try
        {
            flush.Arm();
            var bResponse = b.TouchAsync(2, ct).AsTask();
            await flush.Entered.Task.WaitAsync(ct);
            Ensure(!bResponse.IsCompleted && route.IsOpenOn(session), "a real server flush is held before any retirement notice");

            EnsureReplacementReleased(await server.ReplaceAssemblyAsync(oldAssembly, newAssembly,
                TimeSpan.FromSeconds(5), ct), "the server cuts R1 while output is held");
            var target = server.ReadNStudyTarget(newAssembly, a1.Contract);
            Ensure(target != a1.Target && !peer.NStudyBindings.HasOpenTarget(a1.Target) &&
                   route.IsOpenOn(session) && session.CaptureNStudyBinding(a1.Contract) == a1.BindingId,
                "server admission is sealed although the client still holds the original route");
            trace.Record("ServerCutPrecedesDeliveryOfRetirementNotice", a1, b1);

            var previous = peer.LastAcceptedRequestId;
            var stale = InvokeValueTaskAsync<int>(a, contracts.ContractType, "UnaryAsync", 3, ct).AsTask();
            await WaitForNStudyAsync(() => peer.LastAcceptedRequestId > previous, ct);
            Ensure(!stale.IsCompleted && !bResponse.IsCompleted && route.IsOpenOn(session) &&
                   counter.Calls == 1 && NStudyCount(oldType, "Disposed") == 1 && NStudyCount(newType, "Created") == 0,
                "the actual post-cut old request reached the server before notice delivery without dispatch");
            trace.Record("ActualOldRequestReachesTheCutWhileNoticeIsStillHeld", a1, b1);

            flush.Release.TrySetResult();
            Ensure(await bResponse.WaitAsync(ct) == 102, "the earlier real B response is delivered");
            try
            {
                _ = await stale.WaitAsync(ct);
                throw new InvalidOperationException("An exact retired request must not enter business.");
            }
            catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.Unavailable) { }
            await WaitForNStudyAsync(() => !route.IsOpenOn(session), ct);
            Ensure(counter.Calls == 1 && session.NStudyBindOperationForDiagnostics == binds,
                "the old request never invokes an interceptor or implicitly binds R2");
            var a2 = await receiver.PrepareExact(new(a1.Authority, a1.Contract, a1.Hash, target),
                TimeSpan.FromSeconds(5), ct);
            Ensure(a2.Authority == a1.Authority && a2.Target != a1.Target &&
                   await InvokeValueTaskAsync<int>(a, contracts.ContractType, "UnaryAsync", 4, ct) == 5 &&
                   await b.TouchAsync(3, ct) == 103 && counter.Calls == 2 && NStudyCount(newType, "Created") == 1 &&
                   session.NStudyBindOperationForDiagnostics == binds + 1 &&
                   ReferenceEquals(NStudyClientSession(client), session), "explicit R2 preparation restores A without replacing B or the Session");
            await WaitForNStudyAsync(() => CohortPeerDrained(first, NStudyClientConnection(client), 2), ct);
            trace.Record("RetiredCallRejectsAndExplicitBindRestoresOnlyR2", a1, a2);
        }
        finally { flush.Release.TrySetResult(); }
    }

    private sealed class NStudyDelayedNoticeCounter(long contract) : ISharpLinkServerInterceptor
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        public ValueTask InvokeAsync(SharpLinkServerInvocationContext context, SharpLinkServerInvocationDelegate next)
        {
            if (context.Method.ContractId == contract)
                Interlocked.Increment(ref _calls);
            return next(context);
        }
    }

    private sealed class NStudyHeldResponseFlush
    {
        private int _armed;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Arm() => Volatile.Write(ref _armed, 1);
        internal bool Take() => Interlocked.Exchange(ref _armed, 0) != 0;
    }

    private sealed class NStudyHeldResponseListener(IServerTransportListener inner, NStudyHeldResponseFlush flush)
        : IServerTransportListener
    {
        public EndPoint? LocalEndPoint => inner.LocalEndPoint;
        public async ValueTask<ITransportConnection> AcceptAsync(CancellationToken ct = default)
            => new NStudyHeldResponseConnection(await inner.AcceptAsync(ct), flush);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class NStudyHeldResponseConnection(ITransportConnection inner, NStudyHeldResponseFlush flush)
        : ITransportConnection
    {
        public string Id => inner.Id;
        public PipeReader Input => inner.Input;
        public PipeWriter Output { get; } = new NStudyHeldResponseWriter(inner.Output, flush);
        public EndPoint? LocalEndPoint => inner.LocalEndPoint;
        public EndPoint? RemoteEndPoint => inner.RemoteEndPoint;
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class NStudyHeldResponseWriter(PipeWriter inner, NStudyHeldResponseFlush flush) : PipeWriter
    {
        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
        public override void Advance(int bytes) => inner.Advance(bytes);
        public override void CancelPendingFlush() => inner.CancelPendingFlush();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);

        public override async ValueTask<FlushResult> FlushAsync(CancellationToken ct = default)
        {
            if (flush.Take())
            {
                flush.Entered.TrySetResult();
                await flush.Release.Task.WaitAsync(ct);
            }
            return await inner.FlushAsync(ct);
        }
    }
}
