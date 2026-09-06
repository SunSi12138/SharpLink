from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def replace_once(path: str, old: str, new: str) -> None:
    file = ROOT / path
    text = file.read_text()
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected exactly one match, found {count}: {old[:100]!r}")
    file.write_text(text.replace(old, new, 1))


shared = ROOT / "src/SharpLink.Server/Admission/AdmissionRateWaitQueue.cs"
if shared.exists():
    raise RuntimeError("AdmissionRateWaitQueue.cs already exists")
shared.write_text(r'''using System.Threading.RateLimiting;

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
''')

# Legacy TokenBucket / SlidingWindow state.
path = ROOT / "src/SharpLink.Server/Admission/AdmissionDynamicRateState.cs"
text = path.read_text()
text = text.replace(
    "internal sealed class AdmissionDynamicRateState : IDisposable",
    "internal sealed class AdmissionDynamicRateState : IDisposable, IAdmissionRateWaiterOwner",
    1)
text = text.replace(
    """    private readonly long[] _slidingSegments;\n    private RateWaiter? _waiterHead;\n    private RateWaiter? _waiterTail;\n    private ITimer? _timer;\n""",
    """    private readonly long[] _slidingSegments;\n    private AdmissionRateWaitQueue _waiters;\n    private ITimer? _timer;\n""",
    1)
text = text.replace("    private int _waitingCount;\n", "", 1)
text = text.replace("return _waitingCount;", "return _waiters.Count;", 1)
text = text.replace("FailedLease.Instance", "AdmissionRateLeases.Failed")
text = text.replace("AcquiredLease.Instance", "AdmissionRateLeases.Acquired")
text = text.replace("RateWaiter", "AdmissionRateWaiter")
text = text.replace("_waitingCount != 0", "_waiters.Count != 0")
text = text.replace("_waitingCount == 0", "_waiters.Count == 0")
text = text.replace("            EnqueueLocked(waiter);", "            _waiters.Enqueue(waiter);", 1)
old_registration = '''        if (cancellationToken.CanBeCanceled)\n        {\n            var registration = cancellationToken.UnsafeRegister(\n                static state => ((AdmissionRateWaiter)state!).Owner.CancelWaiter((AdmissionRateWaiter)state!),\n                waiter);\n            waiter.SetRegistration(registration);\n        }\n'''
if text.count(old_registration) != 1:
    raise RuntimeError("legacy cancellation registration block mismatch")
text = text.replace(old_registration, "        waiter.RegisterCancellation();\n", 1)
text = text.replace("while (_waiterHead is not null && CanGrantLocked())", "while (!_waiters.IsEmpty && CanGrantLocked())", 1)
text = text.replace("var waiter = DequeueLocked();", "var waiter = _waiters.Dequeue();", 1)
text = text.replace("if (_disposed != 0 || _waiterHead is null)", "if (_disposed != 0 || _waiters.IsEmpty)", 1)
text = text.replace("removed = RemoveLocked(waiter);", "removed = _waiters.Remove(waiter);", 1)
text = text.replace("failed = DetachAllLocked();", "failed = _waiters.DetachAll();", 1)
text = text.replace("CompleteGranted(granted);", "AdmissionRateWaitQueue.CompleteGranted(granted);")
text = text.replace("CompleteFailed(failed);", "AdmissionRateWaitQueue.CompleteFailed(failed);")
insert = "    private void CancelWaiter(AdmissionRateWaiter waiter)\n"
if text.count(insert) != 1:
    raise RuntimeError("legacy cancel method marker mismatch")
text = text.replace(
    insert,
    "    void IAdmissionRateWaiterOwner.CancelRateWaiter(AdmissionRateWaiter waiter) => CancelWaiter(waiter);\n\n" + insert,
    1)
queue_start = text.index("    private void EnqueueLocked(AdmissionRateWaiter waiter)\n")
queue_end = text.index("    public void Dispose()\n", queue_start)
text = text[:queue_start] + text[queue_end:]
nested_start = text.index("    private sealed class AdmissionRateWaiter(")
text = text[:nested_start] + "}\n"
for dead in ("_waiterHead", "_waiterTail", "_waitingCount", "EnqueueLocked(", "DequeueLocked(", "RemoveLocked(", "DetachAllLocked(", "private sealed class AdmissionRateWaiter"):
    if dead in text:
        raise RuntimeError(f"legacy waiter duplication survived: {dead}")
path.write_text(text)

