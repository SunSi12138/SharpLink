from pathlib import Path
import re

p = Path('test/SharpLink.Generator.Tests/RpcAnalyzerTests.cs')
text = p.read_text()

# SHARPLINK007 belonged exclusively to the removed pseudo-parameter and is removed with it.
pattern = re.compile(
    r'\n    \[Test\]\n    public Task MultipleCallOptionsShouldReportSharplink007\(\)\n    \{.*?\n    \}\n(?=\n    \[Test\])',
    re.S)
text, count = pattern.subn('\n', text, count=1)
assert count == 1

# SHARPLINK008 remains useful for the only retained control parameter: CancellationToken.
old = '''    public Task MisplacedControlParameterShouldReportSharplink008()
    {
        var source = BuildSource("""
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(SharpLink.Sdk.SharpLinkCallOptions options, int value, CancellationToken cancellationToken);
}
""");'''
new = '''    public Task MisplacedControlParameterShouldReportSharplink008()
    {
        var source = BuildSource("""
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(CancellationToken cancellationToken, int value);
}
""");'''
assert old in text
text = text.replace(old, new)

# Identifier escaping coverage no longer needs a removed framework-owned parameter.
text = text.replace(
    'ValueTask<int> @class(int @event, SharpLink.Sdk.SharpLinkCallOptions @params, CancellationToken @default);',
    'ValueTask<int> @class(int @event, CancellationToken @default);')

# The synthetic SDK used by analyzer tests should match the public SDK after the removal.
text = text.replace('    public readonly record struct SharpLinkCallOptions;\n\n', '')
p.write_text(text)
