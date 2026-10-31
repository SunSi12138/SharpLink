namespace SharpLink.Runtime;

/// <summary>Validated immutable local policy used by the pure Protocol3 negotiation mechanism.</summary>
internal readonly struct ProtocolV2NegotiationPolicy
{
    private ProtocolV2NegotiationPolicy(
        ushort minorVersion,
        ProtocolV2Capabilities supportedCapabilities,
        int maxFramePayloadBytes,
        int streamReceiveWindowBytes,
        int connectionReceiveWindowBytes,
        IReadOnlyList<SharpLinkCompressionProviderBinding> compressionProviders,
        Protocol3BindingLimits bindingLimits,
        Guid serverInstanceId,
        Guid registryEpoch)
    {
        MinorVersion = minorVersion;
        SupportedCapabilities = supportedCapabilities;
        MaxFramePayloadBytes = maxFramePayloadBytes;
        StreamReceiveWindowBytes = streamReceiveWindowBytes;
        ConnectionReceiveWindowBytes = connectionReceiveWindowBytes;
        CompressionProviders = compressionProviders;
        BindingLimits = bindingLimits;
        ServerInstanceId = serverInstanceId;
        RegistryEpoch = registryEpoch;
    }

    internal ushort MinorVersion { get; }

    internal ProtocolV2Capabilities SupportedCapabilities { get; }

    internal int MaxFramePayloadBytes { get; }

    internal int StreamReceiveWindowBytes { get; }

    internal int ConnectionReceiveWindowBytes { get; }

    internal IReadOnlyList<SharpLinkCompressionProviderBinding> CompressionProviders { get; }

    internal Protocol3BindingLimits BindingLimits { get; }

    internal Guid ServerInstanceId { get; }

    internal Guid RegistryEpoch { get; }

    internal static ProtocolV2NegotiationPolicy Create(
        ushort minorVersion,
        ProtocolV2Capabilities supportedCapabilities,
        int maxFramePayloadBytes,
        int streamReceiveWindowBytes,
        int connectionReceiveWindowBytes,
        IReadOnlyList<SharpLinkCompressionProviderBinding> compressionProviders,
        Protocol3BindingLimits? bindingLimits = null,
        Guid serverInstanceId = default,
        Guid registryEpoch = default)
    {
        ArgumentNullException.ThrowIfNull(compressionProviders);
        if (minorVersion != ProtocolV2Constants.MinorVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(minorVersion));
        }
        var selectedBindingLimits = bindingLimits ?? Protocol3BindingLimits.Default;
        if (!Protocol3BindingLimitsCodec.IsValid(selectedBindingLimits))
            throw new ArgumentOutOfRangeException(nameof(bindingLimits));
        if ((serverInstanceId == Guid.Empty) != (registryEpoch == Guid.Empty))
            throw new ArgumentException("Server authority requires both instance and registry epoch GUIDs.");
        if ((supportedCapabilities & ProtocolV2Negotiator.MandatoryCapabilities) != ProtocolV2Negotiator.MandatoryCapabilities)
            throw new ArgumentException("Protocol3 mandatory capabilities must be implemented.", nameof(supportedCapabilities));
        if ((supportedCapabilities & ~RpcSessionProtocolRules.RecognizedCapabilities) != 0)
            throw new ArgumentOutOfRangeException(nameof(supportedCapabilities));
        if (maxFramePayloadBytes < SharpLinkProtocolOptions.MinMaxFramePayloadBytes ||
            maxFramePayloadBytes > SharpLinkProtocolOptions.MaxMaxFramePayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFramePayloadBytes));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(streamReceiveWindowBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(connectionReceiveWindowBytes);
        if (connectionReceiveWindowBytes < streamReceiveWindowBytes)
        {
            throw new ArgumentException(
                "Connection receive window cannot be smaller than stream receive window.",
                nameof(connectionReceiveWindowBytes));
        }

        var compressionSupported =
            (supportedCapabilities & ProtocolV2Capabilities.Compression) != 0;
        if (compressionSupported != (compressionProviders.Count != 0))
        {
            throw new ArgumentException(
                "Local compression capability and provider bindings must either both be present or both be absent.",
                nameof(compressionProviders));
        }
        if (compressionProviders.Count > SharpLinkCompressionOptions.MaxProviders)
            throw new ArgumentOutOfRangeException(nameof(compressionProviders));
        for (var index = 0; index < compressionProviders.Count; index++)
        {
            var binding = compressionProviders[index];
            ArgumentNullException.ThrowIfNull(binding.Provider);
            SharpLinkCompressionProfile.Validate(binding.WireProfile, nameof(compressionProviders));
            if (!string.Equals(binding.WireProfile, binding.Provider.WireProfile, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "A compression provider binding must match its provider wire profile.",
                    nameof(compressionProviders));
            }
            for (var previous = 0; previous < index; previous++)
            {
                if (string.Equals(
                        compressionProviders[previous].WireProfile,
                        binding.WireProfile,
                        StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Compression wire profile '{binding.WireProfile}' is registered more than once.",
                        nameof(compressionProviders));
                }
            }
        }

        return new ProtocolV2NegotiationPolicy(
            minorVersion,
            supportedCapabilities,
            maxFramePayloadBytes,
            streamReceiveWindowBytes,
            connectionReceiveWindowBytes,
            compressionProviders,
            selectedBindingLimits,
            serverInstanceId,
            registryEpoch);
    }
}

