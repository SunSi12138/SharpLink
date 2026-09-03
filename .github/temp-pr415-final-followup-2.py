from pathlib import Path


def replace_once(path, old, new):
    p = Path(path)
    text = p.read_text()
    if old not in text:
        raise SystemExit(f"missing expected block in {path}: {old[:160]!r}")
    p.write_text(text.replace(old, new, 1))

# The first patch already updates the actual RunContractGenerator compilation. Assert that
# exact invariant here so the validation script fails loudly if its targeting regresses.
helper = Path("test/SharpLink.Generator.Tests/ContractManifestGeneratorTestHelpers.cs").read_text()
required_helper = '''        var compilation = CSharpCompilation.Create(
            "ContractManifestTestAssembly",
            [syntaxTree],
            GetPlatformReferences().Concat(additionalReferences),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
'''
if required_helper not in helper:
    raise SystemExit("RunContractGenerator does not include additionalReferences in its compilation")

# Make the direct-baseline regression prove both persistence surfaces, not just the final
# compatibility diagnostic. This distinguishes manifest construction from comparison.
path = "test/SharpLink.Generator.Tests/RpcCodecTenthReviewRegressionTests.cs"
replace_once(path,
'''        var directBaseline = RunContractGenerator(directConsumer, additionalReferences: [h1]).Json;
        var directChanged = RunContractGenerator(directConsumer, directBaseline, additionalReferences: [h2]);
''',
'''        var directBaseline = RunContractGenerator(directConsumer, additionalReferences: [h1]).Json;
        var directDocument = System.Text.Json.Nodes.JsonNode.Parse(directBaseline)!.AsObject();
        var directRequest = directDocument["contracts"]!.AsArray()[0]!["methods"]!.AsArray()[0]!["request"]!.AsArray()[0]!.AsObject();
        Ensure(IsValidCodecHashText(directRequest["codecHash"]?.GetValue<string>()),
            "a direct referenced final Codec leaf must persist its exact hash on the request value");
        var directReferencedCodec = directDocument["codecs"]!.AsArray()
            .Select(static item => item!.AsObject())
            .Single(static item => item["type"]!.GetValue<string>() == "Referenced.Payload");
        Ensure(directReferencedCodec["kind"]!.GetValue<string>() == "Referenced" &&
               IsValidCodecHashText(directReferencedCodec["codecHash"]?.GetValue<string>()),
            "a direct referenced final Codec leaf must also persist in the reachable Codec identity inventory");
        var directChanged = RunContractGenerator(directConsumer, directBaseline, additionalReferences: [h2]);
''')

# Avoid relying on non-generic IDictionary implementation details in the shutdown assertion.
path = "test/SharpLink.IntegrationTests/RuntimeAssemblyDependencyIdentityIntegrationTests.cs"
replace_once(path,
'''        return ((System.Collections.IDictionary)(field.GetValue(endpoint)
            ?? throw new InvalidOperationException("Dynamic module registry was null."))).Count;
''',
'''        var registry = field.GetValue(endpoint)
            ?? throw new InvalidOperationException("Dynamic module registry was null.");
        var countProperty = registry.GetType().GetProperty("Count")
            ?? throw new InvalidOperationException("Dynamic module registry count was unavailable.");
        return (int)(countProperty.GetValue(registry)
            ?? throw new InvalidOperationException("Dynamic module registry count was null."));
''')

print("PR415 follow-up validation patch 2 applied")
