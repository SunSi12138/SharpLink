using System.Text.Json;

// Deterministic algebra/admission oracle, not a transport or a concurrency implementation.
// All counts below are logical model operations. Ticket/Request allocations are test bookkeeping.
var rows = new List<object>();
var checks = new List<string>();
foreach (var grant in new[] { 256, 1024, 4096 })
{
    foreach (var size in new[] { 16, 64, 4096 })
    {
        foreach (var count in new[] { 1, 16, 128 })
        {
            var m = new GrantModel(1 << 20, 16 << 20, grant);
            var streams = Enumerable.Range(0, count).Select(_ => m.Open()).ToArray();
            var pendingConsumed = new long[count];
            long pendingConnection = 0;
            long streamThresholdFlushes = 0, connectionThresholdFlushes = 0;
            const int items = 1024;
            for (var i = 0; i < items; i++)
                for (var index = 0; index < count; index++)
                {
                    var h = streams[index];
                    var r = m.Request(h, size);
                    Need(r.Ticket is not null, "default-window workload blocked");
                    m.Handoff(r.Ticket!);
                    pendingConsumed[index] += size;
                    pendingConnection += size;
                    // Match dev's eager consumer: stream half-window first; otherwise
                    // connection half-window drains every stream's pending consumed bytes.
                    if (pendingConsumed[index] >= (1 << 19))
                    {
                        streamThresholdFlushes++;
                        m.Update(h, pendingConsumed[index]);
                        pendingConnection -= pendingConsumed[index];
                        pendingConsumed[index] = 0;
                    }
                    else if (pendingConnection >= (8 << 20))
                    {
                        connectionThresholdFlushes++;
                        for (var updateIndex = 0; updateIndex < count; updateIndex++)
                        {
                            if (pendingConsumed[updateIndex] == 0) continue;
                            m.Update(streams[updateIndex], pendingConsumed[updateIndex]);
                            pendingConsumed[updateIndex] = 0;
                        }
                        pendingConnection = 0;
                    }
                }
            for (var index = 0; index < count; index++)
            {
                m.Complete(streams[index]);
                if (pendingConsumed[index] != 0) m.Update(streams[index], pendingConsumed[index]);
            }
            m.Check();
            Need(m.Free == (16 << 20), "default-window run leaked credit at completion");
            rows.Add(new {
                workload = "default-window-eager-consume-threshold-updates", grant, size, streams = count,
                items = items * count, m.AuthorityVisits, m.Refills, m.LocalDebits,
                m.WindowUpdates, m.PressureScans, m.ReclaimedBytes, m.PeakGrantBytes,
                streamThresholdFlushes, connectionThresholdFlushes,
                updateSchedule = "stream 524288 B first; otherwise connection 8388608 B drains all pending streams; terminal remainder",
                authorityVisitsPerItem = (double)m.AuthorityVisits / (items * count),
                refillVisitsPerItem = (double)m.Refills / (items * count),
                observation = "logical counts only; no CPU, lock, allocation, RPC, or transport claim"
            });
        }
    }
    Scenarios(grant);
}
InvalidControls();
Console.WriteLine(JsonSerializer.Serialize(new {
    evidence = "new deterministic B2 conservation and admission oracle",
    runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    baseline = "f8ba89121c87a6410a0139c44d7db30eab877f41",
    status = "passed",
    tests = checks,
    rows,
    exclusions = new[] {
        "Not a production scheduler, matched transport, RPC benchmark or NativeAOT RPC.",
        "Serial atomic model events do not prove .NET memory ordering, lock order or concurrent linearizability.",
        "No serializer, buffer budget, SendPump, FlushAsync, deadline or protocol implementation is linked.",
        "Ticket/Request objects are explicit oracle instrumentation; zero allocation is not claimed.",
        "WindowUpdate schedules assume the peer only returns writer-owned bytes; sequence-free stale updates while new debt exists are not solved.",
        "Pressure fairness is FIFO among stream-eligible requests; bytes insufficient for an eligible head stop admission."
    }
}, new JsonSerializerOptions { WriteIndented = true }));

