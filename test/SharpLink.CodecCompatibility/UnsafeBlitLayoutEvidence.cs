using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpLink.Runtime;

namespace SharpLink.CodecCompatibility;

internal static class LayoutEvidenceProfiles
{
    internal const string FixedWidth = "fixed-width";
    internal const string NativeWidth = "native-width";

    internal static void Validate(string profile)
    {
        if (!string.Equals(profile, FixedWidth, StringComparison.Ordinal)
            && !string.Equals(profile, NativeWidth, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unknown UnsafeBlit layout evidence profile '{profile}'. Expected '{FixedWidth}' or '{NativeWidth}'.");
        }
    }
}

internal sealed class LayoutEvidenceRuntimeIdentity
{
    public int SchemaVersion { get; set; } = 1;
    public string SharpLinkCommit { get; set; } = string.Empty;
    public string TargetFramework { get; set; } = string.Empty;
    public string FrameworkDescription { get; set; } = string.Empty;
    public string RuntimeFamily { get; set; } = string.Empty;
    public string RuntimeFamilySource { get; set; } = string.Empty;
    public string RuntimeVersion { get; set; } = string.Empty;
    public string SdkVersion { get; set; } = string.Empty;
    public string RuntimeIdentifier { get; set; } = string.Empty;
    public string ExecutionEnvironment { get; set; } = string.Empty;
    public string Os { get; set; } = string.Empty;
    public string OsVersion { get; set; } = string.Empty;
    public string ProcessArchitecture { get; set; } = string.Empty;
    public string OsArchitecture { get; set; } = string.Empty;
    public int PointerSize { get; set; }
    public bool IsLittleEndian { get; set; }
    public string CompilationMode { get; set; } = string.Empty;
    public string PlatformTag { get; set; } = string.Empty;
}

internal sealed class LayoutEvidenceCase
{
    public string Id { get; set; } = string.Empty;
    public string LogicalShape { get; set; } = string.Empty;
    public string LayoutKind { get; set; } = string.Empty;
    public int? Pack { get; set; }
    public string WidthDomain { get; set; } = string.Empty;
    public bool NativeWidth { get; set; }
    public bool LegacyControl { get; set; }
    public List<string> FrameworkRawFields { get; set; } = [];
    public string Type { get; set; } = string.Empty;
    public int Size { get; set; }
    public Dictionary<string, int> FieldOffsets { get; set; } = [];
    public Dictionary<string, int> FieldSizes { get; set; } = [];
    public List<int> PaddingByteOffsets { get; set; } = [];
    public string ExpectedLogicalValue { get; set; } = string.Empty;
    public string WireFile { get; set; } = string.Empty;
    public string WireSha256 { get; set; } = string.Empty;
}

internal sealed class LayoutEvidenceEnvelope
{
    public int SchemaVersion { get; set; } = 1;
    public string Profile { get; set; } = string.Empty;
    public LayoutEvidenceRuntimeIdentity Runtime { get; set; } = new();
    public List<LayoutEvidenceCase> Cases { get; set; } = [];
    public Dictionary<string, string> CaseBytesBase64 { get; set; } = [];
}

internal sealed class LayoutEvidenceFieldDifference
{
    public string Field { get; set; } = string.Empty;
    public int? Producer { get; set; }
    public int? Consumer { get; set; }
}

internal sealed class LayoutEvidenceResult
{
    public string Profile { get; set; } = string.Empty;
    public string Producer { get; set; } = string.Empty;
    public string Consumer { get; set; } = string.Empty;
    public string Fixture { get; set; } = string.Empty;
    public string LogicalShape { get; set; } = string.Empty;
    public string LayoutKind { get; set; } = string.Empty;
    public int? Pack { get; set; }
    public string WidthDomain { get; set; } = string.Empty;
    public bool NativeWidth { get; set; }
    public bool LegacyControl { get; set; }
    public List<string> FrameworkRawFields { get; set; } = [];
    public int ProducerSize { get; set; }
    public int ConsumerSize { get; set; }
    public int ProducerPointerSize { get; set; }
    public int ConsumerPointerSize { get; set; }
    public Dictionary<string, int> ProducerFieldOffsets { get; set; } = [];
    public Dictionary<string, int> ConsumerFieldOffsets { get; set; } = [];
    public Dictionary<string, int> ProducerFieldSizes { get; set; } = [];
    public Dictionary<string, int> ConsumerFieldSizes { get; set; } = [];
    public List<LayoutEvidenceFieldDifference> FieldOffsetDifferences { get; set; } = [];
    public List<LayoutEvidenceFieldDifference> FieldSizeDifferences { get; set; } = [];
    public List<int> ProducerPaddingByteOffsets { get; set; } = [];
    public List<int> ConsumerPaddingByteOffsets { get; set; } = [];
    public string ProducerWireHash { get; set; } = string.Empty;
    public string ConsumerLocalWireHash { get; set; } = string.Empty;
    public bool SizeEqual { get; set; }
    public bool FieldOffsetsEqual { get; set; }
    public bool FieldSizesEqual { get; set; }
    public bool LayoutMetadataEqual { get; set; }
    public bool PointerWidthMismatch { get; set; }
    public bool ByteForByteEquality { get; set; }
    public List<int> DifferingByteOffsets { get; set; } = [];
    public bool DifferencesOnlyInPaddingOnBothSides { get; set; }
    public bool DifferencesConfinedToPaddingOnEitherSide { get; set; }
    public bool NestedFieldMetadataMismatch { get; set; }
    public bool DifferingBytesTouchNestedField { get; set; }
    public bool DifferingBytesTouchFrameworkRawField { get; set; }
    public bool? CrossDeserializeResult { get; set; }
    public bool? LogicalEquality { get; set; }
    public bool? SegmentedCrossDeserializeResult { get; set; }
    public bool? SegmentedLogicalEquality { get; set; }
    public string ExpectedLogicalValue { get; set; } = string.Empty;
    public string ActualLogicalValue { get; set; } = string.Empty;
    public string? ExceptionType { get; set; }
    public string? ExceptionMessage { get; set; }
    public bool RawWireCompatible { get; set; }
    public bool RawRepresentationStable { get; set; }
    public string Classification { get; set; } = string.Empty;
}

internal sealed class LayoutEvidenceReport
{
    public int SchemaVersion { get; set; } = 1;
    public LayoutEvidenceRuntimeIdentity Consumer { get; set; } = new();
    public List<LayoutEvidenceResult> Results { get; set; } = [];
}

internal sealed class LayoutFixtureConclusion
{
    public string Fixture { get; set; } = string.Empty;
    public string LogicalShape { get; set; } = string.Empty;
    public string LayoutKind { get; set; } = string.Empty;
    public int? Pack { get; set; }
    public string WidthDomain { get; set; } = string.Empty;
    public bool NativeWidth { get; set; }
    public bool LegacyControl { get; set; }
    public int CrossPlatformEdges { get; set; }
    public int RawWireCompatibleEdges { get; set; }
    public int RawRepresentationStableEdges { get; set; }
    public int SizeMismatchEdges { get; set; }
    public int FieldOffsetMismatchEdges { get; set; }
    public int RawByteDifferenceEdges { get; set; }
    public int LogicalMismatchEdges { get; set; }
    public int PaddingOnlyDifferenceEdges { get; set; }
    public int NestedRepresentationDifferenceEdges { get; set; }
    public int FrameworkRawDifferenceEdges { get; set; }
    public int PointerWidthMismatchEdges { get; set; }
    public bool AllCrossPlatformRawWireCompatible { get; set; }
    public bool AllCrossPlatformRawRepresentationStable { get; set; }
}

internal sealed class LayoutEvidenceHypothesis
{
    public string Id { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public bool SupportedByObservedMatrix { get; set; }
    public List<string> Evidence { get; set; } = [];
    public List<string> CounterEvidence { get; set; } = [];
}

internal sealed class LayoutEvidenceSummary
{
    public int SchemaVersion { get; set; } = 1;
    public string SharpLinkCommit { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public List<string> Platforms { get; set; } = [];
    public List<LayoutFixtureConclusion> Fixtures { get; set; } = [];
    public List<LayoutEvidenceHypothesis> Hypotheses { get; set; } = [];
    public List<LayoutEvidenceResult> Results { get; set; } = [];
}

internal sealed class LayoutEvidenceFieldMap<T> where T : unmanaged
{
    internal Dictionary<string, int> Offsets { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, int> Sizes { get; } = new(StringComparer.Ordinal);

    internal void Add<TField>(ref T root, ref TField field, string path) where TField : unmanaged
    {
        ref var rootByte = ref Unsafe.As<T, byte>(ref root);
        ref var fieldByte = ref Unsafe.As<TField, byte>(ref field);
        var offset = checked((int)Unsafe.ByteOffset(ref rootByte, ref fieldByte));
        if (!Offsets.TryAdd(path, offset) || !Sizes.TryAdd(path, Unsafe.SizeOf<TField>()))
            throw new InvalidOperationException($"Duplicate layout evidence field path {typeof(T).Name}.{path}.");
    }

    internal List<int> GetPaddingOffsets()
    {
        var occupied = new bool[Unsafe.SizeOf<T>()];
        foreach (var pair in Offsets)
        {
            var size = Sizes[pair.Key];
            for (var index = pair.Value; index < Math.Min(pair.Value + size, occupied.Length); index++)
            {
                if (index >= 0)
                    occupied[index] = true;
            }
        }
        return Enumerable.Range(0, occupied.Length).Where(index => !occupied[index]).ToList();
    }
}

internal interface ILayoutEvidenceFixture
{
    string Id { get; }
    string LogicalShape { get; }
    string LayoutKind { get; }
    int? Pack { get; }
    string WidthDomain { get; }
    bool NativeWidth { get; }
    bool LegacyControl { get; }
    IReadOnlyList<string> FrameworkRawFields { get; }
    int Size { get; }
    byte[] Serialize();
    LayoutEvidenceCase CreateCase(byte[] bytes);
    LayoutEvidenceResult Verify(
        string profile,
        byte[] producerBytes,
        LayoutEvidenceCase producerCase,
        LayoutEvidenceRuntimeIdentity producer,
        LayoutEvidenceRuntimeIdentity consumer);
}

internal sealed class LayoutEvidenceFixture<T> : ILayoutEvidenceFixture where T : unmanaged
{
    private static readonly JsonSerializerOptions DescribeOptions = new() { IncludeFields = true };
    private readonly T _value;
    private readonly Func<T, T, bool> _logicalEquals;
    private readonly Dictionary<string, int> _fieldOffsets;
    private readonly Dictionary<string, int> _fieldSizes;
    private readonly List<int> _paddingOffsets;

