using SharpLink.Abstractions;
using System.Reflection;

internal static class ArmAdapter
{
#if BASELINE && CANDIDATE
#error An arm cannot select both BASELINE and CANDIDATE.
#elif BASELINE
    public const string Name = "baseline-3d76a3a8";
    public const string Revision = "3d76a3a8b55a37e24c62a073fe588cff3d3867be";
#elif CANDIDATE
    public const string Name = "candidate-b3cf84da";
    public const string Revision = "b3cf84da2853b8dc8d107a03755d3e415988485d";
#else
#error Select Arm=BASELINE or Arm=CANDIDATE.
#endif
    public const int GeneratedApi = 5;
    public const string GeneratorVersion = "3.0.0";
    public const string Abi = "sharplink-3.0-api5-owner-bound-unsafe-blit-v1";

    public static object VerifyManifest(Assembly module)
    {
        var attribute = module.GetCustomAttribute<SharpLinkGeneratedAssemblyManifestAttribute>()
            ?? throw new InvalidOperationException("Missing independently generated module manifest.");
        var manifest = (ISharpLinkGeneratedAssemblyManifest)Activator.CreateInstance(attribute.ManifestType)!;
        if (attribute.ApiVersion != GeneratedApi || manifest.ApiVersion != GeneratedApi)
            throw new InvalidOperationException($"Wrong generated API for {Name}: {attribute.ApiVersion}/{manifest.ApiVersion}.");
        if (attribute.ProtocolVersion != 2 || manifest.ProtocolVersion != 2 ||
            attribute.GeneratorVersion != GeneratorVersion || manifest.GeneratorVersion != GeneratorVersion ||
            attribute.AbiIdentity != Abi)
            throw new InvalidOperationException($"Wrong independent generated protocol/generator/ABI for {Name}.");
        if (Environment.GetEnvironmentVariable("SHARPLINK_BENCHMARK_SHA") != Revision)
            throw new InvalidOperationException("Missing or incorrect pinned arm source revision.");
        return new { arm = Name, sourceRevision = Revision, api = attribute.ApiVersion,
            protocol = attribute.ProtocolVersion, abi = attribute.AbiIdentity,
            generator = attribute.GeneratorVersion, rpcAssemblyHash = manifest.RpcAssemblyHash.ToString() };
    }

    public static void VerifyPath(bool dynamic, string channel, string outer, HashSet<string> wrappers)
    {
        // Both API5 arms use these owners. Each independently generated manifest
        // must pass above, and every positive check applies symmetrically.
        var expectedChannel = dynamic ? "SharpLink.Client.SharpLinkModuleRpcChannel" : "SharpLink.Client.SharpLinkClient";
        if (channel != expectedChannel)
            throw new InvalidOperationException($"{Name}: expected channel {expectedChannel}, got {channel}.");
        if (!dynamic && !outer.Contains("+LogicalInvocationAsyncEnumerable`", StringComparison.Ordinal))
            throw new InvalidOperationException($"{Name}: static outer logical owner missing: {outer}.");
        if (dynamic != outer.Contains("InvokeServerStreamingDeferred", StringComparison.Ordinal))
            throw new InvalidOperationException($"{Name}: wrong outer stream {outer}.");
        foreach (var required in new[] { "+LogicalInvocationAsyncEnumerable`", "+InterceptedAsyncEnumerable`", "SharpLink.Runtime.PooledAsyncStreamDispatcher`" })
            if (!wrappers.Any(name => name.Contains(required, StringComparison.Ordinal)))
                throw new InvalidOperationException($"{Name}: positive wrapper proof missing {required}.");
        if (wrappers.Any(name => name.Contains("CompletionOwned", StringComparison.Ordinal) || name.Contains("+TelemetryAsync", StringComparison.Ordinal)))
            throw new InvalidOperationException($"{Name}: unexpected optimized/telemetry wrapper.");
    }
}
