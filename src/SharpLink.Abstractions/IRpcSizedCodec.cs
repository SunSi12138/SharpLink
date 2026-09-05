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

    /// <summary>Calculates an exact size without retaining member values.</summary>
    bool TryGetEncodedSize(in T value, out int size);

    /// <summary>
    /// Calculates an exact size and captures the member values used for that calculation so the
    /// subsequent sized write can reuse them without evaluating stateful getters again.
    /// </summary>
    bool TryGetEncodedSize(in T value, out int size, out IRpcSizedCodecSnapshot? snapshot);

    /// <summary>
    /// Writes the value using member values captured by the snapshot overload.
    /// </summary>
    void SerializeSized(in T value, IBufferWriter<byte> buffer, int size, IRpcSizedCodecSnapshot? snapshot);

    /// <summary>Returns a captured snapshot to its generated pool.</summary>
    void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot);
}

/// <summary>Marker for a generated snapshot of member values captured during exact sizing.</summary>
public interface IRpcSizedCodecSnapshot
{
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
