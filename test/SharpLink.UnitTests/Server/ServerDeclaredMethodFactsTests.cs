using System.Collections;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class ServerDeclaredMethodFactsTests
{
    [Test]
    [Arguments(RpcMethodKind.Unary)]
    [Arguments(RpcMethodKind.OneWay)]
    [Arguments(RpcMethodKind.ClientStreaming)]
    [Arguments(RpcMethodKind.ServerStreaming)]
    [Arguments(RpcMethodKind.DuplexStreaming)]
    public void CompleteDeclaredFactsAreFrozenWithoutRequestCallbacks(RpcMethodKind kind)
    {
        var count = kind is RpcMethodKind.ClientStreaming or RpcMethodKind.DuplexStreaming ? 2 : 0;
        var response = kind != RpcMethodKind.OneWay;
        var facts = new RpcMethodFacts(8, long.MinValue, kind, count, true, response,
            responseNullable: response, hasMethodTimeout: true, methodTimeout: TimeSpan.FromTicks(123), isIdempotent: true);
        var stub = new Stub(_ => facts);
        var quota = new ServerDeclaredMethodFactsQuota();
        using var group = quota.BeginGroup();
        var reservation = group.ReserveContract();
        reservation.SetMethodCount(1);
        group.Seal();
        using var table = ServerDeclaredMethodFacts.Prepare(Contract([Method(long.MinValue, kind)]), stub, reservation);
        group.Commit();
        group.Dispose();
        Ensure(quota.Methods == 1 && quota.Bytes == 384);
        stub.Poison = true;
        Ensure(ReferenceEquals(table.Resolve(long.MinValue), facts));
        Ensure(ReferenceEquals(table.Resolve(long.MaxValue), RpcMethodFacts.UnknownMethod));
        Ensure(stub.Reads == 1 && stub.Resolutions == 1 && stub.BusinessCalls == 0);
        table.Dispose();
        table.Dispose();
        Ensure(quota.Methods == 0 && quota.Bytes == 0);
        Ensure(ReferenceEquals(table.Resolve(long.MinValue), facts)); // Disposal cannot mutate captured scalar readers.
    }

    [Test]
    public void SortedLookupPreservesSignedIdsAndOriginalInstances()
    {
        var ids = new[] { long.MaxValue, -1L, 0L, long.MinValue, 17L };
        var facts = new Dictionary<long, RpcMethodFacts>();
        var descriptors = new List<SharpLinkGeneratedMethodDescriptor>();
        foreach (var id in ids)
        {
            facts.Add(id, new RpcMethodFacts(8, id, RpcMethodKind.Unary, 0, true, true));
            descriptors.Add(Method(id));
        }
        using var group = new ServerDeclaredMethodFactsQuota().BeginGroup();
        var reservation = group.ReserveContract();
        reservation.SetMethodCount(ids.Length);
        group.Seal();
        using var table = ServerDeclaredMethodFacts.Prepare(Contract(descriptors), new Stub(id => facts[id]), reservation);
        foreach (var id in ids)
            Ensure(ReferenceEquals(table.Resolve(id), facts[id]));
        Ensure(ReferenceEquals(table.Resolve(16), RpcMethodFacts.UnknownMethod));
    }

    [Test]
    public void DuplicateAndEnumerationDriftRejectBeforeFactsCallbacks()
    {
        VerifyInvalid([Method(1), Method(1)], expectedResolutions: 0);
        VerifyInvalid(new CountedList(1, [Method(1), Method(2)]), expectedResolutions: 0);
        VerifyInvalid(new CountedList(2, [Method(1)]), expectedResolutions: 0);
        VerifyInvalid(new CountedList(1, [Method(1)], inconsistentIndexer: true), expectedResolutions: 0);
    }

    [Test]
    public void CallbackMutationAndInvalidFactsRejectWithExactContext()
    {
        var invalidFacts = new RpcMethodFacts?[]
        {
            null, RpcMethodFacts.UnknownMethod,
            (RpcMethodFacts)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(RpcMethodFacts)),
            new RpcMethodFacts(9, 1, RpcMethodKind.Unary, 0, true, true),
            new RpcMethodFacts(8, 2, RpcMethodKind.Unary, 0, true, true),
            new RpcMethodFacts(8, 1, RpcMethodKind.OneWay, 0, true, false),
            new RpcMethodFacts(8, 1, RpcMethodKind.Unary, 0, false, true)
        };
        foreach (var facts in invalidFacts)
            VerifyInvalid([Method(1)], 1, _ => facts!);
        var methods = new List<SharpLinkGeneratedMethodDescriptor> { Method(1) };
        VerifyInvalid(methods, 1, id =>
        {
            methods[0] = Method(2);
            return new RpcMethodFacts(8, id, RpcMethodKind.Unary, 0, true, true);
        });
    }

    [Test]
    public void EmptyDomainAndOneWayClientStreamsRetainDeclaredTimeoutPresence()
    {
        var quota = new ServerDeclaredMethodFactsQuota();
        using var group = quota.BeginGroup();
        var emptyReservation = group.ReserveContract();
        emptyReservation.SetMethodCount(0);
        var streamReservation = group.ReserveContract();
        streamReservation.SetMethodCount(1);
        group.Seal();
        var emptyStub = new Stub(_ => throw new Exception("Empty contract cannot resolve any method."));
        using var empty = ServerDeclaredMethodFacts.Prepare(Contract([]), emptyStub, emptyReservation);
        var facts = new RpcMethodFacts(8, 1, RpcMethodKind.OneWay, 3, true, false,
            hasMethodTimeout: true, methodTimeout: null);
        using var stream = ServerDeclaredMethodFacts.Prepare(Contract([Method(1, RpcMethodKind.OneWay)]),
            new Stub(_ => facts), streamReservation);
        Ensure(ReferenceEquals(empty.Resolve(1), RpcMethodFacts.UnknownMethod) &&
            emptyStub.Reads == 1 && emptyStub.Resolutions == 0);
        Ensure(ReferenceEquals(stream.Resolve(1), facts) && facts.HasMethodTimeout && !facts.MethodTimeout.HasValue);
    }

    [Test]
    public void WrongContractAndChangedReservedCountNeverResolveMethods()
    {
        using var group = new ServerDeclaredMethodFactsQuota().BeginGroup();
        var wrongContract = group.ReserveContract();
        wrongContract.SetMethodCount(1);
        var wrongCount = group.ReserveContract();
        wrongCount.SetMethodCount(0);
        group.Seal();
        var stub = new Stub(id => new RpcMethodFacts(8, id, RpcMethodKind.Unary, 0, true, true));
        var contract = Contract([Method(1)]) with { ContractId = 9 };
        Expect<ServerDeclaredMethodFactsException>(() => ServerDeclaredMethodFacts.Prepare(contract, stub, wrongContract));
        Expect<ServerDeclaredMethodFactsException>(() => ServerDeclaredMethodFacts.Prepare(Contract([Method(1)]), stub, wrongCount));
        Ensure(stub.Reads == 1 && stub.Resolutions == 0);
    }

    [Test]
    public void CallbackFailureKeepsChargeUntilRollbackAndRetainsOriginalCauseLocally()
    {
        var quota = new ServerDeclaredMethodFactsQuota();
        using var group = quota.BeginGroup();
        var reservation = group.ReserveContract();
        reservation.SetMethodCount(1);
        group.Seal();
        var original = new InvalidOperationException("callback failure");
        var failure = Expect<ServerDeclaredMethodFactsException>(() =>
            ServerDeclaredMethodFacts.Prepare(Contract([Method(1)]), new Stub(_ => throw original), reservation));
        Ensure(failure.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidManifest &&
            failure.ContractId == 8 && failure.MethodId == 1 && ReferenceEquals(failure.InnerException, original));
        Ensure(quota.Methods == 1 && quota.Bytes == 384);
        Expect<InvalidOperationException>(() => group.Commit());
        group.Dispose();
        group.Dispose();
        Ensure(quota.Methods == 0 && quota.Bytes == 0);
    }

    [Test]
    public void CancellationAndSealingPreventCallbacksWithoutReleasingPhysicalOwner()
    {
        var quota = new ServerDeclaredMethodFactsQuota();
        using var group = quota.BeginGroup();
        var reservation = group.ReserveContract();
        reservation.SetMethodCount(1);
        var stub = new Stub(id => new RpcMethodFacts(8, id, RpcMethodKind.Unary, 0, true, true));
        Expect<InvalidOperationException>(() => ServerDeclaredMethodFacts.Prepare(Contract([Method(1)]), stub, reservation));
        group.Seal();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Expect<OperationCanceledException>(() =>
            ServerDeclaredMethodFacts.Prepare(Contract([Method(1)]), stub, reservation, cancellation.Token));
        Ensure(stub.Reads == 0 && stub.Resolutions == 0 && quota.Bytes == 384);
        group.Dispose();
        Ensure(quota.Bytes == 0);
    }

    private static void VerifyInvalid(IReadOnlyList<SharpLinkGeneratedMethodDescriptor> methods,
        int expectedResolutions, Func<long, RpcMethodFacts>? resolve = null)
    {
        var quota = new ServerDeclaredMethodFactsQuota();
        using var group = quota.BeginGroup();
        var reservation = group.ReserveContract();
        reservation.SetMethodCount(methods.Count);
        group.Seal();
        var stub = new Stub(resolve ?? (id => new RpcMethodFacts(8, id, RpcMethodKind.Unary, 0, true, true)));
        var error = Expect<ServerDeclaredMethodFactsException>(() =>
            ServerDeclaredMethodFacts.Prepare(Contract(methods), stub, reservation));
        Ensure(error.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidManifest && error.ContractId == 8);
        Ensure(stub.Resolutions == expectedResolutions && stub.BusinessCalls == 0);
        group.Dispose();
        Ensure(quota.Bytes == 0 && quota.Methods == 0);
    }

    private static SharpLinkGeneratedMethodDescriptor Method(long id, RpcMethodKind kind = RpcMethodKind.Unary)
        => new("Method", id, kind, true, "request", "response", "fingerprint");

    private static SharpLinkGeneratedContractDescriptor Contract(IReadOnlyList<SharpLinkGeneratedMethodDescriptor> methods)
        => new(typeof(object), "Contract", 8, "fingerprint", methods,
            static (_, _) => throw new InvalidOperationException("No proxy factory in facts preparation."),
            static _ => throw new InvalidOperationException("Caller supplies the already privately owned stub."));

    private static T Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }

    private static void Ensure(bool condition)
    {
        if (!condition) throw new Exception("Declared-method facts assertion failed.");
    }

    private sealed class CountedList(int count, SharpLinkGeneratedMethodDescriptor[] items,
        bool inconsistentIndexer = false) : IReadOnlyList<SharpLinkGeneratedMethodDescriptor>
    {
        public int Count => count;
        public SharpLinkGeneratedMethodDescriptor this[int index] => inconsistentIndexer ? Method(99) : items[index];
        public IEnumerator<SharpLinkGeneratedMethodDescriptor> GetEnumerator()
            => ((IEnumerable<SharpLinkGeneratedMethodDescriptor>)items).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Stub(Func<long, RpcMethodFacts> resolve) : IRpcStub
    {
        internal int Reads;
        internal int Resolutions;
        internal int BusinessCalls;
        internal bool Poison;
        public long InterfaceHash { get { Reads++; if (Poison) throw new Exception("Poison getter."); return 8; } }
        public RpcMethodFacts ResolveMethodFacts(long methodHash)
        { Resolutions++; if (Poison) throw new Exception("Poison resolver."); return resolve(methodHash); }
        private ValueTask Business() { BusinessCalls++; throw new Exception("Business callback must never run."); }
        public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args) => Business();
        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken) => Business();
        public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output) => Business();
        public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output, CancellationToken cancellationToken) => Business();
    }
}
