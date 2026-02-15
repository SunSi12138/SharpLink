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
        //Error
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
        
    }
}
