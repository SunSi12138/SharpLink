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
                    if (!RpcSessionProtocolRules.IsFrameAllowed(runtimeSession.ProtocolPhase, header.Type) ||
                        header.Type != ProtocolV2FrameType.HandshakeRequest)
                    {
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
                            authResult = await AuthenticateAsync(session, request.AuthenticationPayload, ct)
                                .ConfigureAwait(false);
                        }
                        catch (SharpLinkException exception)
                        {
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
                            throw new SharpLinkException(
                                SharpLinkErrorCode.ProtocolViolation,
                                "The handshake result was already completed or the session terminated.");
                        }
                    }
                    else
                    {
                        if (authResult.ErrorCode == SharpLinkErrorCode.ProtocolViolation)
                            SharpLinkTelemetry.RecordProtocolFailure("server");
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

    private async ValueTask<SharpLinkAuthenticationResult> AuthenticateAsync(
        RpcSession session,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (_authenticator is null)
        {
            return _authenticationRequired
                ? SharpLinkAuthenticationResult.Reject()
                : SharpLinkAuthenticationResult.Success;
        }

        try
        {
            var rpcSession = session;
            var result = await _authenticator.AuthenticateAsync(
                new SharpLinkAuthenticationRequest(
                    session.Id,
                    payload,
                    rpcSession.LocalEndPoint,
                    rpcSession.RemoteEndPoint),
                cancellationToken).ConfigureAwait(false);
            if (result.IsAuthenticated && result.ErrorCode != SharpLinkErrorCode.Unknown)
            {
                return SharpLinkAuthenticationResult.Reject(
                    SharpLinkErrorCode.AuthenticationRejected,
                    "Authentication provider returned a contradictory result.");
            }
            if (result.IsAuthenticated && result.Context?.IsExpired() == true)
            {
                return SharpLinkAuthenticationResult.Reject(
                    SharpLinkErrorCode.AuthenticationExpired,
                    "Authentication token has expired.");
            }
            if (!result.IsAuthenticated && result.ErrorCode == SharpLinkErrorCode.Unknown)
            {
                return SharpLinkAuthenticationResult.Reject(
                    SharpLinkErrorCode.AuthenticationRejected,
                    result.ErrorMessage);
            }
            if (!result.IsAuthenticated &&
                !ProtocolV2PayloadCodec.IsDefinedErrorCode(result.ErrorCode))
            {
                return SharpLinkAuthenticationResult.Reject(
                    SharpLinkErrorCode.AuthenticationRejected,
                    "Authentication provider returned an undefined error code.");
            }
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Security: extension-provider exceptions may contain tokens, credentials, or
            // provider SDK details. Only a stable CLR type identity and an internal,
            // server-generated correlation ID may enter the production log; the full
            // exception is retained in-process (debugger / DEBUG builds) but never
            // persisted by the default logger.
            var failureId = Interlocked.Increment(ref _authenticationFailureSequence);
            LogAuthenticationProviderFailed(
                _logger,
                failureId,
                exception.GetType().FullName ?? exception.GetType().Name);
            DebugTraceAuthenticationProviderException(exception);
            return SharpLinkAuthenticationResult.Reject(
                SharpLinkErrorCode.AuthenticationRejected,
                "Authentication failed.");
        }
    }
}
