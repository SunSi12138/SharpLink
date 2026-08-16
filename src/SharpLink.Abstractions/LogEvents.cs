namespace SharpLink.Abstractions;

/// <summary>Defines stable event identifiers emitted by SharpLink logging integrations.</summary>
public static class LogEvents
{
    /// <summary>Defines connection and handshake event identifiers in the 1000-1999 range.</summary>
    public static class Connection
    {
        //Information
        /// <summary>A client connection completed its handshake.</summary>
        public const int ClientConnected = 1001;
        /// <summary>A client connection closed normally.</summary>
        public const int ClientDisconnected = 1002;
        //Warning
        /// <summary>A protocol handshake did not complete successfully.</summary>
        public const int HandshakeFailed = 1101;
        /// <summary>A connection was closed because its heartbeat timed out.</summary>
        public const int HeartbeatTimeout = 1102;
        /// <summary>A TLS handshake did not complete successfully.</summary>
        public const int TlsHandshakeFailed = 1103;
        /// <summary>An authentication provider threw while validating a handshake.</summary>
        public const int AuthenticationProviderFailed = 1104;
        /// <summary>A connection was rejected because a pre-call admission bound was exhausted.</summary>
        public const int ConnectionAdmissionRejected = 1105;
        //Error
        /// <summary>A client connection closed because of an unexpected error.</summary>
        public const int ClientDisConnectedWithError = 1201;
        //Critical
        //Debug
        /// <summary>A valid heartbeat was received from the peer.</summary>
        public const int HeartbeatReceived = 1401;
        //Trace
    }

    /// <summary>Defines RPC dispatch event identifiers in the 2000-2999 range.</summary>
    public static class Rpc
    {
        //Information
        //Warning
        /// <summary>A request could not be dispatched to its registered handler.</summary>
        public const int DispatchFailed = 2101;
        /// <summary>A one-way request handler failed after the request was accepted.</summary>
        public const int OneWayDispatchFailed = 2102;
        /// <summary>A request was rejected because a configured resource limit was exhausted.</summary>
        public const int ResourceExhausted = 2103;
        //Error
        //Critical
        //Debug
        /// <summary>A valid RPC request was received.</summary>
        public const int RequestReceived = 2401;
        /// <summary>An active call was abandoned by its caller.</summary>
        public const int CallAbandoned = 2402;
        //Trace
    }

    /// <summary>Defines streaming event identifiers in the 3000-3999 range.</summary>
    public static class Stream
    {
        /// <summary>A stream data chunk was received.</summary>
        public const int ChunkReceived = 3001;
        /// <summary>A request or response stream closed.</summary>
        public const int StreamClosed = 3002;
    }

    /// <summary>Defines transport event identifiers in the 4000-4999 range.</summary>
    public static class Transport
    {
        /// <summary>A transport completed TLS negotiation.</summary>
        public const int TlsEstablished = 4001;
    }

    /// <summary>Defines server event identifiers in the 5000-5999 range.</summary>
    public static class Server
    {
        /// <summary>A server background processing loop terminated with an unhandled exception.</summary>
        public const int BackgroundLoopUnhandledException = 5001;
        /// <summary>The server heartbeat loop encountered an unhandled exception.</summary>
        public const int HeartbeatLoopUnhandledException = 5002;
        /// <summary>The server published its effective active-call capacity limits.</summary>
        public const int CallCapacityConfigured = 5003;
        /// <summary>The server published its effective pre-call connection admission bounds.</summary>
        public const int ConnectionAdmissionConfigured = 5004;
        /// <summary>Calls remained active after the graceful-drain interval and were forced to stop.</summary>
        public const int ForcedCallsRemaining = 5101;
        /// <summary>A deferred cleanup operation failed.</summary>
        public const int DeferredCleanupFailed = 5201;
        /// <summary>Framework-owned cleanup did not complete within its timeout.</summary>
        public const int FrameworkCleanupTimeout = 5301;
    }

    /// <summary>Defines client event identifiers in the 6000-6999 range.</summary>
    public static class Client
    {
        /// <summary>A response arrived after its request timed out or was no longer known.</summary>
        public const int UnknownOrTimedOutResponse = 6001;
        /// <summary>A client background processing loop terminated with an unhandled exception.</summary>
        public const int BackgroundLoopUnhandledException = 6002;
        /// <summary>A client connection attempt failed.</summary>
        public const int ConnectionAttemptFailed = 6101;
        /// <summary>A service resolver update failed.</summary>
        public const int ResolverUpdateFailed = 6102;
        /// <summary>A multi-cluster slot lifecycle operation changed stage.</summary>
        public const int MultiClusterMutationStage = 6003;
    }
}
