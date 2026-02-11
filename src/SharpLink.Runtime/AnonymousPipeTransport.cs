namespace SharpLink.Runtime;

/// <summary>
/// 匿名管道传输层。需要传入一对管道流（输入流和输出流）。
/// </summary>
public class AnonymousPipeTransport(PipeStream inputStream, PipeStream outputStream) : ITransport
{
    private readonly PipeStream _input = inputStream ?? throw new ArgumentNullException(nameof(inputStream));
    private readonly PipeStream _output = outputStream ?? throw new ArgumentNullException(nameof(outputStream));
    
    private bool _isConnected;
    private bool _disposed;
    
    public async Task<IRpcSession> ConnectAsync(ISerializer serializer,CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _isConnected, true))
        {
            var tcs = new TaskCompletionSource<IRpcSession>();
            await using var res = ct.Register(() => tcs.TrySetCanceled());
            return await tcs.Task;
        }
        else
        {
            // 匿名管道通常在创建时就已经连接（通过句柄继承），或者是阻塞直到对方读取句柄。
            // 这里假设句柄已经交换完毕，直接建立 Session。
            
            // 注意：匿名管道不需要显式的 Connect 动作，但可能需要等待传递句柄
            // 如果需要同步，可以在外部使用 WaitForPipeDrain 或其他信号量
            
            var reader = PipeReader.Create(_input);
            var writer = PipeWriter.Create(_output);
    
            return new RpcSession(
                Guid.NewGuid().ToString("N"),
                reader,
                writer,
                serializer,
                Dispose, // Disconnect Action 会触发 Dispose 关闭流
                () => _input.IsConnected && _output.IsConnected
            );
        }
        
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, true))
            return;

        _input.Dispose();
        _output.Dispose();
        GC.SuppressFinalize(this);
    }
}
