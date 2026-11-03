"""Instrument only existing await boundaries in an exact disposable main checkout."""
import hashlib
import json
from pathlib import Path

BASE = '9d1635c8988870ee295098fe9e65cb49924013d2'
METHOD = 'Api4DynamicStreamExitShouldReleaseItsCollectibleContext'
REL = 'test/SharpLink.IntegrationTests/'


def require(value, reason):
    if not value:
        raise ValueError(reason)


def replace_once(text, old, new):
    require(text.count(old) == 1, 'Ambiguous instrumentation anchor')
    return text.replace(old, new, 1)


def transform(name, text):
    if name == 'RuntimeAssemblyIntegrationTests.cs':
        start = text.index('    private static async Task<WeakReference> ExecuteDynamicStreamExitAndUnloadAsync(')
        end = text.index('\n    [MethodImpl', start)
        body = text[start:end]
        pairs = [
            ('        await using var harness', '        StreamExitStage(10);\n        await using var harness'),
            ('        using var plugin', '        StreamExitStage(11);\n        using var plugin'),
            ('        object? proxy', '        StreamExitStage(12);\n        object? proxy'),
            ('            Ensure((await CollectAsync', '            StreamExitStage(20);\n            Ensure((await CollectAsync'),
            ('                "normal API 4 dynamic stream completes");', '                "normal API 4 dynamic stream completes");\n            StreamExitStage(21);'),
            ('            Ensure(await enumerator.MoveNextAsync(),', '            StreamExitStage(30);\n            Ensure(await enumerator.MoveNextAsync(),'),
            ('                $"API 4 dynamic stream \'{exitMode}\' starts before exit");', '                $"API 4 dynamic stream \'{exitMode}\' starts before exit");\n            StreamExitStage(31);'),
            ('                    $"unknown dynamic stream exit mode \'{exitMode}\'");', '                    $"unknown dynamic stream exit mode \'{exitMode}\'");\n            StreamExitStage(50);'),
            ('        if (!string.Equals(exitMode, "cancellation-before-first", StringComparison.Ordinal))', '        StreamExitStage(51); // The original await-using DisposeAsync completed.\n        if (!string.Equals(exitMode, "cancellation-before-first", StringComparison.Ordinal))'),
            ('            await plugin.GetStaticTask("ServerStreamDisposed");', '            StreamExitStage(60);\n            await plugin.GetStaticTask("ServerStreamDisposed");\n            StreamExitStage(61);'),
            ('        var service = await', '        StreamExitStage(70);\n        var service = await'),
            ('        var serverContract = await', '        StreamExitStage(71);\n        var serverContract = await'),
            ('        var clientContract = await', '        StreamExitStage(72);\n        var clientContract = await'),
            ('        Ensure(serverContract.ReferencesReleased', '        StreamExitStage(73);\n        Ensure(serverContract.ReferencesReleased'),
            ('        return plugin.Unload();', '        StreamExitStage(79);\n        return plugin.Unload();')]
        for old, new in pairs:
            body = replace_once(body, old, new)
        return text[:start] + body + text[end:]
    if name == 'RuntimeAssemblyIntegrationTests.Support.cs':
        start = text.index('    private sealed class DynamicHarness')
        body = text[start:]
        for old, new in [
            ('            await Client.StopAsync();', '            StreamExitStage(90);\n            await Client.StopAsync();\n            StreamExitStage(91);'),
            ('            await _serverCancellation.CancelAsync();', '            StreamExitStage(92);\n            await _serverCancellation.CancelAsync();\n            StreamExitStage(93);'),
            ('            _serverCancellation.Dispose();', '            StreamExitStage(94);\n            _serverCancellation.Dispose();'),
            ('            await _serviceProvider.DisposeAsync();', '            await _serviceProvider.DisposeAsync();\n            StreamExitStage(95);')]:
            body = replace_once(body, old, new)
        return text[:start] + body
    if name == 'RuntimeAssemblyIntegrationTests.RegistrationAndReplacement.cs':
        start = text.index('    public async Task ' + METHOD)
        end = text.index('\n    [Test]', start)
        body = text[start:end]
        for old, new in [
            ('        for (var attempt', '        StreamExitStage(100); // Helper return includes original plugin/harness cleanup.\n        for (var attempt'),
            ('            GC.Collect();\n            GC.WaitForPendingFinalizers();', '            StreamExitStage(101);\n            GC.Collect();\n            GC.WaitForPendingFinalizers();'),
            ('            await Task.Delay(20);', '            await Task.Delay(20);\n            StreamExitStage(102);'),
            ('        Ensure(!weakContext.IsAlive,', '        StreamExitStage(103);\n        Ensure(!weakContext.IsAlive,'),
            ('            $"API 4 dynamic stream \'{exitMode}\' must not retain its collectible ALC");', '            $"API 4 dynamic stream \'{exitMode}\' must not retain its collectible ALC");\n        StreamExitStage(104);')]:
            body = replace_once(body, old, new)
        return text[:start] + body + text[end:]
    raise ValueError('Unknown instrumented source')


def prepare(root, kit):
    pins = json.loads((kit / 'SOURCE.json').read_text())
    for relative, expected in pins['mainPins'].items():
        p = root / relative
        require(p.is_file() and not p.is_symlink() and hashlib.sha256(p.read_bytes()).hexdigest() == expected,
                'Exact main preimage mismatch')
    target = root / REL / 'RuntimeAssemblyIntegrationTests.StreamExitStage.cs'
    require(not target.exists() and not target.is_symlink(), 'New stage source already exists')
    for name in pins['instrumented']:
        p = root / REL / name
        text = transform(name, p.read_text())
        require(hashlib.sha256(text.encode()).hexdigest() == pins['after'][name], 'Unexpected instrumentation postimage')
        p.write_text(text)
    target.write_bytes((kit / 'StreamExitStage.cs').read_bytes())
