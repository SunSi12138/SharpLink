using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SharpPack;

namespace SharpLink.UnitTests.Runtime;

public class SharpPackCodecTests
{
    private static readonly (string Name, string Hex, Func<object?> Expected, Func<IRpcCodec> Codec)[] GoldenFixtures =
    [
        ("null-root", "FF", static () => null, static () => SharpPackRpcCodec.Create<SharpGoldenPayload>(new())),
        ("full-payload",
            "062A000000EFFFFFFF0C00000053686172704C696E6B2DE4B8ADE6968701000000070000000300000001000000020000000300000001000000FBFFFFFF040000006C616E67F9FFFFFF02000000E4B8ADE6968701F9FFFFFF060000006E6573746564",
            static () => new SharpGoldenPayload { Id = 42, Name = "SharpLink-中文", Optional = 7, Values = [1, 2, 3], Tags = new() { ["lang"] = "中文" }, Child = new() { Value = "nested" } },
            static () => SharpPackRpcCodec.Create<SharpGoldenPayload>(new())),
        ("empty-collections",
            "0600000000FFFFFFFF0000000000000000000000000000000001FFFFFFFF",
            static () => new SharpGoldenPayload { Values = [], Tags = [], Child = new() },
            static () => SharpPackRpcCodec.Create<SharpGoldenPayload>(new())),
        ("list-root", "03000000010000000200000003000000",
            static () => new List<int> { 1, 2, 3 },
            static () => SharpPackRpcCodec.Create<List<int>>(new())),
        ("union-polymorphism", "0002FBFFFFFF040000004D696C6F01",
            static () => (SharpGoldenAnimal)new SharpGoldenDog { Name = "Milo", Barks = true },
            static () => SharpPackRpcCodec.Create<SharpGoldenAnimal>(new())),
        ("circular-reference", "020C0200FBFFFFFF04000000726F6F74FA00",
            static () => { var node = new SharpGoldenNode { Name = "root" }; node.Next = node; return node; },
            static () => SharpPackRpcCodec.Create<SharpGoldenNode>(new()))
    ];

    [Test]
    public void MemoryPackGoldenPayloadsShouldReadAndWriteByteForByte()
    {
        foreach (var fixture in GoldenFixtures)
        {
            var bytes = Convert.FromHexString(fixture.Hex);
            var codec = fixture.Codec();
            var expected = fixture.Expected();
            var actual = Deserialize(codec, bytes);
            EnsureGoldenValue(fixture.Name, expected, actual);
            var writer = new ArrayBufferWriter<byte>();
            Serialize(codec, expected, writer);
            Ensure(writer.WrittenSpan.SequenceEqual(bytes), $"{fixture.Name} byte-for-byte payload");
        }
    }

    [Test]
    public async Task SharpPackCodecShouldRunConcurrentCallsAfterSharedStart()
    {
        var codec = SharpPackRpcCodec.Create<SharpGoldenPayload>(new SharpPackSerializerContext());
        const int workerCount = 64;
        var readyCount = 0;
        var allReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, workerCount).Select(index => Task.Run(async () =>
        {
            if (Interlocked.Increment(ref readyCount) == workerCount)
                allReady.SetResult();
            await start.Task;
            for (var iteration = 0; iteration < 64; iteration++)
            {
                var id = index * 1000 + iteration;
                var value = new SharpGoldenPayload { Id = id, Name = $"并发-{id}", Values = [id] };
                var writer = new ArrayBufferWriter<byte>();
                codec.Serialize(value, writer);
                var decoded = codec.Deserialize(new ReadOnlySequence<byte>(writer.WrittenMemory));
                Ensure(decoded is { Id: var actualId } && actualId == id && decoded.Name == value.Name,
                    $"roundtrip {id}");
            }
        })).ToArray();

