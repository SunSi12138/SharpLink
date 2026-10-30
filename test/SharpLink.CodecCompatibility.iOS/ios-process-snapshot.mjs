// Diagnostic only: a bounded, read-only view of the owned process group. Never a cleanup verdict.
import { spawn } from 'node:child_process';

const snapshotTimeoutMs = 250;
const outputLimitBytes = 16 * 1024;

export function captureOwnedGroupSnapshot(ownedPgid, { spawnImpl = spawn } = {}) {
    if (!Number.isSafeInteger(ownedPgid) || ownedPgid <= 1) {
        return Promise.resolve({ status: 'unknown', reason: 'invalid-pgid', ownedPgid, rows: [] });
    }
    const started = process.hrtime.bigint();
    const elapsedMs = () => Number(process.hrtime.bigint() - started) / 1e6;
    const args = ['-x', '-g', String(ownedPgid), '-o', 'pid=,ppid=,pgid=,uid=,ruid=,stat=,etime=,ucomm='];
    return new Promise(resolve => {
        let child, timer, settleTimer, complete = false, stopping = false, closed = false, reason = null, exitCode = null, signal = null;
        let outputBytes = 0;
        const chunks = [];
        const result = { ownedPgid, snapshotPid: null, command: '/bin/ps', args, timeoutMs: snapshotTimeoutMs,
            outputLimitBytes, status: 'unknown', rows: [], closed: false, killAttempted: false, killReturnValue: null,
            referenceCleanup: { stdoutDestroyCalled: false, stderrDestroyCalled: false, unrefCalled: false }, errors: [], droppedErrorCount: 0 };
        const recordError = (source, error) => {
            const detail = { source, code: error.code ?? null, message: error.message };
            if (result.errors.length < 16) result.errors.push(detail); else result.droppedErrorCount++;
            const field = source.startsWith('kill') ? 'killError' : source === 'spawn' ? 'spawnError' : 'childError';
            if (!result[field] || (!result[field].code && detail.code)) result[field] = detail;
        };
        const releaseReferences = () => {
            for (const [field, target, method] of [
                ['stdoutDestroyCalled', child?.stdout, 'destroy'],
                ['stderrDestroyCalled', child?.stderr, 'destroy'],
                ['unrefCalled', child, 'unref']
            ]) {
                if (!target) continue;
                try { target[method](); result.referenceCleanup[field] = true; }
                catch (error) { recordError(`reference-${field}`, error); }
            }
        };
        const finish = () => {
            if (complete) return;
            complete = true;
            clearTimeout(timer); clearTimeout(settleTimer);
            Object.assign(result, { exitCode, signal, outputBytes, closed, elapsedMs: elapsedMs() });
            if (!reason && elapsedMs() >= snapshotTimeoutMs) reason = 'timeout';
            if (!reason && signal !== null) reason = 'signaled';
            if (!reason && exitCode !== 0) reason = 'nonzero-exit';
            if (!reason) {
                const text = Buffer.concat(chunks).toString('utf8');
                if (text.length && !text.endsWith('\n')) reason = 'truncated-output';
                const lines = text.split(/\r?\n/).map(line => line.trim()).filter(Boolean);
                if (lines.length === 0) reason = 'empty-output'; // Not proof that the group has gone.
                for (const line of lines) {
                    const match = /^(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\S+)\s+(\S+)\s+([^\x00-\x1f\x7f]+)$/.exec(line);
                    if (!match) { reason = 'malformed-output'; break; }
                    const [pid, ppid, pgid, uid, ruid] = match.slice(1, 6).map(Number);
                    if (![pid, ppid, pgid, uid, ruid].every(Number.isSafeInteger) || pid <= 0 || ppid < 0 || uid < 0 || ruid < 0) {
                        reason = 'malformed-output'; break;
                    }
                    if (pgid !== ownedPgid) { reason = 'wrong-pgid'; break; }
                    result.rows.push({ pid, ppid, pgid, uid, ruid, stat: match[6], elapsed: match[7], program: match[8] });
                }
            }
            if (reason) { result.reason = reason; result.rows = []; }
            else { result.status = 'observed-members'; result.reason = null; }
            resolve(result);
        };
        const stopSnapshot = failure => {
            reason ??= failure;
            if (complete || stopping) return;
            stopping = true;
            // Install the last escape before kill: an asynchronous 'error' is not a 'close'.
            settleTimer ??= setTimeout(() => {
                releaseReferences();
                finish(); // Unknown live-child state remains explicit; releasing refs is not successful termination.
            }, 100);
            // The diagnostic child is distinct from the owned workload. Never signal a group here.
            if (Number.isSafeInteger(child?.pid) && child.pid > 1) {
                result.killAttempted = true;
                try {
                    result.killReturnValue = child.kill('SIGKILL');
                    if (result.killReturnValue === false) recordError('kill-return', new Error('SIGKILL returned false; termination is unconfirmed.'));
                } catch (error) { recordError('kill-call', error); }
            }
        };
        try {
            child = spawnImpl('/bin/ps', args, {
                env: { COMMAND_MODE: 'unix2003', LC_ALL: 'C' },
                stdio: ['ignore', 'pipe', 'pipe'], detached: false
            });
            result.snapshotPid = Number.isSafeInteger(child.pid) ? child.pid : null;
            const collect = (chunk, stdout) => {
                if (complete) return;
                outputBytes += chunk.length;
                if (outputBytes > outputLimitBytes) { stopSnapshot('output-overflow'); return; }
                if (stdout) chunks.push(Buffer.from(chunk));
                // stderr is counted against the cap but not retained; diagnostics need no extra process data.
            };
            child.stdout.on('data', chunk => collect(chunk, true));
            child.stderr.on('data', chunk => collect(chunk, false));
            child.stdout.on('error', error => { if (!complete) { recordError('stdout-stream', error); stopSnapshot('stream-error'); } });
            child.stderr.on('error', error => { if (!complete) { recordError('stderr-stream', error); stopSnapshot('stream-error'); } });
            child.on('error', error => {
                if (complete) return;
                const source = stopping ? 'kill-event' : Number.isSafeInteger(child.pid) && child.pid > 1 ? 'child' : 'spawn';
                recordError(source, error);
                stopSnapshot(`${source}-error`); // Keep the settle timer until actual close or bounded ref release.
            });
            child.on('close', (code, sig) => { if (complete) return; closed = true; exitCode = code; signal = sig; finish(); });
            timer = setTimeout(() => stopSnapshot('timeout'), Math.max(1, snapshotTimeoutMs - elapsedMs()));
        } catch (error) {
            recordError(child ? 'child-setup' : 'spawn', error);
            if (child) stopSnapshot('child-setup-error');
            else { reason = 'spawn-error'; finish(); }
        }
    });
}
