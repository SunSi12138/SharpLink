using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SharpLink.UnitTests.Validation;

// Process entry points selected only by eng/validate-codec-semantics.py. Explicit
// prevents a process-global TZ experiment or microbenchmark joining normal tests.
[Explicit]
public sealed class CodecValidationProbe
{
    [Test]
    public void DateTimeCrossZone()
    {
        using var provider = new RpcCodecProvider(null, new Dictionary<Type, IRpcCodec>());
        var scalarCodec = provider.GetCodec<DateTime>();
        var nullableCodec = provider.GetCodec<DateTime?>();
        var arrayCodec = provider.GetCodec<DateTime[]>();
        var listCodec = provider.GetCodec<List<DateTime>>();
        var memoryCodec = provider.GetCodec<Memory<DateTime>>();
        var readOnlyMemoryCodec = provider.GetCodec<ReadOnlyMemory<DateTime>>();
        var immutableArrayCodec = provider.GetCodec<ImmutableArray<DateTime>>();
        var input = Environment.GetEnvironmentVariable("SHARPLINK_CODEC_INPUT");
        var dateCase = Environment.GetEnvironmentVariable("SHARPLINK_DATE_CASE") ?? "normal";
        if (string.IsNullOrEmpty(input))
        {
            var kind = Enum.Parse<DateTimeKind>(Environment.GetEnvironmentVariable("SHARPLINK_DATE_KIND")!);
            var value = dateCase switch
            {
                "normal" => new DateTime(2026, 1, 15, 12, 34, 56, kind),
                "max-local" when kind == DateTimeKind.Local =>
                    new DateTime(DateTime.MaxValue.Ticks - TimeSpan.TicksPerHour, DateTimeKind.Local),
                _ => throw new InvalidOperationException($"Unsupported DateTime validation case '{dateCase}' for {kind}.")
            };
            var scalar = Encode(scalarCodec, value);
            var nullable = Encode(nullableCodec, (DateTime?)value);
            var array = Encode(arrayCodec, new[] { value });
            var list = Encode(listCodec, new List<DateTime> { value });
            var memory = Encode(memoryCodec, new[] { value }.AsMemory());
            var readOnlyMemory = Encode(readOnlyMemoryCodec, new ReadOnlyMemory<DateTime>(new[] { value }));
            var immutableArray = Encode(immutableArrayCodec, ImmutableArray.Create(value));
            var scalarResult = Decode(scalarCodec, scalar);
            var nullableResult = Decode(nullableCodec, nullable)
                ?? throw new InvalidOperationException("Valid nullable DateTime decoded as null.");
            var arrayResult = Decode(arrayCodec, array)[0];
            var listResult = Decode(listCodec, list)[0];
            var memoryResult = Decode(memoryCodec, memory).Span[0];
            var readOnlyMemoryResult = Decode(readOnlyMemoryCodec, readOnlyMemory).Span[0];
            var immutableArrayResult = Decode(immutableArrayCodec, immutableArray)[0];
            PendingLifecycleValidationProbe.Write(new
            {
                phase = "complete",
                operation = "write",
                dateCase,
                zone = TimeZoneInfo.Local.Id,
                offsetTicks = TimeZoneInfo.Local.GetUtcOffset(value).Ticks,
                source = Snapshot(value),
                payloads = new { scalar, nullable, array, list, memory, readOnlyMemory, immutableArray },
                codecs = new
                {
                    scalar = scalarCodec.GetType().FullName,
                    nullable = nullableCodec.GetType().FullName,
                    array = arrayCodec.GetType().FullName,
                    list = listCodec.GetType().FullName,
                    memory = memoryCodec.GetType().FullName,
                    readOnlyMemory = readOnlyMemoryCodec.GetType().FullName,
                    immutableArray = immutableArrayCodec.GetType().FullName
                },
                invariant = Same(value, scalarResult) && Same(value, nullableResult) &&
                    Same(value, arrayResult) && Same(value, listResult) && Same(value, memoryResult) &&
                    Same(value, readOnlyMemoryResult) && Same(value, immutableArrayResult)
            });
            return;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(input));
        var root = document.RootElement;
        var payloads = root.GetProperty("payloads");
        var decodedScalar = Decode(scalarCodec, payloads.GetProperty("scalar").GetString()!);
        var decodedNullable = Decode(nullableCodec, payloads.GetProperty("nullable").GetString()!)
            ?? throw new InvalidOperationException("Valid nullable DateTime decoded as null.");
        var decodedArray = Decode(arrayCodec, payloads.GetProperty("array").GetString()!)[0];
        var decodedList = Decode(listCodec, payloads.GetProperty("list").GetString()!)[0];
        var decodedMemory = Decode(memoryCodec, payloads.GetProperty("memory").GetString()!).Span[0];
        var decodedReadOnlyMemory = Decode(readOnlyMemoryCodec, payloads.GetProperty("readOnlyMemory").GetString()!).Span[0];
        var decodedImmutableArray = Decode(immutableArrayCodec, payloads.GetProperty("immutableArray").GetString()!)[0];
        PendingLifecycleValidationProbe.Write(new
        {
            phase = "complete",
            operation = "read",
            dateCase,
            sourceZone = root.GetProperty("zone").GetString(),
            zone = TimeZoneInfo.Local.Id,
            offsetTicks = TimeZoneInfo.Local.GetUtcOffset(decodedScalar).Ticks,
            source = root.GetProperty("source"),
            scalar = Snapshot(decodedScalar),
            nullable = Snapshot(decodedNullable),
            array = Snapshot(decodedArray),
            list = Snapshot(decodedList),
            memory = Snapshot(decodedMemory),
            readOnlyMemory = Snapshot(decodedReadOnlyMemory),
            immutableArray = Snapshot(decodedImmutableArray),
            invariant = Same(decodedScalar, decodedNullable) && Same(decodedScalar, decodedArray) &&
                Same(decodedScalar, decodedList) && Same(decodedScalar, decodedMemory) &&
                Same(decodedScalar, decodedReadOnlyMemory) && Same(decodedScalar, decodedImmutableArray)
        });
    }

