using SharpLink.Runtime;

namespace SharpLink.Hosting;

/// <summary>Exposes the anonymous-pipe allocator registered by a hosted server.</summary>
public interface IAnonymousPipeAllocatorAccessor
{
    /// <summary>Gets the allocator, or <see langword="null"/> when the server uses another transport.</summary>
    IAnonymousPipeAllocator? AnonymousPipeAllocator { get; }
}
