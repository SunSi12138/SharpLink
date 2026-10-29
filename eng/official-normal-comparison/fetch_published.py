#!/usr/bin/env python3
"""Fresh hosted runner: fetch frozen official NuGet bytes, or verify an existing local cache."""
import argparse
import hashlib
from pathlib import Path
import urllib.request
from common import CONTRACT, ROOT, digest, strict_json, write
from prepare import package_identity


def verify(path, name, entry):
    if digest(path) != entry['package_sha256']:
        raise ValueError(f'Official package SHA256 mismatch: {name}')
    dll, generator = package_identity(path, name.removeprefix('SharpLink.'), '2.0.3', CONTRACT['officialSource'])
    if hashlib.sha256(dll).hexdigest() != entry['dll_sha256']:
        raise ValueError(f'Official DLL SHA256 mismatch: {name}')
    if generator and hashlib.sha256(generator).hexdigest() != entry['generator_sha256']:
        raise ValueError('Official SDK Generator SHA256 mismatch')


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--output', type=Path, required=True)
    a = p.parse_args()
    out = a.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    provenance = strict_json((ROOT / 'published-cache/provenance.json').read_text())
    results = {}
    for name, entry in provenance['packages'].items():
        filename = f'{name.lower()}.2.0.3.nupkg'
        expected_url = f'https://api.nuget.org/v3-flatcontainer/{name.lower()}/2.0.3/{filename}'
        if entry['package_url'] != expected_url:
            raise ValueError('Unreviewed official source URL')
        destination = out / filename
        reused = destination.exists()
        if not reused:
            # Retain partial/error bytes under .download; never replace a cached mismatch.
            partial = out / (filename + '.download')
            with urllib.request.urlopen(expected_url, timeout=120) as response, partial.open('xb') as stream:
                if not response.geturl().startswith('https://'):
                    raise ValueError('Non-TLS package redirect')
                while chunk := response.read(1024 * 1024):
                    stream.write(chunk)
            verify(partial, name, entry)
            partial.rename(destination)
        verify(destination, name, entry)
        results[name] = dict(status='verified', reusedExisting=reused, sha256=digest(destination), url=expected_url)
        write(out / 'fetch-verification.json', results)
    print(f'OFFICIAL_CACHE_VERIFIED {out}')


if __name__ == '__main__':
    main()
