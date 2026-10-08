#!/usr/bin/env python3
"""Collect pristine G2/R actual RPC NativeAOT code; never execute or time the ELF."""
import argparse
import json
import lzma
import os
import pathlib
import re
import subprocess
import tarfile
import traceback

from prepare import SOURCE, trees, git, save, sha
from codegen import budget

HOST = pathlib.Path('test/SharpLink.FirstReceiveRpcEvidence')
SDK = '10.0.112'
# Include original binding and retained dispatch separately. The retained outer
# wrappers may inline into the two root MoveNext bodies; absence is not proof.
METHOD_OWNERS = {
    'ClientConnection': ('SendClientStreamAsync',),
    'RpcSession': (
        'PumpGeneratedOutboundStreamAsync',
        'SendClientStreamChunkResolvedAsync', 'SendGeneratedStreamChunkResolvedAsync',
        'SendUnsizedStreamChunkResolvedAsync', 'SendStreamChunkKnownSizeResolvedAsync',
        'AwaitPreCreditBudgetAndRetainedFlowCreditAsync',
        'SendClientStreamChunkWithCreditLeaseAsync', 'SendGeneratedStreamChunkAsync',
        'SendClientStreamChunkKnownSizeAsync', 'SendClientUnsizedStreamChunkAsync',
        'SendUnsizedStreamChunkWithCreditLeaseAsync', 'SendStreamChunkKnownSizeWithCreditLeaseAsync',
        'SerializeUnsizedStreamChunkWithCreditLease', 'AwaitClientPreCreditBudgetAndFlowCreditAsync',
        'AwaitPreCreditBudgetAndResolvedFlowCreditAsync', 'AcquireStreamSendCreditAsync',
        'AcquireResolvedStreamSendCreditAsync', 'TryAcquireResolvedStreamSendCredit',
        'TryAcquireStreamSendCredit', 'AwaitResolvedSendCreditAsync', 'ReturnUnsentStreamCredit',
        'ThrowIfStreamPublicationRejected', 'SendPacket', 'ObserveAbandonedGeneratedSendAsync'),
    'StreamFlowController': (
        'TryAcquireSendCredit', 'AcquireSendCreditAsync', 'AcquireResolvedContendedSendCreditAsync',
        'TryAcquireSendCreditLease', 'AcquireSendCreditLeaseAsync', 'AwaitFirstAdmissionLeaseAsync',
        'ResolveSendCreditLease', 'ValidateSendLease', 'TryValidateSendLease', 'ReturnUnsentCredit'),
    'PreCreditSerializedBudget': ('AcquireAsync', 'AcquireContendedAsync', 'WaitForGrantAsync', 'Release'),
    'SharpLinkTimer': ('WaitAsync',),
    'BenchmarkRpcService': ('UploadPayloadsAsync', 'DownloadPayloadsAsync'),
    'GeneratedAbiStreamingCase': ('CreateAsync', 'InvokeServerStreamingAsync'),
    'BenchmarkEnvironment': ('CreateSharedMemoryAsync',),
}
GROUPS = {owner + '.' + method: re.compile(re.escape(owner + '__' + method) + r'(?:$|[_<])')
          for owner, methods in METHOD_OWNERS.items() for method in methods}
GROUPS['GeneratedRpc.PayloadProxies'] = re.compile(
    r'SharpLink_Benchmarks_SharpLink_Generated_.*__(?:UploadPayloadsAsync|DownloadPayloadsAsync)(?:$|[_<])')
