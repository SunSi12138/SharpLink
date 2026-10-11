using System.Linq;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public void ShapeEncodingShouldKeepRejecting128ClientStreams()
    {
        var parameters = string.Join(", ", Enumerable.Range(0, 128)
            .Select(index => $"IAsyncEnumerable<int> stream{index}"));
        EnsureHasRuleContaining(SimpleContract($"ValueTask Consume({parameters});"),
            "SHARPLINK003", "at most 127");
    }
}
