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
    public string CompileTimeDescriptor => "rollback-test";
    public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
    public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
    public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; } =
        string.Equals(Environment.GetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC"), "1", StringComparison.Ordinal)
            ? []
            : [new RollbackCodecFactory(Environment.GetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA") ?? "default")];
    public IReadOnlyList<string> Dependencies => [];
}

internal sealed class RollbackCodecFactory(string schemaId) : IRpcGeneratedCodecFactory
{
    public Type TargetType => typeof(string);
    public string SchemaId { get; } = schemaId;
    public string WireFormatId => "rollback-wire/v1";
    public string AdapterId => "rollback-adapter/v1";
    public IRpcCodecAdapter Adapter { get; } = new RollbackAdapter();

    public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
        => (adapterScope ?? throw new ArgumentNullException(nameof(adapterScope))).CreateCodec<string>();

    public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<string>;
}

internal sealed class RollbackAdapter : IRpcCodecAdapter
{
    public string AdapterId => "rollback-adapter/v1";
    public string WireFormatId => "rollback-wire/v1";
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
