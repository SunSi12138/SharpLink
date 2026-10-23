using System.Buffers.Binary;
using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using System.Reflection;

namespace SharpLink.UnitTests.Runtime;

public sealed class BackpressuredSendOwnershipTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task ReservationMetricCancellationCannotStealGrantedFrame()
    {
        var scope = new AsyncLocal<bool>();
        scope.Value = true;
        await using var fixture = new Fixture();
        fixture.Session.SendPacket(fixture.Frame(1, 800));
        scope.Value = false;
        await fixture.Output.FirstFlushEntered.Task.WaitAsync(Timeout);
        using var cancellation = new CancellationTokenSource();
        var packet = new ReturnGateWriter();
        packet.ReleaseReturn();
        fixture.WriteFrame(packet, 2, 800);
        var send = fixture.Session.SendPacketWithBackpressureAsync(packet, cancellation.Token).AsTask();
        var drain = fixture.Session.CaptureBackpressuredSendDrain();
        var callbacks = 0;
        Exception? callbackFailure = null;
        using var listener = ListenForReservation(scope, () =>
        {
            Interlocked.Increment(ref callbacks);
            try { cancellation.Cancel(); }
            catch (Exception exception) { callbackFailure = exception; }
        });
        fixture.Output.Release();
        var failure = await FailureAsync(send);
        await drain.WaitAsync(Timeout);
        await fixture.Session.FlushSendQueueAsync().AsTask().WaitAsync(Timeout);
        Ensure(callbacks == 1 && callbackFailure is null && cancellation.IsCancellationRequested,
            "the exact pump reservation must synchronously run the cancellation callback");
        Ensure(failure is null, "the already claimed reservation wins over reentrant cancellation");
        var ids = await fixture.ReadRequestIdsAsync();
        Ensure(ids.Count == 2 && ids[0] == 1 && ids[1] == 2,
            "the claimed frame must enter FIFO once and remain readable until the pump returns it");
        Ensure(packet.ReturnCount == 1 && fixture.Session.QueuedSendBytes == 0,
            "a telemetry cancellation must not double-return or leak the reservation");
    }

    [Test]
    public async Task ReservationMetricStopRetainsCleanupUntilWriterReturns()
    {
        var scope = new AsyncLocal<bool>();
        scope.Value = true;
        await using var fixture = new Fixture();
        fixture.Session.SendPacket(fixture.Frame(1, 800));
        scope.Value = false;
        await fixture.Output.FirstFlushEntered.Task.WaitAsync(Timeout);
        var packet = new ReturnGateWriter();
        fixture.WriteFrame(packet, 2, 800);
        var send = fixture.Session.SendPacketWithBackpressureAsync(packet).AsTask();
        var drain = fixture.Session.CaptureBackpressuredSendDrain();
        var callbacks = 0;
        Exception? callbackFailure = null;
        using var listener = ListenForReservation(scope, () =>
        {
            Interlocked.Increment(ref callbacks);
            try { Pump(fixture.Session).GetType().GetMethod("Stop")!.Invoke(Pump(fixture.Session), null); }
            catch (Exception exception) { callbackFailure = exception; }
        });
        try
        {
            fixture.Output.Release();
            await packet.ReturnEntered.Task.WaitAsync(Timeout);
            Ensure(!drain.IsCompleted && !send.IsCompleted,
                "stopping inside a reservation callback cannot retire a physically held writer");
        }
        finally { packet.ReleaseReturn(); }
        Ensure(await FailureAsync(send) is SharpLinkException,
            "a synchronously stopped pump must reject the frame rather than enqueue it");
        await drain.WaitAsync(Timeout);
        await fixture.Session.DisposeAsync().AsTask().WaitAsync(Timeout);
        Ensure(callbacks == 1 && callbackFailure is null && packet.ReturnCount == 1 &&
            fixture.Session.QueuedSendBytes == 0,
            "stop reentrancy must preserve exactly one cleanup owner and balanced capacity");
    }

    [Test]
    public async Task ReservationMetricSiblingCancellationKeepsLiveSuccessorAndAppendedWaiter()
    {
        var scope = new AsyncLocal<bool>();
        scope.Value = true;
        await using var fixture = new Fixture();
        fixture.Session.SendPacket(fixture.Frame(1, 800));
        scope.Value = false;
        await fixture.Output.FirstFlushEntered.Task.WaitAsync(Timeout);
        using var cancellation = new CancellationTokenSource();
        var grantedPacket = new ReturnGateWriter();
        var cancelledPacket = new ReturnGateWriter();
        var successorPacket = new ReturnGateWriter();
        var appendedPacket = new ReturnGateWriter();
        cancelledPacket.ReleaseReturn();
        successorPacket.ReleaseReturn();
        appendedPacket.ReleaseReturn();
        fixture.WriteFrame(grantedPacket, 2, 400);
        fixture.WriteFrame(cancelledPacket, 3, 400);
        fixture.WriteFrame(successorPacket, 4, 400);
        fixture.WriteFrame(appendedPacket, 5, 800);
        var granted = fixture.Session.SendPacketWithBackpressureAsync(grantedPacket).AsTask();
        var cancelled = fixture.Session.SendPacketWithBackpressureAsync(cancelledPacket, cancellation.Token).AsTask();
        var successor = fixture.Session.SendPacketWithBackpressureAsync(successorPacket).AsTask();
        Task? appended = null;
        Exception? callbackFailure = null;
        using var listener = ListenForReservation(scope, () =>
        {
            try
            {
                cancellation.Cancel();
                appended = fixture.Session.SendPacketWithBackpressureAsync(appendedPacket).AsTask();
            }
            catch (Exception exception) { callbackFailure = exception; }
        });
        try
        {
            fixture.Output.Release();
            await grantedPacket.ReturnEntered.Task.WaitAsync(Timeout);
            await granted.WaitAsync(Timeout);
            await successor.WaitAsync(Timeout);
            Ensure(callbackFailure is null && appended is { IsCompleted: false },
                "the observer must append a genuinely waiting frame, without losing the live successor");
            Ensure(await FailureAsync(cancelled) is OperationCanceledException && cancelledPacket.ReturnCount == 1,
                "the removed successor retains its own independent cancellation cleanup");
            Ensure(!fixture.Session.CaptureBackpressuredSendDrain().IsCompleted,
                "the observer's appended waiter stays owned while capacity is held");
        }
        finally { grantedPacket.ReleaseReturn(); }
        await appended!.WaitAsync(Timeout);
        await fixture.Session.CaptureBackpressuredSendDrain().WaitAsync(Timeout);
        await fixture.Session.FlushSendQueueAsync().AsTask().WaitAsync(Timeout);
        var ids = await fixture.ReadRequestIdsAsync();
        Ensure(ids.Count == 4 && ids[0] == 1 && ids[1] == 2 && ids[2] == 4 && ids[3] == 5,
            "live and appended successors enter FIFO once; the cancelled successor never enters it");
        Ensure(grantedPacket.ReturnCount == 1 && successorPacket.ReturnCount == 1 &&
            appendedPacket.ReturnCount == 1 && fixture.Session.QueuedSendBytes == 0,
            "reentrant queue mutation must not leak or duplicate any remaining frame");
    }

    [Test]
    public async Task FinalReservationMetricKeepsReentrantlyPublishedWaiterWakeable()
    {
        await using var fixture = new Fixture();
        fixture.Session.SendPacket(fixture.Frame(1, 1000));
        await fixture.Output.FirstFlushEntered.Task.WaitAsync(Timeout);
        var pump = Pump(fixture.Session);
        var gate = (Lock)Field(pump, "_admissionGate")!;
        var scope = new AsyncLocal<bool>();
        using var cancellation = new CancellationTokenSource();
        var packet = new ReturnGateWriter();
        var appendedPacket = new ReturnGateWriter();
        appendedPacket.ReleaseReturn();
        fixture.WriteFrame(packet, 2, 800);
        fixture.WriteFrame(appendedPacket, 3, 800);
        Task? appended = null;
        Exception? callbackFailure = null;
        using var listener = ListenForReservation(scope, () =>
        {
            try { appended = fixture.Session.SendPacketWithBackpressureAsync(appendedPacket, cancellation.Token).AsTask(); }
            catch (Exception exception) { callbackFailure = exception; }
        });
        using var started = new ManualResetEventSlim();
        var submitted = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var producer = new Thread(() =>
        {
            scope.Value = true;
            started.Set();
            try { submitted.TrySetResult(fixture.Session.SendPacketWithBackpressureAsync(packet).AsTask()); }
            catch (Exception exception) { submitted.TrySetException(exception); }
        }) { IsBackground = true };
        try
        {
            using (gate.EnterScope())
            {
                producer.Start();
                Ensure(started.Wait(Timeout) && SpinWait.SpinUntil(
                    () => (producer.ThreadState & ThreadState.WaitSleepJoin) != 0, Timeout),
                    "the producer must reach the final recheck's admission gate while full");
                Ensure(Field(pump, "_capacityChanged") is null, "the first waiter has not published yet");
                fixture.Output.Release();
                Ensure(SpinWait.SpinUntil(() => fixture.Session.QueuedSendBytes == 0, Timeout),
                    "capacity must recover before the final recheck can acquire the gate");
            }
            await (await submitted.Task.WaitAsync(Timeout)).WaitAsync(Timeout);
            await packet.ReturnEntered.Task.WaitAsync(Timeout);
            Ensure(callbackFailure is null && appended is { IsCompleted: false },
                "the final recheck's callback must publish a genuinely capacity-blocked send");
            using (gate.EnterScope())
                Ensure(Field(pump, "_capacityChanged") is not null,
                    "the final recheck must not overwrite a queue published by its own metric callback");
            packet.ReleaseReturn();
            await appended!.WaitAsync(Timeout);
            await fixture.Session.CaptureBackpressuredSendDrain().WaitAsync(Timeout);
            await fixture.Session.FlushSendQueueAsync().AsTask().WaitAsync(Timeout);
            var ids = await fixture.ReadRequestIdsAsync();
            Ensure(ids.Count == 3 && ids[0] == 1 && ids[1] == 2 && ids[2] == 3 &&
                packet.ReturnCount == 1 && appendedPacket.ReturnCount == 1 && fixture.Session.QueuedSendBytes == 0,
                "both final-recheck and callback sends must drain once without another capacity pulse");
        }
        finally
        {
            packet.ReleaseReturn();
            cancellation.Cancel();
            if ((producer.ThreadState & ThreadState.Unstarted) == 0)
                Ensure(producer.Join(Timeout), "the dedicated producer must terminate");
        }
    }

    private static MeterListener ListenForReservation(AsyncLocal<bool> scope, Action callback)
    {
        var listener = new MeterListener();
        var observed = 0;
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == "SharpLink" && instrument.Name == "sharplink.send.queue.bytes")
                owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, bytes, _, _) =>
        {
            // Only the selected fixture operation inherits this context;
            // unrelated parallel sessions cannot trigger this observer.
            if (scope.Value && bytes > 0 && Interlocked.Exchange(ref observed, 1) == 0)
                callback();
        });
        listener.Start();
        return listener;
    }

    [Test]
    public async Task OrdinaryFifoFenceAloneCanOvertakeAPreReserveSend()
    {
        await using var fixture = new Fixture();
        fixture.Session.SendPacket(fixture.Frame(1, 800));
        await fixture.Output.FirstFlushEntered.Task.WaitAsync(Timeout);
        var send = fixture.Session.SendPacketWithBackpressureAsync(fixture.Frame(2, 800)).AsTask();
        Ensure(!send.IsCompleted, "the old response must be waiting outside FIFO");
        var fence = fixture.Session.SendPacketAndFlushAsync(fixture.Frame(3, 32)).AsTask();
        fixture.Output.Release();
        await Task.WhenAll(send, fence).WaitAsync(Timeout);
        await fixture.Session.FlushSendQueueAsync();
        var ids = await fixture.ReadRequestIdsAsync();
        Ensure(ids.IndexOf(3) < ids.IndexOf(2), "the accepted FIFO fence overtakes the pre-reserve response");
    }

    [Test]
    public async Task ColdDrainPublishesSynchronouslyAndClosesPreReserveHoleBeforeFence()
    {
        await using var fixture = new Fixture();
        fixture.Session.SendPacket(fixture.Frame(1, 800));
        await fixture.Output.FirstFlushEntered.Task.WaitAsync(Timeout);
        var send = fixture.Session.SendPacketWithBackpressureAsync(fixture.Frame(2, 800)).AsTask();
        var drain = fixture.Session.CaptureBackpressuredSendDrain();
        Ensure(!send.IsCompleted && !drain.IsCompleted,
            "the Full branch must publish ownership before returning to its caller");
        var secondDrain = fixture.Session.CaptureBackpressuredSendDrain();
        Ensure(!secondDrain.IsCompleted, "concurrent cold captures both see the producer");
        fixture.Output.Release();
        await drain.WaitAsync(Timeout);
        await send.WaitAsync(Timeout);
        await secondDrain.WaitAsync(Timeout);
        await fixture.Session.SendPacketAndFlushAsync(fixture.Frame(3, 32));
        var ids = await fixture.ReadRequestIdsAsync();
        Ensure(ids.IndexOf(2) < ids.IndexOf(3), "the drained response enters FIFO before the reverse fence");
    }

    [Test]
    public async Task ZeroQueuedBytesAndCompletedFlushDoNotProvePreReserveCompletion()
    {
        await using var fixture = new Fixture(maxQueueBytes: 32 * 1024);
        fixture.Output.Release();
        using var cancellation = new CancellationTokenSource();
        // This normal frame cannot occupy the reserved progress headroom. Its
        // reservation stays pending even on an empty queue, deterministically.
        var send = fixture.Session.SendPacketWithBackpressureAsync(
            fixture.Frame(1, 32 * 1024), cancellation.Token).AsTask();
        Ensure(!send.IsCompleted && fixture.Session.QueuedSendBytes == 0,
            "unreserved bytes are absent from the queue census");
        await fixture.Session.FlushSendQueueAsync().AsTask().WaitAsync(Timeout);
        var drain = fixture.Session.CaptureBackpressuredSendDrain();
        Ensure(!drain.IsCompleted, "a completed empty FIFO marker cannot retire the pending producer");
        cancellation.Cancel();
        Ensure(await FailureAsync(send) is OperationCanceledException, "the send observes cancellation");
        await drain.WaitAsync(Timeout);
    }

    [Test]
    public async Task CancellationRetainsOwnerThroughBlockedAndThrowingWriterReturn()
    {
        await using var fixture = new Fixture();
        fixture.Session.SendPacket(fixture.Frame(1, 800));
        await fixture.Output.FirstFlushEntered.Task.WaitAsync(Timeout);
        using var cancellation = new CancellationTokenSource();
        var packet = new ReturnGateWriter(throwOnReturn: true);
        fixture.WriteFrame(packet, 2, 800);
        var send = fixture.Session.SendPacketWithBackpressureAsync(packet, cancellation.Token).AsTask();
        var drain = fixture.Session.CaptureBackpressuredSendDrain();
        try
        {
            cancellation.Cancel();
            await packet.ReturnEntered.Task.WaitAsync(Timeout);
            Ensure(!drain.IsCompleted, "physical buffer cleanup is still part of the slow owner");
        }
        finally
        {
            packet.ReleaseReturn();
        }
        Ensure(await FailureAsync(send) is InvalidOperationException,
            "a throwing return remains visible to the send caller");
        await drain.WaitAsync(Timeout);
        Ensure(packet.ReturnCount == 1 && fixture.Session.CaptureBackpressuredSendDrain().IsCompletedSuccessfully,
            "failed cleanup releases the census exactly once without poisoning later captures");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TerminalTransitionReleasesEveryRegisteredWaiter(bool fault)
    {
        await using var fixture = new Fixture();
        fixture.Session.SendPacket(fixture.Frame(1, 800));
        await fixture.Output.FirstFlushEntered.Task.WaitAsync(Timeout);
        var sends = new List<Task>();
        var packets = new List<ReturnGateWriter>();
        for (var index = 0; index < 16; index++)
        {
            var packet = new ReturnGateWriter();
            packet.ReleaseReturn();
            fixture.WriteFrame(packet, index + 2, 800);
            packets.Add(packet);
            sends.Add(fixture.Session.SendPacketWithBackpressureAsync(packet).AsTask());
        }
        var drain = fixture.Session.CaptureBackpressuredSendDrain();
        Ensure(!drain.IsCompleted, "all blocked producers are registered");
        if (fault)
        {
            fixture.Output.FailOnRelease = true;
            fixture.Output.Release();
            await fixture.Output.FailureObserved.Task.WaitAsync(Timeout);
        }
        var disposal = fixture.Session.DisposeAsync().AsTask();
        fixture.Output.Release();
        await disposal.WaitAsync(Timeout);
        foreach (var send in sends)
            Ensure(await FailureAsync(send) is SharpLinkException,
                "terminal reservation failure must be visible to each caller");
        await drain.WaitAsync(Timeout);
        Ensure(packets.TrueForAll(packet => packet.ReturnCount == 1), "every unreserved frame returns once");
        Ensure(fixture.Session.CaptureBackpressuredSendDrain().IsCompletedSuccessfully,
            "concurrent terminal cleanup leaves no stale slow owner");
    }

    [Test]
    public async Task AlreadyCancelledFullBranchCompletesWithoutLeavingOwner()
    {
        await using var fixture = new Fixture();
        fixture.Session.SendPacket(fixture.Frame(1, 800));
        await fixture.Output.FirstFlushEntered.Task.WaitAsync(Timeout);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var packet = new ReturnGateWriter();
        packet.ReleaseReturn();
        fixture.WriteFrame(packet, 2, 800);
        var send = fixture.Session.SendPacketWithBackpressureAsync(packet, cancellation.Token);
        Ensure(send.IsCompleted, "a synchronous failed reserve must finish the Full helper synchronously");
        Ensure(await FailureAsync(send.AsTask()) is OperationCanceledException, "preserve cancellation");
        Ensure(packet.ReturnCount == 1 && fixture.Session.CaptureBackpressuredSendDrain().IsCompletedSuccessfully,
            "a synchronously completed reserve failure leaves no owner");
    }

    [Test]
    public async Task CapacityArrivingBeforeFullHelperStartsCanCompleteSynchronously()
    {
        await using var fixture = new Fixture();
        fixture.Output.Release();
        await fixture.Session.SendPacketAndFlushAsync(fixture.Frame(1, 32));
        // Enter the existing Full-branch helper directly with capacity already
        // available: the deterministic equivalent of capacity arriving between
        // TryEnqueueForBackpressure's Full result and helper invocation.
        var pump = typeof(RpcSession).GetField("_pump", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(fixture.Session)!;
        var helper = typeof(RpcSession).GetMethod(
            "AwaitBackpressureEnqueueAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var frame = new OwnedFrame(fixture.Frame(2, 32), false, null, false);
        var send = (ValueTask)helper.Invoke(fixture.Session, [pump, frame, CancellationToken.None])!;
        Ensure(send.IsCompletedSuccessfully, "late capacity can make the slow helper synchronous");
        await send;
        Ensure(fixture.Session.CaptureBackpressuredSendDrain().IsCompletedSuccessfully,
            "successful synchronous transfer must release its temporary owner");
    }

    [Test]
    public async Task DrainIsSessionLocalAndDoesNotSealFutureProducers()
    {
        await using var first = new Fixture();
        await using var unrelated = new Fixture();
        var before = first.Session.CaptureBackpressuredSendDrain();
        first.Session.SendPacket(first.Frame(1, 800));
        await first.Output.FirstFlushEntered.Task.WaitAsync(Timeout);
        using var cancellation = new CancellationTokenSource();
        var send = first.Session.SendPacketWithBackpressureAsync(first.Frame(2, 800), cancellation.Token).AsTask();
        Ensure(before.IsCompletedSuccessfully, "a past empty snapshot is not a future-producer seal");
        Ensure(!first.Session.CaptureBackpressuredSendDrain().IsCompleted,
            "a fresh capture sees the later registered producer");
        Ensure(unrelated.Session.CaptureBackpressuredSendDrain().IsCompletedSuccessfully,
            "an unrelated session cannot join the blocked session's census");
        cancellation.Cancel();
        Ensure(await FailureAsync(send) is OperationCanceledException, "cancel the test producer");
    }

    [Test]
    [Arguments(SharpLinkErrorCode.DeadlineExceeded)]
    [Arguments(SharpLinkErrorCode.Unimplemented)]
    [Arguments(SharpLinkErrorCode.ResourceExhausted)]
    [Arguments(SharpLinkErrorCode.Unavailable)]
    [Arguments(SharpLinkErrorCode.DataLoss)]
    [Arguments(SharpLinkErrorCode.Internal)]
    public async Task EarlyRpcErrorWrappersPublishPendingOwnershipBeforeReturning(SharpLinkErrorCode code)
    {
        await using var fixture = new Fixture();
        fixture.Session.SendPacket(fixture.Frame(1, 1000));
        await fixture.Output.FirstFlushEntered.Task.WaitAsync(Timeout);
        var error = fixture.Session.SendRpcErrorWithBackpressureAsync(
            2, new SharpLinkException(code, "injected early rejection")).AsTask();
        var drain = fixture.Session.CaptureBackpressuredSendDrain();
        Ensure(!error.IsCompleted && !drain.IsCompleted,
            "an encoded early rejection must publish before its wrapper returns");
        fixture.Output.Release();
        await Task.WhenAll(error, drain).WaitAsync(Timeout);
        await fixture.Session.SendPacketAndFlushAsync(fixture.Frame(3, 32));
        var ids = await fixture.ReadRequestIdsAsync();
        Ensure(ids.IndexOf(2) < ids.IndexOf(3), "the error response precedes the subsequent normal fence");
    }

    [Test]
    public async Task CancelledHeadCleanupDoesNotBlockLaterSendOrDisappearFromDrain()
    {
        await using var fixture = new Fixture();
        fixture.Session.SendPacket(fixture.Frame(1, 800));
        await fixture.Output.FirstFlushEntered.Task.WaitAsync(Timeout);
        using var cancellation = new CancellationTokenSource();
        var packet = new ReturnGateWriter();
        fixture.WriteFrame(packet, 2, 800);
        var cancelled = fixture.Session.SendPacketWithBackpressureAsync(packet, cancellation.Token).AsTask();
        // Cover the other production capacity-waiting entry point as well.
        var following = fixture.Session.SendPacketAsync(
            fixture.Frame(3, 800), waitForCapacity: true, forceFlush: false).AsTask();
        var drain = fixture.Session.CaptureBackpressuredSendDrain();
        try
        {
            cancellation.Cancel();
            await packet.ReturnEntered.Task.WaitAsync(Timeout);
            fixture.Output.Release();
            await following.WaitAsync(Timeout);
            Ensure(!drain.IsCompleted, "completed reservation cannot hide the canceled writer still returning");
            await fixture.Session.FlushSendQueueAsync();
            var ids = await fixture.ReadRequestIdsAsync();
            Ensure(!ids.Contains(2) && ids.Contains(3), "a canceled head cannot starve a later send");
        }
        finally
        {
            packet.ReleaseReturn();
        }
        Ensure(await FailureAsync(cancelled) is OperationCanceledException, "canceled head retains its result");
        await drain.WaitAsync(Timeout);
        Ensure(packet.ReturnCount == 1, "head cleanup returns its frame exactly once");
    }

    [Test]
    public async Task CapacityAndCancellationRacesTransferOrReturnEachFrameExactlyOnce()
    {
        for (var iteration = 0; iteration < 32; iteration++)
        {
            await using var fixture = new Fixture();
            fixture.Session.SendPacket(fixture.Frame(1, 800));
            await fixture.Output.FirstFlushEntered.Task.WaitAsync(Timeout);
            using var cancellation = new CancellationTokenSource();
            var packet = new ReturnGateWriter();
            packet.ReleaseReturn();
            fixture.WriteFrame(packet, 2, 800);
            var send = fixture.Session.SendPacketWithBackpressureAsync(packet, cancellation.Token).AsTask();
            var drain = fixture.Session.CaptureBackpressuredSendDrain();
            using var start = new ManualResetEventSlim();
            var cancel = Task.Run(() => { start.Wait(Timeout); cancellation.Cancel(); });
            var release = Task.Run(() => { start.Wait(Timeout); fixture.Output.Release(); });
            start.Set();
            await Task.WhenAll(cancel, release).WaitAsync(Timeout);
            var failure = await FailureAsync(send);
            Ensure(failure is null or OperationCanceledException, "only transfer or cancellation may win");
            await drain.WaitAsync(Timeout);
            await fixture.Session.FlushSendQueueAsync();
            Ensure(packet.ReturnCount == 1, "the race must never leak or double-return a frame");
        }
    }

    [Test]
    public async Task SessionDisposalJoinsDetachedCancellationCleanup()
    {
        await using var fixture = new Fixture();
        fixture.Session.SendPacket(fixture.Frame(1, 800));
        await fixture.Output.FirstFlushEntered.Task.WaitAsync(Timeout);
        using var cancellation = new CancellationTokenSource();
        var packet = new ReturnGateWriter();
        fixture.WriteFrame(packet, 2, 800);
        var send = fixture.Session.SendPacketWithBackpressureAsync(packet, cancellation.Token).AsTask();
        Task? disposal = null;
        try
        {
            cancellation.Cancel();
            await packet.ReturnEntered.Task.WaitAsync(Timeout);
            disposal = fixture.Session.DisposeAsync().AsTask();
            fixture.Output.Release();
            var pump = typeof(RpcSession).GetField("_pump", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(fixture.Session)!;
            var lifetime = pump.GetType().GetField(
                "_pumpLifetime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pump)!;
            var pumpTask = lifetime as Task ?? (Task)lifetime.GetType().GetField(
                "PumpTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lifetime)!;
            await pumpTask.WaitAsync(Timeout);
            Ensure(!disposal.IsCompleted, "Dispose cannot finish while a pending frame's writer return is held");
            Ensure(!fixture.Session.CaptureBackpressuredSendDrain().IsCompleted,
                "terminal cleanup remains in the same intrinsic waiter census");
        }
        finally
        {
            packet.ReleaseReturn();
        }
        Ensure(await FailureAsync(send) is OperationCanceledException, "preserve the send's cancellation");
        await disposal!.WaitAsync(Timeout);
        Ensure(packet.ReturnCount == 1 && fixture.Session.QueuedSendBytes == 0,
            "disposal joins each queue and pending writer exactly once");
    }


    [Test]
    public async Task HeldCanceledReturnDoesNotAddGateWorkToOrdinarySuccessfulRelease()
    {
        await using var fixture = new Fixture();
        fixture.Session.SendPacket(fixture.Frame(1, 800));
        await fixture.Output.FirstFlushEntered.Task.WaitAsync(Timeout);
        using var cancellation = new CancellationTokenSource();
        var packet = new ReturnGateWriter();
        fixture.WriteFrame(packet, 2, 800);
        var send = fixture.Session.SendPacketWithBackpressureAsync(packet, cancellation.Token).AsTask();
        var drain = fixture.Session.CaptureBackpressuredSendDrain();
        var pump = Pump(fixture.Session);
        try
        {
            cancellation.Cancel();
            await packet.ReturnEntered.Task.WaitAsync(Timeout);
            Ensure(Field(pump, "_capacityChanged") is null,
                "retiring cleanup must not keep the capacity-wake slot active");
            fixture.Output.Release();
            await fixture.Session.SendPacketAndFlushAsync(fixture.Frame(3, 32));
            var gate = (Lock)Field(pump, "_admissionGate")!;
            using (gate.EnterScope())
            {
                fixture.Session.SendPacket(fixture.Frame(4, 32));
                Ensure(SpinWait.SpinUntil(() => fixture.Session.QueuedSendBytes == 0 &&
                    fixture.Session.HasPendingSendPumpIdleWait, Timeout),
                    "an ordinary successful release must finish while the unrelated cleanup gate is held");
            }
            Ensure(!drain.IsCompleted && !fixture.Session.CaptureBackpressuredSendDrain().IsCompleted,
                "both cold snapshots still own physical cleanup despite the empty capacity slot");
        }
        finally { packet.ReleaseReturn(); }
        Ensure(await FailureAsync(send) is OperationCanceledException, "preserve cancellation");
        await drain.WaitAsync(Timeout);
    }

    [Test]
    public async Task NewWaitersPrecedeHeldRetiringCleanupInTheSameOwnershipList()
    {
        await using var fixture = new Fixture(maxQueueBytes: 32 * 1024);
        fixture.Output.Release();
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        var packet = new ReturnGateWriter();
        fixture.WriteFrame(packet, 1, 32 * 1024);
        var first = fixture.Session.SendPacketWithBackpressureAsync(packet, firstCancellation.Token).AsTask();
        Task? second = null;
        try
        {
            firstCancellation.Cancel();
            await packet.ReturnEntered.Task.WaitAsync(Timeout);
            second = fixture.Session.SendPacketWithBackpressureAsync(
                fixture.Frame(2, 32 * 1024), secondCancellation.Token).AsTask();
            var queue = Field(Pump(fixture.Session), "_capacityChanged")!;
            var head = Field(queue, "Head")!;
            Ensure(!(bool)Field(head, "Completing")! && ReferenceEquals(head, Field(queue, "LastWaiting")),
                "the waiting prefix must precede the physically held retiring suffix");
            Ensure((bool)Field(Field(head, "Next")!, "Completing")!, "old cleanup stays in the suffix");
            await fixture.Session.SendPacketAndFlushAsync(fixture.Frame(3, 32));
            secondCancellation.Cancel();
            Ensure(await FailureAsync(second) is OperationCanceledException, "new waiter can cancel independently");
            Ensure(Field(Pump(fixture.Session), "_capacityChanged") is null &&
                !fixture.Session.CaptureBackpressuredSendDrain().IsCompleted,
                "no real waiter remains, but old physical cleanup is still owned");
        }
        finally
        {
            secondCancellation.Cancel();
            packet.ReleaseReturn();
        }
        Ensure(await FailureAsync(first) is OperationCanceledException, "first cancellation completes after return");
        if (second is not null) await FailureAsync(second);
        await fixture.Session.CaptureBackpressuredSendDrain().WaitAsync(Timeout);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReleaseOrStopBeforeFirstWaiterPublicationIsObservedByFinalRecheck(bool stop)
    {
        await using var fixture = new Fixture();
        fixture.Session.SendPacket(fixture.Frame(1, 1000));
        await fixture.Output.FirstFlushEntered.Task.WaitAsync(Timeout);
        var pump = Pump(fixture.Session);
        var gate = (Lock)Field(pump, "_admissionGate")!;
        var packet = new ReturnGateWriter();
        packet.ReleaseReturn();
        fixture.WriteFrame(packet, 2, 800);
        using var started = new ManualResetEventSlim();
        var submitted = new TaskCompletionSource<(Task Send, bool CompletedSynchronously)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var producer = new Thread(() =>
        {
            started.Set();
            try
            {
                var send = fixture.Session.SendPacketWithBackpressureAsync(packet);
                submitted.TrySetResult((send.AsTask(), send.IsCompleted));
            }
            catch (Exception exception) { submitted.TrySetException(exception); }
        })
        { IsBackground = true };
        using (gate.EnterScope())
        {
            producer.Start();
            Ensure(started.Wait(Timeout) && SpinWait.SpinUntil(
                () => (producer.ThreadState & ThreadState.WaitSleepJoin) != 0, Timeout),
                "the first producer must be blocked on the actual admission gate");
            Ensure(Field(pump, "_capacityChanged") is null, "no waiter has been published yet");
            if (stop) pump.GetType().GetMethod("Stop")!.Invoke(pump, null);
            fixture.Output.Release();
            Ensure(SpinWait.SpinUntil(() => fixture.Session.QueuedSendBytes == 0, Timeout),
                "the original reservation is released before the first waiter can publish");
        }
        var result = await submitted.Task.WaitAsync(Timeout);
        Ensure(result.CompletedSynchronously, "final recheck observes released capacity or stop without stranding");
        var failure = await FailureAsync(result.Send);
        Ensure(stop ? failure is SharpLinkException : failure is null, "preserve terminal versus accepted result");
        if (!stop) await fixture.Session.FlushSendQueueAsync();
        Ensure(packet.ReturnCount == 1 && fixture.Session.CaptureBackpressuredSendDrain().IsCompletedSuccessfully,
            "immediate final recheck leaves no physical owner or duplicate return");
        var marker = pump.GetType().GetField("RegistrationInProgress", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        Ensure(Field(marker, "Head") is null && Field(marker, "LastWaiting") is null,
            "the shared immutable publication marker must never contain session operations");
    }

    private static object Pump(RpcSession session) => Field(session, "_pump")!;
    private static object? Field(object owner, string name)
        => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);

    private static async Task<Exception?> FailureAsync(Task task)
    {
        try { await task.WaitAsync(Timeout); return null; }
        catch (Exception exception) { return exception; }
    }

    private static void Ensure(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        private readonly Pipe _input = new();
        private readonly Pipe _output = new();
        private readonly SharpLinkRuntimeContext _context;
        internal GatePipeWriter Output { get; }
        internal RpcSession Session { get; }

        internal Fixture(int maxQueueBytes = 1024)
        {
            _context = new SharpLinkRuntimeContextBuilder().Configure(options =>
            {
                options.FlowControl.MaxSendQueueBytes = maxQueueBytes;
                options.PerformanceProfile = SharpLinkPerformanceProfile.LowLatency;
            }).Build(includeGeneratedAssemblyCatalog: false);
            Output = new GatePipeWriter(_output.Writer);
            Session = RpcSessionTestFixture.CreateSessionOverTestTransport(
                Guid.NewGuid().ToString(), _input.Reader, Output, RpcSessionTestFixture.ServerOptions(_context));
        }

        internal IRpcByteBufferWriter Frame(long requestId, int payloadBytes)
        {
            var writer = Session.RuntimeContext.Buffers.Rent();
            WriteFrame(writer, requestId, payloadBytes);
            return writer;
        }

        internal void WriteFrame(IRpcByteBufferWriter writer, long requestId, int payloadBytes)
        {
            using (writer.BeginPacketScope(ProtocolV2FrameType.Response, ProtocolV2FrameFlags.None, (ulong)requestId))
            {
                writer.GetSpan(payloadBytes)[..payloadBytes].Clear();
                writer.Advance(payloadBytes);
            }
        }

        internal async Task<List<long>> ReadRequestIdsAsync()
        {
            var read = await _output.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
            var bytes = read.Buffer.ToArray();
            var result = new List<long>();
            for (var offset = 0; offset < bytes.Length;)
            {
                var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1));
                result.Add(BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset + 7)));
                offset += ProtocolV2Constants.HeaderBytes + length;
            }
            _output.Reader.AdvanceTo(read.Buffer.End);
            return result;
        }

        public async ValueTask DisposeAsync()
        {
            Output.Release();
            await Session.DisposeAsync().AsTask().WaitAsync(Timeout);
            await _input.Writer.CompleteAsync();
            await _output.Reader.CompleteAsync();
            _context.Dispose();
        }
    }

    internal sealed class GatePipeWriter(PipeWriter inner) : PipeWriter
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FirstFlushEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FailureObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool FailOnRelease { get; set; }
        internal void Release() => _release.TrySetResult();
        public override void Advance(int bytes) => inner.Advance(bytes);
        public override void CancelPendingFlush() { Release(); inner.CancelPendingFlush(); }
        public override void Complete(Exception? exception = null) { Release(); inner.Complete(exception); }
        public override ValueTask CompleteAsync(Exception? exception = null) { Release(); return inner.CompleteAsync(exception); }
        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
        public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            FirstFlushEntered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            if (FailOnRelease)
            {
                FailureObserved.TrySetResult();
                throw new IOException("injected transport failure");
            }
            return await inner.FlushAsync(cancellationToken);
        }
    }

    private sealed class ReturnGateWriter(bool throwOnReturn = false) : IRpcByteBufferWriter
    {
        private readonly PooledByteBufferWriter _inner = new();
        private readonly ManualResetEventSlim _release = new();
        private int _returnCount;
        internal TaskCompletionSource ReturnEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ReturnCount => Volatile.Read(ref _returnCount);
        internal void ReleaseReturn() => _release.Set();
        public int WrittenCount => _inner.WrittenCount;
        public ReadOnlyMemory<byte> WrittenMemory => _inner.WrittenMemory;
        public Span<byte> WrittenSpan => _inner.WrittenSpan;
        public int Capacity => _inner.Capacity;
        public void Advance(int count) => _inner.Advance(count);
        public Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);
        public Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);
        public void Clear() => _inner.Clear();
        public void Dispose()
        {
            Interlocked.Increment(ref _returnCount);
            ReturnEntered.TrySetResult();
            if (!_release.Wait(Timeout)) throw new TimeoutException("writer return was not released");
            _inner.Dispose();
            if (throwOnReturn) throw new InvalidOperationException("injected writer cleanup failure");
        }
    }
}
