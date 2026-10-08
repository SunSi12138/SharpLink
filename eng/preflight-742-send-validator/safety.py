#!/usr/bin/env python3
"""Run full safety suites and unchanged allocation controls before codegen review."""
import argparse
import json
import os
import pathlib
import subprocess

from prepare import KIT, SOURCE, TREES, git, pristine, save, sha


def execute(root, out, name, command, env=None):
    path = out / (name + '.log')
    with path.open('w') as log:
        result = subprocess.run(command, cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT)
    print(path.read_text(), flush=True)
    assert result.returncode == 0, (name, result.returncode)


def build(root, out, name, project, *properties):
    execute(root, out, name, ['dotnet', 'build', project, '-c', 'Release', '-p:Platform=AnyCPU',
        '-p:TreatWarningsAsErrors=true', '-m:1', '/nodeReuse:false', '-p:UseSharedCompilation=false', *properties])


def units(root, out, name, count=2133, filter_=None):
    command = ['dotnet', 'test', '--project', 'test/SharpLink.UnitTests', '-c', 'Release',
               '--no-build', '-p:Platform=AnyCPU']
    if filter_:
        command += ['--', '--treenode-filter', '/*/*/StreamFlowControllerTests/' + filter_]
    execute(root, out, name, command)
    subprocess.run(['python3', str(KIT / 'check-tests.py'), str(out / (name + '.log')), str(count)], check=True)


def binaries(root):
    result = {}
    for project in ('SharpLink.UnitTests', 'SharpLink.Benchmarks'):
        folder = root / 'test' / project / 'bin/Release/net10.0'
        result.update({str(path.relative_to(root)): sha(path) for path in folder.rglob('*') if path.is_file()})
    assert result
    return result


def defaults(roots, out):
    for label in ('G2-tests', 'V-tests'):
        root = roots[label]
        pristine(root, TREES[label])
        build(root, out, label + '-build', 'test/SharpLink.UnitTests')
        if label == 'V-tests':
            changed = git(root, 'diff', '--cached', '--name-only', '--', 'src', 'test').splitlines()
            execute(root, out, 'format', ['dotnet', 'format', 'whitespace',
                'test/SharpLink.UnitTests/SharpLink.UnitTests.csproj', '--no-restore', '--verify-no-changes',
                '--include', *changed])
            execute(root, out, 'references', ['python3', 'eng/check-project-reference-boundaries.py'])
            execute(root, out, 'maintainability', ['bash', 'eng/check-maintainability.sh'])
        units(root, out, label + '-default-unit')
        # Explicit unchanged old/new behavioral control: all five new cases,
        # with exact counts, in addition to both complete default suites.
        for method, count in (
            ('InvalidResolvedSendLeasesShouldNotMutateLiveCredit', 1),
            ('StaleResolvedSendLeaseShouldNotMutatePooledReplacement*', 2),
            ('InvalidResolvedSendLeaseShouldPreserveErrorPrecedence*', 2),
        ):
            units(root, out, label + '-' + method.rstrip('*'), count, method)
        build(root, out, label + '-allocation-build', 'test/SharpLink.Benchmarks', '-p:PublishAot=false')
        env = dict(os.environ, SHARPLINK_ALLOCATION_OUTPUT=str(out / (label + '-allocation-gate.json')))
        execute(root, out, label + '-allocation-gate', ['bash', 'eng/run-allocation-gate.sh'], env)
        save(out / (label + '-default-binaries.json'), binaries(root))
        pristine(root, TREES[label])
    # Original 120-row warm/cold matrix and original <=0.01 B/request gate.
    for label, side in (('G2-tests', 'control'), ('V-tests', 'candidate')):
        execute(SOURCE, out, label + '-lifecycle', ['python3',
            str(SOURCE / 'eng/preflight-742-redesign/lifecycle.py'), side, str(roots[label]), str(out)])
    # Original actual-session setup and full production-callback allocation evidence.
    # The reused scripts name their inputs F/G; provenance maps those labels to G2/V.
    save(out / 'original-allocation-label-map.json', {'F': 'G2-tests', 'G': 'V-tests', 'trees': TREES})
    for name in ('actual-setup', 'production-lifecycle'):
        execute(SOURCE, out, name, ['python3', str(SOURCE / 'eng/preflight-742-quiescent' / (name + '.py')),
            str(roots['G2-tests']), str(roots['V-tests']), str(out / name)])
    for label in ('G2-tests', 'V-tests'):
        pristine(roots[label], TREES[label])
        assert binaries(roots[label]) == json.loads((out / (label + '-default-binaries.json')).read_text())
    save(out / 'default-safety-result.json', dict(full_default_units={'G2': 2133, 'V': 2133},
        new_cases_per_arm=5, original_allocation_gates=True, legacy_lifecycle_rows_per_arm=120,
        boundary='Exact production source with the same test-only overlay; no timing evidence.'))


def experimental(roots, out):
    root = roots['V-experimental']
    pristine(root, TREES['V-experimental'])
    for name in ('test-ready-writer.py', 'test-ready-writer-budget.py',
                 'test-ready-writer-collection.py', 'test-ready-writer-native.py'):
        execute(root, out, name.removesuffix('.py'), ['python3', 'eng/' + name])
    execute(root, out, 'writer-prepare', ['python3', 'eng/prepare-ready-writer.py', '--apply'])
    # This disposable hook changes only the experimental root; production and
    # full-default roots remain byte-identical and are never reused from here.
    (out / 'experimental-source.patch').write_text(git(root, 'diff', '--binary') + '\n')
    hook = root / 'src/SharpLink.Runtime/RpcSession.ReadyWriter.cs'
    save(out / 'experimental-source.json', dict(base_tree=TREES['V-experimental'],
        tracked_delta_sha256=sha(out / 'experimental-source.patch'), added_ready_writer_sha256=sha(hook)))
    build(root, out, 'writer-build', 'test/SharpLink.Benchmarks', '-p:ReadyWriterExperiment=true')
    execute(root, out, 'writer-checks', ['dotnet', 'test/SharpLink.Benchmarks/bin/Release/net10.0/SharpLink.Benchmarks.dll',
        '--ready-writer-self-test'])
    assert '171/171 ready writer transport checks passed.' in (out / 'writer-checks.log').read_text()
    build(root, out, 'experimental-build', 'test/SharpLink.UnitTests')
    units(root, out, 'experimental-unit')
    save(out / 'experimental-binaries.json', binaries(root))
    for label in ('G2', 'V', 'G2-tests', 'V-tests'):
        pristine(roots[label], TREES[label])
    save(out / 'experimental-safety-result.json', dict(full_experimental_units=2133, writer_checks=171,
        boundary='Original disposable ready-writer hook only; never a production/native-codegen input.'))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('mode', choices=('default', 'experimental'))
    parser.add_argument('destination', type=pathlib.Path)
    parser.add_argument('output', type=pathlib.Path)
    args = parser.parse_args()
    manifest = json.loads((args.destination / 'identities.json').read_text())
    assert manifest['trees'] == TREES
    roots = {label: pathlib.Path(path) for label, path in manifest['roots'].items()}
    out = args.output.resolve()
    out.mkdir(parents=True, exist_ok=True)
    (defaults if args.mode == 'default' else experimental)(roots, out)
