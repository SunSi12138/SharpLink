using SharpLink.Sdk;

namespace SharpLink.DynamicPlugin;

[RpcContract]
public interface IMaintenanceNotificationProbe : IService
{
    ValueTask CompleteAsync(CancellationToken cancellationToken);

    [Oneway]
    ValueTask ConsumeAsync(IAsyncEnumerable<int> values, CancellationToken cancellationToken);

    [Timeout]
    ValueTask<int> ReleaseBeforeCancellationCallbackAsync(IAsyncEnumerable<int> values, CancellationToken cancellationToken);
}
