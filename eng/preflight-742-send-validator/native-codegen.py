#!/usr/bin/env python3
"""Archive matched actual RPC ELFs; collect code without inferring proof from absence."""
import argparse
import gzip
import json
import pathlib
import re
import subprocess
import tarfile

from prepare import SOURCE, TREES, git, save, sha

METHODS = ('ValidateSendLease', 'TryValidateSendLease', 'GetValidSendState',
           'TryAcquireSendCredit', 'AcquireSendCreditAsync',
           'AcquireResolvedContendedSendCreditAsync', 'ReturnUnsentCredit')
SYMBOL = re.compile(r'StreamFlowController__(' + '|'.join(METHODS) + r')(?:_\d+)?$')
ASSIGNMENT = re.compile(r'<([^>]*(?:CheckedAssignRef|AssignRef)[^>]*)>')


def source_snapshot(root, label):
    assert git(root, 'write-tree') == TREES[label]
    assert not git(root, 'diff', '--name-only', '--', 'src', 'test')
    untracked = git(root, 'ls-files', '--others', '--exclude-standard', '--', 'src', 'test').splitlines()
    assert all(name.startswith('test/SharpLink.FirstReceiveRpcEvidence/') for name in untracked), untracked
    return {name: sha(root / name) for name in git(root, 'ls-files', '--', 'src', 'test').splitlines()}


def extract(binary, output):
    output.mkdir(parents=True, exist_ok=True)
    assert binary.read_bytes()[:4] == b'\x7fELF'
    symbols_text = subprocess.check_output(['nm', '-n', '--defined-only', str(binary)], text=True)
    (output / 'symbols.txt').write_text(symbols_text)
    (output / 'file.txt').write_text(subprocess.check_output(['file', str(binary)], text=True))
    with (output / 'objdump-all.txt').open('w') as file:
        subprocess.run(['objdump', '-d', str(binary)], stdout=file, stderr=subprocess.STDOUT, check=True)
    with (output / 'objdump-all.txt').open('rb') as source, gzip.open(output / 'objdump-all.txt.gz', 'wb') as target:
        for block in iter(lambda: source.read(1024 * 1024), b''):
            target.write(block)
    (output / 'objdump-all.txt').unlink()
    selected = {method: [] for method in METHODS}
    excerpts = []
    for line in symbols_text.splitlines():
        parts = line.split(maxsplit=2)
        if len(parts) != 3 or parts[1] not in ('t', 'T'):
            continue
        match = SYMBOL.search(parts[2])
        if not match:
            continue
        address, _, symbol = parts
        text = subprocess.check_output(['objdump', '-d', '--disassemble=' + symbol, str(binary)], text=True)
        selected[match[1]].append(dict(symbol=symbol, address=address,
            calls=[line.strip() for line in text.splitlines() if re.search(r'\bcallq?\b', line)],
            reference_assignment_sites=[line.strip() for line in text.splitlines() if ASSIGNMENT.search(line)]))
        excerpts.append(text)
    (output / 'focused-disassembly.txt').write_text('\n'.join(excerpts))
    save(output / 'focused-symbols.json', selected)
    # Some validators/callers can inline or be pruned by NativeAOT. Preserve the
    # absence explicitly; never mistake a missing symbol for an absent cost.
    result = dict(binary_sha256=sha(binary), symbols=selected,
        missing_method_groups=[method for method, entries in selected.items() if not entries],
        proof_status='pending-independent-review',
        required_review=['Locate G2 old out-reference assignment on the resolved validation path.',
            'Trace the matching V resolved paths, whether outlined or inlined.',
            'Verify type, owner, Attached, generation and null/rejection checks remain.',
            'Verify three throwing admissions and no-op refund semantics plus lock/terminal/abort checks.',
            'Distinguish legitimate waiter/pool assignments elsewhere; do not ban helpers globally.'],
        boundary='Raw machine code and exact addresses are retained. No normalization, '
            'automatic performance claim, automatic mechanism pass, or symbol-absence success.')
    save(output / 'review-status.json', result)
    return result


def main(destination, output):
    manifest = json.loads((destination / 'identities.json').read_text())
    assert manifest['trees'] == TREES
    roots = {label: pathlib.Path(manifest['roots'][label]) for label in ('G2', 'V')}
    output.mkdir(parents=True, exist_ok=True)
    before = {label: source_snapshot(root, label) for label, root in roots.items()}
    save(output / 'production-source-before.json', before)
    subprocess.run(['python3', str(SOURCE / 'eng/preflight-735-prepare-native-rpc.py'),
        str(roots['G2']), str(roots['V']), str(output / 'common-host')], check=True)
    after_adapter = {label: source_snapshot(root, label) for label, root in roots.items()}
    save(output / 'production-source-after-adapter.json', after_adapter)
    assert before == after_adapter, 'Native host adaptation changed production/tracked test sources'
    host_hashes = {label: {path.name: sha(path) for path in
        (root / 'test/SharpLink.FirstReceiveRpcEvidence').iterdir() if path.is_file()} for label, root in roots.items()}
    assert host_hashes['G2'] == host_hashes['V']
    save(output / 'adapter-only-source.json', host_hashes)
    reports = {}
    for label, root in roots.items():
        publish = root / 'artifacts/validator-native-rpc'
        command = ['dotnet', 'publish', str(root / 'test/SharpLink.FirstReceiveRpcEvidence'),
            '-c', 'Release', '-r', 'linux-x64', '-p:PublishAot=true', '-p:StripSymbols=false', '-o', str(publish)]
        save(output / (label + '-publish-command.json'), command)
        with (output / (label + '-build.log')).open('w') as log:
            result = subprocess.run(command, cwd=root, stdout=log, stderr=subprocess.STDOUT)
        if result.returncode:
            print((output / (label + '-build.log')).read_text(), flush=True)
            raise SystemExit(result.returncode)
        files = {str(path.relative_to(publish)): sha(path) for path in publish.rglob('*') if path.is_file()}
        assert files and 'SharpLink.Benchmarks' in files
        save(output / (label + '-publish-sha256.json'), files)
        archive = output / ('native-rpc-elf-' + label + '.tar.gz')
        with tarfile.open(archive, 'w:gz') as tar:
            tar.add(publish, arcname='.')
        with tarfile.open(archive) as tar:
            (output / (label + '-archive-contents.txt')).write_text('\n'.join(tar.getnames()) + '\n')
        save(output / (label + '-archive-sha256.json'), {archive.name: sha(archive)})
        reports[label] = extract(publish / 'SharpLink.Benchmarks', output / label)
        assert files == {str(path.relative_to(publish)): sha(path) for path in publish.rglob('*') if path.is_file()}
    after = {label: source_snapshot(root, label) for label, root in roots.items()}
    save(output / 'production-source-after-builds.json', after)
    assert before == after
    for label, root in roots.items():
        current = {name: sha(root / 'test/SharpLink.FirstReceiveRpcEvidence' / name) for name in host_hashes[label]}
        assert current == host_hashes[label]
    save(output / 'codegen-review.json', dict(production_trees={label: TREES[label] for label in roots},
        reports=reports, proof_status='pending-independent-review', timing_enabled=False,
        boundary='Collection success is not proof that the intended old path changed correctly. '
            'Do not publish a timing workflow until independent inspection resolves the old/new path.'))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('destination', type=pathlib.Path)
    parser.add_argument('output', type=pathlib.Path)
    args = parser.parse_args()
    main(args.destination.resolve(), args.output.resolve())
