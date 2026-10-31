using System.Linq;
using static SharpLink.UnitTests.BindingPrototype.BindingScalarCodec;

namespace SharpLink.UnitTests.BindingPrototype;

// Deterministic, single-threaded transition model. Explicit steps are barriers, NOT proof of runtime
// memory ordering, locks, send-pump FIFO, timers, weak-reference lifetime, allocation cost or transport.
internal static class BindingControlModel
{
    internal static readonly Guid ServerId = new("00112233-4455-6677-8899-aabbccddeeff");
    internal static readonly Guid EpochId = new("11223344-5566-7788-99aa-bbccddeeff00");
    internal static Key Plain(long contract = 7) => new(contract, 11, 13, Requirement.Compatible,
        false, Guid.Empty, Guid.Empty, 0, 0);
    internal static Key Receipt(Requirement requirement = Requirement.ExactRegistration) =>
        Plain() with { Requirement = requirement, HasReceipt = true, Server = ServerId,
            Epoch = EpochId, Generation = 3, Registration = 17 };

    internal sealed record Entry(ulong Token, ulong Creator, long Contract, ulong HashHigh,
        ulong HashLow, ulong Registration, ulong Generation, object Session);

    internal sealed class Server
    {
        private readonly Entry?[] _slots;
        private readonly ulong[] _incarnations;
        private readonly ulong _maxIncarnation;
        private ulong _lastOperation;
        internal object Session { get; } = new();
        internal Phase Phase { get; set; } = Phase.Ready;
        internal ulong Registration { get; set; } = 17;
        internal ulong PublicationGeneration { get; set; } = 3;
        internal ulong UnrelatedGlobalGeneration { get; set; } = 3;
        internal bool AdmissionOpen { get; set; } = true;
        internal int BusinessExecutions { get; private set; }
        internal int Occupancy => _slots.Count(x => x is not null);
        internal bool Closed { get; private set; }

        internal Server(int capacity = 4, ulong maxIncarnation = (1UL << 48) - 1)
        {
            Need(capacity is >= 1 and <= 65536 && maxIncarnation is >= 1 and < (1UL << 48), "model_limits");
            _slots = new Entry?[capacity];
            _incarnations = new ulong[capacity];
            _maxIncarnation = maxIncarnation;
        }

        internal Result Bind(ulong operation, Key key, bool canQueueFinal = true)
        {
            key.Validate();
            Need(!Closed && Phase is Phase.Ready or Phase.Draining, "phase");
            Need(_lastOperation != ulong.MaxValue && operation == _lastOperation + 1, "operation_order");
            _lastOperation = operation;
            var status = Phase == Phase.Draining ? Status.SessionDraining :
                key.Contract != 7 ? Status.UnknownContract :
                key.HashHigh != 11 || key.HashLow != 13 ? Status.HashMismatch :
                key.HasReceipt && (key.Server != ServerId || key.Epoch != EpochId) ? Status.ReceiptScopeMismatch :
                (byte)key.Requirement >= (byte)Requirement.MinimumVersion && PublicationGeneration < key.Generation ? Status.BelowMinimum :
                key.Requirement == Requirement.ExactRegistration && Registration != key.Registration ? Status.RegistrationMismatch :
                !AdmissionOpen ? Status.RegistrationRetiring : Status.Success;
            Entry? created = null;
            if (status == Status.Success)
            {
                var slot = Array.FindIndex(_slots, x => x is null);
                while (slot >= 0 && _incarnations[slot] == _maxIncarnation)
                    slot = Array.FindIndex(_slots, slot + 1, x => x is null);
                if (slot < 0) status = Status.BindingCapacity;
                else
                {
                    var token = (++_incarnations[slot] << 16) | (uint)slot;
                    created = new Entry(token, operation, key.Contract, key.HashHigh, key.HashLow,
                        Registration, PublicationGeneration, Session);
                    _slots[slot] = created;
                }
            }
            if (!canQueueFinal)
            {
                if (created is not null) Release(created.Token);
                Close();
                return Result.Failure(Status.ControlCapacity);
            }
            return created is null ? Result.Failure(status) : new Result(Status.Success, created.Token,
                created.Contract, created.HashHigh, created.HashLow, created.Registration,
                created.Generation, ServerId, EpochId);
        }

        internal Entry? Capture(ulong token)
        {
            var slot = (int)(token & 0xffff);
            if (token == 0 || slot >= _slots.Length) return null;
            var captured = _slots[slot]; // Exactly one model read, no reread after comparison.
            return captured?.Token == token ? captured : null;
        }

