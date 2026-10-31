using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public class ServerBindingTableTests
{
    [Test]
    public void CapturedEntryNeverRetargetsAfterReleaseAndSlotReuse()
    {
        var quota = new ServerBindingSlotQuota(1);
        var table = new ServerBindingTable(1, quota);
        var oldRegistration = Registration(1);
        var replacement = Registration(2);
        var old = Bind(table, 1, oldRegistration);
        var captured = table.Capture(old.Token) ?? throw new InvalidOperationException("Missing captured entry.");
        Ensure(ReferenceEquals(old, captured));
        Ensure(table.Release(old.Token));
        Ensure(table.Capture(old.Token) is null && quota.Used == 0);
        var current = Bind(table, 2, replacement);
        Ensure(current.Token != old.Token && Protocol3BindingToken.Incarnation(current.Token) == 2);
        Ensure(!table.Release(old.Token) && !table.Cancel(1));
        Ensure(ReferenceEquals(table.Capture(current.Token), current));

        // Captured-before-release is an exact old control, not an implicit lease or
        // registry relookup. It may still acquire while that control remains open.
        Ensure(table.TryAcquireCaptured(captured, out var oldLease));
        Ensure(ReferenceEquals(oldLease.Control, oldRegistration));
        Ensure(oldRegistration.PhysicalOwnerCount == 1 && replacement.PhysicalOwnerCount == 0);
        oldLease.Release();
        oldRegistration.Retire();
        Ensure(!table.TryAcquireCaptured(captured, out _));
        Ensure(table.TryAcquire(current.Token, out var newLease));
        Ensure(ReferenceEquals(newLease.Control, replacement));
        newLease.Release();
        table.Close();
        Ensure(quota.Used == 0);
    }

    [Test]
    public void CaptureThenClosePreventsAcquisitionAndLeavesNoPhysicalOwner()
    {
        var quota = new ServerBindingSlotQuota(2);
        var table = new ServerBindingTable(2, quota);
        var registration = Registration(1);
        var entry = Bind(table, 1, registration);
        var captured = table.Capture(entry.Token) ?? throw new InvalidOperationException("Missing captured entry.");
        table.Close();
        Ensure(table.IsClosed && quota.Used == 0);
        Ensure(!table.TryAcquireCaptured(captured, out _) && !table.TryAcquire(entry.Token, out _));
        Ensure(registration.PhysicalOwnerCount == 0);
        Ensure(!table.Release(entry.Token) && !table.Cancel(1));
        table.Close();
        Ensure(quota.Used == 0);
        table.ObserveBindId(2);
        Ensure(table.TryBind(2, registration, out var refused) == Protocol3BindStatus.SessionDraining && refused is null);
    }

    [Test]
    public void AdmittedPhysicalLeaseSurvivesSessionCloseAndRegistrationRetirement()
    {
        var quota = new ServerBindingSlotQuota(1);
        var table = new ServerBindingTable(1, quota);
        var registration = Registration(1);
        var entry = Bind(table, 1, registration);
        Ensure(table.TryAcquire(entry.Token, out var lease));
        var exactPayload = lease.Registration;
        table.Close();
        registration.Retire();
        Ensure(quota.Used == 0 && registration.PhysicalOwnerCount == 1);
        Ensure(ReferenceEquals(lease.Registration, exactPayload));
        Ensure(!table.TryAcquireCaptured(entry, out _));
        lease.Release();
        Ensure(registration.PhysicalOwnerCount == 0);
    }

    [Test]
    public void DifferentBindIdsForSameRegistrationAlwaysOwnDistinctTokens()
    {
        var quota = new ServerBindingSlotQuota(2);
        var table = new ServerBindingTable(2, quota);
        var registration = Registration(1);
        var first = Bind(table, 1, registration);
        var second = Bind(table, 2, registration);
        Ensure(first.Token != second.Token && quota.Used == 2);
        Ensure(table.Cancel(1) && !table.Cancel(1));
        Ensure(table.Capture(first.Token) is null && ReferenceEquals(table.Capture(second.Token), second));
        Ensure(quota.Used == 1);
        Ensure(table.TryAcquire(second.Token, out var lease));
        lease.Release();
        table.Close();
        Ensure(quota.Used == 0);
    }

    [Test]
    public void GlobalQuotaIsReservedAcrossSessionsAndReclaimedExactlyOnce()
    {
        var quota = new ServerBindingSlotQuota(1);
        var first = new ServerBindingTable(2, quota);
        var second = new ServerBindingTable(2, quota);
        var registration = Registration(1);
        var entry = Bind(first, 1, registration);
        second.ObserveBindId(1);
        Ensure(second.TryBind(1, registration, out var full) == Protocol3BindStatus.BindingCapacity && full is null);
        Ensure(quota.Used == 1);
        first.Close();
        Ensure(!first.Release(entry.Token) && !first.Cancel(1) && quota.Used == 0);
        var secondEntry = Bind(second, 2, registration);
        Ensure(Protocol3BindingToken.Incarnation(secondEntry.Token) == 1);
        // Equal numeric tokens in different physical Sessions convey no cross-Session ownership.
        Ensure(!second.TryAcquireCaptured(entry, out _));
        Ensure(registration.PhysicalOwnerCount == 0);
        Ensure(second.Release(secondEntry.Token));
        second.Close();
        Ensure(quota.Used == 0);
    }

    [Test]
    public void FixedTableCapacityRetiredRegistrationAndMalformedTokensDoNotAllocateSlots()
    {
        var quota = new ServerBindingSlotQuota(2);
        var table = new ServerBindingTable(1, quota);
        var registration = Registration(1);
        var entry = Bind(table, 1, registration);
        table.ObserveBindId(2);
        Ensure(table.TryBind(2, registration, out var full) == Protocol3BindStatus.BindingCapacity && full is null);
        Ensure(quota.Used == 1);
        Ensure(table.Capture(0) is null && table.Capture((1UL << 16) | 1) is null);
        ExpectViolation(() => table.Release((1UL << 16) | 1));
        Ensure(table.Release(entry.Token));
        registration.Retire();
        table.ObserveBindId(3);
        Ensure(table.TryBind(3, registration, out var retired) == Protocol3BindStatus.RegistrationRetiring && retired is null);
        Ensure(quota.Used == 0 && registration.PhysicalOwnerCount == 0);
        table.Close();
    }

    [Test]
    public void IncarnationExhaustionPermanentlyDrainsInsteadOfWrapping()
    {
        var quota = new ServerBindingSlotQuota(1);
        var table = new ServerBindingTable(1, quota, Protocol3BindingToken.MaximumIncarnation - 1);
        var registration = Registration(1);
        var last = Bind(table, 1, registration);
        Ensure(Protocol3BindingToken.Incarnation(last.Token) == Protocol3BindingToken.MaximumIncarnation);
        Ensure(table.Release(last.Token));
        table.ObserveBindId(2);
        Ensure(table.TryBind(2, registration, out var exhausted) == Protocol3BindStatus.SessionDraining && exhausted is null);
        table.ObserveBindId(3);
        Ensure(table.TryBind(3, registration, out exhausted) == Protocol3BindStatus.SessionDraining && exhausted is null);
        Ensure(quota.Used == 0 && table.Capture(last.Token) is null);
        table.Close();
    }

    [Test]
    public void ResolutionFailureStillAdvancesCorrelationWithoutInstallingAnEntry()
    {
        var quota = new ServerBindingSlotQuota(1);
        var table = new ServerBindingTable(1, quota);
        var registration = Registration(1);
        table.ObserveBindId(1); // controller returns UnknownContract, never calls TryBind
        Ensure(!table.Cancel(1) && quota.Used == 0);
        ExpectViolation(() => table.ObserveBindId(1));
        var entry = Bind(table, 2, registration);
        ExpectViolation(() => table.TryBind(1, registration, out _));
        Ensure(!table.Cancel(1) && ReferenceEquals(table.Capture(entry.Token), entry));
        Ensure(table.Release(entry.Token));
        ExpectViolation(() => table.TryBind(2, registration, out _));
        table.Close();
        Ensure(quota.Used == 0);
    }

    [Test]
    public void BindIdsArePositiveMonotonicAndNeverReusableAfterReleaseOrFailure()
    {
        var quota = new ServerBindingSlotQuota(1);
        var table = new ServerBindingTable(1, quota);
        var registration = Registration(1);
        foreach (var invalid in new[] { 0UL, 1UL << 63, ulong.MaxValue })
        {
            ExpectViolation(() => table.TryBind(invalid, registration, out _));
            ExpectViolation(() => table.ObserveBindId(invalid));
            ExpectViolation(() => table.Cancel(invalid));
        }
        var first = Bind(table, 3, registration); // gaps for never-published client IDs are legal
        table.ObserveBindId(5);
        Ensure(table.TryBind(5, registration, out _) == Protocol3BindStatus.BindingCapacity);
        Ensure(table.Release(first.Token));
        foreach (var reused in new[] { 1UL, 3UL, 5UL })
        {
            ExpectViolation(() => table.ObserveBindId(reused));
            ExpectViolation(() => table.TryBind(reused, registration, out _));
        }
        Ensure(!table.Cancel(3) && !table.Cancel(5));
        ExpectViolation(() => table.Cancel(6));
        var final = Bind(table, long.MaxValue, registration);
        Ensure(final.CreatorOperationId == long.MaxValue);
        Ensure(table.Cancel(long.MaxValue) && quota.Used == 0);
        ExpectViolation(() => table.TryBind(long.MaxValue, registration, out _));
        table.Close();
    }

    private static ServerBindingEntry Bind(ServerBindingTable table, ulong id, ServerBindingRegistration registration)
    {
        table.ObserveBindId(id);
        Ensure(table.TryBind(id, registration, out var entry) == Protocol3BindStatus.Success);
        return entry ?? throw new InvalidOperationException("Successful Bind did not install its entry.");
    }

    private static ServerBindingRegistration Registration(long id)
    {
        // Factory, provider, facts and invocation are poison pills. Table acquisition only
        // captures the actual registration payload and must never activate/dispatch it.
        var payload = ServiceRegistration.CreateSingletonFactory(typeof(object), new NoBusinessStub(),
            new NoServices(), static _ => throw new InvalidOperationException("Business factory called."), ownsService: false);
        return new ServerBindingRegistration(payload, 7, new RpcHash128(1, 2), id, id);
    }

    private static void Ensure(bool condition)
    {
        if (!condition)
            throw new InvalidOperationException("Server binding table assertion failed.");
    }

    private static void ExpectViolation(Action action)
    {
        try { action(); }
        catch (SharpLinkProtocolViolationException) { return; }
        throw new InvalidOperationException("Expected binding protocol violation.");
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => throw new InvalidOperationException("Service provider called.");
    }

    private sealed class NoBusinessStub : IRpcStub
    {
        public long InterfaceHash => 7;
        public RpcMethodFacts ResolveMethodFacts(long methodHash) => throw new InvalidOperationException("Method facts called.");
        public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args) => throw new InvalidOperationException("Business callback called.");
        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Business callback called.");
        public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output)
            => throw new InvalidOperationException("Business callback called.");
        public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Business callback called.");
    }
}