/// <summary>One server response and the immutable local binding it publishes after authentication.</summary>
internal sealed class ProtocolV2ServerNegotiation
{
    internal ProtocolV2ServerNegotiation(
        ProtocolV2HandshakeResponse response,
        NegotiatedSessionOptions options)
    {
        Response = response;
        Options = options;
    }

    internal ProtocolV2HandshakeResponse Response { get; }

    internal NegotiatedSessionOptions Options { get; }
}

/// <summary>Pure Protocol3 offer, intersection, and response-validation rules.</summary>
internal static class ProtocolV2Negotiator
{
    internal const ProtocolV2Capabilities MandatoryCapabilities =
        ProtocolV2Capabilities.FlowControl |
        ProtocolV2Capabilities.CancellationReason |
        ProtocolV2Capabilities.ContractManifest;

    internal const ProtocolV2Capabilities AlwaysImplementedCapabilities =
        ProtocolV2Capabilities.Metadata |
        ProtocolV2Capabilities.FlowControl |
        ProtocolV2Capabilities.HealthCheck |
        ProtocolV2Capabilities.CancellationReason |
        ProtocolV2Capabilities.ContractManifest;

    internal static ProtocolV2HandshakeRequest CreateClientOffer(
        in ProtocolV2NegotiationPolicy policy,
        ProtocolV2Capabilities requiredCapabilities,
        ReadOnlyMemory<byte> authenticationPayload,
        ulong responseCompressionPreferenceGeneration = 0,
        bool allowResponseCompression = true)
    {
        if (responseCompressionPreferenceGeneration > long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(responseCompressionPreferenceGeneration));
        requiredCapabilities |= MandatoryCapabilities;
        if ((requiredCapabilities & ~policy.SupportedCapabilities) != 0)
        {
            throw new ArgumentException(
                "Required capabilities must be a subset of the local supported capabilities.",
                nameof(requiredCapabilities));
        }

        ReadOnlyMemory<string> compressionProfiles = ReadOnlyMemory<string>.Empty;
        if ((policy.SupportedCapabilities & ProtocolV2Capabilities.Compression) != 0)
        {
            var profiles = new string[policy.CompressionProviders.Count];
            for (var index = 0; index < profiles.Length; index++)
                profiles[index] = policy.CompressionProviders[index].WireProfile;
            compressionProfiles = profiles;
        }

        return new ProtocolV2HandshakeRequest(
            policy.MinorVersion,
            policy.SupportedCapabilities,
            requiredCapabilities,
            policy.MaxFramePayloadBytes,
            policy.StreamReceiveWindowBytes,
            policy.ConnectionReceiveWindowBytes,
            authenticationPayload,
            compressionProfiles,
            responseCompressionPreferenceGeneration,
            allowResponseCompression,
            policy.BindingLimits);
    }

