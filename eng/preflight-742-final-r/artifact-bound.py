#!/usr/bin/env python3
"""Bound each evidence download after timing without changing retained source bytes."""
import json
import os
import pathlib
import sys
import tempfile
import zipfile

root = pathlib.Path(sys.argv[1]).resolve()
limit = 31 * 1024 * 1024
archives = []
raw = []
for path in sorted(root.rglob('*')):
    if not path.is_file():
        continue
    relative = path.relative_to(root)
    if relative.parts[0] == 'writer-bin' or relative.parts[:2] == ('aot', 'bin'):
        continue
    if path.name.endswith('.tar.gz'):
        archives.append({'path': str(relative), 'bytes': path.stat().st_size, 'within_bound': path.stat().st_size < limit})
    else:
        raw.append(path)
assert raw, 'No raw evidence available'
with tempfile.TemporaryFile() as handle:
    with zipfile.ZipFile(handle, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in raw:
            archive.write(path, str(path.relative_to(root)))
    size = handle.tell()
raw_ready = size < limit - 65536
proof = {'raw_zip_bytes_before_manifest': size, 'limit_bytes': limit, 'raw_file_count': len(raw), 'raw_within_bound': raw_ready, 'native_archives': archives}
(root / 'artifact-bound.json').write_text(json.dumps(proof, indent=2) + '\n')
print(json.dumps(proof, indent=2))

keys = {'native-micro-publish.tar.gz': 'micro', 'native-rpc-elf-dev.tar.gz': 'rpc_dev',
        'native-rpc-elf-candidate.tar.gz': 'rpc_candidate', 'native-writer-publish.tar.gz': 'writer'}
outputs = {'raw_ready': raw_ready}
for item in archives:
    name = pathlib.Path(item['path']).name
    assert name in keys, f'Unexpected native archive: {name}'
    outputs[keys[name] + '_ready'] = item['within_bound']
if os.environ.get('GITHUB_OUTPUT'):
    with open(os.environ['GITHUB_OUTPUT'], 'a') as handle:
        for key, value in outputs.items():
            handle.write(f'{key}={str(value).lower()}\n')
assert raw_ready and all(item['within_bound'] for item in archives), 'Oversized evidence retained locally; bounded raw and other archives remain uploadable'
