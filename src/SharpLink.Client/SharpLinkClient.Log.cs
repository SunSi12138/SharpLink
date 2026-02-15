namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private static readonly Func<ILogger, string, IDisposable?> SSessionScope =
        LoggerMessage.DefineScope<string>("SessionId:{SessionId}");

    private static readonly Func<ILogger, long, IDisposable?> SRequestScope =
        LoggerMessage.DefineScope<long>("RequestId:{RequestId}");

    private static IDisposable? BeginSessionLogScope(ILogger logger, string sessionId) => SSessionScope(logger, sessionId);

    private static IDisposable? BeginRequestLogScope(ILogger logger, long requestId) => SRequestScope(logger, requestId);

    [System.Diagnostics.Conditional(CompileSymbols.Debug)]
    private static void DebugLogServerHeartbeatReceived(ILogger logger) => LogServerHeartbeatReceived(logger);

    [System.Diagnostics.Conditional(CompileSymbols.Debug)]
    private static void DebugLogServerCancelIgnored(ILogger logger) => LogServerCancelIgnored(logger);
    
    

    [LoggerMessage(EventId = LogEvents.Connection.HeartbeatReceived, Level = LogLevel.Debug, Message = "Receive heartbeat from server.")]
    private static partial void LogServerHeartbeatReceived(ILogger logger);

    [LoggerMessage(EventId = LogEvents.Rpc.RequestReceived, Level = LogLevel.Debug, Message = "Ignore cancel packet from server.")]
    private static partial void LogServerCancelIgnored(ILogger logger);

    [LoggerMessage(EventId = LogEvents.Connection.HeartbeatTimeout, Level = LogLevel.Warning, Message = "Server disconnected due to heartbeat timeout.")]
    private static partial void LogServerHeartbeatTimeout(ILogger logger);

    [LoggerMessage(EventId = LogEvents.Rpc.DispatchFailed, Level = LogLevel.Warning, Message = "Response for unknown or timed-out request.")]
    private static partial void LogUnknownOrTimedOutResponse(ILogger logger);

    [LoggerMessage(EventId = LogEvents.Connection.ClientDisconnected, Level = LogLevel.Information, Message = "Client disconnected.")]
    private static partial void LogClientDisconnected(ILogger logger, Exception exception);
}
