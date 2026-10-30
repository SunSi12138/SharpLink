import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

// Only a local executable mock is launched. These tests never call a real simulator,
// .NET, MSBuild, networking, or a mobile runtime.
const launcher = fileURLToPath(new URL('./run-ios.mjs', import.meta.url));
const udid = '01234567-89AB-CDEF-0123-456789ABCDEF';
const bundle = 'com.sharplink.codeccompat.ios';
const envelope = {
    schemaVersion: 1,
    manifest: {
        schemaVersion: 1,
        platformTag: 'ios-x64-simulator-coreclr-net11',
        cases: [{ id: 'fixture-1', wireFile: 'cases/fixture-1.bin', category: 'semantic' }]
    },
    caseBytesBase64: { 'fixture-1': Buffer.from([0, 17, 255]).toString('base64') }
};
const verification = {
    schemaVersion: 1,
    consumer: { runtimeFamily: 'CoreCLR', platformTag: 'ios-x64-simulator-coreclr-net11' },
    results: [{ producer: 'linux-x64-hosted-desktop-coreclr-net10', blocking: false }]
};

const fakeXcrun = `#!/usr/bin/env node
const fs = require('node:fs');
const path = require('node:path');
const { spawn } = require('node:child_process');
const args = process.argv.slice(2);
fs.appendFileSync(process.env.FAKE_COMMAND_LOG, JSON.stringify({ args, mode: process.env.SIMCTL_CHILD_SHARPLINK_MODE ?? null }) + '\\n');
if (args[0] !== 'simctl') process.exit(91);
const command = args[1];
const scenario = process.env.FAKE_SCENARIO;
const documents = path.join(process.env.FAKE_CONTAINER, 'Documents');
fs.mkdirSync(documents, { recursive: true });
if (command === 'get_app_container') {
    if (scenario === 'container-timeout') {
        process.stderr.write('RAW_CONTAINER_TIMEOUT\\n');
        process.on('SIGTERM', () => {});
        setInterval(() => {}, 1000);
    } else {
        process.stdout.write(process.env.FAKE_CONTAINER + '\\n');
    }
} else if (command === 'launch') {
    process.stdout.write('RAW_LAUNCH_STDOUT\\n');
    process.stderr.write('RAW_LAUNCH_STDERR\\n');
    if (scenario === 'launch-failure' || scenario === 'primary-and-cleanup-failure') process.exit(17);
    if (scenario === 'launch-orphan') {
        const child = spawn(process.execPath, ['-e', "const fs=require('node:fs');process.on('SIGTERM',()=>{});setInterval(()=>fs.appendFileSync(process.env.FAKE_HEARTBEAT,'x'),20)"], { stdio: 'ignore' });
        fs.writeFileSync(process.env.FAKE_CHILD_PID, String(child.pid));
        child.unref();
        setTimeout(() => process.exit(0), 200);
    }
    if (scenario === 'launch-timeout' || scenario === 'launch-timeout-child') {
        if (scenario === 'launch-timeout-child') {
            const child = spawn(process.execPath, ['-e', "const fs=require('node:fs');process.on('SIGTERM',()=>{});setInterval(()=>fs.appendFileSync(process.env.FAKE_HEARTBEAT,'x'),20)"], { stdio: 'ignore' });
            fs.writeFileSync(process.env.FAKE_CHILD_PID, String(child.pid));
        }
        process.on('SIGTERM', () => {});
        setInterval(() => {}, 1000);
    } else {
        if (scenario !== 'missing-result') {
            const payload = process.env.SIMCTL_CHILD_SHARPLINK_MODE === 'verify'
                ? JSON.parse(process.env.FAKE_VERIFICATION)
                : JSON.parse(process.env.FAKE_ENVELOPE);
            if (scenario === 'malformed-result') {
                fs.writeFileSync(path.join(documents, 'sharplink-result.json'), 'NOT_VALID_JSON');
            } else if (scenario === 'portable-error') {
                fs.writeFileSync(path.join(documents, 'sharplink-result.json'), JSON.stringify({ portableProbeError: 'ORIGINAL_PROBE_FAILURE' }));
            } else {
                fs.writeFileSync(path.join(documents, 'sharplink-result.json'), JSON.stringify(payload));
            }
        }
        process.stdout.write('com.sharplink.codeccompat.ios: 12345\\n');
    }
} else if (command === 'terminate') {
    process.stdout.write('RAW_CLEANUP_STDOUT\\n');
    process.stderr.write('RAW_CLEANUP_STDERR\\n');
    if (scenario === 'cleanup-failure' || scenario === 'primary-and-cleanup-failure') process.exit(23);
    if (scenario === 'cleanup-timeout') {
        process.on('SIGTERM', () => {});
        setInterval(() => {}, 1000);
    }
} else if (command === 'list' || command === 'spawn') {
    process.stdout.write('MOCK_DIAGNOSTIC\\n');
} else {
    process.stderr.write('Unexpected mock command: ' + args.join(' ') + '\\n');
    process.exit(92);
}
`;

