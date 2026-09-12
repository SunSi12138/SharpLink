using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpLink.CodecCompatibility;

internal static class LayoutEvidenceFixtureRegistry
{
    internal static IReadOnlyList<ILayoutEvidenceFixture> All { get; } = Create();
    internal static IReadOnlyDictionary<string, ILayoutEvidenceFixture> ById { get; } =
        All.ToDictionary(static fixture => fixture.Id, StringComparer.Ordinal);

    internal static IReadOnlyList<ILayoutEvidenceFixture> ForProfile(string profile)
    {
        LayoutEvidenceProfiles.Validate(profile);
        return All.Where(fixture => string.Equals(profile, LayoutEvidenceProfiles.NativeWidth, StringComparison.Ordinal)
                ? fixture.NativeWidth
                : !fixture.NativeWidth)
            .ToArray();
    }

    private static IReadOnlyList<ILayoutEvidenceFixture> Create()
    {
        var fixtures = new List<ILayoutEvidenceFixture>
        {
            CreateMixedAuto(), CreateMixedSequential(), CreateMixedExplicit(),
            CreatePaddingAuto(), CreatePaddingSequential(null), CreatePaddingSequential(1),
            CreatePaddingSequential(4), CreatePaddingSequential(8), CreatePaddingExplicit(),
            CreateNestedAuto(), CreateNestedSequential(), CreateNestedExplicit(),
            CreateAutoGeneric("Generic.Byte.Auto", "generic-byte-fixed", (byte)0x52),
            CreateSequentialGeneric("Generic.Byte.Sequential", "generic-byte-fixed", (byte)0x52),
            CreateExplicitGenericByte(),
            CreateAutoGeneric("Generic.Int64.Auto", "generic-int64-fixed", 0x1020304050607080L),
            CreateSequentialGeneric("Generic.Int64.Sequential", "generic-int64-fixed", 0x1020304050607080L),
            CreateExplicitGenericInt64(),
            CreateAutoGeneric("Generic.Guid.Auto", "generic-guid-framework", Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), ["Value"]),
            CreateSequentialGeneric("Generic.Guid.Sequential", "generic-guid-framework", Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), ["Value"]),
            CreateExplicitGenericGuid(),
            CreateAutoGenericDateTimeOffset(), CreateSequentialGenericDateTimeOffset(), CreateExplicitGenericDateTimeOffset(),
            CreateDateTimeOffsetContainerAuto(), CreateDateTimeOffsetContainerSequential(), CreateDateTimeOffsetContainerExplicit(),
            CreateNativeAuto(), CreateNativeSequential(), CreateNativeExplicit()
        };
        fixtures.AddRange(CreateLegacyControls());
        return fixtures;
    }

    private static ILayoutEvidenceFixture CreateMixedAuto()
    {
        var value = new LayoutMixedAuto { A = 0x12, B = 0x2345, C = 0x3456789A, D = 0x0102030405060708, E = 12345.25d };
        var fields = new LayoutEvidenceFieldMap<LayoutMixedAuto>();
        fields.Add(ref value, ref value.A, "A"); fields.Add(ref value, ref value.B, "B"); fields.Add(ref value, ref value.C, "C"); fields.Add(ref value, ref value.D, "D"); fields.Add(ref value, ref value.E, "E");
        return Fixture("Mixed.Auto", "mixed-alignment-fixed", "Auto", null, "fixed-width-primitive", false, false, [], value, fields);
    }

    private static ILayoutEvidenceFixture CreateMixedSequential()
    {
        var value = new LayoutMixedSequential { A = 0x12, B = 0x2345, C = 0x3456789A, D = 0x0102030405060708, E = 12345.25d };
        var fields = new LayoutEvidenceFieldMap<LayoutMixedSequential>();
        fields.Add(ref value, ref value.A, "A"); fields.Add(ref value, ref value.B, "B"); fields.Add(ref value, ref value.C, "C"); fields.Add(ref value, ref value.D, "D"); fields.Add(ref value, ref value.E, "E");
        return Fixture("Mixed.Sequential", "mixed-alignment-fixed", "Sequential", null, "fixed-width-primitive", false, false, [], value, fields);
    }

    private static ILayoutEvidenceFixture CreateMixedExplicit()
    {
        var value = new LayoutMixedExplicit { A = 0x12, B = 0x2345, C = 0x3456789A, D = 0x0102030405060708, E = 12345.25d };
        var fields = new LayoutEvidenceFieldMap<LayoutMixedExplicit>();
        fields.Add(ref value, ref value.A, "A"); fields.Add(ref value, ref value.B, "B"); fields.Add(ref value, ref value.C, "C"); fields.Add(ref value, ref value.D, "D"); fields.Add(ref value, ref value.E, "E");
        return Fixture("Mixed.Explicit", "mixed-alignment-fixed", "Explicit", null, "fixed-width-primitive", false, false, [], value, fields);
    }

    private static ILayoutEvidenceFixture CreatePaddingAuto()
    {
        var value = new LayoutPaddingAuto { Prefix = 0x51, Value = 0x4142434445464748, Suffix = 0x52 };
        var fields = new LayoutEvidenceFieldMap<LayoutPaddingAuto>();
        fields.Add(ref value, ref value.Prefix, "Prefix"); fields.Add(ref value, ref value.Value, "Value"); fields.Add(ref value, ref value.Suffix, "Suffix");
        return Fixture("Padding.Auto", "padding-heavy-fixed", "Auto", null, "fixed-width-primitive", false, false, [], value, fields);
    }

    private static ILayoutEvidenceFixture CreatePaddingSequential(int? pack)
        => pack switch
        {
            null => CreatePaddingSequentialDefault(),
            1 => CreatePaddingSequentialPack1(),
            4 => CreatePaddingSequentialPack4(),
            8 => CreatePaddingSequentialPack8(),
            _ => throw new InvalidOperationException($"Unsupported evidence pack {pack}.")
        };

    private static ILayoutEvidenceFixture CreatePaddingSequentialDefault()
    {
        var value = new LayoutPaddingSequential { Prefix = 0x51, Value = 0x4142434445464748, Suffix = 0x52 };
        var fields = new LayoutEvidenceFieldMap<LayoutPaddingSequential>();
        fields.Add(ref value, ref value.Prefix, "Prefix"); fields.Add(ref value, ref value.Value, "Value"); fields.Add(ref value, ref value.Suffix, "Suffix");
        return Fixture("Padding.Sequential.Default", "padding-heavy-fixed", "Sequential", null, "fixed-width-primitive", false, false, [], value, fields);
    }

    private static ILayoutEvidenceFixture CreatePaddingSequentialPack1()
    {
        var value = new LayoutPaddingSequentialPack1 { Prefix = 0x51, Value = 0x4142434445464748, Suffix = 0x52 };
        var fields = new LayoutEvidenceFieldMap<LayoutPaddingSequentialPack1>();
        fields.Add(ref value, ref value.Prefix, "Prefix"); fields.Add(ref value, ref value.Value, "Value"); fields.Add(ref value, ref value.Suffix, "Suffix");
        return Fixture("Padding.Sequential.Pack1", "padding-heavy-fixed", "Sequential", 1, "fixed-width-primitive", false, false, [], value, fields);
    }

    private static ILayoutEvidenceFixture CreatePaddingSequentialPack4()
    {
        var value = new LayoutPaddingSequentialPack4 { Prefix = 0x51, Value = 0x4142434445464748, Suffix = 0x52 };
        var fields = new LayoutEvidenceFieldMap<LayoutPaddingSequentialPack4>();
        fields.Add(ref value, ref value.Prefix, "Prefix"); fields.Add(ref value, ref value.Value, "Value"); fields.Add(ref value, ref value.Suffix, "Suffix");
        return Fixture("Padding.Sequential.Pack4", "padding-heavy-fixed", "Sequential", 4, "fixed-width-primitive", false, false, [], value, fields);
    }

    private static ILayoutEvidenceFixture CreatePaddingSequentialPack8()
    {
        var value = new LayoutPaddingSequentialPack8 { Prefix = 0x51, Value = 0x4142434445464748, Suffix = 0x52 };
        var fields = new LayoutEvidenceFieldMap<LayoutPaddingSequentialPack8>();
        fields.Add(ref value, ref value.Prefix, "Prefix"); fields.Add(ref value, ref value.Value, "Value"); fields.Add(ref value, ref value.Suffix, "Suffix");
        return Fixture("Padding.Sequential.Pack8", "padding-heavy-fixed", "Sequential", 8, "fixed-width-primitive", false, false, [], value, fields);
    }

    private static ILayoutEvidenceFixture CreatePaddingExplicit()
    {
        var value = new LayoutPaddingExplicit { Prefix = 0x51, Value = 0x4142434445464748, Suffix = 0x52 };
        var fields = new LayoutEvidenceFieldMap<LayoutPaddingExplicit>();
        fields.Add(ref value, ref value.Prefix, "Prefix"); fields.Add(ref value, ref value.Value, "Value"); fields.Add(ref value, ref value.Suffix, "Suffix");
        return Fixture("Padding.Explicit", "padding-heavy-fixed", "Explicit", null, "fixed-width-primitive", false, false, [], value, fields);
    }

    private static ILayoutEvidenceFixture CreateNestedAuto()
    {
        var value = new LayoutNestedAuto { Prefix = 0x1234, Inner = new LayoutInnerAuto { A = 0x33, B = 0x55667788 }, Tail = 0x0102030405060708 };
        var fields = new LayoutEvidenceFieldMap<LayoutNestedAuto>();
        fields.Add(ref value, ref value.Prefix, "Prefix"); fields.Add(ref value, ref value.Inner.A, "Inner.A"); fields.Add(ref value, ref value.Inner.B, "Inner.B"); fields.Add(ref value, ref value.Tail, "Tail");
        return Fixture("Nested.Auto", "nested-fixed", "Auto", null, "fixed-width-primitive", false, false, [], value, fields);
    }

    private static ILayoutEvidenceFixture CreateNestedSequential()
    {
        var value = new LayoutNestedSequential { Prefix = 0x1234, Inner = new LayoutInnerSequential { A = 0x33, B = 0x55667788 }, Tail = 0x0102030405060708 };
        var fields = new LayoutEvidenceFieldMap<LayoutNestedSequential>();
        fields.Add(ref value, ref value.Prefix, "Prefix"); fields.Add(ref value, ref value.Inner.A, "Inner.A"); fields.Add(ref value, ref value.Inner.B, "Inner.B"); fields.Add(ref value, ref value.Tail, "Tail");
        return Fixture("Nested.Sequential", "nested-fixed", "Sequential", null, "fixed-width-primitive", false, false, [], value, fields);
    }

    private static ILayoutEvidenceFixture CreateNestedExplicit()
    {
        var value = new LayoutNestedExplicit { Prefix = 0x1234, Inner = new LayoutInnerExplicit { A = 0x33, B = 0x55667788 }, Tail = 0x0102030405060708 };
        var fields = new LayoutEvidenceFieldMap<LayoutNestedExplicit>();
        fields.Add(ref value, ref value.Prefix, "Prefix"); fields.Add(ref value, ref value.Inner.A, "Inner.A"); fields.Add(ref value, ref value.Inner.B, "Inner.B"); fields.Add(ref value, ref value.Tail, "Tail");
        return Fixture("Nested.Explicit", "nested-fixed", "Explicit", null, "fixed-width-primitive", false, false, [], value, fields);
    }

    private static ILayoutEvidenceFixture CreateAutoGeneric<T>(string id, string shape, T item, IReadOnlyList<string>? frameworkRawFields = null) where T : unmanaged
    {
        var value = new LayoutAutoGeneric<T> { Prefix = 0x41, Value = item, Tail = 0x1112131415161718 };
        var fields = new LayoutEvidenceFieldMap<LayoutAutoGeneric<T>>();
        fields.Add(ref value, ref value.Prefix, "Prefix"); fields.Add(ref value, ref value.Value, "Value"); fields.Add(ref value, ref value.Tail, "Tail");
        return Fixture(id, shape, "Auto", null, frameworkRawFields is { Count: > 0 } ? "fixed-width-framework" : "fixed-width-primitive", false, false, frameworkRawFields ?? [], value, fields);
    }

    private static ILayoutEvidenceFixture CreateSequentialGeneric<T>(string id, string shape, T item, IReadOnlyList<string>? frameworkRawFields = null) where T : unmanaged
    {
        var value = new LayoutSequentialGeneric<T> { Prefix = 0x41, Value = item, Tail = 0x1112131415161718 };
        var fields = new LayoutEvidenceFieldMap<LayoutSequentialGeneric<T>>();
        fields.Add(ref value, ref value.Prefix, "Prefix"); fields.Add(ref value, ref value.Value, "Value"); fields.Add(ref value, ref value.Tail, "Tail");
        return Fixture(id, shape, "Sequential", null, frameworkRawFields is { Count: > 0 } ? "fixed-width-framework" : "fixed-width-primitive", false, false, frameworkRawFields ?? [], value, fields);
    }

    private static ILayoutEvidenceFixture CreateExplicitGenericByte()
    {
        var value = new LayoutExplicitGenericByte { Prefix = 0x41, Value = 0x52, Tail = 0x1112131415161718 };
        var fields = new LayoutEvidenceFieldMap<LayoutExplicitGenericByte>(); fields.Add(ref value, ref value.Prefix, "Prefix"); fields.Add(ref value, ref value.Value, "Value"); fields.Add(ref value, ref value.Tail, "Tail");
        return Fixture("Generic.Byte.Explicit", "generic-byte-fixed", "Explicit", null, "fixed-width-primitive", false, false, [], value, fields);
    }

    private static ILayoutEvidenceFixture CreateExplicitGenericInt64()
    {
        var value = new LayoutExplicitGenericInt64 { Prefix = 0x41, Value = 0x1020304050607080, Tail = 0x1112131415161718 };
        var fields = new LayoutEvidenceFieldMap<LayoutExplicitGenericInt64>(); fields.Add(ref value, ref value.Prefix, "Prefix"); fields.Add(ref value, ref value.Value, "Value"); fields.Add(ref value, ref value.Tail, "Tail");
        return Fixture("Generic.Int64.Explicit", "generic-int64-fixed", "Explicit", null, "fixed-width-primitive", false, false, [], value, fields);
    }

    private static ILayoutEvidenceFixture CreateExplicitGenericGuid()
    {
        var value = new LayoutExplicitGenericGuid { Prefix = 0x41, Value = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), Tail = 0x1112131415161718 };
        var fields = new LayoutEvidenceFieldMap<LayoutExplicitGenericGuid>(); fields.Add(ref value, ref value.Prefix, "Prefix"); fields.Add(ref value, ref value.Value, "Value"); fields.Add(ref value, ref value.Tail, "Tail");
        return Fixture("Generic.Guid.Explicit", "generic-guid-framework", "Explicit", null, "fixed-width-framework", false, false, ["Value"], value, fields);
    }

    private static DateTimeOffset EvidenceOffset()
        => new(2026, 8, 31, 13, 45, 12, TimeSpan.FromHours(5.5));

    private static ILayoutEvidenceFixture CreateAutoGenericDateTimeOffset()
    {
        var value = new LayoutAutoGeneric<DateTimeOffset> { Prefix = 0x44, Value = EvidenceOffset(), Tail = 0x3132333435363738 };
        var fields = new LayoutEvidenceFieldMap<LayoutAutoGeneric<DateTimeOffset>>(); fields.Add(ref value, ref value.Prefix, "Prefix"); fields.Add(ref value, ref value.Value, "Value"); fields.Add(ref value, ref value.Tail, "Tail");
        return Fixture("Generic.DateTimeOffset.Auto", "generic-datetimeoffset-framework", "Auto", null, "fixed-width-framework", false, false, ["Value"], value, fields, static (left, right) => left.Prefix == right.Prefix && left.Tail == right.Tail && left.Value.EqualsExact(right.Value));
    }

    private static ILayoutEvidenceFixture CreateSequentialGenericDateTimeOffset()
    {
        var value = new LayoutSequentialGeneric<DateTimeOffset> { Prefix = 0x44, Value = EvidenceOffset(), Tail = 0x3132333435363738 };
        var fields = new LayoutEvidenceFieldMap<LayoutSequentialGeneric<DateTimeOffset>>(); fields.Add(ref value, ref value.Prefix, "Prefix"); fields.Add(ref value, ref value.Value, "Value"); fields.Add(ref value, ref value.Tail, "Tail");
        return Fixture("Generic.DateTimeOffset.Sequential", "generic-datetimeoffset-framework", "Sequential", null, "fixed-width-framework", false, false, ["Value"], value, fields, static (left, right) => left.Prefix == right.Prefix && left.Tail == right.Tail && left.Value.EqualsExact(right.Value));
    }

    private static ILayoutEvidenceFixture CreateExplicitGenericDateTimeOffset()
    {
        var value = new LayoutExplicitGenericDateTimeOffset { Prefix = 0x44, Value = EvidenceOffset(), Tail = 0x3132333435363738 };
        var fields = new LayoutEvidenceFieldMap<LayoutExplicitGenericDateTimeOffset>(); fields.Add(ref value, ref value.Prefix, "Prefix"); fields.Add(ref value, ref value.Value, "Value"); fields.Add(ref value, ref value.Tail, "Tail");
        return Fixture("Generic.DateTimeOffset.Explicit", "generic-datetimeoffset-framework", "Explicit", null, "fixed-width-framework", false, false, ["Value"], value, fields, static (left, right) => left.Prefix == right.Prefix && left.Tail == right.Tail && left.Value.EqualsExact(right.Value));
    }

    private static ILayoutEvidenceFixture CreateDateTimeOffsetContainerAuto()
    {
        var value = new LayoutDateTimeOffsetAuto { Prefix = 0x62, Value = EvidenceOffset(), Tail = 0x6162636465666768 };
        var fields = new LayoutEvidenceFieldMap<LayoutDateTimeOffsetAuto>(); fields.Add(ref value, ref value.Prefix, "Prefix"); fields.Add(ref value, ref value.Value, "Value"); fields.Add(ref value, ref value.Tail, "Tail");
        return Fixture("DateTimeOffsetContainer.Auto", "datetimeoffset-container-framework", "Auto", null, "fixed-width-framework", false, false, ["Value"], value, fields, static (left, right) => left.Prefix == right.Prefix && left.Tail == right.Tail && left.Value.EqualsExact(right.Value));
    }

    private static ILayoutEvidenceFixture CreateDateTimeOffsetContainerSequential()
    {
        var value = new LayoutDateTimeOffsetSequential { Prefix = 0x62, Value = EvidenceOffset(), Tail = 0x6162636465666768 };
        var fields = new LayoutEvidenceFieldMap<LayoutDateTimeOffsetSequential>(); fields.Add(ref value, ref value.Prefix, "Prefix"); fields.Add(ref value, ref value.Value, "Value"); fields.Add(ref value, ref value.Tail, "Tail");
        return Fixture("DateTimeOffsetContainer.Sequential", "datetimeoffset-container-framework", "Sequential", null, "fixed-width-framework", false, false, ["Value"], value, fields, static (left, right) => left.Prefix == right.Prefix && left.Tail == right.Tail && left.Value.EqualsExact(right.Value));
    }

    private static ILayoutEvidenceFixture CreateDateTimeOffsetContainerExplicit()
    {
        var value = new LayoutDateTimeOffsetExplicit { Prefix = 0x62, Value = EvidenceOffset(), Tail = 0x6162636465666768 };
        var fields = new LayoutEvidenceFieldMap<LayoutDateTimeOffsetExplicit>(); fields.Add(ref value, ref value.Prefix, "Prefix"); fields.Add(ref value, ref value.Value, "Value"); fields.Add(ref value, ref value.Tail, "Tail");
        return Fixture("DateTimeOffsetContainer.Explicit", "datetimeoffset-container-framework", "Explicit", null, "fixed-width-framework", false, false, ["Value"], value, fields, static (left, right) => left.Prefix == right.Prefix && left.Tail == right.Tail && left.Value.EqualsExact(right.Value));
    }

    private static ILayoutEvidenceFixture CreateNativeAuto()
    {
        var value = new LayoutNativeAuto { A = (nint)0x12345678, B = (nuint)0x23456789 };
        var fields = new LayoutEvidenceFieldMap<LayoutNativeAuto>(); fields.Add(ref value, ref value.A, "A"); fields.Add(ref value, ref value.B, "B");
        return Fixture("NativeWidth.Auto", "native-width-pair", "Auto", null, "native-width", true, false, [], value, fields);
    }

    private static ILayoutEvidenceFixture CreateNativeSequential()
    {
        var value = new LayoutNativeSequential { A = (nint)0x12345678, B = (nuint)0x23456789 };
        var fields = new LayoutEvidenceFieldMap<LayoutNativeSequential>(); fields.Add(ref value, ref value.A, "A"); fields.Add(ref value, ref value.B, "B");
        return Fixture("NativeWidth.Sequential", "native-width-pair", "Sequential", null, "native-width", true, false, [], value, fields);
    }

    private static ILayoutEvidenceFixture CreateNativeExplicit()
    {
        var value = new LayoutNativeExplicit { A = (nint)0x12345678, B = (nuint)0x23456789 };
        var fields = new LayoutEvidenceFieldMap<LayoutNativeExplicit>(); fields.Add(ref value, ref value.A, "A"); fields.Add(ref value, ref value.B, "B");
        return Fixture("NativeWidth.Explicit", "native-width-pair", "Explicit", null, "native-width", true, false, [], value, fields);
    }

    private static IEnumerable<ILayoutEvidenceFixture> CreateLegacyControls()
    {
        var offset = EvidenceOffset();
        var guid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var mixed = new AutoMixed { A = 0x12, B = 0x2345, C = 0x3456789A, D = 0x0102030405060708, E = 1234567890.123456789m, F = guid, G = offset };
        var mixedFields = new LayoutEvidenceFieldMap<AutoMixed>();
        mixedFields.Add(ref mixed, ref mixed.A, "A"); mixedFields.Add(ref mixed, ref mixed.B, "B"); mixedFields.Add(ref mixed, ref mixed.C, "C"); mixedFields.Add(ref mixed, ref mixed.D, "D"); mixedFields.Add(ref mixed, ref mixed.E, "E"); mixedFields.Add(ref mixed, ref mixed.F, "F"); mixedFields.Add(ref mixed, ref mixed.G, "G");
        yield return Fixture("AutoMixed", "legacy-auto-mixed", "Auto", null, "fixed-width-framework", false, true, ["E", "F", "G"], mixed, mixedFields);

        var nested = new AutoNested { Prefix = 0x31, Inner = mixed, Tail = 0x1122334455667788 };
        var nestedFields = new LayoutEvidenceFieldMap<AutoNested>();
        nestedFields.Add(ref nested, ref nested.Prefix, "Prefix"); nestedFields.Add(ref nested, ref nested.Inner.A, "Inner.A"); nestedFields.Add(ref nested, ref nested.Inner.B, "Inner.B"); nestedFields.Add(ref nested, ref nested.Inner.C, "Inner.C"); nestedFields.Add(ref nested, ref nested.Inner.D, "Inner.D"); nestedFields.Add(ref nested, ref nested.Inner.E, "Inner.E"); nestedFields.Add(ref nested, ref nested.Inner.F, "Inner.F"); nestedFields.Add(ref nested, ref nested.Inner.G, "Inner.G"); nestedFields.Add(ref nested, ref nested.Tail, "Tail");
        yield return Fixture("AutoNested", "legacy-auto-nested", "Auto", null, "fixed-width-framework", false, true, ["Inner.E", "Inner.F", "Inner.G"], nested, nestedFields);

        yield return CreateLegacyGeneric("AutoGenericByte", (byte)0x52, []);
        yield return CreateLegacyGeneric("AutoGenericInt64", 0x1020304050607080L, []);
        yield return CreateLegacyGeneric("AutoGenericGuid", guid, ["Value"]);
        yield return CreateLegacyGenericDateTimeOffset(offset);

        var padding = new AutoPaddingHeavy { Prefix = 0x51, Value = 0x4142434445464748, Suffix = 0x52 };
        var paddingFields = new LayoutEvidenceFieldMap<AutoPaddingHeavy>(); paddingFields.Add(ref padding, ref padding.Prefix, "Prefix"); paddingFields.Add(ref padding, ref padding.Value, "Value"); paddingFields.Add(ref padding, ref padding.Suffix, "Suffix");
        yield return Fixture("AutoPaddingHeavy", "legacy-auto-padding-heavy", "Auto", null, "fixed-width-primitive", false, true, [], padding, paddingFields);

        var sequentialDto = new DateTimeOffsetContainer { Prefix = 0x61, Value = offset, Tail = 0x5152535455565758 };
        var sequentialDtoFields = new LayoutEvidenceFieldMap<DateTimeOffsetContainer>(); sequentialDtoFields.Add(ref sequentialDto, ref sequentialDto.Prefix, "Prefix"); sequentialDtoFields.Add(ref sequentialDto, ref sequentialDto.Value, "Value"); sequentialDtoFields.Add(ref sequentialDto, ref sequentialDto.Tail, "Tail");
        yield return Fixture("DateTimeOffsetContainer", "legacy-datetimeoffset-container", "Sequential", null, "fixed-width-framework", false, true, ["Value"], sequentialDto, sequentialDtoFields, static (left, right) => left.Prefix == right.Prefix && left.Tail == right.Tail && left.Value.EqualsExact(right.Value));

        var autoDto = new AutoDateTimeOffsetContainer { Prefix = 0x62, Value = offset, Tail = 0x6162636465666768 };
        var autoDtoFields = new LayoutEvidenceFieldMap<AutoDateTimeOffsetContainer>(); autoDtoFields.Add(ref autoDto, ref autoDto.Prefix, "Prefix"); autoDtoFields.Add(ref autoDto, ref autoDto.Value, "Value"); autoDtoFields.Add(ref autoDto, ref autoDto.Tail, "Tail");
        yield return Fixture("AutoDateTimeOffsetContainer", "legacy-auto-datetimeoffset-container", "Auto", null, "fixed-width-framework", false, true, ["Value"], autoDto, autoDtoFields, static (left, right) => left.Prefix == right.Prefix && left.Tail == right.Tail && left.Value.EqualsExact(right.Value));
    }

    private static ILayoutEvidenceFixture CreateLegacyGeneric<T>(string id, T item, IReadOnlyList<string> frameworkRawFields) where T : unmanaged
    {
        var value = new AutoGeneric<T> { Prefix = 0x43, Value = item, Tail = 0x2122232425262728 };
        var fields = new LayoutEvidenceFieldMap<AutoGeneric<T>>(); fields.Add(ref value, ref value.Prefix, "Prefix"); fields.Add(ref value, ref value.Value, "Value"); fields.Add(ref value, ref value.Tail, "Tail");
        return Fixture(id, "legacy-auto-generic", "Auto", null, frameworkRawFields.Count == 0 ? "fixed-width-primitive" : "fixed-width-framework", false, true, frameworkRawFields, value, fields);
    }

    private static ILayoutEvidenceFixture CreateLegacyGenericDateTimeOffset(DateTimeOffset item)
    {
        var value = new AutoGeneric<DateTimeOffset> { Prefix = 0x44, Value = item, Tail = 0x3132333435363738 };
        var fields = new LayoutEvidenceFieldMap<AutoGeneric<DateTimeOffset>>(); fields.Add(ref value, ref value.Prefix, "Prefix"); fields.Add(ref value, ref value.Value, "Value"); fields.Add(ref value, ref value.Tail, "Tail");
        return Fixture("AutoGenericDateTimeOffset", "legacy-auto-generic", "Auto", null, "fixed-width-framework", false, true, ["Value"], value, fields, static (left, right) => left.Prefix == right.Prefix && left.Tail == right.Tail && left.Value.EqualsExact(right.Value));
    }

    private static LayoutEvidenceFixture<T> Fixture<T>(
        string id,
        string shape,
        string layoutKind,
        int? pack,
        string widthDomain,
        bool nativeWidth,
        bool legacyControl,
        IReadOnlyList<string> frameworkRawFields,
        T value,
        LayoutEvidenceFieldMap<T> fields,
        Func<T, T, bool>? logicalEquals = null) where T : unmanaged
        => new(id, shape, layoutKind, pack, widthDomain, nativeWidth, legacyControl, frameworkRawFields, value, fields, logicalEquals);
}

