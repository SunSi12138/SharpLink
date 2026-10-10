#!/usr/bin/env python3
"""Fail-closed, hash-checked diagnostic projection. Never edits the source checkout."""
import difflib
import hashlib
import json
import pathlib
import shutil
import subprocess
import tarfile
from counters import NAMES, counter_source

SOURCE = 'eb99fe887cf2129d9b88441245ca0a4a6406b6c2'
HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parents[3]
C = 'global::SharpLink.Abstractions.Issue739MultiplicityCounters'

def inc(name):
    return f'{C}.Increment({C}.{name});'

def add(name, value):
    return f'{C}.Add({C}.{name}, {value});'

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def replace(text, old, new, count=1):
    actual = text.count(old)
    if actual != count:
        raise ValueError(f'Expected {count} exact matches, got {actual}: {old[:110]!r}')
    return text.replace(old, new)

def mutations(path, text):
    if path.endswith('SharpLinkClient.Invokers.cs'):
        text = replace(text, '        if (invocation.IsCompleted)\n        {',
            '        '+inc('logical_decision')+'\n        if (invocation.IsCompleted)\n        {\n            '+inc('logical_fast'), 2)
        text = replace(text, '        return AwaitLogicalInvocationAsync(invocation);',
            '        '+inc('logical_slow')+'\n        return AwaitLogicalInvocationAsync(invocation);', 2)
        for signature in ['private async ValueTask<T> AwaitLogicalInvocationAsync<T>(ValueTask<T> invocation)',
                          'private async ValueTask AwaitLogicalInvocationAsync(ValueTask invocation)']:
            text = replace(text, signature+'\n    {', signature+'\n    {\n        '+inc('logical_helper_entry'))
    elif path.endswith('RpcRequestOperation.cs'):
        text = replace(text, '        _core.RunContinuationsAsynchronously = true;',
            '        _core.RunContinuationsAsynchronously = true;\n        '+inc('operation_new'))
        text = replace(text, '        => _core.OnCompleted(continuation, state, token, flags);', '''    {
        '''+inc('operation_registration_entry')+'''
        '''+inc('operation_registration_inflight')+'''
        try
        {
            _core.OnCompleted(continuation, state, token, flags);
            // Continuation may already have consumed and recycled this instance.
            // Only static counters are accessed from this point onward.
            '''+inc('operation_registration_accepted')+'''
        }
        catch
        {
            '''+inc('operation_registration_failure')+'''
            throw;
        }
        finally { '''+add('operation_registration_inflight', '-1')+''' }
    }''')
    elif path.endswith('Admission/ServerCallAdmission.cs'):
        old = '            permit = new ServerRequestPermit(this, connection, testHooks, mayDecode);'
        text = replace(text, old, old+'\n            if (mayDecode) '+inc('permit_decode_new')+'\n            else '+inc('permit_plain_new'))
    elif path.endswith('ServerConnectionState.cs'):
        text = replace(text, '            return defaultCallContext;', '            '+inc('context_cache_hit')+'\n            return defaultCallContext;')
        text = replace(text, '        return new SharpLinkCallContextSnapshot(\n            Session.Id,',
                       '        var snapshot = new SharpLinkCallContextSnapshot(\n            Session.Id,')
        text = replace(text, '            _timeProvider,\n            metadata);',
                       '            _timeProvider,\n            metadata);\n        '+inc('context_snapshot_new')+'\n        return snapshot;')
    elif path.endswith('SharpLinkCallContext.cs'):
        text = replace(text, '        SCurrent.Value = snapshot;',
            f'        {C}.Transition({C}.push_null_null, previous, snapshot);\n        SCurrent.Value = snapshot;')
        text = replace(text, '            SCurrent.Value = previous;',
            f'            {C}.Transition({C}.restore_null_null, SCurrent.Value, previous);\n            SCurrent.Value = previous;')
    elif path.endswith('Transport/TransportConnection.cs'):
        text = replace(text, '            read = inner.ReadAsync(cancellationToken);',
            '            '+inc('ownership_read_calls')+'\n            read = inner.ReadAsync(cancellationToken);')
        text = replace(text, '''        return read.IsCompletedSuccessfully
            ? read
            : AwaitReadAsync(read);''', '''        if (read.IsCompletedSuccessfully)
        {
            '''+inc('ownership_read_completed_successfully')+'''
            return read;
        }
        '''+inc('ownership_read_other')+'''
        return AwaitReadAsync(read);''')
        text = replace(text, 'private async ValueTask<ReadResult> AwaitReadAsync(ValueTask<ReadResult> read)\n    {',
            'private async ValueTask<ReadResult> AwaitReadAsync(ValueTask<ReadResult> read)\n    {\n        '+inc('ownership_helper_entry'))
    elif path.endswith('RpcSession.SendPump.cs'):
        text = replace(text, '                _wakeup.Signal();\n                return SendEnqueueResult.Accepted;',
                       '                '+inc('send_accepted_frames')+'\n                _wakeup.Signal();\n                return SendEnqueueResult.Accepted;', 2)
        text = replace(text, '            var flush = _output.FlushAsync(_sessionCancellation);',
            '            '+inc('send_flush_calls')+'\n            '+add('send_flush_frames', 'pending.Count')+'\n            var flush = _output.FlushAsync(_sessionCancellation);\n            if (flush.IsCompleted) '+inc('send_flush_completed_at_probe')+'\n            else '+inc('send_flush_incomplete_at_probe'))
        text = replace(text, '''                    _capacityChanged ??= new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    waitTask = _capacityChanged.Task;''', '''                    if (_capacityChanged is null)
                    {
                        _capacityChanged = new TaskCompletionSource<bool>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        '''+inc('send_capacity_tcs_new')+'''
                    }
                    '''+inc('send_capacity_wait_uses')+'''
                    waitTask = _capacityChanged.Task;''')
    elif path.endswith('WakeupSignal.cs'):
        text = replace(text, '            _core.OnCompleted(continuation, state, token, flags);',
            '            _core.OnCompleted(continuation, state, token, flags);\n            '+inc('wakeup_registration_accepted'))
        text = replace(text, '            ValueTaskSourceOnCompletedFlags.None);\n    }',
            '            ValueTaskSourceOnCompletedFlags.None);\n        '+inc('wakeup_registration_accepted')+'\n    }')
    elif path.endswith('SharedMemoryAsyncPulse.cs'):
        text = replace(text, '        => _source.OnCompleted(continuation, state, token, flags);',
            '    {\n        _source.OnCompleted(continuation, state, token, flags);\n        '+inc('shm_pulse_registration_accepted')+'\n    }')
    else:
        raise ValueError(path)
    return text

