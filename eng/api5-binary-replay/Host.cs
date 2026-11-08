using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using SharpLink.Abstractions;
using SharpLink.Client;

var path = Path.GetFullPath(args[0]);
var expectedIdentity = args[1];
var reject = bool.Parse(args[2]);
var expectedHostIdentity = args[3];
if (SharpLinkGeneratedManifestVersions.Api != 5 || SharpLinkGeneratedManifestVersions.Protocol != 2 ||
    SharpLinkGeneratedManifestVersions.AbiIdentity != expectedHostIdentity)
    throw new InvalidOperationException("Host must match the expected real API5/protocol2 identity.");
// Build before loading the plugin so catalog discovery cannot pre-register it.
var client = SharpClientBuilder.Create().DisableRequestTimeout().UseTcp("127.0.0.1", 1).Build();
try
{
    var context = new PluginContext();
    var assembly = context.LoadFromAssemblyPath(path);
    var locator = assembly.GetCustomAttributesData().Single(a =>
        a.AttributeType.FullName == typeof(SharpLinkGeneratedAssemblyManifestAttribute).FullName);
    if (locator.ConstructorArguments.Count != 5 ||
        (int)locator.ConstructorArguments[1].Value! != 5 ||
        (int)locator.ConstructorArguments[2].Value! != 2 ||
        (string)locator.ConstructorArguments[4].Value! != expectedIdentity)
        throw new InvalidOperationException("Unexpected generated plugin ABI identity.");
    var result = client.RegisterAssembly(assembly);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        Host = SharpLinkGeneratedManifestVersions.AbiIdentity,
        Plugin = expectedIdentity,
        result.Succeeded,
        ErrorCode = result.Error?.Code.ToString(),
        ErrorMessage = result.Error?.Message,
        Assembly = assembly.FullName
    }));
    if (reject)
    {
        if (result.Succeeded || result.Error?.Code != SharpLinkAssemblyRegistrationErrorCode.IncompatibleManifest ||
            !result.Error.Message.Contains(expectedIdentity, StringComparison.Ordinal) ||
            !result.Error.Message.Contains(SharpLinkGeneratedManifestVersions.AbiIdentity, StringComparison.Ordinal))
            throw new InvalidOperationException("Old API5 must be rejected specifically for its exact ABI identity.");
    }
    else
    {
        if (!result.Succeeded)
            throw new InvalidOperationException($"Same-generation registration failed: {result.Error}");
        // Confirm this is a real generator-produced contract+stub, not an empty
        // locator-only fixture or a hand-written identity string.
        var stubs = assembly.GetTypes().Where(t => typeof(IRpcStub).IsAssignableFrom(t) && !t.IsAbstract).ToArray();
        if (stubs.Length != 1)
            throw new InvalidOperationException($"Expected one generated stub, found {stubs.Length}.");
        if (!((await client.UnregisterAssemblyAsync(assembly, TimeSpan.FromSeconds(2))).ReferencesReleased))
            throw new InvalidOperationException("Positive-control registration did not release.");
    }
    context.Unload();
}
finally
{
    await client.StopAsync();
}

sealed class PluginContext() : AssemblyLoadContext("real-api5-fixture", isCollectible: true)
{
    protected override Assembly? Load(AssemblyName name)
        => name.Name?.StartsWith("SharpLink.", StringComparison.Ordinal) == true
            ? Default.LoadFromAssemblyName(name)
            : null;
}
