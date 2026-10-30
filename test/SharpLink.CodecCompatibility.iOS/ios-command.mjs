// Bounded subprocess ownership for iOS CI. Logs contain this command's output, never its environment.
import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

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
    // The workflow reserves its final minutes for evidence upload. Cleanup retains its own short bound.
    const deadline = Number(process.env.SHARPLINK_IOS_EXECUTION_DEADLINE_MS || 0);
    if (!Number.isSafeInteger(deadline) || deadline < 0) throw new Error('Invalid iOS execution deadline.');
    const budgetExpired = Boolean(deadline && !cleanup && deadline <= Date.now());
    if (deadline && !cleanup) timeoutMs = Math.min(timeoutMs, Math.max(1, deadline - Date.now()));
    const directory = process.env.SHARPLINK_IOS_DIAGNOSTICS || 'artifacts/codec-compat/ios-diagnostics';
    fs.mkdirSync(directory, { recursive: true });
    const stem = path.join(directory, `${Date.now()}-${process.pid}-${++sequence}-${label}`);
    const outFd = fs.openSync(`${stem}.stdout.log`, 'wx');
    const errFd = fs.openSync(`${stem}.stderr.log`, 'wx');
    const startedAt = new Date().toISOString();
    const record = { label, command, args, startedAt, timeoutMs, status: 'running' };
    fs.writeFileSync(`${stem}.json`, JSON.stringify(record, null, 2) + '\n', { flag: 'wx' });
    console.log(`[ios-command] START ${label} timeoutMs=${timeoutMs}`);
    let stdout = '', stderr = '', child, exitCode = null, signal = null, spawnError, outputError, cleanupError;
    let exited = false, closed = false, timedOut = false, pipeLeak = false;
    let timer, pipeTimer, watchdog;
    let interrupted = null;
    let resolveClosed;
    const closePromise = new Promise(resolve => { resolveClosed = resolve; });
    const append = (fd, stream, chunk, isStdout) => {
        try { fs.writeSync(fd, chunk); } catch (error) { outputError ??= error; }
        try { stream.write(chunk); } catch (error) { outputError ??= error; }
        if (outputError) void stop();
        // Full output always remains in raw logs; only small command return values need memory capture.
        if (capture) {
            if (isStdout) stdout += chunk.toString(); else stderr += chunk.toString();
        }
    };
    const groupAlive = () => {
        if (!child?.pid) return false;
        try { process.kill(-child.pid, 0); return true; }
        catch (error) {
            if (error.code === 'ESRCH') return false;
            cleanupError ??= error; return true; // Unknown is never proof of group exit.
        }
    };
    const send = sig => {
        if (!child?.pid) return;
        try { process.kill(-child.pid, sig); }
        catch (error) { if (error.code !== 'ESRCH') cleanupError ??= error; }
    };
    let stopping;
    const stop = () => stopping ??= (async () => {
        send('SIGTERM');
        const termDeadline = Date.now() + 1000;
        while ((!closed || groupAlive()) && Date.now() < termDeadline) await sleep(25);
        if (!closed || groupAlive()) send('SIGKILL');
        const killDeadline = Date.now() + 2000;
        while ((!closed || groupAlive()) && Date.now() < killDeadline) await sleep(25);
        // Do not wait forever for inherited output pipes after killing the owned group.
        if (!closed) {
            child?.stdout?.destroy(); child?.stderr?.destroy();
            await Promise.race([closePromise, sleep(250)]);
            if (!closed) child?.unref(); // Report uncertain cleanup, but do not hang the supervisor itself.
        }
    })();
    const onInterrupt = sig => { interrupted = sig; void stop(); };
    const onTerm = () => onInterrupt('SIGTERM');
    const onInt = () => onInterrupt('SIGINT');
    const onOutputError = error => { outputError ??= error; void stop(); };
    process.stdout.on('error', onOutputError); process.stderr.on('error', onOutputError);
    process.on('SIGTERM', onTerm); process.on('SIGINT', onInt);
    try {
        if (budgetExpired) {
            spawnError = new Error('iOS execution budget exhausted; command was not started.');
            closed = true; resolveClosed();
        } else {
            child = spawn(command, args, { env, detached: true, stdio: ['ignore', 'pipe', 'pipe'] });
            child.stdout.on('data', chunk => append(outFd, process.stdout, chunk, true));
            child.stderr.on('data', chunk => append(errFd, process.stderr, chunk, false));
            child.on('error', error => { spawnError = error; });
            child.on('exit', (code, sig) => {
                exited = true; exitCode = code; signal = sig;
                pipeTimer = setTimeout(() => { if (!closed) { pipeLeak = true; void stop(); } }, 1000);
            });
            child.on('close', (code, sig) => {
                closed = true; exitCode = code; signal = sig; clearTimeout(pipeTimer); resolveClosed();
            });
        }
        timer = setTimeout(() => { timedOut = true; void stop(); }, timeoutMs);
        await Promise.race([closePromise, new Promise(resolve => { watchdog = setTimeout(resolve, timeoutMs + 4000); })]);
        clearTimeout(watchdog);
        clearTimeout(timer); clearTimeout(pipeTimer);
        // A delayed JS timer must not turn a command completed after its limit into success.
        if (!budgetExpired && Date.now() >= Date.parse(startedAt) + timeoutMs) timedOut = true;
        const lingering = groupAlive();
        if (!closed || lingering) await stop();
        if (stopping) await stopping;
        const groupExited = !groupAlive() && !cleanupError;
        const ok = !cleanupError && !outputError && !interrupted && !spawnError && !timedOut && !pipeLeak && !lingering && closed && exitCode === 0 && signal === null && groupExited;
        Object.assign(record, { endedAt: new Date().toISOString(), status: ok ? 'success' : 'failure', exitCode, signal,
            timedOut, budgetExpired, interrupted, pipeLeak, lingeringDescendants: lingering, childExited: exited, pipesClosed: closed, processGroupExited: groupExited,
            spawnError: spawnError?.message ?? null, outputError: outputError?.message ?? null, cleanupError: cleanupError?.message ?? null });
        fs.writeFileSync(`${stem}.json`, JSON.stringify(record, null, 2) + '\n');
        console.log(`[ios-command] END ${label} status=${record.status} exit=${exitCode} signal=${signal} timeout=${timedOut} groupExited=${groupExited}`);
        if (!ok) {
            const error = new Error(`${label} failed: exit=${exitCode}, signal=${signal}, timeout=${timedOut}, spawn=${spawnError?.message ?? 'none'}, output=${outputError?.message ?? 'none'}, cleanup=${cleanupError?.message ?? 'none'}, pipesClosed=${closed}, groupExited=${groupExited}; raw evidence: ${stem}`);
            error.commandRecord = record;
            throw error;
        }
        return { stdout, stderr, record };
    } finally {
        // Unexpected logger/metadata errors must never abandon an owned live group.
        if (child?.pid && (!closed || groupAlive())) await stop();
        if (stopping) await stopping;
        clearTimeout(timer); clearTimeout(pipeTimer); clearTimeout(watchdog);
        process.off('SIGTERM', onTerm); process.off('SIGINT', onInt);
        process.stdout.off('error', onOutputError); process.stderr.off('error', onOutputError);
        fs.closeSync(outFd); fs.closeSync(errFd);
    }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    const [label, seconds, command, ...args] = process.argv.slice(2);
    runCommand(label, command, args, { timeoutMs: Number(seconds) * 1000, capture: false }).catch(error => {
        console.error(error.stack ?? error); process.exitCode = 1;
    });
}