HEADER = re.compile(r'^([0-9a-fA-F]+) <(.+)>:$')
ASSIGNMENT = re.compile(r'<([^>]*(?:CheckedAssignRef|AssignRef)[^>]*)>')
REVIEW_SOURCES = (
    'src/SharpLink.Client/ClientConnection.cs',
    'src/SharpLink.Runtime/RpcSession.ClientStreamPublication.cs',
    'src/SharpLink.Runtime/RpcSession.GeneratedServerBridge.cs',
    'src/SharpLink.Runtime/RpcSession.PreCreditStreaming.cs',
    'src/SharpLink.Runtime/RpcSession.cs',
    'src/SharpLink.Runtime/StreamFlowController.cs',
    'src/SharpLink.Runtime/StreamFlowController.FirstAdmission.cs',
    'src/SharpLink.Runtime/PreCreditSerializedBudget.cs',
    'src/SharpLink.Serializer.SharpPack/SharpPackRpcCodec.cs',
)
REQUIRED_REVIEW = [
    'Map actual UploadPayloadsAsync/DownloadPayloadsAsync byte[] RPC and SHM server source '
    'to canonical reference root wrappers and their actual MoveNext bodies.',
    'Follow retained outer dispatch even when inlined; establish the first owned 40-byte '
    'lease snapshot before reentrant codec/size callbacks, without aliasing mutable root storage.',
    'Compare the owned-local-to-inner-call boundary: G2 passes a second 40-byte value; '
    'R should pass an 8-byte readonly pointer, removing 32 outgoing payload bytes. '
    'Do not infer a stack-frame shrink from that hypothesis.',
    'Trace the actual SendUnsizedStreamChunkResolvedAsync lease reads, acquire and publication; '
    'exclude an unconditional defensive or compensating whole-lease copy in the callee.',
    'Trace AwaitPreCreditBudgetAndRetainedFlowCreditAsync wrapper and MoveNext: the slow '
    'helper must own a by-value lease copy before the synchronous inner call returns; '
    'refund and contended-controller copies are allowed and must remain accounted for.',
    'Compare unchanged root lease/awaiter fields, first binding, success, suspension, '
    'completed fault/cancel, timer, abandonment and later-item send behavior.',
    'Follow all outlined or inlined helpers, direct calls, tail jumps and indirect dispatch; '
    'missing symbols or unmatched canonical names never establish absence of cost.',
]


def source_snapshot(root, label, identities):
    assert git(root, 'write-tree') == identities[label]
    assert not git(root, 'diff', '--name-only'), 'Tracked source/build-input drift'
    untracked = git(root, 'ls-files', '--others', '--exclude-standard', '--', 'src', 'test').splitlines()
    assert all(name.startswith(HOST.as_posix() + '/') for name in untracked), untracked
    return {name: sha(root / name) for name in git(root, 'ls-files', '--', 'src', 'test').splitlines()}


def checked(command, cwd, log):
    log.parent.mkdir(parents=True, exist_ok=True)
    with log.open('w') as stream:
        result = subprocess.run(command, cwd=cwd, stdout=stream, stderr=subprocess.STDOUT)
    if result.returncode:
        raise subprocess.CalledProcessError(result.returncode, command)


def archive_files(root, paths, archive):
    paths = sorted(paths)
    hashes = {str(path.relative_to(root)): sha(path) for path in paths}
    with tarfile.open(archive, 'w:gz') as stream:
        for path in paths:
            stream.add(path, arcname=str(path.relative_to(root)), recursive=False)
    save(archive.with_name(archive.name + '.json'), dict(sha256=sha(archive), files=hashes))
    assert hashes == {str(path.relative_to(root)): sha(path) for path in paths}
    return hashes


def capture_sources(root, output):
    host = root / HOST
    generated = sorted((host / 'obj').rglob('*.cs'))
    assert generated, 'Retain actual generated byte[] RPC source, not a substitute microbenchmark'
    generated_hashes = archive_files(host, generated, output / 'generated-source.tar.gz')
    archive_files(root, [root / name for name in REVIEW_SOURCES], output / 'production-review-source.tar.gz')
    # Preserve exact generated declarations and callers for manual canonical mapping.
    # These source hits are navigation aids, not evidence that a machine-code path ran.
    terms = ('UploadPayloadsAsync', 'DownloadPayloadsAsync', 'DuplexPayloadsAsync',
             'byte[]', 'System.Byte[]', 'SharedMemory', 'sharedmemory', 'Server1x16',
             'Server100x16', 'Client100x16', 'Client100x4096',
             'SendClientStream', 'PumpGeneratedOutboundStream', 'SharpPackRpcCodec')
    files = generated + sorted(host.glob('*.cs'))
    hits = {str(path.relative_to(host)): [dict(line=number, text=line)
            for number, line in enumerate(path.read_text().splitlines(), 1)
            if any(term in line for term in terms)] for path in files}
    assert any('DownloadPayloadsAsync' in row['text'] for path in generated
               for row in hits[str(path.relative_to(host))]), 'Generated byte[] server RPC mapping missing'
    assert any('UploadPayloadsAsync' in row['text'] for path in generated
               for row in hits[str(path.relative_to(host))]), 'Generated byte[] client RPC mapping missing'
    save(output / 'actual-rpc-source-map.json', dict(generated_sha256=generated_hashes, hits=hits,
        requested_compiled_path_review_targets=[
            dict(scenario='Client100x16', transport='tcp'),
            dict(scenario='Client100x4096', transport='sharedmemory'),
            dict(scenario='Server1x16', transport='sharedmemory'),
            dict(scenario='Server100x16', transport='sharedmemory')],
        execution_performed=False, canonical_mapping='pending-independent-review',
        boundary='Compiled path review targets and source mapping only; NativeAOT generics '
                 'can share canonical bodies. These are not executed NativeAOT cases or '
                 'runtime path coverage. The ELF is not executed and no transport workload '
                 'is timed here. Actual retained-item routing must be established separately.'))