        internal bool Dispatch(Entry? captured, long method = 9, bool shapeMatches = true)
        {
            if (Closed || Phase == Phase.Terminal || captured is null || !ReferenceEquals(captured.Session, Session) ||
                !AdmissionOpen || method != 9 || !shapeMatches) return false;
            BusinessExecutions++;
            return true;
        }

        internal void Release(ulong token)
        {
            var slot = (int)(token & 0xffff);
            if (slot < _slots.Length && _slots[slot]?.Token == token) _slots[slot] = null;
        }

        internal void Cancel(ulong operation)
        {
            Need(operation != 0 && operation <= _lastOperation, "future_cancel");
            for (var i = 0; i < _slots.Length; i++)
                if (_slots[i]?.Creator == operation) _slots[i] = null;
        }

        internal void Close()
        {
            Closed = true;
            Phase = Phase.Terminal;
            Array.Clear(_slots);
        }
    }

    internal sealed class Operation(ulong id, Key key)
    {
        internal ulong Id { get; } = id;
        internal Key Key { get; } = key;
        internal int Waiters { get; set; } = 1;
        internal bool Cancelled { get; set; }
        internal bool Accepted { get; set; }
        internal bool ProtocolTerminal { get; set; }
        internal bool CleanupRequired { get; set; }
        internal Result? Final { get; set; }
    }

    internal sealed class Client
    {
        private readonly int _window;
        private readonly int _waiterLimit;
        private readonly int _siteLimit;
        private readonly HashSet<Key> _knownSites = [];
        private readonly Dictionary<ulong, Operation> _operations = [];
        private readonly Dictionary<Key, ulong> _candidates = [];
        private readonly ulong[] _invalidations;
        private ulong _next = 1;
        private ulong _retired;
        private Operation? _publishing;
        internal bool Closed { get; private set; }
        internal int Occupancy => _operations.Count;
        internal int CandidateCount => _candidates.Count;
        internal ulong Retired => _retired;
        internal int Waiters => _operations.Values.Sum(x => x.Waiters);

        internal Client(int window = 4, int slots = 4, int waiterLimit = 8, int siteLimit = 8)
        {
            Need(window is >= 1 and <= 64 && slots is >= 1 and <= 65536 && waiterLimit is >= 1 and <= 4096 &&
                siteLimit is >= 1 and <= 4096,
                "model_limits");
            _window = window;
            _waiterLimit = waiterLimit;
            _siteLimit = siteLimit;
            _invalidations = new ulong[slots];
        }

        // Explicit NEW protocol demand, not public Prepare-on-ready or steady candidate reuse.
        internal Operation Begin(Key key)
        {
            key.Validate();
            Need(!Closed, "closed");
            Need(!_candidates.ContainsKey(key), "ready_candidate_requires_release");
            var live = _operations.Values.SingleOrDefault(x => x.Key == key && !x.Cancelled && !x.ProtocolTerminal);
            Need(Waiters < _waiterLimit && (live?.Waiters ?? 0) < 64, "waiter_capacity");
            if (live is not null) { live.Waiters++; return live; }
            Need(_knownSites.Contains(key) || _knownSites.Count < _siteLimit, "site_capacity");
            Need(_publishing is null && _operations.Count < _window && _next != ulong.MaxValue, "window");
            _knownSites.Add(key);
            var operation = new Operation(_next, key);
            _operations.Add(operation.Id, operation); // Installed before exposing bytes.
            _publishing = operation;
            return operation;
        }

        internal void ConfirmEnqueue(Operation operation, bool accepted, bool uncertain = false)
        {
            Need(ReferenceEquals(operation, _publishing), "publisher_order");
            if (uncertain || (!accepted && operation.Final is not null)) { Close(); return; }
            if (!accepted)
            {
                _operations.Remove(operation.Id);
                operation.Waiters = 0;
                _publishing = null; // No exposed bytes or early reply: tentative ID never committed.
                return;
            }
            operation.Accepted = true;
            _next++;
            _publishing = null;
            FinishIfReady(operation);
        }

        internal void CancelWaiter(Operation operation)
        {
            Need(operation.Waiters > 0, "waiter");
            operation.Waiters--;
            if (operation.Waiters == 0)
            {
                operation.Cancelled = true;
                FinishIfReady(operation);
            }
        }