async function setup(t, scenario = 'success', overrides = {}) {
    const root = await fs.mkdtemp(path.join(os.tmpdir(), 'sharplink-ios-launcher-test-'));
    t.after(() => fs.rm(root, { recursive: true, force: true }));
    const bin = path.join(root, 'bin');
    const container = path.join(root, 'container');
    const diagnostics = path.join(root, 'diagnostics');
    await fs.mkdir(bin);
    await fs.mkdir(path.join(container, 'Documents'), { recursive: true });
    await fs.writeFile(path.join(bin, 'xcrun'), fakeXcrun, { mode: 0o755 });
    const env = {
        ...process.env,
        PATH: `${bin}${path.delimiter}${process.env.PATH ?? ''}`,
        IOS_SIMULATOR_UDID: udid,
        SHARPLINK_IOS_DIAGNOSTICS: diagnostics,
        SHARPLINK_IOS_PROBE_TIMEOUT_MS: '100',
        SHARPLINK_IOS_COMMAND_TIMEOUT_MS: '1000',
        SHARPLINK_IOS_CLEANUP_TIMEOUT_MS: '1000',
        FAKE_SCENARIO: scenario,
        FAKE_CONTAINER: container,
        FAKE_COMMAND_LOG: path.join(root, 'commands.jsonl'),
        FAKE_HEARTBEAT: path.join(root, 'heartbeat'),
        FAKE_CHILD_PID: path.join(root, 'child.pid'),
        FAKE_ENVELOPE: JSON.stringify(envelope),
        FAKE_VERIFICATION: JSON.stringify(verification),
        ...overrides
    };
    delete env.SHARPLINK_IOS_PROBE_ATTEMPTS;
    if (Object.hasOwn(overrides, 'SHARPLINK_IOS_PROBE_ATTEMPTS')) env.SHARPLINK_IOS_PROBE_ATTEMPTS = overrides.SHARPLINK_IOS_PROBE_ATTEMPTS;
    return { root, container, diagnostics, env, output: path.join(root, 'corpus') };
}

async function run(context, mode = 'produce') {
    let args;
    if (mode === 'verify') {
        const producerRoot = path.join(context.root, 'producers');
        await fs.mkdir(path.join(producerRoot, 'desktop', 'cases'), { recursive: true });
        await fs.writeFile(path.join(producerRoot, 'desktop', 'manifest.json'), JSON.stringify(envelope.manifest));
        await fs.writeFile(path.join(producerRoot, 'desktop', 'cases', 'fixture-1.bin'), Buffer.from([0, 17, 255]));
        context.output = path.join(context.root, 'verification.json');
        args = ['verify', producerRoot, context.output, 'mock-commit', 'mock-sdk', 'net11.0-ios/iossimulator-x64'];
    } else {
        args = ['produce', context.output, 'mock-commit', 'mock-sdk', 'net11.0-ios/iossimulator-x64'];
    }
    const started = Date.now();
    return await new Promise((resolve, reject) => {
        const child = spawn(process.execPath, [launcher, ...args], {
            cwd: context.root, env: context.env, detached: process.platform !== 'win32', stdio: ['ignore', 'pipe', 'pipe']
        });
        let stdout = '';
        let stderr = '';
        const killGroup = () => {
            try { process.kill(process.platform === 'win32' ? child.pid : -child.pid, 'SIGKILL'); } catch {}
        };
        const watchdog = setTimeout(() => { killGroup(); reject(new Error(`Mock launcher exceeded 10s watchdog. stdout=${stdout} stderr=${stderr}`)); }, 10_000);
        let interruptionSent = false;
        child.stdout.on('data', chunk => {
            stdout += chunk;
            if (context.interruptWhenLaunched && !interruptionSent && stdout.includes('iOS simulator launch attempt')) {
                interruptionSent = true;
                setTimeout(() => child.kill('SIGTERM'), 50);
            }
        });
        child.stderr.on('data', chunk => { stderr += chunk; });
        child.on('error', error => { clearTimeout(watchdog); reject(error); });
        child.on('close', (code, signal) => {
            clearTimeout(watchdog);
            resolve({ code, signal, stdout, stderr, elapsedMs: Date.now() - started });
        });
    });
}

