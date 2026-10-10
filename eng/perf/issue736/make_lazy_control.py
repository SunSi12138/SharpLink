from pathlib import Path
import sys
p=Path(sys.argv[1])/"src/SharpLink.Runtime/StreamManager.cs"
s=p.read_text()
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
