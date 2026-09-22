using System.Collections.Generic;
using System.Threading.Tasks;
using SharpLink.Sdk;

namespace SharpLink.StaticCodecCoreEvidence;

[RpcContract]
public interface IStaticCodecCoreEvidenceRpc : IService
{
    [Idempotent]
    [NonCancellable]
    ValueTask<int> TinyAsync(int value);

    [NonCancellable]
    ValueTask<Core16> Echo16Async(Core16 value);

    [NonCancellable]
    ValueTask<Core64> Echo64Async(Core64 value);

    [NonCancellable]
    ValueTask<Core256> Echo256Async(Core256 value);

    [NonCancellable]
    ValueTask<CoreText> EchoTextAsync(CoreText value);

    [NonCancellable]
    ValueTask<CoreCollection> EchoCollectionAsync(CoreCollection value);

    [NonCancellable]
    ValueTask<CoreSnapshot> EchoSnapshotAsync(CoreSnapshot value);

    [NonCancellable]
    ValueTask<CoreMixed> EchoMixedAsync(CoreMixed value);

    [Oneway]
    [NonCancellable]
    ValueTask Publish64Async(Core64 value);

    [NonCancellable]
    ValueTask<Core64> Upload64Async(IAsyncEnumerable<Core64> values);

    [NonCancellable]
    IAsyncEnumerable<Core64> Download64Async(int count);

    [NonCancellable]
    IAsyncEnumerable<Core64> Duplex64Async(IAsyncEnumerable<Core64> values);
}

[RpcSerializable]
public sealed class Core16
{
    public int A { get; init; }
    public int B { get; init; }
    public int C { get; init; }
    public int D { get; init; }
}

[RpcSerializable]
public sealed class Core64
{
    [RpcRequired]
    public Core16 A { get; init; } = new();

    [RpcRequired]
    public Core16 B { get; init; } = new();

    [RpcRequired]
    public Core16 C { get; init; } = new();

    [RpcRequired]
    public Core16 D { get; init; } = new();
}

[RpcSerializable]
public sealed class Core256
{
    [RpcRequired]
    public Core64 A { get; init; } = new();

    [RpcRequired]
    public Core64 B { get; init; } = new();

    [RpcRequired]
    public Core64 C { get; init; } = new();

    [RpcRequired]
    public Core64 D { get; init; } = new();
}

[RpcSerializable]
public sealed class CoreText
{
    public int Id { get; init; }

    [RpcRequired]
    public string Text { get; init; } = string.Empty;
}

[RpcSerializable]
public sealed class CoreCollection
{
    public int Id { get; init; }

    [RpcRequired]
    public List<int> Values { get; init; } = [];
}

[RpcSerializable]
public sealed class CoreSnapshot
{
    public int Id { get; init; }

    [RpcRequired]
    public string Name { get; init; } = string.Empty;

    [RpcRequired]
    public Core64 Nested { get; init; } = new();
}

[RpcSerializable]
public sealed class CoreMixed
{
    [RpcRequired]
    public Core64 Concrete { get; init; } = new();

    [RpcRequired]
    public byte[] Fallback { get; init; } = [];
}


[RpcService]
public sealed class StaticCodecCoreEvidenceService : IStaticCodecCoreEvidenceRpc
{
    public ValueTask<int> TinyAsync(int value)
        => ValueTask.FromResult(value + 1);

    public ValueTask<Core16> Echo16Async(Core16 value)
        => ValueTask.FromResult(value);

    public ValueTask<Core64> Echo64Async(Core64 value)
        => ValueTask.FromResult(value);

    public ValueTask<Core256> Echo256Async(Core256 value)
        => ValueTask.FromResult(value);

    public ValueTask<CoreText> EchoTextAsync(CoreText value)
        => ValueTask.FromResult(value);

    public ValueTask<CoreCollection> EchoCollectionAsync(CoreCollection value)
        => ValueTask.FromResult(value);

    public ValueTask<CoreSnapshot> EchoSnapshotAsync(CoreSnapshot value)
        => ValueTask.FromResult(value);

