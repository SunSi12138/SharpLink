import fs from 'node:fs/promises';
import path from 'node:path';
import { spawnSync } from 'node:child_process';

const bundleId = 'com.sharplink.codeccompat.ios';
const inputFileName = 'sharplink-input.json';
const resultFileName = 'sharplink-result.json';
const builtinRawCategory = 'builtin-semantic-raw';
const probeTimeoutMs = Number(process.env.SHARPLINK_IOS_PROBE_TIMEOUT_MS ?? 300_000);
const maxProbeAttempts = Number(process.env.SHARPLINK_IOS_PROBE_ATTEMPTS ?? 2);
const probeTimeoutCode = 'SHARPLINK_IOS_PROBE_TIMEOUT';

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

function simctl(args, env = process.env) {
    const result = spawnSync('xcrun', ['simctl', ...args], { encoding: 'utf8', env });
    if (result.status !== 0) {
        throw new Error(`xcrun simctl ${args.join(' ')} failed (${result.status}):\n${result.stdout ?? ''}\n${result.stderr ?? ''}`);
    }
    return result.stdout ?? '';
}

function simctlDiagnostic(args) {
    const result = spawnSync('xcrun', ['simctl', ...args], {
        encoding: 'utf8',
        timeout: 15_000
    });
    return [
        `$ xcrun simctl ${args.join(' ')}`,
        `exit=${result.status ?? 'timeout'}`,
        result.stdout ?? '',
        result.stderr ?? ''
    ].join('\n');
}

function delay(milliseconds) {
    return new Promise(resolve => setTimeout(resolve, milliseconds));
}

async function waitForResult(resultPath, launchOutput) {
    const deadline = Date.now() + probeTimeoutMs;
    while (Date.now() < deadline) {
        try {
            return await fs.readFile(resultPath, 'utf8');
        } catch (error) {
            if (error?.code !== 'ENOENT') throw error;
        }
        await delay(250);
    }

    const diagnostics = [
        `simctl launch output:\n${launchOutput}`,
        simctlDiagnostic(['list', 'devices']),
        simctlDiagnostic(['get_app_container', 'booted', bundleId, 'app']),
        simctlDiagnostic(['get_app_container', 'booted', bundleId, 'data']),
        simctlDiagnostic([
            'spawn', 'booted', 'log', 'show',
            '--last', '5m',
            '--style', 'compact',
            '--predicate', 'process CONTAINS[c] "SharpLink" OR eventMessage CONTAINS[c] "SharpLink codec"'
        ])
    ].join('\n\n');
    const error = new Error(
        `iOS simulator probe timed out after ${probeTimeoutMs} ms waiting for container result file.\n${diagnostics}`);
    error.code = probeTimeoutCode;
    throw error;
}

async function runIos(mode, producerRoot, outputPath, commit, sdkVersion, targetFramework) {
    const input = mode === 'verify' ? JSON.stringify(await loadEnvelopes(producerRoot)) : null;

    const dataContainer = simctl(['get_app_container', 'booted', bundleId, 'data']).trim();
    if (!dataContainer) throw new Error('simctl returned an empty iOS app data-container path.');
    const documentsDirectory = path.join(dataContainer, 'Documents');
    const inputPath = path.join(documentsDirectory, inputFileName);
    const resultPath = path.join(documentsDirectory, resultFileName);
    await fs.mkdir(documentsDirectory, { recursive: true });
    await fs.rm(inputPath, { force: true });
    if (input !== null) await fs.writeFile(inputPath, input, 'utf8');

    const launchEnv = {
        ...process.env,
        SIMCTL_CHILD_SHARPLINK_MODE: mode,
        SIMCTL_CHILD_SHARPLINK_COMMIT: commit,
        SIMCTL_CHILD_SHARPLINK_SDK_VERSION: sdkVersion,
        SIMCTL_CHILD_SHARPLINK_TARGET_FRAMEWORK: targetFramework
    };

    let resultText;
    try {
        for (let attempt = 1; attempt <= maxProbeAttempts; attempt++) {
            try { simctl(['terminate', 'booted', bundleId]); } catch {}
            await fs.rm(resultPath, { force: true });
            if (input !== null) await fs.writeFile(inputPath, input, 'utf8');

            if (attempt > 1) {
                console.warn(`Retrying iOS simulator probe (${attempt}/${maxProbeAttempts}) after timeout.`);
                await delay(3_000);
            }

            const launchOutput = simctl(
                ['launch', '--terminate-running-process', 'booted', bundleId],
                launchEnv);
            console.log(`iOS simulator launch attempt ${attempt}/${maxProbeAttempts}: ${launchOutput.trim()}`);
            console.log(`iOS simulator data container: ${dataContainer}`);

            try {
                resultText = await waitForResult(resultPath, launchOutput);
                break;
            } catch (error) {
                if (error?.code !== probeTimeoutCode || attempt === maxProbeAttempts) {
                    throw error;
                }
                console.warn(error.message);
                try { simctl(['terminate', 'booted', bundleId]); } catch {}
            }
        }

        if (resultText === undefined) {
            throw new Error('iOS simulator probe completed without a result.');
        }

        const parsed = JSON.parse(resultText);
        if (parsed?.portableProbeError) {
            throw new Error(parsed.portableProbeError);
        }
        if (mode === 'produce') {
            await writeCorpus(parsed, outputPath);
            console.log(`iOS simulator producer wrote ${parsed.manifest?.cases?.length ?? 0} fixtures for ${parsed.manifest?.platformTag}.`);
        } else {
            await fs.mkdir(path.dirname(outputPath), { recursive: true });
            await fs.writeFile(outputPath, JSON.stringify(parsed, null, 2) + '\n', 'utf8');
            const blocking = (parsed.results ?? []).filter(item => item.blocking).length;
            console.log(`iOS simulator consumer verified ${parsed.results?.length ?? 0} entries; blocking failures: ${blocking}.`);
            if (blocking !== 0) process.exitCode = 1;
        }
    } finally {
        try { simctl(['terminate', 'booted', bundleId]); } catch {}
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