void Scenarios(int grant)
{
    // Toy windows expose mechanisms; they are never used to report production throughput.
    {
        var m = new GrantModel(512, 1024, grant);
        var a = m.Open(); var b = m.Open(); var c = m.Open();
        m.Prime(a, 512); m.Prime(b, 512);
        var r = m.Request(c, 16); // A/B have no running producer to volunteer their grants.
        Need(r.Ticket is not null && m.ReclaimedBytes == 1024, "idle grants stranded credit");
        m.Handoff(r.Ticket!); m.Update(c, 16); m.Check();
        checks.Add($"grant={grant}: idle-producer-independent reclaim");
    }
    {
        var m = new GrantModel(2, 4, grant);
        var a = m.Open();
        var r = m.Request(a, 6); Need(r.Ticket is not null && m.Free == -2, "oversized borrow");
        m.Handoff(r.Ticket!); m.Update(a, 1);
        Need(m.Free == -1 && a.State.Credit == -3, "partial oversized repayment");
        m.Update(a, int.MaxValue); Need(m.Free == 4, "excess update after borrow");
        m.Check(); checks.Add($"grant={grant}: oversized-partial-full-excess");
    }
    {
        var m = new GrantModel(4, 8, grant);
        var a = m.Open(); var b = m.Open();
        var ar = m.Request(a, 4); m.Handoff(ar.Ticket!);
        var blocked = m.Request(a, 1); // Head lacks stream credit.
        var ready = m.Request(b, 1);   // Eligible later stream may proceed.
        Need(blocked.Ticket is null && ready.Ticket is not null, "stream-blocked head starved B");
        m.Handoff(ready.Ticket!); m.Update(b, 1); m.Update(a, 1);
        Need(blocked.Ticket is not null, "missed wake after stream update");
        m.Check(); checks.Add($"grant={grant}: mixed-stream-eligible-fifo-and-wake");
    }
    {
        var m = new GrantModel(4, 4, grant);
        var a = m.Open(); var b = m.Open();
        var first = m.Request(a, 4);
        var blocked = m.Request(b, 1);
        var original = new InvalidOperationException("remote original exception");
        m.Abort(a, original);
        Need(blocked.Ticket is not null, "abort did not admit B");
        m.Update(a, 4); // #840: A's full stream window means effective delta is zero.
        Need(m.Free == 3, "late aborted update minted credit");
        m.ReturnUnsent(first.Ticket!); // Abort already settled this reservation.
        m.ReturnUnsent(blocked.Ticket!);
        Need(m.Free == 4, "refund after late update failed");
        var rejected = m.Request(a, 1);
        Need(ReferenceEquals(rejected.Error, original), "original terminal exception lost");
        m.Check(); checks.Add($"grant={grant}: abort-late-update-unsent-original-error");
    }
    {
        var m = new GrantModel(4, 4, grant);
        var a = m.Open();
        var t = m.Request(a, 2).Ticket!;
        m.ReturnUnsent(t);
        Need(Throws(() => m.ReturnUnsent(t)), "duplicate unsent ticket accepted");
        var owned = m.Request(a, 2).Ticket!;
        m.Handoff(owned);
        Need(Throws(() => m.ReturnUnsent(owned)), "writer-owned frame refunded as unsent");
        m.Complete(a); m.Update(a, 2);
        var replacement = m.Reuse(a);
        Need(ReferenceEquals(a.State, replacement.State), "pool ABA control did not reuse object");
        var stale = m.Request(a, 1);
        Need(stale.Error is not null, "stale lease mutated recycled state");
        Need(m.Request(replacement, 1).Ticket is not null, "new lease rejected");
        m.Check(); checks.Add($"grant={grant}: exactly-once-writer-handoff-tombstone-aba");
    }
    {
        var m = new GrantModel(4, 4, grant);
        var a = m.Open(); var b = m.Open(); var c = m.Open();
        m.Handoff(m.Request(a, 4).Ticket!);
        var head = m.Request(b, 4); var tail = m.Request(c, 1);
        m.Update(a, 1);
        Need(head.Ticket is null && tail.Ticket is null, "eligible FIFO head bypassed");
        m.Cancel(head);
        Need(tail.Ticket is not null, "cancel failed to wake next");
        m.Progress(); m.Progress();
        Need(m.ProgressOperations == 2, "control progress did not run");
        m.Check(); checks.Add($"grant={grant}: cancellation-admission-control-progress");
    }
}
void InvalidControls()
{
    // Newly authored invalid controls, NOT historical #800/#803 reproductions.
    long free = 0, idleGrant = 4, streamCreditWronglyDebitedForGrant = 0;
    var effectiveWrongUpdate = Math.Min(4L, 4 - streamCreditWronglyDebitedForGrant);
    free += effectiveWrongUpdate;
    Need(free + idleGrant != 4, "invalid grant ledger was not falsified");
    checks.Add("invalid-new-model-control: counting idle grant as stream debt violates Wc after WU");
    var noReclaim = new GrantModel(4, 4, 4, reclaimParkedGrants: false);
    noReclaim.Prime(noReclaim.Open(), 4);
    var blocked = noReclaim.Request(noReclaim.Open(), 1);
    Need(blocked.Ticket is null && noReclaim.Free == 0 && noReclaim.PeakGrantBytes == 4,
        "idle-grant stranded-credit control");
    checks.Add("invalid-new-model-control: no owner reclaim cannot admit a peer from parked grants");
}
static bool Throws(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }
static void Need(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }

