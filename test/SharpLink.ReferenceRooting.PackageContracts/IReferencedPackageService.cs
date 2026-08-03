using SharpLink.Sdk;

namespace SharpLink.ReferenceRooting.PackageContracts;

[RpcContract]
public interface IReferencedPackageService : IService
{
    [NonCancellable]
    ValueTask<string> IdentifyAsync(int value);
}
