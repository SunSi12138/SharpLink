namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private async Task<SharpLinkAuthenticationResult> ProcessHandshakeAsync(RpcSession session, CancellationToken ct)
    {
        var compressionProviders = _runtimeContext.Compression.ProviderBindings;
        var negotiationPolicy = ProtocolV2Negotiator.CreateImplementedPolicy(
            _protocolOptions.MaxFramePayloadBytes,
            _runtimeContext.FlowControl.StreamReceiveWindowBytes,
            _runtimeContext.FlowControl.ConnectionReceiveWindowBytes,
            compressionProviders);
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
                        await session.SendHandshakeResponseAndFlushAsync(
                            acceptedNegotiation.Response,
                            ct).ConfigureAwait(false);
                        if (!runtimeSession.TryCompleteHandshake(acceptedNegotiation.Options))
                        {
                            if (!runtimeSession.IsConnected)
                            {
                                // The session terminated concurrently (shutdown/teardown):
                                // an expected connection-termination race, not a protocol bug.
                                throw new SharpLinkException(
                                    SharpLinkErrorCode.ConnectionClosed,
                                    "The handshake session terminated during completion.");
                            }
                            // A connected session whose handshake phase is already gone is a
                            // genuine server-side state bug; classify it as internal so the
                            // connection loop keeps the full Error path for it.
                            throw new SharpLinkProtocolViolationException(
                                ProtocolViolationReason.InternalState,
                                "The handshake result was already completed.");
                        }
                    }
                    else
                    {
                        if (authResult.ErrorCode == SharpLinkErrorCode.ProtocolViolation)
                        {
                            SharpLinkTelemetry.RecordProtocolFailure("server");
                            // Hostile-input rejection during the handshake gets the same
                            // bounded, classified, exception-free Warning as a thrown
                            // violation; the generic handshake-failed Warning is skipped
                            // below so an attacker cannot grow the log per connection.
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
                // The first request can be coalesced with the handshake request. Preserve the
                // unconsumed remainder as unexamined when handing the reader to the request loop.
                // The finally also releases transport read ownership when parsing throws.
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
