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


def parse_resolution(text, library_root, query_dir):
    """Exact observed 2.27.3 merged stdout/stderr contract, not a loose TS type.

    Historical CQL5 failed solely because qlPackName was an unknown key. Its []
    compilationCache did not cause that failure: the old adapter never checked it.
    This tightened contract accepts only the original no-default-cache invocation.
    """
    resolution = strict_json(text)
    if not isinstance(resolution, dict) or set(resolution) != {
            'libraryPath', 'dbscheme', 'relativeName', 'compilationCache', 'qlPackName'}:
        raise ValueError('Unknown library-path JSON schema')
    if resolution['qlPackName'] != 'sharplink/compilation-forensics':
        raise ValueError('Unexpected resolved forensic pack name')
    if resolution['relativeName'] != 'sharplink/compilation-forensics/CompilationEvidence.ql':
        raise ValueError('Unexpected resolved forensic query name')
    if type(resolution['compilationCache']) is not list or resolution['compilationCache']:
        raise ValueError('Unexpected resolved compilation cache')
    resolved = resolution['libraryPath']
    if (not isinstance(resolved, list) or not resolved or
            any(not isinstance(value, str) for value in resolved) or
            len(set(resolved)) != len(resolved) or resolved.count(str(query_dir)) != 1):
        raise ValueError('Invalid/duplicate resolved library path list or query root')
    for value in resolved:
        path = canonical(value)
        if path != query_dir and not path.is_relative_to(library_root):
            raise ValueError('Resolved library outside frozen bundle')
    expected_schema = library_root/'codeql/csharp-all/7.4.1/semmlecode.csharp.dbscheme'
    if canonical(resolution['dbscheme']) != expected_schema:
        raise ValueError('Resolved database schema outside fixed C# library')
    return resolution


def hosted_only():
    if os.environ.get('GITHUB_ACTIONS') != 'true' or os.environ.get('RUNNER_ENVIRONMENT') != 'github-hosted':
        raise ValueError('CodeQL forensics may execute only in authorized hosted CI')


def runtime_context(db, codeql, out, create=False):
    """Pure filesystem/identity checks. This function never starts a process."""
    temp = canonical(os.environ['RUNNER_TEMP']); db = canonical(str(db)); codeql = regular(codeql)
    if out.resolve() != out or not out.is_relative_to(temp):
        raise ValueError('Evidence output must be canonical RUNNER_TEMP directory')
    work = temp/'compilation-forensics-work'
    if work.is_relative_to(db) or db.is_relative_to(work) or work.is_relative_to(out) or out.is_relative_to(work):
        raise ValueError('Forensics work must remain separate from database and artifacts')
    query_dir = work/'query'; cache = work/'cache'
    if create:
        work.mkdir(); query_dir.mkdir(); cache.mkdir()
        for name in ('CompilationEvidence.ql', 'qlpack.yml'):
            shutil.copyfile(regular(ROOT/'compilation-forensics'/name), query_dir/name)
    for directory in (work, query_dir, cache):
        if canonical(str(directory)) != directory or not directory.is_dir():
            raise ValueError('Missing/corrupt preflight work directory')
    if {str(p.relative_to(query_dir)) for p in query_dir.rglob('*')} != {'CompilationEvidence.ql', 'qlpack.yml'}:
        raise ValueError('Unexpected forensic query-pack files or cached plan')
    for name in ('CompilationEvidence.ql', 'qlpack.yml'):
        if sha(regular(query_dir/name)) != sha(regular(ROOT/'compilation-forensics'/name)):
            raise ValueError('Forensic query/manifest differs from exact staged tool')
    toolchain = load(out/'toolchain.json')
    if (toolchain.get('codeqlPath') != str(codeql) or toolchain.get('codeqlBinarySha256') != sha(codeql)
            or toolchain.get('bundleSha256') != BUNDLE_SHA or toolchain.get('actualVersion', {}).get('version') != '2.27.3'):
        raise ValueError('Frozen CLI/bundle identity not established')
    library_root = canonical(str(codeql.parent/'qlpacks/codeql/csharp-queries/1.10.2/.codeql/libraries'))
    packs = sorted({p.parent for name in ('qlpack.yml', 'codeql-pack.yml') for p in library_root.rglob(name)})
    if not packs:
        raise ValueError('Frozen bundle library closure missing')
    manifests = {str(p): sha(regular(p)) for directory in packs for p in
                 (directory/'qlpack.yml', directory/'codeql-pack.yml') if p.is_file()}
    search = ':'.join(str(canonical(str(p))) for p in packs)
    flags = ['--search-path='+search, '--common-caches='+str(cache), '--no-default-compilation-cache']
    return {'codeqlPath': str(codeql), 'database': str(db/'csharp'), 'workDirectory': str(work),
            'queryPath': str(query_dir/'CompilationEvidence.ql'), 'libraryRoot': str(library_root),
            'flags': flags, 'searchManifestHashes': manifests, 'toolchainReceiptSha256': sha(out/'toolchain.json'),
            'querySourceSha256': sha(query_dir/'CompilationEvidence.ql'), 'queryPackSha256': sha(query_dir/'qlpack.yml')}


