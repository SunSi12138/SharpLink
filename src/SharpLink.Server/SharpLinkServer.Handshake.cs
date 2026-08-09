namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private async Task<SharpLinkAuthenticationResult> ProcessHandshakeAsync(IRpcSession session, CancellationToken ct)
    {

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
                    SharpLinkAuthenticationResult authResult;
                    ProtocolV2HandshakeRequest request = default;
                    var supportedCapabilities =
                        ProtocolV2Capabilities.Metadata |
                        ProtocolV2Capabilities.FlowControl |
                        ProtocolV2Capabilities.HealthCheck |
                        ProtocolV2Capabilities.CancellationReason;
                    if (_runtimeContext.Compression.ProviderBindings.Count != 0)
                        supportedCapabilities |= ProtocolV2Capabilities.Compression;
                    if (header.Type != ProtocolV2FrameType.HandshakeRequest)
                    {
                        authResult = SharpLinkAuthenticationResult.Reject(
                            SharpLinkErrorCode.ProtocolViolation,
                            "Expected HandshakeRequest frame.");
                    }
                    else
                    {
                        request = ProtocolV2PayloadCodec.ReadHandshakeRequest(message, _protocolOptions);
                        var unsupportedRequired = request.RequiredCapabilities & ~supportedCapabilities;
                        if (unsupportedRequired != ProtocolV2Capabilities.None)
                        {
                            authResult = SharpLinkAuthenticationResult.Reject(
                                SharpLinkErrorCode.Unimplemented,
                                $"Required capabilities are unsupported: {unsupportedRequired}.");
                        }
                        else if ((request.RequiredCapabilities & ProtocolV2Capabilities.Compression) != 0 &&
                                 SelectCompressionProvider(request) is null)
                        {
                            authResult = SharpLinkAuthenticationResult.Reject(
                                SharpLinkErrorCode.Unimplemented,
                                "Required compression has no mutually supported profile.");
                        }
                        else
                        {
                            authResult = await AuthenticateAsync(session, request.AuthenticationPayload, ct)
                                .ConfigureAwait(false);
                        }
                    }

                    if (authResult.IsAuthenticated)
                    {
                        var compressionBinding = SelectCompressionProvider(request);
                        var negotiatedCapabilities = request.SupportedCapabilities & supportedCapabilities;
                        if (compressionBinding is null)
                            negotiatedCapabilities &= ~ProtocolV2Capabilities.Compression;
                        var response = new ProtocolV2HandshakeResponse(
                            Math.Min(request.MinorVersion, ProtocolV2Constants.MinorVersion),
                            negotiatedCapabilities,
                            Math.Min(request.MaxFramePayloadBytes, _protocolOptions.MaxFramePayloadBytes),
                            Math.Min(request.StreamReceiveWindowBytes, _runtimeContext.FlowControl.StreamReceiveWindowBytes),
                            Math.Min(request.ConnectionReceiveWindowBytes, _runtimeContext.FlowControl.ConnectionReceiveWindowBytes),
                            compressionBinding?.WireProfile);
                        var runtimeSession = (RpcSession)session;
                        runtimeSession.NegotiatedCapabilities = response.NegotiatedCapabilities;
                        runtimeSession.SetNegotiatedMaxFramePayloadBytes(response.MaxFramePayloadBytes);
                        if (compressionBinding is { } binding)
                            runtimeSession.EnableCompression(binding.Provider, binding.WireProfile);
                        if ((response.NegotiatedCapabilities & ProtocolV2Capabilities.FlowControl) != 0)
                        {
                            runtimeSession.EnableStreamFlowControl(
                                response.StreamReceiveWindowBytes,
                                response.ConnectionReceiveWindowBytes);
                        }
                        await session.SendHandshakeResponseAndFlushAsync(response, ct).ConfigureAwait(false);
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

    private SharpLinkCompressionProviderBinding? SelectCompressionProvider(
        in ProtocolV2HandshakeRequest request)
    {
        if ((request.SupportedCapabilities & ProtocolV2Capabilities.Compression) == 0 ||
            request.CompressionProfiles.IsEmpty)
        {
            return null;
        }

        foreach (var binding in _runtimeContext.Compression.ProviderBindings)
        {
            foreach (var profile in request.CompressionProfiles.Span)
            {
                if (string.Equals(binding.WireProfile, profile, StringComparison.Ordinal))
                    return binding;
            }
        }
        return null;
    }

    private async ValueTask<SharpLinkAuthenticationResult> AuthenticateAsync(
        IRpcSession session,
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
            var rpcSession = (RpcSession)session;
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
            LogAuthenticationProviderFailed(_logger, exception);
            return SharpLinkAuthenticationResult.Reject(
                SharpLinkErrorCode.AuthenticationRejected,
                "Authentication failed.");
        }
    }
}
