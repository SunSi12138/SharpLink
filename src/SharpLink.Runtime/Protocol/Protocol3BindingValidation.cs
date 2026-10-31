namespace SharpLink.Runtime;

internal static class Protocol3BindingValidation
{
    internal static bool IsPositive63(ulong value) => value is >= 1 and <= long.MaxValue;

    internal static bool IsValid(in Protocol3BindRequest request)
    {
        if (request.Requirement > Protocol3BindingRequirement.ExactRegistration ||
            request.ContractId == 0 || request.Hash.IsEmpty)
            return false;
        if (request.Receipt is not { } receipt)
            return request.Requirement == Protocol3BindingRequirement.Compatible;
        return receipt.ServerInstanceId != Guid.Empty && receipt.RegistryEpoch != Guid.Empty &&
               IsPositive63(receipt.Generation) && IsPositive63(receipt.RegistrationId);
    }

    internal static bool IsValid(in Protocol3BindResult result, uint negotiatedSlots)
    {
        if (result.Status > Protocol3BindStatus.SessionDraining)
            return false;
        if (result.Status != Protocol3BindStatus.Success)
            return result.Token == 0 && result.ContractId == 0 && result.Hash.IsEmpty &&
                   result.RegistrationId == 0 && result.Generation == 0 &&
                   result.ServerInstanceId == Guid.Empty && result.RegistryEpoch == Guid.Empty;
        return Protocol3BindingToken.IsValid(result.Token, negotiatedSlots) && result.ContractId != 0 &&
               !result.Hash.IsEmpty && IsPositive63(result.RegistrationId) && IsPositive63(result.Generation) &&
               result.ServerInstanceId != Guid.Empty && result.RegistryEpoch != Guid.Empty;
    }

    // Call only after operation-ID correlation. This does not grant route publication:
    // the same physical Session's watermark/publication gate must still accept the token.
    internal static void ValidateResultForRequest(
        in Protocol3BindResult result,
        in Protocol3BindRequest request,
        in Protocol3BindingAuthority sessionAuthority,
        uint negotiatedSlots)
    {
        if (!IsValid(request) || sessionAuthority.ServerInstanceId == Guid.Empty || sessionAuthority.RegistryEpoch == Guid.Empty)
            throw new ArgumentException("The captured binding request and Session authority must be valid.");
        if (!IsValid(result, negotiatedSlots))
            throw Malformed("Invalid BindResult.");
        if (result.Status != Protocol3BindStatus.Success)
            return;
        if (result.ContractId != request.ContractId || result.Hash != request.Hash ||
            result.ServerInstanceId != sessionAuthority.ServerInstanceId || result.RegistryEpoch != sessionAuthority.RegistryEpoch)
            throw State("BindResult does not match the captured request or Session authority.");
        if (request.Receipt is not { } receipt)
            return;
        if (receipt.ServerInstanceId != sessionAuthority.ServerInstanceId || receipt.RegistryEpoch != sessionAuthority.RegistryEpoch ||
            request.Requirement != Protocol3BindingRequirement.Compatible && result.Generation < receipt.Generation ||
            request.Requirement == Protocol3BindingRequirement.ExactRegistration && result.RegistrationId != receipt.RegistrationId)
        {
            throw State("BindResult does not satisfy the captured receipt requirement.");
        }
    }

    internal static void ValidateOperationId(ulong operationId)
    {
        if (!IsPositive63(operationId))
            throw Malformed("Binding operation ID is outside the positive 63-bit domain.");
    }

    internal static SharpLinkProtocolViolationException Malformed(string message)
        => new(ProtocolViolationReason.MalformedFrame, message);

    internal static SharpLinkProtocolViolationException State(string message)
        => new(ProtocolViolationReason.ProtocolState, message);
}
