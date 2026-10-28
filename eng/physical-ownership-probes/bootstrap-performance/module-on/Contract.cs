using SharpLink.Sdk;
using System.Runtime.CompilerServices;
namespace PhysicalTcp;
[RpcContract]
public interface IScalarStream : IService {
    [SharpLink.Sdk.Timeout(30)]
    IAsyncEnumerable<int> DownloadAsync(int count, CancellationToken cancellationToken);
}
[RpcService]
public sealed class ScalarStreamService : IScalarStream {
    public async IAsyncEnumerable<int> DownloadAsync(int count, [EnumeratorCancellation] CancellationToken cancellationToken) {
        for (int i=0;i<count;i++) { cancellationToken.ThrowIfCancellationRequested(); yield return i; await Task.CompletedTask; }
    }
}
