using SharpLink.Client;

namespace SharpLink.UnitTests.Runtime;

public sealed class PendingProducerCapacityTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task CapacityOneKeepsLogicalTerminalProducerChargedAndWaiterBlocked(int family)
    {
        var owner = new Owner();
        using var table = PendingRequestTableTestFixture.Create(1, owner);
        var pending = Register(table, family);
        Ensure(table.TryAcquireProducerLifetime(pending.Id, out var physical, out var token));
        var terminal = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "logical result before physical exit");
        Ensure(table.TryComplete(pending.Id, PendingCallCompletionReason.ConnectionClosed, terminal));
        Ensure(ReferenceEquals(await pending.Observe(), terminal));
        Ensure(table.Count == 0 && table.ActiveCount == 1 && owner.Idle == 0);
        Ensure(token.IsCancellationRequested && token.WaitHandle is not null);
        ExpectCapacity(() => Register(table, family));
        Ensure(table.ActiveCount == 1); // Rejected admission must not alter the held original charge.
        var waiter = table.RentAsync<int>(waitForSlot: true, default, CancellationToken.None).AsTask();
        Ensure(!waiter.IsCompleted);
        physical.Dispose(); // Exact table attachment boundary; parent Group joins tasks/fence before this.
        EnsureDisposed(token);
        var admitted = await waiter.WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(table.Count == 1 && table.ActiveCount == 1 && owner.Idle == 1);
        Ensure(table.TryComplete(admitted.Id, PendingCallCompletionReason.ConnectionClosed, terminal));
        Ensure(ReferenceEquals(await Outcome(admitted.Operation.AsValueTask().AsTask()), terminal));
        Ensure(table.Count == 0 && table.ActiveCount == 0 && owner.Idle == 2);
        physical.Dispose();
        Ensure(owner.Idle == 2);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task PhysicalFirstCannotRefundTheStillLiveLogicalCall(int family)
    {
        var owner = new Owner();
        using var table = PendingRequestTableTestFixture.Create(1, owner);
        var pending = Register(table, family);
        Ensure(table.TryAcquireProducerLifetime(pending.Id, out var physical, out var token));
        physical.Dispose();
        Ensure(table.Count == 1 && table.ActiveCount == 1 && owner.Idle == 0);
        Ensure(token.WaitHandle is not null && !table.TryAcquireProducerLifetime(pending.Id, out _, out _));
        ExpectCapacity(() => Register(table, family));
        Ensure(table.TryComplete(pending.Id, PendingCallCompletionReason.ConnectionClosed));
        Ensure(await pending.Observe() is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed });
        Ensure(table.ActiveCount == 0 && owner.Idle == 1);
        EnsureDisposed(token);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task NeverAttachedTerminalWinnerRefundsAndFailedAttachmentOwnsNoSecondRefund(int family)
    {
        var owner = new Owner();
        using var table = PendingRequestTableTestFixture.Create(1, owner);
        var pending = Register(table, family);
        var originalToken = table.GetProducerCancellationToken(pending.Id);
        Ensure(table.TryComplete(pending.Id, PendingCallCompletionReason.SendFailure,
            new InvalidOperationException("unstarted head failed")));
        Ensure(await pending.Observe() is InvalidOperationException);
        Ensure(table.Count == 0 && table.ActiveCount == 0 && owner.Idle == 1);
        EnsureDisposed(originalToken);
        Ensure(!table.TryAcquireProducerLifetime(pending.Id, out var absent, out _));
        absent.Dispose();
        Ensure(owner.Idle == 1);
        var next = Register(table, family);
        Ensure(table.ActiveCount == 1);
        Ensure(table.TryComplete(next.Id, PendingCallCompletionReason.ConnectionClosed));
        await next.Observe();
        Ensure(table.ActiveCount == 0 && owner.Idle == 2);
    }

    [Test]
    public async Task CanceledWaiterAndDisposedTableStillAcceptOneLatePhysicalRefundDespiteIdleFailure()
    {
        var idleFailure = new InvalidOperationException("late idle owner failure");
        var owner = new Owner { IdleFailure = idleFailure };
        var table = PendingRequestTableTestFixture.Create(1, owner);
        var pending = Register(table, 2);
        Ensure(table.TryAcquireProducerLifetime(pending.Id, out var physical, out var token));
        Ensure(table.TryComplete(pending.Id, PendingCallCompletionReason.ConnectionClosed));
        await pending.Observe();
        using var cancellation = new CancellationTokenSource();
        var waiter = table.RentAsync<int>(waitForSlot: true, default, cancellation.Token).AsTask();
        Ensure(!waiter.IsCompleted);
        cancellation.Cancel();
        Ensure(await Outcome(waiter) is OperationCanceledException);
        table.Dispose(); // No logical slot remains, but its original physical capacity does.
        Ensure(table.ActiveCount == 1 && token.WaitHandle is not null);
        try { _ = table.Rent<int>(out _); throw new Exception("Disposed table admitted a new call."); }
        catch (ObjectDisposedException) { }
        Exception? failure = null;
        try { physical.Dispose(); }
        catch (Exception error) { failure = error; }
        Ensure(ReferenceEquals(failure, idleFailure) && table.ActiveCount == 0 && owner.Idle == 1);
        EnsureDisposed(token);
        physical.Dispose(); table.Dispose();
        Ensure(owner.Idle == 1);
    }

    [Test]
    public async Task FaultedLogicalOwnerStillDeliversAndDefersCapacityUntilIndependentFinalCleanup()
    {
        var logicalFailure = new InvalidOperationException("logical owner failure");
        var idleFailure = new InvalidOperationException("physical idle failure");
        var owner = new Owner { CompletionFailure = logicalFailure, IdleFailure = idleFailure };
        using var table = PendingRequestTableTestFixture.Create(1, owner);
        var pending = Register(table, 0);
        Ensure(table.TryAcquireProducerLifetime(pending.Id, out var physical, out var token));
        var terminal = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "original logical failure");
        Exception? observed = null;
        try { table.TryComplete(pending.Id, PendingCallCompletionReason.ConnectionClosed, terminal); }
        catch (Exception failure) { observed = failure; }
        Ensure(ReferenceEquals(observed, logicalFailure) && ReferenceEquals(await pending.Observe(), terminal));
        Ensure(table.Count == 0 && table.ActiveCount == 1 && owner.Idle == 0);
        ExpectCapacity(() => Register(table, 0));
        observed = null;
        try { physical.Dispose(); }
        catch (Exception failure) { observed = failure; }
        Ensure(ReferenceEquals(observed, idleFailure) && table.ActiveCount == 0 && owner.Idle == 1);
        EnsureDisposed(token);
        owner.CompletionFailure = null; owner.IdleFailure = null;
        var next = Register(table, 0);
        Ensure(table.TryComplete(next.Id, PendingCallCompletionReason.ConnectionClosed));
        await next.Observe();
        Ensure(table.ActiveCount == 0);
    }

    [Test]
    public async Task OrdinaryUnaryStillRefundsAtItsOriginalLogicalCompletion()
    {
        var owner = new Owner();
        using var table = PendingRequestTableTestFixture.Create(1, owner);
        var operation = table.Rent<int>(out var id);
        Ensure(!table.TryAcquireProducerLifetime(id, out _, out _));
        Ensure(table.TryComplete(id, PendingCallCompletionReason.ConnectionClosed));
        Ensure(await Outcome(operation.AsValueTask().AsTask()) is SharpLinkException);
        Ensure(table.Count == 0 && table.ActiveCount == 0 && owner.Idle == 1);
    }

    private static Registered Register(PendingRequestTable table, int family)
    {
        if (family == 0)
        {
            var operation = table.Rent(PendingRequestTableTestFixture.Codecs.GetCodec<int>(), PendingCallKind.ClientStreaming,
                default, CancellationToken.None, out var id);
            return new(id, () => Outcome(operation.AsValueTask().AsTask()));
        }
        if (family == 1)
        {
            var dispatcher = new Dispatcher();
            var id = table.RegisterStream(PendingCallKind.DuplexStreaming, dispatcher, default, CancellationToken.None);
            return new(id, () => dispatcher.Completed.Task);
        }
        if (family == 2)
        {
            var pending = table.RegisterOneWayClientStream(default, CancellationToken.None);
            return new(pending.Id, () => Outcome(pending.Operation.AsValueTask().AsTask()));
        }
        throw new ArgumentOutOfRangeException(nameof(family));
    }
    private readonly record struct Registered(long Id, Func<Task<Exception?>> Observe);
    private static async Task<Exception?> Outcome(Task operation)
    { try { await operation; return null; } catch (Exception failure) { return failure; } }
    private static void ExpectCapacity(Action action)
    {
        try { action(); }
        catch (SharpLinkException failure) when (failure.Code == SharpLinkErrorCode.ResourceExhausted &&
            failure.DetailCode == SharpLinkErrorDetails.ResourceExhausted.PendingRequestCapacity) { return; }
        throw new InvalidOperationException("Expected the existing exact pending-capacity refusal.");
    }
    private static void EnsureDisposed(CancellationToken token)
    {
        try { _ = token.WaitHandle; }
        catch (ObjectDisposedException) { return; }
        throw new InvalidOperationException("Original producer source was not finally disposed.");
    }
    private static void Ensure(bool condition)
    { if (!condition) throw new InvalidOperationException("Existing pending capacity transfer assertion failed."); }
    private sealed class Dispatcher : IStreamDispatcher
    {
        internal TaskCompletionSource<Exception?> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) => ValueTask.CompletedTask;
        public void Complete(bool isError, string? errorMessage)
            => Completed.TrySetResult(isError ? new Exception(errorMessage) : null);
        public void Complete(Exception? exception) => Completed.TrySetResult(exception);
    }
    private sealed class Owner : IPendingCallOwner
    {
        internal int Idle;
        internal Exception? IdleFailure;
        internal Exception? CompletionFailure;
        public void OnPendingCallCompleted(in PendingCallCompletion completion)
        {
            completion.Dispatcher?.Complete(completion.Exception);
            if (CompletionFailure is { } failure) throw failure;
        }
        public void OnProducerCancellationCallbackFailed(Exception exception) => throw exception;
        public void OnPendingCallCapacityIdle()
        { Idle++; if (IdleFailure is { } failure) throw failure; }
    }
}
