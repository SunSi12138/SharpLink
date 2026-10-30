import fs from 'node:fs/promises';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { runCommand, shortenedTimeout } from './ios-command.mjs';

const bundleId = 'com.sharplink.codeccompat.ios';
const inputFileName = 'sharplink-input.json';
const resultFileName = 'sharplink-result.json';
const builtinRawCategory = 'builtin-semantic-raw';
const probeTimeoutMs = shortenedTimeout('SHARPLINK_IOS_PROBE_TIMEOUT_MS', 300_000);
const commandTimeoutMs = shortenedTimeout('SHARPLINK_IOS_COMMAND_TIMEOUT_MS', 90_000);
const cleanupTimeoutMs = shortenedTimeout('SHARPLINK_IOS_CLEANUP_TIMEOUT_MS', 15_000);
if (process.env.SHARPLINK_IOS_PROBE_ATTEMPTS && process.env.SHARPLINK_IOS_PROBE_ATTEMPTS !== '1') {
    throw new Error('SHARPLINK_IOS_PROBE_ATTEMPTS must be 1; automatic probe retries are prohibited.');
}
const simulatorUdid = process.env.IOS_SIMULATOR_UDID;
if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(simulatorUdid ?? '')) {
    throw new Error('IOS_SIMULATOR_UDID must identify the exact installed simulator.');
}
const diagnosticsDirectory = process.env.SHARPLINK_IOS_DIAGNOSTICS || 'artifacts/codec-compat/ios-diagnostics';
let interrupted;
const onSignal = signal => {
    interrupted = signal;
    process.exitCode = 1;
    console.error(`iOS probe received ${signal}; cancellation remains a failure through final evidence preservation.`);
};
process.on('SIGTERM', () => onSignal('SIGTERM'));
process.on('SIGINT', () => onSignal('SIGINT'));

async function findManifestFiles(root) {
    const found = [];
    async function visit(directory) {
        for (const entry of await fs.readdir(directory, { withFileTypes: true })) {
            const fullPath = path.join(directory, entry.name);
            if (entry.isDirectory()) {
                await visit(fullPath);
            } else if (entry.isFile() && entry.name === 'manifest.json') {
                found.push(fullPath);
            }
        }
    }
    await visit(root);
    found.sort((left, right) => left.localeCompare(right));
    return found;
}

async function loadEnvelopes(root) {
    const manifestFiles = await findManifestFiles(root);
    if (manifestFiles.length === 0) {
        throw new Error(`No manifest.json files found under ${root}`);
    }

    const excludeBuiltinRaw = process.env.SHARPLINK_SKIP_BUILTIN_RAW === '1';
    const envelopes = [];
    for (const manifestFile of manifestFiles) {
        const originalManifest = JSON.parse(await fs.readFile(manifestFile, 'utf8'));
        if (originalManifest?.schemaVersion !== 1 || !Array.isArray(originalManifest?.cases)) {
            throw new Error(`Invalid portable manifest ${manifestFile}.`);
        }
        const cases = originalManifest.cases.filter(
            item => !excludeBuiltinRaw || item?.category !== builtinRawCategory);
        const manifest = { ...originalManifest, cases };
        const corpusRoot = path.dirname(manifestFile);
        const caseBytesBase64 = {};
        for (const item of cases) {
            const wirePath = path.join(corpusRoot, ...String(item.wireFile).split('/'));
            caseBytesBase64[item.id] = (await fs.readFile(wirePath)).toString('base64');
        }
        envelopes.push({ schemaVersion: 1, manifest, caseBytesBase64 });
    }
    return envelopes;
}

async function writeCorpus(envelope, outputDirectory) {
    if (envelope?.schemaVersion !== 1 || !envelope?.manifest || !envelope?.caseBytesBase64) {
        throw new Error('Portable producer output is not a corpus envelope.');
    }

    await fs.rm(outputDirectory, { recursive: true, force: true });
    await fs.mkdir(path.join(outputDirectory, 'cases'), { recursive: true });
    await fs.writeFile(
        path.join(outputDirectory, 'manifest.json'),
        JSON.stringify(envelope.manifest, null, 2) + '\n',
        'utf8');

    for (const item of envelope.manifest.cases ?? []) {
        const encoded = envelope.caseBytesBase64[item.id];
        if (typeof encoded !== 'string') {
            throw new Error(`Portable envelope is missing ${item.id}.`);
        }
        const wirePath = path.join(outputDirectory, ...String(item.wireFile).split('/'));
        await fs.mkdir(path.dirname(wirePath), { recursive: true });
        await fs.writeFile(wirePath, Buffer.from(encoded, 'base64'));
    }
}

async function simctl(label, args, env = process.env, cleanup = false) {
    const result = await runCommand(label, 'xcrun', ['simctl', ...args], {
        env, timeoutMs: cleanup ? cleanupTimeoutMs : Math.min(commandTimeoutMs, label.endsWith('-get-container') ? 15_000 : 90_000), cleanup
    });
    return result.stdout;
}

async function preserveResult(resultPath, label) {
    await fs.mkdir(diagnosticsDirectory, { recursive: true });
    for (const suffix of ['', '.tmp']) {
        try {
            // Unique immutable copies preserve malformed, partial, stale, and failing samples as well as successes.
            await fs.copyFile(resultPath + suffix,
                path.join(diagnosticsDirectory, `${label}-${randomUUID()}-result.json${suffix}`), fs.constants.COPYFILE_EXCL);
        } catch (error) { if (error.code !== 'ENOENT') throw error; }
    }
}

