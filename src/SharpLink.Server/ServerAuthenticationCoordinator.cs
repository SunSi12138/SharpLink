using System.Diagnostics;

namespace SharpLink.Server;

/// <summary>Owns server authentication provider decisions and failure handling.</summary>
internal sealed partial class ServerAuthenticationCoordinator
{
    private readonly ISharpLinkServerAuthenticator? _authenticator;
    private readonly bool _authenticationRequired;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private FixedWindowLogThrottle _failureLogThrottle;
    private long _failureSequence;

    internal ServerAuthenticationCoordinator(
        ISharpLinkServerAuthenticator? authenticator,
        bool authenticationRequired,
        ILogger logger,
        TimeProvider timeProvider)
    {
        _authenticator = authenticator;
        _authenticationRequired = authenticationRequired;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _failureLogThrottle = new FixedWindowLogThrottle(
            TimeSpan.FromSeconds(5),
            timeProvider.TimestampFrequency);
    }

    internal async ValueTask<SharpLinkAuthenticationResult> AuthenticateAsync(
        SharpLinkAuthenticationRequest request,
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
            var result = await _authenticator.AuthenticateAsync(request, cancellationToken).ConfigureAwait(false);
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
            // persisted by the default logger. The warning is also rate-limited so a
            // client that reliably makes the provider throw cannot grow the log per
            // connection attempt.
            var failureId = Interlocked.Increment(ref _failureSequence);
            if (_failureLogThrottle.ShouldLog(_timeProvider.GetTimestamp(), out _))
            {
                LogAuthenticationProviderFailed(
                    _logger,
                    failureId,
                    exception.GetType().FullName ?? exception.GetType().Name);
            }
            DebugTraceAuthenticationProviderException(exception);
            return SharpLinkAuthenticationResult.Reject(
                SharpLinkErrorCode.AuthenticationRejected,
                "Authentication failed.");
        }
    }

    [LoggerMessage(
        EventId = LogEvents.Connection.AuthenticationProviderFailed,
        Level = LogLevel.Warning,
        Message = "Authentication provider failed. FailureId={FailureId}, ExceptionType={ExceptionType}.")]
    private static partial void LogAuthenticationProviderFailed(
        ILogger logger,
        long failureId,
        string exceptionType);

    /// <summary>
    /// Debug-build-only sink for the full authentication provider exception. Production
    /// builds never persist provider exception payloads; this exists solely for in-process
    /// debugging when the DEBUG symbol is defined.
    /// </summary>
    [Conditional(CompileSymbols.Debug)]
    private static void DebugTraceAuthenticationProviderException(Exception exception)
        => Debug.WriteLine(exception);
}
