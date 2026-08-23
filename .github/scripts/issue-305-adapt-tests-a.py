from pathlib import Path

path = Path('test/SharpLink.UnitTests/Server/AdmissionControlTests.cs')
text = path.read_text()
old = '''            slotCount: 2,
            partition: null);'''
new = '''            slotCount: 2,
            partitionOwner: null,
            partitionEntry: null);'''
if text.count(old) != 1:
    raise SystemExit(f'expected exactly one direct AdmissionRequest partition argument, found {text.count(old)}')
path.write_text(text.replace(old, new, 1))
