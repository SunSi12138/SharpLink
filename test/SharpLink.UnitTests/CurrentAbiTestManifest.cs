using System.Collections.Generic;
using System.Reflection;
using SharpLink.Abstractions;

[assembly: SharpLinkGeneratedAssemblyManifest(
    typeof(SharpLink.UnitTests.CurrentAbiTestManifest),
    SharpLinkGeneratedManifestVersions.Api,
    SharpLinkGeneratedManifestVersions.Protocol,
    "test",
    SharpLinkGeneratedManifestVersions.AbiIdentity)]

namespace SharpLink.UnitTests;

/// <summary>
/// Supplies the current generated-ABI locator for unit-test manifests whose owner is this assembly.
/// Individual tests can still use dynamically emitted owner assemblies to exercise missing or stale locators.
/// </summary>
public sealed class CurrentAbiTestManifest : ISharpLinkGeneratedAssemblyManifest
{
    public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
    public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
    public string GeneratorVersion => "test";
    public Assembly OwnerAssembly => typeof(CurrentAbiTestManifest).Assembly;
    public string CompileTimeDescriptor => "unit-tests-current-abi";
    public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
    public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
    public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
    public IReadOnlyList<string> Dependencies => [];
}
