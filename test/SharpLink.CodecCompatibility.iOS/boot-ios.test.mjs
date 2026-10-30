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
if (args[0] !== 'simctl') process.exit(90);
if (args[1] === 'list') {
    process.stdout.write(JSON.stringify({ devices: {
        'com.apple.CoreSimulator.SimRuntime.tvOS-26-0': [{ isAvailable: true, name: 'iPhone ignored runtime', udid: process.env.OTHER_UDID, state: 'Shutdown' }],
        'com.apple.CoreSimulator.SimRuntime.iOS-26-0': process.env.FAKE_SCENARIO === 'no-available' ? [] : [
            { isAvailable: true, name: 'iPad Pro', udid: process.env.OTHER_UDID, state: 'Shutdown' },
            { isAvailable: false, name: 'iPhone unavailable', udid: process.env.OTHER_UDID, state: 'Shutdown' },
            { isAvailable: true, name: 'iPhone chosen', udid: process.env.SELECTED_UDID, state: process.env.FAKE_SCENARIO === 'shutdown-failure' ? 'Booted' : 'Shutdown' },
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

async function exercise(t, scenario) {
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
    const result = await new Promise((resolve, reject) => {
        const child = spawn(process.execPath, [boot], { cwd: root, env, stdio: ['ignore', 'pipe', 'pipe'] });
        let stdout = '', stderr = '';
        const timer = setTimeout(() => { child.kill('SIGTERM'); reject(new Error('Boot mock exceeded watchdog')); }, 5000);
        child.stdout.on('data', data => { stdout += data; });
        child.stderr.on('data', data => { stderr += data; });
        child.on('error', error => { clearTimeout(timer); reject(error); });
        child.on('close', code => { clearTimeout(timer); resolve({ code, stdout, stderr }); });
    });
    const commands = (await fs.readFile(env.FAKE_COMMAND_LOG, 'utf8')).trim().split('\n').map(line => JSON.parse(line));
    let githubEnv = '';
    try { githubEnv = await fs.readFile(env.GITHUB_ENV, 'utf8'); }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
    const records = await Promise.all((await fs.readdir(env.SHARPLINK_IOS_DIAGNOSTICS)).filter(name => name.endsWith('.json')).map(async name => JSON.parse(await fs.readFile(path.join(env.SHARPLINK_IOS_DIAGNOSTICS, name), 'utf8'))));
    return { ...result, commands, githubEnv, records };
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
    assert.deepEqual(result.records.map(record => record.timeoutMs).sort((a, b) => a - b), [15000, 90000, 120000]);
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