    internal static ProtocolV2ServerNegotiation NegotiateServer(
        in ProtocolV2HandshakeRequest offer,
        in ProtocolV2NegotiationPolicy serverPolicy)
    {
        ValidatePeerOffer(offer);
        if (serverPolicy.ServerInstanceId == Guid.Empty || serverPolicy.RegistryEpoch == Guid.Empty)
            throw new InvalidOperationException("Server negotiation requires its stable registry authority.");
        var bindingLimits = Protocol3BindingLimitsCodec.Negotiate(offer.BindingLimits, serverPolicy.BindingLimits);
        var authority = new Protocol3BindingAuthority(serverPolicy.ServerInstanceId, serverPolicy.RegistryEpoch, bindingLimits);

        var unsupportedRequired = offer.RequiredCapabilities & ~serverPolicy.SupportedCapabilities;
        if (unsupportedRequired != ProtocolV2Capabilities.None)
        {
            throw Failure(
                SharpLinkErrorCode.Unimplemented,
                $"Required capabilities are unsupported: {unsupportedRequired}.");
        }

        var negotiatedCapabilities = offer.SupportedCapabilities & serverPolicy.SupportedCapabilities;
        SharpLinkCompressionProviderBinding? compressionBinding = null;
        if ((negotiatedCapabilities & ProtocolV2Capabilities.Compression) != 0)
        {
            compressionBinding = SelectServerCompressionBinding(
                offer.CompressionProfiles.Span,
                serverPolicy.CompressionProviders);
            if (compressionBinding is null)
                negotiatedCapabilities &= ~ProtocolV2Capabilities.Compression;
        }

        var missingRequired = offer.RequiredCapabilities & ~negotiatedCapabilities;
        if (missingRequired != ProtocolV2Capabilities.None)
        {
            var message = (missingRequired & ProtocolV2Capabilities.Compression) != 0
                ? "Required compression has no mutually supported profile."
                : $"Required capabilities are unsupported: {missingRequired}.";
            throw Failure(SharpLinkErrorCode.Unimplemented, message);
        }

        var minorVersion = ProtocolV2Constants.MinorVersion;
        var maxFramePayloadBytes = Math.Min(
            offer.MaxFramePayloadBytes,
            serverPolicy.MaxFramePayloadBytes);
        var streamReceiveWindowBytes = Math.Min(
            offer.StreamReceiveWindowBytes,
            serverPolicy.StreamReceiveWindowBytes);
        var connectionReceiveWindowBytes = Math.Min(
            offer.ConnectionReceiveWindowBytes,
            serverPolicy.ConnectionReceiveWindowBytes);
        var response = new ProtocolV2HandshakeResponse(
            minorVersion,
            negotiatedCapabilities,
            maxFramePayloadBytes,
            streamReceiveWindowBytes,
            connectionReceiveWindowBytes,
            compressionBinding?.WireProfile,
            authority);
        var options = new NegotiatedSessionOptions(
            minorVersion,
            negotiatedCapabilities,
            maxFramePayloadBytes,
            streamReceiveWindowBytes,
            connectionReceiveWindowBytes,
            compressionBinding,
            authority);
        return new ProtocolV2ServerNegotiation(response, options);
    }

