"""Fail-closed provenance shared by the limited matrix and aggregate gates."""
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import subprocess

SCRIPTS = (
    'eng/run-performance-matrix.sh',
    'eng/run-latency-recorder-baseline.sh',
    'eng/run-release-soak.sh',
    'eng/run-generated-abi-dynamic-module-soak.sh',
)
COMMON_BINARIES = {'SharpLink.Abstractions', 'SharpLink.Runtime', 'SharpLink.Client', 'SharpLink.Server'}


def read_json(path):
    def reject_constant(value):
        raise ValueError('Non-finite JSON number: ' + value)
    def unique_pairs(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError('Duplicate JSON key: ' + key)
            result[key] = value
        return result
    return json.loads(Path(path).read_text(), parse_constant=reject_constant, object_pairs_hook=unique_pairs)


def expected_script_hashes(root, sha):
    root = Path(root)
    def git(*args):
        return subprocess.check_output(['git', '-C', str(root), *args], text=True).strip()
    if git('rev-parse', 'HEAD') != sha or git('status', '--porcelain', '--untracked-files=normal'):
        raise ValueError('Expected-script checkout must be the exact clean production SHA')
    return {name: hashlib.sha256((root / name).read_bytes()).hexdigest() for name in SCRIPTS}


def context_from_args(args):
    values = {'SourceCommit': args.sha, 'ToolingCommit': args.tooling_sha,
              'RunId': args.run_id, 'RunAttempt': args.run_attempt, 'FinalMain': args.final_main == 'true'}
    for key in ('SourceCommit', 'ToolingCommit'):
        if not re.fullmatch('[0-9a-f]{40}', values[key]):
            raise ValueError(key + ' must be a full lowercase SHA')
    for key in ('RunId', 'RunAttempt'):
        if not re.fullmatch('[1-9][0-9]*', values[key]):
            raise ValueError(key + ' must be a positive integer string')
    return values


def add_context_arguments(parser):
    parser.add_argument('--tooling-sha', required=True)
    parser.add_argument('--run-id', required=True)
    parser.add_argument('--run-attempt', required=True)
    parser.add_argument('--final-main', choices=['true', 'false'], required=True)
    parser.add_argument('--source-root', type=Path, required=True)


def read_hash_manifest(path):
    result = {}
    for line in Path(path).read_text().splitlines():
        match = re.fullmatch(r'([0-9a-f]{64})  (.+)', line)
        if not match or match[2] in result:
            raise ValueError('Invalid or duplicate SHA256 manifest entry')
        result[match[2]] = match[1]
    if not result:
        raise ValueError('Empty SHA256 manifest')
    return result


def validate_provenance(folder, context, scripts, required_binaries=None):
    folder = Path(folder)
    failures = []
    def check(ok, message):
        if not ok:
            failures.append(folder.name + ': ' + message)
    try:
        for name in ('environment.txt', 'run.log', 'completed.txt', 'source-sha.txt'):
            check((folder / name).is_file() and (folder / name).stat().st_size > 0, 'missing ' + name)
        for name in ('source-sha.txt', 'completed.txt'):
            check((folder / name).read_text().strip() == context['SourceCommit'], name + ' source mismatch')
        provenance = read_json(folder / 'provenance.json')
        check(type(provenance.get('SchemaVersion')) is int and provenance['SchemaVersion'] == 1, 'provenance schema')
        for key, value in context.items():
            check(type(provenance.get(key)) is type(value) and provenance.get(key) == value, 'provenance ' + key)
        check(provenance.get('SourceTreeClean') is True and provenance.get('Version') == '2.0.4', 'unclean/wrong-version provenance')
        check(isinstance(provenance.get('MainCommit'), str) and bool(re.fullmatch('[0-9a-f]{40}', provenance['MainCommit'])), 'missing main identity')
        if context['FinalMain']:
            check(provenance.get('MainCommit') == context['SourceCommit'], 'source was not fetched main')
        check(read_hash_manifest(folder / 'canonical-scripts.sha256') == scripts, 'canonical script hashes differ from exact source')
        if required_binaries is None:
            return failures
        manifest = read_hash_manifest(folder / 'dll-sha256.txt')
        dlls = read_json(folder / 'dll-identities.json')
        check(dlls.get('SchemaVersion') == 1 and dlls.get('SourceCommit') == context['SourceCommit'], 'binary provenance header')
        assemblies = dlls['Assemblies']
        check(type(assemblies) is list and bool(assemblies), 'empty binary inventory')
        identities = {}
        names = set()
        for item in assemblies:
            path, name = item['Path'], item['Name']
            parts = PurePosixPath(path).parts
            check(not PurePosixPath(path).is_absolute() and '..' not in parts and parts[0] in ('src', 'test') and
                  '/bin/Release/' in path and path.endswith('/' + name + '.dll'), 'invalid binary path ' + path)
            check(path not in identities, 'duplicate binary ' + path)
            check(item['AssemblyVersion'] == '2.0.4.0', 'wrong assembly version ' + path)
            check(item['InformationalVersion'] == '2.0.4+' + context['SourceCommit'], 'wrong actual DLL informational version ' + path)
            check(isinstance(item['Sha256'], str) and bool(re.fullmatch('[0-9a-f]{64}', item['Sha256'])), 'invalid DLL hash ' + path)
            identities[path] = item['Sha256']
            names.add(name)
        check(manifest == identities, 'DLL hashes/metadata inventory disagree')
        check((COMMON_BINARIES | set(required_binaries)) <= names, 'missing required binary identities')
        for name in required_binaries:
            check(f'test/{name}/bin/Release/net10.0/{name}.dll' in identities, 'missing executed entrypoint ' + name)
    except (KeyError, ValueError, TypeError, OSError, IndexError) as error:
        failures.append(folder.name + ': invalid provenance: ' + str(error))
    return failures
