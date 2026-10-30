#!/usr/bin/env python3
"""Separate, fail-closed CodeQL Compilation evidence. No product execution.

run_query is HOSTED ONLY and uses a fresh RUNNER_TEMP work directory. validate is
pure Python and runs after native archive/package evidence collection. The original
59 queries, SARIF and their result directory are never modified by this module.
"""
import argparse
import hashlib
import json
import math
import os
import re
import shutil
import time
from pathlib import Path

import process_host
from verify_request import strict_json

ROOT = Path(__file__).resolve().parent
# Identity is the already reviewed official bundle, not a cache/registry package.
BUNDLE_SHA = '64518dc413c8e7b5733fee2489e830a899bb1e3f28c8873b5869d5cc90164c5c'
OFFICIAL_COMMIT = 'a1575b98a8cc19bcfa7bb6cb41be688cb3dae65c'
OFFICIAL_FILES = {
    'semmle/code/csharp/commons/Compilation.qll': '0daedd8bc18add4f3a76175ec51c8a03362372f17481ea723b1be5e75006b1cd',
    'semmle/code/csharp/commons/Diagnostics.qll': '64d5b7c723229603ac7674bade4e9110da9f5b691f96876febf9cd137a28359a',
    'semmle/code/csharp/Location.qll': 'b266cb72892922b4cbd9e7b67b8120ee642886e8334c6fc5d945f4c5c0d70d72',
    'semmlecode.csharp.dbscheme': '82344014be27beb59d03e17e3d948476b84f4116284c985bcfe4c51647832ae6',
}
SCHEMA = {
    'contexts': [('context', 'Entity')],
    'directories': [('context', 'Entity'), ('path', 'String')],
    'outputs': [('context', 'Entity'), ('path', 'String')],
    'inputs': [('context', 'Entity'), ('index', 'Integer'), ('path', 'String')],
    'references': [('context', 'Entity'), ('index', 'Integer'), ('path', 'String')],
    'finished': [('context', 'Entity'), ('cpuSeconds', 'Float'), ('elapsedSeconds', 'Float')],
    'diagnostics': [('diagnostic', 'Entity'), ('severity', 'Integer'), ('tag', 'String')],
    'diagnosticContexts': [('diagnostic', 'Entity'), ('context', 'Entity')],
    'diagnosticLocations': [('diagnostic', 'Entity'), ('path', 'String'), ('startLine', 'Integer'), ('startColumn', 'Integer'), ('endLine', 'Integer'), ('endColumn', 'Integer')],
    'extractorMessages': [('message', 'Entity'), ('severity', 'Integer'), ('origin', 'String')],
    'extractorLocations': [('message', 'Entity'), ('path', 'String'), ('startLine', 'Integer'), ('startColumn', 'Integer'), ('endLine', 'Integer'), ('endColumn', 'Integer')],
}


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def write(path, value):
    with path.open('x') as stream:
        json.dump(value, stream, indent=2, allow_nan=False)
        stream.write('\n')


def canonical(value):
    if not isinstance(value, str) or not value or any(ord(c) < 32 for c in value):
        raise ValueError('Invalid path text')
    path = Path(value)
    if not path.is_absolute() or '..' in path.parts or str(path) != value or path.resolve() != path:
        raise ValueError('Noncanonical or symlinked path: ' + value)
    return path


def regular(path):
    canonical(str(path))
    if not path.is_file() or path.is_symlink():
        raise ValueError('Missing regular file: ' + str(path))
    return path


def load(path):
    if regular(path).stat().st_size > 128 * 1024 * 1024:
        raise ValueError('Oversized structured evidence')
    return strict_json(path.read_text())


