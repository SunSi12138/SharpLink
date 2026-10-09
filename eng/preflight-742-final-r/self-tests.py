#!/usr/bin/env python3
"""Untimed fixture checks for frozen source identities and original helper bytes."""
import hashlib
import json
import pathlib
import prepare

kit = pathlib.Path(__file__).resolve().parent
assert prepare.IDENTITIES == {'dev': '0fe26024b114bb6e78411a9b86276086c045d03d', 'candidate': 'df4383c7d00ee4a31c129ae42958e013f4ff5385'}
for name, digest in prepare.PATCHES.items():
    assert prepare.sha(kit / name) == digest, name
    headers = [line for line in (kit / name).read_text().splitlines() if line.startswith('diff --git ')]
    assert headers and all(line.split()[2].startswith(('a/src/', 'a/test/', 'a/eng/')) for line in headers)
for name, digest in json.loads((kit / 'dependencies.json').read_text()).items():
    assert prepare.sha(prepare.SOURCE / name) == digest, name
assert len(prepare.HARNESS) == 2
print('Frozen R reconstruction and original helper fixtures passed.')
