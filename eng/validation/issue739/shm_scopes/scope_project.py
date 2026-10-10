"""Extend the frozen multiplicity projection only in isolated diagnostic copies."""
import difflib
import hashlib
import json
import pathlib
import sys
HERE = pathlib.Path(__file__).resolve().parent
MULTIPLICITY = HERE.parent/'multiplicity'
sys.path.insert(0, str(MULTIPLICITY))
import project as original
from project import SOURCE, ROOT, sha, replace

SITES = {
    'src/SharpLink.Client/SharpLinkClient.Lifecycle.cs': ('reader.ReadAsync(ct)', 'global::SharpLink.Runtime.Issue739ShmScopes.ReadPipe(reader, ct)', 2),
    'src/SharpLink.Server/SharpLinkServer.Handshake.cs': ('reader.ReadAsync(ct)', 'global::SharpLink.Runtime.Issue739ShmScopes.ReadPipe(reader, ct)', 1),
    'src/SharpLink.Server/SharpLinkServer.RequestLoop.cs': ('reader.ReadAsync(ct)', 'global::SharpLink.Runtime.Issue739ShmScopes.ReadPipe(reader, ct)', 1),
    'src/SharpLink.Runtime/Transport/SharedMemoryControlChannel.cs': ('_stream.ReadAsync(signal)', 'Issue739ShmScopes.ReadControl(_stream, signal)', 1),
}