async function commands(context) {
    let text;
    try { text = await fs.readFile(context.env.FAKE_COMMAND_LOG, 'utf8'); }
    catch (error) { if (error.code === 'ENOENT') return []; throw error; }
    return text.trim().split('\n').filter(Boolean).map(line => JSON.parse(line));
}

async function allEvidence(directory) {
    let text = '';
    let entries;
    try { entries = await fs.readdir(directory, { withFileTypes: true }); }
    catch (error) { if (error.code === 'ENOENT') return ''; throw error; }
    for (const entry of entries) {
        const full = path.join(directory, entry.name);
        text += entry.isDirectory() ? await allEvidence(full) : await fs.readFile(full, 'utf8');
    }
    return text;
}

function assertOneLaunch(rows) {
    assert.equal(rows.filter(row => row.args[1] === 'launch').length, 1, 'exactly one launch; no retry');
    assert.ok(rows.every(row => !row.args.includes('booted')), 'no ambient booted selector');
    for (const row of rows.filter(row => ['launch', 'terminate', 'get_app_container', 'spawn'].includes(row.args[1]))) {
        assert.ok(row.args.includes(udid), `explicit chosen UDID missing: ${row.args.join(' ')}`);
    }
}

for (const mode of ['produce', 'verify']) {
    test(`${mode}: success preserves output and uses one launch with the exact UDID`, async t => {
        const context = await setup(t);
        const result = await run(context, mode);
        assert.equal(result.code, 0, result.stdout + result.stderr);
        const rows = await commands(context);
        assertOneLaunch(rows);
        assert.equal(rows.filter(row => row.args[1] === 'terminate').length, 1, 'single final cleanup, no speculative terminate');
        if (mode === 'produce') {
            assert.deepEqual(JSON.parse(await fs.readFile(path.join(context.output, 'manifest.json'), 'utf8')), envelope.manifest);
            assert.deepEqual(await fs.readFile(path.join(context.output, 'cases', 'fixture-1.bin')), Buffer.from([0, 17, 255]));
        } else {
            assert.deepEqual(JSON.parse(await fs.readFile(context.output, 'utf8')), verification);
        }
    });
}

test('nonzero launch preserves raw stdout/stderr, fails, cleans up, and never retries', async t => {
    const context = await setup(t, 'launch-failure');
    const result = await run(context);
    assert.notEqual(result.code, 0);
    const rows = await commands(context);
    assertOneLaunch(rows);
    assert.equal(rows.filter(row => row.args[1] === 'terminate').length, 1);
    const evidence = result.stdout + result.stderr + await allEvidence(context.diagnostics);
    assert.match(evidence, /RAW_LAUNCH_STDOUT/);
    assert.match(evidence, /RAW_LAUNCH_STDERR/);
    assert.match(evidence, /17/);
});

