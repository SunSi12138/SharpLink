#!/usr/bin/env python3
"""Isolated Linux nonce-recovery proof; never patches the checked-out product."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys

RUNTIME = 'src/SharpLink.Runtime/Transport/SharedMemoryTransport.cs'
FIXTURE = 'test/SharpLink.IntegrationTests/SharedMemoryTransportConnectionIntegrationTests.ProcessAndHandshake.cs'
DIAGNOSTIC = 'test/SharpLink.IntegrationTests/SharedMemoryHandshakeBacklogDiagnosticTests.cs'
PROJECT = 'test/SharpLink.IntegrationTests/SharpLink.IntegrationTests.csproj'
BIN = 'test/SharpLink.IntegrationTests/bin/Release/net10.0'
FILTER = '/*/*/SharedMemoryTransportConnectionIntegrationTests/SharedMemoryHandshakeShouldRejectMismatchedAcknowledgementNonceAndCleanMapping'
MODES = ('positive', 'remove-nonce-check', 'block-rearm')


def replace_once(text, old, new):
    if text.count(old) != 1:
        raise ValueError('Diagnostic source anchor is not unique: ' + old[:90])
    return text.replace(old, new, 1)


def apply_overlay(work, tools, mode):
    if mode not in MODES:
        raise ValueError('Unknown diagnostic mode')
    path = work / RUNTIME
    source = path.read_text()
    if 'ForDiagnostics' in source:
        raise ValueError('Refuse to apply overlay twice')
    source = replace_once(source,
        'public sealed class SharedMemoryClientTransportFactory : IClientTransportFactory, IPerformanceProfileAwareTransport\n{',
        'public sealed class SharedMemoryClientTransportFactory : IClientTransportFactory, IPerformanceProfileAwareTransport\n{\n    internal Func<Task>? PipeConnectedForDiagnostics { get; set; }')
    source = replace_once(source,
        '            await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);',
        '            await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);\n            if (PipeConnectedForDiagnostics is { } connected) await connected().ConfigureAwait(false);')
    source = replace_once(source,
        '    internal Action? AcceptIterationStartedForTesting { get; set; }',
        '    internal Action? AcceptIterationStartedForTesting { get; set; }\n    internal Func<Task>? RejectedMappingDisposedForDiagnostics { get; set; }')
    source = replace_once(source,
        '            if (mapping is not null)\n                await mapping.DisposeAsync().ConfigureAwait(false);\n            if (pipe is not null)\n                await pipe.DisposeAsync().ConfigureAwait(false);\n        }\n    }\n\n    private static bool IsRejectedHandshake',
        '            if (mapping is not null)\n            {\n                await mapping.DisposeAsync().ConfigureAwait(false);\n                if (RejectedMappingDisposedForDiagnostics is { } disposed) await disposed().ConfigureAwait(false);\n            }\n            if (pipe is not null)\n                await pipe.DisposeAsync().ConfigureAwait(false);\n        }\n    }\n\n    private static bool IsRejectedHandshake')
    if mode == 'remove-nonce-check':
        source = replace_once(source,
            '        if (!buffer.AsSpan(8, SharedMemoryLayout.NonceBytes).SequenceEqual(expectedNonce))\n            throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "Shared-memory client acknowledgement nonce did not match.");',
            '        // DIAGNOSTIC NEGATIVE CONTROL: acknowledgement nonce validation deliberately removed.')
    if mode == 'block-rearm':
        source = replace_once(source,
            '            if (connection is not null)\n                return connection;',
            '            if (connection is not null)\n                return connection;\n            // DIAGNOSTIC NEGATIVE CONTROL: never begin the next accept.\n            await Task.Delay(Timeout.InfiniteTimeSpan, _disposeCts.Token).ConfigureAwait(false);')
    path.write_text(source)
    shutil.copyfile(tools / 'SharedMemoryHandshakeBacklogDiagnosticTests.cs', work / DIAGNOSTIC)


def inspect(text, total, failed):
    text = re.sub(r'\x1b\[[0-9;]*m', '', text)
    counts = {}
    for key in ('total', 'failed', 'succeeded', 'skipped'):
        matches = re.findall(r'\b' + key + r':\s*(\d+)\b', text)
        counts[key] = int(matches[-1]) if matches else (0 if key == 'failed' else None)
    expected = dict(total=total, failed=failed, succeeded=total-failed, skipped=0)
    if counts != expected:
        raise ValueError(f'Unexpected exact test census: {counts}, expected {expected}')
    return counts


EXPECTED_RUNTIME = '10.0.14'
PIPE_SOURCE = dict(repository='dotnet/runtime', ref='v10.0.14',
                   path='src/libraries/System.IO.Pipes/src/System/IO/Pipes/NamedPipeServerStream.Unix.cs',
                   gitBlob='7a0b6dc8e37055dc1670bfce307336e1c36f65cc')


def run_command(out, result, name, cmd, cwd, expected=0, reports=None, runner=subprocess.run):
    row = dict(name=name, command=cmd, cwd=str(cwd), expectedExitCode=expected,
               exitCode=None, status='running')
    result['rows'].append(row)
    (out / 'result.json').write_text(json.dumps(result, indent=2) + '\n')
    error = None
    launched = False
    completed = None
    try:
        if reports is not None and reports.exists():
            prior = out / (name + '-preexisting-TestResults')
            shutil.move(str(reports), str(prior))
            row['priorReports'] = str(prior)
        launched = True
        completed = runner(cmd, cwd=cwd, text=True, capture_output=True, timeout=600)
        (out / (name + '.stdout.log')).write_text(completed.stdout)
        (out / (name + '.stderr.log')).write_text(completed.stderr)
        row.update(exitCode=completed.returncode, status='completed')
        if completed.returncode != expected:
            row['status'] = 'unexpected-exit'
            raise ValueError(f'{name}: exit {completed.returncode}, expected {expected}')
    except subprocess.TimeoutExpired as exception:
        for stream in ('stdout', 'stderr'):
            value = getattr(exception, stream) or ''
            if isinstance(value, bytes):
                value = value.decode(errors='replace')
            (out / (name + '.' + stream + '.log')).write_text(value)
        row['status'] = 'timeout'
        error = exception
    except Exception as exception:
        error = exception
        if row['status'] == 'running':
            row['status'] = 'launch-or-preparation-failed'
    finally:
        if reports is not None:
            row['reportsStatus'] = 'absent'
            if launched and reports.exists():
                try:
                    destination = out / (name + '-TestResults')
                    shutil.copytree(reports, destination)
                    row.update(reportsStatus='retained', reports=str(destination))
                except Exception as archive_error:
                    row['reportsStatus'] = 'archive-failed'
                    row['reportArchiveError'] = repr(archive_error)
                    if error is None:
                        error = archive_error
        if error is not None:
            row['error'] = dict(type=type(error).__name__, message=str(error))
        (out / 'result.json').write_text(json.dumps(result, indent=2) + '\n')
        print(json.dumps(row), flush=True)
    if error is not None:
        raise error
    return completed.stdout + completed.stderr


def capture_runtime_identity(text):
    roots = re.findall(r'^Microsoft\.NETCore\.App ' + re.escape(EXPECTED_RUNTIME) + r' \[(.+)\]$', text, re.MULTILINE)
    if len(roots) != 1:
        raise ValueError('Require exactly one installed .NET runtime ' + EXPECTED_RUNTIME)
    root = Path(roots[0]) / EXPECTED_RUNTIME
    binaries = {name: dict(path=str(root / name), sha256=hashlib.sha256((root / name).read_bytes()).hexdigest())
                for name in ('System.IO.Pipes.dll', 'System.Private.CoreLib.dll')}
    return dict(expectedReportRuntime='.NET ' + EXPECTED_RUNTIME, binaries=binaries,
                officialSource=PIPE_SOURCE, sourceBinaryCorrespondence='not proved by source blob identity alone')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    if sys.platform != 'linux':
        raise ValueError('This diagnostic intentionally requires Linux')
    repo = Path.cwd()
    if subprocess.check_output(['git', 'status', '--porcelain'], text=True).strip():
        raise ValueError('Require a clean checked-out source')
    out = args.out.resolve()
    out.mkdir(parents=True, exist_ok=False)
    current = subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip()
    temp = Path(os.environ['RUNNER_TEMP']) / ('nonce-recovery-' + os.environ['GITHUB_RUN_ID'] + '-' + os.environ['GITHUB_RUN_ATTEMPT'])
    temp.mkdir(parents=True, exist_ok=False)
    tools = Path(__file__).resolve().parent
    rows = []
    result = dict(sourceSha=current, diagnosticOnly=True, performanceEvidence=False, modes=list(MODES), rows=rows, status='incomplete')

    def run(name, cmd, cwd, expected=0, reports=None):
        return run_command(out, result, name, cmd, cwd, expected, reports)

    try:
        run('dotnet-info', ['dotnet', '--info'], repo)
        runtime = capture_runtime_identity(run('dotnet-runtimes', ['dotnet', '--list-runtimes'], repo))
        (out / 'runtime-identity.json').write_text(json.dumps(runtime, indent=2) + '\n')
        for mode in MODES:
            work = temp / mode
            run(mode + '-worktree', ['git', 'worktree', 'add', '--detach', str(work), current], repo)
            apply_overlay(work, tools, mode)
            for name in (RUNTIME, FIXTURE, DIAGNOSTIC):
                destination = out / mode / name
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(work / name, destination)
            patch = subprocess.check_output(['git', 'diff', '--binary'], cwd=work)
            (out / (mode + '.patch')).write_bytes(patch)
            run(mode + '-build', ['dotnet', 'build', PROJECT, '-c', 'Release', '-m:1', '-nr:false', '-p:UseSharedCompilation=false'], work)
            binaries = {str(p.relative_to(work / BIN)): hashlib.sha256(p.read_bytes()).hexdigest()
                        for p in sorted((work / BIN).rglob('*')) if p.is_file() and p.suffix in ('.dll', '.json', '.pdb') and 'TestResults' not in p.parts}
            (out / (mode + '-binaries.json')).write_text(json.dumps(binaries, indent=2) + '\n')
            cases = [('fixture', FILTER, 1, 0 if mode == 'positive' else 1)]
            if mode == 'positive':
                cases.insert(0, ('gated-window', '/*/*/SharedMemoryHandshakeBacklogDiagnosticTests/*', 2, 0))
            for case, filter_, total, failed in cases:
                label = mode + '-' + case
                text = run(label, ['dotnet', BIN + '/SharpLink.IntegrationTests.dll', '--treenode-filter', filter_],
                           work, 2 if failed else 0, reports=work / BIN / 'TestResults')
                inspect(text, total, failed)
                reports = list((out / (label + '-TestResults')).glob('*.tunit-report.json'))
                if len(reports) != 1:
                    raise ValueError('Require one actual native TUnit report')
                report = json.loads(reports[0].read_text())
                if report['runtimeVersion'] != runtime['expectedReportRuntime']:
                    raise ValueError('Actual test runtime differs from the pinned diagnostic runtime')
                if report['summary'] != dict(total=total, passed=total-failed, failed=failed, skipped=0, cancelled=0, timedOut=0, flaky=0):
                    raise ValueError('TUnit JSON census differs from the required nonflaky result')
                combined = text + json.dumps(report)
                if mode == 'remove-nonce-check' and not any(marker in combined for marker in (
                        'invalid nonce must not complete the original accept', 'the original accept completed before listener rearm')):
                    raise ValueError('Missing specific nonce-validation negative-control failure')
                if mode == 'block-rearm' and 'expected accept iteration 2 was not observed' not in combined:
                    raise ValueError('Missing specific second-iteration blocked failure')
                if case == 'gated-window':
                    for assembly, identity in runtime['binaries'].items():
                        if f"NONCE_RUNTIME {assembly} sha256={identity['sha256']}" not in combined:
                            raise ValueError('Actual loaded runtime assembly hash missing or different: ' + assembly)
                    for marker in ('NONCE_GATE waitForRearm=True connects=1 result=success exchange=both',
                                   'NONCE_GATE waitForRearm=False connects=1 result=Unavailable/IOException'):
                        if marker not in combined:
                            raise ValueError('Missing gated-window observation: ' + marker)
        result['status'] = 'passed'
    except Exception as exception:
        result['status'] = 'failed'
        result['error'] = dict(type=type(exception).__name__, message=str(exception))
        raise
    finally:
        (out / 'result.json').write_text(json.dumps(result, indent=2) + '\n')
        files = {str(p.relative_to(out)): hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(out.rglob('*')) if p.is_file()}
        (out / 'sha256.json').write_text(json.dumps(files, indent=2) + '\n')
    if result['status'] != 'passed':
        raise SystemExit(1)


if __name__ == '__main__':
    main()
