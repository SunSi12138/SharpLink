using System.Buffers.Binary;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace SharpLink.UnitTests.Validation;

// Isolated A/B evidence for #559. The candidate side always uses the real provider/runtime codec;
// the baseline side is a test-only copy of the pre-#559 collection decode algorithm so both run
// in the same process on the same payloads and runner. Timing values are evidence, never CI gates.
[Explicit]
public sealed class DateTimeOffsetPerformanceValidationProbe
{
    [Test]
    public void Run()
    {
        using var provider = new RpcCodecProvider(null, new Dictionary<Type, IRpcCodec>());
        var candidateArray = provider.GetCodec<DateTimeOffset[]>();
        var candidateList = provider.GetCodec<List<DateTimeOffset>>();
        IRpcCodec<DateTimeOffset[]?> baselineArray = new BaselineArrayCodec();
        IRpcCodec<List<DateTimeOffset>?> baselineList = new BaselineListCodec();
        var candidateMeasurements = new List<Measurement>();
        var baselineMeasurements = new List<Measurement>();
        var comparisons = new List<object>();

        foreach (var count in new[] { 64, 256, 1024 })
        {
            var values = Enumerable.Range(0, count).Select(index =>
                new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.FromMinutes((index % 9 - 4) * 15))
                    .AddTicks(index)).ToArray();
            var list = values.ToList();
            var arrayBytes = Encode(candidateArray, values);
            var listBytes = Encode(candidateList, list);
            PendingLifecycleValidationProbe.Require(arrayBytes.AsSpan().SequenceEqual(listBytes),
                "Array/List input bytes differ; A/B payload is not controlled.");

            foreach (var fragmentSize in new[] { arrayBytes.Length, 64, 7, 1 })
            {
                var sequence = Fragment(arrayBytes, fragmentSize);
                var baselineArrayMeasurement = Measure(
                    baselineArray, values, sequence, count, fragmentSize, "array", "baseline");
                var candidateArrayMeasurement = Measure(
                    candidateArray, values, sequence, count, fragmentSize, "array", "candidate");
                var baselineListMeasurement = Measure(
                    baselineList, values, sequence, count, fragmentSize, "list", "baseline");
                var candidateListMeasurement = Measure(
                    candidateList, values, sequence, count, fragmentSize, "list", "candidate");

                baselineMeasurements.Add(baselineArrayMeasurement);
                candidateMeasurements.Add(candidateArrayMeasurement);
                baselineMeasurements.Add(baselineListMeasurement);
                candidateMeasurements.Add(candidateListMeasurement);
                comparisons.Add(Compare(baselineArrayMeasurement, candidateArrayMeasurement));
                comparisons.Add(Compare(baselineListMeasurement, candidateListMeasurement));
            }
        }