    [Test]
    public void DateTimeOffsetFragmentation()
    {
        using var provider = new RpcCodecProvider(null, new Dictionary<Type, IRpcCodec>());
        var arrayCodec = provider.GetCodec<DateTimeOffset[]>();
        var listCodec = provider.GetCodec<List<DateTimeOffset>>();
        var measurements = new List<object>();
        foreach (var count in new[] { 64, 256, 1024 })
        {
            var values = Enumerable.Range(0, count).Select(index =>
                new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.FromMinutes((index % 9 - 4) * 15))
                    .AddTicks(index)).ToArray();
            var list = values.ToList();
            var arrayBytes = Convert.FromBase64String(Encode(arrayCodec, values));
            var listBytes = Convert.FromBase64String(Encode(listCodec, list));
            PendingLifecycleValidationProbe.Require(arrayBytes.AsSpan().SequenceEqual(listBytes),
                "Array/List input bytes differ; performance comparison is not controlled.");
            foreach (var fragmentSize in new[] { arrayBytes.Length, 64, 7, 1 })
            {
                var sequence = Fragment(arrayBytes, fragmentSize);
                measurements.Add(Measure(arrayCodec, values, sequence, count, fragmentSize, "array"));
                measurements.Add(Measure(listCodec, values, sequence, count, fragmentSize, "list"));
            }
        }
        PendingLifecycleValidationProbe.Write(new
        {
            phase = "complete",
            invariant = true,
            runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            processorCount = Environment.ProcessorCount,
            timerFrequency = Stopwatch.Frequency,
            measurements,
            note = "Release microbenchmark of current implementation only; no candidate optimization or end-to-end speedup is measured."
        });
    }

    private static object Measure<T>(IRpcCodec<T> codec, DateTimeOffset[] expected,
        ReadOnlySequence<byte> sequence, int count, int fragmentSize, string collection) where T : class
    {
        // Input creation and correctness checks are OUTSIDE the measured interval.
        // Deserialize takes the complete sequence by 'in' and enforces exact size;
        // this API has no consumed-position to advance or report.
        var inputLength = sequence.Length;
        var first = codec.Deserialize(sequence)
            ?? throw new InvalidOperationException("Valid non-null collection decoded as null.");
        Check(first, expected);
        PendingLifecycleValidationProbe.Require(sequence.Length == inputLength, "input sequence was modified");
        for (var warmup = 0; warmup < 3; warmup++)
            Check(codec.Deserialize(sequence), expected);
        const int samples = 7;
        var iterations = count <= 64 ? 32 : count <= 256 ? 8 : 3;
        var times = new double[samples];
        var allocations = new double[samples];
        T last = first;
        for (var sample = 0; sample < samples; sample++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            for (var iteration = 0; iteration < iterations; iteration++)
                last = codec.Deserialize(sequence)
                    ?? throw new InvalidOperationException("Valid non-null collection decoded as null.");
            var elapsed = Stopwatch.GetTimestamp() - started;
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            times[sample] = elapsed * (1_000_000_000.0 / Stopwatch.Frequency) / iterations;
            allocations[sample] = (double)bytes / iterations;
        }
        Check(last, expected);
        GC.KeepAlive(first);
        var sortedTimes = times.Order().ToArray();
        return new
        {
            collection,
            count,
            fragmentSize,
            segmentCount = (inputLength + fragmentSize - 1) / fragmentSize,
            inputLength,
            samples,
            iterations,
            codec = codec.GetType().FullName,
            medianNanoseconds = sortedTimes[samples / 2],
            minNanoseconds = sortedTimes[0],
            maxNanoseconds = sortedTimes[^1],
            nanosecondsPerSample = times,
            allocatedBytesPerOperation = allocations.Order().ElementAt(samples / 2),
            exactRoundtrip = true
        };
    }

    private static void Check<T>(T actual, DateTimeOffset[] expected)
    {
        if (actual is not IReadOnlyList<DateTimeOffset> values || values.Count != expected.Length)
            throw new InvalidOperationException("DateTimeOffset collection shape changed.");
        for (var index = 0; index < expected.Length; index++)
            PendingLifecycleValidationProbe.Require(values[index].EqualsExact(expected[index]),
                $"DateTimeOffset instant/offset mismatch at {index}.");
    }

    private static ReadOnlySequence<byte> Fragment(byte[] bytes, int size)
    {
        if (size >= bytes.Length) return new ReadOnlySequence<byte>(bytes);
        var first = new Segment(bytes.AsMemory(0, Math.Min(size, bytes.Length)));
        var last = first;
        for (var offset = size; offset < bytes.Length; offset += size)
            last = last.Append(bytes.AsMemory(offset, Math.Min(size, bytes.Length - offset)));
        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private static string Encode<T>(IRpcCodec<T> codec, T value)
    {
        var writer = new ArrayBufferWriter<byte>();
        codec.Serialize(in value, writer);
        return Convert.ToBase64String(writer.WrittenSpan);
    }

    private static T Decode<T>(IRpcCodec<T> codec, string encoded)
    {
        var sequence = new ReadOnlySequence<byte>(Convert.FromBase64String(encoded));
        var result = codec.Deserialize(sequence);
        if (result is null)
            throw new InvalidOperationException("Valid non-null value decoded as null.");
        return result;
    }

    private static bool Same(DateTime left, DateTime right)
        => left.Ticks == right.Ticks && left.Kind == right.Kind;

    private static object Snapshot(DateTime value)
        => new { ticks = value.Ticks, kind = value.Kind.ToString(), utcTicks = value.ToUniversalTime().Ticks };

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        internal Segment(ReadOnlyMemory<byte> memory) => Memory = memory;
        internal Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
