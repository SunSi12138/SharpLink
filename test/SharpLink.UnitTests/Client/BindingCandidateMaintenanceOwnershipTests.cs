using System.IO.Pipelines;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;
using static SharpLink.UnitTests.Client.SharpLinkClientLifecycleSharedSupport;

namespace SharpLink.UnitTests.Client;

// Actual Client/Connection/Session/pump owners. The weak-reference observation is controlled
// explicitly: these tests assert no GC deadline and do not substitute a model transport queue.
public sealed class BindingCandidateMaintenanceOwnershipTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task DuplicateRetirementAndLogicalCloseKeepExactRecordUntilPhysicalFlushDrain()
    {
        await using var fixture = new Fixture();
        var target = fixture.Target(1);
        fixture.Binding.Publication.ObserveValidatedSuccess(target.Token);
        await fixture.Binding.TrackCandidateAsync(fixture.Site, target);
        var cleanup = fixture.Binding.RetireCandidateAsync(target);
        await fixture.Output.Entered.Task.WaitAsync(Timeout);
        Ensure(ReferenceEquals(cleanup, fixture.Binding.RetireCandidateAsync(target)), "duplicate retirement joins one owner");
        Ensure(fixture.Binding.ActiveOperationCount == 1 && fixture.Client.BindingBudget.Operations == 1,
            "cleanup consumes the existing shared control allowance");
        Ensure(fixture.Binding.CandidateCount == 1 && fixture.Binding.CleanupOwnerCount == 1,
            "current record remains owned during actual flush");
        fixture.Binding.Close(new IOException("logical close with physical flush held"));
        await fixture.Output.CancelObserved.Task.WaitAsync(Timeout);
        Ensure(!cleanup.IsCompleted && !fixture.Binding.TrackingDrained.IsCompleted &&
            fixture.Client.BindingBudget.TrackingSlots == 1, "logical close cannot refund physical owners");
        fixture.Output.Release();
        Ensure(await CaptureFailure(cleanup) is not null, "the physically canceled Release reports its failure");
        await fixture.Binding.TrackingDrained.WaitAsync(Timeout);
        Ensure(fixture.Binding.CandidateCount == 0 && fixture.Binding.CleanupOwnerCount == 0 &&
            fixture.Client.BindingBudget.Operations == 0 && fixture.Client.BindingBudget.TrackingSlots == 0,
            "physical terminal drain releases each exact owner once");
    }

    [Test]
    public async Task DelayedOldSuccessUsesCleanupOnlyOwnerWithoutDamagingNewerSlot()
    {
        await using var fixture = new Fixture();
        var newer = fixture.Target(3);
        var older = fixture.Target(2);
        fixture.Binding.Publication.ObserveValidatedSuccess(newer.Token);
        await fixture.Binding.TrackCandidateAsync(fixture.Site, newer);
        await fixture.Binding.TrackCandidateAsync(fixture.Site, older);
        var oldCleanup = fixture.Binding.RetireCandidateAsync(older);
        await fixture.Output.Entered.Task.WaitAsync(Timeout);
        Ensure(fixture.Binding.IsCurrentCandidateForTesting(newer) && fixture.Binding.CandidateCount == 1 &&
            fixture.Binding.CleanupOwnerCount == 1, "delayed old result has separate bounded cleanup ownership");
        Ensure(fixture.Binding.Publication.IsEligible(newer.Token) && fixture.Session.IsConnected,
            "legitimate delayed result neither invalidates newer nor closes the Session");
        fixture.Output.Release();
        await oldCleanup.WaitAsync(Timeout);
        Ensure(fixture.Binding.IsCurrentCandidateForTesting(newer) && fixture.Binding.CandidateCount == 1 &&
            fixture.Client.BindingBudget.Operations == 0, "old completion cannot clear/refund the newer current record");
        await fixture.Binding.RetireCandidateAsync(newer).WaitAsync(Timeout);
    }

    [Test]
    public async Task NewIncarnationTransfersOldRecordBeforeOverwriteAndOldCompletionCannotClearIt()
    {
        await using var fixture = new Fixture();
        var old = fixture.Target(1);
        var replacement = fixture.Target(2);
        fixture.Binding.Publication.ObserveValidatedSuccess(old.Token);
        await fixture.Binding.TrackCandidateAsync(fixture.Site, old);
        fixture.Binding.Publication.ObserveValidatedSuccess(replacement.Token);
        await fixture.Binding.TrackCandidateAsync(fixture.Site, replacement);
        var oldCleanup = fixture.Binding.RetireCandidateAsync(old);
        await fixture.Output.Entered.Task.WaitAsync(Timeout);
        Ensure(fixture.Binding.CandidateCount == 1 && fixture.Binding.CleanupOwnerCount == 1 &&
            fixture.Client.BindingBudget.Operations == 1, "slot overwrite first transfers old exact record to shared allowance");
        fixture.Output.Release();
        await oldCleanup.WaitAsync(Timeout);
        Ensure(fixture.Binding.IsCurrentCandidateForTesting(replacement) &&
            fixture.Binding.Publication.IsEligible(replacement.Token), "reference-equal clearing cannot remove replacement");
        await fixture.Binding.RetireCandidateAsync(replacement).WaitAsync(Timeout);
    }

    [Test]
    public async Task CleanupCapacitySharesBindAllowanceAndTerminalFallbackKeepsBothOwnersUntilDrain()
    {
        await using var fixture = new Fixture(operations: 1);
        var target = fixture.Target(1);
        fixture.Binding.Publication.ObserveValidatedSuccess(target.Token);
        await fixture.Binding.TrackCandidateAsync(fixture.Site, target);
        var binding = fixture.Binding.BindAsync(new Protocol3BindRequest(Protocol3BindingRequirement.Compatible,
            fixture.Site.Local.ContractId, fixture.Site.Local.RpcAssemblyHash)).AsTask();
        await fixture.Output.Entered.Task.WaitAsync(Timeout);
        var retirement = fixture.Binding.RetireCandidateAsync(target);
        await fixture.Output.CancelObserved.Task.WaitAsync(Timeout);
        Ensure(fixture.Binding.ActiveOperationCount == 1 && fixture.Binding.CleanupOwnerCount == 0 &&
            fixture.Client.BindingBudget.Operations == 1, "cleanup cannot allocate a parallel operation cap or waiter");
        Ensure(fixture.Binding.CandidateCount == 1 && fixture.Client.BindingBudget.TrackingSlots == 1 &&
            !retirement.IsCompleted && !binding.IsCompleted, "terminal fallback retains Bind and candidate during physical hold");
        fixture.Output.Release();
        Ensure(await CaptureFailure(binding) is not null, "closed pending Bind ends without an unowned success");
        await retirement.WaitAsync(Timeout);
        await fixture.Binding.TrackingDrained.WaitAsync(Timeout);
        Ensure(fixture.Client.BindingBudget.Operations == 0 && fixture.Client.BindingBudget.TrackingSlots == 0,
            "same shared budgets are refunded after actual terminal cleanup");
    }

    [Test]
    public async Task OneSecondWorkerScansOnly256SlotsAndJoinsDrainingSessionCleanupOnStop()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = await ConnectedFixture.CreateAsync(clock);
        var scheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstTick = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondScheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var schedules = 0;
        fixture.Client.BindingMaintenanceDelayScheduledForTesting = () =>
        {
            if (Interlocked.Increment(ref schedules) == 1) scheduled.TrySetResult();
            else secondScheduled.TrySetResult();
        };
        fixture.Client.BindingMaintenanceTickCompletedForTesting = count => firstTick.TrySetResult(count);
        var target = fixture.Target(1, slot: 256);
        fixture.Binding.Publication.ObserveValidatedSuccess(target.Token);
        await fixture.Client.TrackBindingCandidateAsync(fixture.Site, target);
        await scheduled.Task.WaitAsync(Timeout);
        fixture.Binding.ForgetCandidateSiteForTesting(target);
        fixture.BeginCapacitySafeDrain(); // Explicit setup delta: keep the genuinely pooled owner until cleanup takes over.
        Ensure(fixture.Connection.State == ClientConnectionState.Draining && fixture.Session.IsConnected,
            "local drain is included, not mistaken for physical closure");
        clock.Advance(TimeSpan.FromSeconds(1));
        Ensure(await firstTick.Task.WaitAsync(Timeout) == 256 && fixture.Binding.CleanupOwnerCount == 0,
            "first tick examines exactly its bounded prefix, not every slot");
        await secondScheduled.Task.WaitAsync(Timeout);
        clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Output.Entered.Task.WaitAsync(Timeout);
        Ensure(fixture.Binding.CleanupOwnerCount == 1, "rotating cursor reaches weak-dead slot in Draining Session");
        fixture.TransferDrainProtectionToCleanup();
        var stop = fixture.Client.StopAsync().AsTask();
        await fixture.Output.CancelObserved.Task.WaitAsync(Timeout);
        Ensure(!stop.IsCompleted && fixture.Client.BindingBudget.TrackingSlots == 257,
            "Stop joins the real physical cleanup rather than abandoning the orphan owner");
        fixture.Output.Release();
        await stop.WaitAsync(Timeout);
        var trackingSealed = fixture.Client.BindingBudget.TrackingSealed;
        var candidateRecords = fixture.Client.BindingBudget.CandidateRecords;
        var trackingSlots = fixture.Client.BindingBudget.TrackingSlots;
        var timers = clock.CaptureActiveTimerDiagnostics();
        Ensure(trackingSealed && candidateRecords == 0 && trackingSlots == 0 && timers.Count == 0,
            $"joined worker has no live timer, record, or tracking owner; " +
            $"TrackingSealed={trackingSealed}, CandidateRecords={candidateRecords}, " +
            $"TrackingSlots={trackingSlots}, ActiveTimerCount={timers.Count}; {timers.Owners}");
    }

    [Test]
    public Task PreEnqueueExpiryWithThrowingObserverStillJoinsPhysicalDrainAndRefundsBindOwner()
        => AssertThrowingObserverBindFailureAsync(expireBeforeEnqueue: true);

    [Test]
    public Task BindCatchWithThrowingObserverStillJoinsPhysicalDrainAndRefundsBindOwner()
        => AssertThrowingObserverBindFailureAsync(expireBeforeEnqueue: false);

    private static async Task AssertThrowingObserverBindFailureAsync(bool expireBeforeEnqueue)
    {
        var clock = new ExpiringTimestampProvider();
        await using var fixture = new Fixture(clock: clock);
        var physicalSend = fixture.HoldOutputAsync();
        await fixture.Output.Entered.Task.WaitAsync(Timeout);
        var original = new InvalidOperationException("original Bind publication failure");
        var observerFailure = new InvalidOperationException("throwing actual budget task observer");
        var drainFailure = new InvalidOperationException("throwing Session cancellation during physical drain");
        var terminalObserved = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.BindingBudget.SetTaskObserver((task, operation) =>
        {
            Ensure(operation == "BindingTerminalDrain", "the actual Close terminal task is being registered");
            terminalObserved.TrySetResult(task);
            throw observerFailure;
        });
        using var throwingCancellation = fixture.Session.LifetimeToken.UnsafeRegister(static state =>
            throw (Exception)state!, drainFailure);
        if (expireBeforeEnqueue) clock.ExpireAfterReservationTimestamp();
        else fixture.Binding.PublicationWaitStartedForTesting = _ => throw original;

        var binding = fixture.Binding.BindAsync(new Protocol3BindRequest(Protocol3BindingRequirement.Compatible,
            fixture.Site.Local.ContractId, fixture.Site.Local.RpcAssemblyHash)).AsTask();
        var terminal = await terminalObserved.Task.WaitAsync(Timeout);
        await fixture.Output.CancelObserved.Task.WaitAsync(Timeout);
        Ensure(!binding.IsCompleted && !terminal.IsCompleted && !fixture.Binding.TrackingDrained.IsCompleted,
            "observer failure cannot skip joining the already-created, physically held terminal drain");
        Ensure(fixture.Binding.ActiveOperationCount == 1 && fixture.Client.BindingBudget.Operations == 1 &&
            fixture.Client.BindingBudget.TrackingSlots == 1, "Bind and tracking owners remain charged through physical hold");

        fixture.Output.Release();
        var failure = await CaptureFailure(binding);
        Ensure(failure is not null && ContainsException(failure, exception => ReferenceEquals(exception, observerFailure)) &&
            ContainsException(failure, exception => ReferenceEquals(exception, drainFailure)),
            "both observer and actual drain callback failures survive cleanup");
        Ensure(ContainsException(failure!, exception => expireBeforeEnqueue
                ? exception is TimeoutException && exception.Message == "Binding control deadline expired before enqueue."
                : ReferenceEquals(exception, original)), "the original failure is retained in both exact Bind branches");
        Ensure(await CaptureFailure(physicalSend) is not null, "the held actual output frame reports terminal failure");
        Ensure(await CaptureFailure(terminal) is not null &&
            await CaptureFailure(fixture.Binding.TrackingDrained) is not null, "terminal drain errors remain observable");
        Ensure(fixture.Binding.ActiveOperationCount == 0 && fixture.Client.BindingBudget.Operations == 0 &&
            fixture.Client.BindingBudget.TrackingSlots == 0 && fixture.Session.QueuedSendBytes == 0,
            "independent finally refunds Bind exactly once after real physical cleanup despite observer failure");
    }

    private static async Task<Exception?> CaptureFailure(Task task)
    {
        try { await task.WaitAsync(Timeout); return null; }
        catch (Exception exception) { return exception; }
    }

    // Only the maintenance/Stop positive setup uses this fixture. The six direct-owner cases
    // above keep their original Fixture and bodies. Production ConnectAsync owns publication
    // and eventual Connection/PendingRequestTable disposal; no membership mutation is performed.
    private sealed class ConnectedFixture : IAsyncDisposable
    {
        private readonly ConnectedTransport _transport;
        private int _drainReservation;
        internal SharpLinkClient Client { get; }
        internal ClientConnection Connection { get; }
        internal RpcSession Session => Connection.Session;
        internal ClientBindingSession Binding => Connection.Binding;
        internal ArmedHeldOutput Output => _transport.HeldOutput;
        internal SharpLinkClient.ContractCallSite Site { get; }

        private ConnectedFixture(SharpLinkClient client, ClientConnection connection, ConnectedTransport transport)
        {
            Client = client;
            Connection = connection;
            _transport = transport;
            var local = new RpcLocalContract(Guid.NewGuid(), 1, 41001, new RpcHash128(1, 2));
            Site = new SharpLinkClient.ContractCallSite(client, local, new RpcProxyOptions(), new ClientLocalContractControl(local));
        }

        internal static async Task<ConnectedFixture> CreateAsync(ManualTimeProvider clock)
        {
            var transport = new ConnectedTransport();
            SharpLinkClient? client = null;
            try
            {
                client = ClientBuilderTestHelper.Build(transport, builder => builder
                    .UseTimeProvider(clock)
                    .UseHeartbeat(TimeSpan.FromHours(1), TimeSpan.FromHours(2))
                    .UseConnectionPool(options => { options.MinConnections = 1; options.MaxConnections = 1; }));
                transport.Configure((SharpLinkRuntimeContext)client.RuntimeContext);
                await client.ConnectAsync().AsTask().WaitAsync(Timeout);
                var connection = GetOnlyReadyConnection(client); // Existing read-only inspection, never publication/membership mutation.
                Ensure(transport.ConnectCount == 1 && connection.Session.BindingAuthority == transport.Authority &&
                    connection.Session.BindingAuthority.Limits.Slots == 257 && client.BindingBudget.TrackingSlots == 257,
                    "actual ConnectAsync published one exact current-authority Session with 257 tracking slots");
                connection.AssertStateInvariant();
                // Connect starts one immediate heartbeat. Observe its real output before arming,
                // so neither handshake nor bootstrap Ping can consume the Release flush barrier.
                await transport.Raw.WaitForSentFrame(ProtocolV2FrameType.Ping).WaitAsync(Timeout);
                transport.HeldOutput.Arm();
                return new ConnectedFixture(client, connection, transport);
            }
            catch (Exception original)
            {
                transport.HeldOutput.Release();
                try
                {
                    if (client is not null) await client.StopAsync().AsTask().WaitAsync(Timeout);
                    else await transport.DisposeAsync();
                }
                catch (Exception cleanup) { throw new AggregateException(original, cleanup); }
                throw;
            }
        }

        internal SharpLinkClient.ContractCallSite.BoundTarget Target(ulong incarnation, ushort slot = 0)
            => new(Connection, Protocol3BindingToken.Create(slot, incarnation, Session.BindingAuthority.Limits.Slots));

        internal void BeginCapacitySafeDrain()
        {
            Ensure(Connection.TryReserveExactCallAdmission(), "capture original admission protection before the real cold drain");
            Volatile.Write(ref _drainReservation, 1);
            Connection.OnBindingSessionDraining();
            Ensure(Connection.RequiresCapacitySafeDrain && Connection.OccupiesConnectionCapacity &&
                Connection.CallAdmissionReservationCount == 1 && _transport.ConnectCount == 1,
                "the actual draining owner retains the single pool slot before the maintenance ticks");
            Connection.AssertStateInvariant();
        }

        internal void TransferDrainProtectionToCleanup()
        {
            Ensure(Binding.ActiveOperationCount == 1 && Binding.CleanupOwnerCount == 1,
                "the held Release owns the existing cold allowance before the admission guard is returned");
            Ensure(Interlocked.Exchange(ref _drainReservation, 0) == 1, "return the original admission guard exactly once");
            Connection.ReleaseCallAdmissionReservation();
            Ensure(Connection.CallAdmissionReservationCount == 0 && Connection.State == ClientConnectionState.Draining &&
                Connection.OccupiesConnectionCapacity && _transport.ConnectCount == 1,
                "cleanup now prevents idle retirement without opening replacement capacity");
        }

        public async ValueTask DisposeAsync()
        {
            // Failure-path safety only; the positive test awaits/asserts Stop BEFORE this runs.
            // Start normal client shutdown before returning a still-held guard to avoid reconnect.
            Output.Release();
            Task? stopping = null;
            Exception? failure = null;
            try { stopping = Client.StopAsync().AsTask(); }
            catch (Exception exception) { failure = exception; }
            try
            {
                if (Interlocked.Exchange(ref _drainReservation, 0) != 0)
                    Connection.ReleaseCallAdmissionReservation();
            }
            catch (Exception exception) { failure = RpcPublicationErrors.Combine(failure, exception); }
            if (stopping is not null)
            {
                try { await stopping.WaitAsync(Timeout); }
                catch (Exception exception) { failure = RpcPublicationErrors.Combine(failure, exception); }
            }
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            // Never manually dispose Connection or its PendingRequestTable to satisfy the oracle.
        }
    }

    // TestTransportConnection's raw frame injector/output observer are current parser users.
    // Its historical InjectSuccessfulHandshakeAsync helper is deliberately NOT used here.
    private sealed class ConnectedTransport : IClientTransportFactory, ITransportConnection
    {
        private SharpLinkRuntimeContext? _context;
        private int _connectCount;
        internal TestTransportConnection Raw { get; } = new();
        internal ArmedHeldOutput HeldOutput { get; }
        internal Protocol3BindingAuthority Authority { get; } = new(Guid.NewGuid(), Guid.NewGuid(),
            Protocol3BindingLimits.Default with { Slots = 257, Operations = 4 });
        internal int ConnectCount => Volatile.Read(ref _connectCount);

        internal ConnectedTransport() => HeldOutput = new ArmedHeldOutput(Raw.Output);
        internal void Configure(SharpLinkRuntimeContext context) => _context = context;
        public string Id => Raw.Id;
        public PipeReader Input => Raw.Input;
        public PipeWriter Output => HeldOutput;
        public System.Net.EndPoint? LocalEndPoint => Raw.LocalEndPoint;
        public System.Net.EndPoint? RemoteEndPoint => Raw.RemoteEndPoint;

        public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _connectCount) != 1)
                throw new InvalidOperationException("The connected maintenance fixture permits exactly one physical dial.");
            var context = _context ?? throw new InvalidOperationException("Fixture runtime limits were not configured.");
            using var response = new PooledByteBufferWriter();
            ProtocolV2PayloadCodec.WriteHandshakeResponse(response, new ProtocolV2HandshakeResponse(
                ProtocolV2Constants.MinorVersion, ProtocolV2Negotiator.MandatoryCapabilities,
                context.Protocol.MaxFramePayloadBytes, context.FlowControl.StreamReceiveWindowBytes,
                context.FlowControl.ConnectionReceiveWindowBytes, Authority: Authority));
            await Raw.InjectFrameAsync(ProtocolV2FrameType.HandshakeResponse, ProtocolV2FrameFlags.None,
                0, response.WrittenMemory, cancellationToken);
            using var manifest = new PooledByteBufferWriter();
            ProtocolV2ContractManifestCodec.Write(manifest, new ProtocolV2ContractManifest(0, []), context.Protocol);
            await Raw.InjectFrameAsync(ProtocolV2FrameType.ContractManifest, ProtocolV2FrameFlags.None,
                0, manifest.WrittenMemory, cancellationToken);
            return this; // Production Client.ConnectAsync sends/validates its own offer and publishes the Connection.
        }

        public ValueTask DisposeAsync() => Raw.DisposeAsync(); // Idempotent raw transport cleanup; never releases HeldOutput's barrier.
    }

    private sealed class ArmedHeldOutput(PipeWriter inner) : PipeWriter
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancelObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Arm()
        {
            if (Interlocked.Exchange(ref _armed, 1) != 0) throw new InvalidOperationException("Output barrier already armed.");
        }
        internal void Release() => _release.TrySetResult();
        public override void Advance(int bytes) => inner.Advance(bytes);
        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
        public override void CancelPendingFlush()
        {
            if (Volatile.Read(ref _armed) != 0) CancelObserved.TrySetResult();
            inner.CancelPendingFlush();
        }
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);
        public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 0)
                return await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
            using var registration = cancellationToken.UnsafeRegister(static state =>
                ((ArmedHeldOutput)state!).CancelObserved.TrySetResult(), this);
            Entered.TrySetResult();
            await _release.Task.ConfigureAwait(false); // Logical cancellation/transport disposal cannot release the physical hold.
            return await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Pipe _input = new();
        private readonly Pipe _output = new();
        internal readonly SharpLinkClient Client;
        internal readonly RpcSession Session;
        internal readonly ClientConnection Connection;
        internal readonly HeldOutput Output;
        internal readonly SharpLinkClient.ContractCallSite Site;
        internal ClientBindingSession Binding => Connection.Binding;

        internal Fixture(uint slots = 1, ushort operations = 4, TimeProvider? clock = null)
        {
            Client = ClientBuilderTestHelper.Build(new NonConnectingFactory(),
                builder => { if (clock is not null) builder.UseTimeProvider(clock); });
            var context = (SharpLinkRuntimeContext)Client.RuntimeContext;
            Output = new HeldOutput(_output.Writer);
            Session = RpcSessionTestFixture.CreateSessionOverTestTransport("candidate-maintenance", _input.Reader,
                Output, RpcSessionTestFixture.ClientOptions(context), completeHandshake: false);
            var limits = Protocol3BindingLimits.Default with { Slots = slots, Operations = operations };
            Ensure(Session.TryCompleteHandshake(new NegotiatedSessionOptions(ProtocolV2Constants.MinorVersion,
                ProtocolV2Negotiator.MandatoryCapabilities, context.Protocol.MaxFramePayloadBytes,
                context.FlowControl.StreamReceiveWindowBytes, context.FlowControl.ConnectionReceiveWindowBytes,
                authority: new Protocol3BindingAuthority(Guid.NewGuid(), Guid.NewGuid(), limits))), "exact handshake");
            Session.NotifyConnected();
            Connection = new ClientConnection(Client, Session, new CancellationTokenSource(), 8, context);
            var local = new RpcLocalContract(Guid.NewGuid(), 1, 41001, new RpcHash128(1, 2));
            Site = new SharpLinkClient.ContractCallSite(Client, local, new RpcProxyOptions(), new ClientLocalContractControl(local));
        }

        internal Task HoldOutputAsync()
        {
            var writer = Session.RentFrameWriter();
            using (writer.BeginPacketScope(ProtocolV2FrameType.Ping, ProtocolV2FrameFlags.None, 0))
            {
                writer.GetSpan(sizeof(long))[..sizeof(long)].Clear();
                writer.Advance(sizeof(long));
            }
            return Session.SendPacketAndFlushAsync(writer).AsTask();
        }

        internal SharpLinkClient.ContractCallSite.BoundTarget Target(ulong incarnation, ushort slot = 0)
            => new(Connection, Protocol3BindingToken.Create(slot, incarnation, Session.BindingAuthority.Limits.Slots));

        public async ValueTask DisposeAsync()
        {
            Output.Release();
            try { await Connection.DisposeAsync().AsTask().WaitAsync(Timeout); }
            finally
            {
                try { await Client.StopAsync().AsTask().WaitAsync(Timeout); }
                finally
                {
                    await _input.Writer.CompleteAsync();
                    await _output.Reader.CompleteAsync();
                }
            }
        }
    }

    private sealed class ExpiringTimestampProvider : TimeProvider
    {
        private int _samples = -1;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        internal void ExpireAfterReservationTimestamp() => Volatile.Write(ref _samples, 0);
        public override long GetTimestamp()
            => Volatile.Read(ref _samples) < 0 || Interlocked.Increment(ref _samples) == 1
                ? 0 : TimeSpan.FromSeconds(31).Ticks;
    }

    private sealed class HeldOutput(PipeWriter inner) : PipeWriter
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancelObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Release() => _release.TrySetResult();
        public override void Advance(int bytes) => inner.Advance(bytes);
        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
        public override void CancelPendingFlush() { CancelObserved.TrySetResult(); inner.CancelPendingFlush(); }
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);
        public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            using var registration = cancellationToken.UnsafeRegister(static state =>
                ((HeldOutput)state!).CancelObserved.TrySetResult(), this);
            Entered.TrySetResult();
            await _release.Task.ConfigureAwait(false); // Intentionally ignores logical cancellation until physical release.
            return await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
