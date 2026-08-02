using SharpLink.Sdk;

namespace SharpLink.SdkOnlyPackageSmoke;

[RpcContract]
public interface IEchoService : IService
{
    ValueTask<string> EchoAsync(string value, CancellationToken cancellationToken);
}