        await allReady.Task;
        start.SetResult();
        await Task.WhenAll(tasks);
    }

    [Test]
    public void SharpPackAdapterScopesShouldOwnIsolatedFormatterGraphs()
    {
        using var firstScope = new SharpPackRpcCodecAdapter().CreateScope();
        using var secondScope = new SharpPackRpcCodecAdapter().CreateScope();
        var firstCodec = (SharpPackRpcCodec<SharpGoldenPayload>)firstScope.CreateCodec<SharpGoldenPayload>();
        var sameScopeCodec = (SharpPackRpcCodec<SharpGoldenPayload>)firstScope.CreateCodec<SharpGoldenPayload>();
        var secondCodec = (SharpPackRpcCodec<SharpGoldenPayload>)secondScope.CreateCodec<SharpGoldenPayload>();

        var firstFormatter = firstCodec.Context.GetFormatter<SharpGoldenPayload>();
        var sameScopeFormatter = sameScopeCodec.Context.GetFormatter<SharpGoldenPayload>();
        var secondFormatter = secondCodec.Context.GetFormatter<SharpGoldenPayload>();

        Ensure(ReferenceEquals(firstCodec.Context, sameScopeCodec.Context),
            "Codecs from one Adapter Scope share one serializer Context");
        Ensure(ReferenceEquals(firstFormatter, sameScopeFormatter),
            "Codecs from one Adapter Scope share one formatter graph");
        Ensure(!ReferenceEquals(firstCodec.Context, secondCodec.Context),
            "different Adapter Scopes own different serializer Contexts");
        Ensure(!ReferenceEquals(firstFormatter, secondFormatter),
            "different Adapter Scopes do not fall back to one process-wide formatter slot");
    }

    [Test]
    public void SharpPackCodecShouldRoundTripSingleAndMultiSegmentPayloads()
    {
        var codec = SharpPackRpcCodec.Create<SharpGoldenPayload>(new SharpPackSerializerContext());
        var value = new SharpGoldenPayload
        {
            Id = 91,
            Name = new string('界', 16_384),
            Values = Enumerable.Range(0, 4096).ToArray()
        };
        var writer = new ArrayBufferWriter<byte>();
        codec.Serialize(value, writer);

        var single = codec.Deserialize(new ReadOnlySequence<byte>(writer.WrittenMemory));
        var multi = codec.Deserialize(CreateMultiSegmentSequence(writer.WrittenMemory));

        Ensure(single is { Id: 91 } && single.Name == value.Name && single.Values!.SequenceEqual(value.Values),
            "single-segment large payload");
        Ensure(multi is { Id: 91 } && multi.Name == value.Name && multi.Values!.SequenceEqual(value.Values),
            "multi-segment large payload");
    }

    [Test]
    public void SharpPackCodecShouldRejectMalformedPayloadWithoutLeakingContent()
    {
        const string secret = "payload-secret-不得泄漏";
        var codec = SharpPackRpcCodec.Create<SharpGoldenPayload>(new SharpPackSerializerContext());
        var writer = new ArrayBufferWriter<byte>();
        codec.Serialize(new SharpGoldenPayload { Id = 7, Name = secret, Values = [1, 2, 3] }, writer);

        var truncated = ExpectDataLoss(() => codec.Deserialize(
            new ReadOnlySequence<byte>(writer.WrittenMemory[..^1])));
        var trailingBytes = writer.WrittenMemory.ToArray().Concat([byte.MaxValue]).ToArray();
        var trailing = ExpectDataLoss(() => codec.Deserialize(new ReadOnlySequence<byte>(trailingBytes)));
        var wrong = ExpectDataLoss(() => codec.Deserialize(new ReadOnlySequence<byte>([6, 1, 2])));

        Ensure(truncated.InnerException is not null, "truncated serializer failure keeps its cause");
        Ensure(trailing.Message.Contains("trailing bytes", StringComparison.Ordinal),
            "trailing bytes are rejected explicitly");
        Ensure(!truncated.Message.Contains(secret, StringComparison.Ordinal) &&
               !trailing.Message.Contains(secret, StringComparison.Ordinal) &&
               !wrong.Message.Contains(secret, StringComparison.Ordinal),
            "DataLoss messages do not include business payload content");
    }

    [Test]
    public void SharpPackCodecShouldNotWrapSharpLinkOrFatalExceptions()
    {
        Ensure(!SharpPackRpcCodec<SharpGoldenPayload>.ShouldWrap(
                new SharpLinkException(SharpLinkErrorCode.DataLoss, "already mapped")),
            "SharpLinkException is not wrapped again");
        Ensure(!SharpPackRpcCodec<SharpGoldenPayload>.ShouldWrap(new OutOfMemoryException()),
            "OutOfMemoryException is not wrapped");
        Ensure(!SharpPackRpcCodec<SharpGoldenPayload>.ShouldWrap(new StackOverflowException()),
            "StackOverflowException is not wrapped");
        Ensure(!SharpPackRpcCodec<SharpGoldenPayload>.ShouldWrap(
                new InvalidOperationException("serializer wrapper", new OutOfMemoryException())),
            "a serializer wrapper cannot hide a fatal inner exception as DataLoss");
        Ensure(!SharpPackRpcCodec<SharpGoldenPayload>.ShouldWrap(
                new InvalidOperationException("serializer wrapper", new OperationCanceledException())),
            "a serializer wrapper cannot hide cancellation as DataLoss");
        Ensure(!SharpPackRpcCodec<SharpGoldenPayload>.ShouldWrap(
                new AggregateException(
                    new InvalidOperationException("ordinary formatter failure"),
                    new OutOfMemoryException())),
            "a later AggregateException branch cannot hide a fatal exception as DataLoss");
        Ensure(SharpPackRpcCodec<SharpGoldenPayload>.ShouldWrap(new InvalidOperationException()),
            "ordinary formatter errors are mapped to DataLoss");
    }

    [Test]
    public void SharpPackCodecShouldNotWrapAccessViolationException()
    {
        var expected = new AccessViolationException("process-corrupting failure");
        var codec = SharpPackRpcCodec.Create<int>(new SharpPackSerializerContext());

        try
        {
            codec.Serialize(42, new ThrowingBufferWriter(expected));
            throw new Exception("expected AccessViolationException");
        }
        catch (AccessViolationException actual)
        {
            Ensure(ReferenceEquals(actual, expected), "fatal exception identity is preserved");
        }
    }

    [Test]
    public void SharpPackCodecShouldNotWrapCancellationException()
    {
        var expected = new OperationCanceledException("writer cancellation");
        var codec = SharpPackRpcCodec.Create<int>(new SharpPackSerializerContext());

        try
        {
            codec.Serialize(42, new ThrowingBufferWriter(expected));
            throw new Exception("expected OperationCanceledException");
        }
        catch (OperationCanceledException actual)
        {
            Ensure(ReferenceEquals(actual, expected), "cancellation exception identity is preserved");
        }
    }

    [Test]
    public void SharpPackAdapterScopeDisposeShouldBeIdempotentAndRejectCreation()
    {
        var scope = new SharpPackRpcCodecAdapter().CreateScope();
        var codec = scope.CreateCodec<SharpGoldenPayload>();
        scope.Dispose();
        scope.Dispose();

        try
        {
            _ = scope.CreateCodec<SharpGoldenPayload>();
            throw new Exception("expected disposed Adapter Scope to reject Codec creation");
        }
        catch (ObjectDisposedException)
        {
        }

        var writer = new ArrayBufferWriter<byte>();
        codec.Serialize(new SharpGoldenPayload { Id = 5 }, writer);
        Ensure(codec.Deserialize(new ReadOnlySequence<byte>(writer.WrittenMemory)) is { Id: 5 },
            "a previously published Codec remains valid for already leased calls");
    }

    [Test]
    public void SharpPackCodecShouldUseCallerCustomFormatterContext()
    {
        var formatter = new SharpExternalValueFormatter();
        var context = new SharpPackSerializerContextBuilder()
            .Register<SharpExternalValue>(formatter)
            .Build();
        var codec = SharpPackRpcCodec.Create<SharpExternalValue>(context);
        var writer = new ArrayBufferWriter<byte>();

        codec.Serialize(new SharpExternalValue { Value = 314 }, writer);
        var decoded = codec.Deserialize(new ReadOnlySequence<byte>(writer.WrittenMemory));

        Ensure(decoded is { Value: 314 }, "caller-provided formatter roundtrip");
        Ensure(formatter.SerializeCount == 1 && formatter.DeserializeCount == 1,
            "explicit Codec uses the caller-owned formatter Context");
    }

    private static object? Deserialize(IRpcCodec codec, byte[] bytes) => codec switch
    {
        IRpcCodec<SharpGoldenPayload> typed => typed.Deserialize(new ReadOnlySequence<byte>(bytes)),
        IRpcCodec<List<int>> typed => typed.Deserialize(new ReadOnlySequence<byte>(bytes)),
        IRpcCodec<SharpGoldenAnimal> typed => typed.Deserialize(new ReadOnlySequence<byte>(bytes)),
        IRpcCodec<SharpGoldenNode> typed => typed.Deserialize(new ReadOnlySequence<byte>(bytes)),
        _ => throw new InvalidOperationException("Unknown golden Codec type.")
    };

    private static void Serialize(IRpcCodec codec, object? value, IBufferWriter<byte> writer)
    {
        switch (codec)
        {
            case IRpcCodec<SharpGoldenPayload> typed:
                typed.Serialize((SharpGoldenPayload?)value!, writer);
                break;
            case IRpcCodec<SharpGoldenAnimal> typed:
                typed.Serialize((SharpGoldenAnimal?)value!, writer);
                break;
            case IRpcCodec<List<int>> typed:
                typed.Serialize((List<int>?)value!, writer);
                break;
            case IRpcCodec<SharpGoldenNode> typed:
                typed.Serialize((SharpGoldenNode?)value!, writer);
                break;
            default:
                throw new InvalidOperationException("Unknown golden Codec type.");
        }
    }

    private static void EnsureGoldenValue(string name, object? expected, object? actual)
    {
        switch (expected, actual)
        {
            case (null, null): return;
            case (SharpGoldenPayload left, SharpGoldenPayload right):
                Ensure(left.Id == right.Id && left.Name == right.Name && left.Optional == right.Optional, name);
                Ensure((left.Values ?? []).SequenceEqual(right.Values ?? []), name + " values");
                Ensure((left.Tags ?? []).OrderBy(static pair => pair.Key).SequenceEqual((right.Tags ?? []).OrderBy(static pair => pair.Key)), name + " tags");
                Ensure(left.Child?.Value == right.Child?.Value, name + " child");
                return;
            case (SharpGoldenDog left, SharpGoldenDog right):
                Ensure(left.Name == right.Name && left.Barks == right.Barks, name);
                return;
            case (List<int> left, List<int> right):
                Ensure(left.SequenceEqual(right), name);
                return;
            case (SharpGoldenNode left, SharpGoldenNode right):
                Ensure(left.Name == right.Name && ReferenceEquals(right, right.Next), name);
                return;
            default: throw new Exception($"{name} unexpected value shape");
        }
    }

    private static SharpLinkException ExpectDataLoss(Action action)
    {
        try { action(); }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.DataLoss) { return exception; }
        throw new Exception("Expected SharpLink DataLoss.");
    }

    private static ReadOnlySequence<byte> CreateMultiSegmentSequence(ReadOnlyMemory<byte> payload)
    {
        var firstLength = payload.Length / 3;
        var secondLength = payload.Length / 3;
        var first = new SequenceSegment(payload[..firstLength]);
        var second = first.Append(payload.Slice(firstLength, secondLength));
        var third = second.Append(payload[(firstLength + secondLength)..]);
        return new ReadOnlySequence<byte>(first, 0, third, third.Memory.Length);
    }

    private sealed class SequenceSegment : ReadOnlySequenceSegment<byte>
    {
        internal SequenceSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        internal SequenceSegment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new SequenceSegment(memory)
            {
                RunningIndex = RunningIndex + Memory.Length
            };
            Next = next;
            return next;
        }
    }

    private sealed class ThrowingBufferWriter(Exception exception) : IBufferWriter<byte>
    {
        public void Advance(int count) => throw exception;
        public Memory<byte> GetMemory(int sizeHint = 0) => throw exception;
        public Span<byte> GetSpan(int sizeHint = 0) => throw exception;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}

