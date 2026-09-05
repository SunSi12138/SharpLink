using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public void ExternalVersionTolerantSharpPackMetadataShouldFailClosed()
    {
        var diagnostic = GetUnsupportedSharpPackMetadataDiagnostic("""
using SharpPack;

namespace Vendor;

[SharpPackable(GenerateType.VersionTolerant)]
public sealed class ExternalRequest
{
    [SharpPackOrder(0)]
    public int Id { get; set; }
}
""");

        Ensure(diagnostic.GetMessage().Contains("GenerateType", StringComparison.Ordinal),
            "non-default GenerateType must be identified by SLSP0001");
    }

    [Test]
    public void ExternalExplicitLayoutSharpPackMetadataShouldFailClosed()
    {
        var diagnostic = GetUnsupportedSharpPackMetadataDiagnostic("""
using SharpPack;

namespace Vendor;

[SharpPackable(SerializeLayout.Explicit)]
public sealed class ExternalRequest
{
    [SharpPackOrder(0)]
    public int Id { get; set; }
}
""");

        Ensure(diagnostic.GetMessage().Contains("SerializeLayout", StringComparison.Ordinal),
            "explicit SharpPack layout must be identified by SLSP0001");
    }

    [Test]
    public void ExternalSharpPackConstructorMetadataShouldFailClosed()
    {
        var diagnostic = GetUnsupportedSharpPackMetadataDiagnostic("""
using SharpPack;

namespace Vendor;

[SharpPackable]
public sealed class ExternalRequest
{
    public int Id { get; }

    [SharpPackConstructor]
    public ExternalRequest(int id) => Id = id;
}
""");

        Ensure(diagnostic.GetMessage().Contains("SharpPackConstructor", StringComparison.Ordinal),
            "annotated constructor semantics must be identified by SLSP0001");
    }

    [Test]
    public void ExternalSharpPackCallbackMetadataShouldFailClosed()
    {
        var diagnostic = GetUnsupportedSharpPackMetadataDiagnostic("""
using SharpPack;

namespace Vendor;

[SharpPackable]
public sealed class ExternalRequest
{
    public int Id { get; set; }

    [SharpPackOnDeserialized]
    private void OnDeserialized() => Id++;
}
""");

        Ensure(diagnostic.GetMessage().Contains("SharpPackOnDeserialized", StringComparison.Ordinal),
            "serialization callback semantics must be identified by SLSP0001");
    }

    [Test]
    public void ExternalSuppressDefaultInitializationMetadataShouldFailClosed()
    {
        var diagnostic = GetUnsupportedSharpPackMetadataDiagnostic("""
using SharpPack;

namespace Vendor;

[SharpPackable]
public sealed class ExternalRequest
{
    [SuppressDefaultInitialization]
    public string? Name { get; set; }
}
""");

        Ensure(diagnostic.GetMessage().Contains("SuppressDefaultInitialization", StringComparison.Ordinal),
            "default-initialization controls must be identified by SLSP0001");
    }

    private static Diagnostic GetUnsupportedSharpPackMetadataDiagnostic(string vendorSource)
    {
        var vendor = CreateSharpPackVendorReference(vendorSource);
        var source = BuildSharpPackContractSource("""
    global::System.Threading.Tasks.Task<Vendor.ExternalRequest> EchoAsync(
        Vendor.ExternalRequest request,
        global::System.Threading.CancellationToken cancellationToken);
""");

        var result = RunSharpPackAndCompile(
            "SharpPackUnsupportedMetadata" + Guid.NewGuid().ToString("N"),
            source,
            [vendor]);
        var diagnostic = result.DriverDiagnostics.FirstOrDefault(static item => item.Id == "SLSP0001");
        Ensure(diagnostic is not null, "unsupported SharpPack-specific metadata produces SLSP0001");
        return diagnostic!;
    }
}
