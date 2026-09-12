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
    public Task GeneratedManifestShouldExposeAnAssemblyOwnedBootstrapForInternalServices()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IInternalService : SharpLink.Sdk.IService
{
    ValueTask<string> Identify();
}

[SharpLink.Sdk.RpcService]
internal sealed class InternalService : IInternalService
{
    public InternalService() { }
    public ValueTask<string> Identify() => new("internal");
}
""");

        var manifest = GetGeneratedManifest(source);
        Ensure(manifest.Contains("typeof(global::InternalService)", StringComparison.Ordinal),
            "the assembly-owned manifest must retain its internal service implementation");
        Ensure(manifest.Contains("public static void Register()", StringComparison.Ordinal),
            "the generated manifest must expose a public static bootstrap entry point");
        Ensure(manifest.Contains(
                "=> SharpLinkGeneratedAssemblyCatalog.Register(Instance);",
                StringComparison.Ordinal),
            "the public bootstrap must register the assembly-owned manifest instance");
        Ensure(manifest.Contains("=> __SharpLinkGeneratedAssemblyManifest_", StringComparison.Ordinal) &&
               manifest.Contains(".Register();", StringComparison.Ordinal),
            "the producer module initializer must delegate to the public bootstrap");
        Ensure(CountOccurrences(manifest, "SharpLinkGeneratedAssemblyCatalog.Register") == 1,
            "registration logic must have one assembly-owned implementation");
        Ensure(!manifest.Contains("Register(global::InternalService", StringComparison.Ordinal),
            "the public bootstrap must not expose the internal implementation type");
        return Task.CompletedTask;
    }

    [Test]
    public Task RpcServiceWithoutExplicitLifetimeShouldGenerateSingletonManifestEntry()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value);
}

[SharpLink.Sdk.RpcService]
public sealed class HelloService : IHelloService
{
    public ValueTask<int> Echo(int value) => new(value);
}
""");

        var manifest = GetGeneratedManifest(source);
        Ensure(manifest.Contains("public const string CompileTimeDescriptor", StringComparison.Ordinal),
            "Manifest must expose its compile-time descriptor.");
        Ensure(manifest.Contains("global::HelloService", StringComparison.Ordinal),
            "Manifest must identify the service implementation.");
        Ensure(manifest.Contains("SharpLinkServiceLifetime.Singleton", StringComparison.Ordinal),
            "RpcService without an explicit lifetime must be generated as Singleton.");
        return Task.CompletedTask;
    }

    [Test]
    public Task RpcServiceExplicitLifetimesShouldBePreservedInManifest()
    {
        foreach (var lifetime in new[] { "Singleton", "Connection", "Call" })
        {
            var source = BuildSource($$"""
[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<int> Get(int value);
}

[SharpLink.Sdk.RpcService(Lifetime = SharpLink.Sdk.SharpLinkServiceLifetime.{{lifetime}})]
public sealed class HelloService : IHelloService
{
    public ValueTask<int> Get(int value) => new(value);
}
""");

            var manifest = GetGeneratedManifest(source);
            Ensure(manifest.Contains("global::HelloService", StringComparison.Ordinal),
                "Manifest must identify the service implementation.");
            Ensure(manifest.Contains($"SharpLinkServiceLifetime.{lifetime}", StringComparison.Ordinal),
                $"Manifest must preserve explicit {lifetime} lifetime.");
        }

        return Task.CompletedTask;
    }

    [Test]
    public Task InvalidRpcServiceLifetimeShouldReportSharplink020()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value);
}

[SharpLink.Sdk.RpcService(Lifetime = (SharpLink.Sdk.SharpLinkServiceLifetime)99)]
public sealed class HelloService : IHelloService
{
    public ValueTask<int> Echo(int value) => new(value);
}
""");

        EnsureHasRuleContaining(source, "SHARPLINK020", "99");
        return Task.CompletedTask;
    }

    [Test]
    public Task RpcServiceWithoutRpcContractShouldReportSharplink016()
    {
        var source = BuildSource("""
public interface IOrdinaryService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value);
}

