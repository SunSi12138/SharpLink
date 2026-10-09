#!/usr/bin/env python3
"""Add the identical three measurement-boundary calls to exact diagnostic trees."""
import hashlib
import json
from pathlib import Path
import subprocess

PROGRAM = 'test/SharpLink.LoadTest/Program.cs'
MARKER = 'test/SharpLink.LoadTest/ProfileWindow753.cs'


def digest(data):
    return hashlib.sha256(data).hexdigest()


def once(text, before, after):
    if text.count(before) != 1:
        raise ValueError(f'Expected exactly one instrumentation boundary: {before!r}')
    return text.replace(before, after, 1)


def instrument(tree, marker):
    original = (tree / PROGRAM).read_bytes()
    text = original.decode()
    text = once(text, '        var options = LoadTestOptions.Parse(args);',
                '        SharpLink.Profiling753.ProfileWindow753.Initialize();\n'
                '        var options = LoadTestOptions.Parse(args);')
    text = once(text, '        var evidenceBefore = s_evidenceCollector!.Capture();',
                '        if (!isWarmup) SharpLink.Profiling753.ProfileWindow753.Begin("tcp-add-c1");\n'
                '        var evidenceBefore = s_evidenceCollector!.Capture();')
    before = '        var measurementStopped = lifecycle.StopStartingNewOperations();'
    text = once(text, before, before + '\n'
                '        if (!isWarmup) SharpLink.Profiling753.ProfileWindow753.Close("tcp-add-c1", measurementStarted, measurementStopped);')
    before = '            s_evidenceCollector.Capture());'
    text = once(text, before, before + '\n'
                '        if (!isWarmup) SharpLink.Profiling753.ProfileWindow753.End("tcp-add-c1", success + failure);')
    (tree / PROGRAM).write_text(text)
    with (tree / MARKER).open('xb') as target:
        target.write(marker.read_bytes())
    changed = subprocess.check_output(['git', 'diff', '--name-only'], cwd=tree, text=True).splitlines()
    untracked = subprocess.check_output(['git', 'ls-files', '--others', '--exclude-standard'], cwd=tree, text=True).splitlines()
    if changed != [PROGRAM] or untracked != [MARKER]:
        raise ValueError(f'Unexpected diagnostic edits: {changed}, {untracked}')
    subprocess.run(['git', 'diff', '--exit-code', '--', 'src'], cwd=tree, check=True)
    return {'originalProgramSha256': digest(original),
            'instrumentedProgramSha256': digest(text.encode()),
            'markerSha256': digest(marker.read_bytes())}
