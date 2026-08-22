from pathlib import Path

p = Path('test/SharpLink.IntegrationTests/IntegrationBehaviorTests.cs')
text = p.read_text()
text = text.replace(
    'public async [Sdk.Timeout(0.1)]\n    ValueTask<int> SlowAddWithMethodTimeoutAsync(',
    'public async ValueTask<int> SlowAddWithMethodTimeoutAsync(')
p.write_text(text)
