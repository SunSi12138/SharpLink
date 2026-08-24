

using System.Diagnostics;

namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private static readonly Func<ILogger, string, IDisposable?> SSessionScope =
        LoggerMessage.DefineScope<string>("SessionId:{SessionId}");

    private static readonly Func<ILogger, long, IDisposable?> SRequestScope =
        LoggerMessage.DefineScope<long>("RequestId:{RequestId}");


    [Conditional(CompileSymbols.Debug)]
    private static void DebugLogClientHeartbeatReceived(ILogger logger) => LogClientHeartbeatReceived(logger);

    private static IDisposable? BeginSessionLogScope(ILogger logger, string sessionId) => SSessionScope(logger, sessionId);

    private static IDisposable? BeginRequestLogScope(ILogger logger, long requestId) => SRequestScope(logger, requestId);

    [LoggerMessage(EventId = LogEvents.Connection.ClientConnected, Level = LogLevel.Information, Message = "Client connected.")]
    private static partial void LogClientConnected(ILogger logger);

    [LoggerMessage(EventId = LogEvents.Connection.ClientDisconnected, Level = LogLevel.Information, Message = "Client disconnected.")]
    private static partial void LogClientDisconnected(ILogger logger);

    [LoggerMessage(EventId = LogEvents.Connection.HandshakeFailed, Level = LogLevel.Warning, Message = "Handshake failed for client.")]
    private static partial void LogHandshakeFailed(ILogger logger);

    [LoggerMessage(EventId = LogEvents.Connection.TlsHandshakeFailed, Level = LogLevel.Warning, Message = "TLS handshake failed for client.")]
    private static partial void LogTlsHandshakeFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = LogEvents.Transport.TlsEstablished, Level = LogLevel.Information, Message = "TLS established using {Protocol} and {CipherSuite}.")]
    private static partial void LogTlsEstablished(ILogger logger, SslProtocols protocol, TlsCipherSuite cipherSuite);

    [LoggerMessage(EventId = LogEvents.Connection.AuthenticationProviderFailed, Level = LogLevel.Warning, Message = "Authentication provider failed without exposing payload data.")]
    private static partial void LogAuthenticationProviderFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = LogEvents.Connection.HeartbeatTimeout, Level = LogLevel.Warning, Message = "Client disconnected due to heartbeat timeout.")]
    private static partial void LogClientHeartbeatTimeout(ILogger logger);

    [LoggerMessage(EventId = LogEvents.Rpc.OneWayDispatchFailed, Level = LogLevel.Warning, Message = "One-way RPC dispatch failed.")]
    private static partial void LogOnewayRpcDispatchFailed(ILogger logger, Exception e);

    [LoggerMessage(EventId = LogEvents.Rpc.ResourceExhausted, Level = LogLevel.Warning, Message = "One-way RPC was rejected because a bounded resource is exhausted ({Reason}).")]
    private static partial void LogOnewayRpcResourceExhausted(ILogger logger, string reason);

    [LoggerMessage(EventId = LogEvents.Rpc.DispatchFailed, Level = LogLevel.Error, Message = "Unhandled exception in RPC dispatch.")]
    private static partial void LogRpcDispatchUnhandledException(ILogger logger, Exception e);

    [LoggerMessage(EventId = LogEvents.Rpc.CallAbandoned, Level = LogLevel.Debug, Message = "RPC call was abandoned with reason {Reason}; any late response is suppressed.")]
    private static partial void LogRpcCallAbandoned(ILogger logger, ServerCallCancellationReason reason);

    [LoggerMessage(EventId = LogEvents.Server.BackgroundLoopUnhandledException, Level = LogLevel.Error, Message = "Server background loop {LoopName} failed.")]
    private static partial void LogServerBackgroundLoopUnhandledException(ILogger logger, string loopName, Exception e);

    [LoggerMessage(EventId = LogEvents.Server.CallCapacityConfigured, Level = LogLevel.Information, Message = "Server call capacity configured: per_connection={MaxConcurrentCallsPerConnection}, per_server={MaxConcurrentCallsPerServer}.")]
    private static partial void LogServerCallCapacityConfigured(
        ILogger logger,
        int maxConcurrentCallsPerConnection,
        int maxConcurrentCallsPerServer);

    [LoggerMessage(EventId = LogEvents.Server.ForcedCallsRemaining, Level = LogLevel.Warning, Message = "Server grace period expired with {ActiveCalls} user calls still running; their service graph will be released after they finish.")]
    private static partial void LogForcedCallsRemaining(ILogger logger, int activeCalls);

    [LoggerMessage(EventId = LogEvents.Server.DeferredCleanupFailed, Level = LogLevel.Error, Message = "Deferred server cleanup {CleanupName} failed.")]
    private static partial void LogDeferredCleanupFailed(ILogger logger, string cleanupName, Exception exception);

    [LoggerMessage(EventId = LogEvents.Server.FrameworkCleanupTimeout, Level = LogLevel.Critical, Message = "Server framework cleanup exceeded its fixed {CleanupBudgetSeconds}-second budget; StopAsync is returning with the server faulted.")]
    private static partial void LogFrameworkCleanupTimeout(ILogger logger, int cleanupBudgetSeconds);

    [LoggerMessage(EventId = LogEvents.Connection.HeartbeatReceived, Level = LogLevel.Debug, Message = "Received client heartbeat.")]
    private static partial void LogClientHeartbeatReceived(ILogger logger);
}
