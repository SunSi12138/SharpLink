using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpLink.Runtime;

namespace SharpLink.UnitTests.Runtime;

public class DateTimeWireSemanticsEvidenceTests
{
    [Test]
    public void ScalarAndCollectionDateTimeUseDifferentPhysicalContracts()
    {
        var value = new DateTime(2024, 1, 15, 12, 34, 56, DateTimeKind.Local);

        var scalarWriter = new ArrayBufferWriter<byte>();
        DateTimeCodec.Instance.Serialize(in value, scalarWriter);
        if (scalarWriter.WrittenCount != sizeof(long))
            throw new Exception("scalar DateTime payload should contain exactly one Int64");
        var scalarBits = MemoryMarshal.Read<long>(scalarWriter.WrittenSpan);
        if (scalarBits != value.ToBinary())
            throw new Exception("scalar DateTimeCodec should encode DateTime.ToBinary()");

        var collectionWriter = new ArrayBufferWriter<byte>();
        DateTime[] values = [value];
        BlitArrayCodec<DateTime>.Instance.Serialize(in values, collectionWriter);
        if (collectionWriter.WrittenCount != sizeof(int) + sizeof(long))
            throw new Exception("single-element DateTime array should contain length plus native DateTime bits");

        var rawValue = value;
        var nativeBits = Unsafe.As<DateTime, long>(ref rawValue);
        var collectionBits = MemoryMarshal.Read<long>(collectionWriter.WrittenSpan[sizeof(int)..]);
        if (collectionBits != nativeBits)
            throw new Exception("DateTime collection should encode the native DateTime representation");

        // On a non-UTC local zone this also makes the byte-level difference directly observable.
        // UTC CI hosts can legitimately produce equal numeric bits while still exercising two
        // distinct contracts (ToBinary versus raw DateTime memory).
        if (TimeZoneInfo.Local.GetUtcOffset(value) != TimeSpan.Zero && scalarBits == collectionBits)
            throw new Exception("non-UTC Local DateTime should expose the scalar/collection encoding split");
    }

    [Test]
    public void ScalarAndCollectionDateTimeRoundTripTheirCurrentSameProcessSemantics()
    {
        var value = new DateTime(2024, 7, 1, 8, 9, 10, DateTimeKind.Local);

        var scalarWriter = new ArrayBufferWriter<byte>();
        DateTimeCodec.Instance.Serialize(in value, scalarWriter);
        var scalarRoundTrip = DateTimeCodec.Instance.Deserialize(
            new ReadOnlySequence<byte>(scalarWriter.WrittenMemory));

        var collectionWriter = new ArrayBufferWriter<byte>();
        DateTime[] values = [value];
        BlitArrayCodec<DateTime>.Instance.Serialize(in values, collectionWriter);
        var collectionRoundTrip = BlitArrayCodec<DateTime>.Instance.Deserialize(
            new ReadOnlySequence<byte>(collectionWriter.WrittenMemory));

        if (scalarRoundTrip != value || scalarRoundTrip.Kind != value.Kind)
            throw new Exception("scalar DateTime should preserve its current same-process contract");
        if (collectionRoundTrip is not { Length: 1 } ||
            collectionRoundTrip[0] != value ||
            collectionRoundTrip[0].Kind != value.Kind)
        {
            throw new Exception("DateTime collection should preserve its current same-process raw-bit contract");
        }
    }
}