    internal LayoutEvidenceFixture(
        string id,
        string logicalShape,
        string layoutKind,
        int? pack,
        string widthDomain,
        bool nativeWidth,
        bool legacyControl,
        IReadOnlyList<string> frameworkRawFields,
        T value,
        LayoutEvidenceFieldMap<T> fields,
        Func<T, T, bool>? logicalEquals = null)
    {
        Id = id;
        LogicalShape = logicalShape;
        LayoutKind = layoutKind;
        Pack = pack;
        WidthDomain = widthDomain;
        NativeWidth = nativeWidth;
        LegacyControl = legacyControl;
        FrameworkRawFields = frameworkRawFields.ToArray();
        _value = value;
        _logicalEquals = logicalEquals ?? EqualityComparer<T>.Default.Equals;
        _fieldOffsets = new Dictionary<string, int>(fields.Offsets, StringComparer.Ordinal);
        _fieldSizes = new Dictionary<string, int>(fields.Sizes, StringComparer.Ordinal);
        _paddingOffsets = fields.GetPaddingOffsets();
    }

    public string Id { get; }
    public string LogicalShape { get; }
    public string LayoutKind { get; }
    public int? Pack { get; }
    public string WidthDomain { get; }
    public bool NativeWidth { get; }
    public bool LegacyControl { get; }
    public IReadOnlyList<string> FrameworkRawFields { get; }
    public int Size => Unsafe.SizeOf<T>();

