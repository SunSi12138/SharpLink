using System.Runtime.CompilerServices;
using System.Buffers;
using System.Buffers.Binary;
using SharpLink.Abstractions;
using SharpLink.Sdk;

namespace SharpLink.GroupPlugin;

public static class Signals
{
    public static int Tag;
    public static Action<string>? Observe;
    public static Func<Task>? Body;
    public static Func<Task>? Cleanup;
    public static Action? Decode;
    public static Func<Task>? StreamCleanup;
    public static Action? Encode;
    public static Func<Task>? ProducerCleanup;
}

[RpcContract]
public interface IHotRpc : IService
{
    [NonCancellable] ValueTask<int> GetAsync();
    ValueTask<int> HoldAsync(CancellationToken cancellationToken);
    [NonCancellable] ValueTask<int> EchoAsync(HotPayload value);
    ValueTask<int> ConsumeAsync(IAsyncEnumerable<HotPayload> values, CancellationToken cancellationToken);
    ValueTask<int> ConsumeWithHeaderAsync(HotPayload header, IAsyncEnumerable<HotPayload> values, CancellationToken cancellationToken);
    IAsyncEnumerable<HotPayload> ProduceAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<HotPayload> DuplexAsync(IAsyncEnumerable<HotPayload> values, CancellationToken cancellationToken);
#if GROUP_CHANGED_HASH
    [NonCancellable] ValueTask<int> ExtraAsync();
#endif
}
[RpcContract]
public interface ISecondHotRpc : IService
{
    [NonCancellable] ValueTask<int> GetSecondAsync();
}
[RpcCodec(typeof(HotPayloadCodec))]
public readonly record struct HotPayload(int Value);
[RpcCodecSemanticIdentity(0x77531UL, 0x77532UL)]
public sealed class HotPayloadCodec : IRpcCodec<HotPayload>
{
    public void Serialize(in HotPayload value, IBufferWriter<byte> writer)
    { Signals.Observe?.Invoke("encode"); Signals.Encode?.Invoke(); BinaryPrimitives.WriteInt32LittleEndian(writer.GetSpan(4), value.Value); writer.Advance(4); }
    public HotPayload Deserialize(in ReadOnlySequence<byte> bytes)
    {
        Signals.Observe?.Invoke("decode");
        Signals.Decode?.Invoke();
        var reader = new SequenceReader<byte>(bytes);
        if (!reader.TryReadLittleEndian(out int value)) throw new InvalidDataException("Truncated payload.");
        return new(value + Signals.Tag * 10);
    }
}
[RpcService(Lifetime = SharpLinkServiceLifetime.Call)]
public sealed class HotService : IHotRpc, IAsyncDisposable
{
    public HotService() => Signals.Observe?.Invoke("activate");
    public ValueTask<int> GetAsync() { Signals.Observe?.Invoke("get"); return ValueTask.FromResult(Signals.Tag); }
    public ValueTask<int> EchoAsync(HotPayload value) => ValueTask.FromResult(Signals.Tag * 1000 + value.Value);
    public async ValueTask<int> ConsumeAsync(IAsyncEnumerable<HotPayload> values, CancellationToken cancellationToken)
    {
        var sum = 0;
        await foreach (var value in values.WithCancellation(cancellationToken)) sum += value.Value;
        Signals.Observe?.Invoke("consumed");
        return Signals.Tag * 1000 + sum;
    }
    public async ValueTask<int> ConsumeWithHeaderAsync(HotPayload header, IAsyncEnumerable<HotPayload> values, CancellationToken cancellationToken)
    { return header.Value + await ConsumeAsync(values, cancellationToken); }
    public async IAsyncEnumerable<HotPayload> ProduceAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try { await Task.Yield(); yield return new HotPayload(Signals.Tag * 100); }
        finally
        {
            Signals.Observe?.Invoke("iterator-cleanup");
            if (Signals.StreamCleanup is { } cleanup) await cleanup();
            Signals.Observe?.Invoke("iterator-exited");
        }
    }
    public async IAsyncEnumerable<HotPayload> DuplexAsync(IAsyncEnumerable<HotPayload> values, [EnumeratorCancellation] CancellationToken cancellationToken)
    { await foreach (var value in values.WithCancellation(cancellationToken)) yield return new HotPayload(value.Value * 2); }
    public async ValueTask<int> HoldAsync(CancellationToken cancellationToken)
    {
        Signals.Observe?.Invoke("body");
        if (Signals.Body is { } body) await body(); // Deliberately non-cooperative physical work.
        Signals.Observe?.Invoke("body-exited");
        return Signals.Tag;
    }
    public async ValueTask DisposeAsync()
    {
        Signals.Observe?.Invoke("cleanup");
        if (Signals.Cleanup is { } cleanup) await cleanup();
        Signals.Observe?.Invoke("cleanup-exited");
    }