def command_plan(runtime, phase, out):
    query = runtime['queryPath']; flags = runtime['flags']; work = Path(runtime['workDirectory'])
    plans = {
        'resolve': (['resolve', 'library-path', '--query='+query, '--format=json', *flags], True),
        'schema': (['resolve', 'library-path', '--query='+query, '--format=dbscheme', *flags], True),
        # Official query compile --check-only validates QL, but does NOT optimize,
        # store a query plan, evaluate a database or establish extraction coverage.
        'compile': (['query', 'compile', '--check-only', '--threads=2', '--ram=2048', *flags, '--', query], True),
        'query': (['query', 'run', '--database='+runtime['database'], '--output='+str(work/'CompilationEvidence.bqrs'),
                   '--threads=2', '--ram=2048', *flags, '--', query], False),
        'decode': (['bqrs', 'decode', '--format=json', '--entities=id', '--output='+str(out/'compilation-rows.json'),
                    str(work/'CompilationEvidence.bqrs')], False),
    }
    arguments, capture = plans[phase]
    return [runtime['codeqlPath'], *arguments], capture


def execute_phase(runtime, phase, out, receipt, started, previously_elapsed=0):
    remaining = 300 - previously_elapsed - (time.monotonic() - started)
    if remaining <= 0:
        raise ValueError('Shared forensics active wall-clock budget exhausted')
    target = out/('process-compilation-'+phase+'.json')
    argv, capture = command_plan(runtime, phase, out)
    receipt['safeToContinue'] = False
    result = process_host.execute(argv, Path(runtime['workDirectory']), remaining, target, capture=capture)
    receipt['commands'].append({'phase': phase, 'receipt': target.name, 'sha256': sha(target)})
    if not process_host.safe(result):
        raise ValueError('Unsafe owned process lifecycle: '+phase)
    receipt['safeToContinue'] = True
    if result.get('exitCode') != 0:
        raise ValueError('Nonzero CodeQL command: '+phase)
    return result.get('capturedOutput', '').strip()


def verify_commands(receipt, phases, runtime, out):
    commands = receipt.get('commands', [])
    if [c.get('phase') for c in commands] != phases:
        raise ValueError('Missing/unexpected forensic command phase')
    for command in commands:
        name = 'process-compilation-'+command['phase']+'.json'
        if command.get('receipt') != name or command.get('sha256') != sha(regular(out/name)):
            raise ValueError('Forensic process receipt changed')
        process = load(out/name); argv, _ = command_plan(runtime, command['phase'], out)
        timeout = process.get('timeoutSeconds')
        if (process.get('argv') != argv or process.get('cwd') != runtime['workDirectory'] or
                type(timeout) not in (int, float) or not math.isfinite(timeout) or not 0 < timeout <= 300 or
                not process_host.safe(process) or process.get('exitCode') != 0):
            raise ValueError('Forensic process receipt invalid')


def current_library(runtime, out):
    process = load(out/'process-compilation-resolve.json')
    library_root = Path(runtime['libraryRoot']); query_dir = Path(runtime['queryPath']).parent
    resolution = parse_resolution(process['capturedOutput'], library_root, query_dir)
    snapshot = library_snapshot(library_root, resolution['libraryPath'], query_dir)
    schema = load(out/'process-compilation-schema.json')['capturedOutput'].strip()
    if canonical(schema) != canonical(resolution['dbscheme']):
        raise ValueError('Resolved schema commands disagree')
    snapshot.update(resolvedSchema=schema, schemaSha256=sha(regular(Path(schema))))
    return snapshot


