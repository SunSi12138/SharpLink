using System.Buffers;
using System.Buffers.Binary;
using SharpLink.Abstractions;
using SharpLink.Sdk;

namespace SharpLink.BindingSlice;

[RpcContract]
public interface IOneWayInputRpc : IService
{
    [Oneway, Timeout] ValueTask FireAsync(OneWayValue header, IAsyncEnumerable<OneWayValue> values, CancellationToken cancellationToken);
    ValueTask<int> BarrierAsync(CancellationToken cancellationToken);
    ValueTask HoldAsync(CancellationToken cancellationToken);
}
[RpcCodec(typeof(OneWayValueCodec))]
public readonly record struct OneWayValue(int Value);
[RpcCodecSemanticIdentity(0x77561UL, 0x77562UL)]
public sealed class OneWayValueCodec : IRpcCodec<OneWayValue>
{
    internal static int Decodes;
    public void Serialize(in OneWayValue value, IBufferWriter<byte> writer)
    {
        var bytes = writer.GetSpan(256)[..256]; bytes.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value.Value); writer.Advance(256);
    }
    public OneWayValue Deserialize(in ReadOnlySequence<byte> bytes)
    {
        Interlocked.Increment(ref Decodes);
        if (bytes.Length != 256) throw new InvalidDataException("Wrong typed payload size.");
        var reader = new SequenceReader<byte>(bytes); reader.TryReadLittleEndian(out int value); return new(value);
    }
}
public sealed class OneWayInputService : IOneWayInputRpc
{
    internal int Calls, Sum;
    internal RpcDeadline Deadline;
    internal bool ReturnEarly, HoldBusiness;
    internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async ValueTask FireAsync(OneWayValue header, IAsyncEnumerable<OneWayValue> values, CancellationToken cancellationToken)
    {
        Deadline = SharpLinkCallContext.Current!.LocalRpcDeadline;
        Interlocked.Increment(ref Calls); Sum = header.Value; Entered.TrySetResult();
        if (HoldBusiness) await Release.Task.WaitAsync(cancellationToken);
        if (!ReturnEarly) await foreach (var value in values.WithCancellation(cancellationToken)) Sum += value.Value;
        Finished.TrySetResult();
    }
    public ValueTask<int> BarrierAsync(CancellationToken cancellationToken) => ValueTask.FromResult(42);
    public async ValueTask HoldAsync(CancellationToken cancellationToken)
    { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
}
