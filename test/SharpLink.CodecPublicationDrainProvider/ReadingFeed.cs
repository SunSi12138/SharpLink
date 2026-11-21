using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using SharpLink.Abstractions;
using SharpLink.Sdk;

[assembly: RpcCodecAdapterRegistration(typeof(SharpLink.CodecPublicationDrainProvider.FeedReadingCodecAdapter),
    "sharplink.tests.reading-feed/v1")]

namespace SharpLink.CodecPublicationDrainProvider;

[RpcContract]
public interface IReadingFeed : IService
{
    [SharpLink.Sdk.Timeout(12)]
    IAsyncEnumerable<FeedReading> ReadAsync(bool follow, CancellationToken cancellationToken);
}

[RpcCodecAdapter(typeof(FeedReadingCodecAdapter))]
public readonly record struct FeedReading(int Value);

[RpcCodecSemanticIdentity(0x9bff6b9eb60d4a39UL, 0xa948e36697963226UL)]
public sealed class FeedReadingCodecAdapter : IRpcCodecAdapter
{
    public string AdapterId => "sharplink.tests.reading-feed/v1";
    public IRpcCodecAdapterScope CreateScope() => new Scope();

    private sealed class Scope : IRpcCodecAdapterScope
    {
        private readonly FeedReadingCodec _codec = new();

        public IRpcCodec<T> CreateCodec<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>()
            => _codec as IRpcCodec<T> ?? throw new NotSupportedException($"No reading codec for {typeof(T)}.");

        public void Dispose() => _codec.Dispose();
    }
}

public sealed class FeedReadingCodec : IRpcCodec<FeedReading>, IDisposable
{
    public static int Serialized;
    public static int Deserialized;
    public static int Disposed;
    public static Action<int>? BeforeDecodeReturns { get; set; }
    private bool _disposed;

    public void Serialize(in FeedReading value, IBufferWriter<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.GetSpan(4), value.Value);
        buffer.Advance(4);
        Interlocked.Increment(ref Serialized);
    }

    public FeedReading Deserialize(in ReadOnlySequence<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Span<byte> bytes = stackalloc byte[4];
        buffer.CopyTo(bytes);
        var value = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        BeforeDecodeReturns?.Invoke(value);
        ObjectDisposedException.ThrowIf(_disposed, this);
        Interlocked.Increment(ref Deserialized);
        return new(value);
    }

    public void Dispose()
    {
        _disposed = true;
        Interlocked.Increment(ref Disposed);
    }
}

[RpcService]
public sealed class ReadingFeed : IReadingFeed, IAsyncDisposable
{
    private static readonly TaskCompletionSource Update = NewSignal();
    private static readonly TaskCompletionSource Cleanup = NewSignal();
    private static readonly TaskCompletionSource Waiting = NewSignal();
    private static readonly TaskCompletionSource Finishing = NewSignal();
    public static int FirstValue { get; set; } = 10;
    public static int ClosedFeeds;
    public static int DisposedServices;
    public static Task WaitingForUpdate => Waiting.Task;
    public static Task ClosingFeed => Finishing.Task;
    public static void PublishUpdate() => Update.TrySetResult();
    public static void FinishClosing() => Cleanup.TrySetResult();

    public async IAsyncEnumerable<FeedReading> ReadAsync(bool follow,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            yield return new(FirstValue);
            if (!follow)
                yield break;
            Waiting.TrySetResult();
            await Update.Task.WaitAsync(cancellationToken);
            yield return new(FirstValue + 1);
        }
        finally
        {
            if (follow)
            {
                Finishing.TrySetResult();
                await Cleanup.Task;
            }
            Interlocked.Increment(ref ClosedFeeds);
        }
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref DisposedServices);
        return ValueTask.CompletedTask;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