def extract(binary, output):
    output.mkdir(parents=True, exist_ok=True)
    with binary.open('rb') as stream:
        assert stream.read(4) == b'\x7fELF'
    with (output / 'nm-stderr.txt').open('w') as errors:
        symbols_text = subprocess.check_output(
            ['nm', '-n', '--defined-only', str(binary)], text=True, stderr=errors)
    with lzma.open(output / 'symbols.txt.xz', 'wt', preset=6) as stream:
        stream.write(symbols_text)
    checked(['file', str(binary)], binary.parent, output / 'file.txt')
    selected = {group: [] for group in GROUPS}
    by_address = {}
    for line in symbols_text.splitlines():
        parts = line.split(maxsplit=2)
        if len(parts) != 3 or parts[1] not in ('t', 'T'):
            continue
        address, _, symbol = parts
        groups = [group for group, pattern in GROUPS.items() if pattern.search(symbol)]
        if not groups:
            continue
        entry = dict(symbol=symbol, address=address, canonical_reference='System___Canon' in symbol,
                     disassembly_captured=False, calls=[], branches=[], reference_assignment_sites=[])
        for group in groups:
            selected[group].append(entry)
        by_address.setdefault(int(address, 16), []).append(entry)
    save(output / 'focused-symbols.json', selected)
    command = ['objdump', '-d', str(binary)]
    save(output / 'disassembly-command.json', command)
    # One raw full disassembly plus address-indexed excerpts. Looking up aliases by
    # address avoids silently losing a body when objdump chooses another symbol.
    with (output / 'objdump-stderr.txt').open('w') as errors, \
            lzma.open(output / 'objdump-all.txt.xz', 'wt', preset=6) as full, \
            (output / 'focused-disassembly.txt').open('w') as focused:
        with subprocess.Popen(command, stdout=subprocess.PIPE, stderr=errors, text=True) as process:
            entries = []
            for line in process.stdout:
                full.write(line)
                header = HEADER.match(line.strip())
                if header:
                    entries = by_address.get(int(header[1], 16), [])
                    for entry in entries:
                        entry['disassembly_symbol'] = header[2]
                if entries:
                    focused.write(line)
                    instruction = bool(re.match(r'^\s*[0-9a-fA-F]+:\s+[0-9a-fA-F]{2}\b', line))
                    for entry in entries:
                        entry['disassembly_captured'] |= instruction
                        if re.search(r'\bcallq?\b', line):
                            entry['calls'].append(line.strip())
                        if re.search(r'\bj[a-z]+\b', line):
                            entry['branches'].append(line.strip())
                        if ASSIGNMENT.search(line):
                            entry['reference_assignment_sites'].append(line.strip())
            status = process.wait()
    save(output / 'focused-symbols.json', selected)
    result = dict(binary_sha256=sha(binary), symbols=selected,
        missing_method_groups=[group for group, entries in selected.items() if not entries],
        symbols_without_disassembly=[entry['symbol'] for entries in by_address.values()
                                     for entry in entries if not entry['disassembly_captured']],
        objdump_exit_code=status, proof_status='pending-independent-review',
        required_review=REQUIRED_REVIEW,
        boundary='Raw full machine code, ELF, symbols and exact addresses are retained. '
            'Missing/inlined symbols are unresolved review work, never a mechanism pass. '
            'No normalization, root-layout shrink assertion, performance claim or promotion.')
    save(output / 'review-status.json', result)
    if status:
        raise subprocess.CalledProcessError(status, command)
    return result