[SharpPackable]
public partial class SharpGoldenPayload
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public int? Optional { get; set; }
    public int[]? Values { get; set; }
    public Dictionary<string, string>? Tags { get; set; }
    public SharpGoldenChild? Child { get; set; }
}

[SharpPackable]
public partial class SharpGoldenChild { public string? Value { get; set; } }

[SharpPackable]
[SharpPackUnion(0, typeof(SharpGoldenDog))]
[SharpPackUnion(1, typeof(SharpGoldenCat))]
public partial interface SharpGoldenAnimal { }
[SharpPackable] public partial class SharpGoldenDog : SharpGoldenAnimal { public string Name { get; set; } = ""; public bool Barks { get; set; } }
[SharpPackable] public partial class SharpGoldenCat : SharpGoldenAnimal { public string Name { get; set; } = ""; }

[SharpPackable(SharpPack.GenerateType.CircularReference)]
public partial class SharpGoldenNode
{
    [SharpPackOrder(0)] public string Name { get; set; } = "";
    [SharpPackOrder(1)] public SharpGoldenNode? Next { get; set; }
}

public sealed class SharpExternalValue
{
    public int Value { get; set; }
}

public sealed class SharpExternalValueFormatter : SharpPackFormatter<SharpExternalValue>
{
    public int SerializeCount { get; private set; }
    public int DeserializeCount { get; private set; }

    public override void Serialize<TBufferWriter>(
        ref SharpPackWriter<TBufferWriter> writer,
        scoped ref SharpExternalValue? value)
    {
        SerializeCount++;
        if (value is null)
        {
            writer.WriteNullObjectHeader();
            return;
        }
        writer.WriteObjectHeader(1);
        writer.WriteUnmanaged(value.Value);
    }

    public override void Deserialize(ref SharpPackReader reader, scoped ref SharpExternalValue? value)
    {
        DeserializeCount++;
        if (!reader.TryReadObjectHeader(out var count))
        {
            value = null;
            return;
        }
        if (count != 1)
            throw new InvalidOperationException("Unexpected custom formatter field count.");
        value = new SharpExternalValue { Value = reader.ReadUnmanaged<int>() };
    }
}
