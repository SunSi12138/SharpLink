from pathlib import Path

path = Path('.github/issue-287-review5-p2b.py')
text = path.read_text()
old = '''# Bound the whole non-streaming interceptor chain, not only the post-await result.
path = "src/SharpLink.Client/SharpLinkClient.Interceptors.cs"
replace_once(
    path,
    "var result = await InvokeNextAsync(0, _context).ConfigureAwait(false);",
    "var result = await AwaitInvocationWithinFrozenDeadlineAsync(\\n                    InvokeNextAsync(0, _context)).ConfigureAwait(false);")
# The same source text occurs three times; replace_once only handled the first. Apply to the remaining two.
p = Path(path)
text = p.read_text()
old = "var result = await InvokeNextAsync(0, _context).ConfigureAwait(false);"
assert text.count(old) == 2, f"{path}: expected two remaining direct interceptor awaits"
text = text.replace(
    old,
    "var result = await AwaitInvocationWithinFrozenDeadlineAsync(\\n                    InvokeNextAsync(0, _context)).ConfigureAwait(false);")
p.write_text(text)
'''
new = '''# Bound the whole non-streaming interceptor chain, not only the post-await result.
path = "src/SharpLink.Client/SharpLinkClient.Interceptors.cs"
p = Path(path)
source = p.read_text()
direct_await = "var result = await InvokeNextAsync(0, _context).ConfigureAwait(false);"
assert source.count(direct_await) == 3, f"{path}: expected three direct interceptor awaits"
source = source.replace(
    direct_await,
    "var result = await AwaitInvocationWithinFrozenDeadlineAsync(\\n                    InvokeNextAsync(0, _context)).ConfigureAwait(false);")
p.write_text(source)
'''
assert text.count(old) == 1, 'unexpected P2B interceptor preamble'
path.write_text(text.replace(old, new, 1))