test('launch timeout is bounded, preserves failure, cleans up, and never retries', async t => {
    const context = await setup(t, 'launch-timeout', { SHARPLINK_IOS_COMMAND_TIMEOUT_MS: '300' });
    const result = await run(context);
    assert.notEqual(result.code, 0);
    assert.ok(result.elapsedMs < 9_000);
    const rows = await commands(context);
    assertOneLaunch(rows);
    assert.equal(rows.filter(row => row.args[1] === 'terminate').length, 1);
    const evidence = result.stdout + result.stderr + await allEvidence(context.diagnostics);
    assert.match(evidence, /RAW_LAUNCH_STDERR/);
    assert.match(evidence, /timeout|timed out|deadline/i);
});

test('timeout terminates a spawned descendant rather than leaving background work alive', async t => {
    const context = await setup(t, 'launch-timeout-child', { SHARPLINK_IOS_COMMAND_TIMEOUT_MS: '800' });
    const result = await run(context);
    assert.notEqual(result.code, 0);
    assertOneLaunch(await commands(context));
    const pid = Number(await fs.readFile(context.env.FAKE_CHILD_PID, 'utf8'));
    t.after(() => { try { process.kill(pid, 'SIGKILL'); } catch {} });
    const before = await fs.readFile(context.env.FAKE_HEARTBEAT, 'utf8');
    assert.ok(before.length > 0, 'descendant actually ran');
    await new Promise(resolve => setTimeout(resolve, 150));
    assert.equal(await fs.readFile(context.env.FAKE_HEARTBEAT, 'utf8'), before, 'no descendant heartbeat after timeout cleanup');
});

test('missing result fails after one bounded probe and does not launch again', async t => {
    const context = await setup(t, 'missing-result');
    const result = await run(context);
    assert.notEqual(result.code, 0);
    assertOneLaunch(await commands(context));
    assert.match(result.stdout + result.stderr + await allEvidence(context.diagnostics), /timeout|timed out|deadline/i);
});

for (const scenario of ['cleanup-failure', 'cleanup-timeout']) {
    test(`${scenario}: successful producer output cannot turn failed cleanup into success`, async t => {
        const context = await setup(t, scenario, { SHARPLINK_IOS_CLEANUP_TIMEOUT_MS: '300' });
        const result = await run(context);
        assert.notEqual(result.code, 0);
        assertOneLaunch(await commands(context));
        const evidence = result.stdout + result.stderr + await allEvidence(context.diagnostics);
        assert.match(evidence, /RAW_CLEANUP_STDOUT/);
        assert.match(evidence, /RAW_CLEANUP_STDERR/);
        assert.match(evidence, scenario === 'cleanup-failure' ? /23/ : /timeout|timed out|deadline/i);
    });
}

test('primary failure and cleanup failure are both retained', async t => {
    const context = await setup(t, 'primary-and-cleanup-failure');
    const result = await run(context);
    assert.notEqual(result.code, 0);
    assertOneLaunch(await commands(context));
    const evidence = result.stdout + result.stderr + await allEvidence(context.diagnostics);
    assert.match(evidence, /RAW_LAUNCH_STDERR/);
    assert.match(evidence, /RAW_CLEANUP_STDERR/);
    assert.match(evidence, /17/);
    assert.match(evidence, /23/);
});

test('portable probe failure remains a failed sample and is never retried', async t => {
    const context = await setup(t, 'portable-error');
    const result = await run(context);
    assert.notEqual(result.code, 0);
    assertOneLaunch(await commands(context));
    const evidence = result.stdout + result.stderr + await allEvidence(context.diagnostics);
    assert.match(evidence, /ORIGINAL_PROBE_FAILURE/);
});

test('stale result bytes are retained as evidence instead of silently overwritten or reused', async t => {
    const context = await setup(t);
    await fs.writeFile(path.join(context.container, 'Documents', 'sharplink-result.json'), 'ORIGINAL_STALE_RESULT');
    const result = await run(context);
    assert.equal(result.code, 0, result.stdout + result.stderr);
    assertOneLaunch(await commands(context));
    assert.match(await allEvidence(context.diagnostics), /ORIGINAL_STALE_RESULT/);
});

