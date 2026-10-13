namespace SharpLink.Server;

/// <summary>
/// Validates wire identities before any business codec or activator runs. The returned module
/// lease belongs to the complete request dispatch, including queued admission and stream teardown.
/// </summary>
internal static class ServerContractAdmission
{
    internal static bool TryAcquire(
        ServerServiceModuleRegistry registry,
        in ProtocolV2RequestIdentity identity,
        ProtocolV2FrameFlags flags,
        out ServiceRegistration registration,
        out SharpLinkDynamicModuleLease lease,
        out SharpLinkException? rejection)
    {
        lease = default;
        // Static registrations cannot be replaced or unregistered while the server runs. Their
        // immutable reference and receipt therefore need no shared registry lock on the hot path.
        if (Volatile.Read(ref registry.ServicesStorage).TryGetValue(identity.ContractId, out var current) &&
            !current.HasModule)
        {
            registration = current;
            return Validate(current, in identity, flags, out rejection);
        }

        lock (registry.Gate)
        {
            if (!registry.ServicesStorage.TryGetValue(identity.ContractId, out current))
            {
                registration = null!;
                rejection = new SharpLinkException(
                    SharpLinkErrorCode.Unimplemented,
                    SharpLinkErrorDetails.Unimplemented.ContractNotRegistered,
                    "The requested RPC contract is not registered.");
                return false;
            }

            registration = current;
            if (!Validate(current, in identity, flags, out rejection))
                return false;

            var isStream = identity.Kind is RpcMethodKind.ClientStreaming or
                RpcMethodKind.ServerStreaming or RpcMethodKind.DuplexStreaming ||
                identity.ClientStreamCount != 0;
            if (current.Module is null || current.Module.TryAcquire(isStream, out lease))
                return true;

            rejection = new SharpLinkException(
                SharpLinkErrorCode.Unavailable,
                SharpLinkErrorDetails.Unavailable.RegistrationRetired,
                "The selected RPC registration stopped accepting calls.");
            return false;
        }
    }

    private static bool Validate(
        ServiceRegistration registration,
        in ProtocolV2RequestIdentity identity,
        ProtocolV2FrameFlags flags,
        out SharpLinkException? rejection)
    {
        rejection = null;
        if (registration.RpcAssemblyHash != identity.RpcAssemblyHash)
        {
            rejection = new SharpLinkException(
                SharpLinkErrorCode.FailedPrecondition,
                SharpLinkErrorDetails.FailedPrecondition.ContractMismatch,
                "The requested RPC assembly identity does not match the current registration.");
            return false;
        }
        if (identity.Receipt is { } required && !MatchesReceipt(registration.Receipt, required, identity.Requirement))
        {
            rejection = new SharpLinkException(
                SharpLinkErrorCode.FailedPrecondition,
                SharpLinkErrorDetails.FailedPrecondition.ReceiptMismatch,
                "The current RPC registration does not satisfy the supplied receipt.");
            return false;
        }
        if (!registration.Stub.TryGetMethodDescriptor(identity.MethodId, out var descriptor))
        {
            rejection = new SharpLinkException(
                SharpLinkErrorCode.Unimplemented,
                SharpLinkErrorDetails.Unimplemented.MethodNotImplemented,
                "The requested RPC method is not implemented.");
            return false;
        }
        if (descriptor.Kind != identity.Kind || descriptor.ClientStreamCount != identity.ClientStreamCount ||
            ((flags & ProtocolV2FrameFlags.OneWay) != 0) != (descriptor.Kind == RpcMethodKind.OneWay) ||
            ((flags & ProtocolV2FrameFlags.HasReturn) != 0) != descriptor.HasResponsePayload)
        {
            throw new SharpLinkProtocolViolationException(
                ProtocolViolationReason.MalformedFrame,
                "Request invocation shape does not match the selected contract method.");
        }
        return true;
    }

    private static bool MatchesReceipt(
        ContractRegistrationReceipt current,
        ContractRegistrationReceipt required,
        RpcRegistrationRequirement requirement)
        => current.ServerInstanceId == required.ServerInstanceId &&
           current.RegistryEpoch == required.RegistryEpoch &&
           current.ContractId == required.ContractId &&
           current.RpcAssemblyHash == required.RpcAssemblyHash &&
           (requirement == RpcRegistrationRequirement.Compatible || current.Generation >= required.Generation) &&
           (requirement != RpcRegistrationRequirement.ExactRegistration ||
            current.RegistrationId == required.RegistrationId);
}
