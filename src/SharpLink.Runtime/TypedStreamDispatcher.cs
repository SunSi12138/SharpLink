namespace SharpLink.Runtime;

public class TypedStreamDispatcher<T>(ChannelWriter<T> writer, ISerializer serializer) : IStreamDispatcher
{
    public ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
    {
        var item = serializer.Deserialize<T>(ref payload);
        return writer.TryWrite(item!) ? ValueTask.CompletedTask : writer.WriteAsync(item!);
    }

    public void Complete(bool isError, string? msg)
    {
        writer.TryComplete(isError ? new Exception(msg) : null);
    }
}
