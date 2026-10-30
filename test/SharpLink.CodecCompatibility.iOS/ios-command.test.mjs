import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';
import test from 'node:test';

// This suite exercises only Node child processes; no real mobile or build tools.
const helper = fileURLToPath(new URL('./ios-command.mjs', import.meta.url));

async function execute(t, { code, timeoutMs = 1000, command = process.execPath, args, env = {}, preload, monotonicBudgetMs, cleanup = false }) {
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
    let entry = helper;
    if (monotonicBudgetMs !== undefined) {
        entry = path.join(root, 'monotonic-driver.mjs');
        await fs.writeFile(entry, `
            import { runCommand } from ${JSON.stringify(pathToFileURL(helper).href)};
            import { performance } from 'node:perf_hooks';
            const [label, seconds, command, ...args] = process.argv.slice(2);
            const monotonicDeadlineMs = performance.now() + ${JSON.stringify(monotonicBudgetMs)};
            runCommand(label, command, args, { timeoutMs: Number(seconds) * 1000, capture: false,
                monotonicDeadlineMs, cleanup: ${JSON.stringify(cleanup)} }).catch(error => {
                console.error(error.stack ?? error); process.exitCode = 1;
            });
        `);
    }
    const started = Date.now();
    const outcome = await new Promise((resolve, reject) => {
        const child = spawn(process.execPath,
            [...nodeArgs, entry, 'mock-command', String(timeoutMs / 1000), command, ...(args ?? ['-e', code])],
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
    const rawOutBuffer = Buffer.concat(await Promise.all(entries.filter(name => name.endsWith('.stdout.log')).map(name => fs.readFile(path.join(diagnostics, name)))));
    const rawErrBuffer = Buffer.concat(await Promise.all(entries.filter(name => name.endsWith('.stderr.log')).map(name => fs.readFile(path.join(diagnostics, name)))));
    const rawOut = rawOutBuffer.toString('utf8');
    const rawErr = rawErrBuffer.toString('utf8');
    return { ...outcome, root, records, rawOut, rawErr, rawOutBuffer, rawErrBuffer, childEnv };
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
    const termEvent = result.records[0].events.find(event => event.type === 'signal-result' && event.signal === 'SIGTERM');
    assert.equal(termEvent.errno, 'EPERM');
    assert.equal(termEvent.result, 'unknown');
    assert.equal(result.records[0].events[termEvent.sequence - 2].type, 'signal-call');
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

test('diagnostic directory setup crossing the absolute deadline cannot start a command', async t => {
    const preload = `
        import fs from 'node:fs';
        const originalMkdir = fs.mkdirSync;
        fs.mkdirSync = function(name, ...args) {
            if (String(name) === process.env.SHARPLINK_IOS_DIAGNOSTICS) {
                console.log('MOCK_DIRECTORY_SETUP_WAIT');
                const end = Date.now() + 900;
                while (Date.now() < end) {}
            }
            return originalMkdir.call(fs, name, ...args);
        };
    `;
    const result = await execute(t, {
        code: `process.stdout.write('COMMAND_MUST_NOT_START_AFTER_SETUP');`, timeoutMs: 5000, preload,
        env: { SHARPLINK_IOS_EXECUTION_DEADLINE_MS: String(Date.now() + 700) }
    });
    assert.notEqual(result.code, 0);
    assert.match(result.stdout, /MOCK_DIRECTORY_SETUP_WAIT/);
    assert.doesNotMatch(result.stdout, /COMMAND_MUST_NOT_START_AFTER_SETUP/);
    assert.equal(result.rawOut, '');
    assert.equal(result.records.length, 1);
    assert.equal(result.records[0].status, 'failure');
    assert.equal(result.records[0].childExited, false, 'no subprocess was started');
    assert.match(result.records[0].spawnError, /not started/);
    assert.ok(result.elapsedMs < 4000);
});

test('final metadata writing across the absolute deadline cannot retain a successful record or exit', async t => {
    const preload = `
        import fs from 'node:fs';
        const originalWrite = fs.writeFileSync;
        let delayedSuccessWrite = false;
        fs.writeFileSync = function(name, data, ...args) {
            let record;
            if (String(name).endsWith('.json') && typeof data === 'string') {
                try { record = JSON.parse(data); } catch {}
            }
            if (!delayedSuccessWrite && record?.status === 'success') {
                delayedSuccessWrite = true;
                console.log('MOCK_FINAL_METADATA_WAIT');
                const end = Number(process.env.SHARPLINK_IOS_EXECUTION_DEADLINE_MS) + 100;
                while (Date.now() < end) {}
            }
            return originalWrite.call(fs, name, data, ...args);
        };
    `;
    const result = await execute(t, {
        code: `process.stdout.write('COMMAND_COMPLETED_BEFORE_FINAL_METADATA');`, timeoutMs: 5000, preload,
        env: { SHARPLINK_IOS_EXECUTION_DEADLINE_MS: String(Date.now() + 1200) }
    });
    assert.notEqual(result.code, 0);
    assert.equal(result.rawOut, 'COMMAND_COMPLETED_BEFORE_FINAL_METADATA');
    assert.match(result.stdout, /MOCK_FINAL_METADATA_WAIT/, 'command reached its initial successful completion record');
    assert.equal(result.records.length, 1);
    assert.equal(result.records[0].status, 'failure', 'final evidence must not retain an obsolete success verdict');
    assert.equal(result.records[0].exitCode, 0, 'failure is post-command budget exhaustion, not a command failure');
    assert.equal(result.records[0].processGroupExited, true);
    assert.ok(result.elapsedMs < 4000);
});

test('legal short raw-log writes preserve every output byte before reporting success', async t => {
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
            if (descriptor === rawStdoutFd && Buffer.isBuffer(buffer)) {
                const offset = args[0] ?? 0;
                const requested = args[1] ?? (buffer.length - offset);
                const partial = Math.max(1, Math.floor(requested / 2));
                process.stderr.write('MOCK_LEGAL_SHORT_WRITE\\n');
                return originalWrite.call(fs, descriptor, buffer, offset, partial, args[2] ?? null);
            }
            return originalWrite.call(fs, descriptor, buffer, ...args);
        };
    `;
    const result = await execute(t, { code: `process.stdout.write('EXACT_OUTPUT_BYTES');`, preload });
    assert.equal(result.code, 0, result.stderr);
    assert.equal(result.rawOut, 'EXACT_OUTPUT_BYTES', 'legal short writes must be completed, never silently truncate evidence');
    assert.ok((result.stderr.match(/MOCK_LEGAL_SHORT_WRITE/g) ?? []).length > 1, 'mock exercised multiple partial writes');
    assert.equal(result.records.length, 1);
    assert.equal(result.records[0].status, 'success');
    assert.equal(result.records[0].outputError, null);
});

test('zero-byte raw-log write fails without spinning and stops the owned child', async t => {
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
            if (descriptor === rawStdoutFd && Buffer.isBuffer(buffer)) return 0;
            return originalWrite.call(fs, descriptor, buffer, ...args);
        };
    `;
    const code = `
        const fs = require('node:fs');
        fs.writeFileSync(process.env.TEST_PID_FILE, String(process.pid));
        setInterval(() => fs.appendFileSync(process.env.TEST_HEARTBEAT_FILE, 'x'), 20);
        setTimeout(() => process.stdout.write('TRIGGER_ZERO_WRITE'), 100);
    `;
    const result = await execute(t, { code, timeoutMs: 1000, preload });
    assert.notEqual(result.code, 0);
    assert.ok(result.elapsedMs < 4000, 'zero progress must not loop indefinitely');
    assert.equal(result.records.length, 1);
    assert.equal(result.records[0].status, 'failure');
    assert.match(result.records[0].outputError, /write|progress/i);
    assert.equal(result.records[0].pipesClosed, true);
    assert.equal(result.records[0].processGroupExited, true);
    assert.equal(result.rawOut, '');
    assert.match(result.stdout, /TRIGGER_ZERO_WRITE/);
    const heartbeat = await fs.readFile(result.childEnv.TEST_HEARTBEAT_FILE, 'utf8');
    assert.ok(heartbeat.length > 0);
    await new Promise(resolve => setTimeout(resolve, 300));
    assert.equal(await fs.readFile(result.childEnv.TEST_HEARTBEAT_FILE, 'utf8'), heartbeat);
});

test('ownership metadata and monotonic call/return lifecycle distinguish a reaped group from unknown', async t => {
    const result = await execute(t, { code: `process.stdout.write('owned');` });
    assert.equal(result.code, 0, result.stderr);
    const record = result.records[0];
    assert.ok(Number.isSafeInteger(record.pid) && record.pid > 1);
    assert.equal(record.expectedPgid, record.pid);
    assert.ok(Number.isFinite(Date.parse(record.timelineOriginAt)));
    assert.ok(Date.parse(record.timelineOriginAt) <= Date.parse(record.startedAt), 'timeline origin precedes log setup; startedAt is not monotonic zero');
    assert.equal(record.finalGroupState, 'gone');
    assert.equal(record.unknownGroupOwnership, false);
    assert.equal(record.eventLimit, 1024);
    assert.equal(record.droppedEventCount, 0);
    const events = record.events;
    for (let index = 0; index < events.length; index++) {
        assert.equal(events[index].sequence, index + 1);
        assert.ok(events[index].monotonicMs >= 0);
        if (index) assert.ok(events[index].monotonicMs >= events[index - 1].monotonicMs);
    }
    const at = type => events.findIndex(event => event.type === type);
    assert.ok(at('spawn-call') < at('spawn-returned'));
    assert.ok(at('spawn-returned') < at('exit'));
    assert.ok(at('exit') < at('close'));
    const probes = events.filter(event => event.type === 'group-probe-result');
    assert.ok(probes.length > 0);
    assert.ok(probes.every(event => event.signal === 0 && event.state === 'gone' && event.errno === 'ESRCH'));
    for (const probe of probes) {
        const call = events[probe.sequence - 2];
        assert.equal(call.type, 'group-probe-call');
        assert.equal(call.source, probe.source);
        assert.equal(call.expectedPgid, record.expectedPgid);
    }
    assert.deepEqual(record.snapshots, []);
});

test('signal-zero EPERM is unknown ownership, not confirmed remaining descendants', async t => {
    const preload = `
        const originalKill = process.kill.bind(process);
        process.kill = function(pid, signal) {
            if (pid < 0 && signal === 0) throw Object.assign(new Error('mock signal-zero EPERM'), { code: 'EPERM' });
            return originalKill(pid, signal);
        };
    `;
    const result = await execute(t, { code: `process.stdout.write('finished');`, preload });
    assert.notEqual(result.code, 0);
    const record = result.records[0];
    assert.equal(record.remainingGroupState, 'unknown');
    assert.equal(record.unknownGroupOwnership, true);
    assert.equal(record.lingeringDescendants, false);
    assert.equal(record.confirmedRemainingGroupMembers, false);
    assert.equal(record.processGroupExited, false);
    const probes = record.events.filter(event => event.type === 'group-probe-result');
    assert.ok(probes.length > 0 && probes.every(event => event.state === 'unknown' && event.errno === 'EPERM'));
    const signals = record.events.filter(event => event.type === 'signal-result');
    assert.ok(signals.some(event => event.signal === 'SIGTERM' && event.errno === 'ESRCH' && event.result === 'gone'));
    assert.ok(record.snapshots.length <= 2);
    assert.ok(record.events.length <= record.eventLimit);
});

test('SIGKILL EPERM retains its distinct errno source and cannot become success after a late normal exit', async t => {
    const preload = `
        const originalKill = process.kill.bind(process);
        process.kill = function(pid, signal) {
            if (pid < 0 && signal === 'SIGKILL') throw Object.assign(new Error('mock SIGKILL EPERM'), { code: 'EPERM' });
            return originalKill(pid, signal);
        };
    `;
    const code = `process.on('SIGTERM', () => {});setTimeout(() => process.exit(0), 2200);`;
    const result = await execute(t, { code, timeoutMs: 300, preload });
    assert.notEqual(result.code, 0);
    const record = result.records[0];
    assert.equal(record.status, 'failure');
    assert.equal(record.timedOut, true);
    assert.match(record.cleanupError, /mock SIGKILL EPERM/);
    const signal = record.events.find(event => event.type === 'signal-result' && event.signal === 'SIGKILL');
    assert.equal(signal.errno, 'EPERM');
    assert.equal(signal.result, 'unknown');
    assert.equal(record.events[signal.sequence - 2].type, 'signal-call');
    assert.ok(record.events.some(event => event.type === 'close' && event.exitCode === 0));
    assert.ok(record.snapshots.length <= 2);
});

test('late close after the command timer remains failed and lifecycle timestamps show the ordering', async t => {
    const preload = `
        import cp from 'node:child_process';
        import { syncBuiltinESMExports } from 'node:module';
        const originalSpawn = cp.spawn;
        cp.spawn = function(command, ...args) {
            const child = originalSpawn.call(cp, command, ...args);
            if (command === process.execPath) {
                const originalEmit = child.emit.bind(child);
                child.emit = function(name, ...values) {
                    if (name === 'close') { setTimeout(() => originalEmit(name, ...values), 350); return true; }
                    return originalEmit(name, ...values);
                };
            }
            return child;
        };
        syncBuiltinESMExports();
    `;
    const result = await execute(t, { code: `process.stdout.write('root completed');`, timeoutMs: 150, preload });
    assert.notEqual(result.code, 0);
    const record = result.records[0];
    assert.equal(record.timedOut, true);
    assert.equal(record.exitCode, 0);
    const fired = record.events.find(event => event.type === 'timer-fired' && event.timer === 'command-timeout');
    const close = record.events.find(event => event.type === 'close');
    assert.ok(fired && close && fired.monotonicMs < close.monotonicMs);
});

test('snapshot timeouts are independent diagnostics and cannot delay TERM-to-KILL escalation', async t => {
    const preload = `
        import cp from 'node:child_process';
        import { syncBuiltinESMExports } from 'node:module';
        import { EventEmitter } from 'node:events';
        import { PassThrough } from 'node:stream';
        const originalSpawn = cp.spawn;
        cp.spawn = function(command, args, options) {
            if (command !== '/bin/ps') return originalSpawn.call(cp, command, args, options);
            const child = new EventEmitter();
            child.pid = 99001;
            child.stdout = new PassThrough(); child.stderr = new PassThrough();
            child.unref = () => {};
            child.kill = signal => { queueMicrotask(() => child.emit('close', null, signal)); return true; };
            return child;
        };
        syncBuiltinESMExports();
    `;
    const result = await execute(t, { code: `process.on('SIGTERM', () => {});setInterval(() => {}, 1000);`, timeoutMs: 300, preload });
    assert.notEqual(result.code, 0);
    const record = result.records[0];
    assert.equal(record.snapshots.length, 2);
    assert.ok(record.snapshots.every(snapshot => snapshot.status === 'unknown' && snapshot.reason === 'timeout'));
    const term = record.events.find(event => event.type === 'signal-call' && event.signal === 'SIGTERM');
    const kill = record.events.find(event => event.type === 'signal-call' && event.signal === 'SIGKILL');
    assert.ok(term && kill);
    assert.ok(kill.monotonicMs - term.monotonicMs < 1400, 'diagnostic snapshots must not add their timeout to TERM grace');
    assert.equal(record.processGroupExited, true);
});

test('command evidence distinguishes the simulator-preparation deadline from the original job budget', async t => {
    const original = Date.now() + 30000;
    const preparation = Date.now() + 10000;
    const result = await execute(t, {
        code: `process.stdout.write('scoped');`,
        env: {
            SHARPLINK_IOS_EXECUTION_DEADLINE_MS: String(preparation),
            SHARPLINK_IOS_DEADLINE_SCOPE: 'simulator-preparation',
            SHARPLINK_IOS_ORIGINAL_EXECUTION_DEADLINE_MS: String(original),
            SHARPLINK_IOS_PREPARATION_DEADLINE_MS: String(preparation)
        }
    });
    assert.equal(result.code, 0, result.stderr);
    assert.equal(result.records[0].deadlineScope, 'simulator-preparation');
    assert.equal(result.records[0].effectiveDeadlineMs, preparation);
    assert.equal(result.records[0].originalExecutionDeadlineMs, original);
    assert.equal(result.records[0].preparationDeadlineMs, preparation);
});


test('monotonic event storage is capped and counts dropped events rather than growing without bound', async t => {
    const preload = `
        const originalWrite = process.stdout.write.bind(process.stdout);
        let flooded = false;
        process.stdout.write = function(chunk, ...args) {
            if (!flooded && String(chunk).includes('TRIGGER_EVENT_FLOOD')) {
                flooded = true;
                for (let index = 0; index < 1500; index++) process.emit('SIGINT');
            }
            return originalWrite(chunk, ...args);
        };
    `;
    const result = await execute(t, { code: `process.stdout.write('TRIGGER_EVENT_FLOOD');setInterval(() => {}, 1000);`, preload });
    assert.notEqual(result.code, 0);
    const record = result.records[0];
    assert.equal(record.status, 'failure');
    assert.equal(record.events.length, record.eventLimit);
    assert.equal(record.eventLimit, 1024);
    assert.ok(record.droppedEventCount > 400);
    assert.ok(record.snapshots.length <= 2);
});

test('absolute monotonic budget bounds a command even without a wall-clock deadline', async t => {
    const result = await execute(t, { code: `setInterval(() => {}, 1000);`, timeoutMs: 5000, monotonicBudgetMs: 150 });
    assert.notEqual(result.code, 0);
    assert.ok(result.elapsedMs < 3000);
    const record = result.records[0];
    assert.equal(record.effectiveDeadlineMs, null);
    assert.ok(Number.isFinite(record.monotonicDeadlineMs) && record.monotonicDeadlineMs > 0);
    assert.equal(record.monotonicDeadlineClock, 'performance.now');
    assert.ok(record.timeoutMs > 0 && record.timeoutMs <= 150 && Number.isInteger(record.timeoutMs));
    assert.equal(record.budgetExpired, true);
});

test('shorter wall budget wins over a longer monotonic budget without extending the command', async t => {
    const result = await execute(t, {
        code: `setInterval(() => {}, 1000);`, timeoutMs: 5000, monotonicBudgetMs: 1500,
        env: { SHARPLINK_IOS_EXECUTION_DEADLINE_MS: String(Date.now() + 350) }
    });
    assert.notEqual(result.code, 0);
    assert.ok(result.elapsedMs < 1500);
    assert.ok(result.records[0].timeoutMs <= 350);
    assert.equal(result.records[0].budgetExpired, true);
});

test('initial metadata blocking plus wall-clock rollback cannot spawn after the monotonic phase budget', async t => {
    const preload = `
        import fs from 'node:fs';
        const originalWrite = fs.writeFileSync;
        const realNow = Date.now.bind(Date);
        let delayed = false;
        fs.writeFileSync = function(name, data, ...args) {
            if (!delayed && String(name).endsWith('.json') && typeof data === 'string' && JSON.parse(data).status === 'running') {
                delayed = true;
                const end = realNow() + 250;
                while (realNow() < end) {}
                Date.now = () => realNow() - 1000;
                console.log('MOCK_INITIAL_METADATA_BLOCK_AND_WALL_ROLLBACK');
            }
            return originalWrite.call(fs, name, data, ...args);
        };
    `;
    const result = await execute(t, {
        code: `process.stdout.write('MUST_NOT_SPAWN_PAST_MONOTONIC_BUDGET');`, timeoutMs: 5000, monotonicBudgetMs: 150, preload,
        env: { SHARPLINK_IOS_EXECUTION_DEADLINE_MS: String(Date.now() + 700) }
    });
    assert.notEqual(result.code, 0);
    assert.match(result.stdout, /MOCK_INITIAL_METADATA_BLOCK_AND_WALL_ROLLBACK/);
    assert.doesNotMatch(result.stdout, /MUST_NOT_SPAWN_PAST_MONOTONIC_BUDGET/);
    assert.equal(result.rawOut, '');
    const record = result.records[0];
    assert.equal(record.status, 'failure');
    assert.equal(record.pid, null);
    assert.equal(record.childExited, false);
    assert.equal(record.budgetExpired, true);
    assert.match(record.spawnError, /not started/);
});

test('final metadata write crossing only the monotonic budget must revise success to failure', async t => {
    const preload = `
        import fs from 'node:fs';
        const originalWrite = fs.writeFileSync;
        let delayed = false;
        fs.writeFileSync = function(name, data, ...args) {
            if (!delayed && String(name).endsWith('.json') && typeof data === 'string' && JSON.parse(data).status === 'success') {
                delayed = true;
                console.log('MOCK_FINAL_METADATA_MONOTONIC_WAIT');
                const end = performance.now() + 400;
                while (performance.now() < end) {}
            }
            return originalWrite.call(fs, name, data, ...args);
        };
    `;
    const result = await execute(t, { code: `process.stdout.write('completed-before-log');`, timeoutMs: 5000, monotonicBudgetMs: 300, preload });
    assert.notEqual(result.code, 0);
    assert.match(result.stdout, /MOCK_FINAL_METADATA_MONOTONIC_WAIT/);
    assert.equal(result.rawOut, 'completed-before-log');
    assert.equal(result.records[0].status, 'failure');
    assert.equal(result.records[0].budgetExpired, true);
    assert.equal(result.records[0].exitCode, 0);
});

test('cleanup retains its own bounded timeout when the execution monotonic budget is exhausted', async t => {
    const result = await execute(t, { code: `process.stdout.write('bounded-cleanup');`, timeoutMs: 1000, monotonicBudgetMs: 0, cleanup: true });
    assert.equal(result.code, 0, result.stderr);
    assert.equal(result.records[0].status, 'success');
    assert.equal(result.records[0].timeoutMs, 1000);
    assert.equal(result.records[0].budgetExpired, false);
});

test('invalid absolute monotonic deadline is rejected before any subprocess or evidence record', async t => {
    const result = await execute(t, { code: `process.stdout.write('MUST_NOT_RUN');`, monotonicBudgetMs: 'invalid' });
    assert.notEqual(result.code, 0);
    assert.deepEqual(result.records, []);
    assert.doesNotMatch(result.stdout, /MUST_NOT_RUN/);
    assert.match(result.stderr, /Invalid absolute monotonic deadline/);
});

test('opt-in observation measures slow raw writes without changing the timeout failure gate', async t => {
    const preload = `
        import fs from 'node:fs';
        const originalOpen = fs.openSync, originalWrite = fs.writeSync;
        let rawStdoutFd;
        fs.openSync = function(name, ...args) {
            const fd = originalOpen.call(fs, name, ...args);
            if (String(name).endsWith('.stdout.log')) rawStdoutFd = fd;
            return fd;
        };
        fs.writeSync = function(fd, buffer, ...args) {
            if (fd === rawStdoutFd && Buffer.isBuffer(buffer)) {
                const until = performance.now() + 350;
                while (performance.now() < until) {}
            }
            return originalWrite.call(fs, fd, buffer, ...args);
        };
    `;
    const result = await execute(t, { timeoutMs: 200, preload,
        env: { SHARPLINK_IOS_IO_OBSERVATION: '1' },
        code: `process.stdout.write('RAW_WRITE_TIMING');setInterval(() => {},1000);` });
    assert.notEqual(result.code, 0);
    assert.equal(result.rawOut, 'RAW_WRITE_TIMING');
    assert.equal(result.records.length, 1);
    const record = result.records[0];
    assert.equal(record.status, 'failure');
    assert.equal(record.timedOut, true);
    assert.equal(record.processGroupExited, true);
    assert.ok(record.ioObservation.streams.stdout.rawWriteMaxMs >= 300);
    assert.equal(record.ioObservation.streams.stdout.byteCount, Buffer.byteLength('RAW_WRITE_TIMING'));
    assert.ok(result.elapsedMs < 5000);
});

test('opt-in observation preserves binary raw output and observes false forwarding without drain control', async t => {
    const preload = `
        const originalWrite = process.stdout.write.bind(process.stdout);
        Object.defineProperty(process.stdout, 'writableLength', { configurable: true, get: () => 64 });
        process.stdout.write = function(chunk, ...args) {
            const returned = originalWrite(chunk, ...args);
            return Buffer.isBuffer(chunk) ? false : returned;
        };
    `;
    const bytes = [255, 254, 0, 195, 40, 128];
    const result = await execute(t, { preload, env: { SHARPLINK_IOS_IO_OBSERVATION: '1' },
        code: `process.stdout.write(Buffer.from(${JSON.stringify(bytes)}));process.stderr.write(Buffer.from([0,255]));` });
    assert.equal(result.code, 0, result.stderr);
    assert.deepEqual(result.rawOutBuffer, Buffer.from(bytes));
    assert.deepEqual(result.rawErrBuffer, Buffer.from([0, 255]));
    const record = result.records[0];
    assert.equal(record.status, 'success');
    assert.equal(record.ioObservation.streams.stdout.byteCount, bytes.length);
    assert.equal(record.ioObservation.streams.stderr.byteCount, 2);
    assert.ok(record.ioObservation.streams.stdout.forwardFalseCount >= 1);
    assert.equal(record.ioObservation.streams.stdout.queuePeakBytes, 64);
    assert.equal(record.ioObservation.streams.stdout.queueFinalBytes, 64);
    assert.equal(record.outputError, null);
});

test('I/O observation remains opt-in and default command records contain no telemetry field', async t => {
    // Explicitly absent even if the parent test runner opts other subprocess controls into observation.
    const result = await execute(t, { env: { SHARPLINK_IOS_IO_OBSERVATION: undefined },
        code: `process.stdout.write('DEFAULT_UNOBSERVED');` });
    assert.equal(result.code, 0, result.stderr);
    assert.equal(result.rawOut, 'DEFAULT_UNOBSERVED');
    assert.equal(Object.hasOwn(result.records[0], 'ioObservation'), false);
});

test('invalid I/O observation flag fails configuration before spawning any command', async t => {
    const result = await execute(t, { env: { SHARPLINK_IOS_IO_OBSERVATION: 'yes' },
        code: `process.stdout.write('MUST_NOT_SPAWN_INVALID_FLAG');` });
    assert.notEqual(result.code, 0);
    assert.doesNotMatch(result.stdout, /MUST_NOT_SPAWN_INVALID_FLAG/);
    assert.match(result.stderr, /SHARPLINK_IOS_IO_OBSERVATION must be 0 or 1/);
    assert.equal(result.rawOut, '');
});
