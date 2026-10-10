from pathlib import Path
import sys
import hashlib
import subprocess
p=Path(sys.argv[1])/"src/SharpLink.Runtime/StreamManager.cs"
s=p.read_text()
assert hashlib.sha256(p.read_bytes()).hexdigest() == "c28eac60829aade9fdbb14d5b946df733d3d74e417085131355188cf84577b02", "Unexpected baseline StreamManager source"
assert s.count('private readonly Dictionary<ushort, DispatcherEntry> _byStreamId = [];') == 1
for pattern, count in {
    'if (!_byStreamId.TryGetValue(streamId,': 3,
    'if (_byStreamId.TryGetValue(streamId,': 2,
    'var found = _byStreamId.TryGetValue(streamId,': 2,
    'if (_byStreamId.Remove(streamId,': 1,
    '_byStreamId.Count == 0': 1,
    'foreach (var pair in _byStreamId)': 1,
    'entries = [.. _byStreamId.Values];': 1,
}.items():
    assert s.count(pattern) == count, (pattern, s.count(pattern))
s=s.replace('private readonly Dictionary<ushort, DispatcherEntry> _byStreamId = [];', 'private Dictionary<ushort, DispatcherEntry>? _byStreamId;')
s=s.replace('if (_byStreamId.ContainsKey(streamId))','var children = _byStreamId ??= new();\n                if (children.ContainsKey(streamId))').replace('_byStreamId.Add(streamId, new DispatcherEntry(dispatcher));','children.Add(streamId, new DispatcherEntry(dispatcher));')
s=s.replace('if (!_byStreamId.TryGetValue(streamId,','if (_byStreamId is null || !_byStreamId.TryGetValue(streamId,').replace('if (_byStreamId.TryGetValue(streamId,','if (_byStreamId is not null && _byStreamId.TryGetValue(streamId,').replace('var found = _byStreamId.TryGetValue(streamId,','var found = _byStreamId is not null && _byStreamId.TryGetValue(streamId,').replace('if (_byStreamId.Remove(streamId,','if (_byStreamId is not null && _byStreamId.Remove(streamId,')
s=s.replace('''                foreach (var pair in _byStreamId)
                {
                    pair.Value.Close();
                    entries.Add(new RequestDrainEntry(pair.Key, pair.Value));
                }
                _byStreamId.Clear();''','''                if (_byStreamId is not null)
                {
                    foreach (var pair in _byStreamId)
                    {
                        pair.Value.Close();
                        entries.Add(new RequestDrainEntry(pair.Key, pair.Value));
                    }
                    _byStreamId.Clear();
                }''')
s=s.replace('''                count += _byStreamId.Count;
                entries = [.. _byStreamId.Values];
                _byStreamId.Clear();''','''                count += _byStreamId?.Count ?? 0;
                entries = _byStreamId is null ? [] : [.. _byStreamId.Values];
                _byStreamId?.Clear();''').replace('_byStreamId.Count == 0','(_byStreamId?.Count ?? 0) == 0')
p.write_text(s)

changed = subprocess.check_output(["git", "-C", sys.argv[1], "diff", "--name-only"], text=True).splitlines()
assert changed == ["src/SharpLink.Runtime/StreamManager.cs"], changed
assert "private readonly Dictionary<ushort, DispatcherEntry> _byStreamId" not in s
assert s.count("var children = _byStreamId ??= new();") == 1
