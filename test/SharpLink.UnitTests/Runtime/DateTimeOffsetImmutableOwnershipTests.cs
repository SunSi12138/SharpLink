using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace SharpLink.UnitTests.Runtime;

public sealed class DateTimeOffsetImmutableOwnershipTests
{
    [Test]
    public void DefaultAndEmptyShouldRemainDistinct()
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, -1);
        var absent = DateTimeOffsetImmutableArrayCodec.Instance.Deserialize(new ReadOnlySequence<byte>(bytes));
        Array.Clear(bytes);
        var empty = DateTimeOffsetImmutableArrayCodec.Instance.Deserialize(new ReadOnlySequence<byte>(bytes));
        if (!absent.IsDefault || empty.IsDefault || !empty.IsEmpty)
            throw new InvalidOperationException("Default and empty collection semantics changed.");
    }

    [Test]
    public void ResultsShouldNotAliasInputOrOtherDecodes()
    {
        var bytes = new byte[20];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 1);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(12), DateTime.UnixEpoch.Ticks);
        var first = DateTimeOffsetImmutableArrayCodec.Instance.Deserialize(new ReadOnlySequence<byte>(bytes));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(12), DateTime.UnixEpoch.Ticks + 1);
        var second = DateTimeOffsetImmutableArrayCodec.Instance.Deserialize(new ReadOnlySequence<byte>(bytes));
        if (first[0].UtcTicks != DateTime.UnixEpoch.Ticks || second[0].UtcTicks != DateTime.UnixEpoch.Ticks + 1)
            throw new InvalidOperationException("Result aliases mutable input.");
        if (ReferenceEquals(ImmutableCollectionsMarshal.AsArray(first), ImmutableCollectionsMarshal.AsArray(second)))
            throw new InvalidOperationException("Independent decodes share storage.");
    }
}
