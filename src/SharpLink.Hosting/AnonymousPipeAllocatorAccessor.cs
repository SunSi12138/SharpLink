using SharpLink.Runtime;

namespace SharpLink.Hosting;

internal sealed class AnonymousPipeAllocatorAccessor(SharpLinkServerBuilder builder) : IAnonymousPipeAllocatorAccessor
{
    private IAnonymousPipeAllocator? _allocator;

    public IAnonymousPipeAllocator? AnonymousPipeAllocator =>
        builder.Transport as IAnonymousPipeAllocator ?? Volatile.Read(ref _allocator);

    // Build clears the builder's transport. Retain only a non-owning reference;
    // the server or the failed build transaction remains responsible for cleanup.
    internal void CaptureTransport() =>
        Volatile.Write(ref _allocator, builder.Transport as IAnonymousPipeAllocator);
}
