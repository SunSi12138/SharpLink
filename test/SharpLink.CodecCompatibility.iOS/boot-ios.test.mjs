import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const boot = fileURLToPath(new URL('./boot-ios.mjs', import.meta.url));
const selected = '01234567-89AB-CDEF-0123-456789ABCDEF';
const other = '11111111-2222-3333-4444-555555555555';
const fake = `#!/usr/bin/env node
const fs = require('node:fs');
const args = process.argv.slice(2);
fs.appendFileSync(process.env.FAKE_COMMAND_LOG, JSON.stringify(args) + '\\n');
const delayMs = Number(process.env['FAKE_' + String(args[1]).toUpperCase() + '_DELAY_MS'] || 0);
if (delayMs) Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, delayMs);
if (args[0] !== 'simctl') process.exit(90);
if (args[1] === 'list') {
    process.stdout.write(JSON.stringify({ devices: {
        'com.apple.CoreSimulator.SimRuntime.tvOS-26-0': [{ isAvailable: true, name: 'iPhone ignored runtime', udid: process.env.OTHER_UDID, state: 'Shutdown' }],
        'com.apple.CoreSimulator.SimRuntime.iOS-26-0': process.env.FAKE_SCENARIO === 'no-available' ? [] : [
            { isAvailable: true, name: 'iPad Pro', udid: process.env.OTHER_UDID, state: 'Shutdown' },
            { isAvailable: false, name: 'iPhone unavailable', udid: process.env.OTHER_UDID, state: 'Shutdown' },
            { isAvailable: true, name: 'iPhone chosen', udid: process.env.SELECTED_UDID, state: ['shutdown-failure', 'already-booted'].includes(process.env.FAKE_SCENARIO) ? 'Booted' : 'Shutdown' },
            { isAvailable: true, name: 'iPhone later', udid: process.env.OTHER_UDID, state: 'Shutdown' }
        ]
    } }));
} else if (args[1] === 'shutdown' && process.env.FAKE_SCENARIO === 'shutdown-failure') {
    process.stderr.write('ORIGINAL_SHUTDOWN_FAILURE\\n');
    process.exit(26);
} else if (args[1] === 'bootstatus' && process.env.FAKE_SCENARIO === 'ready-failure') {
    process.stderr.write('ORIGINAL_READY_FAILURE\\n');
    process.exit(27);
} else if (!['shutdown', 'boot', 'bootstatus'].includes(args[1])) {
    process.exit(91);
}
`;

async function exercise(t, scenario, overrides = {}, preload = null) {
    const root = await fs.mkdtemp(path.join(os.tmpdir(), 'sharplink-ios-boot-test-'));
    t.after(() => fs.rm(root, { recursive: true, force: true }));
    const bin = path.join(root, 'bin');
    await fs.mkdir(bin);
    await fs.writeFile(path.join(bin, 'xcrun'), fake, { mode: 0o755 });
    const env = {
        ...process.env, PATH: `${bin}${path.delimiter}${process.env.PATH ?? ''}`,
        SHARPLINK_IOS_DIAGNOSTICS: path.join(root, 'diagnostics'),
        GITHUB_ENV: path.join(root, 'github-env'),
        FAKE_COMMAND_LOG: path.join(root, 'commands'),
        FAKE_SCENARIO: scenario, SELECTED_UDID: selected, OTHER_UDID: other
    };
    delete env.SHARPLINK_IOS_EXECUTION_DEADLINE_MS;
    delete env.SHARPLINK_IOS_PREP_TIMEOUT_MS;
    Object.assign(env, overrides);
    const nodeArgs = [];
    if (preload) { const hook = path.join(root, 'hook.mjs'); await fs.writeFile(hook, preload); nodeArgs.push('--import', hook); }
    nodeArgs.push(boot);
    const result = await new Promise((resolve, reject) => {
        const child = spawn(process.execPath, nodeArgs, { cwd: root, env, stdio: ['ignore', 'pipe', 'pipe'] });
        let stdout = '', stderr = '';
        const timer = setTimeout(() => { child.kill('SIGTERM'); reject(new Error('Boot mock exceeded watchdog')); }, 5000);
        child.stdout.on('data', data => { stdout += data; });
        child.stderr.on('data', data => { stderr += data; });
        child.on('error', error => { clearTimeout(timer); reject(error); });
        child.on('close', code => { clearTimeout(timer); resolve({ code, stdout, stderr }); });
    });
    let commands = [];
    try { commands = (await fs.readFile(env.FAKE_COMMAND_LOG, 'utf8')).trim().split('\n').filter(Boolean).map(line => JSON.parse(line)); }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
    let githubEnv = '';
    try { githubEnv = await fs.readFile(env.GITHUB_ENV, 'utf8'); }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
    const files = await fs.readdir(env.SHARPLINK_IOS_DIAGNOSTICS).catch(error => { if (error.code === 'ENOENT') return []; throw error; });
    const records = await Promise.all(files.filter(name => name.endsWith('.json')).map(async name => JSON.parse(await fs.readFile(path.join(env.SHARPLINK_IOS_DIAGNOSTICS, name), 'utf8'))));
    let phaseEvents = [];
    try { phaseEvents = (await fs.readFile(path.join(env.SHARPLINK_IOS_DIAGNOSTICS, 'preparation-events.jsonl'), 'utf8')).trim().split('\n').map(line => JSON.parse(line)); }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
    return { ...result, commands, githubEnv, records, phaseEvents };
}

