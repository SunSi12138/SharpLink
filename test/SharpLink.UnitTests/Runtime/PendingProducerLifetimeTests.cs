using System.Reflection;
using SharpLink.Client;

namespace SharpLink.UnitTests.Runtime;

public sealed class PendingProducerLifetimeTests
{
    [Test]
    public async Task RemovedSlotKeepsTheOriginalProducerSourceUntilPhysicalRelease()
    {
        var owner = new Owner();
        using var table = PendingRequestTableTestFixture.Create(1, owner);
        var pending = table.RegisterOneWayClientStream(default, CancellationToken.None);
        Ensure(table.TryAcquireProducerLifetime(pending.Id, out var lifetime, out var token));
        Ensure(token.CanBeCanceled && !token.IsCancellationRequested);
        var terminal = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "logical terminal");
        Ensure(table.TryComplete(pending.Id, PendingCallCompletionReason.ConnectionClosed, terminal));
        Ensure(!table.Contains(pending.Id) && table.ActiveCount == 1 && token.IsCancellationRequested);
        Ensure(ReferenceEquals(await Failure(pending.Operation.AsValueTask().AsTask()), terminal));
        Ensure(!table.TryAcquireProducerLifetime(pending.Id, out _, out _));
        EnsureUsable(token);
        lifetime.Dispose();
        EnsureDisposed(token);
        lifetime.Dispose(); // Copied/duplicate release cannot claim cleanup twice.
        Ensure(owner.Completed == 1 && owner.Idle == 1);
    }

    [Test]
    public async Task PhysicalReleaseBeforeResponsePreservesLogicalSourceAndRejectsReattachment()
    {
        using var table = PendingRequestTableTestFixture.Create(1);
        var operation = table.Rent(PendingRequestTableTestFixture.Codecs.GetCodec<int>(), PendingCallKind.ClientStreaming,
            default, CancellationToken.None, out var id);
        Ensure(table.TryAcquireProducerLifetime(id, out var lifetime, out var token));
        Ensure(!table.TryAcquireProducerLifetime(id, out _, out _));
        lifetime.Dispose();
        Ensure(!table.TryAcquireProducerLifetime(id, out _, out _));
        EnsureUsable(token);
        var payload = new ReadOnlySequence<byte>(BitConverter.GetBytes(42));
        Ensure(table.Dispatch(id, ref payload));
        Ensure(await operation.AsValueTask() == 42);
        EnsureDisposed(token);
    }

    [Test]
    public async Task HeldCancellationCallbackPreventsFinalCleanupAfterPhysicalRelease()
    {
        using var table = PendingRequestTableTestFixture.Create(1);
        var pending = table.RegisterOneWayClientStream(default, CancellationToken.None);
        Ensure(table.TryAcquireProducerLifetime(pending.Id, out var lifetime, out var token));
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        using var callback = token.Register(() => { entered.Set(); resume.Wait(); });
        var terminal = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "held callback");
        var completion = Task.Run(() => table.TryComplete(pending.Id, PendingCallCompletionReason.ConnectionClosed, terminal));
        try
        {
            Ensure(entered.Wait(TimeSpan.FromSeconds(5)));
            Ensure(!table.Contains(pending.Id) && table.ActiveCount == 1);
            lifetime.Dispose();
            EnsureUsable(token);
            Ensure(!completion.IsCompleted); // Logical processing itself still owns the original call.
        }
        finally { resume.Set(); }
        Ensure(await completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Ensure(ReferenceEquals(await Failure(pending.Operation.AsValueTask().AsTask()), terminal));
        EnsureDisposed(token);
        Ensure(table.ActiveCount == 0);
    }

    [Test]
    public async Task ReusedPhysicalSlotDoesNotReuseOrRetargetTheHeldPendingCall()
    {
        using var table = PendingRequestTableTestFixture.Create(2);
        var first = table.RegisterOneWayClientStream(default, CancellationToken.None);
        var originalCall = CurrentCall(table, first.Id);
        Ensure(table.TryAcquireProducerLifetime(first.Id, out var oldLifetime, out var oldToken));
        Ensure(table.TryComplete(first.Id, PendingCallCompletionReason.ConnectionClosed));
        await Failure(first.Operation.AsValueTask().AsTask());
        _ = table.AllocateRequestId(); // Legitimate skipped ID, not a mutable counter reset.
        var second = table.RegisterOneWayClientStream(default, CancellationToken.None);
        Ensure(first.Id != second.Id && first.Id % table.Capacity == second.Id % table.Capacity &&
            !ReferenceEquals(originalCall, CurrentCall(table, second.Id)));
        Ensure(table.Count == 1 && table.ActiveCount == 2);
        Ensure(!table.TryAcquireProducerLifetime(first.Id, out _, out _));
        Ensure(table.TryAcquireProducerLifetime(second.Id, out var nextLifetime, out var nextToken));
        oldLifetime.Dispose();
        oldLifetime.Dispose();
        EnsureDisposed(oldToken);
        EnsureUsable(nextToken);
        Ensure(!nextToken.IsCancellationRequested && table.Contains(second.Id));
        Ensure(table.TryComplete(second.Id, PendingCallCompletionReason.ConnectionClosed));
        await Failure(second.Operation.AsValueTask().AsTask());
        EnsureUsable(nextToken);
        nextLifetime.Dispose();
        EnsureDisposed(nextToken);
    }

    [Test]
    public async Task ThrowingTerminalObserverAndOwnerCannotDropDecodedResponseOrAnyCleanup()
    {
        var observerFailure = new InvalidOperationException("terminal observer");
        var ownerFailure = new InvalidOperationException("terminal owner");
        var idleFailure = new InvalidOperationException("capacity owner");
        var owner = new Owner { CompletionFailure = ownerFailure, IdleFailure = idleFailure };
        var observer = new Observer(observerFailure);
        using var table = PendingRequestTableTestFixture.Create(1, owner);
        var operation = table.Rent(PendingRequestTableTestFixture.Codecs.GetCodec<int>(), PendingCallKind.ClientStreaming,
            default, CancellationToken.None, out var id, completionObserver: observer);
        Ensure(table.TryAcquireProducerLifetime(id, out var lifetime, out var token));
        Exception? failure = null;
        var payload = new ReadOnlySequence<byte>(BitConverter.GetBytes(73));
        try { table.Dispatch(id, ref payload); }
        catch (Exception error) { failure = error; }
        Ensure(Contains(failure, observerFailure) && Contains(failure, ownerFailure) && !Contains(failure, idleFailure));
        Ensure(await operation.AsValueTask() == 73); // Observer failure cannot drop the actual logical result.
        Ensure(owner.Completed == 1 && owner.Idle == 0 && observer.Completed == 1);
        Ensure(table.Count == 0 && table.ActiveCount == 1); // Physical capacity successor: not refunded logically.
        EnsureUsable(token);
        Exception? physicalFailure = null;
        try { lifetime.Dispose(); }
        catch (Exception error) { physicalFailure = error; }
        Ensure(ReferenceEquals(physicalFailure, idleFailure) && owner.Idle == 1 && table.ActiveCount == 0);
        EnsureDisposed(token);
    }

    [Test]
    public async Task SuccessfulCancellationFailureDiagnosticKeepsTheOriginalNonthrowingContract()
    {
        var owner = new Owner();
        using var table = PendingRequestTableTestFixture.Create(1, owner);
        var pending = table.RegisterOneWayClientStream(default, CancellationToken.None);
        Ensure(table.TryAcquireProducerLifetime(pending.Id, out var lifetime, out var token));
        var callbackFailure = new InvalidOperationException("producer callback");
        using var callback = token.Register(() => throw callbackFailure);
        var terminal = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "original result");
        Ensure(table.TryComplete(pending.Id, PendingCallCompletionReason.ConnectionClosed, terminal));
        Ensure(owner.Diagnostics == 1 && Contains(owner.ReportedFailure, callbackFailure));
        Ensure(ReferenceEquals(await Failure(pending.Operation.AsValueTask().AsTask()), terminal));
        Ensure(table.ActiveCount == 1 && owner.Completed == 1);
        lifetime.Dispose();
        EnsureDisposed(token);
    }

    [Test]
    public async Task ThrowingCancellationDiagnosticRetainsOriginalFailuresAfterResultDelivery()
    {
        var callbackFailure = new InvalidOperationException("producer callback");
        var diagnosticFailure = new InvalidOperationException("diagnostic callback");
        var observerFailure = new InvalidOperationException("observer callback");
        var owner = new Owner { DiagnosticFailure = diagnosticFailure };
        using var table = PendingRequestTableTestFixture.Create(1, owner);
        var operation = table.Rent(PendingRequestTableTestFixture.Codecs.GetCodec<int>(), PendingCallKind.ClientStreaming,
            default, CancellationToken.None, out var id, completionObserver: new Observer(observerFailure));
        Ensure(table.TryAcquireProducerLifetime(id, out var lifetime, out var token));
        using var callback = token.Register(() => throw callbackFailure);
        var terminal = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "original logical result");
        Exception? failure = null;
        try { table.TryComplete(id, PendingCallCompletionReason.ConnectionClosed, terminal); }
        catch (Exception error) { failure = error; }
        Ensure(Contains(failure, callbackFailure) && Contains(failure, diagnosticFailure) && Contains(failure, observerFailure));
        Ensure(ReferenceEquals(await Failure(operation.AsValueTask().AsTask()), terminal));
        Ensure(owner.Completed == 1 && table.ActiveCount == 1);
        lifetime.Dispose();
        EnsureDisposed(token);
    }

    [Test]
    public async Task TerminalWinnerRejectsAttachmentBeforeAnyProducerWork()
    {
        using var table = PendingRequestTableTestFixture.Create(1);
        var pending = table.RegisterOneWayClientStream(default, CancellationToken.None);
        Ensure(table.TryComplete(pending.Id, PendingCallCompletionReason.ConnectionClosed));
        var producerCalls = 0;
        if (table.TryAcquireProducerLifetime(pending.Id, out var lifetime, out _))
        { producerCalls++; lifetime.Dispose(); }
        Ensure(producerCalls == 0);
        await Failure(pending.Operation.AsValueTask().AsTask());
    }

    [Test]
    public async Task TableDisposalStillCompletesEverySlotWhenAnObserverThrows()
    {
        var owner = new Owner { CompletionFailure = new InvalidOperationException("owner") };
        var table = PendingRequestTableTestFixture.Create(2, owner);
        var first = table.RegisterOneWayClientStream(default, CancellationToken.None);
        var second = table.RegisterOneWayClientStream(default, CancellationToken.None);
        Ensure(table.TryAcquireProducerLifetime(first.Id, out var firstLifetime, out var firstToken));
        Ensure(table.TryAcquireProducerLifetime(second.Id, out var secondLifetime, out var secondToken));
        Exception? failure = null;
        try { table.Dispose(); }
        catch (Exception error) { failure = error; }
        Ensure(Contains(failure, owner.CompletionFailure) && owner.Completed == 2 && table.ActiveCount == 2);
        await Failure(first.Operation.AsValueTask().AsTask());
        await Failure(second.Operation.AsValueTask().AsTask());
        EnsureUsable(firstToken); EnsureUsable(secondToken);
        firstLifetime.Dispose(); secondLifetime.Dispose();
        EnsureDisposed(firstToken); EnsureDisposed(secondToken);
        Ensure(table.ActiveCount == 0);
        table.Dispose();
    }

    private static object CurrentCall(PendingRequestTable table, long id)
        => ((Array)typeof(PendingRequestTable).GetField("_slots", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(table)!).GetValue((int)(id & (table.Capacity - 1)))!;
    private static void EnsureUsable(CancellationToken token) => Ensure(token.WaitHandle is not null);
    private static void EnsureDisposed(CancellationToken token)
    {
        try { _ = token.WaitHandle; }
        catch (ObjectDisposedException) { return; }
        throw new InvalidOperationException("Original producer CTS was not disposed at final physical/logical cleanup.");
    }
    private static async Task<Exception> Failure(Task task)
    {
        try { await task; }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected the original logical failure.");
    }
    private static bool Contains(Exception? outer, Exception? expected)
    {
        if (expected is null) return false;
        if (ReferenceEquals(outer, expected)) return true;
        if (outer is AggregateException aggregate)
            foreach (var item in aggregate.InnerExceptions) if (Contains(item, expected)) return true;
        return false;
    }
    private static void Ensure(bool condition)
    { if (!condition) throw new InvalidOperationException("Exact pending producer ownership assertion failed."); }

    private sealed class Observer(Exception failure) : IPendingCallCompletionObserver
    {
        internal int Completed;
        public void OnResponseObserved() { }
        public void OnPendingCallCompleted(in PendingCallCompletion completion)
        { Completed++; throw failure; }
    }
    private sealed class Owner : IPendingCallOwner
    {
        internal int Completed;
        internal int Idle;
        internal int Diagnostics;
        internal Exception? ReportedFailure;
        internal Exception? CompletionFailure;
        internal Exception? IdleFailure;
        internal Exception? DiagnosticFailure;
        public void OnPendingCallCompleted(in PendingCallCompletion completion)
        { Completed++; if (CompletionFailure is { } failure) throw failure; }
        public void OnProducerCancellationCallbackFailed(Exception exception)
        { Diagnostics++; ReportedFailure = exception; if (DiagnosticFailure is { } failure) throw failure; }
        public void OnPendingCallCapacityIdle()
        { Idle++; if (IdleFailure is { } failure) throw failure; }
    }
}