def decode_tables(doc):
    """Validate the entire nonpaginated bqrs decode --format=json --entities=id schema."""
    if not isinstance(doc, dict) or set(doc) != set(SCHEMA):
        raise ValueError('Missing/unexpected BQRS result sets')
    tables = {}
    for name, columns in SCHEMA.items():
        table = doc[name]
        if not isinstance(table, dict) or set(table) != {'columns', 'tuples'}:
            raise ValueError('Malformed/paginated BQRS table: ' + name)
        if table['columns'] != [{'name': n, 'kind': k} for n, k in columns]:
            raise ValueError('BQRS column schema changed: ' + name)
        if not isinstance(table['tuples'], list):
            raise ValueError('Malformed BQRS tuples')
        rows = []; seen = set()
        for row in table['tuples']:
            if not isinstance(row, list) or len(row) != len(columns):
                raise ValueError('Malformed BQRS tuple: ' + name)
            normalized = []
            for value, (_, kind) in zip(row, columns):
                if kind == 'Entity':
                    if not isinstance(value, dict) or set(value) != {'id'} or type(value['id']) is not int or value['id'] < 0:
                        raise ValueError('Missing/invalid database-local opaque entity ID')
                    value = str(value['id'])
                elif kind == 'Integer':
                    if type(value) is not int:
                        raise ValueError('Noninteger BQRS value')
                elif kind == 'Float':
                    if type(value) not in (int, float) or not math.isfinite(value) or value < 0:
                        raise ValueError('Invalid finished timing')
                elif not isinstance(value, str):
                    raise ValueError('Nonstring BQRS value')
                normalized.append(value)
            key = tuple(normalized)
            if key in seen:
                raise ValueError('Duplicate BQRS tuple: ' + name)
            seen.add(key); rows.append(normalized)
        tables[name] = rows
    return tables


def pack_manifest(directory):
    candidates = [p for p in (directory/'qlpack.yml', directory/'codeql-pack.yml') if p.is_file()]
    if len(candidates) != 1:
        raise ValueError('Missing/ambiguous installed pack manifest: ' + str(directory))
    path = regular(candidates[0]); content = path.read_text()
    fields = {}
    for field in ('name', 'version'):
        values = re.findall(r'^' + field + r':\s*([^\n]+)$', content, re.M)
        if len(values) != 1:
            raise ValueError('Invalid installed pack manifest field')
        fields[field] = values[0].strip().strip('\"\'')
    return {'path': str(path), 'sha256': sha(path), 'content': content, **fields}


def library_snapshot(library_root, resolved, query_dir):
    """Bind EVERY resolved library file; reject any fallback path outside the bundle."""
    if not resolved or len(set(resolved)) != len(resolved):
        raise ValueError('Empty/duplicate resolved library path')
    libraries = []; names = set()
    for value in resolved:
        path = canonical(value)
        if path == query_dir:
            continue
        if not path.is_relative_to(library_root) or not path.is_dir():
            raise ValueError('Resolved library outside frozen bundle: ' + value)
        manifest = pack_manifest(path)
        if manifest['name'] in names:
            raise ValueError('Duplicate resolved pack name')
        names.add(manifest['name']); files = {}
        for file in sorted(path.rglob('*')):
            if file.is_symlink():
                raise ValueError('Symlink in resolved library')
            if file.is_file():
                files[str(file.relative_to(path))] = sha(regular(file))
        libraries.append({'directory': value, 'manifest': manifest, 'fileHashes': files})
    by_name = {p['manifest']['name']: p for p in libraries}
    for name, version in [('codeql/csharp-all', '7.4.1'), ('codeql/threat-models', '1.1.0')]:
        if name not in by_name or by_name[name]['manifest']['version'] != version:
            raise ValueError('Frozen library name/version missing')
    csharp = by_name['codeql/csharp-all']
    if csharp['directory'] != str(library_root/'codeql/csharp-all/7.4.1'):
        raise ValueError('C# library is not exact installed bundle path')
    for relative, digest in OFFICIAL_FILES.items():
        if csharp['fileHashes'].get(relative) != digest:
            raise ValueError('Official fixed source/schema hash mismatch: ' + relative)
    return {'officialSourceCommit': OFFICIAL_COMMIT, 'libraryRoot': str(library_root),
            'resolvedSearchPaths': resolved, 'libraries': libraries}