test('boot selects the first available iPhone, waits for readiness once, and hands off exactly its UDID', async t => {
    const result = await exercise(t, 'success');
    assert.equal(result.code, 0, result.stderr);
    assert.deepEqual(result.commands, [
        ['simctl', 'list', 'devices', 'available', '-j'],
        ['simctl', 'boot', selected],
        ['simctl', 'bootstatus', selected, '-b']
    ]);
    assert.equal(result.githubEnv, `IOS_SIMULATOR_UDID=${selected}\n`);
    const ordered = ['simulator-list', 'simulator-boot', 'simulator-ready'].map(label => result.records.find(record => record.label === label));
    assert.ok(ordered[0].timeoutMs > 470000 && ordered[0].timeoutMs < 480000, 'list receives the shared phase remainder, not the former 15-second cap');
    assert.ok(ordered[1].timeoutMs > 470000, 'boot is not restricted to the former 90-second cap');
    for (let index = 1; index < ordered.length; index++) {
        assert.ok(ordered[index].timeoutMs > 0 && ordered[index].timeoutMs < ordered[index - 1].timeoutMs, 'every phase command consumes one continuously decreasing budget');
    }
    assert.equal(new Set(ordered.map(record => record.monotonicDeadlineMs)).size, 1, 'one absolute monotonic deadline, never reset per command');
    assert.equal(new Set(ordered.map(record => record.effectiveDeadlineMs)).size, 1, 'one absolute wall deadline, never reset per command');
    assert.ok(result.records.every(record => record.status === 'success'));
});

test('shutdown failure is retained and prevents boot and environment handoff', async t => {
    const result = await exercise(t, 'shutdown-failure');
    assert.notEqual(result.code, 0);
    assert.deepEqual(result.commands, [
        ['simctl', 'list', 'devices', 'available', '-j'], ['simctl', 'shutdown', selected]
    ]);
    assert.equal(result.githubEnv, '');
    assert.match(result.stderr, /ORIGINAL_SHUTDOWN_FAILURE/);
    assert.equal(result.records.find(record => record.label === 'simulator-shutdown').exitCode, 26);
});

test('boot readiness failure is not swallowed or retried and does not publish the UDID', async t => {
    const result = await exercise(t, 'ready-failure');
    assert.notEqual(result.code, 0);
    assert.equal(result.commands.filter(command => command[1] === 'boot').length, 1);
    assert.equal(result.commands.filter(command => command[1] === 'bootstatus').length, 1);
    assert.equal(result.githubEnv, '');
    assert.match(result.stderr, /ORIGINAL_READY_FAILURE/);
    assert.equal(result.records.find(record => record.label === 'simulator-ready').exitCode, 27);
});

test('no available iPhone fails without booting an ambient simulator', async t => {
    const result = await exercise(t, 'no-available');
    assert.notEqual(result.code, 0);
    assert.deepEqual(result.commands, [['simctl', 'list', 'devices', 'available', '-j']]);
    assert.equal(result.githubEnv, '');
    assert.match(result.stderr, /No available iPhone simulator/);
});