    public byte[] Serialize()
    {
        var writer = new ArrayBufferWriter<byte>(Size);
        var value = _value;
        UnsafeBlitCodec<T>.Instance.Serialize(in value, writer);
        return writer.WrittenSpan.ToArray();
    }

    public LayoutEvidenceCase CreateCase(byte[] bytes)
        => new()
        {
            Id = Id,
            LogicalShape = LogicalShape,
            LayoutKind = LayoutKind,
            Pack = Pack,
            WidthDomain = WidthDomain,
            NativeWidth = NativeWidth,
            LegacyControl = LegacyControl,
            FrameworkRawFields = FrameworkRawFields.ToList(),
            Type = typeof(T).FullName ?? typeof(T).Name,
            Size = Size,
            FieldOffsets = new Dictionary<string, int>(_fieldOffsets, StringComparer.Ordinal),
            FieldSizes = new Dictionary<string, int>(_fieldSizes, StringComparer.Ordinal),
            PaddingByteOffsets = [.. _paddingOffsets],
            ExpectedLogicalValue = Describe(_value),
            WireFile = $"cases/{SanitizeFileName(Id)}.bin",
            WireSha256 = Hash(bytes)
        };

    public LayoutEvidenceResult Verify(
        string profile,
        byte[] producerBytes,
        LayoutEvidenceCase producerCase,
        LayoutEvidenceRuntimeIdentity producer,
        LayoutEvidenceRuntimeIdentity consumer)
    {
        var localBytes = Serialize();
        var localCase = CreateCase(localBytes);
        var differingBytes = FindDifferences(producerBytes, localBytes);
        var offsetDifferences = FindDictionaryDifferences(producerCase.FieldOffsets, localCase.FieldOffsets);
        var sizeDifferences = FindDictionaryDifferences(producerCase.FieldSizes, localCase.FieldSizes);
        var producerPadding = producerCase.PaddingByteOffsets.ToHashSet();
        var consumerPadding = localCase.PaddingByteOffsets.ToHashSet();

        var result = new LayoutEvidenceResult
        {
            Profile = profile,
            Producer = producer.PlatformTag,
            Consumer = consumer.PlatformTag,
            Fixture = Id,
            LogicalShape = LogicalShape,
            LayoutKind = LayoutKind,
            Pack = Pack,
            WidthDomain = WidthDomain,
            NativeWidth = NativeWidth,
            LegacyControl = LegacyControl,
            FrameworkRawFields = FrameworkRawFields.ToList(),
            ProducerSize = producerCase.Size,
            ConsumerSize = localCase.Size,
            ProducerPointerSize = producer.PointerSize,
            ConsumerPointerSize = consumer.PointerSize,
            ProducerFieldOffsets = new Dictionary<string, int>(producerCase.FieldOffsets, StringComparer.Ordinal),
            ConsumerFieldOffsets = new Dictionary<string, int>(localCase.FieldOffsets, StringComparer.Ordinal),
            ProducerFieldSizes = new Dictionary<string, int>(producerCase.FieldSizes, StringComparer.Ordinal),
            ConsumerFieldSizes = new Dictionary<string, int>(localCase.FieldSizes, StringComparer.Ordinal),
            FieldOffsetDifferences = offsetDifferences,
            FieldSizeDifferences = sizeDifferences,
            ProducerPaddingByteOffsets = [.. producerCase.PaddingByteOffsets],
            ConsumerPaddingByteOffsets = [.. localCase.PaddingByteOffsets],
            ProducerWireHash = producerCase.WireSha256,
            ConsumerLocalWireHash = localCase.WireSha256,
            SizeEqual = producerCase.Size == localCase.Size,
            FieldOffsetsEqual = offsetDifferences.Count == 0,
            FieldSizesEqual = sizeDifferences.Count == 0,
            PointerWidthMismatch = producer.PointerSize != consumer.PointerSize,
            ByteForByteEquality = producerBytes.AsSpan().SequenceEqual(localBytes),
            DifferingByteOffsets = differingBytes,
            DifferencesOnlyInPaddingOnBothSides = differingBytes.Count != 0
                && differingBytes.All(offset => producerPadding.Contains(offset) && consumerPadding.Contains(offset)),
            DifferencesConfinedToPaddingOnEitherSide = differingBytes.Count != 0
                && differingBytes.All(offset => producerPadding.Contains(offset) || consumerPadding.Contains(offset)),
            NestedFieldMetadataMismatch = offsetDifferences.Concat(sizeDifferences)
                .Any(static difference => difference.Field.Contains('.', StringComparison.Ordinal)),
            DifferingBytesTouchNestedField = TouchesFieldRegion(
                differingBytes,
                producerCase,
                localCase,
                static field => field.Contains('.', StringComparison.Ordinal)),
            DifferingBytesTouchFrameworkRawField = TouchesFieldRegion(
                differingBytes,
                producerCase,
                localCase,
                field => FrameworkRawFields.Contains(field, StringComparer.Ordinal)),
            ExpectedLogicalValue = localCase.ExpectedLogicalValue
        };
        result.LayoutMetadataEqual = result.SizeEqual && result.FieldOffsetsEqual && result.FieldSizesEqual;

        if (result.SizeEqual && producerBytes.Length == localCase.Size)
        {
            try
            {
                var sequence = new ReadOnlySequence<byte>(producerBytes);
                var actual = UnsafeBlitCodec<T>.Instance.Deserialize(in sequence);
                result.CrossDeserializeResult = true;
                result.LogicalEquality = _logicalEquals(_value, actual);
                result.ActualLogicalValue = Describe(actual);

                if (producerBytes.Length > 1)
                {
                    var segmented = CreateSegmentedSequence(producerBytes);
                    var segmentedActual = UnsafeBlitCodec<T>.Instance.Deserialize(in segmented);
                    result.SegmentedCrossDeserializeResult = true;
                    result.SegmentedLogicalEquality = _logicalEquals(_value, segmentedActual);
                }
            }
            catch (Exception exception)
            {
                result.CrossDeserializeResult = false;
                result.LogicalEquality = false;
                result.ExceptionType = exception.GetType().FullName;
                result.ExceptionMessage = exception.Message;
            }
        }

        result.RawWireCompatible = result.CrossDeserializeResult == true
            && result.LogicalEquality == true
            && (producerBytes.Length <= 1
                || (result.SegmentedCrossDeserializeResult == true && result.SegmentedLogicalEquality == true));
        result.RawRepresentationStable = result.RawWireCompatible
            && result.LayoutMetadataEqual
            && result.ByteForByteEquality;
        result.Classification = Classify(result);
        return result;
    }

