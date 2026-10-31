namespace SharpLink.Runtime;

// Payload-only codec. The frame parser owns type/flags/direction/phase/header correlation.
// No callback, service descriptor, or business codec is reachable from this scalar boundary.
internal static class Protocol3BindingControlCodec
{
    internal const int BindContractBytes = 28;
    internal const int BindContractWithReceiptBytes = 76;
    internal const int BindResultBytes = 84;
    internal const int BindingInvalidatedBytes = 12;
    internal const int ReleaseBindingBytes = 8;
    internal const int MaximumFrameBytes = 15 + BindResultBytes;

    internal static void WriteBindContract(IBufferWriter<byte> writer, in Protocol3BindRequest request)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (!Protocol3BindingValidation.IsValid(request))
            throw new ArgumentException("Invalid binding request.", nameof(request));
        var length = request.Receipt.HasValue ? BindContractWithReceiptBytes : BindContractBytes;
        var bytes = writer.GetSpan(length)[..length];
        bytes[0] = (byte)request.Requirement;
        bytes[1] = request.Receipt.HasValue ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[2..], 0);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[4..], request.ContractId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[12..], request.Hash.High);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[20..], request.Hash.Low);
        if (request.Receipt is { } receipt)
        {
            receipt.ServerInstanceId.TryWriteBytes(bytes[28..44]);
            receipt.RegistryEpoch.TryWriteBytes(bytes[44..60]);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[60..], receipt.Generation);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[68..], receipt.RegistrationId);
        }
        writer.Advance(length);
    }

    internal static Protocol3BindRequest ReadBindContract(ReadOnlySequence<byte> payload)
    {
        if (payload.Length != BindContractBytes && payload.Length != BindContractWithReceiptBytes)
            throw Protocol3BindingValidation.Malformed("BindContract has an invalid payload length.");
        Span<byte> storage = stackalloc byte[BindContractWithReceiptBytes];
        var bytes = storage[..(int)payload.Length];
        payload.CopyTo(bytes);
        if (bytes[1] > 1 || BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]) != 0 ||
            bytes.Length != (bytes[1] == 1 ? BindContractWithReceiptBytes : BindContractBytes))
            throw Protocol3BindingValidation.Malformed("BindContract has invalid receipt or reserved fields.");
        Protocol3BindingReceipt? receipt = bytes[1] == 0 ? null : new Protocol3BindingReceipt(
            new Guid(bytes[28..44]), new Guid(bytes[44..60]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[60..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[68..]));
        var request = new Protocol3BindRequest((Protocol3BindingRequirement)bytes[0],
            BinaryPrimitives.ReadInt64LittleEndian(bytes[4..]),
            new RpcHash128(BinaryPrimitives.ReadUInt64LittleEndian(bytes[12..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[20..])), receipt);
        if (!Protocol3BindingValidation.IsValid(request))
            throw Protocol3BindingValidation.Malformed("BindContract contains an invalid identity or requirement.");
        return request;
    }

    internal static void WriteBindResult(IBufferWriter<byte> writer, in Protocol3BindResult result, uint negotiatedSlots)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (!Protocol3BindingValidation.IsValid(result, negotiatedSlots))
            throw new ArgumentException("Invalid binding result.", nameof(result));
        var bytes = writer.GetSpan(BindResultBytes)[..BindResultBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)result.Status);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[2..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[4..], result.Token);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[12..], result.ContractId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[20..], result.Hash.High);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[28..], result.Hash.Low);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[36..], result.RegistrationId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[44..], result.Generation);
        result.ServerInstanceId.TryWriteBytes(bytes[52..68]);
        result.RegistryEpoch.TryWriteBytes(bytes[68..84]);
        writer.Advance(BindResultBytes);
    }

    internal static Protocol3BindResult ReadBindResult(ReadOnlySequence<byte> payload, uint negotiatedSlots)
    {
        Span<byte> bytes = stackalloc byte[BindResultBytes];
        CopyExact(payload, bytes);
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]) != 0)
            throw Protocol3BindingValidation.Malformed("BindResult reserved field is nonzero.");
        var result = new Protocol3BindResult(
            (Protocol3BindStatus)BinaryPrimitives.ReadUInt16LittleEndian(bytes),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[4..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[12..]),
            new RpcHash128(BinaryPrimitives.ReadUInt64LittleEndian(bytes[20..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[28..])),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[36..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[44..]),
            new Guid(bytes[52..68]), new Guid(bytes[68..84]));
        if (!Protocol3BindingValidation.IsValid(result, negotiatedSlots))
            throw Protocol3BindingValidation.Malformed("BindResult contains invalid status or identity fields.");
        return result;
    }

    internal static void WriteBindingInvalidated(
        IBufferWriter<byte> writer, in Protocol3BindingInvalidated invalidated, uint negotiatedSlots)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (!Protocol3BindingToken.IsValid(invalidated.Token, negotiatedSlots) || !IsValid(invalidated.Reason))
            throw new ArgumentException("Invalid binding invalidation.", nameof(invalidated));
        var bytes = writer.GetSpan(BindingInvalidatedBytes)[..BindingInvalidatedBytes];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, invalidated.Token);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[8..], (ushort)invalidated.Reason);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[10..], 0);
        writer.Advance(BindingInvalidatedBytes);
    }

    internal static Protocol3BindingInvalidated ReadBindingInvalidated(ReadOnlySequence<byte> payload, uint negotiatedSlots)
    {
        Span<byte> bytes = stackalloc byte[BindingInvalidatedBytes];
        CopyExact(payload, bytes);
        var value = new Protocol3BindingInvalidated(BinaryPrimitives.ReadUInt64LittleEndian(bytes),
            (Protocol3BindingInvalidationReason)BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]));
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]) != 0 ||
            !Protocol3BindingToken.IsValid(value.Token, negotiatedSlots) || !IsValid(value.Reason))
            throw Protocol3BindingValidation.Malformed("BindingInvalidated contains an invalid token, reason, or reserved field.");
        return value;
    }

    internal static void WriteReleaseBinding(IBufferWriter<byte> writer, ulong token, uint negotiatedSlots)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (!Protocol3BindingToken.IsValid(token, negotiatedSlots))
            throw new ArgumentOutOfRangeException(nameof(token));
        BinaryPrimitives.WriteUInt64LittleEndian(writer.GetSpan(ReleaseBindingBytes), token);
        writer.Advance(ReleaseBindingBytes);
    }

    internal static ulong ReadReleaseBinding(ReadOnlySequence<byte> payload, uint negotiatedSlots)
    {
        Span<byte> bytes = stackalloc byte[ReleaseBindingBytes];
        CopyExact(payload, bytes);
        var token = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        if (!Protocol3BindingToken.IsValid(token, negotiatedSlots))
            throw Protocol3BindingValidation.Malformed("ReleaseBinding contains an invalid token.");
        return token;
    }

    internal static void WriteCancelBind(IBufferWriter<byte> writer, ulong operationId)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (!Protocol3BindingValidation.IsPositive63(operationId))
            throw new ArgumentOutOfRangeException(nameof(operationId));
        // The complete payload is empty. The caller writes operationId in the frame header.
    }

    internal static void ReadCancelBind(ReadOnlySequence<byte> payload, ulong operationId)
    {
        Protocol3BindingValidation.ValidateOperationId(operationId);
        if (!payload.IsEmpty)
            throw Protocol3BindingValidation.Malformed("CancelBind must have an empty payload.");
    }

    private static bool IsValid(Protocol3BindingInvalidationReason reason)
        => reason >= Protocol3BindingInvalidationReason.RegistrationRetired &&
           reason <= Protocol3BindingInvalidationReason.SessionDraining;

    private static void CopyExact(ReadOnlySequence<byte> payload, Span<byte> destination)
    {
        if (payload.Length != destination.Length)
            throw Protocol3BindingValidation.Malformed("Binding control payload has an invalid fixed length.");
        payload.CopyTo(destination);
    }
}