def verify_preflight(runtime, out):
    receipt = load(out/'compilation-preflight.json')
    elapsed = receipt.get('elapsedActiveSeconds')
    if (receipt.get('status') != 'QUERY_CHECKED_NOT_EVALUATED' or receipt.get('safeToContinue') is not True or
            receipt.get('wallClockBudgetSeconds') != 300 or receipt.get('runtime') != runtime or
            type(elapsed) not in (int, float) or not math.isfinite(elapsed) or not 0 <= elapsed < 300 or
            receipt.get('elapsedWallSeconds') != elapsed):
        raise ValueError('Missing/invalid/changed successful forensic preflight')
    verify_commands(receipt, ['resolve', 'schema', 'compile'], runtime, out)
    if receipt.get('libraryReceiptSha256') != sha(regular(out/'compilation-library.json')):
        raise ValueError('Preflight library receipt changed')
    if current_library(runtime, out) != load(out/'compilation-library.json'):
        raise ValueError('Resolved library bytes changed since preflight')
    return receipt


def finish_phase(path, receipt, started, previously_elapsed=0):
    receipt['elapsedWallSeconds'] = time.monotonic() - started
    receipt['elapsedActiveSeconds'] = previously_elapsed + receipt['elapsedWallSeconds']
    # Includes Python identity checks and process cleanup, excludes the intervening
    # product restore/build and original 59-query analysis. Never reset after check.
    if receipt['elapsedActiveSeconds'] >= 300:
        receipt['status'] = 'INCOMPLETE'
        receipt['problems'].append('Shared forensics active wall-clock budget exhausted')
    write(path, receipt)
    if os.environ.get('GITHUB_OUTPUT'):
        with Path(os.environ['GITHUB_OUTPUT']).open('a') as stream:
            stream.write('safe='+str(receipt['safeToContinue']).lower()+'\n')
    return receipt


def preflight(db, codeql, out):
    """Hosted-only resolve/schema/check before product restore/build. No DB required."""
    path = out/'compilation-preflight.json'
    if path.exists():
        raise ValueError('No compilation preflight overwrite/retry')
    started = time.monotonic()
    receipt = {'status': 'INCOMPLETE', 'safeToContinue': True, 'wallClockBudgetSeconds': 300,
               'commands': [], 'problems': [], 'limitation': 'check-only validates QL; no optimization, query plan or database evaluation'}
    try:
        hosted_only()
        runtime = runtime_context(db, codeql, out, create=True); receipt['runtime'] = runtime
        text = execute_phase(runtime, 'resolve', out, receipt, started)
        resolution = parse_resolution(text, Path(runtime['libraryRoot']), Path(runtime['queryPath']).parent)
        # Bind all actual library source bytes before compiling anything.
        snapshot = library_snapshot(Path(runtime['libraryRoot']), resolution['libraryPath'], Path(runtime['queryPath']).parent)
        schema = execute_phase(runtime, 'schema', out, receipt, started)
        if canonical(schema) != canonical(resolution['dbscheme']):
            raise ValueError('Resolved schema commands disagree')
        snapshot.update(resolvedSchema=schema, schemaSha256=sha(regular(Path(schema))))
        write(out/'compilation-library.json', snapshot)
        receipt['libraryReceiptSha256'] = sha(out/'compilation-library.json')
        execute_phase(runtime, 'compile', out, receipt, started)
        if current_library(runtime, out) != snapshot or runtime_context(db, codeql, out) != runtime:
            raise ValueError('Forensic query/toolchain/library bytes changed during preflight')
        receipt['status'] = 'QUERY_CHECKED_NOT_EVALUATED'
    except Exception as error:
        receipt['problems'].append(type(error).__name__+': '+str(error))
    return finish_phase(path, receipt, started)