        internal void Receive(ulong id, Result result, bool sameSession = true)
        {
            try { result.Validate(); }
            catch (ModelViolation) { Close(); throw; }
            if (!sameSession) { Close(); throw new ModelViolation("session"); }
            if (id <= _retired) return;
            if (!_operations.TryGetValue(id, out var operation)) { Close(); throw new ModelViolation("unknown_operation"); }
            if (result.Status == Status.Success)
            {
                var key = operation.Key;
                var valid = result.Contract == key.Contract && result.HashHigh == key.HashHigh && result.HashLow == key.HashLow &&
                    result.Server == ServerId && result.Epoch == EpochId &&
                    (!key.HasReceipt || (result.Server == key.Server && result.Epoch == key.Epoch)) &&
                    ((byte)key.Requirement < (byte)Requirement.MinimumVersion || result.Generation >= key.Generation) &&
                    (key.Requirement != Requirement.ExactRegistration || result.Registration == key.Registration) &&
                    (result.Token & 0xffff) < (ulong)_invalidations.Length;
                if (!valid) { Close(); throw new ModelViolation("correlation"); }
            }
            if (operation.Final is { } prior)
            {
                if (prior != result) { Close(); throw new ModelViolation("conflicting_result"); }
                return;
            }
            operation.Final = result; // Bounded one-result buffer also valid for Publishing.
            FinishIfReady(operation);
        }

        private void FinishIfReady(Operation operation)
        {
            if (!operation.Accepted || operation.Final is not { } result || operation.ProtocolTerminal) return;
            if (result.Status == Status.Success)
            {
                var invalid = (result.Token >> 16) <= _invalidations[(int)(result.Token & 0xffff)];
                if (operation.Cancelled || invalid)
                {
                    operation.CleanupRequired = true;
                    return;
                }
                _candidates[operation.Key] = result.Token;
            }
            operation.Waiters = 0;
            operation.ProtocolTerminal = true;
            AdvancePrefix();
        }

        internal void ConfirmCleanup(Operation operation, bool accepted)
        {
            Need(operation.Accepted && operation.CleanupRequired && !operation.ProtocolTerminal, "cleanup_state");
            if (!accepted) { Close(); return; }
            operation.CleanupRequired = false;
            operation.Waiters = 0;
            operation.ProtocolTerminal = true;
            AdvancePrefix();
        }

        internal void Invalidate(ulong token)
        {
            var slot = (int)(token & 0xffff);
            Need((token >> 16) != 0 && slot < _invalidations.Length, "token");
            _invalidations[slot] = Math.Max(_invalidations[slot], token >> 16);
            foreach (var pair in _candidates.Where(x => (x.Value & 0xffff) == (ulong)slot &&
                         (x.Value >> 16) <= _invalidations[slot]).ToArray()) _candidates.Remove(pair.Key);
        }

        internal bool HasCandidate(Key key) => _candidates.ContainsKey(key);
        private void AdvancePrefix()
        {
            while (_operations.TryGetValue(_retired + 1, out var first) && first.ProtocolTerminal)
            {
                _operations.Remove(++_retired);
            }
        }
        internal void Expire() => Close(); // Explicit model clock barrier, no timer implementation claim.
        internal void Close()
        {
            Closed = true;
            foreach (var operation in _operations.Values) operation.Waiters = 0;
            _operations.Clear();
            _candidates.Clear();
            _knownSites.Clear();
            _publishing = null;
        }
    }

    internal static bool Eligible(Key key, bool manifestMatches, Guid verifiedServer, Guid verifiedEpoch,
        bool scopeAllowed = true) => scopeAllowed && (key.HasReceipt
            ? key.Server == verifiedServer && key.Epoch == verifiedEpoch : manifestMatches);

    internal sealed class Connection(object session, ulong generation)
    {
        internal object Session { get; } = session;
        internal ulong Generation { get; } = generation;
        internal bool Open { get; set; } = true;
        internal Connection? Redirect { get; set; }
        internal int Reservations { get; private set; }
        internal bool ReserveExact(object expectedSession, ulong expectedGeneration)
        {
            if (!Open || !ReferenceEquals(Session, expectedSession) || Generation != expectedGeneration) return false;
            Reservations++;
            return true; // Single-threaded model: actual increment/recheck race is a future source test.
        }
        internal void Release() { Need(Reservations > 0, "reservation"); Reservations--; }
    }

    // Tests choose actual acceptance separately from publisher continuation; no actual SendPump used.
    internal sealed class Queues
    {
        internal readonly Queue<string> Normal = new();
        internal readonly Queue<string> Progress = new();
        internal string? DrainOne() => Progress.Count != 0 ? Progress.Dequeue() :
            Normal.Count != 0 ? Normal.Dequeue() : null;
        internal static bool IsNewControlProgress(byte type)
        {
            Need(type is >= 17 and <= 23, "type");
            return false;
        }
    }
}