        PendingLifecycleValidationProbe.Write(new
        {
            phase = "complete",
            invariant = candidateMeasurements.Count == 24 && baselineMeasurements.Count == 24 &&
                candidateMeasurements.All(static item => item.ExactRoundtrip) &&
                baselineMeasurements.All(static item => item.ExactRoundtrip),
            runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            processorCount = Environment.ProcessorCount,
            timerFrequency = Stopwatch.Frequency,
            baseline = "pre-#559 repeated absolute Slice + intermediate array for List",
            candidate = "production codec at the checked-out PR head",
            candidateMeasurements,
            baselineMeasurements,
            comparisons,
            note = "Same-process Release A/B evidence only; timing ratios are not pass/fail thresholds or end-to-end RPC claims."
        });
    }

    private static Measurement Measure<T>(
        IRpcCodec<T> codec,
        DateTimeOffset[] expected,
        ReadOnlySequence<byte> sequence,
        int count,
        int fragmentSize,
        string collection,
        string implementation)
        where T : class?
    {
        var inputLength = sequence.Length;
        var first = codec.Deserialize(sequence)
            ?? throw new InvalidOperationException("Valid non-null collection decoded as null.");
        Check(first, expected);
        PendingLifecycleValidationProbe.Require(sequence.Length == inputLength,
            "input sequence was modified");
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
            {
                last = codec.Deserialize(sequence)
                    ?? throw new InvalidOperationException("Valid non-null collection decoded as null.");
            }
            var elapsed = Stopwatch.GetTimestamp() - started;
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            times[sample] = elapsed * (1_000_000_000.0 / Stopwatch.Frequency) / iterations;
            allocations[sample] = (double)bytes / iterations;
        }
        Check(last, expected);
        GC.KeepAlive(first);

        var sortedTimes = times.Order().ToArray();
        return new Measurement(
            implementation,
            collection,
            count,
            fragmentSize,
            (inputLength + fragmentSize - 1) / fragmentSize,
            inputLength,
            samples,
            iterations,
            codec.GetType().FullName,
            sortedTimes[samples / 2],
            sortedTimes[0],
            sortedTimes[^1],
            times,
            allocations.Order().ElementAt(samples / 2),
            ExactRoundtrip: true);
    }

    private static object Compare(Measurement baseline, Measurement candidate)
        => new
        {
            collection = candidate.Collection,
            count = candidate.Count,
            fragmentSize = candidate.FragmentSize,
            segmentCount = candidate.SegmentCount,
            baselineNanoseconds = baseline.MedianNanoseconds,
            candidateNanoseconds = candidate.MedianNanoseconds,
            speedupRatio = baseline.MedianNanoseconds / candidate.MedianNanoseconds,
            baselineAllocatedBytes = baseline.AllocatedBytesPerOperation,
            candidateAllocatedBytes = candidate.AllocatedBytesPerOperation,
            allocationDeltaBytes = candidate.AllocatedBytesPerOperation - baseline.AllocatedBytesPerOperation
        };

    private static void Check<T>(T? actual, DateTimeOffset[] expected)
    {
        if (actual is not IReadOnlyList<DateTimeOffset> values || values.Count != expected.Length)
            throw new InvalidOperationException("DateTimeOffset collection shape changed.");
        for (var index = 0; index < expected.Length; index++)
        {
            PendingLifecycleValidationProbe.Require(values[index].EqualsExact(expected[index]),
                $"DateTimeOffset instant/offset mismatch at {index}.");
        }
    }

    private static byte[] Encode<T>(IRpcCodec<T> codec, T value)
    {
        var writer = new ArrayBufferWriter<byte>();
        codec.Serialize(in value, writer);
        return writer.WrittenSpan.ToArray();
    }

    private static ReadOnlySequence<byte> Fragment(byte[] bytes, int size)
    {
        if (size >= bytes.Length)
            return new ReadOnlySequence<byte>(bytes);
        var first = new Segment(bytes.AsMemory(0, Math.Min(size, bytes.Length)));
        var last = first;
        for (var offset = size; offset < bytes.Length; offset += size)
            last = last.Append(bytes.AsMemory(offset, Math.Min(size, bytes.Length - offset)));
        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private static DateTimeOffset[]? BaselineReadCollection(in ReadOnlySequence<byte> buffer)
    {
        const int elementSize = 16;
        var length = CodecHelpers.ReadInt32(buffer);
        if (length < -1)
            throw new SharpLinkException(SharpLinkErrorCode.DataLoss, $"Invalid collection length {length}.");
        if (length <= 0)
        {
            CodecHelpers.EnsureExactSize(buffer, sizeof(int));
            return length == -1 ? null : [];
        }

        int payloadBytes;
        try
        {
            payloadBytes = checked(length * elementSize);
        }
        catch (OverflowException exception)
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.DataLoss,
                "Collection byte length overflowed.",
                exception);
        }
        if (payloadBytes > SharpLinkProtocolOptions.MaxMaxFramePayloadBytes - sizeof(int))
            throw new SharpLinkException(SharpLinkErrorCode.DataLoss, "Collection payload exceeds the protocol maximum.");
        CodecHelpers.EnsureExactSize(buffer, (long)sizeof(int) + payloadBytes);

        var result = new DateTimeOffset[length];
        var payload = buffer.Slice(sizeof(int));
        Span<byte> temporary = stackalloc byte[elementSize];
        for (var index = 0; index < length; index++)
        {
            var encoded = payload.Slice((long)index * elementSize, elementSize);
            if (encoded.FirstSpan.Length >= elementSize)
                result[index] = BaselineReadElement(encoded.FirstSpan[..elementSize]);
            else
            {
                encoded.CopyTo(temporary);
                result[index] = BaselineReadElement(temporary);
            }
        }
        return result;
    }

    private static DateTimeOffset BaselineReadElement(ReadOnlySpan<byte> element)
    {
        if (element.Slice(sizeof(short), 6).IndexOfAnyExcept((byte)0) >= 0)
            throw new SharpLinkException(SharpLinkErrorCode.DataLoss,
                "DateTimeOffset collection contains non-canonical padding.");
        var offsetMinutes = BinaryPrimitives.ReadInt16LittleEndian(element);
        var utcTicks = BinaryPrimitives.ReadInt64LittleEndian(element.Slice(sizeof(long)));
        if ((ulong)utcTicks > (ulong)DateTime.MaxValue.Ticks || offsetMinutes is < -840 or > 840)
            throw new SharpLinkException(SharpLinkErrorCode.DataLoss,
                "DateTimeOffset collection contains invalid UTC ticks or offset.");
        var offsetTicks = (long)offsetMinutes * TimeSpan.TicksPerMinute;
        if (offsetTicks > 0 && utcTicks > DateTime.MaxValue.Ticks - offsetTicks ||
            offsetTicks < 0 && utcTicks < -offsetTicks)
        {
            throw new SharpLinkException(SharpLinkErrorCode.DataLoss,
                "DateTimeOffset collection contains a value outside the supported clock range.");
        }
        return CodecHelpers.CreateDateTimeOffset(utcTicks + offsetTicks, offsetMinutes);
    }

    private sealed class BaselineArrayCodec : IRpcCodec<DateTimeOffset[]?>
    {
        public void Serialize(in DateTimeOffset[]? value, IBufferWriter<byte> writer)
            => DateTimeOffsetArrayCodec.Instance.Serialize(in value, writer);

        public DateTimeOffset[]? Deserialize(in ReadOnlySequence<byte> buffer)
            => BaselineReadCollection(buffer);
    }

    private sealed class BaselineListCodec : IRpcCodec<List<DateTimeOffset>?>
    {
        public void Serialize(in List<DateTimeOffset>? value, IBufferWriter<byte> writer)
            => DateTimeOffsetListCodec.Instance.Serialize(in value, writer);

        public List<DateTimeOffset>? Deserialize(in ReadOnlySequence<byte> buffer)
        {
            var array = BaselineReadCollection(buffer);
            return array is null ? null : [.. array];
        }
    }

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

    private sealed record Measurement(
        string Implementation,
        string Collection,
        int Count,
        int FragmentSize,
        long SegmentCount,
        long InputLength,
        int Samples,
        int Iterations,
        string? Codec,
        double MedianNanoseconds,
        double MinNanoseconds,
        double MaxNanoseconds,
        double[] NanosecondsPerSample,
        double AllocatedBytesPerOperation,
        bool ExactRoundtrip);
}