internal static class LayoutEvidenceProbe
{
    internal static string ProduceJson(
        string sharpLinkCommit,
        string sdkVersion,
        string targetFramework,
        string profile,
        string? expectedRuntimeFamily = null,
        string? executionEnvironmentOverride = null)
    {
        LayoutEvidenceProfiles.Validate(profile);
        var runtime = CreateRuntimeIdentity(sharpLinkCommit, sdkVersion, targetFramework, expectedRuntimeFamily, executionEnvironmentOverride);
        var envelope = new LayoutEvidenceEnvelope { Profile = profile, Runtime = runtime };
        foreach (var fixture in LayoutEvidenceFixtureRegistry.ForProfile(profile))
        {
            var bytes = fixture.Serialize();
            var item = fixture.CreateCase(bytes);
            envelope.Cases.Add(item);
            envelope.CaseBytesBase64.Add(item.Id, Convert.ToBase64String(bytes));
        }
        return JsonSerializer.Serialize(envelope, typeof(LayoutEvidenceEnvelope), LayoutEvidenceJsonContext.Default);
    }

    internal static string VerifyJson(
        string envelopesJson,
        string sharpLinkCommit,
        string sdkVersion,
        string targetFramework,
        string? expectedRuntimeFamily = null,
        string? executionEnvironmentOverride = null)
    {
        var envelopes = JsonSerializer.Deserialize(envelopesJson, typeof(List<LayoutEvidenceEnvelope>), LayoutEvidenceJsonContext.Default) as List<LayoutEvidenceEnvelope>
            ?? throw new InvalidOperationException("Failed to deserialize UnsafeBlit layout evidence envelopes.");
        var consumer = CreateRuntimeIdentity(sharpLinkCommit, sdkVersion, targetFramework, expectedRuntimeFamily, executionEnvironmentOverride);
        var report = new LayoutEvidenceReport { Consumer = consumer };
        foreach (var envelope in envelopes
                     .OrderBy(static item => item.Runtime.PlatformTag, StringComparer.Ordinal)
                     .ThenBy(static item => item.Profile, StringComparer.Ordinal))
        {
            ValidateEnvelope(envelope, sharpLinkCommit);
            foreach (var producerCase in envelope.Cases.OrderBy(static item => item.Id, StringComparer.Ordinal))
            {
                var fixture = LayoutEvidenceFixtureRegistry.ById[producerCase.Id];
                var producerBytes = Convert.FromBase64String(envelope.CaseBytesBase64[producerCase.Id]);
                report.Results.Add(fixture.Verify(envelope.Profile, producerBytes, producerCase, envelope.Runtime, consumer));
            }
        }
        return JsonSerializer.Serialize(report, typeof(LayoutEvidenceReport), LayoutEvidenceJsonContext.Default);
    }