    private static string Classify(LayoutEvidenceResult result)
    {
        if (!result.SizeEqual)
            return result.PointerWidthMismatch && result.NativeWidth
                ? "POINTER_WIDTH_SIZE_MISMATCH"
                : "SIZE_MISMATCH";
        if (result.CrossDeserializeResult == false)
            return "DESERIALIZE_REJECTED";
        if (result.LogicalEquality != true || result.SegmentedLogicalEquality == false)
            return result.DifferingBytesTouchFrameworkRawField
                ? "FRAMEWORK_RAW_LOGICAL_MISMATCH"
                : result.DifferingBytesTouchNestedField || result.NestedFieldMetadataMismatch
                    ? "NESTED_LOGICAL_MISMATCH"
                    : "LOGICAL_DESERIALIZE_MISMATCH";
        if (!result.FieldOffsetsEqual)
            return result.NestedFieldMetadataMismatch
                ? "NESTED_FIELD_OFFSET_MISMATCH_BUT_LOGICALLY_COMPATIBLE"
                : "FIELD_OFFSET_MISMATCH_BUT_LOGICALLY_COMPATIBLE";
        if (result.ByteForByteEquality)
            return result.FieldSizesEqual
                ? "IDENTICAL_RAW_AND_LOGICAL"
                : "IDENTICAL_BYTES_WITH_FIELD_SIZE_DIFFERENCE";
        if (result.DifferencesOnlyInPaddingOnBothSides)
            return "PADDING_BYTES_DIFFER_ONLY";
        if (result.DifferingBytesTouchFrameworkRawField)
            return "FRAMEWORK_RAW_BYTES_DIFFER_BUT_LOGICALLY_COMPATIBLE";
        if (result.DifferingBytesTouchNestedField)
            return "NESTED_BYTES_DIFFER_BUT_LOGICALLY_COMPATIBLE";
        return result.PointerWidthMismatch && result.NativeWidth
            ? "POINTER_WIDTH_BYTES_DIFFER_BUT_LOGICALLY_COMPATIBLE"
            : "RAW_BYTES_DIFFER_BUT_LOGICALLY_COMPATIBLE";
    }

