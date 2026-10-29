using SharpLink.Abstractions;
using System.Reflection;

internal static class ArmAdapter
{
    // Identity is embedded from the same required build properties inspected by the
    // metadata-only preflight. No source revision is independently hard-coded here.
    private static readonly IReadOnlyDictionary<string, string?> Identity = typeof(ArmAdapter).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(attribute => attribute.Key, attribute => attribute.Value);
    public static readonly string Revision = RequiredIdentity("ExpectedProductCommit");
    public static readonly string Name = RequiredIdentity("Arm") + "-" + Revision[..8];

    private static string RequiredIdentity(string key) =>
        Identity.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value)
            ? value : throw new InvalidOperationException("Missing embedded fixture identity: " + key);

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
