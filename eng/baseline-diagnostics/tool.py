"""Pinned official collector package only; no installer scripts or target execution."""
import argparse
import hashlib
import io
from pathlib import Path
import xml.etree.ElementTree as ET
import zipfile
from common import ROOT, strict_json, inventory, write

PIN = strict_json((ROOT / 'tool.json').read_text())

def payload(package):
    data = package.read_bytes()
    if hashlib.sha512(data).hexdigest() != PIN['sha512']:
        raise ValueError('TOOL_PACKAGE_HASH')
    result = {}
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        ns = {'n': 'http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd'}
        meta = ET.fromstring(archive.read('dotnet-trace.nuspec')).find('n:metadata', ns)
        if (meta.find('n:id', ns).text, meta.find('n:version', ns).text,
            meta.find('n:repository', ns).attrib.get('commit')) != (PIN['id'], PIN['version'], PIN['source']):
            raise ValueError('TOOL_PACKAGE_SOURCE')
        for entry in archive.infolist():
            if not entry.filename.startswith(PIN['payloadPrefix']) or entry.is_dir():
                continue
            name = entry.filename[len(PIN['payloadPrefix']):]
            if (not name or name in result or name.startswith('/') or '\\' in name or
                any(p in ('', '.', '..') for p in name.split('/'))):
                raise ValueError('TOOL_PACKAGE_PATH')
            result[name] = archive.read(entry)
    if len(result) != PIN['payloadFiles'] or 'dotnet-trace.dll' not in result:
        raise ValueError('TOOL_PACKAGE_CLOSURE')
    return result

def verify(package, root):
    expected = {p: hashlib.sha256(v).hexdigest() for p, v in payload(package).items()}
    if root.is_symlink() or any(p.is_symlink() for p in root.rglob('*')) or inventory(root) != expected:
        raise ValueError('TOOL_EXTRACTED_HASH')
    return expected

def main():
    p = argparse.ArgumentParser(); p.add_argument('--package', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True); a = p.parse_args()
    values = payload(a.package)
    a.output.mkdir(parents=True, exist_ok=False)
    for name, value in values.items():
        path = a.output / name; path.parent.mkdir(parents=True, exist_ok=True); path.write_bytes(value)
    verify(a.package, a.output)
    print('COLLECTOR_PACKAGE_PASS version=10.0.750501')

if __name__ == '__main__': main()
