"""Regression checks: missing/malformed/per-frame allocating evidence cannot pass."""
import json
from pathlib import Path
import subprocess
import sys
import tempfile

script = Path(__file__).with_name('summarize.py')
with tempfile.TemporaryDirectory() as directory:
    root = Path(directory)
    for mode in ('fullopt', 'pgo', 'native'):
        rows = []
        for arity in (0, 1, 2, 4, 8, 127):
            if mode != 'native':
                rows += [dict(layer='known-route', arity=arity, repeat=r, ns=20.0, bytes=0) for r in range(7)]
            for requests in (1, 8, 32, 128):
                rows += [dict(requests=requests, arity=arity, repeat=r, ns=40.0, bytes=0) for r in range(5)]
            for field in ('registrationBytesPerRequest', 'cleanupBytesPerRequest', 'individualTerminalLifecycleBytesPerRequest'):
                rows.append(dict(arity=arity, **{field: 100.0}))
        payload = json.dumps(rows)
        for arm in ('baseline', 'candidate', 'lazy'):
            for pair in range(1, 7):
                (root/f'{arm}-{pair}.json').write_text(payload)
        def verify(success):
            result = subprocess.run([sys.executable, str(script), str(root), mode], capture_output=True)
            assert (result.returncode == 0) == success, result.stderr.decode()
        verify(True)
        target = root/'candidate-1.json'
        for bad in (rows[:-1], rows + rows[:1], [dict(rows[0], bytes=64)] + rows[1:], [dict(rows[0], ns=float('nan'))] + rows[1:]):
            target.write_text(json.dumps(bad))
            verify(False)
        target.write_text('{')
        verify(False)
        target.unlink()
        verify(False)
        target.write_text(payload)
print('Strict evidence validation positive and negative controls passed for all three modes.')
