from pathlib import Path
import re

# Models: CallOptions is no longer a framework/control parameter.
p = Path('src/SharpLink.Generator/RpcGenerator.Models.cs')
text = p.read_text()
text = text.replace('    bool HasCallOptions,\n', '')
text = text.replace('    bool IsCallOptions,\n', '')
text = text.replace('internal readonly record struct InvalidCallOptionsMethodModel(string MethodName, Location? Location);\n', '')
p.write_text(text)

# Registration/diagnostic pipeline.
p = Path('src/SharpLink.Generator/RpcGenerator.cs')
text = p.read_text()
text, count = re.subn(
    r'        var invalidCallOptionsMethods = context\.SyntaxProvider\.ForAttributeWithMetadataName\(\n.*?            \.Where\(x => x\.Length > 0\);\n',
    '', text, count=1, flags=re.S)
assert count == 1
text, count = re.subn(
    r'        context\.RegisterSourceOutput\(invalidCallOptionsMethods, static \(spc, methods\) =>\n        \{\n.*?        \}\);\n',
    '', text, count=1, flags=re.S)
assert count == 1
p.write_text(text)

p = Path('src/SharpLink.Generator/RpcGenerator.Diagnostics.cs')
text = p.read_text()
start = text.index('    private static readonly DiagnosticDescriptor MultipleCallOptionsRule = new(')
end = text.index('    private static readonly DiagnosticDescriptor ControlParameterOrderRule = new(', start)
text = text[:start] + text[end:]
text = text.replace('title: "Invalid RPC Control Parameter Order",', 'title: "Invalid RPC CancellationToken Position",')
text = text.replace(
    'messageFormat: "RPC method \'{0}\' must place SharpLinkCallOptions and CancellationToken last, with CancellationToken last when both are present",',
    'messageFormat: "RPC method \'{0}\' must place CancellationToken last",')
p.write_text(text)

p = Path('src/SharpLink.Generator/AnalyzerReleases.Unshipped.md')
text = '\n'.join(line for line in p.read_text().splitlines() if 'SHARPLINK007' not in line) + '\n'
text = text.replace('SharpLinkCallOptions and CancellationToken', 'CancellationToken')
p.write_text(text)

# Analysis: only CancellationToken remains a non-payload contract control parameter.
p = Path('src/SharpLink.Generator/RpcGenerator.Analysis.cs')
text = p.read_text()
start = text.index('    private static ImmutableArray<InvalidCallOptionsMethodModel> GetInvalidCallOptionsMethods(')
end = text.index('    private static ImmutableArray<InvalidControlParameterOrderModel> GetInvalidControlParameterOrderMethods(', start)
text = text[:start] + text[end:]

start = text.index('    private static ImmutableArray<InvalidControlParameterOrderModel> GetInvalidControlParameterOrderMethods(')
end = text.index('    private static ImmutableArray<InvalidGenericUsageModel> GetInvalidGenericUsage(', start)
replacement = '''    private static ImmutableArray<InvalidControlParameterOrderModel> GetInvalidControlParameterOrderMethods(
        GeneratorAttributeSyntaxContext context,
        CancellationToken _)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol || symbol.TypeKind != TypeKind.Interface ||
            !InheritsIService(symbol))
            return ImmutableArray<InvalidControlParameterOrderModel>.Empty;

        var list = ImmutableArray.CreateBuilder<InvalidControlParameterOrderModel>();
        foreach (var method in GetContractMethods(symbol))
        {
            var cancellationIndex = -1;
            for (var index = 0; index < method.Parameters.Length; index++)
            {
                if (IsCancellationTokenParameter(method.Parameters[index]))
                    cancellationIndex = index;
            }

            if (cancellationIndex < 0 || cancellationIndex == method.Parameters.Length - 1)
                continue;
            list.Add(new InvalidControlParameterOrderModel(method.Name, method.Locations.FirstOrDefault()));
        }
        return list.ToImmutable();
    }

'''
text = text[:start] + replacement + text[end:]

