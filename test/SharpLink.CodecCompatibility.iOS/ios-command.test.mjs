import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

// This suite exercises only Node child processes; no real mobile or build tools.
const helper = fileURLToPath(new URL('./ios-command.mjs', import.meta.url));

async function execute(t, { code, timeoutMs = 1000, command = process.execPath, args, env = {}, preload }) {
    const root = await fs.mkdtemp(path.join(os.tmpdir(), 'sharplink-ios-command-test-'));
    const diagnostics = path.join(root, 'diagnostics');
    t.after(async () => {
        // Kill any explicitly recorded mock descendant before removing its PID file.
        try { process.kill(Number(await fs.readFile(path.join(root, 'pid'), 'utf8')), 'SIGKILL'); }
        catch (error) { if (!['ENOENT', 'ESRCH'].includes(error.code)) throw error; }
        await fs.rm(root, { recursive: true, force: true });
    });
    const childEnv = {
        ...process.env,
        SHARPLINK_IOS_DIAGNOSTICS: diagnostics,
        TEST_PID_FILE: path.join(root, 'pid'),
        TEST_HEARTBEAT_FILE: path.join(root, 'heartbeat'),
        ...env
    };
    delete childEnv.SHARPLINK_IOS_EXECUTION_DEADLINE_MS;
    if (Object.hasOwn(env, 'SHARPLINK_IOS_EXECUTION_DEADLINE_MS')) {
        childEnv.SHARPLINK_IOS_EXECUTION_DEADLINE_MS = env.SHARPLINK_IOS_EXECUTION_DEADLINE_MS;
    }
    const nodeArgs = [];
    if (preload) {
        const hook = path.join(root, 'preload.mjs');
        await fs.writeFile(hook, preload);
        nodeArgs.push('--import', hook);
    }
    const started = Date.now();
    const outcome = await new Promise((resolve, reject) => {
        const child = spawn(process.execPath,
            [...nodeArgs, helper, 'mock-command', String(timeoutMs / 1000), command, ...(args ?? ['-e', code])],
            { cwd: root, env: childEnv, detached: true, stdio: ['ignore', 'pipe', 'pipe'] });
        let stdout = '', stderr = '';
        const watchdog = setTimeout(() => {
            try { child.kill('SIGTERM'); } catch {}
            reject(new Error(`Command mock exceeded watchdog: ${stdout}\n${stderr}`));
        }, 10_000);
        child.stdout.on('data', part => { stdout += part; });
        child.stderr.on('data', part => { stderr += part; });
        child.on('error', error => { clearTimeout(watchdog); reject(error); });
        child.on('close', (code, signal) => {
            clearTimeout(watchdog);
            resolve({ code, signal, stdout, stderr, elapsedMs: Date.now() - started });
        });
    });
    let entries = [];
    try { entries = await fs.readdir(diagnostics); }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
    const records = await Promise.all(entries.filter(name => name.endsWith('.json')).map(async name => JSON.parse(await fs.readFile(path.join(diagnostics, name), 'utf8'))));
    const rawOut = (await Promise.all(entries.filter(name => name.endsWith('.stdout.log')).map(name => fs.readFile(path.join(diagnostics, name), 'utf8')))).join('');
    const rawErr = (await Promise.all(entries.filter(name => name.endsWith('.stderr.log')).map(name => fs.readFile(path.join(diagnostics, name), 'utf8')))).join('');
    return { ...outcome, root, records, rawOut, rawErr, childEnv };
}

test('command success preserves complete raw stdout and stderr with a zero exit record', async t => {
    const result = await execute(t, { code: `process.stdout.write('exact out\\n');process.stderr.write('exact err\\n');` });
    assert.equal(result.code, 0, result.stderr);
    assert.equal(result.rawOut, 'exact out\n');
    assert.equal(result.rawErr, 'exact err\n');
    assert.equal(result.records.length, 1);
    assert.equal(result.records[0].status, 'success');
    assert.equal(result.records[0].exitCode, 0);
    assert.equal(result.records[0].signal, null);
    assert.equal(result.records[0].processGroupExited, true);
});

test('nonzero exit remains a failure with exact code and raw output', async t => {
    const result = await execute(t, { code: `process.stdout.write('bad out');process.stderr.write('bad err');process.exit(19);` });
    assert.notEqual(result.code, 0);
    assert.equal(result.rawOut, 'bad out');
    assert.equal(result.rawErr, 'bad err');
    assert.equal(result.records.length, 1);
    assert.equal(result.records[0].exitCode, 19);
    assert.equal(result.records[0].status, 'failure');
});