def run_query(db, codeql, out):
    """Hosted-only independent query, 300 s shared owned-command wall-clock budget.

    out already contains toolchain.json. db is the action database CLUSTER, so the
    finalized C# database is db/csharp. No pack installation/network/retry occurs.
    All safe receipts survive failure; unsafe lifecycle outputs safe=false so later
    workload phases must be skipped. Raw BQRS/cache stay outside database/artifact.
    """
    receipt = {'status': 'INCOMPLETE', 'safeToContinue': True, 'wallClockBudgetSeconds': 300,
               'commands': [], 'problems': [], 'querySourceSha256': None,
               'entityIdentity': 'opaque within this database only; not PID or cross-run identity'}
    path = out/'compilation-query.json'
    if path.exists():
        raise ValueError('No compilation query overwrite/retry')
    started = time.monotonic()
    try:
        if os.environ.get('GITHUB_ACTIONS') != 'true' or os.environ.get('RUNNER_ENVIRONMENT') != 'github-hosted':
            raise ValueError('CodeQL forensics may execute only in authorized hosted CI')
        temp = canonical(os.environ['RUNNER_TEMP']); db = canonical(str(db)); codeql = regular(codeql)
        if out.resolve() != out or not out.is_relative_to(temp):
            raise ValueError('Evidence output must be canonical RUNNER_TEMP directory')
        work = temp/'compilation-forensics-work'
        if work.is_relative_to(db) or db.is_relative_to(work) or work.is_relative_to(out):
            raise ValueError('Forensics work must remain separate from database and artifacts')
        work.mkdir(); query_dir = work/'query'; query_dir.mkdir()
        cache = work/'cache'; cache.mkdir()
        for name in ('CompilationEvidence.ql', 'qlpack.yml'):
            shutil.copyfile(regular(ROOT/'compilation-forensics'/name), query_dir/name)
        query = query_dir/'CompilationEvidence.ql'
        receipt.update(querySourceSha256=sha(query), queryPackSha256=sha(query_dir/'qlpack.yml'),
                       workDirectory=str(work), database=str(db/'csharp'))
        toolchain = load(out/'toolchain.json')
        if (toolchain.get('codeqlPath') != str(codeql) or toolchain.get('codeqlBinarySha256') != sha(codeql)
                or toolchain.get('bundleSha256') != BUNDLE_SHA or toolchain.get('actualVersion', {}).get('version') != '2.27.3'):
            raise ValueError('Frozen CLI/bundle identity not established')
        library_root = canonical(str(codeql.parent/'qlpacks/codeql/csharp-queries/1.10.2/.codeql/libraries'))
        packs = sorted({p.parent for name in ('qlpack.yml', 'codeql-pack.yml') for p in library_root.rglob(name)})
        if not packs:
            raise ValueError('Frozen bundle library closure missing')
        search = ':'.join(str(canonical(str(p))) for p in packs)
        flags = ['--search-path='+search, '--common-caches='+str(cache), '--no-default-compilation-cache']

        def command(phase, argv, capture=False):
            remaining = 300 - (time.monotonic() - started)
            if remaining <= 0:
                raise ValueError('Shared forensics wall-clock budget exhausted')
            target = out/('process-compilation-'+phase+'.json')
            receipt['safeToContinue'] = False
            r = process_host.execute([str(codeql), *argv], work, remaining, target, capture=capture)
            receipt['commands'].append({'phase': phase, 'receipt': target.name, 'sha256': sha(target)})
            if not process_host.safe(r):
                receipt['safeToContinue'] = False
                raise ValueError('Unsafe owned process lifecycle: ' + phase)
            receipt['safeToContinue'] = True
            if r.get('exitCode') != 0:
                raise ValueError('Nonzero CodeQL command: ' + phase)
            return r.get('capturedOutput', '').strip()

        # Official VSCode QuerySetup contract (84f482fcb79cd4d7b08c20ed3a7b4942f4474a83):
        # libraryPath:string[], dbscheme:string; avoid guessing a text path separator.
        resolved_text = command('resolve', ['resolve', 'library-path', '--query='+str(query), '--format=json', *flags], True)
        resolution = strict_json(resolved_text)
        if not isinstance(resolution, dict) or not {'libraryPath', 'dbscheme'} <= set(resolution) or set(resolution) - {'libraryPath', 'dbscheme', 'relativeName', 'compilationCache'}:
            raise ValueError('Unknown library-path JSON schema')
        resolved = resolution['libraryPath']
        if not isinstance(resolved, list) or any(not isinstance(value, str) for value in resolved):
            raise ValueError('Invalid resolved library path list')
        snapshot = library_snapshot(library_root, resolved, query_dir)
        schema = command('schema', ['resolve', 'library-path', '--query='+str(query), '--format=dbscheme', *flags], True)
        expected_schema = library_root/'codeql/csharp-all/7.4.1/semmlecode.csharp.dbscheme'
        if canonical(schema) != expected_schema or canonical(resolution['dbscheme']) != expected_schema:
            raise ValueError('Resolved database schema outside fixed C# library')
        snapshot.update(resolvedSchema=schema, schemaSha256=sha(regular(expected_schema)))
        write(out/'compilation-library.json', snapshot)
        receipt['libraryReceiptSha256'] = sha(out/'compilation-library.json')
        bqrs = work/'CompilationEvidence.bqrs'
        command('query', ['query', 'run', '--database='+str(db/'csharp'), '--output='+str(bqrs),
                          '--threads=2', '--ram=2048', *flags, '--', str(query)])
        regular(bqrs)
        receipt['bqrs'] = {'path': str(bqrs), 'sha256': sha(bqrs), 'bytes': bqrs.stat().st_size, 'bytesExported': False}
        decoded = out/'compilation-rows.json'
        command('decode', ['bqrs', 'decode', '--format=json', '--entities=id', '--output='+str(decoded), str(bqrs)])
        decode_tables(load(decoded))
        receipt['decodedSha256'] = sha(decoded)
        if library_snapshot(library_root, resolved, query_dir) != {k: v for k, v in snapshot.items() if k not in ('resolvedSchema', 'schemaSha256')}:
            raise ValueError('Resolved library bytes changed during query')
        if sha(query) != receipt['querySourceSha256'] or sha(query_dir/'qlpack.yml') != receipt['queryPackSha256']:
            raise ValueError('Forensic query/manifest changed during execution')
        receipt['status'] = 'QUERY_ROWS_OBSERVED_NOT_COVERAGE_COMPLETION'
    except Exception as error:
        receipt['problems'].append(type(error).__name__ + ': ' + str(error))
    finally:
        receipt['elapsedWallSeconds'] = time.monotonic() - started
        write(path, receipt)
        if os.environ.get('GITHUB_OUTPUT'):
            with Path(os.environ['GITHUB_OUTPUT']).open('a') as stream:
                stream.write('safe='+str(receipt['safeToContinue']).lower()+'\n')
    return receipt


