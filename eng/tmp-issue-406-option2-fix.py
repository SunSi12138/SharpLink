from pathlib import Path

path = Path('src/SharpLink.Server/SharpLinkServer.Interceptors.cs')
text = path.read_text()
replacements = [
    ('            private ServerPipelineFacts _owner;\n', '            private ServerPipelineFacts? _owner;\n'),
    ('                    ? _owner.InvokeNextAsync(_nextIndex, context)\n', '                    ? _owner!.InvokeNextAsync(_nextIndex, context)\n'),
]
for old, new in replacements:
    if text.count(old) != 1:
        raise SystemExit(f'expected one occurrence: {old!r}; found {text.count(old)}')
    text = text.replace(old, new, 1)
path.write_text(text)
print('issue 406 option-2 nullable fallback fixed')