sealed class GrantModel(long streamWindow, long connectionWindow, int grantSize, bool reclaimParkedGrants = true)
{
    readonly List<State> states = [];
    readonly List<Pending> waiters = [];
    long lease;
    public long Free = connectionWindow;
    public long AuthorityVisits, Refills, LocalDebits, WindowUpdates, PressureScans;
    public long ReclaimedBytes, PeakGrantBytes, ProgressOperations;

    public Handle Open()
    {
        AuthorityVisits++;
        var s = new State { Lease = ++lease, Credit = streamWindow };
        states.Add(s); return new Handle(s, s.Lease);
    }
    public Handle Reuse(Handle h)
    {
        CheckHandle(h);
        if (!h.State.Completed || h.State.Reserved + h.State.Sent + h.State.Grant != 0)
            throw new InvalidOperationException("retire requires settled debt and grant");
        AuthorityVisits++;
        var s = h.State;
        s.Lease = ++lease; s.Completed = false; s.Error = null;
        return new Handle(s, s.Lease);
    }
    public void Prime(Handle h, long bytes)
    {
        CheckHandle(h); AuthorityVisits++;
        if (bytes > Free || bytes > h.State.Credit - h.State.Grant) throw new InvalidOperationException("prime");
        Free -= bytes; h.State.Grant += bytes; Peak(); Check();
    }
    public Pending Request(Handle h, int bytes)
    {
        if (bytes <= 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        var r = new Pending(h, bytes);
        if (Error(h) is { } error) { r.Error = error; return r; }
        if (waiters.Count == 0 && TryReserve(r, false)) { Check(); return r; }
        AuthorityVisits++;
        waiters.Add(r);
        // An owner, independent of producer continuation or writer Flush, freezes local grants.
        if (reclaimParkedGrants) Reclaim();
        Drain(); Check(); return r;
    }
    bool TryReserve(Pending r, bool pressure)
    {
        var s = r.Handle.State;
        if (!Can(s.Credit, streamWindow, r.Bytes)) return false;
        if (s.Grant < r.Bytes)
        {
            AuthorityVisits++; Refills++;
            var available = Free + s.Grant;
            if (!Can(available, connectionWindow, r.Bytes)) return false;
            if (r.Bytes > streamWindow || r.Bytes > connectionWindow)
            {
                Free += s.Grant; s.Grant = 0;
                Free -= r.Bytes; s.Credit -= r.Bytes; s.Reserved += r.Bytes;
                r.Ticket = new Ticket(r.Handle, r.Bytes); LocalDebits++; return true;
            }
            var target = pressure ? r.Bytes : Math.Max(r.Bytes, grantSize);
            var amount = Math.Min(Free, Math.Min(target, s.Credit) - s.Grant);
            if (amount < 0) throw new InvalidOperationException("negative refill");
            Free -= amount; s.Grant += amount; Peak();
        }
        if (s.Grant < r.Bytes) return false;
        s.Grant -= r.Bytes; s.Credit -= r.Bytes; s.Reserved += r.Bytes;
        r.Ticket = new Ticket(r.Handle, r.Bytes); LocalDebits++; return true;
    }
    void Reclaim()
    {
        AuthorityVisits++;
        foreach (var s in states)
        {
            PressureScans++;
            Free += s.Grant; ReclaimedBytes += s.Grant; s.Grant = 0;
        }
    }
    void Drain()
    {
        for (var i = 0; i < waiters.Count;)
        {
            var r = waiters[i];
            if (Error(r.Handle) is { } error) { r.Error = error; waiters.RemoveAt(i); continue; }
            if (!Can(r.Handle.State.Credit, streamWindow, r.Bytes)) { i++; continue; }
            if (!Can(Free + r.Handle.State.Grant, connectionWindow, r.Bytes)) break;
            if (!TryReserve(r, true)) throw new InvalidOperationException("eligible waiter failed");
            waiters.RemoveAt(i);
        }
    }
    public void Handoff(Ticket t)
    {
        CheckHandle(t.Handle);
        if (t.Phase != 0 || t.Handle.State.Error is not null) throw new InvalidOperationException("handoff");
        t.Handle.State.Reserved -= t.Bytes; t.Handle.State.Sent += t.Bytes; t.Phase = 1; Check();
    }
    public void ReturnUnsent(Ticket t)
    {
        AuthorityVisits++; CheckHandle(t.Handle);
        if (t.Phase != 0) throw new InvalidOperationException("not uniquely producer-owned");
        t.Phase = 2;
        var s = t.Handle.State;
        if (s.Error is null)
        {
            s.Reserved -= t.Bytes; s.Credit += t.Bytes; Free += t.Bytes;
        }
        Drain(); Check();
    }
    public void Update(Handle h, long incoming)
    {
        if (incoming <= 0) throw new ArgumentOutOfRangeException(nameof(incoming));
        AuthorityVisits++; WindowUpdates++;
        if (h.Lease != h.State.Lease) return; // Model route oracle identifies an obsolete generation.
        var s = h.State;
        var delta = Math.Min(incoming, streamWindow - s.Credit); // #840 effective stream delta.
        if (delta > s.Sent)
            throw new InvalidOperationException("oracle peer schedule cannot credit unsent bytes");
        s.Credit += delta; s.Sent -= delta; Free += delta;
        Drain(); Check();
    }
    public void Abort(Handle h, Exception error)
    {
        CheckHandle(h); AuthorityVisits++;
        var s = h.State;
        Free += s.Grant + s.Reserved + s.Sent;
        s.Grant = s.Reserved = s.Sent = 0; s.Credit = streamWindow; s.Error ??= error;
        Drain(); Check();
    }
    public void Complete(Handle h)
    {
        CheckHandle(h); AuthorityVisits++;
        var s = h.State;
        Free += s.Grant; s.Grant = 0; s.Completed = true;
        Drain(); Check();
    }
    public void Cancel(Pending r)
    {
        AuthorityVisits++;
        if (r.Ticket is not null) throw new InvalidOperationException("admitted cancellation must settle ticket");
        waiters.Remove(r); r.Error = new OperationCanceledException("model cancellation");
        Drain(); Check();
    }
    public void Progress() { AuthorityVisits++; ProgressOperations++; }
    Exception? Error(Handle h)
    {
        if (h.Lease != h.State.Lease) return new InvalidOperationException("stale lease");
        return h.State.Error ?? (h.State.Completed ? new InvalidOperationException("stream closed") : null);
    }
    static bool Can(long available, long window, int bytes) => bytes <= available || (bytes > window && available == window);
    static void CheckHandle(Handle h)
    {
        if (h.Lease != h.State.Lease) throw new InvalidOperationException("stale lease");
    }
    void Peak() => PeakGrantBytes = Math.Max(PeakGrantBytes, states.Sum(s => s.Grant));
    public void Check()
    {
        long total = Free;
        foreach (var s in states)
        {
            if (s.Grant < 0 || s.Reserved < 0 || s.Sent < 0 ||
                s.Credit + s.Reserved + s.Sent != streamWindow ||
                s.Grant > Math.Max(0, s.Credit))
                throw new InvalidOperationException("stream/grant conservation");
            total += s.Grant + s.Reserved + s.Sent;
        }
        if (total != connectionWindow || Free > connectionWindow)
            throw new InvalidOperationException("connection conservation");
    }
}
sealed class State
{
    public long Lease, Credit, Grant, Reserved, Sent;
    public bool Completed;
    public Exception? Error;
}
readonly record struct Handle(State State, long Lease);
sealed class Pending(Handle handle, int bytes)
{
    public readonly Handle Handle = handle;
    public readonly int Bytes = bytes;
    public Ticket? Ticket;
    public Exception? Error;
}
sealed class Ticket(Handle handle, int bytes)
{
    public readonly Handle Handle = handle;
    public readonly int Bytes = bytes;
    public int Phase; // 0 producer-owned, 1 writer-owned, 2 refunded/settled by producer.
}

