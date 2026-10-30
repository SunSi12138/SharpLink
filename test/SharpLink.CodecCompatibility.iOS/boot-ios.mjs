import fs from 'node:fs/promises';
import fsSync from 'node:fs';
import path from 'node:path';
import { runCommand, shortenedTimeout } from './ios-command.mjs';

// One phase budget, not eight minutes per command. The caller's 40-minute deadline can only shorten it.
const phaseStartedMono = performance.now();
const phaseStartedWall = Date.now();
const phaseBudgetMs = shortenedTimeout('SHARPLINK_IOS_PREP_TIMEOUT_MS', 480_000);
const callerDeadline = Number(process.env.SHARPLINK_IOS_EXECUTION_DEADLINE_MS || Infinity);
if (callerDeadline !== Infinity && (!Number.isSafeInteger(callerDeadline) || callerDeadline <= 0)) {
    throw new Error('Invalid iOS caller execution deadline.');
}
const phaseDeadline = Math.min(phaseStartedWall + phaseBudgetMs, callerDeadline);
// Process-local only: never rewrite GITHUB_ENV's original 40-minute job budget.
process.env.SHARPLINK_IOS_EXECUTION_DEADLINE_MS = String(phaseDeadline);
process.env.SHARPLINK_IOS_DEADLINE_SCOPE = 'simulator-preparation';
process.env.SHARPLINK_IOS_ORIGINAL_EXECUTION_DEADLINE_MS = callerDeadline === Infinity ? '' : String(callerDeadline);
process.env.SHARPLINK_IOS_PREPARATION_DEADLINE_MS = String(phaseStartedWall + phaseBudgetMs);
const diagnosticsDirectory = process.env.SHARPLINK_IOS_DIAGNOSTICS || 'artifacts/codec-compat/ios-diagnostics';
let handoffWritten = false;
function phaseEvent(type, fields = {}) {
    fsSync.mkdirSync(diagnosticsDirectory, { recursive: true });
    fsSync.appendFileSync(path.join(diagnosticsDirectory, 'preparation-events.jsonl'), JSON.stringify({
        type, wallTime: new Date().toISOString(), monotonicElapsedMs: performance.now() - phaseStartedMono,
        deadlineScope: 'simulator-preparation', phaseBudgetMs, effectiveDeadlineMs: phaseDeadline,
        preparationDeadlineMs: phaseStartedWall + phaseBudgetMs,
        originalExecutionDeadlineMs: callerDeadline === Infinity ? null : callerDeadline, ...fields
    }) + '\n');
}
function remainingPhaseMs() {
    const remaining = Math.floor(Math.min(phaseBudgetMs - (performance.now() - phaseStartedMono), phaseDeadline - Date.now()));
    if (remaining <= 0) {
        const cause = Date.now() >= callerDeadline ? 'caller execution' : 'preparation phase';
        throw new Error(`iOS ${cause} deadline exhausted; no next command or successful UDID handoff.`);
    }
    return remaining;
}
async function runPhase(label, args, capMs) {
    const result = await runCommand(label, 'xcrun', args, {
        timeoutMs: Math.min(capMs, remainingPhaseMs()),
        // Absolute in this Node process: log setup cannot restart or extend the phase.
        monotonicDeadlineMs: phaseStartedMono + phaseBudgetMs
    });
    remainingPhaseMs();
    return result;
}

try {
    phaseEvent('phase-start');
    console.log(`iOS preparation budget: ${phaseBudgetMs} ms; effective deadline=${new Date(phaseDeadline).toISOString()}; no retries.`);
    const { stdout } = await runPhase('simulator-list', ['simctl', 'list', 'devices', 'available', '-j'], 15_000);
    const data = JSON.parse(stdout);
    let selected;
    for (const [runtime, devices] of Object.entries(data.devices)) {
        if (!runtime.includes('iOS')) continue;
        const device = devices.find(item => item.isAvailable && item.name?.startsWith('iPhone'));
        if (device) { selected = { ...device, runtime }; break; }
    }
    if (!selected || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(selected.udid)) throw new Error('No available iPhone simulator found.');
    console.log(`Selected simulator: ${selected.name}, runtime=${selected.runtime}, udid=${selected.udid}, initialState=${selected.state}`);
    if (selected.state !== 'Shutdown') {
        await runPhase('simulator-shutdown', ['simctl', 'shutdown', selected.udid], 30_000);
    }
    await runPhase('simulator-boot', ['simctl', 'boot', selected.udid], 90_000);
    await runPhase('simulator-ready', ['simctl', 'bootstatus', selected.udid, '-b'], 480_000);
    if (!process.env.GITHUB_ENV) throw new Error('GITHUB_ENV is required to hand off the exact simulator.');
    remainingPhaseMs();
    phaseEvent('handoff-write-start', { udid: selected.udid });
    remainingPhaseMs();
    await fs.appendFile(process.env.GITHUB_ENV, `IOS_SIMULATOR_UDID=${selected.udid}\n`);
    handoffWritten = true;
    phaseEvent('handoff-written', { udid: selected.udid });
    remainingPhaseMs();
    phaseEvent('phase-success');
    remainingPhaseMs();
} catch (error) {
    try { phaseEvent('phase-failure', { error: error.message, handoffWritten, lateHandoffFailure: handoffWritten }); }
    catch (diagnosticError) { console.error(`Preparation event persistence failed: ${diagnosticError.message}`); }
    console.error(error.stack ?? error);
    process.exitCode = 1;
}
