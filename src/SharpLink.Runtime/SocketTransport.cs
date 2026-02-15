namespace SharpLink.Runtime;

/// <summary>
/// 通用 Socket 传输层，支持 TCP 和 UDS (Unix Domain Socket)
/// </summary>
public class SocketTransport(Socket socket, bool isServer = true, EndPoint? remoteEndPoint = null) : ITransport, IRpcSessionFlushConfigurableTransport
{
    private readonly Socket _socket = socket ?? throw new ArgumentNullException(nameof(socket));
    private readonly CancellationTokenSource _cts = new();
    private NetworkStream? _networkStream;
    private bool _disposed;
    private RpcSessionFlushOptions? _rpcSessionFlushOptions;

    // 客户端构造函数
    public SocketTransport(Socket socket, EndPoint remoteEndPoint) : this(socket, false, remoteEndPoint: remoteEndPoint) { }
    // 服务端构造函数 (Socket 必须由外部 Bind/Listen)

    public async Task<IRpcSession> ConnectAsync(ISerializer serializer,CancellationToken ct = default)
    {
        Socket connectedSocket;

        if (isServer)
        {
            // 服务端：等待 Accept
            connectedSocket = await _socket.AcceptAsync(ct);
        }
        else
        {
            // 客户端：发起 Connect
            if (remoteEndPoint == null) throw new InvalidOperationException("Client mode requires a RemoteEndPoint.");
            await _socket.ConnectAsync(remoteEndPoint, ct);
            connectedSocket = _socket;
        }

        // 禁用 Nagle 算法以降低延迟
        if (connectedSocket.ProtocolType == ProtocolType.Tcp)
        {
            connectedSocket.NoDelay = true;
        }

        var networkStream = new NetworkStream(connectedSocket, ownsSocket: true);
        _networkStream = networkStream;

        // 创建 Pipelines
        var reader = PipeReader.Create(networkStream);
        var writer = PipeWriter.Create(networkStream);

        return new RpcSession(
            Guid.NewGuid().ToString("N"),
            reader,
            writer,
            serializer,
            () => networkStream.Dispose(), // Disconnect Action
            () => !Volatile.Read(ref _disposed) && !_cts.IsCancellationRequested, // IsConnected Func
            _rpcSessionFlushOptions
        );
    }

    public void ConfigureRpcSessionFlush(RpcSessionFlushOptions options)
    {
        RpcSessionFlushOptions.Validate(options.FlushSizeThreshold, options.MaxLatency);
        _rpcSessionFlushOptions = options;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, true))
            return;

        _cts.Cancel();
        _networkStream?.Dispose();
        // 如果是服务端监听 Socket，通常由外部管理生命周期，或者在这里关闭
        if (isServer)
        {
            _socket.Dispose();
        }
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
