using SharpLink.ReferenceRooting.PackageContracts;
using SharpLink.Sdk;

namespace SharpLink.ReferenceRooting.PackageServices;

[RpcService]
internal sealed class ReferencedPackageService : IReferencedPackageService
{
    public ReferencedPackageService()
    {
    }

    public ValueTask<string> IdentifyAsync(int value)
        => ValueTask.FromResult($"package-service:{value + 1}");
}