async function waitForResult(resultPath) {
    const executionDeadline = Number(process.env.SHARPLINK_IOS_EXECUTION_DEADLINE_MS || Infinity);
    const deadline = Math.min(Date.now() + probeTimeoutMs, executionDeadline);
    while (Date.now() < deadline) {
        if (interrupted) throw new Error(`iOS probe interrupted by ${interrupted}; cleanup remains mandatory.`);
        try { return await fs.readFile(resultPath, 'utf8'); }
        catch (error) { if (error.code !== 'ENOENT') throw error; }
        await new Promise(resolve => setTimeout(resolve, Math.min(250, Math.max(1, deadline - Date.now()))));
    }
    throw new Error(`iOS simulator probe timed out waiting for result (limit ${probeTimeoutMs} ms; one attempt, no retry).`);
}

async function runIos(mode, producerRoot, outputPath, commit, sdkVersion, targetFramework) {
    const input = mode === 'verify' ? JSON.stringify(await loadEnvelopes(producerRoot)) : null;
    const dataContainer = (await simctl(`${mode}-get-container`, ['get_app_container', simulatorUdid, bundleId, 'data'])).trim();
    if (!dataContainer) throw new Error('simctl returned an empty iOS app data-container path.');
    const documentsDirectory = path.join(dataContainer, 'Documents');
    const inputPath = path.join(documentsDirectory, inputFileName);
    const resultPath = path.join(documentsDirectory, resultFileName);
    await fs.mkdir(documentsDirectory, { recursive: true });
    await preserveResult(resultPath, `${mode}-before`);
    await fs.rm(resultPath, { force: true });
    await fs.rm(resultPath + '.tmp', { force: true });
    await fs.rm(inputPath, { force: true });
    if (input !== null) await fs.writeFile(inputPath, input, 'utf8');
    const launchEnv = {
        ...process.env,
        SIMCTL_CHILD_SHARPLINK_MODE: mode,
        SIMCTL_CHILD_SHARPLINK_COMMIT: commit,
        SIMCTL_CHILD_SHARPLINK_SDK_VERSION: sdkVersion,
        SIMCTL_CHILD_SHARPLINK_TARGET_FRAMEWORK: targetFramework
    };
    let failure;
    try {
        if (interrupted) throw new Error(`iOS probe interrupted by ${interrupted} before launch.`);
        const launchOutput = await simctl(`${mode}-launch`,
            ['launch', '--terminate-running-process', simulatorUdid, bundleId], launchEnv);
        console.log(`iOS simulator launch attempt 1/1: ${launchOutput.trim()}`);
        const parsed = JSON.parse(await waitForResult(resultPath));
        if (parsed?.portableProbeError) throw new Error(parsed.portableProbeError);
        if (mode === 'produce') {
            await writeCorpus(parsed, outputPath);
            console.log(`iOS simulator producer wrote ${parsed.manifest?.cases?.length ?? 0} fixtures for ${parsed.manifest?.platformTag}.`);
        } else {
            await fs.mkdir(path.dirname(outputPath), { recursive: true });
            await fs.writeFile(outputPath, JSON.stringify(parsed, null, 2) + '\n', 'utf8');
            const blocking = (parsed.results ?? []).filter(item => item.blocking).length;
            console.log(`iOS simulator consumer verified ${parsed.results?.length ?? 0} entries; blocking failures: ${blocking}.`);
            if (blocking !== 0) throw new Error(`iOS verification has ${blocking} blocking result(s).`);
        }
        const executionDeadline = Number(process.env.SHARPLINK_IOS_EXECUTION_DEADLINE_MS || Infinity);
        if (Date.now() >= executionDeadline) {
            throw new Error('iOS normal execution exceeded its deadline while processing or writing the result.');
        }
    } catch (error) { failure = error; }
    if (interrupted && !failure) failure = new Error(`iOS probe interrupted by ${interrupted}.`);
    const finalErrors = [];
    if (failure) finalErrors.push(failure);
    try { await simctl(`${mode}-terminate`, ['terminate', simulatorUdid, bundleId], process.env, true); }
    catch (error) { finalErrors.push(error); }
    try { await preserveResult(resultPath, `${mode}-after`); }
    catch (error) { finalErrors.push(error); }
    if (interrupted && !finalErrors.some(error => String(error.message).includes(interrupted))) {
        finalErrors.push(new Error(`iOS probe interrupted by ${interrupted} during cleanup or evidence preservation.`));
    }
    if (finalErrors.length) {
        throw new AggregateError(finalErrors, finalErrors.map((error, index) => `Failure ${index + 1}: ${error.stack ?? error}`).join('\n'));
    }
}

const args = process.argv.slice(2);
const mode = args[0];
if (mode === 'produce' && args.length === 5) {
    runIos('produce', null, args[1], args[2], args[3], args[4]).catch(error => {
        console.error(error.stack ?? error);
        process.exitCode = 1;
    });
} else if (mode === 'verify' && args.length === 6) {
    runIos('verify', args[1], args[2], args[3], args[4], args[5]).catch(error => {
        console.error(error.stack ?? error);
        process.exitCode = 1;
    });
} else {
    console.error('Usage: run-ios.mjs produce <corpus-output> <commit> <sdk> <target-framework>');
    console.error('   or: run-ios.mjs verify <producer-root> <report-output> <commit> <sdk> <target-framework>');
    process.exit(2);
}
