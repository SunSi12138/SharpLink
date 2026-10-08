#!/usr/bin/env python3
"""Archive matched actual RPC ELFs; collect code without inferring proof from absence."""
import argparse
import lzma
import json
import pathlib
import re
import subprocess
import tarfile

from prepare import SOURCE, trees, git, save, sha
from codegen import budget

TREES = trees()

METHODS = ('SendClientStreamAsync', 'PumpGeneratedOutboundStreamAsync',
           'SendClientStreamChunkWithCreditLeaseAsync', 'SendGeneratedStreamChunkAsync',
           'SendClientStreamChunkKnownSizeAsync', 'SendClientUnsizedStreamChunkAsync',
           'SendUnsizedStreamChunkWithCreditLeaseAsync', 'SendStreamChunkKnownSizeWithCreditLeaseAsync',
           'SerializeUnsizedStreamChunkWithCreditLease', 'AwaitClientPreCreditBudgetAndFlowCreditAsync',
           'AwaitPreCreditBudgetAndResolvedFlowCreditAsync',
           'WaitAsync', 'ObserveAbandonedGeneratedSendAsync')
SYMBOL = re.compile(r'(?:ClientConnection|RpcSession)__(' + '|'.join(METHODS[:-2]) +
                    r')(?:$|[_<])|SharpLinkTimer__WaitAsync|RpcSession__ObserveAbandonedGeneratedSendAsync')

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
    with lzma.open(output / 'symbols.txt.xz', 'wt', preset=6) as symbol_file:
        symbol_file.write(symbols_text)
    (output / 'file.txt').write_text(subprocess.check_output(['file', str(binary)], text=True))
    with (output / 'objdump-all.txt').open('w') as file:
        subprocess.run(['objdump', '-d', str(binary)], stdout=file, stderr=subprocess.STDOUT, check=True)
    with (output / 'objdump-all.txt').open('rb') as source, lzma.open(output / 'objdump-all.txt.xz', 'wb', preset=6) as target:
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
        selected[next(method for method in METHODS if method in symbol)].append(dict(symbol=symbol, address=address,
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
        required_review=['Map actual byte[] unsized RPC to canonical reference root wrappers and MoveNext bodies.',
            'Compare exact NativeAOT root/state allocation sizes, zeroing, field and spill locations.',
            'Verify direct success and task-backed suspension/completed fault/cancel paths.',
            'Trace unchanged generated timer, abandonment observation and later-item send paths.',
            'Follow outlined or inlined helpers; missing symbols never establish absence of cost.'],
        boundary='Raw machine code and exact addresses are retained. No normalization, '
            'automatic performance claim, automatic mechanism pass, or symbol-absence success.')
    save(output / 'review-status.json', result)
    return result


def main(destination, output):
    manifest = json.loads((destination / 'identities.json').read_text())
    assert manifest['trees'] == TREES
    roots = {label: pathlib.Path(manifest['roots'][label]) for label in ('G2', 'A')}
    output.mkdir(parents=True, exist_ok=True)
    before = {label: source_snapshot(root, label) for label, root in roots.items()}
    save(output / 'index/production-source-before.json', before)
    subprocess.run(['python3', str(SOURCE / 'eng/preflight-735-prepare-native-rpc.py'),
        str(roots['G2']), str(roots['A']), str(output / 'index/common-host')], check=True)
    after_adapter = {label: source_snapshot(root, label) for label, root in roots.items()}
    save(output / 'index/production-source-after-adapter.json', after_adapter)
    assert before == after_adapter, 'Native host adaptation changed production/tracked test sources'
    host_hashes = {label: {path.name: sha(path) for path in
        (root / 'test/SharpLink.FirstReceiveRpcEvidence').iterdir() if path.is_file()} for label, root in roots.items()}
    assert host_hashes['G2'] == host_hashes['A']
    save(output / 'index/adapter-only-source.json', host_hashes)
    reports = {}
    for label, root in roots.items():
        target = output / (label + '-elf')
        code_target = output / (label + '-code')
        target.mkdir(parents=True, exist_ok=True)
        publish = root / 'artifacts/first-binding-native-rpc'
        command = ['dotnet', 'publish', str(root / 'test/SharpLink.FirstReceiveRpcEvidence'),
            '-c', 'Release', '-r', 'linux-x64', '-p:PublishAot=true', '-p:StripSymbols=false', '-o', str(publish)]
        save(target / 'publish-command.json', command)
        with (target / 'build.log').open('w') as log:
            result = subprocess.run(command, cwd=root, stdout=log, stderr=subprocess.STDOUT)
        if result.returncode:
            print((target / 'build.log').read_text(), flush=True)
            raise SystemExit(result.returncode)
        files = {str(path.relative_to(publish)): sha(path) for path in publish.rglob('*') if path.is_file()}
        assert files and 'SharpLink.Benchmarks' in files
        save(target / 'publish-sha256.json', files)
        archive = target / 'native-rpc-elf.tar.gz'
        with tarfile.open(archive, 'w:gz') as tar:
            tar.add(publish, arcname='.')
        with tarfile.open(archive) as tar:
            (target / 'archive-contents.txt').write_text('\n'.join(tar.getnames()) + '\n')
        save(target / 'archive-sha256.json', {archive.name: sha(archive)})
        reports[label] = extract(publish / 'SharpLink.Benchmarks', code_target)
        reports[label]['artifact_bytes'] = {'elf': budget(target), 'code': budget(code_target)}
        assert files == {str(path.relative_to(publish)): sha(path) for path in publish.rglob('*') if path.is_file()}
    after = {label: source_snapshot(root, label) for label, root in roots.items()}
    save(output / 'index/production-source-after-builds.json', after)
    assert before == after
    for label, root in roots.items():
        current = {name: sha(root / 'test/SharpLink.FirstReceiveRpcEvidence' / name) for name in host_hashes[label]}
        assert current == host_hashes[label]
    save(output / 'index/codegen-review.json', dict(production_trees={label: TREES[label] for label in roots},
        reports=reports, proof_status='pending-independent-review', timing_enabled=False,
        boundary='Collection success is not proof that the intended old path changed correctly. '
            'No throughput comparison or promotion until independent safety and layout review.'))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('destination', type=pathlib.Path)
    parser.add_argument('output', type=pathlib.Path)
    args = parser.parse_args()
    main(args.destination.resolve(), args.output.resolve())