for (const [name, overrides] of [
    ['missing UDID', { IOS_SIMULATOR_UDID: '' }],
    ['ambient UDID selector', { IOS_SIMULATOR_UDID: 'booted' }],
    ['retry attempts', { SHARPLINK_IOS_PROBE_ATTEMPTS: '2' }],
    ['probe threshold extension', { SHARPLINK_IOS_PROBE_TIMEOUT_MS: '300001' }],
    ['command threshold extension', { SHARPLINK_IOS_COMMAND_TIMEOUT_MS: '90001' }],
    ['cleanup threshold extension', { SHARPLINK_IOS_CLEANUP_TIMEOUT_MS: '15001' }],
    ['invalid timeout', { SHARPLINK_IOS_PROBE_TIMEOUT_MS: 'NaN' }]
]) {
    test(`reject ${name} before invoking any simulator command`, async t => {
        const context = await setup(t, 'success', overrides);
        const result = await run(context);
        assert.notEqual(result.code, 0);
        assert.deepEqual(await commands(context), []);
    });
}


test('exit zero with lingering descendants is a failure and the process group is cleaned up', async t => {
    const context = await setup(t, 'launch-orphan');
    const result = await run(context);
    assert.notEqual(result.code, 0, 'a zero root exit cannot hide owned background work');
    assertOneLaunch(await commands(context));
    const pid = Number(await fs.readFile(context.env.FAKE_CHILD_PID, 'utf8'));
    t.after(() => { try { process.kill(pid, 'SIGKILL'); } catch {} });
    const before = await fs.readFile(context.env.FAKE_HEARTBEAT, 'utf8');
    assert.ok(before.length > 0, 'descendant actually ran');
    await new Promise(resolve => setTimeout(resolve, 150));
    assert.equal(await fs.readFile(context.env.FAKE_HEARTBEAT, 'utf8'), before);
    assert.match(await allEvidence(context.diagnostics), /"lingeringDescendants": true/);
});

test('container lookup is bounded and cannot fall through to a launch or retry', async t => {
    const context = await setup(t, 'container-timeout', { SHARPLINK_IOS_COMMAND_TIMEOUT_MS: '300' });
    const result = await run(context);
    assert.notEqual(result.code, 0);
    const rows = await commands(context);
    assert.equal(rows.filter(row => row.args[1] === 'get_app_container').length, 1);
    assert.equal(rows.filter(row => row.args[1] === 'launch').length, 0);
    assert.match(await allEvidence(context.diagnostics), /RAW_CONTAINER_TIMEOUT/);
});

test('malformed result is retained verbatim and fails without retry', async t => {
    const context = await setup(t, 'malformed-result');
    const result = await run(context);
    assert.notEqual(result.code, 0);
    assertOneLaunch(await commands(context));
    assert.match(await allEvidence(context.diagnostics), /NOT_VALID_JSON/);
});

test('consumer blocking results remain blocking and the original report is preserved', async t => {
    const blocked = { ...verification, results: [{ producer: 'linux-x64-hosted-desktop-coreclr-net10', blocking: true }] };
    const context = await setup(t, 'success', { FAKE_VERIFICATION: JSON.stringify(blocked) });
    const result = await run(context, 'verify');
    assert.notEqual(result.code, 0);
    assertOneLaunch(await commands(context));
    assert.deepEqual(JSON.parse(await fs.readFile(context.output, 'utf8')), blocked);
    assert.match(await allEvidence(context.diagnostics), /"blocking":true/);
});


test('SIGTERM while awaiting a result becomes a recorded failure and still runs bounded cleanup', async t => {
    const context = await setup(t, 'missing-result', { SHARPLINK_IOS_PROBE_TIMEOUT_MS: '5000' });
    context.interruptWhenLaunched = true;
    const result = await run(context);
    assert.notEqual(result.code, 0);
    assert.equal(result.signal, null, 'launcher handles interruption instead of bypassing cleanup');
    assertOneLaunch(await commands(context));
    assert.equal((await commands(context)).filter(row => row.args[1] === 'terminate').length, 1);
    assert.match(result.stdout + result.stderr + await allEvidence(context.diagnostics), /interrupt|SIGTERM/i);
    assert.ok(result.elapsedMs < 3000, 'interruption is noticed without waiting out the full probe deadline');
});
