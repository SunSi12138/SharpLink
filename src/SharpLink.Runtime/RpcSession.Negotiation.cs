namespace SharpLink.Runtime;

public sealed partial class RpcSession
{
    internal bool TryCompleteHandshake(NegotiatedSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (Volatile.Read(ref _protocolState).Phase != RpcSessionProtocolPhase.Handshaking)
            return false;
        if (Interlocked.CompareExchange(ref _handshakeCompletionStarted, 1, 0) != 0)
            return false;
        if (Volatile.Read(ref _protocolState).Phase != RpcSessionProtocolPhase.Handshaking)
            return false;

        StreamFlowController? flowController;
        try
        {
            flowController = ValidateAndCreateNegotiatedFlowController(options);
        }
        catch (Exception exception)
        {
            var protocolException = exception as SharpLinkException ??
                new SharpLinkException(
                    SharpLinkErrorCode.ProtocolViolation,
                    "Negotiated session initialization failed.",
                    exception);
            Fault(protocolException);
            throw protocolException;
        }

        var current = Volatile.Read(ref _protocolState);
        if (current.Phase != RpcSessionProtocolPhase.Handshaking)
            return false;
        var ready = new RpcSessionProtocolState(
            RpcSessionProtocolPhase.Ready,
            options,
            flowController);
        return ReferenceEquals(
            Interlocked.CompareExchange(ref _protocolState, ready, current),
            current);
    }

    internal void EnsureInboundFrameAllowed(
        ProtocolV2FrameType frameType,
        bool allowRequestWhileDraining = false)
    {
        var phase = Volatile.Read(ref _protocolState).Phase;
        if (Volatile.Read(ref _terminal) is { } terminal)
            throw terminal.Exception;

        if (RpcSessionProtocolRules.IsFrameAllowed(phase, frameType) ||
            (allowRequestWhileDraining &&
             phase == RpcSessionProtocolPhase.Draining &&
             frameType == ProtocolV2FrameType.Request))
        {
            return;
        }

        throw new SharpLinkException(
            SharpLinkErrorCode.ProtocolViolation,
            $"Frame {frameType} is not allowed while the session is {phase}.");
    }

    private StreamFlowController? ValidateAndCreateNegotiatedFlowController(
        NegotiatedSessionOptions options)
    {
        if (options.ProtocolMinorVersion > ProtocolV2Constants.MinorVersion)
        {
            throw NegotiationViolation(
                $"Negotiated protocol minor version {options.ProtocolMinorVersion} exceeds the local " +
                $"version {ProtocolV2Constants.MinorVersion}.");
        }
        if ((options.Capabilities & ~RpcSessionProtocolRules.KnownCapabilities) != 0)
            throw NegotiationViolation("Negotiated capabilities contain unknown bits.");
        if (options.MaxFramePayloadBytes < SharpLinkProtocolOptions.MinMaxFramePayloadBytes ||
            options.MaxFramePayloadBytes > RuntimeContext.Protocol.MaxFramePayloadBytes)
        {
            throw NegotiationViolation(
                $"Negotiated frame limit {options.MaxFramePayloadBytes} is outside the local protocol limits.");
        }
        if (options.StreamReceiveWindowBytes <= 0 || options.ConnectionReceiveWindowBytes <= 0)
            throw NegotiationViolation("Negotiated receive windows must be positive.");
        if (options.ConnectionReceiveWindowBytes < options.StreamReceiveWindowBytes)
            throw NegotiationViolation("The negotiated connection window cannot be smaller than the stream window.");
        if (options.StreamReceiveWindowBytes > RuntimeContext.FlowControl.StreamReceiveWindowBytes ||
            options.ConnectionReceiveWindowBytes > RuntimeContext.FlowControl.ConnectionReceiveWindowBytes)
        {
            throw NegotiationViolation("Negotiated receive windows exceed the local flow-control limits.");
        }

        var compressionNegotiated =
            (options.Capabilities & ProtocolV2Capabilities.Compression) != 0;
        if (compressionNegotiated != options.CompressionBinding.HasValue)
        {
            throw NegotiationViolation(
                "Negotiated compression capability and provider binding must be published together.");
        }
        if (options.CompressionBinding is { } binding)
            ValidateCompressionBinding(binding);

        if ((options.Capabilities & ProtocolV2Capabilities.FlowControl) == 0)
            return null;
        return new StreamFlowController(
            options.StreamReceiveWindowBytes,
            options.ConnectionReceiveWindowBytes,
            options.MaxFramePayloadBytes,
            RuntimeContext.Protocol.MaxConcurrentStreamsPerConnection);
    }

    private void ValidateCompressionBinding(SharpLinkCompressionProviderBinding binding)
    {
        if (binding.Provider is null)
            throw NegotiationViolation("Negotiated compression provider is missing.");
        try
        {
            SharpLinkCompressionProfile.Validate(binding.WireProfile, nameof(binding));
        }
        catch (ArgumentException exception)
        {
            throw NegotiationViolation("Negotiated compression profile is invalid.", exception);
        }
        if (!string.Equals(
                binding.WireProfile,
                binding.Provider.WireProfile,
                StringComparison.Ordinal))
        {
            throw NegotiationViolation(
                "Negotiated compression profile does not match its provider binding.");
        }

        foreach (var configured in RuntimeContext.Compression.ProviderBindings)
        {
            if (string.Equals(configured.WireProfile, binding.WireProfile, StringComparison.Ordinal) &&
                ReferenceEquals(configured.Provider, binding.Provider))
            {
                return;
            }
        }
        throw NegotiationViolation(
            "Negotiated compression binding is not owned by this runtime context.");
    }

    private static SharpLinkException NegotiationViolation(
        string message,
        Exception? innerException = null)
        => new(SharpLinkErrorCode.ProtocolViolation, message, innerException);

    private static void EnsureOutboundFrameAllowed(
        RpcSessionProtocolPhase phase,
        ProtocolV2FrameType frameType)
    {
        if (RpcSessionProtocolRules.IsFrameAllowed(phase, frameType))
            return;
        if (phase == RpcSessionProtocolPhase.Draining && frameType == ProtocolV2FrameType.Request)
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.Unavailable,
                "The connection is draining and cannot start a new request.");
        }
        throw new SharpLinkException(
            SharpLinkErrorCode.ProtocolViolation,
            $"Frame {frameType} is not allowed while the session is {phase}.");
    }

    private void TransitionProtocolPhase(
        RpcSessionProtocolPhase expected,
        RpcSessionProtocolPhase next)
    {
        while (true)
        {
            var current = Volatile.Read(ref _protocolState);
            if (current.Phase != expected)
                return;
            var replacement = current.WithPhase(next);
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _protocolState, replacement, current),
                    current))
            {
                return;
            }
        }
    }

    private void TransitionProtocolPhaseToStopping()
    {
        while (true)
        {
            var current = Volatile.Read(ref _protocolState);
            if (current.Phase is RpcSessionProtocolPhase.Stopping or RpcSessionProtocolPhase.Terminal)
                return;
            var stopping = current.WithPhase(RpcSessionProtocolPhase.Stopping);
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _protocolState, stopping, current),
                    current))
            {
                return;
            }
        }
    }

    private void TransitionProtocolPhaseToTerminal()
    {
        while (true)
        {
            var current = Volatile.Read(ref _protocolState);
            if (current.Phase == RpcSessionProtocolPhase.Terminal)
                return;
            var terminal = current.WithPhase(RpcSessionProtocolPhase.Terminal);
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _protocolState, terminal, current),
                    current))
            {
                return;
            }
        }
    }
}
