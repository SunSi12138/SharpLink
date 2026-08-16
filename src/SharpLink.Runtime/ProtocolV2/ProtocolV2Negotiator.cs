namespace SharpLink.Runtime;

/// <summary>Validated immutable local policy used by the pure Protocol v2 negotiation mechanism.</summary>
internal readonly struct ProtocolV2NegotiationPolicy
{
    private ProtocolV2NegotiationPolicy(
        ushort minorVersion,
        ProtocolV2Capabilities supportedCapabilities,
        int maxFramePayloadBytes,
        int streamReceiveWindowBytes,
        int connectionReceiveWindowBytes,
        IReadOnlyList<SharpLinkCompressionProviderBinding> compressionProviders)
    {
        MinorVersion = minorVersion;
        SupportedCapabilities = supportedCapabilities;
        MaxFramePayloadBytes = maxFramePayloadBytes;
        StreamReceiveWindowBytes = streamReceiveWindowBytes;
        ConnectionReceiveWindowBytes = connectionReceiveWindowBytes;
        CompressionProviders = compressionProviders;
    }

    internal ushort MinorVersion { get; }

    internal ProtocolV2Capabilities SupportedCapabilities { get; }

    internal int MaxFramePayloadBytes { get; }

    internal int StreamReceiveWindowBytes { get; }

    internal int ConnectionReceiveWindowBytes { get; }

    internal IReadOnlyList<SharpLinkCompressionProviderBinding> CompressionProviders { get; }

    internal static ProtocolV2NegotiationPolicy Create(
        ushort minorVersion,
        ProtocolV2Capabilities supportedCapabilities,
        int maxFramePayloadBytes,
        int streamReceiveWindowBytes,
        int connectionReceiveWindowBytes,
        IReadOnlyList<SharpLinkCompressionProviderBinding> compressionProviders)
    {
        ArgumentNullException.ThrowIfNull(compressionProviders);
        if (minorVersion > ProtocolV2Constants.MinorVersion)
            throw new ArgumentOutOfRangeException(nameof(minorVersion));
        if ((supportedCapabilities & ~RpcSessionProtocolRules.KnownCapabilities) != 0)
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
            compressionProviders);
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

/// <summary>Pure Protocol v2 offer, intersection, and response-validation rules.</summary>
internal static class ProtocolV2Negotiator
{
    private const ProtocolV2Capabilities AlwaysImplementedCapabilities =
        RpcSessionProtocolRules.KnownCapabilities & ~ProtocolV2Capabilities.Compression;

    internal static ProtocolV2NegotiationPolicy CreateImplementedPolicy(
        int maxFramePayloadBytes,
        int streamReceiveWindowBytes,
        int connectionReceiveWindowBytes,
        IReadOnlyList<SharpLinkCompressionProviderBinding> compressionProviders)
    {
        ArgumentNullException.ThrowIfNull(compressionProviders);
        var capabilities = AlwaysImplementedCapabilities;
        if (compressionProviders.Count != 0)
            capabilities |= ProtocolV2Capabilities.Compression;
        return ProtocolV2NegotiationPolicy.Create(
            ProtocolV2Constants.MinorVersion,
            capabilities,
            maxFramePayloadBytes,
            streamReceiveWindowBytes,
            connectionReceiveWindowBytes,
            compressionProviders);
    }

    internal static ProtocolV2HandshakeRequest CreateClientOffer(
        in ProtocolV2NegotiationPolicy policy,
        ProtocolV2Capabilities requiredCapabilities,
        ReadOnlyMemory<byte> authenticationPayload)
    {
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
            compressionProfiles);
    }

    internal static ProtocolV2ServerNegotiation NegotiateServer(
        in ProtocolV2HandshakeRequest offer,
        in ProtocolV2NegotiationPolicy serverPolicy)
    {
        ValidatePeerOffer(offer);

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

        var minorVersion = Math.Min(offer.MinorVersion, serverPolicy.MinorVersion);
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
            compressionBinding?.WireProfile);
        var options = new NegotiatedSessionOptions(
            minorVersion,
            negotiatedCapabilities,
            maxFramePayloadBytes,
            streamReceiveWindowBytes,
            connectionReceiveWindowBytes,
            compressionBinding);
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

        if (response.MinorVersion > offer.MinorVersion)
        {
            throw Failure(
                SharpLinkErrorCode.Unimplemented,
                $"Server requires unsupported protocol minor version {response.MinorVersion}.");
        }
        if ((response.NegotiatedCapabilities & ~RpcSessionProtocolRules.KnownCapabilities) != 0)
            throw Failure(SharpLinkErrorCode.ProtocolViolation, "Server negotiated unknown capabilities.");
        if ((response.NegotiatedCapabilities & ~offer.SupportedCapabilities) != 0)
            throw Failure(SharpLinkErrorCode.ProtocolViolation, "Server negotiated a capability the client did not offer.");
        if ((offer.RequiredCapabilities & ~response.NegotiatedCapabilities) != 0)
            throw Failure(SharpLinkErrorCode.ProtocolViolation, "Server omitted a required client capability.");
        if (response.MaxFramePayloadBytes > offer.MaxFramePayloadBytes ||
            response.StreamReceiveWindowBytes > offer.StreamReceiveWindowBytes ||
            response.ConnectionReceiveWindowBytes > offer.ConnectionReceiveWindowBytes)
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
                    $"Server selected compression profile '{selectedProfile}' that the client did not offer.");
            }
            compressionBinding = FindBinding(clientPolicy.CompressionProviders, selectedProfile);
            if (compressionBinding is null)
            {
                throw Failure(
                    SharpLinkErrorCode.ProtocolViolation,
                    $"Server selected compression profile '{selectedProfile}' that the client cannot bind.");
            }
        }

        return new NegotiatedSessionOptions(
            response.MinorVersion,
            response.NegotiatedCapabilities,
            response.MaxFramePayloadBytes,
            response.StreamReceiveWindowBytes,
            response.ConnectionReceiveWindowBytes,
            compressionBinding);
    }

    private static void ValidatePeerOffer(in ProtocolV2HandshakeRequest offer)
    {
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
                        $"Compression wire profile '{profile}' was offered more than once.");
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
                $"{messageType} frame limit is outside the Protocol v2 range.");
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
