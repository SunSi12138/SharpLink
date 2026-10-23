using System;
using System.Linq;
using Microsoft.CodeAnalysis;

[Generator]
public sealed class FixtureGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var modes = context.AdditionalTextsProvider.Select((file, cancellation) => file.GetText(cancellation)?.ToString().Trim()).Collect();
        context.RegisterSourceOutput(modes, (output, values) =>
        {
        if (values.Any(value => value == "FAIL"))
            throw new InvalidOperationException("Intentional inventory generator counterfactual");
        output.AddSource("Fixture.g.cs", """
            namespace Fixture.Generated;
            public class Outer
            {
                public class PublicNested { public int Value => 42; }
                protected class ProtectedNested { }
                internal class InternalNested { }
            }
            internal sealed class InternalGenerated { public string Name => "generated"; }
            """);
        });
    }
}
