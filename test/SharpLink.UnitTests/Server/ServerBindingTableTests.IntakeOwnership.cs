using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public partial class ServerBindingTableTests
{
    [Test]
    public async Task PausedSynchronousSelectionKeepsFactsChargedThroughRetirementAndClose()
    {
        var connection = IntakeConnection(new Pipe().Reader, new Pipe().Writer);
        var slots = new ServerBindingSlotQuota(1);
        var table = connection.InitializeBindings(slots);
        var (registration, payload) = RegistrationWithPayload(1);
        var facts = registration.Facts!;
        var metadata = facts.Quota;
        var entry = Bind(table, 1, registration);
        Ensure(connection.TryStartBindingIntake());
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        table.BeforeFactsResolveForTesting = () => { entered.Set(); resume.Wait(); };
        var selection = Task.Run(() =>
        {
            Ensure(SharpLinkServer.TrySelectBoundMethod(table, entry.Token, 11, out var selected));
            return selected.Method;
        });
        try
        {
            Ensure(entered.Wait(TimeSpan.FromSeconds(5)));
            registration.Retire();
            await payload.DisposeAsync();
            connection.CloseBindings();
            Ensure(!table.CanUseCaptured(entry) && metadata.Bytes == 384 && slots.Used == 1);
            Ensure(!selection.IsCompleted);
            resume.Set();
            var selected = await selection.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(selected.IsKnown && selected.MethodId == 11 && metadata.Bytes == 384);
            connection.CompleteBindingIntake();
            Ensure(metadata.Bytes == 0 && slots.Used == 0);
            Ensure(selected.IsKnown); // Scalar is valid without retaining the full table/domain.
        }
        finally
        {
            resume.Set();
            await selection;
            table.BeforeFactsResolveForTesting = null;
            connection.CompleteBindingIntake();
            await connection.CloseAsync();
        }
    }

    [Test]
    public async Task CloseDuringStartingTransfersAcknowledgmentWithoutRefundingAStartingOwner()
    {
        var connection = IntakeConnection(new Pipe().Reader, new Pipe().Writer);
        var slots = new ServerBindingSlotQuota(1);
        var table = connection.InitializeBindings(slots);
        var (registration, payload) = RegistrationWithPayload(1);
        var metadata = registration.Facts!.Quota;
        Bind(table, 1, registration);
        using var starting = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        connection.BindingIntakeStartingForTesting = () => { starting.Set(); resume.Wait(); };
        var start = Task.Run(connection.TryStartBindingIntake);
        try
        {
            Ensure(starting.Wait(TimeSpan.FromSeconds(5)));
            registration.Retire();
            await payload.DisposeAsync();
            connection.CloseBindings();
            Ensure(slots.Used == 1 && metadata.Bytes == 384);
            resume.Set();
            Ensure(!await start.WaitAsync(TimeSpan.FromSeconds(5)));
            Ensure(slots.Used == 0 && metadata.Bytes == 0);
            Ensure(!connection.TryStartBindingIntake());
        }
        finally
        {
            resume.Set();
            await start;
            connection.BindingIntakeStartingForTesting = null;
            await connection.CloseAsync();
        }
    }

    [Test]
    public async Task NoLoopCloseAcknowledgesWithoutWaitingForAnUnstartedConsumer()
    {
        var connection = IntakeConnection(new Pipe().Reader, new Pipe().Writer);
        var slots = new ServerBindingSlotQuota(1);
        var table = connection.InitializeBindings(slots);
        var (registration, payload) = RegistrationWithPayload(1);
        var metadata = registration.Facts!.Quota;
        Bind(table, 1, registration);
        registration.Retire();
        await payload.DisposeAsync();
        connection.CloseBindings();
        Ensure(slots.Used == 0 && metadata.Bytes == 0 && !connection.TryStartBindingIntake());
        await connection.CloseAsync();
    }

    [Test]
    public async Task IdleMaintenanceWakePreservesAnUnreadPartialProtocolFrame()
    {
        var input = new Pipe();
        var output = new Pipe();
        var tracking = new IntakeTrackingReader(input.Reader);
        var connection = IntakeConnection(tracking, output.Writer);
        var slots = new ServerBindingSlotQuota(1);
        var table = connection.InitializeBindings(slots);
        var (registration, payload) = RegistrationWithPayload(1);
        var metadata = registration.Facts!.Quota;
        Bind(table, 1, registration);
        await using var server = IntakeServer();
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.BindingMaintenanceScanCompletedForTesting = () => acknowledged.TrySetResult();
        var loop = RunIntake(server, connection);
        var pong = new byte[ProtocolV2Constants.HeaderBytes + 8];
        pong[0] = ProtocolV2Constants.Magic;
        BinaryPrimitives.WriteInt32LittleEndian(pong.AsSpan(1), 8);
        pong[5] = (byte)ProtocolV2FrameType.Pong;
        try
        {
            await input.Writer.WriteAsync(pong.AsMemory(0, 7));
            await tracking.PartialExamined.Task.WaitAsync(TimeSpan.FromSeconds(5));
            registration.Retire();
            await payload.DisposeAsync();
            Ensure(metadata.Bytes == 384 && slots.Used == 1);
            connection.RequestBindingMaintenance();
            await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(metadata.Bytes == 0 && slots.Used == 0 && tracking.Consumed == 0);
            Ensure(connection.Session.IsConnected && !loop.IsCompleted);
            await input.Writer.WriteAsync(pong.AsMemory(7));
            await tracking.CompleteFrameConsumed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(tracking.Consumed == pong.Length && connection.Session.IsConnected);
            await input.Writer.CompleteAsync();
            await loop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await input.Writer.CompleteAsync();
            connection.CloseBindings();
            await connection.CloseAsync();
            await loop;
        }
    }

    [Test]
    public async Task ContinuousBufferedFramesAcknowledgeOnlyAfterTheOwnedBatchIsAdvanced()
    {
        var input = new Pipe();
        var tracking = new IntakeTrackingReader(input.Reader);
        var connection = IntakeConnection(tracking, new Pipe().Writer);
        var slots = new ServerBindingSlotQuota(1);
        var table = connection.InitializeBindings(slots);
        var (registration, payload) = RegistrationWithPayload(1);
        var metadata = registration.Facts!.Quota;
        Bind(table, 1, registration);
        await using var server = IntakeServer();
        using var batchOwned = new ManualResetEventSlim();
        using var advance = new ManualResetEventSlim();
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = 0;
        tracking.BeforeAdvance = count =>
        {
            if (count == 2 * (ProtocolV2Constants.HeaderBytes + 8) && Interlocked.Exchange(ref paused, 1) == 0)
            { batchOwned.Set(); advance.Wait(); }
        };
        connection.BindingMaintenanceScanCompletedForTesting = () => acknowledged.TrySetResult();
        var pong = new byte[ProtocolV2Constants.HeaderBytes + 8];
        pong[0] = ProtocolV2Constants.Magic;
        BinaryPrimitives.WriteInt32LittleEndian(pong.AsSpan(1), 8);
        pong[5] = (byte)ProtocolV2FrameType.Pong;
        var batch = pong.Concat(pong).ToArray();
        var loop = RunIntake(server, connection);
        try
        {
            await input.Writer.WriteAsync(batch);
            Ensure(batchOwned.Wait(TimeSpan.FromSeconds(5)));
            registration.Retire();
            await payload.DisposeAsync();
            connection.RequestBindingMaintenance();
            await input.Writer.WriteAsync(batch); // Input remains continuously available.
            Ensure(!acknowledged.Task.IsCompleted && metadata.Bytes == 384 && slots.Used == 1);
            advance.Set();
            await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(metadata.Bytes == 0 && slots.Used == 0);
            await input.Writer.CompleteAsync();
            await loop.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(tracking.Consumed == 2 * batch.Length);
        }
        finally
        {
            advance.Set();
            await input.Writer.CompleteAsync();
            await connection.CloseAsync();
            await loop;
        }
    }

    [Test]
    public async Task RetirementAtFinalScanResetRestartsFromSlotZeroWithoutNetworkInput()
    {
        var input = new Pipe();
        var connection = IntakeConnection(input.Reader, new Pipe().Writer);
        var slots = new ServerBindingSlotQuota(2);
        var table = connection.InitializeBindings(slots);
        var (first, firstPayload) = RegistrationWithPayload(1);
        var (second, secondPayload) = RegistrationWithPayload(2);
        var firstQuota = first.Facts!.Quota;
        var secondQuota = second.Facts!.Quota;
        Bind(table, 1, first); Bind(table, 2, second);
        await using var server = IntakeServer();
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scans = 0;
        connection.BindingMaintenanceScanCompletedForTesting = () =>
        {
            if (Interlocked.Increment(ref scans) == 1)
            {
                second.Retire();
                secondPayload.DeclaredFacts!.Dispose();
                connection.RequestBindingMaintenance();
            }
            else acknowledged.TrySetResult();
        };
        first.Retire();
        await firstPayload.DisposeAsync();
        connection.RequestBindingMaintenance();
        var loop = RunIntake(server, connection);
        try
        {
            await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(scans == 2 && slots.Used == 0 && firstQuota.Bytes == 0 && secondQuota.Bytes == 0);
        }
        finally
        {
            await input.Writer.CompleteAsync();
            await loop.WaitAsync(TimeSpan.FromSeconds(5));
            await secondPayload.DisposeAsync();
            await connection.CloseAsync();
        }
    }

    [Test]
    public async Task SerializedReleaseCancelAndSlotReuseReturnOnlyTheExactEntryOwner()
    {
        var quota = new ServerBindingSlotQuota(1);
        var table = new ServerBindingTable(1, quota);
        var (first, firstPayload) = RegistrationWithPayload(1);
        var (second, secondPayload) = RegistrationWithPayload(2);
        var firstFacts = first.Facts!; var secondFacts = second.Facts!;
        try
        {
            var old = Bind(table, 1, first);
            var scalar = old.ResolveMethod(11);
            Ensure(firstFacts.OwnerCountForTesting == 2);
            table.CompleteBindId(1);
            Ensure(table.Release(old.Token) && firstFacts.OwnerCountForTesting == 1);
            var current = Bind(table, 2, second);
            Ensure(current.Token != old.Token && secondFacts.OwnerCountForTesting == 2);
            Ensure(!table.Release(old.Token) && !table.Cancel(1));
            Ensure(secondFacts.OwnerCountForTesting == 2 && scalar.IsKnown);
            Ensure(table.Cancel(2) && secondFacts.OwnerCountForTesting == 1);
            Ensure(!table.Cancel(2) && quota.Used == 0);
            table.Close(); table.AcknowledgeClosedIntake();
            Ensure(firstFacts.OwnerCountForTesting == 1 && secondFacts.OwnerCountForTesting == 1);
        }
        finally
        {
            table.Close(); table.AcknowledgeClosedIntake();
            await firstPayload.DisposeAsync(); await secondPayload.DisposeAsync();
        }
        Ensure(firstFacts.Quota.Bytes == 0 && secondFacts.Quota.Bytes == 0);
    }

    [Test]
    public async Task DroppedInvalidationNotificationDoesNotKeepTheAcknowledgedFactsOwner()
    {
        var input = new Pipe();
        var connection = IntakeConnection(input.Reader, new Pipe().Writer);
        var slots = new ServerBindingSlotQuota(1);
        var table = connection.InitializeBindings(slots);
        var (registration, payload) = RegistrationWithPayload(1);
        var metadata = registration.Facts!.Quota;
        Bind(table, 1, registration);
        var held = 0;
        while (connection.TryReserveBindingControlFrame(connection.Session.BindingAuthority.Limits.QueuedControlFrames)) held++;
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.BindingMaintenanceScanCompletedForTesting = () => acknowledged.TrySetResult();
        await using var server = IntakeServer();
        registration.Retire(); await payload.DisposeAsync();
        connection.RequestBindingMaintenance();
        var loop = RunIntake(server, connection);
        try
        {
            await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(metadata.Bytes == 0 && slots.Used == 0 && !connection.TryTakeBindingInvalidation(out _));
        }
        finally
        {
            while (held-- > 0) connection.ReleaseBindingControlFrame();
            await input.Writer.CompleteAsync();
            await loop.WaitAsync(TimeSpan.FromSeconds(5));
            await connection.CloseAsync();
        }
    }

    private static async Task RunIntake(SharpLinkServer server, ServerConnectionState connection)
    {
        connection.MarkSessionLoopStarted();
        try { await server.ProcessRequestLoop(connection); }
        finally { connection.MarkSessionLoopCompleted(); }
    }

    private static ServerConnectionState IntakeConnection(PipeReader input, PipeWriter output)
    {
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport("binding-intake-" + Guid.NewGuid(),
            input, output, RpcSessionTestFixture.ServerOptions(), completeHandshake: false);
        var runtime = session.RuntimeContext;
        Ensure(session.TryCompleteHandshake(new NegotiatedSessionOptions(ProtocolV2Constants.MinorVersion,
            ProtocolV2Negotiator.MandatoryCapabilities, runtime.Protocol.MaxFramePayloadBytes,
            runtime.FlowControl.StreamReceiveWindowBytes, runtime.FlowControl.ConnectionReceiveWindowBytes,
            authority: new Protocol3BindingAuthority(Guid.NewGuid(), Guid.NewGuid(), Protocol3BindingLimits.Default))));
        var connection = new ServerConnectionState(session, new RpcSessionGeneratedServerBridge(session),
            new StripedLongMap<ServerCallCancellationState>(runtime.Concurrency), CancellationToken.None, runtime.TimeProvider);
        Ensure(connection.MarkReady(null));
        return connection;
    }

    private static SharpLinkServer IntakeServer() => (SharpLinkServer)SharpLinkServerBuilder.Create()
        .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
        .UseTransport(new IntakeListener()).Build();

    private sealed class IntakeListener : IServerTransportListener
    {
        public EndPoint? LocalEndPoint => null;
        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class IntakeTrackingReader(PipeReader inner) : PipeReader
    {
        private ReadOnlySequence<byte> _buffer;
        internal long Consumed;
        internal Action<long>? BeforeAdvance;
        internal readonly TaskCompletionSource PartialExamined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource CompleteFrameConsumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            var result = await inner.ReadAsync(cancellationToken);
            _buffer = result.Buffer;
            return result;
        }
        public override bool TryRead(out ReadResult result)
        {
            var found = inner.TryRead(out result);
            if (found) _buffer = result.Buffer;
            return found;
        }
        public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            var count = _buffer.Slice(0, consumed).Length;
            var observed = _buffer.Slice(0, examined).Length;
            Consumed += count;
            BeforeAdvance?.Invoke(count);
            inner.AdvanceTo(consumed, examined);
            if (count == 0 && observed == 7) PartialExamined.TrySetResult();
            if (Consumed == ProtocolV2Constants.HeaderBytes + 8) CompleteFrameConsumed.TrySetResult();
            _buffer = default;
        }
        public override void CancelPendingRead() => inner.CancelPendingRead();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
    }
}
