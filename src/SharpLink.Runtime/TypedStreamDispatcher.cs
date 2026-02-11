namespace SharpLink.Runtime;

public class TypedStreamDispatcher<T>(ChannelWriter<T> writer, ISerializer serializer) : IStreamDispatcher
{
    public async ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
    {
        var item = serializer.Deserialize<T>(ref payload);
        await writer.WriteAsync(item!);
    }

    public void Complete(bool isError, string? msg)
    {
        writer.TryComplete(isError ? new Exception(msg) : null);
    }
}