    private static void ValidateEnvelope(LayoutEvidenceEnvelope envelope, string expectedCommit)
    {
        if (envelope.SchemaVersion != 1 || envelope.Runtime.SchemaVersion != 1)
            throw new InvalidOperationException($"Unsupported layout evidence schema from {envelope.Runtime.PlatformTag}.");
        LayoutEvidenceProfiles.Validate(envelope.Profile);
        if (!string.Equals(envelope.Runtime.SharpLinkCommit, expectedCommit, StringComparison.Ordinal))
            throw new InvalidOperationException($"Layout evidence commit mismatch from {envelope.Runtime.PlatformTag}: {envelope.Runtime.SharpLinkCommit} != {expectedCommit}.");

        var expected = LayoutEvidenceFixtureRegistry.ForProfile(envelope.Profile).OrderBy(static item => item.Id, StringComparer.Ordinal).ToArray();
        var actual = envelope.Cases.OrderBy(static item => item.Id, StringComparer.Ordinal).ToArray();
        if (actual.Length != expected.Length || envelope.CaseBytesBase64.Count != expected.Length)
            throw new InvalidOperationException($"Layout evidence fixture count mismatch from {envelope.Runtime.PlatformTag}/{envelope.Profile}.");

        for (var index = 0; index < expected.Length; index++)
        {
            var fixture = expected[index];
            var item = actual[index];
            if (!string.Equals(item.Id, fixture.Id, StringComparison.Ordinal)
                || !string.Equals(item.LogicalShape, fixture.LogicalShape, StringComparison.Ordinal)
                || !string.Equals(item.LayoutKind, fixture.LayoutKind, StringComparison.Ordinal)
                || item.Pack != fixture.Pack
                || !string.Equals(item.WidthDomain, fixture.WidthDomain, StringComparison.Ordinal)
                || item.NativeWidth != fixture.NativeWidth
                || item.LegacyControl != fixture.LegacyControl
                || !item.FrameworkRawFields.SequenceEqual(fixture.FrameworkRawFields, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"Layout evidence metadata mismatch for {envelope.Runtime.PlatformTag}/{item.Id}.");
            }
            if (!envelope.CaseBytesBase64.TryGetValue(item.Id, out var base64))
                throw new InvalidOperationException($"Missing layout evidence bytes for {envelope.Runtime.PlatformTag}/{item.Id}.");
            var bytes = Convert.FromBase64String(base64);
            if (bytes.Length != item.Size || !string.Equals(Hash(bytes), item.WireSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Layout evidence wire integrity mismatch for {envelope.Runtime.PlatformTag}/{item.Id}.");
        }
    }

    private static LayoutEvidenceRuntimeIdentity CreateRuntimeIdentity(
        string sharpLinkCommit,
        string sdkVersion,
        string targetFramework,
        string? expectedRuntimeFamily,
        string? executionEnvironmentOverride)
    {
        var os = OperatingSystem.IsBrowser() ? "browser"
            : OperatingSystem.IsAndroid() ? "android"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macos"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux"
            : "unknown";
        var (runtimeFamily, runtimeFamilySource) = DetectRuntimeFamily();
        if (!string.IsNullOrWhiteSpace(expectedRuntimeFamily)
            && !string.Equals(runtimeFamily, expectedRuntimeFamily, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Layout evidence runtime mismatch: expected={expectedRuntimeFamily}, observed={runtimeFamily}.");
        }
        var compilationMode = !RuntimeFeature.IsDynamicCodeSupported ? "AOT"
            : RuntimeFeature.IsDynamicCodeCompiled ? "JIT"
            : "Interpreter";
        var processArchitecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var runtimeIdentifier = RuntimeInformation.RuntimeIdentifier;
        if (OperatingSystem.IsAndroid() && !runtimeIdentifier.StartsWith("android-", StringComparison.OrdinalIgnoreCase))
            runtimeIdentifier = $"android-{processArchitecture}";
        var executionEnvironment = executionEnvironmentOverride
            ?? (OperatingSystem.IsBrowser() ? "browser" : OperatingSystem.IsAndroid() ? "android-runtime" : "hosted-desktop");
        var frameworkTag = GetFrameworkTag(targetFramework);
        return new LayoutEvidenceRuntimeIdentity
        {
            SharpLinkCommit = string.IsNullOrWhiteSpace(sharpLinkCommit) ? "unknown" : sharpLinkCommit,
            TargetFramework = targetFramework,
            FrameworkDescription = RuntimeInformation.FrameworkDescription,
            RuntimeFamily = runtimeFamily,
            RuntimeFamilySource = runtimeFamilySource,
            RuntimeVersion = Environment.Version.ToString(),
            SdkVersion = string.IsNullOrWhiteSpace(sdkVersion) ? "unknown" : sdkVersion,
            RuntimeIdentifier = runtimeIdentifier,
            ExecutionEnvironment = executionEnvironment,
            Os = os,
            OsVersion = RuntimeInformation.OSDescription,
            ProcessArchitecture = processArchitecture,
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            PointerSize = IntPtr.Size,
            IsLittleEndian = BitConverter.IsLittleEndian,
            CompilationMode = compilationMode,
            PlatformTag = $"{os}-{processArchitecture}-{executionEnvironment}-{runtimeFamily.ToLowerInvariant()}-{frameworkTag}"
        };
    }

    private static (string Family, string Source) DetectRuntimeFamily()
    {
        if (OperatingSystem.IsBrowser())
            return ("Mono", "platform-runtime-pack");
        if (!OperatingSystem.IsAndroid())
            return (Type.GetType("Mono.Runtime") is null ? "CoreCLR" : "Mono", "runtime-reflection");
        var maps = File.ReadAllText("/proc/self/maps");
        var mono = maps.Contains("libmonosgen-2.0.so", StringComparison.Ordinal);
        var coreClr = maps.Contains("libcoreclr.so", StringComparison.Ordinal);
        if (mono == coreClr)
            throw new InvalidOperationException($"Unable to identify Android layout evidence runtime: monoLoaded={mono}, coreClrLoaded={coreClr}.");
        return (mono ? "Mono" : "CoreCLR", "loaded-runtime-library");
    }

    private static string GetFrameworkTag(string targetFramework)
    {
        var framework = targetFramework.Split('/', 2, StringSplitOptions.TrimEntries)[0];
        var separator = framework.IndexOf('-');
        if (separator >= 0) framework = framework[..separator];
        separator = framework.IndexOf('.');
        if (separator >= 0) framework = framework[..separator];
        return framework.ToLowerInvariant();
    }

    private static string Hash(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

internal static class LayoutEvidenceSummaryBuilder
{
    internal static LayoutEvidenceSummary Build(IReadOnlyList<LayoutEvidenceReport> reports)
    {
        if (reports.Count == 0)
            throw new InvalidOperationException("No UnsafeBlit layout evidence reports were supplied.");
        var commits = reports.Select(static report => report.Consumer.SharpLinkCommit).Distinct(StringComparer.Ordinal).ToArray();
        if (commits.Length != 1 || string.IsNullOrWhiteSpace(commits[0]) || string.Equals(commits[0], "unknown", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Layout evidence summary requires one known SharpLink commit; observed [{string.Join(", ", commits)}].");
        var results = reports.SelectMany(static report => report.Results)
            .OrderBy(static item => item.Profile, StringComparer.Ordinal)
            .ThenBy(static item => item.Fixture, StringComparer.Ordinal)
            .ThenBy(static item => item.Producer, StringComparer.Ordinal)
            .ThenBy(static item => item.Consumer, StringComparer.Ordinal)
            .ToList();
        var consumers = reports.Select(static report => report.Consumer.PlatformTag).Distinct(StringComparer.Ordinal).OrderBy(static item => item, StringComparer.Ordinal).ToArray();
        foreach (var profile in new[] { LayoutEvidenceProfiles.FixedWidth, LayoutEvidenceProfiles.NativeWidth })
        {
            var producers = results.Where(item => string.Equals(item.Profile, profile, StringComparison.Ordinal)).Select(static item => item.Producer).Distinct(StringComparer.Ordinal).OrderBy(static item => item, StringComparer.Ordinal).ToArray();
            if (!producers.SequenceEqual(consumers, StringComparer.Ordinal))
                throw new InvalidOperationException($"Layout evidence profile {profile} is not a complete producer/consumer matrix: producers=[{string.Join(", ", producers)}], consumers=[{string.Join(", ", consumers)}].");
        }

        var conclusions = results.GroupBy(static item => item.Fixture, StringComparer.Ordinal)
            .Select(group => BuildConclusion(group.Key, group.ToArray()))
            .OrderBy(static item => item.LogicalShape, StringComparer.Ordinal)
            .ThenBy(static item => item.LayoutKind, StringComparer.Ordinal)
            .ThenBy(static item => item.Pack)
            .ToList();
        return new LayoutEvidenceSummary
        {
            SharpLinkCommit = commits[0],
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Platforms = [.. consumers],
            Fixtures = conclusions,
            Hypotheses = BuildHypotheses(conclusions),
            Results = results
        };
    }

    internal static string CreateMarkdown(LayoutEvidenceSummary summary)
    {
        var lines = new List<string>
        {
            "# UnsafeBlit layout compatibility evidence",
            "",
            $"Commit: `{summary.SharpLinkCommit}`",
            $"Platforms: {string.Join(", ", summary.Platforms.Select(static item => $"`{item}`"))}",
            "",
            "## Hypotheses",
            ""
        };
        foreach (var hypothesis in summary.Hypotheses)
        {
            lines.Add($"- **{hypothesis.Id}** — {(hypothesis.SupportedByObservedMatrix ? "supported by this matrix" : "not established by this matrix")}: {hypothesis.Question}");
            if (hypothesis.Evidence.Count != 0) lines.Add($"  Evidence: {string.Join("; ", hypothesis.Evidence)}");
            if (hypothesis.CounterEvidence.Count != 0) lines.Add($"  Counter-evidence: {string.Join("; ", hypothesis.CounterEvidence)}");
        }
        lines.AddRange(["", "## Fixture conclusions", "", "| Fixture | Shape | Layout | Pack | Domain | Wire compatible | Raw stable | Size mismatch | Offset mismatch | Byte diff | Logical mismatch | Padding-only | Nested diff | Framework raw diff | Pointer diff |", "|---|---|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|"]);
        foreach (var item in summary.Fixtures)
        {
            lines.Add($"| {item.Fixture} | {item.LogicalShape} | {item.LayoutKind} | {(item.Pack?.ToString() ?? "default")} | {item.WidthDomain} | {item.RawWireCompatibleEdges}/{item.CrossPlatformEdges} | {item.RawRepresentationStableEdges}/{item.CrossPlatformEdges} | {item.SizeMismatchEdges} | {item.FieldOffsetMismatchEdges} | {item.RawByteDifferenceEdges} | {item.LogicalMismatchEdges} | {item.PaddingOnlyDifferenceEdges} | {item.NestedRepresentationDifferenceEdges} | {item.FrameworkRawDifferenceEdges} | {item.PointerWidthMismatchEdges} |");
        }
        var failures = summary.Results.Where(static item => item.Producer != item.Consumer && !item.RawWireCompatible).ToArray();
        lines.AddRange(["", "## Cross-platform incompatibility details", ""]);
        if (failures.Length == 0)
        {
            lines.Add("No cross-platform logical UnsafeBlit incompatibilities were observed.");
        }
        else
        {
            lines.Add("| Fixture | Producer → Consumer | Classification | Size | Offset differences | Byte differences | Padding-only | Nested | Framework raw | Pointer width | Logical | ");
            lines.Add("|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var result in failures)
            {
                var offsets = result.FieldOffsetDifferences.Count == 0 ? "none" : string.Join(",", result.FieldOffsetDifferences.Select(static item => $"{item.Field}:{item.Producer}->{item.Consumer}"));
                var bytes = result.DifferingByteOffsets.Count == 0 ? "none" : string.Join(",", result.DifferingByteOffsets);
                lines.Add($"| {result.Fixture} | {result.Producer} → {result.Consumer} | {result.Classification} | {result.ProducerSize}->{result.ConsumerSize} | {offsets} | {bytes} | {result.DifferencesOnlyInPaddingOnBothSides} | {result.DifferingBytesTouchNestedField || result.NestedFieldMetadataMismatch} | {result.DifferingBytesTouchFrameworkRawField} | {result.ProducerPointerSize}->{result.ConsumerPointerSize} | {result.LogicalEquality?.ToString() ?? "n/a"} |");
            }
        }
        return string.Join("\n", lines) + "\n";
    }

    private static LayoutFixtureConclusion BuildConclusion(string fixture, IReadOnlyList<LayoutEvidenceResult> results)
    {
        var sample = results[0];
        var cross = results.Where(static item => !string.Equals(item.Producer, item.Consumer, StringComparison.Ordinal)).ToArray();
        return new LayoutFixtureConclusion
        {
            Fixture = fixture,
            LogicalShape = sample.LogicalShape,
            LayoutKind = sample.LayoutKind,
            Pack = sample.Pack,
            WidthDomain = sample.WidthDomain,
            NativeWidth = sample.NativeWidth,
            LegacyControl = sample.LegacyControl,
            CrossPlatformEdges = cross.Length,
            RawWireCompatibleEdges = cross.Count(static item => item.RawWireCompatible),
            RawRepresentationStableEdges = cross.Count(static item => item.RawRepresentationStable),
            SizeMismatchEdges = cross.Count(static item => !item.SizeEqual),
            FieldOffsetMismatchEdges = cross.Count(static item => !item.FieldOffsetsEqual),
            RawByteDifferenceEdges = cross.Count(static item => !item.ByteForByteEquality),
            LogicalMismatchEdges = cross.Count(static item => item.LogicalEquality != true),
            PaddingOnlyDifferenceEdges = cross.Count(static item => item.DifferencesOnlyInPaddingOnBothSides),
            NestedRepresentationDifferenceEdges = cross.Count(static item => item.NestedFieldMetadataMismatch || item.DifferingBytesTouchNestedField),
            FrameworkRawDifferenceEdges = cross.Count(static item => item.DifferingBytesTouchFrameworkRawField),
            PointerWidthMismatchEdges = cross.Count(static item => item.PointerWidthMismatch),
            AllCrossPlatformRawWireCompatible = cross.Length != 0 && cross.All(static item => item.RawWireCompatible),
            AllCrossPlatformRawRepresentationStable = cross.Length != 0 && cross.All(static item => item.RawRepresentationStable)
        };
    }

    private static List<LayoutEvidenceHypothesis> BuildHypotheses(IReadOnlyList<LayoutFixtureConclusion> fixtures)
    {
        var matchedShapes = fixtures.Where(static item => !item.LegacyControl && !item.NativeWidth)
            .GroupBy(static item => item.LogicalShape, StringComparer.Ordinal)
            .ToArray();
        var autoFailsSeqExplicitPass = new List<string>();
        var onlyExplicitPasses = new List<string>();
        foreach (var shape in matchedShapes)
        {
            var auto = shape.FirstOrDefault(static item => item.LayoutKind == "Auto");
            var sequential = shape.FirstOrDefault(static item => item.LayoutKind == "Sequential" && item.Pack is null);
            var explicitLayout = shape.FirstOrDefault(static item => item.LayoutKind == "Explicit");
            if (auto is null || sequential is null || explicitLayout is null) continue;
            if (!auto.AllCrossPlatformRawWireCompatible && sequential.AllCrossPlatformRawWireCompatible && explicitLayout.AllCrossPlatformRawWireCompatible)
                autoFailsSeqExplicitPass.Add(shape.Key);
            if (!auto.AllCrossPlatformRawWireCompatible && !sequential.AllCrossPlatformRawWireCompatible && explicitLayout.AllCrossPlatformRawWireCompatible)
                onlyExplicitPasses.Add(shape.Key);
        }

        var primitiveExplicit = fixtures.Where(static item => !item.LegacyControl && item.LayoutKind == "Explicit" && item.WidthDomain == "fixed-width-primitive").ToArray();
        var frameworkExplicit = fixtures.Where(static item => !item.LegacyControl && item.LayoutKind == "Explicit" && item.WidthDomain == "fixed-width-framework").ToArray();
        var primitivePass = primitiveExplicit.Where(static item => item.AllCrossPlatformRawWireCompatible).Select(static item => item.Fixture).ToArray();
        var frameworkFail = frameworkExplicit.Where(static item => !item.AllCrossPlatformRawWireCompatible).Select(static item => item.Fixture).ToArray();

        var fixedSequentialExplicit = fixtures.Where(static item => !item.LegacyControl && item.WidthDomain == "fixed-width-primitive" && (item.LayoutKind == "Sequential" || item.LayoutKind == "Explicit")).ToArray();
        var fixedFailures = fixedSequentialExplicit.Where(static item => !item.AllCrossPlatformRawWireCompatible).Select(static item => item.Fixture).ToArray();

        return
        [
            new LayoutEvidenceHypothesis
            {
                Id = "H1",
                Question = "Auto is incompatible while matched Sequential and Explicit variants are compatible.",
                SupportedByObservedMatrix = autoFailsSeqExplicitPass.Count != 0,
                Evidence = autoFailsSeqExplicitPass,
                CounterEvidence = matchedShapes.Where(shape => shape.Any(static item => item.LayoutKind == "Auto" && item.AllCrossPlatformRawWireCompatible)).Select(static shape => $"{shape.Key}: Auto remained compatible").ToList()
            },
            new LayoutEvidenceHypothesis
            {
                Id = "H2",
                Question = "Sequential can remain incompatible where only the matched Explicit variant is compatible.",
                SupportedByObservedMatrix = onlyExplicitPasses.Count != 0,
                Evidence = onlyExplicitPasses
            },
            new LayoutEvidenceHypothesis
            {
                Id = "H3",
                Question = "Explicit primitive-only shapes are compatible while Explicit shapes containing framework raw representations (for example DateTimeOffset) can remain incompatible.",
                SupportedByObservedMatrix = primitivePass.Length != 0 && frameworkFail.Length != 0,
                Evidence = primitivePass.Select(static item => $"primitive compatible: {item}").Concat(frameworkFail.Select(static item => $"framework raw incompatible: {item}")).ToList(),
                CounterEvidence = frameworkExplicit.Where(static item => item.AllCrossPlatformRawWireCompatible).Select(static item => $"framework raw compatible: {item.Fixture}").ToList()
            },
            new LayoutEvidenceHypothesis
            {
                Id = "H4",
                Question = "Fixed-width primitive Sequential/Explicit fixtures form one cross-platform raw-wire compatibility domain across the observed CoreCLR, Mono, and Browser matrix.",
                SupportedByObservedMatrix = fixedSequentialExplicit.Length != 0 && fixedFailures.Length == 0,
                Evidence = fixedFailures.Length == 0 ? [.. fixedSequentialExplicit.Select(static item => $"compatible: {item.Fixture}")] : [],
                CounterEvidence = [.. fixedFailures.Select(static item => $"incompatible: {item}")]
            }
        ];
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(LayoutEvidenceEnvelope))]
[JsonSerializable(typeof(List<LayoutEvidenceEnvelope>))]
[JsonSerializable(typeof(LayoutEvidenceReport))]
[JsonSerializable(typeof(List<LayoutEvidenceReport>))]
[JsonSerializable(typeof(LayoutEvidenceSummary))]
internal partial class LayoutEvidenceJsonContext : JsonSerializerContext
{
}

[StructLayout(LayoutKind.Auto)]
internal struct LayoutMixedAuto { public byte A; public short B; public int C; public long D; public double E; }
[StructLayout(LayoutKind.Sequential)]
internal struct LayoutMixedSequential { public byte A; public short B; public int C; public long D; public double E; }
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct LayoutMixedExplicit { [FieldOffset(0)] public byte A; [FieldOffset(2)] public short B; [FieldOffset(4)] public int C; [FieldOffset(8)] public long D; [FieldOffset(16)] public double E; }

[StructLayout(LayoutKind.Auto)]
internal struct LayoutPaddingAuto { public byte Prefix; public long Value; public byte Suffix; }
[StructLayout(LayoutKind.Sequential)]
internal struct LayoutPaddingSequential { public byte Prefix; public long Value; public byte Suffix; }
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct LayoutPaddingSequentialPack1 { public byte Prefix; public long Value; public byte Suffix; }
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct LayoutPaddingSequentialPack4 { public byte Prefix; public long Value; public byte Suffix; }
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct LayoutPaddingSequentialPack8 { public byte Prefix; public long Value; public byte Suffix; }
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct LayoutPaddingExplicit { [FieldOffset(0)] public byte Prefix; [FieldOffset(8)] public long Value; [FieldOffset(16)] public byte Suffix; }

[StructLayout(LayoutKind.Auto)]
internal struct LayoutInnerAuto { public byte A; public int B; }
[StructLayout(LayoutKind.Auto)]
internal struct LayoutNestedAuto { public short Prefix; public LayoutInnerAuto Inner; public long Tail; }
[StructLayout(LayoutKind.Sequential)]
internal struct LayoutInnerSequential { public byte A; public int B; }
[StructLayout(LayoutKind.Sequential)]
internal struct LayoutNestedSequential { public short Prefix; public LayoutInnerSequential Inner; public long Tail; }
[StructLayout(LayoutKind.Explicit, Size = 8)]
internal struct LayoutInnerExplicit { [FieldOffset(0)] public byte A; [FieldOffset(4)] public int B; }
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct LayoutNestedExplicit { [FieldOffset(0)] public short Prefix; [FieldOffset(4)] public LayoutInnerExplicit Inner; [FieldOffset(16)] public long Tail; }

[StructLayout(LayoutKind.Auto)]
internal struct LayoutAutoGeneric<T> where T : unmanaged { public byte Prefix; public T Value; public long Tail; }
[StructLayout(LayoutKind.Sequential)]
internal struct LayoutSequentialGeneric<T> where T : unmanaged { public byte Prefix; public T Value; public long Tail; }
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct LayoutExplicitGenericByte { [FieldOffset(0)] public byte Prefix; [FieldOffset(1)] public byte Value; [FieldOffset(8)] public long Tail; }
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct LayoutExplicitGenericInt64 { [FieldOffset(0)] public byte Prefix; [FieldOffset(8)] public long Value; [FieldOffset(16)] public long Tail; }
[StructLayout(LayoutKind.Explicit, Size = 32)]
internal struct LayoutExplicitGenericGuid { [FieldOffset(0)] public byte Prefix; [FieldOffset(8)] public Guid Value; [FieldOffset(24)] public long Tail; }
[StructLayout(LayoutKind.Explicit, Size = 32)]
internal struct LayoutExplicitGenericDateTimeOffset { [FieldOffset(0)] public byte Prefix; [FieldOffset(8)] public DateTimeOffset Value; [FieldOffset(24)] public long Tail; }

[StructLayout(LayoutKind.Auto)]
internal struct LayoutDateTimeOffsetAuto { public byte Prefix; public DateTimeOffset Value; public long Tail; }
[StructLayout(LayoutKind.Sequential)]
internal struct LayoutDateTimeOffsetSequential { public byte Prefix; public DateTimeOffset Value; public long Tail; }
[StructLayout(LayoutKind.Explicit, Size = 32)]
internal struct LayoutDateTimeOffsetExplicit { [FieldOffset(0)] public byte Prefix; [FieldOffset(8)] public DateTimeOffset Value; [FieldOffset(24)] public long Tail; }

[StructLayout(LayoutKind.Auto)]
internal struct LayoutNativeAuto { public nint A; public nuint B; }
[StructLayout(LayoutKind.Sequential)]
internal struct LayoutNativeSequential { public nint A; public nuint B; }
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct LayoutNativeExplicit { [FieldOffset(0)] public nint A; [FieldOffset(8)] public nuint B; }