test('one preparation budget is shared across list and boot rather than renewed per command', async t => {
    const result = await exercise(t, 'success', {
        SHARPLINK_IOS_PREP_TIMEOUT_MS: '450', FAKE_LIST_DELAY_MS: '180', FAKE_BOOT_DELAY_MS: '300'
    });
    assert.notEqual(result.code, 0);
    assert.equal(result.githubEnv, '');
    assert.equal(result.commands.filter(command => command[1] === 'bootstatus').length, 0);
    const list = result.records.find(record => record.label === 'simulator-list');
    const bootRecord = result.records.find(record => record.label === 'simulator-boot');
    assert.equal(list.status, 'success');
    assert.ok(bootRecord && bootRecord.timeoutMs < list.timeoutMs);
    assert.equal(bootRecord.status, 'failure');
});

test('exhausting preparation during list never starts boot or publishes a UDID', async t => {
    const result = await exercise(t, 'success', { SHARPLINK_IOS_PREP_TIMEOUT_MS: '200', FAKE_LIST_DELAY_MS: '500' });
    assert.notEqual(result.code, 0);
    assert.equal(result.githubEnv, '');
    assert.equal(result.records.length, 1);
    assert.equal(result.records[0].label, 'simulator-list');
    assert.equal(result.records[0].status, 'failure');
    assert.ok(result.commands.every(command => command[1] === 'list'));
});

test('the caller deadline wins when earlier than the preparation phase allowance', async t => {
    const result = await exercise(t, 'success', {
        SHARPLINK_IOS_PREP_TIMEOUT_MS: '1000',
        SHARPLINK_IOS_EXECUTION_DEADLINE_MS: String(Date.now() + 300), FAKE_LIST_DELAY_MS: '500'
    });
    assert.notEqual(result.code, 0);
    assert.equal(result.githubEnv, '');
    assert.equal(result.records.length, 1);
    assert.ok(result.records[0].timeoutMs <= 300);
    assert.equal(result.records[0].status, 'failure');
});

test('expired caller budget fails before any simulator command or handoff', async t => {
    const result = await exercise(t, 'success', { SHARPLINK_IOS_EXECUTION_DEADLINE_MS: String(Date.now() - 1) });
    assert.notEqual(result.code, 0);
    assert.deepEqual(result.commands, []);
    assert.equal(result.githubEnv, '');
    assert.match(result.stderr, /caller execution deadline exhausted/);
});

for (const value of ['480001', 'NaN']) {
    test(`preparation override ${value} cannot extend or invalidate the phase budget`, async t => {
        const result = await exercise(t, 'success', { SHARPLINK_IOS_PREP_TIMEOUT_MS: value });
        assert.notEqual(result.code, 0);
        assert.deepEqual(result.commands, []);
        assert.equal(result.githubEnv, '');
        assert.match(result.stderr, /SHARPLINK_IOS_PREP_TIMEOUT_MS/);
    });
}


test('the budget is checked again immediately before the environment handoff', async t => {
    const result = await exercise(t, 'success', { SHARPLINK_IOS_PREP_TIMEOUT_MS: '1200' }, `
        import fs from 'node:fs';
        const append = fs.appendFileSync;
        fs.appendFileSync = function(file, data, ...args) {
            if (String(file).endsWith('preparation-events.jsonl') && String(data).includes('handoff-write-start')) {
                const end = Date.now() + 1300; while (Date.now() < end) {}
            }
            return append.call(fs, file, data, ...args);
        };
    `);
    assert.notEqual(result.code, 0);
    assert.equal(result.githubEnv, '');
    assert.equal(result.phaseEvents.at(-1).type, 'phase-failure');
    assert.equal(result.phaseEvents.at(-1).handoffWritten, false);
    assert.equal(result.commands.filter(command => command[1] === 'bootstatus').length, 1);
});

test('a late filesystem handoff is retained as failure, not falsely claimed unwritten or successful', async t => {
    const result = await exercise(t, 'success', { SHARPLINK_IOS_PREP_TIMEOUT_MS: '1200' }, `
        import fs from 'node:fs/promises';
        const append = fs.appendFile;
        fs.appendFile = async function(file, ...args) {
            if (String(file).endsWith('github-env')) await new Promise(resolve => setTimeout(resolve, 1300));
            return append.call(fs, file, ...args);
        };
    `);
    assert.notEqual(result.code, 0);
    assert.equal(result.githubEnv, `IOS_SIMULATOR_UDID=${selected}\n`);
    assert.equal(result.phaseEvents.at(-1).type, 'phase-failure');
    assert.equal(result.phaseEvents.at(-1).handoffWritten, true);
    assert.equal(result.phaseEvents.at(-1).lateHandoffFailure, true);
    assert.equal(result.phaseEvents.at(-1).deadlineScope, 'simulator-preparation');
});


