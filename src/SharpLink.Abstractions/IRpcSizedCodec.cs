using System.ComponentModel;

namespace SharpLink.Abstractions;

/// <summary>
/// Marks a generated codec that can calculate the exact encoded size of a value.
/// The contract lives in the shared abstractions assembly so generated codecs from
/// different assemblies can recognize each other.
/// </summary>
public interface IRpcSizedCodec<T>
{
    /// <summary>
    /// Gets whether this codec can always calculate an exact encoded size for its value,
    /// including every nested generated codec, without traversing the value itself.
    /// </summary>
    bool CanExactSize { get; }

    /// <summary>
    /// Calculates the exact encoded size, including the DTO presence marker and terminator.
    /// Returns <see langword="false"/> when a nested member does not support exact sizing.
    /// </summary>
    bool TryGetEncodedSize(in T value, out int size);
}

/// <summary>
/// Thread-local suppression scope used by generated codecs after an ancestor has reserved
/// enough capacity for the complete object graph. Nested codecs skip their own reservation
/// and recursive size calculation while this scope is active.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class RpcGeneratedCodecSizing
{
    [ThreadStatic]
    private static int _suppressionDepth;

    /// <summary>Gets whether generated codec descendants should skip their own reservation.</summary>
    public static bool IsSuppressed => _suppressionDepth > 0;

    /// <summary>Enters one nested exact-size serialization scope.</summary>
    public static void Enter() => _suppressionDepth++;

    /// <summary>Exits one nested exact-size serialization scope.</summary>
    public static void Exit() => _suppressionDepth--;
}
