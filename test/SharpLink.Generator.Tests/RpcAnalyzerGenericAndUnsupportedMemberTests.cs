using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{

    [Test]
    public Task GenericMethodInIServiceShouldReportSharplink005Once()
    {
        var source = BuildSource("""
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<T> Echo<T>(T value);
}
""");
        source = source.Replace("public interface IHelloService : SharpLink.Sdk.IService", "[SharpLink.Sdk.RpcContract]\npublic interface IHelloService : SharpLink.Sdk.IService");

        var diagnostics = RunGenerator(source);
        var hits = diagnostics.Where(d => d.Id == "SHARPLINK005").ToArray();
        Ensure(hits.Length == 1, $"Expected exactly one SHARPLINK005, but got {hits.Length}.");
        return Task.CompletedTask;
    }

    [Test]
    public Task AbstractAndOpenGenericRpcServicesShouldReportSharplink018()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IAbstractContract : SharpLink.Sdk.IService
{
    ValueTask<int> Get(int value);
}

[SharpLink.Sdk.RpcContract]
public interface IGenericContract : SharpLink.Sdk.IService
{
    ValueTask<int> Get(int value);
}

[SharpLink.Sdk.RpcService]
public abstract class AbstractService : IAbstractContract
{
    public abstract ValueTask<int> Get(int value);
}

[SharpLink.Sdk.RpcService]
public sealed class GenericService<T> : IGenericContract
{
    public ValueTask<int> Get(int value) => new(value);
}
""");

        EnsureRuleCount(source, "SHARPLINK018", 2);
        return Task.CompletedTask;
    }

    [Test]
    public Task AbstractNonMethodContractMembersShouldReportSharplink054()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IMemberContract : SharpLink.Sdk.IService
{
    int Version { get; }
    string this[int index] { get; }
    event Action Changed;
}
""");

        EnsureRuleCount(source, "SHARPLINK054", 3);
        return Task.CompletedTask;
    }

    [Test]
    public Task RefLikeDtoShouldBeRejectedWithoutEmittingBrokenContractArtifacts()
    {
        var source = AddAssemblyAttribute(BuildSource("""
[SharpLink.Sdk.RpcCodecAdapter(typeof(RefPayloadAdapter))]
[SharpLink.Sdk.RpcSerializable]
public ref struct RefPayload
{
    public int Value;
}

[SharpLink.Sdk.RpcContract]
public interface IRefPayloadContract : SharpLink.Sdk.IService
{
    ValueTask<int> Send(RefPayload payload, CancellationToken cancellationToken);
}

public sealed class RefPayloadAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "ref.adapter/v1";
    public string WireFormatId => "ref-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(RefPayloadAdapter), \"ref.adapter/v1\", \"ref-wire/v1\")]");

        EnsureRuleCount(source, "SHARPLINK009", 1);
        Ensure(!string.Join("\n", RunGeneratorAndGetSources(source)).Contains(
                "IRefPayloadContract",
                StringComparison.Ordinal),
            "a ref-like payload must suppress contract artifacts that cannot use it as a generic argument");
        return Task.CompletedTask;
    }

    [Test]
    public Task StaticAbstractOperatorsShouldRejectRpcContractGeneration()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IOperatorContract : SharpLink.Sdk.IService
{
    static abstract IOperatorContract operator +(IOperatorContract left, IOperatorContract right);
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}
""");

        EnsureRuleCount(source, "SHARPLINK054", 1);
        Ensure(!string.Join("\n", RunGeneratorAndGetSources(source)).Contains(
                "IOperatorContract",
                StringComparison.Ordinal),
            "a contract with an unimplementable static abstract operator must not emit a Proxy");
        return Task.CompletedTask;
    }

    [Test]
    public Task PointerPayloadDiagnosticsMustSuppressBrokenContractArtifacts()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public unsafe interface IPointerPayloadContract : SharpLink.Sdk.IService
{
    ValueTask<int> SendPointer(int* value, CancellationToken cancellationToken);
    ValueTask<int> SendFunction(delegate*<int, int> callback, CancellationToken cancellationToken);
}
""");

        EnsureRuleCount(source, "SHARPLINK009", 2);
        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(!generated.Contains("IPointerPayloadContract", StringComparison.Ordinal),
            "pointer payloads must suppress all contract artifacts that cannot represent them");
        return Task.CompletedTask;
    }

    [Test]
    public Task StaticAbstractRpcMethodsShouldReportSharplink053()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IStaticContract : SharpLink.Sdk.IService
{
    static abstract ValueTask<int> Invoke(int value, CancellationToken cancellationToken);
}
""");

        EnsureRuleCount(source, "SHARPLINK053", 1);
        return Task.CompletedTask;
    }

    [Test]
    public Task DefaultInterfaceMembersShouldNotBeRejectedAsRpcRoutes()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IDefaultMemberContract : SharpLink.Sdk.IService
{
    int Version => 1;
    event Action Changed { add { } remove { } }
    ValueTask<int> Invoke(CancellationToken cancellationToken);
}
""");

        EnsureDoesNotHaveRule(source, "SHARPLINK054");
        return Task.CompletedTask;
    }

    [Test]
    public Task NonPublicDefaultInterfaceHelpersShouldNotBecomeRpcRoutes()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IHelperContract : SharpLink.Sdk.IService
{
    ValueTask<int> Invoke(int value, CancellationToken cancellationToken);

    private ValueTask<int> Normalize(int value) => new(value);
}
""");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(!generated.Contains(" Normalize(", StringComparison.Ordinal) &&
               !generated.Contains(".Normalize(", StringComparison.Ordinal) &&
               !generated.Contains("\"Normalize\"", StringComparison.Ordinal),
            "non-public default interface helpers must not become generated routes");

        var nonPublicAbstract = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface INonPublicAbstractContract : SharpLink.Sdk.IService
{
    protected abstract ValueTask<int> Hidden(int value, CancellationToken cancellationToken);
}
""");
        EnsureRuleCount(nonPublicAbstract, "SHARPLINK054", 1);
        return Task.CompletedTask;
    }

    [Test]
    public Task OpenGenericAdapterTargetShouldReportSharplink047()
    {
        var source = AddAssemblyAttribute(BuildSource("public sealed class FakeAdapter { }"),
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(List<>), typeof(FakeAdapter))]");

        EnsureHasRule(source, "SHARPLINK047");
        return Task.CompletedTask;
    }

    [Test]
    public Task InstalledUnselectedAdapterShouldNotFallbackForUnsupportedDto()
    {
        var source = AddAssemblyAttribute(BuildSource("""
public sealed class Graph
{
    public Graph? Parent { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IGraphService : SharpLink.Sdk.IService
{
    ValueTask<Graph> Echo(Graph value);
}

public sealed class InstalledAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "installed/v1";
    public string WireFormatId => "installed-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(InstalledAdapter), \"installed/v1\", \"installed-wire/v1\")]");

        EnsureHasRule(source, "SHARPLINK010");
        return Task.CompletedTask;
    }

    [Test]
    public Task ByRefRpcSignaturesShouldReportSharplink052()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IByRefContract : SharpLink.Sdk.IService
{
    ValueTask<int> Ref(ref int value, CancellationToken cancellationToken);
    ValueTask<int> Out(out int value, CancellationToken cancellationToken);
    ValueTask<int> In(in int value, CancellationToken cancellationToken);
    ref ValueTask<int> RefReturn(CancellationToken cancellationToken);
}
""");

        EnsureRuleCount(source, "SHARPLINK052", 4);
        return Task.CompletedTask;
    }
}