def harness(text):
    text = replace(text, '        if (args.Length != 7)', '''        if (args.Length == 2 && args[0] == "counter-control")
        {
            await MultiplicityProbe.CounterControl(args[1]);
            return;
        }
        var multiplicity = MultiplicityProbe.Connect();
        if (args.Length != 7)''')
    text = replace(text, '        if (diagnostic) { factory', '        if (diagnostic) throw new InvalidOperationException("Legacy read wrapper must be disabled");\n        if (diagnostic) { factory')
    text = replace(text, '        var rpc = client.Get<ITiny>();',
        '        var rpc = client.Get<ITiny>();\n        multiplicity.VerifyConfiguration(client, rpc, server, rpcKind);')
    text = replace(text, '        Markers.Log.Start(args[5]);',
        '        multiplicity.WaitForRegistrationReturn();\n        multiplicity.CaptureStart();\n        Markers.Log.Start(args[5]);')
    text = replace(text, '        await all;\n        await Drain();',
        '        await all;\n        await Drain();\n        multiplicity.WaitForRegistrationReturn();')
    text = replace(text, '        int gen0 = GC.CollectionCount(0) - gen0Start;',
        '        multiplicity.CaptureEnd();\n        int gen0 = GC.CollectionCount(0) - gen0Start;')
    text = replace(text, '            schemaVersion = 2, processId',
        '            multiplicity = multiplicity.Report(operations),\n            schemaVersion = 2, processId')
    return text

def project(work_root, output):
    expected = json.loads((HERE/'expected_hashes.json').read_text())
    for path, digest in expected.items():
        if sha(ROOT/path) != digest:
            raise ValueError(f'Source/harness hash mismatch: {path}')
    changed = subprocess.check_output(['git', 'diff', SOURCE, '--', 'src', 'Directory.Build.props', 'Directory.Packages.props', 'global.json'], cwd=ROOT)
    if changed:
        raise ValueError('Primary production source differs from exact pin')
    if subprocess.check_output(['git','ls-files','--others','--exclude-standard','src'],cwd=ROOT).strip():
        raise ValueError('Untracked production inputs exist')
    if work_root.exists():
        raise ValueError(f'Projection directory already exists: {work_root}; use a fresh path')
    work_root.mkdir(parents=True)
    manifests = {}
    for variant in ['A', 'B']:
        dest = work_root/variant
        dest.mkdir()
        process = subprocess.Popen(['git', 'archive', SOURCE], cwd=ROOT, stdout=subprocess.PIPE)
        with tarfile.open(fileobj=process.stdout, mode='r|') as archive:
            archive.extractall(dest, filter='data')
        if process.wait():
            raise RuntimeError('git archive failed')
        target = dest/'eng/validation/issue739'
        target.mkdir(parents=True, exist_ok=True)
        for filename in ['Program.cs', 'Issue739.csproj', 'ReadDiagnostics.cs', 'ReceiveCredits.cs']:
            shutil.copy2(ROOT/'eng/validation/issue739'/filename, target/filename)
        (target/'Program.cs').write_text(harness((target/'Program.cs').read_text()))
        shutil.copy2(HERE/'MultiplicityProbe.cs.in', target/'MultiplicityProbe.cs')
        patches = []
        if variant == 'B':
            for path in expected:
                if not path.startswith('src/'):
                    continue
                file = dest/path
                before = file.read_text()
                after = mutations(path, before)
                file.write_text(after)
                patches.extend(difflib.unified_diff(before.splitlines(True), after.splitlines(True), fromfile='a/'+path, tofile='b/'+path))
            counter_path = dest/'src/SharpLink.Abstractions/Issue739MultiplicityCounters.cs'
            counter_path.write_text(counter_source())
            patches.extend(difflib.unified_diff([], counter_path.read_text().splitlines(True), fromfile='/dev/null', tofile='b/src/SharpLink.Abstractions/Issue739MultiplicityCounters.cs'))
        (output/f'{variant}-source.patch').write_text(''.join(patches))
        relevant = [p for p in (dest/'src').rglob('*') if p.is_file()] + list(target.glob('*.cs')) + list(target.glob('*.csproj'))
        relevant += [dest/name for name in ['Directory.Build.props','Directory.Packages.props','global.json','.editorconfig'] if (dest/name).is_file()]
        manifests[variant] = {'role': 'vanilla-primary' if variant == 'A' else 'source-counter-diagnostic',
            'files': {str(p.relative_to(dest)): sha(p) for p in relevant},
            'patchSha256': sha(output/f'{variant}-source.patch')}
    (output/'projection-manifest.json').write_text(json.dumps({'sourceSha': SOURCE, 'counterNames': NAMES,
        'expectedInputHashes': expected, 'variants': manifests}, indent=2)+'\n')
    return {v:work_root/v for v in manifests}