def project(work, output):
    expected = json.loads((HERE/'expected_hashes.json').read_text())
    for filename, digest in expected.items():
        if sha(ROOT/filename) != digest: raise ValueError('Frozen dependency mismatch: '+filename)
    variants = original.project(work, output)
    manifest = json.loads((output/'projection-manifest.json').read_text())
    for variant, dest in variants.items():
        target = dest/'eng/validation/issue739'
        program = target/'Program.cs'
        text = program.read_text()
        text = replace(text, '        var multiplicity = MultiplicityProbe.Connect();', '''        if (args.Length == 2 && args[0] == "scope-metadata")
        {
            ScopeProbe.MetadataControl(args[1]);
            return;
        }
        if (args.Length == 2 && args[0] == "empty-scope-control")
        {
            ScopeProbe.EmptyControl(args[1]);
            return;
        }
        var multiplicity = MultiplicityProbe.Connect();
        var scopes = ScopeProbe.Connect();''')
        text = replace(text, '        ThreadPool.SetMinThreads(132, 132);', '        ThreadPool.SetMinThreads(132, 132);\n        scopes.WarmThreads();')
        text = replace(text, '        multiplicity.CaptureStart();', '        scopes.CaptureStart();\n        multiplicity.CaptureStart();')
        text = replace(text, '        multiplicity.CaptureEnd();', '        multiplicity.CaptureEnd();\n        scopes.CaptureEnd();')
        text = replace(text, '            multiplicity = multiplicity.Report(operations),', '            scopes = scopes.Report(),\n            multiplicity = multiplicity.Report(operations),')
        program.write_text(text)
        probe = target/'MultiplicityProbe.cs'
        legacy = probe.read_text()
        legacy = replace(legacy, '        await Task.Delay(1000).ConfigureAwait(false);\n        const int count = 131072;', '        await Task.Delay(1000).ConfigureAwait(false);\n        // Warm the ThreadStatic owner-overlap branch on this post-await thread.\n        loop(32768);\n        const int count = 131072;')
        legacy = replace(legacy, '        long start = GC.GetAllocatedBytesForCurrentThread();', '        using var process = Process.GetCurrentProcess();\n        var cpuStart = process.TotalProcessorTime;\n        long ticks = Stopwatch.GetTimestamp();\n        long start = GC.GetAllocatedBytesForCurrentThread();')
        legacy = replace(legacy, '        long bytes = GC.GetAllocatedBytesForCurrentThread() - start;', '        long bytes = GC.GetAllocatedBytesForCurrentThread() - start;\n        long elapsed = Stopwatch.GetTimestamp() - ticks;\n        double cpuMs = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;')
        legacy = replace(legacy, '            runtime = Environment.Version.ToString(), operations = count, currentThreadBytes = bytes,', '            runtime = Environment.Version.ToString(), operations = count, currentThreadBytes = bytes,\n            processId = Environment.ProcessId, processStartUtc = process.StartTime.ToUniversalTime().ToString("O"),\n            elapsedTicks = elapsed, stopwatchFrequency = Stopwatch.Frequency, cpuMilliseconds = cpuMs,\n            nanosecondsPerIteration = elapsed * 1e9 / Stopwatch.Frequency / count, cpuNanosecondsPerIteration = cpuMs * 1e6 / count,')
        probe.write_text(legacy)
        (target/'ScopeProbe.cs').write_text((HERE/'ScopeProbe.cs.in').read_text())
        if variant == 'B':
            patches = []
            owner_path = 'src/SharpLink.Abstractions/Issue739MultiplicityCounters.cs'
            owner = dest/owner_path
            before = owner.read_text()
            after = replace(before, '    public static void Increment(int index) => Interlocked.Increment(ref Values[index]);', '''    [ThreadStatic] public static int ScopeDepth;
    public static long ScopePricedOwnerOverlap;
    public static void Increment(int index)
    {
        if (ScopeDepth != 0 && (index == operation_registration_accepted || index == logical_helper_entry || index == permit_plain_new || index == push_null_snapshot || index == restore_snapshot_null))
            Interlocked.Increment(ref ScopePricedOwnerOverlap);
        Interlocked.Increment(ref Values[index]);
    }''')
            owner.write_text(after)
            patches.extend(difflib.unified_diff(before.splitlines(True), after.splitlines(True), fromfile='a/'+owner_path, tofile='b/'+owner_path))
            for path, (old, new, count) in SITES.items():
                file = dest/path; before = file.read_text(); after = replace(before, old, new, count)
                file.write_text(after)
                patches.extend(difflib.unified_diff(before.splitlines(True), after.splitlines(True), fromfile='a/'+path, tofile='b/'+path))
            path = 'src/SharpLink.Runtime/Issue739ShmScopes.cs'
            text = (HERE/'ScopeCounters.cs.in').read_text(); (dest/path).write_text(text)
            patches.extend(difflib.unified_diff([], text.splitlines(True), fromfile='/dev/null', tofile='b/'+path))
            full = []
            originals = json.loads((MULTIPLICITY/'expected_hashes.json').read_text())
            paths = sorted({p for p in originals if p.startswith('src/')} | set(SITES))
            for path in paths:
                full.extend(difflib.unified_diff((ROOT/path).read_text().splitlines(True), (dest/path).read_text().splitlines(True), fromfile='a/'+path, tofile='b/'+path))
            for path in ['src/SharpLink.Abstractions/Issue739MultiplicityCounters.cs','src/SharpLink.Runtime/Issue739ShmScopes.cs']:
                full.extend(difflib.unified_diff([], (dest/path).read_text().splitlines(True), fromfile='/dev/null', tofile='b/'+path))
            (output/'B-source.patch').write_text(''.join(full))
            (output/'B-scope-only.patch').write_text(''.join(patches))
        files = list((dest/'src').rglob('*')) + list(target.glob('*.cs')) + list(target.glob('*.csproj'))
        files += [dest/name for name in ['Directory.Build.props', 'Directory.Packages.props', 'global.json', '.editorconfig']]
        manifest['variants'][variant]['files'] = {str(p.relative_to(dest)): sha(p) for p in files if p.is_file()}
        manifest['variants'][variant]['patchSha256'] = sha(output/f'{variant}-source.patch')
        manifest['variants'][variant]['role'] = 'vanilla-primary' if variant == 'A' else 'source-counter-plus-inclusive-initiation-scope-diagnostic'
        # Entire SHM async method implementation remains unchanged; only callers are patched.
        path = 'src/SharpLink.Runtime/Transport/SharedMemoryPipelines.cs'
        if sha(dest/path) != sha(ROOT/path): raise ValueError('SHM async body changed')
    manifest['scopeExpectedInputHashes'] = expected
    manifest['scopeSites'] = SITES
    (output/'projection-manifest.json').write_text(json.dumps(manifest, indent=2)+'\n')
    return variants
