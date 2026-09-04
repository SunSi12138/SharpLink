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
    public Task MultipleCancellationTokensShouldReportSharplink002()
    {
        var source = BuildSource("""
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken ct1, CancellationToken ct2);
}
""");
        source = source.Replace("public interface IHelloService : SharpLink.Sdk.IService", "[SharpLink.Sdk.RpcContract]\npublic interface IHelloService : SharpLink.Sdk.IService");

        EnsureHasRule(source, "SHARPLINK002");
        return Task.CompletedTask;
    }

    [Test]
    public Task TooManyStreamParametersShouldReportSharplink003()
    {
        var parameters = string.Join(", ",
            Enumerable.Range(0, 128).Select(i => $"IAsyncEnumerable<int> p{i}"));
        var source = BuildSource($$"""
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo({{parameters}});
}
""");
        source = source.Replace("public interface IHelloService : SharpLink.Sdk.IService", "[SharpLink.Sdk.RpcContract]\npublic interface IHelloService : SharpLink.Sdk.IService");

        EnsureHasRule(source, "SHARPLINK003");
        return Task.CompletedTask;
    }

    [Test]
    public Task MissingCancellationTokenShouldReportSharplink004()
    {
        var source = BuildSource("""
public interface IHelloService : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.Timeout(1)]
    ValueTask<int> Echo(int value);
}
""");
        source = source.Replace("public interface IHelloService : SharpLink.Sdk.IService", "[SharpLink.Sdk.RpcContract]\npublic interface IHelloService : SharpLink.Sdk.IService");

        EnsureHasRule(source, "SHARPLINK004");
        return Task.CompletedTask;
    }

    [Test]
    public Task StreamingWithoutCancellationTokenShouldReportSharplink014()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    IAsyncEnumerable<int> Download(int count);
}
""");

        EnsureHasRule(source, "SHARPLINK014");
        EnsureDoesNotHaveRule(source, "SHARPLINK004");
        return Task.CompletedTask;
    }

    [Test]
    public Task NonCancellableWithCancellationTokenShouldReportSharplink015()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable]
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}
""");

        EnsureHasRule(source, "SHARPLINK015");
        return Task.CompletedTask;
    }

    [Test]
    public Task InvalidOnewayReturnShapesShouldReportSharplink056()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IInvalidOnewayContract : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.Oneway]
    Task<int> TaskResult(CancellationToken cancellationToken);

    [SharpLink.Sdk.Oneway]
    ValueTask<int> ValueTaskResult(CancellationToken cancellationToken);

    [SharpLink.Sdk.Oneway]
    IAsyncEnumerable<int> StreamResult(CancellationToken cancellationToken);
}
""");

        EnsureRuleCount(source, "SHARPLINK056", 3);

        var valid = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IValidOnewayContract : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.Oneway]
    Task Fire(CancellationToken cancellationToken);

    [SharpLink.Sdk.Oneway]
    ValueTask Send(CancellationToken cancellationToken);
}
""");
        EnsureDoesNotHaveRule(valid, "SHARPLINK056");
        return Task.CompletedTask;
    }

    [Test]
    public Task ConflictingInheritedOnewayShapesShouldReportASpecificDiagnostic()
    {
        var source = BuildSource("""
public interface IFireAndForgetBase
{
    [SharpLink.Sdk.Oneway]
    ValueTask Notify(CancellationToken cancellationToken);
}

public interface IAcknowledgedBase
{
    ValueTask Notify(CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcContract]
public interface IConflictingOnewayContract : SharpLink.Sdk.IService, IFireAndForgetBase, IAcknowledgedBase
{
}
""");

        EnsureRuleCount(source, "SHARPLINK057", 1);
        Ensure(!string.Join("\n", RunGeneratorAndGetSources(source)).Contains(
                "IConflictingOnewayContractProxy",
                StringComparison.Ordinal),
            "a conflicting inherited Oneway shape must not emit contract artifacts");
        return Task.CompletedTask;
    }

    [Test]
    public Task ExplicitNonCancellableShouldSuppressSharplink004()
    {
        var source = BuildSource("""
public interface IHelloService : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable]
    ValueTask<int> Echo(int value);
}
""");
        source = source.Replace("public interface IHelloService : SharpLink.Sdk.IService", "[SharpLink.Sdk.RpcContract]\npublic interface IHelloService : SharpLink.Sdk.IService");

        EnsureDoesNotHaveRule(source, "SHARPLINK004");
        return Task.CompletedTask;
    }

    [Test]
    public Task ExplicitNonCancellableShouldSuppressSharplink014()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable]
    IAsyncEnumerable<int> Download(int count);
}
""");

        EnsureDoesNotHaveRule(source, "SHARPLINK014");
        return Task.CompletedTask;
    }
}
