#!/usr/bin/env python3
"""Preflight lazy non-default route dictionary without changing route ownership."""
from pathlib import Path
import subprocess

path = Path('src/SharpLink.Runtime/StreamManager.Routing.cs')
expected = '4920e4c6b1d2f6c0647986c6662c969b04d4080c'
actual = subprocess.check_output(['git', 'hash-object', str(path)], text=True).strip()
if actual != expected:
    raise SystemExit(f'Unexpected routing blob: {actual}; expected {expected}')
text = path.read_text()

def replace(old: str, new: str, count: int = 1) -> None:
    global text
    if text.count(old) != count:
        raise SystemExit(f'Expected {count} source anchors: {old!r}; got {text.count(old)}')
    text = text.replace(old, new)

replace('        private readonly Dictionary<ushort, DispatcherEntry> _byStreamId = [];',
        '        // Default-only requests use the inline slot; allocate the named map only\n'
        '        // when a nonzero stream is actually published under the same registry lock.\n'
        '        private Dictionary<ushort, DispatcherEntry>? _byStreamId;')
replace('if (streamId == 0 ? _defaultDispatcher is not null : _byStreamId.ContainsKey(streamId))',
        'if (streamId == 0 ? _defaultDispatcher is not null : _byStreamId?.ContainsKey(streamId) == true)')
replace('                        _byStreamId.Add(streamId, entry);',
        '                        (_byStreamId ??= []).Add(streamId, entry);')
replace('                if (!_byStreamId.TryGetValue(streamId, out var entry)',
        '                if (_byStreamId is null || !_byStreamId.TryGetValue(streamId, out var entry)', 3)
replace('                if (_byStreamId.TryGetValue(streamId, out var found) &&',
        '                if (_byStreamId is not null && _byStreamId.TryGetValue(streamId, out var found) &&')
replace('                var found = _byStreamId.TryGetValue(streamId, out var entry)',
        '                var found = _byStreamId is not null && _byStreamId.TryGetValue(streamId, out var entry)', 2)
replace('                    if (_byStreamId.TryGetValue(streamId, out entry!) && entry.TryAcquire())',
        '                    if (_byStreamId is not null && _byStreamId.TryGetValue(streamId, out entry!) && entry.TryAcquire())')
replace('                    : _byStreamId.TryGetValue(streamId, out var candidate) ? candidate : null;',
        '                    : _byStreamId is not null && _byStreamId.TryGetValue(streamId, out var candidate) ? candidate : null;')
replace('                if (!_byStreamId.TryGetValue(streamId, out var found) ||',
        '                if (_byStreamId is null || !_byStreamId.TryGetValue(streamId, out var found) ||')
replace('''                foreach (var pair in _byStreamId)
                {
                    if (!pair.Value.TryClaimRetirement())
                        continue;
                    pair.Value.Close();
                    entries.Add(new RequestDrainEntry(pair.Key, pair.Value));
                }''',
        '''                if (_byStreamId is { } namedDispatchers)
                {
                    foreach (var pair in namedDispatchers)
                    {
                        if (!pair.Value.TryClaimRetirement())
                            continue;
                        pair.Value.Close();
                        entries.Add(new RequestDrainEntry(pair.Key, pair.Value));
                    }
                }''')
replace('''                foreach (var entry in _byStreamId.Values)
                {
                    if (!entry.TryClaimRetirement())
                        continue;
                    entry.Close();
                    claimed.Add(entry);
                }
                _byStreamId.Clear();''',
        '''                if (_byStreamId is { } namedDispatchers)
                {
                    foreach (var entry in namedDispatchers.Values)
                    {
                        if (!entry.TryClaimRetirement())
                            continue;
                        entry.Close();
                        claimed.Add(entry);
                    }
                    namedDispatchers.Clear();
                }''')
replace('                    return _defaultDispatcher is null && _byStreamId.Count == 0;',
        '                    return _defaultDispatcher is null && (_byStreamId is null || _byStreamId.Count == 0);')
path.write_text(text)
subprocess.run(['git', 'diff', '--check'], check=True)
subprocess.run(['git', 'diff', '--', str(path)], check=True)
print('Changed only lazy named-map allocation; default routing, locks, receive leases and retirement remain unchanged.')
