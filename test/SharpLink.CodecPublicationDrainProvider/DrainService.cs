using SharpLink.Sdk;

namespace SharpLink.CodecPublicationDrainProvider;

public readonly record struct DrainPayload(int Value);

[RpcContract]
public interface IDrainService : IService
{
    ValueTask<int> BlockAsync(CancellationToken cancellationToken);
    ValueTask<DrainPayload> EchoAsync(DrainPayload value, CancellationToken cancellationToken);
}

[RpcService]
public sealed class DrainService : IDrainService
{
    private static readonly TaskCompletionSource StartedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TaskCompletionSource ReleaseSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static Task Started => StartedSignal.Task;
    public static void Release() => ReleaseSignal.TrySetResult();

    public async ValueTask<int> BlockAsync(CancellationToken cancellationToken)
    {
        StartedSignal.TrySetResult();
        await ReleaseSignal.Task.WaitAsync(cancellationToken);
        return 7;
    }

    public ValueTask<DrainPayload> EchoAsync(DrainPayload value, CancellationToken cancellationToken)
        => ValueTask.FromResult(value);
}
