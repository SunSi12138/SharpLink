using SharpLink.Runtime;

namespace SharpLink.Hosting;

internal sealed class AnonymousPipeAllocatorAccessor:IAnonymousPipeAllocatorAccessor
{
    public IAnonymousPipeAllocator? AnonymousPipeAllocator { get; init; }
}