def reconcile(source, tables, build, archive):
    """Pure-Python binding of ALL rows; preserve missing/extra contexts as evidence."""
    problems = []; contexts = {}; diagnostics = {}; messages = {}
    result = {'status': 'INCOMPLETE', 'contexts': [], 'projectMappings': [], 'diagnostics': [],
              'extractorMessages': [], 'problems': problems, 'contextCountEqualsProjectCountRequired': False,
              'finishedMeansZeroErrors': False, 'perFileSemanticCompleteness': 'NOT_PROVEN',
              'opaqueEntityIdsArePids': False, 'securityDecision': 'NOT_ASSESSED'}
    for [entity] in tables['contexts']:
        if entity in contexts:
            problems.append('Duplicate context ID: '+entity)
        contexts[entity] = {'id': entity, 'directories': [], 'outputs': [], 'inputs': [], 'references': [],
                            'finished': [], 'diagnosticIds': [], 'project': None, 'problems': []}
    result['contexts'] = list(contexts.values())
    if not contexts:
        problems.append('No Compilation entities')
    for name in ('directories', 'outputs', 'inputs', 'references', 'finished'):
        for entity, *values in tables[name]:
            if entity not in contexts:
                problems.append('Orphan '+name+' relationship: '+entity)
            else:
                contexts[entity][name].append(values)
    for entity, severity, tag in tables['diagnostics']:
        if entity in diagnostics:
            problems.append('Duplicate compiler diagnostic ID: '+entity)
        diagnostics[entity] = {'id': entity, 'severity': severity, 'tag': tag, 'contextIds': [], 'locations': []}
        if severity not in range(4) or severity >= 3:
            problems.append('Compiler error/unknown severity: '+entity)
    for diagnostic, context in tables['diagnosticContexts']:
        if diagnostic not in diagnostics or context not in contexts:
            problems.append('Orphan compiler diagnostic relationship')
        else:
            diagnostics[diagnostic]['contextIds'].append(context); contexts[context]['diagnosticIds'].append(diagnostic)
    for entity, severity, origin in tables['extractorMessages']:
        if entity in messages:
            problems.append('Duplicate extractor message ID: '+entity)
        messages[entity] = {'id': entity, 'severity': severity, 'origin': origin, 'locations': []}
        if severity not in range(1, 6) or severity >= 5:
            problems.append('Global extractor error/unknown severity: '+entity)
    for name, target in [('diagnosticLocations', diagnostics), ('extractorLocations', messages)]:
        for entity, *location in tables[name]:
            if entity not in target:
                problems.append('Orphan diagnostic location')
            else:
                target[entity]['locations'].append(location)
    result['diagnostics'] = list(diagnostics.values()); result['extractorMessages'] = list(messages.values())
    for diagnostic in diagnostics.values():
        if not diagnostic['contextIds']:
            problems.append('Missing compiler diagnostic context: '+diagnostic['id'])
    projects = [p for p in build.get('projects', []) if p.get('scope') != 'unreached']
    if not projects or len({p['project'] for p in projects}) != len(projects):
        problems.append('Missing/duplicate expected project inventory')
    expected = []
    for project in projects:
        mapping = {'project': project['project'], 'contextIds': [], 'output': project.get('dll'), 'problems': []}
        result['projectMappings'].append(mapping)
        try:
            directory = canonical(str(source/Path(project['project']).parent))
            dll = canonical(str(source/project['dll']['path']))
            configuration = project['observedProjectConfiguration']; tfm = project['targetFramework']; assembly = project['assemblyName']
            if dll != directory/'bin'/configuration/tfm/(assembly+'.dll'):
                raise ValueError('Expected DLL is not exact project/configuration/TFM path')
            if sha(regular(dll)) != project['dll']['sha256']:
                raise ValueError('Actual bin DLL differs from build snapshot')
            obj = directory/'obj'/configuration/tfm/(assembly+'.dll')
            expected.append((project, mapping, directory, dll, obj))
        except Exception as error:
            mapping['problems'].append(str(error)); problems.append(project['project']+': '+str(error))
    for context in contexts.values():
        errors = context['problems']; entity = context['id']
        try:
            if len(context['directories']) != 1 or len(context['outputs']) != 1 or len(context['finished']) != 1:
                raise ValueError('Missing/ambiguous cwd, output or finished relation')
            cwd = canonical(context['directories'][0][0]); output = canonical(context['outputs'][0][0])
            candidates = [x for x in expected if cwd == x[2] and output in (x[3], x[4])]
            if len(candidates) != 1:
                raise ValueError('Unmapped/ambiguous full-path output and exact project cwd')
            project, mapping, directory, dll, obj = candidates[0]
            digest = sha(regular(output))
            context['outputBinding'] = {'actualCompilerOutputPath': str(output), 'actualCompilerOutputSha256': digest,
                'binPath': str(dll), 'actualBinSha256': sha(regular(dll)), 'buildBinSha256': project['dll']['sha256'],
                'relation': 'same-full-path' if output == dll else 'actual-obj-and-bin-byte-sha256-equality'}
            if digest != project['dll']['sha256']:
                raise ValueError('Actual compiler obj/bin output bytes differ')
            context['project'] = project['project']; mapping['contextIds'].append(entity)
        except Exception as error:
            errors.append(str(error))
        if not context['inputs']:
            errors.append('Missing actual input file relations')
        if not context['references']:
            errors.append('Missing actual assembly reference relations')
        for kind in ('inputs', 'references'):
            rows = context[kind]; seen = set(); observed = []
            for index, value in rows:
                entry = {'index': index, 'path': value}; observed.append(entry)
                try:
                    if index < 0 or index in seen:
                        raise ValueError('Invalid/duplicate '+kind+' index')
                    seen.add(index); path = canonical(value)
                    if kind == 'inputs':
                        if path.is_relative_to(source):
                            key = str(path.relative_to(source))
                        else:
                            key = archive.get('externalSourcePaths', {}).get(value)
                        row = archive.get('csharpFiles', {}).get(key)
                        if not row or row.get('kind') in ('unknown-or-invalid', 'copied-tracked-content'):
                            raise ValueError('Actual compiler input lacks verified native archive provenance')
                        digest = sha(regular(path))
                        if digest != row.get('workspaceSha256'):
                            raise ValueError('Actual compiler input changed from archived workspace bytes')
                        entry.update(archiveKey=key, sha256=digest, provenanceKind=row['kind'])
                        if row['kind'] == 'external-package-source':
                            entry['packageIdentity'] = {k: row.get(k) for k in ('packageId', 'packageVersion', 'member', 'memberSha256')}
                    else:
                        entry.update(sha256=sha(regular(path)), provenance='actual-reference-path-and-bytes; package-origin-not-asserted')
                except Exception as error:
                    entry['problem'] = str(error); errors.append(kind+': '+str(error))
            if seen != set(range(len(rows))):
                errors.append('Noncontiguous '+kind+' indexes')
            context[kind+'Evidence'] = observed
        context['classification'] = 'mapped-project-context' if context['project'] else 'unmapped-extra-context'
        problems.extend('Context '+entity+': '+message for message in errors)
    for mapping in result['projectMappings']:
        if not mapping['contextIds']:
            problems.append('No structural context for expected project output: '+mapping['project'])
        elif len(mapping['contextIds']) > 1:
            for entity in mapping['contextIds'][1:]:
                contexts[entity]['classification'] = 'additional-fully-listed-context-for-same-project'
    known_paths = {row[-1] for name in ('inputs', 'references', 'outputs') for row in tables[name]}
    for diagnostic in list(diagnostics.values()) + list(messages.values()):
        for location in diagnostic['locations']:
            path, *coordinates = location
            try:
                if any(c < 0 for c in coordinates):
                    raise ValueError('Invalid diagnostic coordinates')
                if path:
                    canonical(path)
                    if path not in known_paths:
                        raise ValueError('Diagnostic location not bound to any observed input/reference/output')
                elif coordinates != [0, 0, 0, 0]:
                    raise ValueError('Nonempty diagnostic coordinates without a path')
            except Exception as error:
                problems.append('Diagnostic '+diagnostic['id']+': '+str(error))
    result['extraContextIds'] = [c['id'] for c in contexts.values() if c['classification'] != 'mapped-project-context']
    result['expectedProjectCount'] = len(projects); result['actualContextCount'] = len(contexts)
    if not problems:
        result['status'] = 'STRUCTURAL_COMPILATION_COVERAGE_OBSERVED_NOT_SEMANTIC_COMPLETENESS'
    return result


