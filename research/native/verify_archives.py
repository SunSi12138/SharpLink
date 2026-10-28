#!/usr/bin/env python3
"""Offline SHA512 verification of already-downloaded official artifacts. Never downloads."""
import argparse, hashlib, json
from pathlib import Path

def verify(lock, rid, directory):
    rows = [r for r in lock['artifacts'] if r['rid'] == rid]
    if len(rows) != 3 or {r['component'] for r in rows} != {'sdk', 'runtime', 'aspnetcore-runtime'}:
        raise ValueError('Incomplete/duplicate artifact lock')
    result = []
    for row in rows:
        path = directory / row['name']
        with path.open('rb') as stream:
            digest = hashlib.file_digest(stream, 'sha512').hexdigest()
        if digest.lower() != row['hash'].lower():
            raise ValueError(f'SHA512 mismatch: {path}')
        result.append({'file':str(path.resolve()), 'sha512':digest, 'officialUrl':row['url']})
    return result

if __name__ == '__main__':
    parser=argparse.ArgumentParser();parser.add_argument('rid',choices=['linux-x64','osx-arm64']);parser.add_argument('directory',type=Path)
    args=parser.parse_args()
    print(json.dumps(verify(json.loads(Path(__file__).with_name('artifact-lock.json').read_text()),args.rid,args.directory),indent=2))
