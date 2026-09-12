using System.Threading.RateLimiting;

namespace SharpLink.Server;

internal interface IAdmissionRateWaiterOwner
{
    void CancelRateWaiter(AdmissionRateWaiter waiter);
}

/// <summary>
/// Intrusive rate-waiter queue. The owning limiter provides synchronization; this value type adds
/// no allocation or virtual dispatch to the synchronous permit/reject path.
/// </summary>
internal struct AdmissionRateWaitQueue
{
    private AdmissionRateWaiter? _head;
    private AdmissionRateWaiter? _tail;
    private int _count;

    internal readonly int Count => _count;
    internal readonly bool IsEmpty => _head is null;

    internal void Enqueue(AdmissionRateWaiter waiter)
    {
        waiter.IsQueued = true;
        waiter.Previous = _tail;
        if (_tail is null)
            _head = waiter;
        else
            _tail.Next = waiter;
        _tail = waiter;
        _count++;
    }

    internal AdmissionRateWaiter Dequeue()
    {
        var waiter = _head ??
            throw new InvalidOperationException("Admission rate waiter queue was unexpectedly empty.");
        var next = waiter.Next;
        _head = next;
        if (next is null)
            _tail = null;
        else
            next.Previous = null;
        waiter.Previous = null;
        waiter.Next = null;
        waiter.IsQueued = false;
        _count--;
        return waiter;
    }

    internal bool Remove(AdmissionRateWaiter waiter)
    {
        if (!waiter.IsQueued)
            return false;
        var previous = waiter.Previous;
        var next = waiter.Next;
        if (previous is null)
            _head = next;
        else
            previous.Next = next;
        if (next is null)
            _tail = previous;
        else
            next.Previous = previous;
        waiter.Previous = null;
        waiter.Next = null;
        waiter.IsQueued = false;
        _count--;
        return true;
    }

    internal AdmissionRateWaiter? DetachAll()
    {
        var head = _head;
        _head = null;
        _tail = null;
        _count = 0;
        for (var waiter = head; waiter is not null; waiter = waiter.Next)
        {
            waiter.Previous = null;
            waiter.IsQueued = false;
        }
        return head;
    }

    internal static void CompleteGranted(AdmissionRateWaiter? waiter)
    {
        while (waiter is not null)
        {
            var next = waiter.Next;
            waiter.Next = null;
            waiter.CompleteGranted();
            waiter = next;
        }
    }

    internal static void CompleteFailed(AdmissionRateWaiter? waiter)
    {
        while (waiter is not null)
        {
            var next = waiter.Next;
            waiter.Next = null;
            waiter.CompleteFailed();
            waiter = next;
        }
    }
}

internal sealed class AdmissionRateWaiter(
    IAdmissionRateWaiterOwner owner,
    CancellationToken cancellationToken)
    : TaskCompletionSource<RateLimitLease>(TaskCreationOptions.RunContinuationsAsynchronously)
{
    private CancellationTokenRegistration _registration;
    private int _completed;

    internal IAdmissionRateWaiterOwner Owner { get; } = owner;
    internal CancellationToken CancellationToken { get; } = cancellationToken;
    internal AdmissionRateWaiter? Previous { get; set; }
    internal AdmissionRateWaiter? Next { get; set; }
    internal bool IsQueued { get; set; }

    internal void RegisterCancellation()
    {
        if (!CancellationToken.CanBeCanceled)
            return;
        var registration = CancellationToken.UnsafeRegister(
            static state =>
            {
                var waiter = (AdmissionRateWaiter)state!;
                waiter.Owner.CancelRateWaiter(waiter);
            },
            this);
        _registration = registration;
        if (Volatile.Read(ref _completed) != 0)
            registration.Dispose();
    }

    internal void CompleteGranted()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;
        _registration.Dispose();
        TrySetResult(AdmissionRateLeases.Acquired);
    }

    internal void CompleteCanceled()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;
        _registration.Dispose();
        TrySetCanceled(CancellationToken);
    }

    internal void CompleteFailed()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;
        _registration.Dispose();
        TrySetResult(AdmissionRateLeases.Failed);
    }
}

internal static class AdmissionRateLeases
{
    internal static RateLimitLease Acquired { get; } = new AcquiredLease();
    internal static RateLimitLease Failed { get; } = new FailedLease();

    private sealed class AcquiredLease : RateLimitLease
    {
        public override bool IsAcquired => true;
        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }

    private sealed class FailedLease : RateLimitLease
    {
        public override bool IsAcquired => false;
        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }
}