def validate(source, out):
    """Final collection entrypoint; always preserve an incomplete receipt on failure."""
    result = {'status': 'INCOMPLETE', 'problems': []}
    try:
        source = canonical(str(source))
        query = load(out/'compilation-query.json')
        if query.get('status') != 'QUERY_ROWS_OBSERVED_NOT_COVERAGE_COMPLETION' or query.get('safeToContinue') is not True:
            raise ValueError('Forensic query did not safely complete')
        if query.get('decodedSha256') != sha(regular(out/'compilation-rows.json')):
            raise ValueError('Decoded query rows changed')
        for command in query['commands']:
            receipt = load(out/command['receipt'])
            if sha(out/command['receipt']) != command['sha256'] or not process_host.safe(receipt) or receipt.get('exitCode') != 0:
                raise ValueError('Forensic process receipt invalid')
        if [c['phase'] for c in query['commands']] != ['resolve', 'schema', 'query', 'decode']:
            raise ValueError('Missing forensic command phase')
        if query.get('libraryReceiptSha256') != sha(regular(out/'compilation-library.json')):
            raise ValueError('Resolved library receipt changed')
        build = load(out/'manual-build.json'); archive = load(out/'manual-archive.json')
        import manual
        expected_projects = {p['path']: p['scope'] for p in manual.LEDGER['projects'] if p['scope'] != 'unreached'}
        actual_projects = [p for p in build.get('projects', []) if p.get('scope') != 'unreached']
        if len(expected_projects) != 67 or len(actual_projects) != 67 or {p['project']: p['scope'] for p in actual_projects} != expected_projects:
            raise ValueError('Build project inventory differs from frozen 64-direct/3-transitive closure')
        if query.get('querySourceSha256') != sha(regular(ROOT/'compilation-forensics/CompilationEvidence.ql')) or query.get('queryPackSha256') != sha(regular(ROOT/'compilation-forensics/qlpack.yml')):
            raise ValueError('Query source/manifest differs from exact staged tool')
        tables = decode_tables(load(out/'compilation-rows.json'))
        result = reconcile(source, tables, build, archive)
        if build.get('evidenceStatus') != 'BUILD_OUTPUTS_OBSERVED_NOT_SCAN_COMPLETION' or build.get('problems') or archive.get('problems') or archive.get('inventoryStatus') != 'INPUT_INVENTORY_OBSERVED_NOT_SEMANTIC_COMPLETION':
            result['problems'].append('Build/native source inventory incomplete')
        if result['problems']:
            result['status'] = 'INCOMPLETE'
        result['evidenceHashes'] = {name: sha(regular(out/name)) for name in ('compilation-query.json', 'compilation-library.json', 'compilation-rows.json', 'manual-build.json', 'manual-archive.json')}
    except Exception as error:
        result['status'] = 'INCOMPLETE'; result['problems'].append(type(error).__name__+': '+str(error))
    write(out/'compilation-evidence.json', result)
    if result['status'] == 'INCOMPLETE':
        raise ValueError('Structured compilation evidence incomplete; see compilation-evidence.json')
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('mode', choices=['run-query', 'validate'])
    parser.add_argument('--source', type=Path)
    parser.add_argument('--db', type=Path)
    parser.add_argument('--codeql', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    if args.mode == 'run-query':
        result = run_query(args.db, args.codeql, args.output)
        if result['status'] != 'QUERY_ROWS_OBSERVED_NOT_COVERAGE_COMPLETION':
            raise SystemExit(1)
    else:
        validate(args.source, args.output)
