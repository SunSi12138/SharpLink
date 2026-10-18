using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task Issue874RecordClassesShouldReachServiceAnalysisAndManifest()
    {
        foreach (var declaration in new[] { "class", "record", "record class" })
        {
            var source = BuildSource($$"""
[SharpLink.Sdk.RpcContract]
public interface IRecordAudit : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable] ValueTask<int> Get();
}
[SharpLink.Sdk.RpcService]
public sealed {{declaration}} RecordService : IRecordAudit
{
    public ValueTask<int> Get() => new(1);
}
""");
            var manifest = GetGeneratedManifest(source);
            Ensure(CountOccurrences(manifest, "typeof(global::RecordService)") == 1,
                $"{declaration} service must contribute one manifest descriptor");
        }
        var invalid = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IRecordAudit : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable] ValueTask<int> Get();
}
[SharpLink.Sdk.RpcService]
public abstract record AbstractRecordService : IRecordAudit
{
    public abstract ValueTask<int> Get();
}
""");
        EnsureHasRule(invalid, "SHARPLINK018");
        return Task.CompletedTask;
    }
}