    private static List<LayoutEvidenceFieldDifference> FindDictionaryDifferences(
        IReadOnlyDictionary<string, int> producer,
        IReadOnlyDictionary<string, int> consumer)
    {
        var keys = producer.Keys.Concat(consumer.Keys).Distinct(StringComparer.Ordinal).OrderBy(static key => key, StringComparer.Ordinal);
        var result = new List<LayoutEvidenceFieldDifference>();
        foreach (var key in keys)
        {
            var producerFound = producer.TryGetValue(key, out var producerValue);
            var consumerFound = consumer.TryGetValue(key, out var consumerValue);
            if (!producerFound || !consumerFound || producerValue != consumerValue)
            {
                result.Add(new LayoutEvidenceFieldDifference
                {
                    Field = key,
                    Producer = producerFound ? producerValue : null,
                    Consumer = consumerFound ? consumerValue : null
                });
            }
        }
        return result;
    }

    private static List<int> FindDifferences(ReadOnlySpan<byte> producer, ReadOnlySpan<byte> consumer)
    {
        var count = Math.Max(producer.Length, consumer.Length);
        var result = new List<int>();
        for (var index = 0; index < count; index++)
        {
            if (index >= producer.Length || index >= consumer.Length || producer[index] != consumer[index])
                result.Add(index);
        }
        return result;
    }