test('missing executable records spawn ENOENT as failure rather than null-status success', async t => {
    const result = await execute(t, { command: 'sharplink-deliberately-nonexistent-command-88c01c', args: [] });
    assert.notEqual(result.code, 0);
    assert.equal(result.records.length, 1);
    assert.equal(result.records[0].status, 'failure');
    assert.match(result.records[0].spawnError, /ENOENT/);
    assert.equal(result.records[0].processGroupExited, true);
    assert.equal(result.rawOut, '');
    assert.equal(result.rawErr, '');
});

test('signal termination records null exit and original signal as failure', async t => {
    const result = await execute(t, { code: `process.kill(process.pid, 'SIGTERM');` });
    assert.notEqual(result.code, 0);
    assert.equal(result.records.length, 1);
    assert.equal(result.records[0].exitCode, null);
    assert.equal(result.records[0].signal, 'SIGTERM');
    assert.equal(result.records[0].status, 'failure');
    assert.equal(result.records[0].timedOut, false);
});

test('silent command timeout is bounded, recorded, and never retried', async t => {
    const result = await execute(t, { code: `setInterval(() => {}, 1000);`, timeoutMs: 150 });
    assert.notEqual(result.code, 0);
    assert.ok(result.elapsedMs < 5000);
    assert.equal(result.records.length, 1);
    assert.equal(result.records[0].status, 'failure');
    assert.equal(result.records[0].timedOut, true);
    assert.equal(result.records[0].processGroupExited, true);
    assert.equal(result.rawOut, '');
    assert.equal(result.rawErr, '');
});

test('inherited pipes after root exit cannot hang or falsely pass the command', async t => {
    const code = `
        const fs = require('node:fs');
        const { spawn } = require('node:child_process');
        const child = spawn(process.execPath, ['-e', "process.on('SIGTERM',()=>{});setInterval(()=>{},1000)"], { stdio: ['ignore', 'inherit', 'inherit'] });
        fs.writeFileSync(process.env.TEST_PID_FILE, String(child.pid));
        setTimeout(() => process.exit(0), 200);
    `;
    const result = await execute(t, { code, timeoutMs: 6000 });
    assert.notEqual(result.code, 0);
    assert.ok(result.elapsedMs < 6000);
    assert.equal(result.records.length, 1);
    assert.equal(result.records[0].status, 'failure');
    assert.equal(result.records[0].pipeLeak, true);
    assert.equal(result.records[0].pipesClosed, true);
    assert.equal(result.records[0].processGroupExited, true);
});

test('an execution deadline shortens a longer command budget and retains timeout failure', async t => {
    const deadline = Date.now() + 400;
    const result = await execute(t, {
        code: `setInterval(() => {}, 1000);`, timeoutMs: 5000,
        env: { SHARPLINK_IOS_EXECUTION_DEADLINE_MS: String(deadline) }
    });
    assert.notEqual(result.code, 0);
    assert.ok(result.elapsedMs < 3000);
    assert.equal(result.records.length, 1);
    assert.ok(result.records[0].timeoutMs <= 400);
    assert.equal(result.records[0].timedOut, true);
    assert.equal(result.records[0].processGroupExited, true);
});

test('expired execution deadline records failure without spawning a command', async t => {
    const result = await execute(t, {
        code: `process.stdout.write('MUST_NOT_RUN');`, timeoutMs: 5000,
        env: { SHARPLINK_IOS_EXECUTION_DEADLINE_MS: String(Date.now() - 1000) }
    });
    assert.notEqual(result.code, 0);
    assert.ok(result.elapsedMs < 3000);
    assert.equal(result.records.length, 1);
    assert.equal(result.records[0].timeoutMs, 1);
    assert.equal(result.records[0].childExited, false);
    assert.equal(result.records[0].processGroupExited, true);
    assert.match(result.records[0].spawnError, /not started/);
    assert.equal(result.rawOut, '');
    assert.equal(result.rawErr, '');
    assert.doesNotMatch(result.stdout, /MUST_NOT_RUN/);
});

test('invalid execution deadline fails before spawning the command', async t => {
    const result = await execute(t, {
        code: `process.stdout.write('MUST_NOT_RUN');`,
        env: { SHARPLINK_IOS_EXECUTION_DEADLINE_MS: 'NaN' }
    });
    assert.notEqual(result.code, 0);
    assert.deepEqual(result.records, []);
    assert.doesNotMatch(result.stdout, /MUST_NOT_RUN/);
});


test('large build-style output is preserved without a buffered spawn limit', async t => {
    const result = await execute(t, { code: `process.stdout.write('o'.repeat(262144));process.stderr.write('e'.repeat(262144));` });
    assert.equal(result.code, 0, result.stderr.slice(-200));
    assert.equal(result.rawOut, 'o'.repeat(262144));
    assert.equal(result.rawErr, 'e'.repeat(262144));
    assert.equal(result.records[0].status, 'success');
});


