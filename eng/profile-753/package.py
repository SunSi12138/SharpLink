#!/usr/bin/env python3
"""Lossless bounded transfer parts; preserve the complete originals even on overflow."""
import argparse
import gzip
import hashlib
import json
from pathlib import Path
import tarfile

PART_BYTES = 28 * 1024 * 1024
MAX_PARTS = 32


def digest(path):
    result = hashlib.sha256()
    with path.open('rb') as stream:
        for data in iter(lambda: stream.read(1024 * 1024), b''):
            result.update(data)
    return result.hexdigest()


def package(source, destination):
    source, destination = source.resolve(), destination.resolve()
    if destination == source or source in destination.parents:
        raise ValueError('Parts must be outside the evidence directory')
    destination.mkdir(parents=True, exist_ok=False)
    files = sorted(p for p in source.rglob('*') if p.is_file())
    if any(p.is_symlink() for p in source.rglob('*')):
        raise ValueError('Evidence may not contain symlinks')
    manifest = {'format': 'Concatenate evidence.partNN in order, verify archive SHA256, gunzip and untar.',
                'partBytes': PART_BYTES, 'files': {str(p.relative_to(source)): {'bytes': p.stat().st_size,
                          'sha256': digest(p)} for p in files}}
    archive = destination / 'evidence.tar.gz'
    with archive.open('wb') as raw, gzip.GzipFile(filename='', mode='wb', fileobj=raw, mtime=0) as gz:
        with tarfile.open(fileobj=gz, mode='w|') as tar:
            for path in files:
                info = tar.gettarinfo(str(path), arcname=str(path.relative_to(source)))
                info.uid = info.gid = info.mtime = 0
                info.uname = info.gname = ''
                with path.open('rb') as stream:
                    tar.addfile(info, stream)
    manifest.update(archiveSha256=digest(archive), archiveBytes=archive.stat().st_size, parts=[])
    with archive.open('rb') as stream:
        while data := stream.read(PART_BYTES):
            part = destination / f'evidence.part{len(manifest["parts"]):02d}'
            part.write_bytes(data)
            manifest['parts'].append({'file': part.name, 'bytes': len(data), 'sha256': digest(part)})
    manifest['withinUploadPartLimit'] = len(manifest['parts']) <= MAX_PARTS
    (destination / 'index.json').write_text(json.dumps(manifest, indent=2) + '\n')
    if not manifest['withinUploadPartLimit']:
        raise ValueError('More than32 transfer parts; full original artifact must be retained. Do not retry profiling.')
    # Full originals are uploaded independently. Archive was only an intermediate.
    archive.unlink()
    return manifest


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('source', type=Path)
    parser.add_argument('destination', type=Path)
    args = parser.parse_args()
    package(args.source, args.destination)
