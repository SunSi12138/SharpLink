// Bounded subprocess ownership for iOS CI. Logs contain this command's output, never its environment.
import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { captureOwnedGroupSnapshot } from './ios-process-snapshot.mjs';

let sequence = 0;
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

export function shortenedTimeout(name, maximum) {
    const value = Number(process.env[name] ?? maximum);
    if (!Number.isSafeInteger(value) || value <= 0 || value > maximum) {
        throw new Error(`${name} must be an integer between 1 and ${maximum}; increasing the gate is not supported.`);
    }
    return value;
}

export async function runCommand(label, command, args, { timeoutMs, env = process.env, capture = true, cleanup = false } = {}) {
    if (!/^[a-z0-9-]+$/.test(label) || typeof command !== 'string' || !command || !Array.isArray(args) || args.some(arg => typeof arg !== 'string') || !Number.isSafeInteger(timeoutMs) || timeoutMs <= 0) {
        throw new Error('Invalid command label or timeout.');
    }
    const timelineOriginAt = new Date().toISOString();
    const monotonicOrigin = process.hrtime.bigint();
    const monotonicMs = () => Number(process.hrtime.bigint() - monotonicOrigin) / 1e6;
    const events = [];
    const eventLimit = 1024;
    let droppedEventCount = 0;
    const event = (type, details = {}) => {
        if (events.length >= eventLimit) { droppedEventCount++; return; }
        events.push({ sequence: events.length + 1, monotonicMs: monotonicMs(), type, ...details });
    };
    // The workflow reserves its final minutes for evidence upload. Cleanup retains its own short bound.
    const deadline = Number(process.env.SHARPLINK_IOS_EXECUTION_DEADLINE_MS || 0);
    if (!Number.isSafeInteger(deadline) || deadline < 0) throw new Error('Invalid iOS execution deadline.');
    const deadlineScope = process.env.SHARPLINK_IOS_DEADLINE_SCOPE || 'job-execution';
    if (!['job-execution', 'simulator-preparation'].includes(deadlineScope)) throw new Error('Invalid iOS deadline scope.');
    const optionalDeadline = name => {
        const value = process.env[name];
        if (!value) return null;
        const parsed = Number(value);
        if (!Number.isSafeInteger(parsed) || parsed <= 0) throw new Error(`Invalid ${name}.`);
        return parsed;
    };
    const originalExecutionDeadlineMs = optionalDeadline('SHARPLINK_IOS_ORIGINAL_EXECUTION_DEADLINE_MS') ?? (deadlineScope === 'job-execution' ? deadline || null : null);
    const preparationDeadlineMs = optionalDeadline('SHARPLINK_IOS_PREPARATION_DEADLINE_MS');
    let budgetExpired = Boolean(deadline && !cleanup && deadline <= Date.now());
    if (deadline && !cleanup) timeoutMs = Math.min(timeoutMs, Math.max(1, deadline - Date.now()));
    const directory = process.env.SHARPLINK_IOS_DIAGNOSTICS || 'artifacts/codec-compat/ios-diagnostics';
    fs.mkdirSync(directory, { recursive: true });
    const stem = path.join(directory, `${Date.now()}-${process.pid}-${++sequence}-${label}`);
    const outFd = fs.openSync(`${stem}.stdout.log`, 'wx');
    const errFd = fs.openSync(`${stem}.stderr.log`, 'wx');
    const startedAt = new Date().toISOString();
    const record = { label, command, args, startedAt, timelineOriginAt, timeoutMs, status: 'running', pid: null, expectedPgid: null, deadlineScope, effectiveDeadlineMs: deadline || null, originalExecutionDeadlineMs, preparationDeadlineMs, events, eventLimit, get droppedEventCount() { return droppedEventCount; }, snapshots: [] };
    event('deadline-check', { stage: 'initial', deadlineMs: deadline || null, expired: budgetExpired });
    fs.writeFileSync(`${stem}.json`, JSON.stringify(record, null, 2) + '\n', { flag: 'wx' });
    console.log(`[ios-command] START ${label} timeoutMs=${timeoutMs}`);
    let stdout = '', stderr = '', child, exitCode = null, signal = null, spawnError, outputError, cleanupError;
    let exited = false, closed = false, timedOut = false, pipeLeak = false;
    let timer, pipeTimer, watchdog;
    let interrupted = null, commandStartedMs = null, commandStartedMonotonicMs = null;
    const snapshots = [];
    const requestSnapshot = stage => {
        if (snapshots.length >= 2 || !Number.isSafeInteger(child?.pid) || child.pid <= 1) return;
        event('snapshot-request', { stage, ownedPgid: child.pid });
        const ownedPgid = child.pid;
        const pending = Promise.resolve().then(() => captureOwnedGroupSnapshot(ownedPgid)).then(snapshot => {
            record.snapshots.push({ stage, ...snapshot });
            event('snapshot-complete', { stage, status: snapshot.status, reason: snapshot.reason });
        }).catch(error => {
            record.snapshots.push({ stage, ownedPgid, status: 'unknown', reason: 'internal-snapshot-error', rows: [], error: { code: error.code ?? null, message: error.message } });
            event('snapshot-complete', { stage, status: 'unknown', reason: 'internal-snapshot-error' });
        });
        snapshots.push(pending);
    };
    let resolveClosed;
    const closePromise = new Promise(resolve => { resolveClosed = resolve; });
    const append = (fd, stream, chunk, isStdout) => {
        try {
            let offset = 0;
            while (offset < chunk.length) {
                const written = fs.writeSync(fd, chunk, offset, chunk.length - offset);
                if (!Number.isInteger(written) || written <= 0 || written > chunk.length - offset) {
                    throw new Error(`Raw log write made invalid progress: ${written}.`);
                }
                offset += written;
            }
        } catch (error) { outputError ??= error; }
        try { stream.write(chunk); } catch (error) { outputError ??= error; }
        if (outputError) void stop();
        // Success requires complete raw logs; logging failure is fatal. Only small return values need capture.
        if (capture) {
            if (isStdout) stdout += chunk.toString(); else stderr += chunk.toString();
        }
    };
    const probeGroup = source => {
        if (!child?.pid) return 'not-started';
        let state = 'alive', errno = null;
        event('group-probe-call', { source, signal: 0, expectedPgid: child.pid });
        try { process.kill(-child.pid, 0); }
        catch (error) {
            errno = error.code ?? 'UNKNOWN';
            if (error.code === 'ESRCH') state = 'gone';
            else { cleanupError ??= error; state = 'unknown'; }
        }
        event('group-probe-result', { source, signal: 0, expectedPgid: child.pid, state, errno });
        return state;
    };
    const unresolvedGroup = source => !['gone', 'not-started'].includes(probeGroup(source));
    const send = sig => {
        if (!child?.pid) return;
        let result = 'sent', errno = null;
        event('signal-call', { signal: sig, expectedPgid: child.pid });
        try { process.kill(-child.pid, sig); }
        catch (error) {
            errno = error.code ?? 'UNKNOWN';
            if (error.code === 'ESRCH') result = 'gone';
            else { cleanupError ??= error; result = 'unknown'; }
        }
        event('signal-result', { signal: sig, expectedPgid: child.pid, result, errno });
    };
    let stopping;
    const stop = () => stopping ??= (async () => {
        // Start diagnostics without awaiting them: snapshots cannot delay TERM or KILL.
        send('SIGTERM');
        requestSnapshot('after-term');
        const termDeadline = monotonicMs() + 1000;
        while ((!closed || unresolvedGroup('term-wait')) && monotonicMs() < termDeadline) await sleep(25);
        if (!closed || unresolvedGroup('before-kill')) { send('SIGKILL'); requestSnapshot('after-kill'); }
        const killDeadline = monotonicMs() + 2000;
        while ((!closed || unresolvedGroup('kill-wait')) && monotonicMs() < killDeadline) await sleep(25);
        requestSnapshot('cleanup-complete');
        // Do not wait forever for inherited output pipes after killing the owned group.
        if (!closed) {
            child?.stdout?.destroy(); child?.stderr?.destroy();
            await Promise.race([closePromise, sleep(250)]);
            if (!closed) child?.unref(); // Report uncertain cleanup, but do not hang the supervisor itself.
        }
    })();
    const onInterrupt = sig => { interrupted = sig; event('interrupted', { signal: sig }); void stop(); };
    const onTerm = () => onInterrupt('SIGTERM');
    const onInt = () => onInterrupt('SIGINT');
    const onOutputError = error => { outputError ??= error; void stop(); };
    process.stdout.on('error', onOutputError); process.stderr.on('error', onOutputError);
    process.on('SIGTERM', onTerm); process.on('SIGINT', onInt);
    try {
        // Directory/open/metadata I/O above can consume the shared budget. Recheck at the spawn boundary.
        if (deadline && !cleanup) {
            budgetExpired = deadline <= Date.now();
            timeoutMs = Math.min(timeoutMs, Math.max(1, deadline - Date.now()));
            record.timeoutMs = timeoutMs;
        }
        event('deadline-check', { stage: 'before-spawn', deadlineMs: deadline || null, expired: budgetExpired });
        if (budgetExpired) {
            spawnError = new Error('iOS execution budget exhausted; command was not started.');
            closed = true; resolveClosed();
        } else {
            commandStartedMs = Date.now();
            record.spawnedAt = new Date(commandStartedMs).toISOString();
            commandStartedMonotonicMs = monotonicMs();
            event('spawn-call');
            child = spawn(command, args, { env, detached: true, stdio: ['ignore', 'pipe', 'pipe'] });
            record.pid = child.pid ?? null; record.expectedPgid = child.pid ?? null;
            event('spawn-returned', { pid: record.pid, expectedPgid: record.expectedPgid });
            console.log(`[ios-command] OWNED ${label} pid=${record.pid} expectedPgid=${record.expectedPgid}`);
            child.stdout.on('data', chunk => append(outFd, process.stdout, chunk, true));
            child.stderr.on('data', chunk => append(errFd, process.stderr, chunk, false));
            child.on('error', error => { spawnError = error; event('spawn-error', { errno: error.code ?? null }); });
            child.on('exit', (code, sig) => {
                exited = true; exitCode = code; signal = sig; event('exit', { exitCode: code, signal: sig });
                pipeTimer = setTimeout(() => { if (!closed) { pipeLeak = true; event('timer-fired', { timer: 'pipe-close' }); void stop(); } }, 1000);
            });
            child.on('close', (code, sig) => {
                closed = true; exitCode = code; signal = sig; event('close', { exitCode: code, signal: sig }); clearTimeout(pipeTimer); resolveClosed();
            });
        }
        timer = setTimeout(() => { timedOut = true; event('timer-fired', { timer: 'command-timeout' }); void stop(); }, timeoutMs);
        fs.writeFileSync(`${stem}.json`, JSON.stringify(record, null, 2) + '\n');
        await Promise.race([closePromise, new Promise(resolve => { watchdog = setTimeout(() => { event('timer-fired', { timer: 'supervisor-watchdog' }); resolve(); }, timeoutMs + 4000); })]);
        clearTimeout(watchdog);
        clearTimeout(timer); clearTimeout(pipeTimer);
        // A delayed JS timer must not turn a command completed after its limit into success.
        if (commandStartedMonotonicMs !== null && monotonicMs() >= commandStartedMonotonicMs + timeoutMs) timedOut = true;
        event('deadline-check', { stage: 'after-close-or-watchdog', commandElapsedMs: commandStartedMonotonicMs === null ? null : monotonicMs() - commandStartedMonotonicMs, timeoutMs, timedOut });
        const remainingGroupState = probeGroup('after-close-or-watchdog');
        const lingering = remainingGroupState === 'alive';
        if (!closed || ['alive', 'unknown'].includes(remainingGroupState)) await stop();
        if (stopping) await stopping;
        await Promise.all(snapshots);
        const finalGroupState = probeGroup('final-verdict');
        const groupExited = ['gone', 'not-started'].includes(finalGroupState) && !cleanupError;
        const completedPastDeadline = Boolean(deadline && !cleanup && Date.now() >= deadline);
        budgetExpired ||= completedPastDeadline;
        event('deadline-check', { stage: 'final-verdict', deadlineMs: deadline || null, expired: completedPastDeadline });
        const ok = !completedPastDeadline && !cleanupError && !outputError && !interrupted && !spawnError && !timedOut && !pipeLeak && !lingering && closed && exitCode === 0 && signal === null && groupExited;
        Object.assign(record, { endedAt: new Date().toISOString(), status: ok ? 'success' : 'failure', exitCode, signal,
            timedOut, budgetExpired, interrupted, pipeLeak, lingeringDescendants: exited && lingering, confirmedRemainingGroupMembers: lingering, remainingGroupState, finalGroupState, unknownGroupOwnership: remainingGroupState === 'unknown' || finalGroupState === 'unknown', childExited: exited, pipesClosed: closed, processGroupExited: groupExited,
            spawnError: spawnError?.message ?? null, outputError: outputError?.message ?? null, cleanupError: cleanupError?.message ?? null });
        fs.writeFileSync(`${stem}.json`, JSON.stringify(record, null, 2) + '\n');
        event('deadline-check', { stage: 'after-final-metadata', deadlineMs: deadline || null, expired: Boolean(deadline && !cleanup && Date.now() >= deadline) });
        if (record.status === 'success' && deadline && !cleanup && Date.now() >= deadline) {
            Object.assign(record, { status: 'failure', budgetExpired: true, endedAt: new Date().toISOString() });
            fs.writeFileSync(`${stem}.json`, JSON.stringify(record, null, 2) + '\n');
        }
        console.log(`[ios-command] END ${label} status=${record.status} exit=${exitCode} signal=${signal} timeout=${timedOut} groupExited=${groupExited}`);
        // Console/file output must not create a final successful-return window past the absolute budget.
        if (record.status === 'success' && deadline && !cleanup && Date.now() >= deadline) {
            Object.assign(record, { status: 'failure', budgetExpired: true, endedAt: new Date().toISOString() });
            fs.writeFileSync(`${stem}.json`, JSON.stringify(record, null, 2) + '\n');
        }
        if (record.status !== 'success') {
            const error = new Error(`${label} failed: exit=${exitCode}, signal=${signal}, timeout=${timedOut}, budgetExpired=${record.budgetExpired}, spawn=${spawnError?.message ?? 'none'}, output=${outputError?.message ?? 'none'}, cleanup=${cleanupError?.message ?? 'none'}, pipesClosed=${closed}, groupExited=${groupExited}; raw evidence: ${stem}`);
            error.commandRecord = record;
            throw error;
        }
        return { stdout, stderr, record };
    } finally {
        // Unexpected logger/metadata errors must never abandon an owned live group.
        if (child?.pid && (!closed || unresolvedGroup('finally'))) await stop();
        if (stopping) await stopping;
        await Promise.all(snapshots);
        clearTimeout(timer); clearTimeout(pipeTimer); clearTimeout(watchdog);
        process.off('SIGTERM', onTerm); process.off('SIGINT', onInt);
        process.stdout.off('error', onOutputError); process.stderr.off('error', onOutputError);
        fs.closeSync(outFd); fs.closeSync(errFd);
        const finalCleanupState = probeGroup('after-final-cleanup');
        const wasSuccessfulBeforeFinalCleanup = record.status === 'success';
        const cleanupRemainsUncertain = !['gone', 'not-started'].includes(finalCleanupState) || cleanupError || outputError || interrupted;
        Object.assign(record, { finalGroupState: finalCleanupState,
            processGroupExited: ['gone', 'not-started'].includes(finalCleanupState) && !cleanupError,
            unknownGroupOwnership: Boolean(record.unknownGroupOwnership || finalCleanupState === 'unknown'),
            cleanupError: cleanupError?.message ?? null, outputError: outputError?.message ?? null, interrupted });
        if (record.status === 'success' && cleanupRemainsUncertain) {
            Object.assign(record, { status: 'failure', endedAt: new Date().toISOString() });
        }
        event('deadline-check', { stage: 'after-log-close', deadlineMs: deadline || null, expired: Boolean(deadline && !cleanup && Date.now() >= deadline) });
        fs.writeFileSync(`${stem}.json`, JSON.stringify(record, null, 2) + '\n');
        if (cleanupRemainsUncertain && wasSuccessfulBeforeFinalCleanup) {
            throw new Error(`${label} failed final ownership cleanup: state=${finalCleanupState}, cleanup=${cleanupError?.message ?? 'none'}, output=${outputError?.message ?? 'none'}, interrupted=${interrupted}; raw evidence: ${stem}`);
        }
        if (record.status === 'success' && deadline && !cleanup && Date.now() >= deadline) {
            Object.assign(record, { status: 'failure', budgetExpired: true, endedAt: new Date().toISOString() });
            fs.writeFileSync(`${stem}.json`, JSON.stringify(record, null, 2) + '\n');
            throw new Error(`${label} exceeded its execution deadline during final log cleanup; raw evidence: ${stem}`);
        }
    }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    const [label, seconds, command, ...args] = process.argv.slice(2);
    runCommand(label, command, args, { timeoutMs: Number(seconds) * 1000, capture: false }).catch(error => {
        console.error(error.stack ?? error); process.exitCode = 1;
    });
}
