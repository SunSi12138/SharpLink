namespace SharpLink.Abstractions;

public static class LogEvents
{
    /// <summary>
    /// 1000-1999
    /// </summary>
    public static class Connection
    {
        //Information
        public const int ClientConnected = 1001;
        public const int ClientDisconnected = 1002;
        //Warning
        public const int HandshakeFailed = 1101;
        public const int HeartbeatTimeout = 1102;
        public const int TlsHandshakeFailed = 1103;
        public const int AuthenticationProviderFailed = 1104;
        //Error
        public const int ClientDisConnectedWithError = 1201;
        //Critical
        //Debug
        public const int HeartbeatReceived = 1401;
        //Trace
    }

    /// <summary>
    /// 2000-2999
    /// </summary>
    public static class Rpc
    {
        //Information
        //Warning
        public const int DispatchFailed = 2101;
        public const int OneWayDispatchFailed = 2102;
        public const int ResourceExhausted = 2103;
        //Error
        //Critical
        //Debug
        public const int RequestReceived = 2401;
        //Trace
    }

    /// <summary>
    /// 3000-3999
    /// </summary>
    public static class Stream
    {
        public const int ChunkReceived = 3001;
        public const int StreamClosed = 3002;
    }
    
    /// <summary>
    /// 4000-4999
    /// </summary>
    public static class Transport
    {
        public const int TlsEstablished = 4001;
    }
    
    /// <summary>
    /// 5000-5999
    /// </summary>
    public static class Server
    {
        public const int BackgroundLoopUnhandledException = 5001;
        public const int HeartbeatLoopUnhandledException = 5002;
    }
    
    /// <summary>
    /// 6000-6999
    /// </summary>
    public static class Client
    {
        public const int UnknownOrTimedOutResponse = 6001;
        public const int BackgroundLoopUnhandledException = 6002;
    }
}
