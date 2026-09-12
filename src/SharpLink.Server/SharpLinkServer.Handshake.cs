namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private async Task<SharpLinkAuthenticationResult> ProcessHandshakeAsync(RpcSession session, CancellationToken ct)
    {
        var desiredSession = GetAcceptedDesiredSession();
        BindDesiredSessionSnapshot(session, desiredSession);
        var compressionProviders = _runtimeContext.Compression.ProviderBindings;
        var negotiationPolicy = ProtocolV2ContractManifestNegotiation.CreateImplementedPolicy(
            desiredSession.Configuration.MaxFramePayloadBytes,
            _runtimeContext.FlowControl.StreamReceiveWindowBytes,
            _runtimeContext.FlowControl.ConnectionReceiveWindowBytes,
            compressionProviders,
            enableSessionRefresh: session.SupportsSessionRefreshReplacement);
        var reader = session.Input;
        SharpLinkAuthenticationResult? handshakeResult = null;

        while (session.IsConnected && !ct.IsCancellationRequested)
        {
            var result = await reader.ReadAsync(ct);
            var buffer = result.Buffer;
            try
            {
                while (session.IsConnected &&
                       !ct.IsCancellationRequested &&
                       ProtocolV2FrameParser.TryReadFrame(
                           ref buffer, _protocolOptions, out var header, out var message))
                {
                    SharpLinkTelemetry.RecordReceivedBytes(ProtocolV2Constants.HeaderBytes + message.Length);
                    var runtimeSession = session;
                    SharpLinkAuthenticationResult authResult;
                    ProtocolV2HandshakeRequest request = default;
                    ProtocolV2ServerNegotiation? negotiation = null;
                    ProtocolViolationReason? violationReason = null;
                    if (!RpcSessionProtocolRules.IsFrameAllowed(runtimeSession.ProtocolPhase, header.Type) ||
                        header.Type != ProtocolV2FrameType.HandshakeRequest)
                    {
                        violationReason = ProtocolViolationReason.ProtocolState;
                        authResult = SharpLinkAuthenticationResult.Reject(
                            SharpLinkErrorCode.ProtocolViolation,
                            "Expected HandshakeRequest frame.");
                    }
                    else
                    {
                        request = ProtocolV2PayloadCodec.ReadHandshakeRequest(message, _protocolOptions);
                        try
                        {
                            negotiation = ProtocolV2Negotiator.NegotiateServer(
                                request,
                                negotiationPolicy);
                            authResult = await _authentication.AuthenticateAsync(
                                new SharpLinkAuthenticationRequest(
                                    session.Id,
                                    request.AuthenticationPayload,
                                    runtimeSession.LocalEndPoint,
                                    runtimeSession.RemoteEndPoint),
                                ct).ConfigureAwait(false);
                        }
                        catch (SharpLinkException exception)
                        {
                            violationReason = SharpLinkProtocolViolationException.Classify(exception);
                            authResult = SharpLinkAuthenticationResult.Reject(
                                exception.Code,
                                exception.Message);
                        }
                    }

                    if (authResult.IsAuthenticated)
                    {
                        var acceptedNegotiation = negotiation ?? throw new InvalidOperationException(
                            "Authentication succeeded without a protocol negotiation result.");
                        runtimeSession.InitializeServerResponseCompressionPreference(
                            request.ResponseCompressionPreferenceGeneration,
                            request.AllowResponseCompression);
                        await session.SendHandshakeResponseAndFlushAsync(
                            acceptedNegotiation.Response,
                            ct).ConfigureAwait(false);
                        if (!runtimeSession.TryCompleteHandshake(acceptedNegotiation.Options))
                        {
                            if (!runtimeSession.IsConnected)
                            {
                                throw new SharpLinkException(
                                    SharpLinkErrorCode.ConnectionClosed,
                                    "The handshake session terminated during completion.");
                            }
                            throw new SharpLinkProtocolViolationException(
                                ProtocolViolationReason.InternalState,
                                "The handshake result was already completed.");
                        }

                        // A desired-generation update can race this handshake. Delay the catch-up
                        // refresh until NotifyConnected, which occurs only after the bootstrap
                        // ContractManifest has been published and flushed to the client.
                        if ((acceptedNegotiation.Options.Capabilities & ProtocolV2Capabilities.SessionRefresh) != 0)
                        {
                            runtimeSession.OnConnected += () => TrackFrameworkTask(
                                RunSessionRefreshIfStaleAsync(runtimeSession, desiredSession),
                                "SessionRefreshCatchUp");
                        }
                    }
                    else
                    {
                        if (authResult.ErrorCode == SharpLinkErrorCode.ProtocolViolation)
                        {
                            SharpLinkTelemetry.RecordProtocolFailure("server");
                            LogProtocolViolationRateLimited(
                                violationReason ?? ProtocolViolationReason.Other);
                        }
                        else if (authResult.ErrorCode is SharpLinkErrorCode.AuthenticationRejected or
                                 SharpLinkErrorCode.AuthenticationExpired or
                                 SharpLinkErrorCode.AuthorizationDenied or
                                 SharpLinkErrorCode.PermissionDenied)
                            SharpLinkTelemetry.RecordAuthenticationFailure("server");
                        await session.SendHandshakeErrorAndFlushAsync(
                            authResult.ErrorCode,
                            authResult.ErrorMessage,
                            _protocolOptions.MaxErrorMessageBytes,
                            ct).ConfigureAwait(false);
                    }

                    handshakeResult = authResult;
                    break;
                }
            }
            finally
            {
                reader.AdvanceTo(buffer.Start, handshakeResult.HasValue ? buffer.Start : buffer.End);
            }

            if (handshakeResult.HasValue)
                return handshakeResult.Value;

            if (result.IsCompleted)
                break;
        }

        return SharpLinkAuthenticationResult.Reject(
            SharpLinkErrorCode.ConnectionClosed,
            "Client disconnected during handshake.");
    }
}
