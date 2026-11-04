#!/usr/bin/env python3
"""Read-only frozen-source evidence check. No C# execution or semantic/performance proof."""
import hashlib
import json
from pathlib import Path
import subprocess
import sys

HERE=Path(__file__).resolve().parent

def check_files(root, manifest):
    for row in manifest['files']:
        relative=Path(row['path'])
        if relative.is_absolute() or '..' in relative.parts:
            raise ValueError('unsafe-path')
        path=root/relative
        raw=path.read_bytes()
        if hashlib.sha256(raw).hexdigest()!=row['sha256']:
            raise ValueError('source-bytes-changed')
        lines=raw.decode('utf-8-sig').splitlines()
        for anchor in row['anchors']:
            if anchor['line']<1 or anchor['line']>len(lines) or lines[anchor['line']-1].strip()!=anchor['text']:
                raise ValueError('source-anchor-changed')
    return len(manifest['files'])

def git(root,*args):
    return subprocess.check_output(['git','-c','core.hooksPath=/dev/null',*args],cwd=root,text=True,stderr=subprocess.PIPE,timeout=30).strip()

def main():
    if len(sys.argv)!=2:raise ValueError('arguments')
    root=Path(sys.argv[1]).resolve()
    manifest=json.loads((HERE/'source-snapshot.json').read_text())
    if git(root,'rev-parse','HEAD')!=manifest['sourceCommit'] or git(root,'rev-parse','HEAD:src')!=manifest['sourceTree'] or git(root,'status','--porcelain'):
        raise ValueError('source-identity')
    count=check_files(root,manifest)
    print(json.dumps(dict(schemaVersion=1,status='verified-static-evidence',sourceCommit=manifest['sourceCommit'],files=count,semanticsVerified=False,performanceVerified=False),sort_keys=True))

if __name__=='__main__':
    try:main()
    except Exception as error:
        print(json.dumps(dict(schemaVersion=1,status='failed',category=type(error).__name__),sort_keys=True))
        sys.exit(1)