    internal static NegotiatedSessionOptions ValidateServerResponse(
        in ProtocolV2HandshakeRequest offer,
        in ProtocolV2HandshakeResponse response,
        in ProtocolV2NegotiationPolicy clientPolicy)
    {
        ValidatePeerOffer(offer);
        ValidatePeerLimits(
            response.MaxFramePayloadBytes,
            response.StreamReceiveWindowBytes,
            response.ConnectionReceiveWindowBytes,
            "HandshakeResponse");

        if (response.MinorVersion != ProtocolV2Constants.MinorVersion)
            throw Failure(SharpLinkErrorCode.Unimplemented, "Server selected an unsupported Protocol3 revision.");
        if (response.Authority.ServerInstanceId == Guid.Empty || response.Authority.RegistryEpoch == Guid.Empty)
            throw Failure(SharpLinkErrorCode.ProtocolViolation, "Server handshake authority is empty.");
        Protocol3BindingLimitsCodec.ValidateNegotiated(response.Authority.Limits, offer.BindingLimits, clientPolicy.BindingLimits);
        if ((response.NegotiatedCapabilities & MandatoryCapabilities) != MandatoryCapabilities)
            throw Failure(SharpLinkErrorCode.ProtocolViolation, "Server omitted Protocol3 mandatory capabilities.");
        if ((response.NegotiatedCapabilities & ~RpcSessionProtocolRules.RecognizedCapabilities) != 0)
            throw Failure(SharpLinkErrorCode.ProtocolViolation, "Server negotiated unknown capabilities.");
        if ((response.NegotiatedCapabilities & ~offer.SupportedCapabilities) != 0)
            throw Failure(SharpLinkErrorCode.ProtocolViolation, "Server negotiated a capability the client did not offer.");
        if ((offer.RequiredCapabilities & ~response.NegotiatedCapabilities) != 0)
            throw Failure(SharpLinkErrorCode.ProtocolViolation, "Server omitted a required client capability.");
        if (response.MaxFramePayloadBytes > offer.MaxFramePayloadBytes ||
            response.StreamReceiveWindowBytes > offer.StreamReceiveWindowBytes ||
            response.ConnectionReceiveWindowBytes > offer.ConnectionReceiveWindowBytes ||
            response.MaxFramePayloadBytes > clientPolicy.MaxFramePayloadBytes ||
            response.StreamReceiveWindowBytes > clientPolicy.StreamReceiveWindowBytes ||
            response.ConnectionReceiveWindowBytes > clientPolicy.ConnectionReceiveWindowBytes)
        {
            throw Failure(
                SharpLinkErrorCode.ProtocolViolation,
                "Server negotiated receive limits above the client offer.");
        }

        var compressionNegotiated =
            (response.NegotiatedCapabilities & ProtocolV2Capabilities.Compression) != 0;
        if (compressionNegotiated != (response.CompressionProfile is not null))
        {
            throw Failure(
                SharpLinkErrorCode.ProtocolViolation,
                "Negotiated compression and its selected profile must be published together.");
        }

        SharpLinkCompressionProviderBinding? compressionBinding = null;
        if (response.CompressionProfile is { } selectedProfile)
        {
            if (!ContainsProfile(offer.CompressionProfiles.Span, selectedProfile))
            {
                throw Failure(
                    SharpLinkErrorCode.ProtocolViolation,
                    "Server selected a compression profile that the client did not offer.");
            }
            compressionBinding = FindBinding(clientPolicy.CompressionProviders, selectedProfile);
            if (compressionBinding is null)
            {
                throw Failure(
                    SharpLinkErrorCode.ProtocolViolation,
                    "Server selected a compression profile that the client cannot bind.");
            }
        }

        return new NegotiatedSessionOptions(
            response.MinorVersion,
            response.NegotiatedCapabilities,
            response.MaxFramePayloadBytes,
            response.StreamReceiveWindowBytes,
            response.ConnectionReceiveWindowBytes,
            compressionBinding,
            response.Authority);
    }

    private static void ValidatePeerOffer(in ProtocolV2HandshakeRequest offer)
    {
        if (offer.MinorVersion != ProtocolV2Constants.MinorVersion)
            throw Failure(SharpLinkErrorCode.Unimplemented, "Peer offered an unsupported Protocol3 revision.");
        if (offer.ResponseCompressionPreferenceGeneration > long.MaxValue)
            throw Failure(SharpLinkErrorCode.ProtocolViolation, "Peer preference generation exceeds the 63-bit domain.");
        if (!Protocol3BindingLimitsCodec.IsValid(offer.BindingLimits))
            throw Failure(SharpLinkErrorCode.ProtocolViolation, "Peer binding limits are invalid.");
        if ((offer.RequiredCapabilities & ~RpcSessionProtocolRules.RecognizedCapabilities) != 0 ||
            (offer.RequiredCapabilities & MandatoryCapabilities) != MandatoryCapabilities)
            throw Failure(SharpLinkErrorCode.ProtocolViolation, "Peer required capabilities are unknown or omit Protocol3 mandatory capabilities.");
        ValidatePeerLimits(
            offer.MaxFramePayloadBytes,
            offer.StreamReceiveWindowBytes,
            offer.ConnectionReceiveWindowBytes,
            "HandshakeRequest");
        if ((offer.RequiredCapabilities & ~offer.SupportedCapabilities) != 0)
        {
            throw Failure(
                SharpLinkErrorCode.ProtocolViolation,
                "Required handshake capabilities were not included in the supported capability set.");
        }

        ValidatePeerCompressionProfiles(offer.CompressionProfiles.Span);
        var compressionSupported =
            (offer.SupportedCapabilities & ProtocolV2Capabilities.Compression) != 0;
        if (compressionSupported != !offer.CompressionProfiles.IsEmpty)
        {
            throw Failure(
                SharpLinkErrorCode.ProtocolViolation,
                "Compression capability and offered profiles must either both be present or both be absent.");
        }
    }