test('metadata delay plus wall-clock rollback cannot spawn after the shared monotonic phase expires', async t => {
    const result = await exercise(t, 'success', { SHARPLINK_IOS_PREP_TIMEOUT_MS: '150' }, `
        import fs from 'node:fs';
        const write = fs.writeFileSync, now = Date.now;
        let delayed = false;
        fs.writeFileSync = function(file, ...args) {
            if (!delayed && String(file).endsWith('-simulator-list.json')) {
                delayed = true;
                const end = performance.now() + 250; while (performance.now() < end) {}
                Date.now = () => now() - 1000;
            }
            return write.call(fs, file, ...args);
        };
    `);
    assert.notEqual(result.code, 0);
    assert.deepEqual(result.commands, []);
    assert.equal(result.githubEnv, '');
    assert.equal(result.records.length, 1);
    assert.equal(result.records[0].pid, null);
    assert.equal(result.records[0].status, 'failure');
    assert.equal(result.records[0].budgetExpired, true);
    assert.ok(result.records[0].monotonicDeadlineMs > 0);
});


test('scaled slow list, conditional shutdown, boot and readiness can succeed within one shared phase', async t => {
    const phaseBudget = 2200;
    const result = await exercise(t, 'already-booted', {
        SHARPLINK_IOS_PREP_TIMEOUT_MS: String(phaseBudget),
        FAKE_LIST_DELAY_MS: '300', FAKE_SHUTDOWN_DELAY_MS: '150',
        FAKE_BOOT_DELAY_MS: '400', FAKE_BOOTSTATUS_DELAY_MS: '150'
    });
    assert.equal(result.code, 0, result.stderr);
    assert.deepEqual(result.commands, [
        ['simctl', 'list', 'devices', 'available', '-j'],
        ['simctl', 'shutdown', selected],
        ['simctl', 'boot', selected],
        ['simctl', 'bootstatus', selected, '-b']
    ]);
    const ordered = ['simulator-list', 'simulator-shutdown', 'simulator-boot', 'simulator-ready'].map(label => result.records.find(record => record.label === label));
    assert.equal(result.records.length, 4, 'each command runs once, with no retry');
    for (let index = 0; index < ordered.length; index++) {
        assert.equal(ordered[index].status, 'success');
        assert.ok(ordered[index].timeoutMs > 0 && ordered[index].timeoutMs <= phaseBudget);
        if (index) assert.ok(ordered[index].timeoutMs < ordered[index - 1].timeoutMs);
    }
    assert.equal(new Set(ordered.map(record => record.monotonicDeadlineMs)).size, 1);
    assert.equal(new Set(ordered.map(record => record.effectiveDeadlineMs)).size, 1);
    assert.equal(result.githubEnv, `IOS_SIMULATOR_UDID=${selected}\n`);
    assert.equal(result.phaseEvents.at(-1).type, 'phase-success');
    assert.ok(result.phaseEvents.at(-1).monotonicElapsedMs < phaseBudget);
});

test('scaled cumulative list and boot costs leave only the remainder for readiness, and exhaustion cannot hand off', async t => {
    const result = await exercise(t, 'success', {
        SHARPLINK_IOS_PREP_TIMEOUT_MS: '800',
        FAKE_LIST_DELAY_MS: '200', FAKE_BOOT_DELAY_MS: '200', FAKE_BOOTSTATUS_DELAY_MS: '650'
    });
    assert.notEqual(result.code, 0);
    const ordered = ['simulator-list', 'simulator-boot', 'simulator-ready'].map(label => result.records.find(record => record.label === label));
    assert.ok(ordered.every(Boolean), 'negative control reaches readiness before the phase expires');
    assert.equal(ordered[0].status, 'success');
    assert.equal(ordered[1].status, 'success');
    assert.equal(ordered[2].status, 'failure');
    assert.ok(ordered[2].timeoutMs < ordered[1].timeoutMs && ordered[1].timeoutMs < ordered[0].timeoutMs);
    assert.equal(new Set(ordered.map(record => record.monotonicDeadlineMs)).size, 1);
    assert.equal(new Set(ordered.map(record => record.effectiveDeadlineMs)).size, 1);
    assert.equal(result.commands.filter(command => command[1] === 'bootstatus').length, 1, 'no readiness retry');
    assert.equal(result.githubEnv, '');
    assert.equal(result.phaseEvents.at(-1).type, 'phase-failure');
    assert.equal(result.phaseEvents.at(-1).handoffWritten, false);
});