    public ValueTask<CoreMixed> EchoMixedAsync(CoreMixed value)
        => ValueTask.FromResult(value);

    public ValueTask Publish64Async(Core64 value)
        => ValueTask.CompletedTask;

    public async ValueTask<Core64> Upload64Async(IAsyncEnumerable<Core64> values)
    {
        Core64? last = null;
        await foreach (var value in values.ConfigureAwait(false))
            last = value;
        return last ?? EvidencePayloads.Get64(0);
    }

    public async IAsyncEnumerable<Core64> Download64Async(int count)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        for (var index = 0; index < count; index++)
            yield return EvidencePayloads.Get64(index);
    }

    public async IAsyncEnumerable<Core64> Duplex64Async(IAsyncEnumerable<Core64> values)
    {
        await foreach (var value in values.ConfigureAwait(false))
            yield return value;
    }
}

internal static class EvidencePayloads
{
    private static readonly Core16[] Core16Values = CreateCore16Values();
    private static readonly Core64[] Core64Values = CreateCore64Values();
    private static readonly Core256[] Core256Values = CreateCore256Values();
    private static readonly CoreText[] TextValues = CreateTextValues();
    private static readonly CoreCollection[] CollectionValues = CreateCollectionValues();
    private static readonly CoreSnapshot[] SnapshotValues = CreateSnapshotValues();
    private static readonly CoreMixed[] MixedValues = CreateMixedValues();

    internal static Core16 Get16(int index) => Core16Values[index & 255];
    internal static Core64 Get64(int index) => Core64Values[index & 255];
    internal static Core256 Get256(int index) => Core256Values[index & 63];
    internal static CoreText GetText(int index) => TextValues[index & 255];
    internal static CoreCollection GetCollection(int index) => CollectionValues[index & 255];
    internal static CoreSnapshot GetSnapshot(int index) => SnapshotValues[index & 255];
    internal static CoreMixed GetMixed(int index) => MixedValues[index & 255];

    private static Core16[] CreateCore16Values()
    {
        var values = new Core16[256];
        for (var i = 0; i < values.Length; i++)
            values[i] = new Core16 { A = i, B = i + 1, C = i + 2, D = i + 3 };
        return values;
    }

    private static Core64[] CreateCore64Values()
    {
        var values = new Core64[256];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = new Core64
            {
                A = Get16(i),
                B = Get16(i + 1),
                C = Get16(i + 2),
                D = Get16(i + 3)
            };
        }
        return values;
    }

    private static Core256[] CreateCore256Values()
    {
        var values = new Core256[64];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = new Core256
            {
                A = Get64(i),
                B = Get64(i + 1),
                C = Get64(i + 2),
                D = Get64(i + 3)
            };
        }
        return values;
    }

    private static CoreText[] CreateTextValues()
    {
        var values = new CoreText[256];
        for (var i = 0; i < values.Length; i++)
            values[i] = new CoreText { Id = i, Text = $"static-core-{i:D3}-payload" };
        return values;
    }

    private static CoreCollection[] CreateCollectionValues()
    {
        var values = new CoreCollection[256];
        for (var i = 0; i < values.Length; i++)
            values[i] = new CoreCollection { Id = i, Values = [i, i + 1, i + 2, i + 3, i + 4, i + 5, i + 6, i + 7] };
        return values;
    }

    private static CoreSnapshot[] CreateSnapshotValues()
    {
        var values = new CoreSnapshot[256];
        for (var i = 0; i < values.Length; i++)
            values[i] = new CoreSnapshot { Id = i, Name = $"snapshot-{i:D3}", Nested = Get64(i) };
        return values;
    }

    private static CoreMixed[] CreateMixedValues()
    {
        var values = new CoreMixed[256];
        for (var i = 0; i < values.Length; i++)
            values[i] = new CoreMixed { Concrete = Get64(i), Fallback = [(byte)i, (byte)(i + 1), (byte)(i + 2), (byte)(i + 3)] };
        return values;
    }
}
