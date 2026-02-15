

namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private static readonly Func<ILogger, string, IDisposable?> SSessionScope =
        LoggerMessage.DefineScope<string>("SessionId:{SessionId}");

    private static readonly Func<ILogger, long, IDisposable?> SRequestScope =
        LoggerMessage.DefineScope<long>("RequestId:{RequestId}");

    private static IDisposable? BeginSessionLogScope(ILogger logger, string sessionId) => SSessionScope(logger, sessionId);

    private static IDisposable? BeginRequestLogScope(ILogger logger, long requestId) => SRequestScope(logger, requestId);

    [LoggerMessage(EventId = LogEvents.Connection.ClientConnected, Level = LogLevel.Information, Message = "Client connected.")]
    private static partial void LogClientConnected(ILogger logger);

    [LoggerMessage(EventId = LogEvents.Connection.ClientDisconnected, Level = LogLevel.Information, Message = "Client disconnected.")]
    private static partial void LogClientDisconnected(ILogger logger);
    
    [LoggerMessage(EventId = LogEvents.Connection.HandshakeFailed, Level = LogLevel.Warning, Message = "Handshake failed for client.")]
    private static partial void LogHandshakeFailed(ILogger logger);
    
    [LoggerMessage(EventId = LogEvents.Connection.HeartbeatTimeout, Level = LogLevel.Warning, Message = "Client disconnected due to heartbeat timeout.")]
    private static partial void LogClientHeartbeatTimeout(ILogger logger);
    
    [LoggerMessage(EventId = LogEvents.Rpc.OneWayDispatchFailed, Level = LogLevel.Warning, Message = "One-way RPC dispatch failed.")]
    private static partial void LogOnewayRpcDispatchFailed(ILogger logger, Exception e);

    [LoggerMessage(EventId = LogEvents.Rpc.DispatchFailed, Level = LogLevel.Error, Message = "Unhandled exception in RPC dispatch.")]
    private static partial void LogRpcDispatchUnhandledException(ILogger logger, Exception e);

    [LoggerMessage(EventId = LogEvents.Rpc.DispatchFailed, Level = LogLevel.Error, Message = "Server background loop {LoopName} failed.")]
    private static partial void LogServerBackgroundLoopUnhandledException(ILogger logger, string loopName, Exception e);
    
    [System.Diagnostics.Conditional(CompileSymbols.Debug)]
    private static void DebugLogClientHeartbeatReceived(ILogger logger)=>LogClientHeartbeatReceived(logger);
    
    [LoggerMessage(EventId = LogEvents.Connection.HeartbeatReceived, Level = LogLevel.Debug, Message = "Received client heartbeat.")]
    private static partial void LogClientHeartbeatReceived(ILogger logger);
    
    
    
}
