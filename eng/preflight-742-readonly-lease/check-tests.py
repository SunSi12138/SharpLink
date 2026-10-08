#!/usr/bin/env python3
"""Require complete known unit populations, including the exact new test population."""
import pathlib
import re
import sys

path, expected = pathlib.Path(sys.argv[1]), int(sys.argv[2])
text = re.sub(r'\x1b\[[0-9;]*m', '', path.read_text())
for key, count in (('total', expected), ('succeeded', expected), ('failed', 0), ('skipped', 0)):
    assert re.search(rf'{key}:\s+{count}\b', text), (path, key, count)
print(f'{path.name}: {expected}/{expected} tests succeeded, none skipped')
