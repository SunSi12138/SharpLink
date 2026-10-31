namespace SharpLink.Server;

/// <summary>Exact declared scalar facts, prepared once outside publication locks.</summary>
internal sealed class ServerDeclaredMethodFacts : IDisposable
{
    private readonly Entry[] _entries;
    private readonly ServerDeclaredMethodFactsReservation _reservation;

    private ServerDeclaredMethodFacts(Entry[] entries, ServerDeclaredMethodFactsReservation reservation)
    {
        _entries = entries;
        _reservation = reservation;
    }

    internal int Count => _entries.Length;
    internal ServerDeclaredMethodFactsGroupReservation Group => _reservation.Group;

    internal RpcMethodFacts Resolve(long methodId)
    {
        var low = 0;
        var high = _entries.Length - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) >> 1);
            ref readonly var entry = ref _entries[middle];
            if (entry.MethodId == methodId)
                return entry.Facts;
            if (entry.MethodId < methodId)
                low = middle + 1;
            else
                high = middle - 1;
        }
        return RpcMethodFacts.UnknownMethod;
    }

    internal static ServerDeclaredMethodFacts Prepare(
        SharpLinkGeneratedContractDescriptor contract,
        IRpcStub stub,
        ServerDeclaredMethodFactsReservation reservation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(stub);
        ArgumentNullException.ThrowIfNull(reservation);
        reservation.Group.BeginPreparation(reservation);
        long? currentMethod = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var methods = contract.Methods ?? throw Invalid(contract.ContractId, null, "Declared methods are missing.");
            var count = reservation.Count;
            if (methods.Count != count)
                throw Invalid(contract.ContractId, null, "Declared count changed after reservation.");

            // All count/byte reservations are complete before either count-sized allocation.
            var declared = new SharpLinkGeneratedMethodDescriptor[count];
            var entries = new Entry[count];
            var index = 0;
            foreach (var method in methods)
            {
                if (index == count)
                    throw Invalid(contract.ContractId, null, "Declared enumeration exceeds its captured count.");
                if (method is null || method.Kind is < RpcMethodKind.Unary or > RpcMethodKind.DuplexStreaming ||
                    string.IsNullOrWhiteSpace(method.Name) || method.RequestSchema is null ||
                    method.ResponseSchema is null || string.IsNullOrWhiteSpace(method.Fingerprint))
                    throw Invalid(contract.ContractId, method?.MethodId, "Declared method descriptor is malformed.");
                declared[index] = method;
                entries[index] = new Entry(method.MethodId, null!);
                index++;
            }
            if (index != count)
                throw Invalid(contract.ContractId, null, "Declared enumeration is shorter than its captured count.");
            VerifySnapshot(methods, declared, contract.ContractId);
            Array.Sort(entries, static (left, right) => left.MethodId.CompareTo(right.MethodId));
            for (index = 1; index < count; index++)
                if (entries[index - 1].MethodId == entries[index].MethodId)
                    throw Invalid(contract.ContractId, entries[index].MethodId, "Declared method IDs must be unique.");

            cancellationToken.ThrowIfCancellationRequested();
            var interfaceHash = stub.InterfaceHash;
            cancellationToken.ThrowIfCancellationRequested();
            if (interfaceHash != contract.ContractId)
                throw Invalid(contract.ContractId, null, "Stub contract ID does not match its declared contract.");
            VerifySnapshot(methods, declared, contract.ContractId);

            for (index = 0; index < count; index++)
            {
                var descriptor = declared[index];
                currentMethod = descriptor.MethodId;
                cancellationToken.ThrowIfCancellationRequested();
                var facts = stub.ResolveMethodFacts(descriptor.MethodId);
                cancellationToken.ThrowIfCancellationRequested();
                ValidateFacts(contract.ContractId, descriptor, facts);
                // Preserve the exact returned object; no effective defaults or surrogate facts.
                entries[index] = new Entry(descriptor.MethodId, facts);
            }
            currentMethod = null;
            VerifySnapshot(methods, declared, contract.ContractId);
            cancellationToken.ThrowIfCancellationRequested();
            Array.Sort(entries, static (left, right) => left.MethodId.CompareTo(right.MethodId));
            var table = new ServerDeclaredMethodFacts(entries, reservation);
            reservation.Group.CompletePreparation(reservation);
            return table;
        }
        catch (Exception exception) when (exception is not ServerDeclaredMethodFactsException and
            not OutOfMemoryException and not StackOverflowException &&
            !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // Original exception stays transaction-local for rollback aggregation, never in a public result.
            throw new ServerDeclaredMethodFactsException(SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                $"Declared facts preparation failed: {exception.GetType().Name}: {exception.Message}",
                contract.ContractId, currentMethod, exception);
        }
    }

    private static void VerifySnapshot(IReadOnlyList<SharpLinkGeneratedMethodDescriptor> methods,
        SharpLinkGeneratedMethodDescriptor[] captured, long contractId)
    {
        if (methods.Count != captured.Length)
            throw Invalid(contractId, null, "Declared count changed during preparation.");
        var index = 0;
        foreach (var item in methods)
        {
            if (index == captured.Length || !ReferenceEquals(item, captured[index]))
                throw Invalid(contractId, null, "Declared method identity/order changed during preparation.");
            // Also reject custom lists whose indexed and enumerated views disagree.
            if (!ReferenceEquals(methods[index], captured[index]))
                throw Invalid(contractId, null, "Declared indexed and enumerated views disagree.");
            index++;
        }
        if (index != captured.Length || methods.Count != captured.Length)
            throw Invalid(contractId, null, "Declared enumeration changed during preparation.");
    }

    private static void ValidateFacts(long contractId, SharpLinkGeneratedMethodDescriptor descriptor,
        RpcMethodFacts? facts)
    {
        if (facts is null || !facts.IsKnown || facts.ContractId != contractId || facts.MethodId != descriptor.MethodId ||
            facts.Kind != descriptor.Kind || facts.SupportsCancellation != descriptor.SupportsCancellation)
            throw Invalid(contractId, descriptor.MethodId, "Declared method returned unknown or mismatched facts.");
        var kind = facts.Kind;
        var streams = facts.ClientStreamCount;
        var responseStream = kind is RpcMethodKind.ServerStreaming or RpcMethodKind.DuplexStreaming;
        if (kind is < RpcMethodKind.Unary or > RpcMethodKind.DuplexStreaming ||
            (uint)streams > RpcMethodFacts.MaxClientStreamCount ||
            (kind is RpcMethodKind.ClientStreaming or RpcMethodKind.DuplexStreaming) && streams == 0 ||
            (kind is RpcMethodKind.Unary or RpcMethodKind.ServerStreaming) && streams != 0 ||
            (streams != 0 || responseStream) && !facts.SupportsCancellation ||
            kind == RpcMethodKind.OneWay && facts.HasResponsePayload || responseStream && !facts.HasResponsePayload ||
            facts.ResponseNullable && !facts.HasResponsePayload || facts.MethodTimeout.HasValue && !facts.HasMethodTimeout)
            throw Invalid(contractId, descriptor.MethodId, "Declared method facts violate shape invariants.");
    }

    private static ServerDeclaredMethodFactsException Invalid(long contractId, long? methodId, string message)
        => new(SharpLinkAssemblyRegistrationErrorCode.InvalidManifest, message, contractId, methodId);

    // Scalar lookups already in flight remain valid; disposal never mutates the immutable array.
    // Publication/retirement owners detach table roots separately before releasing their charge.
    public void Dispose() => _reservation.Group.ReleaseTable(_reservation);

    private readonly record struct Entry(long MethodId, RpcMethodFacts Facts);
}