# Stable FixedWindow counter.
path = ROOT / "src/SharpLink.Server/Admission/DynamicFixedWindowRateLimiter.Counter.cs"
text = path.read_text()
text = text.replace("    private sealed class Counter\n", "    private sealed class Counter : IAdmissionRateWaiterOwner\n", 1)
text = text.replace(
    """        private readonly Lock _gate = new();\n        private readonly TimeProvider _timeProvider;\n        private RateWaiter? _waiterHead;\n        private RateWaiter? _waiterTail;\n        private ITimer? _timer;\n""",
    """        private readonly Lock _gate = new();\n        private readonly TimeProvider _timeProvider;\n        private AdmissionRateWaitQueue _waiters;\n        private ITimer? _timer;\n""",
    1)
text = text.replace("        private int _waitingCount;\n", "", 1)
text = text.replace("get { lock (_gate) return _waitingCount; }", "get { lock (_gate) return _waiters.Count; }", 1)
text = text.replace("FailedLease.Instance", "AdmissionRateLeases.Failed")
text = text.replace("AcquiredLease.Instance", "AdmissionRateLeases.Acquired")
text = text.replace("RateWaiter", "AdmissionRateWaiter")
text = text.replace("_waitingCount != 0", "_waiters.Count != 0")
text = text.replace("_waitingCount == 0", "_waiters.Count == 0")
text = text.replace("                EnqueueLocked(waiter);", "                _waiters.Enqueue(waiter);", 1)
old_registration = '''            if (cancellationToken.CanBeCanceled)\n            {\n                var registration = cancellationToken.UnsafeRegister(\n                    static state => ((AdmissionRateWaiter)state!).Owner.CancelWaiter((AdmissionRateWaiter)state!),\n                    waiter);\n                waiter.SetRegistration(registration);\n            }\n'''
if text.count(old_registration) != 1:
    raise RuntimeError("FixedWindow cancellation registration block mismatch")
text = text.replace(old_registration, "            waiter.RegisterCancellation();\n", 1)
text = text.replace("failed = DetachAllLocked();", "failed = _waiters.DetachAll();", 1)
text = text.replace("while (_waiterHead is not null && _consumed < _queuedLimit)", "while (!_waiters.IsEmpty && _consumed < _queuedLimit)", 1)
text = text.replace("var waiter = DequeueLocked();", "var waiter = _waiters.Dequeue();", 1)
text = text.replace("if (_disposed != 0 || _waiterHead is null)", "if (_disposed != 0 || _waiters.IsEmpty)", 1)
text = text.replace("removed = RemoveLocked(waiter);", "removed = _waiters.Remove(waiter);", 1)
text = text.replace("CompleteGranted(granted);", "AdmissionRateWaitQueue.CompleteGranted(granted);")
text = text.replace("CompleteFailed(failed);", "AdmissionRateWaitQueue.CompleteFailed(failed);")
insert = "        private void CancelWaiter(AdmissionRateWaiter waiter)\n"
if text.count(insert) != 1:
    raise RuntimeError("FixedWindow cancel method marker mismatch")
text = text.replace(
    insert,
    "        void IAdmissionRateWaiterOwner.CancelRateWaiter(AdmissionRateWaiter waiter) => CancelWaiter(waiter);\n\n" + insert,
    1)
queue_start = text.index("        private void EnqueueLocked(AdmissionRateWaiter waiter)\n")
queue_end = text.index("        private void ClearPendingLocked()\n", queue_start)
text = text[:queue_start] + text[queue_end:]
nested_start = text.index("    private sealed class AdmissionRateWaiter(")
text = text[:nested_start] + "}\n"
for dead in ("_waiterHead", "_waiterTail", "_waitingCount", "EnqueueLocked(", "DequeueLocked(", "RemoveLocked(", "DetachAllLocked(", "private sealed class AdmissionRateWaiter"):
    if dead in text:
        raise RuntimeError(f"FixedWindow waiter duplication survived: {dead}")
path.write_text(text)

# The outer immutable FixedWindow policy view still has disposed fast-failure paths.
path = ROOT / "src/SharpLink.Server/Admission/DynamicFixedWindowRateLimiter.cs"
text = path.read_text()
if text.count("FailedLease.Instance") != 2:
    raise RuntimeError(
        f"FixedWindow policy view: expected two legacy failed-lease references, found {text.count('FailedLease.Instance')}")
path.write_text(text.replace("FailedLease.Instance", "AdmissionRateLeases.Failed"))

print("issue #410 shared rate waiter refactor staged")
