"""Retained private Linux test temp with a preflight filesystem-UDS byte budget."""
import json
import os
from pathlib import Path
import stat
import sys
import tempfile

from .validate import require

SUN_PATH_BYTES = 108  # Linux sockaddr_un.sun_path, INCLUDING the terminating NUL.
GUID = '0' * 32
SOCKET_SUFFIXES = (
    'sl-perm-' + GUID + '.sock',
    'sl-perm-' + GUID + '/probe.sock',
    'sl-' + GUID + '/file.sock',
    'sl-' + GUID + '/directory.sock',
    'sl-' + GUID + '.sock',
    'sharplink-filesystem.sock',
)


def socket_budget(root: Path):
    values = {suffix: len(str(root / suffix).encode('utf-8')) + 1 for suffix in SOCKET_SUFFIXES}
    require(max(values.values()) <= SUN_PATH_BYTES, 'test TMPDIR exceeds existing UDS fixture byte budget')
    return values


def create_short_temp(output: Path):
    require(sys.platform == 'linux', 'this unit gate requires the reviewed Linux temp/socket contract')
    # Do not use the default gettempdir(): it can inherit the long diagnostic TMPDIR.
    # mkdtemp atomically chooses a unique 0700 directory. Never remove it on failure or
    # success; the association below keeps it discoverable next to the retained raw logs.
    root = Path(tempfile.mkdtemp(prefix='slu-', dir='/tmp'))
    association = {'path': str(root), 'retained': True, 'diagnostic_output': str(output),
                   'sun_path_capacity_including_nul': SUN_PATH_BYTES}
    link = output / 'short-temp.json'
    link.write_text(json.dumps(association, indent=2) + '\n')
    info = root.stat()
    require(not root.is_symlink() and info.st_uid == os.getuid() and stat.S_IMODE(info.st_mode) == 0o700,
            'test temp is not private and owned by the current process user')
    # Retain the harmless empty probe too; a permission check alone need not prove writes.
    with (root / '.gate-write-check').open('xb'):
        pass
    association.update(mode='0700', socket_bytes_including_nul=socket_budget(root))
    link.write_text(json.dumps(association, indent=2) + '\n')
    return root
