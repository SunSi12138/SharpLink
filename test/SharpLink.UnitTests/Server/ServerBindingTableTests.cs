using System.Runtime.CompilerServices;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public class ServerBindingTableTests
{
    [Test]
    public void CompletedCancelIsNoOpAndNeverReleasesTheNextCreator()
    {
        var quota = new ServerBindingSlotQuota(1);
        var table = new ServerBindingTable(1, quota);
        var registration = Registration(1);
        var first = Bind(table, 1, registration);
        table.CompleteBindId(1);
        Ensure(!table.Cancel(1) && ReferenceEquals(table.Capture(first.Token), first) && quota.Used == 1);
        Ensure(table.Release(first.Token));
        var second = Bind(table, 2, registration);
        Ensure(!table.Cancel(1) && ReferenceEquals(table.Capture(second.Token), second));
        Ensure(table.Cancel(2)); // Exact nonterminal creator, not the completed predecessor.
        table.CompleteBindId(2);
        Ensure(!table.Cancel(2) && quota.Used == 0);
        table.ObserveBindId(3); // Failed resolution still has a final result and terminal ID.
        table.CompleteBindId(3);
        Ensure(!table.Cancel(3));
        ExpectViolation(() => table.TryBind(3, registration, out _));
        ExpectViolation(() => table.Cancel(4));
        table.Close();
    }

    [Test]
    public void RetiredTokenScanIsBoundedAndCannotDetachAReusedSlot()
    {
        var quota = new ServerBindingSlotQuota(2);
        var table = new ServerBindingTable(2, quota);
        var retired = Registration(1);
        var live = Registration(2);
        var old = Bind(table, 1, retired);
        var second = Bind(table, 2, live);
        retired.Retire();
        var tokens = new ulong[1];
        var cursor = 0;
        Ensure(table.TakeRetiredTokens(tokens, ref cursor, scanLimit: 1) == 1);
        Ensure(tokens[0] == old.Token && cursor == 1 && quota.Used == 1);
        var reused = Bind(table, 3, live);
        Ensure(Protocol3BindingToken.Slot(reused.Token) == Protocol3BindingToken.Slot(old.Token));
        Ensure(table.TakeRetiredTokens(tokens, ref cursor, scanLimit: 1) == 0 && cursor == 2);
        Ensure(!table.Release(old.Token));
        Ensure(ReferenceEquals(table.Capture(reused.Token), reused));
        Ensure(ReferenceEquals(table.Capture(second.Token), second) && quota.Used == 2);
        cursor = 0;
        Ensure(table.TakeRetiredTokens(tokens, ref cursor) == 0 && cursor == 2);
        table.Close();
        Ensure(quota.Used == 0);
    }

    [Test]
    public void SharedRetirementDetachesEveryExactWeakSelectionAndFullFactsTable()
    {
        var group = new ServerBindingAdmissionGroup();
        var (first, firstPayload) = RegistrationWithPayload(1, group);
        var (second, secondPayload) = RegistrationWithPayload(2, group);
        group.Seal([first, second]);
        Ensure(first.TryGetExactPayload(out var selectedFirst) && ReferenceEquals(selectedFirst, firstPayload));
        Ensure(second.TryGetExactPayload(out var selectedSecond) && ReferenceEquals(selectedSecond, secondPayload));
        var selectedFacts = first.Facts!.Resolve(11);
        group.Retire();
        Ensure(first.IsRetired && second.IsRetired);
        Ensure(!first.TryGetExactPayload(out _) && !second.TryGetExactPayload(out _));
        Ensure(first.Facts is null && second.Facts is null);
        Ensure(selectedFacts.IsKnown && selectedFacts.ContractId == 7 && selectedFacts.MethodId == 11);
        group.Retire(); // Metadata retirement is idempotent, not a physical drain owner.
        GC.KeepAlive(firstPayload);
        GC.KeepAlive(secondPayload);
    }

    [Test]
    public void CapturedFactsLookupReusesTheExactPreparedInstanceWithoutCallingStubAgain()
    {
        var (registration, payload) = RegistrationWithPayload(1);
        var facts = registration.Facts!;
        var first = facts.Resolve(11);
        Ensure(ReferenceEquals(first, facts.Resolve(11)));
        Ensure(ReferenceEquals(facts.Resolve(12), RpcMethodFacts.UnknownMethod));
        Ensure(ReferenceEquals(facts.Resolve(long.MinValue), RpcMethodFacts.UnknownMethod));
        // The fixture stub throws on any second callback, including undeclared IDs.
        // Selected facts remain scalar even after the control detaches the whole table.
        registration.Retire();
        Ensure(registration.Facts is null && first.IsKnown && first.MethodId == 11);
        GC.KeepAlive(payload);
    }

    [Test]
    public async Task StaticCleanupRetiresMetadataWithoutInventingABindingLease()
    {
        var (registration, payload) = RegistrationWithPayload(1);
        var cleanup = new ServerServiceCleanup([payload], ownedServiceProvider: null);
        await cleanup.DisposeAsync();
        Ensure(registration.IsRetired && registration.Facts is null);
        Ensure(!registration.TryGetExactPayload(out _));
        // This component owns no invocation. In real dispatch, existing request permits
        // must finish before service cleanup; the old outer binding lease is not retained.
    }

    [Test]
    public void CapturedEntryNeverRetargetsAfterReleaseAndSlotReuse()
    {
        var quota = new ServerBindingSlotQuota(1);
        var table = new ServerBindingTable(1, quota);
        var (oldRegistration, oldPayload) = RegistrationWithPayload(1);
        var (replacement, newPayload) = RegistrationWithPayload(2);
        var old = Bind(table, 1, oldRegistration);
        var captured = table.Capture(old.Token) ?? throw new InvalidOperationException("Missing captured entry.");
        Ensure(ReferenceEquals(old, captured));
        Ensure(table.Release(old.Token));
        Ensure(table.Capture(old.Token) is null && quota.Used == 0);
        var current = Bind(table, 2, replacement);
        Ensure(current.Token != old.Token && Protocol3BindingToken.Incarnation(current.Token) == 2);
        Ensure(!table.Release(old.Token) && !table.Cancel(1));
        Ensure(ReferenceEquals(table.Capture(current.Token), current));

        // Capture is an exact old identity, not an implicit disposal fence or a lookup
        // of the replacement. Payload use would still need post-permit physical admission.
        Ensure(table.CanUseCaptured(captured));
        Ensure(captured.Registration.TryGetExactPayload(out var oldSelected));
        Ensure(ReferenceEquals(oldSelected, oldPayload) && !ReferenceEquals(oldSelected, newPayload));
        oldRegistration.Retire();
        Ensure(!table.CanUseCaptured(captured) && !captured.Registration.TryGetExactPayload(out _));
        Ensure(table.CanUseCaptured(current));
        Ensure(current.Registration.TryGetExactPayload(out var newSelected) && ReferenceEquals(newSelected, newPayload));
        GC.KeepAlive(oldPayload);
        GC.KeepAlive(newPayload);
        table.Close();
        Ensure(quota.Used == 0);
    }

    [Test]
    public void CaptureThenCloseRejectsSelectionWithoutASecondPhysicalOwner()
    {
        var quota = new ServerBindingSlotQuota(2);
        var table = new ServerBindingTable(2, quota);
        var registration = Registration(1);
        var entry = Bind(table, 1, registration);
        var captured = table.Capture(entry.Token) ?? throw new InvalidOperationException("Missing captured entry.");
        table.Close();
        Ensure(table.IsClosed && quota.Used == 0);
        Ensure(!table.CanUseCaptured(captured) && table.Capture(entry.Token) is null);
        Ensure(!table.Release(entry.Token) && !table.Cancel(1));
        table.Close();
        Ensure(quota.Used == 0);
        table.ObserveBindId(2);
        Ensure(table.TryBind(2, registration, out var refused) == Protocol3BindStatus.SessionDraining && refused is null);
    }

    [Test]
    public void RetainedEntryAndControlDoNotRootRetiredPayloadOrFullFactsTable()
    {
        var (table, captured, payload, stub, facts) = CaptureRetiredWeakSelection();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        Ensure(!payload.TryGetTarget(out _) && !stub.TryGetTarget(out _) && !facts.TryGetTarget(out _));
        Ensure(captured.Registration.Facts is null && !captured.Registration.TryGetExactPayload(out _));
        Ensure(!table.CanUseCaptured(captured));
        GC.KeepAlive(captured);
        table.Close();
    }

    [Test]
    public void ExactWeakControlRejectsPayloadWhoseBindingIdentityWasChanged()
    {
        var (original, payload) = RegistrationWithPayload(1);
        var replacement = Registration(2);
        payload.Binding = replacement;
        Ensure(!original.TryGetExactPayload(out _));
        Ensure(ReferenceEquals(payload.Binding, replacement));
        GC.KeepAlive(payload);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ServerBindingTable Table, ServerBindingEntry Entry,
        WeakReference<ServiceRegistration> Payload, WeakReference<IRpcStub> Stub,
        WeakReference<ServerDeclaredMethodFacts> Facts) CaptureRetiredWeakSelection()
    {
        var (registration, payload) = RegistrationWithPayload(1);
        var facts = registration.Facts!;
        var table = new ServerBindingTable(1, new ServerBindingSlotQuota(1));
        var captured = Bind(table, 1, registration);
        var weakPayload = new WeakReference<ServiceRegistration>(payload);
        var weakStub = new WeakReference<IRpcStub>(payload.Stub);
        var weakFacts = new WeakReference<ServerDeclaredMethodFacts>(facts);
        registration.Retire();
        facts.Dispose(); // Mirrors the independent service cleanup quota owner, not retirement.
        return (table, captured, weakPayload, weakStub, weakFacts);
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
        Ensure(table.CanUseCaptured(second));
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
        Ensure(!second.CanUseCaptured(entry));
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
        Ensure(quota.Used == 0);
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

    private static ServerBindingRegistration Registration(long id, ServerBindingAdmissionGroup? group = null)
        => RegistrationWithPayload(id, group).Control;

    private static (ServerBindingRegistration Control, ServiceRegistration Payload) RegistrationWithPayload(
        long id, ServerBindingAdmissionGroup? group = null)
    {
        // Only cold preparation calls the facts getter. Provider/factory/invocation stay poison.
        var stub = new NoBusinessStub();
        var payload = ServiceRegistration.CreateSingletonFactory(typeof(object), stub,
            new NoServices(), static _ => throw new InvalidOperationException("Business factory called."), ownsService: false);
        var descriptor = new SharpLinkGeneratedContractDescriptor(typeof(object), "BindingTableFixture", 7, "fixture",
            [new SharpLinkGeneratedMethodDescriptor("Known", 11, RpcMethodKind.Unary, false, "", "", "fixture")],
            static (_, _) => throw new InvalidOperationException("Proxy factory called."),
            static _ => throw new InvalidOperationException("Stub factory called."));
        var quota = new ServerDeclaredMethodFactsQuota();
        using var preparation = quota.BeginGroup();
        var slot = preparation.ReserveContract();
        slot.SetMethodCount(descriptor.Methods.Count);
        preparation.Seal();
        var facts = ServerDeclaredMethodFacts.Prepare(descriptor, stub, slot);
        preparation.Commit();
        payload.DeclaredFacts = facts;
        var registration = group is null
            ? new ServerBindingRegistration(payload, 7, new RpcHash128(1, 2), id, id, facts)
            : new ServerBindingRegistration(payload, 7, new RpcHash128(1, 2), id, id, facts, group);
        payload.Binding = registration;
        return (registration, payload);
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
        private readonly RpcMethodFacts _facts = new(7, 11, RpcMethodKind.Unary, 0, false, true);
        private bool _resolved;
        public RpcMethodFacts ResolveMethodFacts(long methodHash)
        {
            if (_resolved || methodHash != 11)
                throw new InvalidOperationException("Facts callback repeated or called for an undeclared method.");
            _resolved = true;
            return _facts;
        }
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