    private static bool TouchesFieldRegion(
        IReadOnlyList<int> differingBytes,
        LayoutEvidenceCase producer,
        LayoutEvidenceCase consumer,
        Func<string, bool> predicate)
    {
        foreach (var field in producer.FieldOffsets.Keys.Concat(consumer.FieldOffsets.Keys).Distinct(StringComparer.Ordinal))
        {
            if (!predicate(field))
                continue;
            if (TouchesRegion(differingBytes, producer.FieldOffsets, producer.FieldSizes, field)
                || TouchesRegion(differingBytes, consumer.FieldOffsets, consumer.FieldSizes, field))
            {
                return true;
            }
        }
        return false;
    }

    private static bool TouchesRegion(
        IReadOnlyList<int> differingBytes,
        IReadOnlyDictionary<string, int> offsets,
        IReadOnlyDictionary<string, int> sizes,
        string field)
    {
        if (!offsets.TryGetValue(field, out var offset) || !sizes.TryGetValue(field, out var size))
            return false;
        return differingBytes.Any(index => index >= offset && index < offset + size);
    }

    private static ReadOnlySequence<byte> CreateSegmentedSequence(byte[] bytes)
    {
        var split = Math.Clamp(bytes.Length / 2, 1, bytes.Length - 1);
        var first = new LayoutSequenceSegment(bytes.AsMemory(0, split));
        var last = first.Append(bytes.AsMemory(split));
        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private static string Describe(T value)
    {
        try
        {
            return JsonSerializer.Serialize(value, DescribeOptions);
        }
        catch (Exception)
        {
            return value.ToString() ?? typeof(T).Name;
        }
    }

    private static string SanitizeFileName(string value)
        => string.Concat(value.Select(static character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));

    private static string Hash(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class LayoutSequenceSegment : ReadOnlySequenceSegment<byte>
    {
        internal LayoutSequenceSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        internal LayoutSequenceSegment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new LayoutSequenceSegment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