def main(destination, output):
    identities = trees()
    manifest = json.loads((destination / 'identities.json').read_text())
    assert manifest['trees'] == identities
    roots = {label: pathlib.Path(manifest['roots'][label]) for label in ('G2', 'R')}
    output.mkdir(parents=True, exist_ok=True)
    assert not any(output.iterdir()), 'Refuse to mix current collection with stale evidence'
    index = output / 'index'
    index.mkdir()
    reports, failures, before, host_hashes = {}, [], {}, {}
    phase = 'source-and-toolchain-checks'
    try:
        before = {label: source_snapshot(root, label, identities) for label, root in roots.items()}
        save(index / 'production-source-before.json', before)
        tuning = {key: value for key, value in os.environ.items()
                  if key.startswith(('COMPlus_', 'DOTNET_Jit', 'DOTNET_TC_', 'DOTNET_Tiered', 'Ilc', 'ILC_'))
                  or key in ('DOTNET_ReadyToRun', 'DOTNET_TieredPGO')}
        save(index / 'compiler-environment.json', dict(tuning_overrides=tuning))
        assert not tuning, 'No environment-supplied compiler/optimization tweaks are permitted'
        assert subprocess.check_output(['dotnet', '--version'], text=True).strip() == SDK
        for tool, command in (('dotnet', ['dotnet', '--info']), ('nm', ['nm', '--version']),
                              ('objdump', ['objdump', '--version'])):
            checked(command, SOURCE, index / (tool + '.txt'))
        phase = 'identical-minimal-rpc-host-adapter'
        adapter = SOURCE / 'eng/preflight-735-prepare-native-rpc.py'
        command = ['python3', str(adapter), str(roots['G2']), str(roots['R']), str(index / 'common-host')]
        save(index / 'adapter-command.json', dict(command=command, adapter_sha256=sha(adapter)))
        checked(command, SOURCE, index / 'adapter.log')
        after_adapter = {label: source_snapshot(root, label, identities) for label, root in roots.items()}
        save(index / 'production-source-after-adapter.json', after_adapter)
        assert before == after_adapter, 'Native host adaptation changed production/tracked test sources'
        host_hashes = {label: {path.name: sha(path) for path in (root / HOST).iterdir() if path.is_file()}
                       for label, root in roots.items()}
        assert host_hashes['G2'] == host_hashes['R']
        save(index / 'adapter-only-source.json', host_hashes)
        phase = 'native-publish-and-code-collection'
        for label, root in roots.items():
            target, code_target = output / (label + '-elf'), output / (label + '-code')
            target.mkdir()
            code_target.mkdir()
            try:
                publish = root / 'artifacts/readonly-lease-native-rpc'
                assert not publish.exists(), 'Refuse stale NativeAOT publish output'
                command = ['dotnet', 'publish', str(root / HOST), '-c', 'Release', '-r', 'linux-x64',
                           '-p:PublishAot=true', '-p:StripSymbols=false', '-o', str(publish)]
                save(target / 'publish-command.json', command)
                checked(command, root, target / 'build.log')
                files = [path for path in publish.rglob('*') if path.is_file()]
                assert files and (publish / 'SharpLink.Benchmarks').is_file()
                hashes = archive_files(publish, files, target / 'native-rpc-elf.tar.gz')
                save(target / 'publish-sha256.json', hashes)
                reports[label] = extract(publish / 'SharpLink.Benchmarks', code_target)
                capture_sources(root, code_target)
                assert hashes == {str(path.relative_to(publish)): sha(path)
                                  for path in publish.rglob('*') if path.is_file()}
                reports[label]['artifact_bytes'] = {'elf': budget(target), 'code': budget(code_target)}
            except Exception as error:
                failures.append(dict(phase=phase, arm=label, error=repr(error)))
                (code_target / 'collection-failure.txt').write_text(traceback.format_exc())
                save(code_target / 'collection-status.json', dict(status='failed', error=repr(error),
                    proof_status='not-established', native_execution_performed=False))
                # Preserve this arm's failures and still collect the independent arm.
    except Exception as error:
        failures.append(dict(phase=phase, error=repr(error)))
        (index / 'collection-failure.txt').write_text(traceback.format_exc())
    finally:
        for label, root in roots.items():
            try:
                after = source_snapshot(root, label, identities)
                save(index / (label + '-production-source-after-build.json'), after)
                assert label in before and before[label] == after
                if label in host_hashes:
                    current = {path.name: sha(path) for path in (root / HOST).iterdir() if path.is_file()}
                    assert current == host_hashes[label], 'Evidence-host source changed during publish'
            except Exception as error:
                failures.append(dict(phase='final-source-identity', arm=label, error=repr(error)))
        save(index / 'codegen-review.json', dict(production_trees={label: identities[label] for label in roots},
            execution_commit=git(SOURCE, 'rev-parse', 'HEAD'), reports=reports, failures=failures,
            collection_status='failed' if failures else 'collected', proof_status='pending-independent-review',
            native_execution_performed=False, timing_enabled=False, required_review=REQUIRED_REVIEW,
            boundary='Collection success is not mechanism, safety, layout, throughput or promotion acceptance.'))
    budget(index)
    if failures:
        raise SystemExit(1)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('destination', type=pathlib.Path)
    parser.add_argument('output', type=pathlib.Path)
    args = parser.parse_args()
    main(args.destination.resolve(), args.output.resolve())