def run_query(db, codeql, out):
    """Consume exact successful preflight; only query/decode, no resolve retry.

    The common 300-second active budget starts in preflight and excludes product
    restore/build/analysis between the two phases. Raw BQRS stays outside artifact.
    """
    path = out/'compilation-query.json'
    if path.exists():
        raise ValueError('No compilation query overwrite/retry')
    started = time.monotonic(); previously_elapsed = 0
    receipt = {'status': 'INCOMPLETE', 'safeToContinue': True, 'wallClockBudgetSeconds': 300,
               'commands': [], 'problems': [], 'entityIdentity': 'opaque within this database only; not PID or cross-run identity'}
    try:
        hosted_only()
        runtime = runtime_context(db, codeql, out); receipt['runtime'] = runtime
        prior = verify_preflight(runtime, out); previously_elapsed = prior['elapsedActiveSeconds']
        receipt.update(preflightReceiptSha256=sha(out/'compilation-preflight.json'),
                       libraryReceiptSha256=prior['libraryReceiptSha256'], querySourceSha256=runtime['querySourceSha256'],
                       queryPackSha256=runtime['queryPackSha256'], database=runtime['database'],
                       workDirectory=runtime['workDirectory'])
        bqrs = Path(runtime['workDirectory'])/'CompilationEvidence.bqrs'
        if bqrs.exists() or (out/'compilation-rows.json').exists():
            raise ValueError('No BQRS/decoded output overwrite/retry')
        execute_phase(runtime, 'query', out, receipt, started, previously_elapsed)
        regular(bqrs)
        receipt['bqrs'] = {'path': str(bqrs), 'sha256': sha(bqrs), 'bytes': bqrs.stat().st_size, 'bytesExported': False}
        execute_phase(runtime, 'decode', out, receipt, started, previously_elapsed)
        decode_tables(load(out/'compilation-rows.json')); receipt['decodedSha256'] = sha(out/'compilation-rows.json')
        verify_preflight(runtime, out)
        if runtime_context(db, codeql, out) != runtime or sha(bqrs) != receipt['bqrs']['sha256']:
            raise ValueError('Forensic query/toolchain/BQRS bytes changed during evaluation')
        receipt['status'] = 'QUERY_ROWS_OBSERVED_NOT_COVERAGE_COMPLETION'
    except Exception as error:
        receipt['problems'].append(type(error).__name__+': '+str(error))
    return finish_phase(path, receipt, started, previously_elapsed)


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
        runtime = query['runtime']
        actual_runtime = runtime_context(Path(runtime['database']).parent, Path(runtime['codeqlPath']), out)
        if actual_runtime != runtime or query.get('preflightReceiptSha256') != sha(regular(out/'compilation-preflight.json')):
            raise ValueError('Query preflight/runtime identity changed')
        prior = verify_preflight(runtime, out)
        verify_commands(query, ['query', 'decode'], runtime, out)
        elapsed = query.get('elapsedActiveSeconds')
        wall = query.get('elapsedWallSeconds')
        if (type(elapsed) not in (int, float) or not math.isfinite(elapsed) or not 0 <= elapsed < 300 or
                type(wall) not in (int, float) or not math.isfinite(wall) or wall < 0 or
                elapsed != prior['elapsedActiveSeconds'] + wall or query.get('wallClockBudgetSeconds') != 300):
            raise ValueError('Shared forensics budget not established')
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
        result['evidenceHashes'] = {name: sha(regular(out/name)) for name in ('compilation-preflight.json', 'compilation-query.json', 'compilation-library.json', 'compilation-rows.json', 'manual-build.json', 'manual-archive.json')}
    except Exception as error:
        result['status'] = 'INCOMPLETE'; result['problems'].append(type(error).__name__+': '+str(error))
    write(out/'compilation-evidence.json', result)
    if result['status'] == 'INCOMPLETE':
        raise ValueError('Structured compilation evidence incomplete; see compilation-evidence.json')
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('mode', choices=['preflight', 'run-query', 'validate'])
    parser.add_argument('--source', type=Path)
    parser.add_argument('--db', type=Path)
    parser.add_argument('--codeql', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    if args.mode == 'preflight':
        result = preflight(args.db, args.codeql, args.output)
        if result['status'] != 'QUERY_CHECKED_NOT_EVALUATED':
            raise SystemExit(1)
    elif args.mode == 'run-query':
        result = run_query(args.db, args.codeql, args.output)
        if result['status'] != 'QUERY_ROWS_OBSERVED_NOT_COVERAGE_COMPLETION':
            raise SystemExit(1)
    else:
        validate(args.source, args.output)
