namespace SharpLink.Runtime;

// Scalar wire values only. These types never own services, codecs, or local registration handles.
internal enum Protocol3BindingRequirement : byte
{
    Compatible = 0,
    MinimumVersion = 1,
    ExactRegistration = 2
}

internal enum Protocol3BindStatus : ushort
{
    Success = 0,
    UnknownContract = 1,
    HashMismatch = 2,
    ReceiptScopeMismatch = 3,
    BelowMinimum = 4,
    RegistrationMismatch = 5,
    RegistrationRetiring = 6,
    BindingCapacity = 7,
    ControlCapacity = 8,
    ControlDeadline = 9,
    ScopeDenied = 10,
    CancelledBeforeCommit = 11,
    SessionDraining = 12
}

internal enum Protocol3BindingInvalidationReason : ushort
{
    RegistrationRetired = 1,
    Evicted = 2,
    SessionDraining = 3
}

internal readonly record struct Protocol3BindingReceipt(
    Guid ServerInstanceId,
    Guid RegistryEpoch,
    ulong Generation,
    ulong RegistrationId);

internal readonly record struct Protocol3BindRequest(
    Protocol3BindingRequirement Requirement,
    long ContractId,
    RpcHash128 Hash,
    Protocol3BindingReceipt? Receipt = null);

internal readonly record struct Protocol3BindResult(
    Protocol3BindStatus Status,
    ulong Token,
    long ContractId,
    RpcHash128 Hash,
    ulong RegistrationId,
    ulong Generation,
    Guid ServerInstanceId,
    Guid RegistryEpoch)
{
    internal static Protocol3BindResult Failure(Protocol3BindStatus status)
    {
        if (status <= Protocol3BindStatus.Success || status > Protocol3BindStatus.SessionDraining)
            throw new ArgumentOutOfRangeException(nameof(status));
        return new Protocol3BindResult(status, 0, 0, default, 0, 0, default, default);
    }
}

internal readonly record struct Protocol3BindingInvalidated(
    ulong Token,
    Protocol3BindingInvalidationReason Reason);

internal static class Protocol3BindingToken
{
    internal const ulong MaximumIncarnation = (1UL << 48) - 1;

    internal static int Slot(ulong token) => (ushort)token;
    internal static ulong Incarnation(ulong token) => token >> 16;

    internal static bool IsValid(ulong token, uint negotiatedSlots)
        => negotiatedSlots is >= 1 and <= 65536 &&
           Incarnation(token) != 0 && (uint)Slot(token) < negotiatedSlots;

    internal static ulong Create(ushort slot, ulong incarnation, uint negotiatedSlots)
    {
        if (incarnation is 0 or > MaximumIncarnation)
            throw new ArgumentOutOfRangeException(nameof(incarnation));
        var token = (incarnation << 16) | slot;
        if (!IsValid(token, negotiatedSlots))
            throw new ArgumentOutOfRangeException(nameof(slot));
        return token;
    }
}
