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