text = re.sub(
    r'\n    private static bool IsCallOptionsParameter\(IParameterSymbol parameter\)\n        => .*?;\n',
    '\n', text, count=1)
start = text.index('    private static bool HasValidControlParameterOrder(IMethodSymbol method)')
end = text.index('    private static bool InheritsIService(', start)
text = text[:start] + '''    private static bool HasValidControlParameterOrder(IMethodSymbol method)
        => !method.Parameters.Any(IsCancellationTokenParameter) ||
           IsCancellationTokenParameter(method.Parameters[method.Parameters.Length - 1]);

''' + text[end:]

text = text.replace('            if (IsCancellationTokenParameter(leftParameter) ||\n                IsCallOptionsParameter(leftParameter))\n',
                    '            if (IsCancellationTokenParameter(leftParameter))\n')
text = text.replace('                    var isCallOptions = IsCallOptionsParameter(p);\n', '')
text = text.replace('                        isCancellationToken,\n                        isCallOptions,\n', '                        isCancellationToken,\n')
text = text.replace('                        !IsCancellationTokenParameter(parameter) &&\n                        !IsCallOptionsParameter(parameter))',
                    '                        !IsCancellationTokenParameter(parameter))')
text = text.replace('.Where(static parameter => !parameter.IsCancellationToken && !parameter.IsCallOptions)',
                    '.Where(static parameter => !parameter.IsCancellationToken)')
text = text.replace('                    HasCallOptions: paramArray.Any(p => p.IsCallOptions),\n', '')
# Other validation paths should no longer give CallOptions special treatment.
text = text.replace('m.Parameters.Count(IsCallOptionsParameter) > 1 ||\n                    ', '')
text = text.replace(' || IsCallOptionsParameter(parameter)', '')
text = text.replace(' ||\n                IsCallOptionsParameter(leftParameter)', '')
p.write_text(text)

# Proxy always calls the low-level channel with no explicit metadata; interceptors may add it.
p = Path('src/SharpLink.Generator/RpcGenerator.ProxyEmitter.cs')
text = p.read_text()
text = text.replace('        var optionsParameter = method.Parameters.FirstOrDefault(static parameter => parameter.IsCallOptions);\n', '')
text = text.replace('        var options = optionsParameter is null ? "default" : EscapeIdentifier(optionsParameter.Name);\n', '')
text = text.replace('{options}, {cancellationToken}', 'default, {cancellationToken}')
text = text.replace(' && !parameter.IsCallOptions', '')
p.write_text(text)

# Stub no longer reconstructs runtime control into a business parameter.
p = Path('src/SharpLink.Generator/RpcGenerator.StubEmitter.cs')
text = p.read_text()
text = text.replace(', IsCallOptions: false', '')
text = text.replace('                    IsCallOptions: false,\n', '')
text, count = re.subn(
    r'\n            foreach \(var p in method\.Parameters\.Where\(p => p\.IsCallOptions\)\)\n            \{\n.*?\n            \}\n',
    '\n', text, count=1, flags=re.S)
assert count == 1
text = text.replace(' && !p.IsCallOptions', '')
text = text.replace(' && !parameter.IsCallOptions', '')
p.write_text(text)

# Manifest/DTO helpers no longer exclude a removed pseudo-parameter.
for filename in [
    'src/SharpLink.Generator/RpcGenerator.ContractManifest.cs',
    'src/SharpLink.Generator/RpcGenerator.DtoAnalysis.cs',
]:
    p = Path(filename)
    text = p.read_text()
    text = text.replace(' && !parameter.IsCallOptions', '')
    text = text.replace(' && !p.IsCallOptions', '')
    text = text.replace('!parameter.IsCallOptions && ', '')
    p.write_text(text)

# Remove public API/type forwarding entirely.
p = Path('src/SharpLink.Sdk/TypeForwards.cs')
text = p.read_text().replace('[assembly: TypeForwardedTo(typeof(SharpLinkCallOptions))]\n', '')
p.write_text(text)
Path('src/SharpLink.Abstractions/Sdk/SharpLinkCallOptions.cs').unlink()