#if GROUP_CHANGED_HASH
    public ValueTask<int> ExtraAsync() => ValueTask.FromResult(Signals.Tag);
#endif
}

[RpcService(Lifetime = SharpLinkServiceLifetime.Call)]
public sealed class SecondHotService : ISecondHotRpc
{
    public ValueTask<int> GetSecondAsync()
    { Signals.Observe?.Invoke("second"); return ValueTask.FromResult(Signals.Tag); }
}


// Shared framework-only return types keep the test harness outside this collectible ALC.
// Calls inside this helper still use the real generated proxy and its exact local Type.
public static class LocalDriver
{
    public static object Create(ISharpLinkClient client, RpcProxyOptions? options) => client.CreateProxy<IHotRpc>(options);
    public static RpcLocalContract Local(ISharpLinkClient client) => client.GetLocalContract<IHotRpc>();
    public static ValueTask Prepare(object proxy, CancellationToken ct) => ((RpcProxy<IHotRpc>)proxy).PrepareAsync(TimeSpan.FromSeconds(5), ct);
    public static ValueTask<int> Unary(object proxy) => ((RpcProxy<IHotRpc>)proxy).Service.GetAsync();
    public static ValueTask<int> Echo(object proxy) => ((RpcProxy<IHotRpc>)proxy).Service.EchoAsync(new HotPayload(7));
    public static ValueTask<int> Consume(object proxy, IAsyncEnumerable<int> input, CancellationToken ct)
        => ((RpcProxy<IHotRpc>)proxy).Service.ConsumeAsync(Input(input, ct), ct);
    public static ValueTask<int> ConsumeWithHeader(object proxy, IAsyncEnumerable<int> input, CancellationToken ct)
        => ((RpcProxy<IHotRpc>)proxy).Service.ConsumeWithHeaderAsync(new HotPayload(7), Input(input, ct), ct);
    public static IAsyncEnumerable<int> Produce(object proxy, CancellationToken ct)
        => Output(((RpcProxy<IHotRpc>)proxy).Service.ProduceAsync(ct), ct);
    public static IAsyncEnumerable<int> Duplex(object proxy, IAsyncEnumerable<int> input, CancellationToken ct)
        => Output(((RpcProxy<IHotRpc>)proxy).Service.DuplexAsync(Input(input, ct), ct), ct);
    private static async IAsyncEnumerable<HotPayload> Input(IAsyncEnumerable<int> input, [EnumeratorCancellation] CancellationToken ct)
    {
        Signals.Observe?.Invoke("producer-entered");
        try { await foreach (var value in input.WithCancellation(ct)) yield return new HotPayload(value); }
        finally
        {
            Signals.Observe?.Invoke("producer-cleanup");
            if (Signals.ProducerCleanup is { } cleanup) await cleanup();
            Signals.Observe?.Invoke("producer-exited");
        }
    }
    private static async IAsyncEnumerable<int> Output(IAsyncEnumerable<HotPayload> input, [EnumeratorCancellation] CancellationToken ct)
    { await foreach (var value in input.WithCancellation(ct)) yield return value.Value; }
}
