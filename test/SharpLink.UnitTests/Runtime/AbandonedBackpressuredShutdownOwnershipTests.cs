namespace SharpLink.UnitTests.Runtime;

public sealed class AbandonedBackpressuredShutdownOwnershipTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SessionShutdownMustNotCompleteAbandonedProducerDisposal(bool cancel)
    {
        await using var fixture = new BackpressuredSendOwnershipTests.Fixture();
        fixture.Session.SendPacket(fixture.Frame(1, 800));
        await fixture.Output.FirstFlushEntered.Task.WaitAsync(Watchdog);
        using var cancellation = new CancellationTokenSource();
        var clock = new ManualTimeProvider();
        var stream = new HeldDisposalStream(fixture);
        Task pump;
        using (SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot(
                   fixture.Session.Id, authentication: null,
                   RpcDeadline.Create(TimeSpan.FromSeconds(1), clock), clock)))
        {
            pump = fixture.Session.PumpGeneratedOutboundStreamAsync(
                73, 0, stream, Int32Codec.Instance, payloadNullable: false, cancellation.Token).AsTask();
        }
        var sends = fixture.Session.CaptureBackpressuredSendDrain();
        Task? physicalWork = null;
        try
        {
            Ensure(!pump.IsCompleted && !sends.IsCompleted,
                "the generated producer's MoveNext must enter the real intrinsic send-capacity wait");
            if (cancel)
                cancellation.Cancel();
            else
                clock.Advance(TimeSpan.FromSeconds(1));
            var failure = await ObserveFailureAsync(pump);
            Ensure(cancel ? failure is OperationCanceledException :
                failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
                "the selected cancellation or deadline must remain the public terminal");
            physicalWork = SharpLinkPhysicalWork.GetAbandonedUserWork(failure);
            Ensure(physicalWork is not null && !physicalWork.IsCompleted,
                "the selected terminal must carry the still-owned producer disposal");
            await stream.DisposeEntered.Task.WaitAsync(Watchdog);
            await sends.WaitAsync(Watchdog);
            Ensure(!physicalWork!.IsCompleted && stream.DisposeCalls == 1,
                "finishing intrinsic send cleanup must not complete the held producer disposal");

            // Session shutdown closes/drains the real queue. It is a separate physical owner
            // from the abandoned generated producer; an empty send census cannot release it.
            fixture.Output.Release();
            await fixture.Session.DisposeAsync().AsTask().WaitAsync(Watchdog);
            Ensure(fixture.Session.QueuedSendBytes == 0 &&
                fixture.Session.CaptureBackpressuredSendDrain().IsCompletedSuccessfully,
                "session shutdown must balance its send queue and intrinsic pending owners");
            Ensure(!physicalWork.IsCompleted && IsSourceAlive(stream.Token),
                "queue shutdown cannot dispose the generated producer's linked lifetime");
            stream.DisposeRelease.TrySetResult();
            await physicalWork.WaitAsync(Watchdog);
            Ensure(stream.DisposeCalls == 1 && !IsSourceAlive(stream.Token),
                "only real producer disposal completes its physical owner and releases its linked source");
        }
        finally
        {
            fixture.Output.Release();
            cancellation.Cancel();
            stream.DisposeRelease.TrySetResult();
            try { await pump.WaitAsync(Watchdog); }
            catch (Exception exception) when (exception is not TimeoutException)
            {
                physicalWork ??= SharpLinkPhysicalWork.GetAbandonedUserWork(exception);
            }
            if (physicalWork is not null)
                await physicalWork.WaitAsync(Watchdog);
        }
    }

    private static async Task<Exception> ObserveFailureAsync(Task task)
    {
        try { await task.WaitAsync(Watchdog); }
        catch (Exception exception) when (exception is not TimeoutException) { return exception; }
        throw new InvalidOperationException("The generated pump must produce its selected terminal.");
    }

    private static bool IsSourceAlive(CancellationToken token)
    {
        try { _ = token.WaitHandle; return true; }
        catch (ObjectDisposedException) { return false; }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class HeldDisposalStream(BackpressuredSendOwnershipTests.Fixture fixture)
        : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        internal TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposeRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token { get; private set; }
        internal int DisposeCalls { get; private set; }
        public int Current => 7;

        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            return this;
        }

        public async ValueTask<bool> MoveNextAsync()
        {
            // Use the actual capacity-waiting API inside user work. Generated stream-data
            // publication itself uses nonwaiting SendPacket and must not be relabeled as it.
            await fixture.Session.SendPacketWithBackpressureAsync(fixture.Frame(2, 800), Token);
            return false;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            DisposeEntered.TrySetResult();
            return new ValueTask(DisposeRelease.Task);
        }
    }
}