    private static void ValidatePeerCompressionProfiles(ReadOnlySpan<string> profiles)
    {
        if (profiles.Length > SharpLinkCompressionOptions.MaxProviders)
            throw Failure(SharpLinkErrorCode.ProtocolViolation, "Too many compression profiles were offered.");
        for (var index = 0; index < profiles.Length; index++)
        {
            var profile = profiles[index];
            try
            {
                SharpLinkCompressionProfile.Validate(profile, nameof(profiles));
            }
            catch (ArgumentException exception)
            {
                throw Failure(
                    SharpLinkErrorCode.ProtocolViolation,
                    "A compression profile is malformed.",
                    exception);
            }
            for (var previous = 0; previous < index; previous++)
            {
                if (string.Equals(profiles[previous], profile, StringComparison.Ordinal))
                {
                    throw Failure(
                        SharpLinkErrorCode.ProtocolViolation,
                        "A compression wire profile was offered more than once.");
                }
            }
        }
    }

    private static SharpLinkCompressionProviderBinding? SelectServerCompressionBinding(
        ReadOnlySpan<string> offeredProfiles,
        IReadOnlyList<SharpLinkCompressionProviderBinding> serverProviders)
    {
        for (var providerIndex = 0; providerIndex < serverProviders.Count; providerIndex++)
        {
            var binding = serverProviders[providerIndex];
            if (ContainsProfile(offeredProfiles, binding.WireProfile))
                return binding;
        }
        return null;
    }

    private static SharpLinkCompressionProviderBinding? FindBinding(
        IReadOnlyList<SharpLinkCompressionProviderBinding> bindings,
        string wireProfile)
    {
        for (var index = 0; index < bindings.Count; index++)
        {
            if (string.Equals(bindings[index].WireProfile, wireProfile, StringComparison.Ordinal))
                return bindings[index];
        }
        return null;
    }

    private static bool ContainsProfile(ReadOnlySpan<string> profiles, string wireProfile)
    {
        foreach (var profile in profiles)
        {
            if (string.Equals(profile, wireProfile, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static void ValidatePeerLimits(
        int maxFramePayloadBytes,
        int streamReceiveWindowBytes,
        int connectionReceiveWindowBytes,
        string messageType)
    {
        if (maxFramePayloadBytes < SharpLinkProtocolOptions.MinMaxFramePayloadBytes ||
            maxFramePayloadBytes > SharpLinkProtocolOptions.MaxMaxFramePayloadBytes)
        {
            throw Failure(
                SharpLinkErrorCode.ProtocolViolation,
                $"{messageType} frame limit is outside the Protocol3 range.");
        }
        if (streamReceiveWindowBytes <= 0 ||
            connectionReceiveWindowBytes <= 0 ||
            connectionReceiveWindowBytes < streamReceiveWindowBytes)
        {
            throw Failure(
                SharpLinkErrorCode.ProtocolViolation,
                $"{messageType} receive windows are invalid.");
        }
    }

    private static SharpLinkException Failure(
        SharpLinkErrorCode code,
        string message,
        Exception? innerException = null)
        => code == SharpLinkErrorCode.ProtocolViolation
            ? new SharpLinkProtocolViolationException(
                ProtocolViolationReason.MalformedFrame,
                message,
                innerException)
            : new SharpLinkException(code, message, innerException);
}
