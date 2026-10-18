using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task Issue870IndependentParentsWithSameSignatureMustBeDiagnosed()
    {
        const string declarations = """
public interface ILeft
{
    [SharpLink.Sdk.NonCancellable] ValueTask<long> Read(long key);
}
public interface IRight
{
    [SharpLink.Sdk.NonCancellable] ValueTask<long> Read(long key);
}
""";

        var ambiguous = BuildSource(declarations + """
[SharpLink.Sdk.RpcContract]
public interface ICombined : SharpLink.Sdk.IService, ILeft, IRight { }
""");
        EnsureHasRuleContaining(ambiguous, "SHARPLINK057", "redeclare");
        var generated = string.Join("\n", RunGeneratorAndGetSources(ambiguous));
        Ensure(!generated.Contains("ICombined", StringComparison.Ordinal),
            "ambiguous method dispatch must not publish a broken combined contract");

        var disambiguated = BuildSource(declarations + """
[SharpLink.Sdk.RpcContract]
public interface ICombined : SharpLink.Sdk.IService, ILeft, IRight
{
    [SharpLink.Sdk.NonCancellable] new ValueTask<long> Read(long key);
}
""");
        EnsureDoesNotHaveRule(disambiguated, "SHARPLINK057");
        Ensure(string.Join("\n", RunGeneratorAndGetSources(disambiguated))
                .Contains("ICombined", StringComparison.Ordinal),
            "direct redeclaration should keep generating");

        var shared = BuildSource("""
public interface IBase
{
    [SharpLink.Sdk.NonCancellable] ValueTask<long> Read(long key);
}
public interface ILeft : IBase { }
public interface IRight : IBase { }
[SharpLink.Sdk.RpcContract]
public interface ICombined : SharpLink.Sdk.IService, ILeft, IRight { }
""");
        EnsureDoesNotHaveRule(shared, "SHARPLINK057");
        Ensure(string.Join("\n", RunGeneratorAndGetSources(shared))
                .Contains("ICombined", StringComparison.Ordinal),
            "common-ancestor declarations must not be treated as independent methods");
        return Task.CompletedTask;
    }
}