[SharpLink.Sdk.RpcService]
public sealed class OrdinaryService : IOrdinaryService
{
    public ValueTask<int> Echo(int value) => new(value);
}
""");

        EnsureHasRuleContaining(source, "SHARPLINK016", "OrdinaryService");
        return Task.CompletedTask;
    }

    [Test]
    public Task RpcServiceImplementingMultipleContractsShouldReportSharplink017()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IFirstService : SharpLink.Sdk.IService
{
    ValueTask<int> First(int value);
}

[SharpLink.Sdk.RpcContract]
public interface ISecondService : SharpLink.Sdk.IService
{
    ValueTask<int> Second(int value);
}

[SharpLink.Sdk.RpcService]
public sealed class AmbiguousService : IFirstService, ISecondService
{
    public ValueTask<int> First(int value) => new(value);
    public ValueTask<int> Second(int value) => new(value);
}
""");

        EnsureHasRuleContaining(source, "SHARPLINK017", "AmbiguousService");
        return Task.CompletedTask;
    }

    [Test]
    public Task MultipleStaticServicesForContractShouldReportSharplink023()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value);
}

[SharpLink.Sdk.RpcService]
public sealed class FirstHelloService : IHelloService
{
    public ValueTask<int> Echo(int value) => new(value);
}

[SharpLink.Sdk.RpcService]
public sealed class SecondHelloService : IHelloService
{
    public ValueTask<int> Echo(int value) => new(value);
}
""");

        EnsureHasRuleContaining(source, "SHARPLINK023", "IHelloService");
        return Task.CompletedTask;
    }

    [Test]
    public Task MarkedServiceConstructorsShouldParticipateInStaticConflictAnalysis()
    {
        var source = BuildSource("""
namespace Microsoft.Extensions.DependencyInjection
{
    [System.AttributeUsage(System.AttributeTargets.Constructor)]
    public sealed class ActivatorUtilitiesConstructorAttribute : System.Attribute { }
}

[SharpLink.Sdk.RpcContract]
public interface IMarkedContract : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value);
}

[SharpLink.Sdk.RpcService]
public sealed class FirstMarkedService : IMarkedContract
{
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public FirstMarkedService() { }
    public FirstMarkedService(string ignored) { }
    public ValueTask<int> Echo(int value) => new(value);
}

[SharpLink.Sdk.RpcService]
public sealed class SecondMarkedService : IMarkedContract
{
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public SecondMarkedService() { }
    public SecondMarkedService(string ignored) { }
    public ValueTask<int> Echo(int value) => new(value);
}
""");

        EnsureHasRuleContaining(source, "SHARPLINK023", "IMarkedContract");
        return Task.CompletedTask;
    }

    [Test]
    public Task ServiceConstructorsMustBeRepresentableByGeneratedDiActivation()
    {
        var source = BuildSource("""
public sealed class Dependency;
public ref struct StackDependency;

[SharpLink.Sdk.RpcContract]
public interface IRefConstructorService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcService]
public sealed class RefConstructorService : IRefConstructorService
{
    public RefConstructorService(ref Dependency dependency) { }
    public ValueTask<int> Echo(int value, CancellationToken cancellationToken) => new(value);
}

[SharpLink.Sdk.RpcContract]
public interface IStackConstructorService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcService]
public sealed class StackConstructorService : IStackConstructorService
{
    public StackConstructorService(StackDependency dependency) { }
    public ValueTask<int> Echo(int value, CancellationToken cancellationToken) => new(value);
}

[SharpLink.Sdk.RpcContract]
public interface IPointerConstructorService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcService]
public sealed class PointerConstructorService : IPointerConstructorService
{
    public unsafe PointerConstructorService(int* dependency) { }
    public ValueTask<int> Echo(int value, CancellationToken cancellationToken) => new(value);
}

[SharpLink.Sdk.RpcContract]
public interface IRefReadonlyConstructorService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcService]
public sealed class RefReadonlyConstructorService : IRefReadonlyConstructorService
{
    public RefReadonlyConstructorService(ref readonly Dependency dependency) { }
    public ValueTask<int> Echo(int value, CancellationToken cancellationToken) => new(value);
}
""");

        EnsureRuleCount(source, "SHARPLINK019", 4);
        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(!generated.Contains("typeof(global::RefConstructorService)", StringComparison.Ordinal),
            "a ref dependency must suppress its generated service descriptor");
        Ensure(!generated.Contains("typeof(global::StackConstructorService)", StringComparison.Ordinal),
            "a ref-like dependency must suppress its generated service descriptor");
        Ensure(!generated.Contains("typeof(global::PointerConstructorService)", StringComparison.Ordinal),
            "a pointer dependency must suppress its generated service descriptor");
        Ensure(!generated.Contains("typeof(global::RefReadonlyConstructorService)", StringComparison.Ordinal),
            "a ref-readonly dependency must suppress a generated call that requires addressable storage");
        return Task.CompletedTask;
    }
}
