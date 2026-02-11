namespace SharpLink.Runtime;

public class RpcSession : IRpcSession
{
    public string Id { get; }
    public DateTime LastActive { get; set; } = DateTime.UtcNow;
    public PipeReader Input { get; }
    public ISerializer Serializer { get; }
    private PipeWriter Output { get; }

    private readonly CancellationTokenSource _cts = new();
    private int _disposed;
    private readonly Channel<ArrayBufferWriter<byte>> _channel;

    public IStreamManager StreamManager { get; } = new StreamManager();
    public bool IsConnected => _isConnected();
    private readonly Action _disconnect;
    private readonly Func<bool> _isConnected;

    public RpcSession(string id, PipeReader reader, PipeWriter writer, ISerializer serializer, Action disconnect, Func<bool> isConnected)
    {
        _disconnect = disconnect;
        _isConnected = isConnected;
        Id = id;
        Input = reader;
        Output = writer;
        Serializer = serializer;
        _channel = Channel.CreateUnbounded<ArrayBufferWriter<byte>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _ = Task.Run(ProcessSendQueueLoop);
    }

    public ValueTask SendPacketAsync(ArrayBufferWriter<byte> packet) => _channel.Writer.WriteAsync(packet);

    private static readonly long TimestampFrequency = Stopwatch.Frequency;
    private static readonly long MaxLatencyTicks = (long)(0.001 * TimestampFrequency);
    private const int FlushSizeThreshold = 1024 * 4;

    private async Task ProcessSendQueueLoop()
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(_cts.Token))
            {
                var bytesAccumulated = 0;
                var startTimestamp = Stopwatch.GetTimestamp();
                var flushNeeded = false;

                while (_channel.Reader.TryRead(out var buffer))
                {
                    var source = buffer.WrittenSpan;
                    if (source.Length > 0)
                    {
                        var destination = Output.GetSpan(source.Length);
                        source.CopyTo(destination);
                        Output.Advance(source.Length);
                        bytesAccumulated += source.Length;
                        flushNeeded = true;
                    }

                    BufferWriterPool.Return(buffer);

                    var currentTimestamp = Stopwatch.GetTimestamp();
                    if (bytesAccumulated < FlushSizeThreshold && (currentTimestamp - startTimestamp) < MaxLatencyTicks)
                        continue;

                    var singleRes = await Output.FlushAsync(_cts.Token);
                    if (singleRes.IsCanceled || singleRes.IsCompleted)
                        return;

                    bytesAccumulated = 0;
                    startTimestamp = currentTimestamp;
                    flushNeeded = false;
                }

                if (!flushNeeded)
                    continue;

                var result = await Output.FlushAsync(_cts.Token);
                if (result.IsCanceled || result.IsCompleted)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await Output.CompleteAsync();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _disconnect();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
