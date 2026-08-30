using System;
using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;

[assembly: SharpLinkGeneratedAssemblyManifest(
    typeof(SharpLink.RollbackPlugin.RollbackManifest),
    SharpLinkGeneratedManifestVersions.Api,
    SharpLinkGeneratedManifestVersions.Protocol,
    "rollback-test",
    SharpLinkGeneratedManifestVersions.AbiIdentity)]

namespace SharpLink.RollbackPlugin;

public sealed class RollbackMarker;

public static class RollbackState
{
    public static int ScopeDisposeCount;
    public static TaskCompletionSource? ManifestConstructionStarted;
    public static TaskCompletionSource? ManifestConstructionRelease;
    public static SemaphoreSlim TestIsolation { get; } = new(1, 1);
}

public sealed class RollbackManifest : ISharpLinkGeneratedAssemblyManifest
{
    public RollbackManifest()
    {
        var identity = Environment.GetEnvironmentVariable("SHARPLINK_ROLLBACK_CODEC_IDENTITY") ??
            Environment.GetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA") ??
            "default";
        var codecHash = ComputeIdentityHash(identity);
        RpcAssemblyHash = new RpcHash128(0x726f6c6c6261636bUL, codecHash.Low);
        Codecs = string.Equals(
                Environment.GetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC"),
                "1",
                StringComparison.Ordinal)
            ? []
            : [new RollbackCodecFactory(codecHash)];

        var started = RollbackState.ManifestConstructionStarted;
        if (started is null)
            return;

        started.TrySetResult();
        RollbackState.ManifestConstructionRelease?.Task.GetAwaiter().GetResult();
    }

    public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
    public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
    public string GeneratorVersion => "rollback-test";
    public Assembly OwnerAssembly => typeof(RollbackManifest).Assembly;
    public RpcHash128 RpcAssemblyHash { get; }
    public string CompileTimeDescriptor => "rollback-test";
    public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
    public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
    public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; }
    public IReadOnlyList<IRpcGeneratedCodecFactory> ContractCodecs => [];
    public IReadOnlyList<string> Dependencies => [];

    private static RpcHash128 ComputeIdentityHash(string value)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var low = offset;
        foreach (var character in value)
        {
            low ^= character;
            low *= prime;
        }
        return new RpcHash128(0x726f6c6c6261636bUL, low == 0 ? 1UL : low);
    }
}

internal sealed class RollbackCodecFactory(RpcHash128 codecHash) : IRpcGeneratedCodecFactory
{
    public Type TargetType => typeof(string);
    public RpcHash128 CodecHash { get; } = codecHash;
    public string AdapterId => "rollback-adapter/v1";
    public IRpcCodecAdapter Adapter { get; } = new RollbackAdapter();

    public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
        => (adapterScope ?? throw new ArgumentNullException(nameof(adapterScope))).CreateCodec<string>();

    public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<string>;
}

internal sealed class RollbackAdapter : IRpcCodecAdapter
{
    public string AdapterId => "rollback-adapter/v1";
    public IRpcCodecAdapterScope CreateScope() => new RollbackScope();
}

internal sealed class RollbackScope : IRpcCodecAdapterScope
{
    public IRpcCodec<T> CreateCodec<T>() => (IRpcCodec<T>)(object)new RollbackStringCodec();

    public void Dispose()
    {
        Interlocked.Increment(ref RollbackState.ScopeDisposeCount);
        throw new InvalidOperationException("rollback Adapter scope cleanup failed");
    }
}

internal sealed class RollbackStringCodec : IRpcCodec<string>
{
    public void Serialize(in string value, IBufferWriter<byte> buffer) { }
    public string Deserialize(in ReadOnlySequence<byte> buffer) => string.Empty;
}
