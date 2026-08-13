using System.Reflection;
using System.Text;

namespace SharpLink.IntegrationTests;

public class GeneratedStringPreReserveIntegrationTests
{
    private const string NonAsciiSeed = "汉🙂";

    [Test]
    [Arguments(1, 1024)]
    [Arguments(4, 1024)]
    [Arguments(16, 1024)]
    [Arguments(64, 1024)]
    [Arguments(1, 64 * 1024)]
    [Arguments(4, 64 * 1024)]
    [Arguments(16, 64 * 1024)]
    [Arguments(64, 64 * 1024)]
    [Arguments(1, 128 * 1024)]
    [Arguments(4, 128 * 1024)]
    [Arguments(16, 128 * 1024)]
    [Arguments(64, 128 * 1024)]
    public void GeneratedDirectStringsShouldPreReserveOnceAndRoundTrip(
        int fieldCount,
        int encodedBytes)
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build();

        switch (fieldCount)
        {
            case 1:
                VerifyBoundaryCase<PreReserveStrings1>(context, encodedBytes);
                break;
            case 4:
                VerifyBoundaryCase<PreReserveStrings4>(context, encodedBytes);
                break;
            case 16:
                VerifyBoundaryCase<PreReserveStrings16>(context, encodedBytes);
                break;
            case 64:
                VerifyBoundaryCase<PreReserveStrings64>(context, encodedBytes);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(fieldCount));
        }
    }

    [Test]
    public void GeneratedDirectStringsShouldPreserveBoundedWriterExhaustionThreshold()
    {
        const int encodedBytes = 1024;
        using var context = new SharpLinkRuntimeContextBuilder().Build();
        var codec = context.Codecs.GetCodec<PreReserveStrings1>();
        var payload = CreatePayload<PreReserveStrings1>(encodedBytes);
        using var pool = new SharpLinkBufferWriterPool(new BufferWriterPoolOptions
        {
            InitialCapacity = 1024,
            MaxPooledWriters = 1,
            MaxRetainedCapacityBytes = BufferWriterPoolOptions.MaximumRetainedCapacityBytes
        });

        var belowThreshold = pool.Rent(encodedBytes + 3);
        var failure = CaptureException(() => codec.Serialize(payload, belowThreshold));
        var failedWrittenCount = belowThreshold.WrittenCount;
        pool.Return(belowThreshold);

        var exactThreshold = pool.Rent(encodedBytes + 4);
        codec.Serialize(payload, exactThreshold);
        var successfulWrittenCount = exactThreshold.WrittenCount;
        pool.Return(exactThreshold);

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.ResourceExhausted },
            "a capacity below the existing five-byte varuint request threshold must remain ResourceExhausted");
        Ensure(failedWrittenCount == 0,
            "the up-front capacity request must reject an undersized bounded writer before partial serialization");
        Ensure(successfulWrittenCount == encodedBytes,
            "the existing encoded-size-plus-four threshold must still serialize the complete payload");
    }

    [Test]
    public void GeneratedDirectStringsShouldKeepStrictEncoderFailureSemantics()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build();
        var codec = context.Codecs.GetCodec<PreReserveStrings1>();
        using var writer = new PooledByteBufferWriter();

        var failure = CaptureException(() => codec.Serialize(
            new PreReserveStrings1 { Field01 = "\uD800" },
            writer));

        Ensure(failure is EncoderFallbackException,
            $"an isolated surrogate must still fail with EncoderFallbackException, not {failure?.GetType().Name}");
        Ensure(writer.WrittenCount == 0,
            "strict UTF-8 validation must complete before the generated DTO mutates the writer");
    }

    [Test]
    public void GeneratedNullableAndEmptyStringsShouldPreserveWireValues()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build();
        var codec = context.Codecs.GetCodec<PreReserveNullableStrings>();
        var payload = new PreReserveNullableStrings { Nullable = null, Empty = string.Empty };
        using var writer = new PooledByteBufferWriter(16);
        var tracking = new PreReserveTrackingWriter(writer);

        codec.Serialize(payload, tracking);
        var decoded = codec.Deserialize(new ReadOnlySequence<byte>(writer.WrittenMemory));

        Ensure(tracking.FirstSizeHint == 12,
            "presence, null key, empty-string key/prefix, terminator, and varuint slack must be pre-reserved exactly");
        Ensure(writer.WrittenCount == 8 && decoded is { Nullable: null, Empty.Length: 0 },
            "nullable and empty strings must retain their distinct wire representations");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void GeneratedStringsWithFixedAndNullableFixedMembersShouldUseExactSize(bool hasOptional)
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build();
        var codec = context.Codecs.GetCodec<PreReserveMixedDirectValues>();
        var payload = new PreReserveMixedDirectValues
        {
            Text = NonAsciiSeed,
            Number = 42,
            Optional = hasOptional ? 17 : null
        };
        using var writer = new PooledByteBufferWriter(16);
        var tracking = new PreReserveTrackingWriter(writer);

        codec.Serialize(payload, tracking);
        var decoded = codec.Deserialize(new ReadOnlySequence<byte>(writer.WrittenMemory));

        Ensure(tracking.FirstSizeHint == writer.WrittenCount + 4,
            "fixed and nullable-fixed members must participate in the exact capacity hint");
        Ensure(decoded is not null && decoded.Text == payload.Text && decoded.Number == 42 &&
               decoded.Optional == payload.Optional,
            "fixed and nullable-fixed values must retain their generated wire semantics");
    }

    private static void VerifyBoundaryCase<T>(SharpLinkRuntimeContext context, int encodedBytes)
        where T : class, new()
    {
        var codec = context.Codecs.GetCodec<T>();
        var payload = CreatePayload<T>(encodedBytes);
        using var writer = new PooledByteBufferWriter(1024);
        var tracking = new PreReserveTrackingWriter(writer);

        codec.Serialize(payload, tracking);
        var decoded = codec.Deserialize(new ReadOnlySequence<byte>(writer.WrittenMemory));

        Ensure(writer.WrittenCount == encodedBytes,
            $"{typeof(T).Name} must write the exact {encodedBytes}-byte wire payload");
        Ensure(tracking.FirstSizeHint == encodedBytes + 4,
            $"{typeof(T).Name} must request exact encoded bytes plus existing varuint request slack before writing");
        Ensure(tracking.GrowthCount == 1 && tracking.FirstGrowthWrittenCount == 0,
            $"{typeof(T).Name} must grow once before any bytes are written");
        Ensure(decoded is not null && StringPropertiesEqual(payload, decoded),
            $"{typeof(T).Name} must round-trip every direct string, including non-ASCII UTF-8");
    }

    private static T CreatePayload<T>(int encodedBytes) where T : class, new()
    {
        var properties = GetStringProperties(typeof(T));
        var framingBytes = 2;
        foreach (var property in properties)
        {
            var fieldId = property.GetCustomAttribute<RpcMemberAttribute>()!.Id;
            var key = checked(((uint)fieldId << 3) | (uint)RpcGeneratedWireType.LengthDelimited);
            framingBytes = checked(framingBytes + GetVarUInt32Size(key) + sizeof(uint));
        }

        var contentBytes = encodedBytes - framingBytes;
        Ensure(contentBytes >= properties.Length * Encoding.UTF8.GetByteCount(NonAsciiSeed),
            "the requested boundary must leave enough content for non-ASCII data in every field");
        var values = CreateUtf8Values(contentBytes, properties.Length);
        var payload = new T();
        for (var index = 0; index < properties.Length; index++)
            properties[index].SetValue(payload, values[index]);
        return payload;
    }

    private static string[] CreateUtf8Values(int contentBytes, int fieldCount)
    {
        var seedBytes = Encoding.UTF8.GetByteCount(NonAsciiSeed);
        var values = new string[fieldCount];
        var baseBytes = contentBytes / fieldCount;
        var remainder = contentBytes % fieldCount;
        for (var index = 0; index < values.Length; index++)
        {
            var fieldBytes = baseBytes + (index < remainder ? 1 : 0);
            values[index] = NonAsciiSeed + new string('x', fieldBytes - seedBytes);
        }
        return values;
    }

    private static bool StringPropertiesEqual<T>(T expected, T actual) where T : class
    {
        foreach (var property in GetStringProperties(typeof(T)))
        {
            if (!string.Equals(
                    (string?)property.GetValue(expected),
                    (string?)property.GetValue(actual),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private static PropertyInfo[] GetStringProperties(Type type)
        => type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(static property => property.PropertyType == typeof(string) &&
                                      property.GetCustomAttribute<RpcMemberAttribute>() is not null)
            .OrderBy(static property => property.GetCustomAttribute<RpcMemberAttribute>()!.Id)
            .ToArray();

    private static int GetVarUInt32Size(uint value)
        => value < 1U << 7 ? 1 :
            value < 1U << 14 ? 2 :
            value < 1U << 21 ? 3 :
            value < 1U << 28 ? 4 : 5;

    private static Exception? CaptureException(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class PreReserveTrackingWriter(PooledByteBufferWriter writer) : IRpcByteBufferWriter
    {
        public int FirstSizeHint { get; private set; } = -1;

        public int GrowthCount { get; private set; }

        public int FirstGrowthWrittenCount { get; private set; } = -1;

        public void Advance(int count) => writer.Advance(count);

        public int WrittenCount => writer.WrittenCount;

        public ReadOnlyMemory<byte> WrittenMemory => writer.WrittenMemory;

        public Span<byte> WrittenSpan => writer.WrittenSpan;

        public int Capacity => writer.Capacity;

        public void Clear() => writer.Clear();

        public void Dispose() => writer.Dispose();

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            RecordFirstHint(sizeHint);
            var previousCapacity = writer.Capacity;
            var writtenCount = writer.WrittenCount;
            var memory = writer.GetMemory(sizeHint);
            RecordGrowth(previousCapacity, writtenCount);
            return memory;
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            RecordFirstHint(sizeHint);
            var previousCapacity = writer.Capacity;
            var writtenCount = writer.WrittenCount;
            var span = writer.GetSpan(sizeHint);
            RecordGrowth(previousCapacity, writtenCount);
            return span;
        }

        private void RecordFirstHint(int sizeHint)
        {
            if (FirstSizeHint < 0)
                FirstSizeHint = sizeHint;
        }

        private void RecordGrowth(int previousCapacity, int writtenCount)
        {
            if (writer.Capacity == previousCapacity)
                return;
            if (GrowthCount == 0)
                FirstGrowthWrittenCount = writtenCount;
            GrowthCount++;
        }
    }
}

[RpcSerializable]
public sealed class PreReserveStrings1
{
    [RpcMember(1)] public string Field01 { get; set; } = string.Empty;
}

[RpcSerializable]
public sealed class PreReserveStrings4
{
    [RpcMember(1)] public string Field01 { get; set; } = string.Empty;
    [RpcMember(2)] public string Field02 { get; set; } = string.Empty;
    [RpcMember(3)] public string Field03 { get; set; } = string.Empty;
    [RpcMember(4)] public string Field04 { get; set; } = string.Empty;
}

[RpcSerializable]
public sealed class PreReserveStrings16
{
    [RpcMember(1)] public string Field01 { get; set; } = string.Empty;
    [RpcMember(2)] public string Field02 { get; set; } = string.Empty;
    [RpcMember(3)] public string Field03 { get; set; } = string.Empty;
    [RpcMember(4)] public string Field04 { get; set; } = string.Empty;
    [RpcMember(5)] public string Field05 { get; set; } = string.Empty;
    [RpcMember(6)] public string Field06 { get; set; } = string.Empty;
    [RpcMember(7)] public string Field07 { get; set; } = string.Empty;
    [RpcMember(8)] public string Field08 { get; set; } = string.Empty;
    [RpcMember(9)] public string Field09 { get; set; } = string.Empty;
    [RpcMember(10)] public string Field10 { get; set; } = string.Empty;
    [RpcMember(11)] public string Field11 { get; set; } = string.Empty;
    [RpcMember(12)] public string Field12 { get; set; } = string.Empty;
    [RpcMember(13)] public string Field13 { get; set; } = string.Empty;
    [RpcMember(14)] public string Field14 { get; set; } = string.Empty;
    [RpcMember(15)] public string Field15 { get; set; } = string.Empty;
    [RpcMember(16)] public string Field16 { get; set; } = string.Empty;
}

[RpcSerializable]
public sealed class PreReserveStrings64
{
    [RpcMember(1)] public string Field01 { get; set; } = string.Empty;
    [RpcMember(2)] public string Field02 { get; set; } = string.Empty;
    [RpcMember(3)] public string Field03 { get; set; } = string.Empty;
    [RpcMember(4)] public string Field04 { get; set; } = string.Empty;
    [RpcMember(5)] public string Field05 { get; set; } = string.Empty;
    [RpcMember(6)] public string Field06 { get; set; } = string.Empty;
    [RpcMember(7)] public string Field07 { get; set; } = string.Empty;
    [RpcMember(8)] public string Field08 { get; set; } = string.Empty;
    [RpcMember(9)] public string Field09 { get; set; } = string.Empty;
    [RpcMember(10)] public string Field10 { get; set; } = string.Empty;
    [RpcMember(11)] public string Field11 { get; set; } = string.Empty;
    [RpcMember(12)] public string Field12 { get; set; } = string.Empty;
    [RpcMember(13)] public string Field13 { get; set; } = string.Empty;
    [RpcMember(14)] public string Field14 { get; set; } = string.Empty;
    [RpcMember(15)] public string Field15 { get; set; } = string.Empty;
    [RpcMember(16)] public string Field16 { get; set; } = string.Empty;
    [RpcMember(17)] public string Field17 { get; set; } = string.Empty;
    [RpcMember(18)] public string Field18 { get; set; } = string.Empty;
    [RpcMember(19)] public string Field19 { get; set; } = string.Empty;
    [RpcMember(20)] public string Field20 { get; set; } = string.Empty;
    [RpcMember(21)] public string Field21 { get; set; } = string.Empty;
    [RpcMember(22)] public string Field22 { get; set; } = string.Empty;
    [RpcMember(23)] public string Field23 { get; set; } = string.Empty;
    [RpcMember(24)] public string Field24 { get; set; } = string.Empty;
    [RpcMember(25)] public string Field25 { get; set; } = string.Empty;
    [RpcMember(26)] public string Field26 { get; set; } = string.Empty;
    [RpcMember(27)] public string Field27 { get; set; } = string.Empty;
    [RpcMember(28)] public string Field28 { get; set; } = string.Empty;
    [RpcMember(29)] public string Field29 { get; set; } = string.Empty;
    [RpcMember(30)] public string Field30 { get; set; } = string.Empty;
    [RpcMember(31)] public string Field31 { get; set; } = string.Empty;
    [RpcMember(32)] public string Field32 { get; set; } = string.Empty;
    [RpcMember(33)] public string Field33 { get; set; } = string.Empty;
    [RpcMember(34)] public string Field34 { get; set; } = string.Empty;
    [RpcMember(35)] public string Field35 { get; set; } = string.Empty;
    [RpcMember(36)] public string Field36 { get; set; } = string.Empty;
    [RpcMember(37)] public string Field37 { get; set; } = string.Empty;
    [RpcMember(38)] public string Field38 { get; set; } = string.Empty;
    [RpcMember(39)] public string Field39 { get; set; } = string.Empty;
    [RpcMember(40)] public string Field40 { get; set; } = string.Empty;
    [RpcMember(41)] public string Field41 { get; set; } = string.Empty;
    [RpcMember(42)] public string Field42 { get; set; } = string.Empty;
    [RpcMember(43)] public string Field43 { get; set; } = string.Empty;
    [RpcMember(44)] public string Field44 { get; set; } = string.Empty;
    [RpcMember(45)] public string Field45 { get; set; } = string.Empty;
    [RpcMember(46)] public string Field46 { get; set; } = string.Empty;
    [RpcMember(47)] public string Field47 { get; set; } = string.Empty;
    [RpcMember(48)] public string Field48 { get; set; } = string.Empty;
    [RpcMember(49)] public string Field49 { get; set; } = string.Empty;
    [RpcMember(50)] public string Field50 { get; set; } = string.Empty;
    [RpcMember(51)] public string Field51 { get; set; } = string.Empty;
    [RpcMember(52)] public string Field52 { get; set; } = string.Empty;
    [RpcMember(53)] public string Field53 { get; set; } = string.Empty;
    [RpcMember(54)] public string Field54 { get; set; } = string.Empty;
    [RpcMember(55)] public string Field55 { get; set; } = string.Empty;
    [RpcMember(56)] public string Field56 { get; set; } = string.Empty;
    [RpcMember(57)] public string Field57 { get; set; } = string.Empty;
    [RpcMember(58)] public string Field58 { get; set; } = string.Empty;
    [RpcMember(59)] public string Field59 { get; set; } = string.Empty;
    [RpcMember(60)] public string Field60 { get; set; } = string.Empty;
    [RpcMember(61)] public string Field61 { get; set; } = string.Empty;
    [RpcMember(62)] public string Field62 { get; set; } = string.Empty;
    [RpcMember(63)] public string Field63 { get; set; } = string.Empty;
    [RpcMember(64)] public string Field64 { get; set; } = string.Empty;
}

[RpcSerializable]
public sealed class PreReserveNullableStrings
{
    [RpcMember(1)] public string? Nullable { get; set; }
    [RpcMember(2)] public string Empty { get; set; } = string.Empty;
}

[RpcSerializable]
public sealed class PreReserveMixedDirectValues
{
    [RpcMember(1)] public string Text { get; set; } = string.Empty;
    [RpcMember(2)] public int Number { get; set; }
    [RpcMember(16)] public int? Optional { get; set; }
}