test('raw log write EIO records failure and stops the owned child instead of escaping cleanup', async t => {
    const preload = `
        import fs from 'node:fs';
        const originalOpen = fs.openSync;
        const originalWrite = fs.writeSync;
        let rawStdoutFd;
        fs.openSync = function(name, ...args) {
            const descriptor = originalOpen.call(fs, name, ...args);
            if (String(name).endsWith('.stdout.log')) rawStdoutFd = descriptor;
            return descriptor;
        };
        fs.writeSync = function(descriptor, buffer, ...args) {
            if (descriptor === rawStdoutFd && Buffer.isBuffer(buffer) && buffer.toString().includes('TRIGGER_LOG_EIO')) {
                throw Object.assign(new Error('mock raw-log write EIO'), { code: 'EIO' });
            }
            return originalWrite.call(fs, descriptor, buffer, ...args);
        };
    `;
    const code = `
        const fs = require('node:fs');
        fs.writeFileSync(process.env.TEST_PID_FILE, String(process.pid));
        setInterval(() => fs.appendFileSync(process.env.TEST_HEARTBEAT_FILE, 'x'), 20);
        setTimeout(() => process.stdout.write('TRIGGER_LOG_EIO'), 100);
    `;
    const result = await execute(t, { code, timeoutMs: 1000, preload });
    assert.notEqual(result.code, 0);
    assert.ok(result.elapsedMs < 4000);
    assert.equal(result.records.length, 1);
    assert.equal(result.records[0].status, 'failure');
    assert.match(result.records[0].outputError, /mock raw-log write EIO/);
    assert.equal(result.records[0].pipesClosed, true);
    assert.equal(result.records[0].processGroupExited, true);
    assert.match(result.stdout, /TRIGGER_LOG_EIO/, 'console output preserves the bytes the raw log could not write');
    const heartbeat = await fs.readFile(result.childEnv.TEST_HEARTBEAT_FILE, 'utf8');
    assert.ok(heartbeat.length > 0, 'owned process was actively running before the injected write failure');
    await new Promise(resolve => setTimeout(resolve, 1500));
    assert.equal(await fs.readFile(result.childEnv.TEST_HEARTBEAT_FILE, 'utf8'), heartbeat, 'no heartbeat after the original command budget');
});

test('SIGTERM EPERM is recorded without an unhandled rejection and SIGKILL still stops owned work', async t => {
    const preload = `
        const originalKill = process.kill.bind(process);
        let refusedFirstTerm = false;
        process.on('unhandledRejection', error => console.error('MOCK_UNHANDLED_REJECTION', error));
        process.kill = function(pid, signal) {
            if (pid < 0 && signal === 'SIGTERM' && !refusedFirstTerm) {
                refusedFirstTerm = true;
                console.error('MOCK_FIRST_SIGTERM_EPERM');
                throw Object.assign(new Error('mock first SIGTERM EPERM'), { code: 'EPERM' });
            }
            if (pid < 0 && signal === 'SIGKILL') console.log('MOCK_SIGKILL_FORWARDED');
            return originalKill(pid, signal);
        };
    `;
    const code = `
        const fs = require('node:fs');
        fs.writeFileSync(process.env.TEST_PID_FILE, String(process.pid));
        process.on('SIGTERM', () => {});
        setInterval(() => fs.appendFileSync(process.env.TEST_HEARTBEAT_FILE, 'x'), 20);
    `;
    const result = await execute(t, { code, timeoutMs: 300, preload });
    assert.notEqual(result.code, 0);
    assert.ok(result.elapsedMs < 5000, 'cleanup error must not cause an unbounded wait');
    assert.equal(result.records.length, 1);
    assert.equal(result.records[0].status, 'failure');
    assert.match(result.records[0].cleanupError, /mock first SIGTERM EPERM/);
    assert.equal(result.records[0].pipesClosed, true);
    assert.equal(result.records[0].signal, 'SIGKILL');
    // A previous cleanup error may deliberately leave processGroupExited false;
    // verify actual inactivity independently instead of treating unknown as success.
    assert.match(result.stderr, /MOCK_FIRST_SIGTERM_EPERM/);
    assert.match(result.stdout, /MOCK_SIGKILL_FORWARDED/);
    assert.doesNotMatch(result.stdout + result.stderr, /MOCK_UNHANDLED_REJECTION/);
    const heartbeat = await fs.readFile(result.childEnv.TEST_HEARTBEAT_FILE, 'utf8');
    assert.ok(heartbeat.length > 0, 'owned child was active before failed SIGTERM');
    await new Promise(resolve => setTimeout(resolve, 300));
    assert.equal(await fs.readFile(result.childEnv.TEST_HEARTBEAT_FILE, 'utf8'), heartbeat, 'SIGKILL left no active heartbeat');
});
