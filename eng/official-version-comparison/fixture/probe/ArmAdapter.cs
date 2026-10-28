using SharpLink.Abstractions;
using System.Reflection;

internal static class ArmAdapter
{
#if OFFICIAL && DEV
#error An arm cannot select both OFFICIAL and DEV.
#elif OFFICIAL
    public const string Name = "official-2.0.3";
    public const string Revision = "4f9295ffb37ce68985051c11bfd9afc0e23166f3";
    public const int GeneratedApi = 4;
    public const string GeneratorVersion = "2.0.3";
    public const string Abi = "sharplink-2.0-api4-rpcchannel-codec-provider-v1";
#elif DEV
    public const string Name = "dev-57f06f1";
    public const string Revision = "57f06f172d29fa5cfd4ea8e9b86a23f122df24ab";
    public const int GeneratedApi = 5;
    public const string GeneratorVersion = "3.0.0";
    public const string Abi = "sharplink-3.0-api5-owner-bound-unsafe-blit-v1";
#else
#error Select Arm=OFFICIAL or Arm=DEV.
#endif

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
        // Both pinned versions use these owners. API 5 is not silently weakened to API 4:
        // its independent generated manifest must pass above, and every positive check applies.
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
