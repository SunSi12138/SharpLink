import fs from 'node:fs/promises';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { loadEnvelopes, writeCorpus } from '../SharpLink.CodecCompatibility.Browser/portable-artifacts.mjs';

const bundleId = 'com.sharplink.codeccompat.ios';
const inputFileName = 'sharplink-input.json';
const resultFileName = 'sharplink-result.json';

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
    const deadline = Date.now() + 120_000;
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
        simctlDiagnostic(['get_app_container', 'booted', bundleId, 'app']),
        simctlDiagnostic(['get_app_container', 'booted', bundleId, 'data']),
        simctlDiagnostic([
            'spawn', 'booted', 'log', 'show',
            '--last', '3m',
            '--style', 'compact',
            '--predicate', 'process CONTAINS[c] "SharpLink" OR eventMessage CONTAINS[c] "SharpLink codec"'
        ])
    ].join('\n\n');
    throw new Error(`iOS simulator probe timed out waiting for container result file.\n${diagnostics}`);
}

async function runIos(mode, producerRoot, outputPath, commit, sdkVersion, targetFramework) {
    const input = mode === 'verify' ? JSON.stringify(await loadEnvelopes(producerRoot)) : null;

    try { simctl(['terminate', 'booted', bundleId]); } catch {}

    const dataContainer = simctl(['get_app_container', 'booted', bundleId, 'data']).trim();
    if (!dataContainer) throw new Error('simctl returned an empty iOS app data-container path.');
    const documentsDirectory = path.join(dataContainer, 'Documents');
    const inputPath = path.join(documentsDirectory, inputFileName);
    const resultPath = path.join(documentsDirectory, resultFileName);
    await fs.mkdir(documentsDirectory, { recursive: true });
    await fs.rm(resultPath, { force: true });
    await fs.rm(inputPath, { force: true });
    if (input !== null) await fs.writeFile(inputPath, input, 'utf8');

    const launchEnv = {
        ...process.env,
        SIMCTL_CHILD_SHARPLINK_MODE: mode,
        SIMCTL_CHILD_SHARPLINK_COMMIT: commit,
        SIMCTL_CHILD_SHARPLINK_SDK_VERSION: sdkVersion,
        SIMCTL_CHILD_SHARPLINK_TARGET_FRAMEWORK: targetFramework
    };
    const launchOutput = simctl(
        ['launch', '--terminate-running-process', 'booted', bundleId],
        launchEnv);
    console.log(`iOS simulator launch: ${launchOutput.trim()}`);
    console.log(`iOS simulator data container: ${dataContainer}`);

    try {
        const resultText = await waitForResult(resultPath, launchOutput);
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
