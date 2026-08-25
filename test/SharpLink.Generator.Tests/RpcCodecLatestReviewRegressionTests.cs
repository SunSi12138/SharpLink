using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task ExplicitBuiltinShouldOverrideMatchingContractRouteWithoutStandaloneDiagnostic()
    {
        var source = AddAssemblyAttributes(BuildRouteSource("""
[SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Native, typeof(BroadAdapter))]
[SharpLink.Sdk.RpcContract]
public interface IBuiltinContract : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}

public sealed class BroadAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "builtin.broad/v1";
    public override string WireFormatId => "builtin-broad/v1";
}

public sealed class ExplicitAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "builtin.explicit/v1";
    public override string WireFormatId => "builtin-explicit/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(BroadAdapter), \"builtin.broad/v1\", \"builtin-broad/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(ExplicitAdapter), \"builtin.explicit/v1\", \"builtin-explicit/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(int), typeof(ExplicitAdapter))]");

        var result = RunContractGenerator(source);
        Ensure(!result.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK044"),
            $"a builtin explicit binding that overrides a matching Contract route must not be rejected by the standalone pass. Diagnostics: {FormatDiagnostics(result.Diagnostics)}");
        Ensure(result.Json.Contains("\"wireFormatId\": \"builtin-explicit/v1\"", StringComparison.Ordinal),
            "the explicit builtin binding must win over the broad Contract route");
        return Task.CompletedTask;
    }

    [Test]
    public Task ContractLocalBuiltinEscapeHatchShouldNotAffectNoRouteSibling()
    {
        var source = AddAssemblyAttributes(BuildRouteSource("""
[SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Native, typeof(BroadAdapter))]
[SharpLink.Sdk.RpcContract]
public interface IRoutedContract : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcContract]
public interface IDefaultContract : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}

public sealed class BroadAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "sibling.broad/v1";
    public override string WireFormatId => "sibling-broad/v1";
}

public sealed class ExplicitAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "sibling.explicit/v1";
    public override string WireFormatId => "sibling-explicit/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(BroadAdapter), \"sibling.broad/v1\", \"sibling-broad/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(ExplicitAdapter), \"sibling.explicit/v1\", \"sibling-explicit/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(int), typeof(ExplicitAdapter))]");

        var result = RunContractGenerator(source);
        Ensure(!result.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK044"),
            $"a route-scoped builtin escape hatch must not be diagnosed from the standalone or unrelated sibling pass. Diagnostics: {FormatDiagnostics(result.Diagnostics)}");
        Ensure(result.Json.Contains("\"contract\": \"IRoutedContract\"", StringComparison.Ordinal) &&
               result.Json.Contains("\"contract\": \"IDefaultContract\"", StringComparison.Ordinal),
            "compatibility Codec identities must be emitted separately for both sibling Contracts");
        Ensure(result.Json.Contains("\"wireFormatId\": \"sibling-explicit/v1\"", StringComparison.Ordinal) &&
               result.Json.Contains("\"wireFormatId\": \"sharplink-native/v1\"", StringComparison.Ordinal),
            "the routed sibling must use the explicit escape hatch while the no-route sibling keeps the default builtin");
        return Task.CompletedTask;
    }

    [Test]
    public Task SameAssemblyContractCodecIdentityChangeShouldBeComparedPerContract()
    {
        const string shared = """
public sealed class AdapterA : TestRouteAdapterBase
{
    public override string AdapterId => "contract-a/v1";
    public override string WireFormatId => "same-wire/v1";
}
public sealed class AdapterB : TestRouteAdapterBase
{
    public override string AdapterId => "contract-b/v1";
    public override string WireFormatId => "same-wire/v1";
}
public sealed class AdapterC : TestRouteAdapterBase
{
    public override string AdapterId => "contract-c/v1";
    public override string WireFormatId => "same-wire/v1";
}
""";
        var baselineSource = AddAssemblyAttributes(BuildRouteSource("""
[SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Native, typeof(AdapterA))]
[SharpLink.Sdk.RpcContract]
public interface IContractA : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Native, typeof(AdapterB))]
[SharpLink.Sdk.RpcContract]
public interface IContractB : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}
""" + shared),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(AdapterA), \"contract-a/v1\", \"same-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(AdapterB), \"contract-b/v1\", \"same-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(AdapterC), \"contract-c/v1\", \"same-wire/v1\")]");
        var currentSource = AddAssemblyAttributes(BuildRouteSource("""
[SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Native, typeof(AdapterA))]
[SharpLink.Sdk.RpcContract]
public interface IContractA : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.Native, typeof(AdapterC))]
[SharpLink.Sdk.RpcContract]
public interface IContractB : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}
""" + shared),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(AdapterA), \"contract-a/v1\", \"same-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(AdapterB), \"contract-b/v1\", \"same-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(AdapterC), \"contract-c/v1\", \"same-wire/v1\")]");

        var baseline = RunContractGenerator(baselineSource);
        Ensure(baseline.Json.Contains("\"contract\": \"IContractA\"", StringComparison.Ordinal) &&
               baseline.Json.Contains("\"contract\": \"IContractB\"", StringComparison.Ordinal),
            "the baseline must retain separate Codec identities for same-assembly Contracts");
        var compared = RunContractGenerator(currentSource, baseline.Json);
        Ensure(compared.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            $"changing only Contract B's Adapter identity under a stable wire ID must be detected independently of Contract A. Diagnostics: {FormatDiagnostics(compared.Diagnostics)}");
        return Task.CompletedTask;
    }
}
