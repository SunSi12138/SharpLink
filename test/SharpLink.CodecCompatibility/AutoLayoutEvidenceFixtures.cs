using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpLink.CodecCompatibility;

internal static class AutoLayoutEvidenceFixtures
{
    [ModuleInitializer]
    internal static void Register()
    {
        if (FixtureRegistry.All is not List<IFixture> fixtures ||
            FixtureRegistry.ById is not Dictionary<string, IFixture> byId)
        {
            throw new InvalidOperationException(
                "Compatibility fixture registry must remain mutable during module initialization.");
        }

        var offset = new DateTimeOffset(2026, 8, 31, 13, 45, 12, TimeSpan.FromHours(5.5));
        var guid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var mixed = new AutoMixed
        {
            A = 0x12,
            B = 0x2345,
            C = 0x3456789A,
            D = 0x0102030405060708,
            E = 1234567890.123456789m,
            F = guid,
            G = offset
        };

        IFixture[] added =
        [
            new Fixture<AutoMixed>("AutoMixed", "auto-layout-release-scoped", mixed),
            new Fixture<AutoNested>("AutoNested", "auto-layout-release-scoped", new AutoNested
            {
                Prefix = 0x31,
                Inner = mixed,
                Tail = 0x1122334455667788
            }),
            new Fixture<AutoGeneric<byte>>("AutoGenericByte", "auto-layout-release-scoped", new AutoGeneric<byte>
            {
                Prefix = 0x41,
                Value = 0x52,
                Tail = 0x0102030405060708
            }),
            new Fixture<AutoGeneric<long>>("AutoGenericInt64", "auto-layout-release-scoped", new AutoGeneric<long>
            {
                Prefix = 0x42,
                Value = 0x1020304050607080,
                Tail = 0x1112131415161718
            }),
            new Fixture<AutoGeneric<Guid>>("AutoGenericGuid", "auto-layout-release-scoped", new AutoGeneric<Guid>
            {
                Prefix = 0x43,
                Value = guid,
                Tail = 0x2122232425262728
            }),
            new Fixture<AutoGeneric<DateTimeOffset>>("AutoGenericDateTimeOffset", "auto-layout-release-scoped", new AutoGeneric<DateTimeOffset>
            {
                Prefix = 0x44,
                Value = offset,
                Tail = 0x3132333435363738
            }),
            new Fixture<AutoPaddingHeavy>("AutoPaddingHeavy", "auto-layout-release-scoped", new AutoPaddingHeavy
            {
                Prefix = 0x51,
                Value = 0x4142434445464748,
                Suffix = 0x52
            }),
            new Fixture<DateTimeOffsetContainer>("DateTimeOffsetContainer", "auto-layout-release-scoped", new DateTimeOffsetContainer
            {
                Prefix = 0x61,
                Value = offset,
                Tail = 0x5152535455565758
            }, false, nameof(DateTimeOffsetContainer.Prefix), nameof(DateTimeOffsetContainer.Value), nameof(DateTimeOffsetContainer.Tail)),
            new Fixture<AutoDateTimeOffsetContainer>("AutoDateTimeOffsetContainer", "auto-layout-release-scoped", new AutoDateTimeOffsetContainer
            {
                Prefix = 0x62,
                Value = offset,
                Tail = 0x6162636465666768
            })
        ];

        foreach (var fixture in added)
        {
            fixtures.Add(fixture);
            byId.Add(fixture.Id, fixture);
        }
    }
}

[StructLayout(LayoutKind.Auto)]
internal struct AutoMixed
{
    public byte A;
    public short B;
    public int C;
    public long D;
    public decimal E;
    public Guid F;
    public DateTimeOffset G;
}

[StructLayout(LayoutKind.Auto)]
internal struct AutoNested
{
    public byte Prefix;
    public AutoMixed Inner;
    public long Tail;
}

[StructLayout(LayoutKind.Auto)]
internal struct AutoGeneric<T> where T : unmanaged
{
    public byte Prefix;
    public T Value;
    public long Tail;
}

[StructLayout(LayoutKind.Auto)]
internal struct AutoPaddingHeavy
{
    public byte Prefix;
    public long Value;
    public byte Suffix;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DateTimeOffsetContainer
{
    public byte Prefix;
    public DateTimeOffset Value;
    public long Tail;
}

[StructLayout(LayoutKind.Auto)]
internal struct AutoDateTimeOffsetContainer
{
    public byte Prefix;
    public DateTimeOffset Value;
    public long Tail;
}
