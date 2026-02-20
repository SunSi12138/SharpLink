using SharpLink.Runtime;

namespace SharpLink.Hosting;

public interface IAnonymousPipeAllocatorAccessor
{
    IAnonymousPipeAllocator? AnonymousPipeAllocator { get; }
}