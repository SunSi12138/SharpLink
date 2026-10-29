#!/usr/bin/env python3
"""Exact source-reviewed Explicit process-worker exclusions, never known failures."""
from pathlib import Path

from .validate import discovery_uids, require, strict_json, unique_uids


def explicit_workers(tool_dir: Path) -> list[dict]:
    workers = strict_json((tool_dir / 'explicit-workers.json').read_text(encoding='utf-8'))
    require(isinstance(workers, list) and workers, 'missing explicit-worker scope')
    keys = {'uid', 'namespace', 'type_name', 'method_name', 'source_path', 'reason'}
    require(all(isinstance(w, dict) and set(w) == keys
                and all(isinstance(v, str) and v for v in w.values()) for w in workers),
            'invalid explicit-worker manifest')
    unique_uids([w['uid'] for w in workers], 'explicit workers')
    return workers


def ordinary_uids(raw: list[str], workers: list[dict]) -> list[str]:
    explicit = {w['uid'] for w in workers}
    require(explicit.issubset(set(raw)), 'explicit-worker UID absent from fresh discovery')
    return unique_uids(sorted(set(raw) - explicit), 'ordinary unit corpus')


def verify_explicit_sources(root: Path, discovery: dict, workers: list[dict]) -> None:
    # This is an exact, reviewed source manifest, not a fuzzy name exclusion. A
    # new Explicit source or a changed marker fails closed and needs fresh review.
    manifest_paths = {w['source_path'] for w in workers}
    marked_paths = {p.relative_to(root).as_posix()
                    for p in (root / 'test/SharpLink.UnitTests').rglob('*.cs')
                    if '[Explicit]' in p.read_text(encoding='utf-8-sig')}
    require(marked_paths == manifest_paths, 'Explicit source inventory differs from reviewed manifest')
    discovery_uids(discovery)
    tests = {t['uid']: t for t in discovery['tests']}
    for worker in workers:
        source = root / worker['source_path']
        require(source.resolve().is_relative_to(root.resolve()), 'explicit source outside checkout')
        marker = '[Explicit]\npublic sealed class ' + worker['type_name'] + '\n'
        require(marker in source.read_text(encoding='utf-8-sig').replace('\r\n', '\n'),
                'explicit source class marker changed')
        test = tests.get(worker['uid'], {})
        identity = test.get('type', {})
        require(identity.get('namespace') == worker['namespace']
                and identity.get('typeName') == worker['type_name']
                and identity.get('methodName') == worker['method_name'], 'explicit discovery identity mismatch')
        location = test.get('location', {}).get('file', '').replace('\\', '/')
        require(location.endswith('/' + worker['source_path']), 'explicit discovery source mapping mismatch')
