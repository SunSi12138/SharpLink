import fs from 'node:fs/promises';
import http from 'node:http';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { loadEnvelopes, writeCorpus } from '../SharpLink.CodecCompatibility.Browser/portable-artifacts.mjs';

function simctl(args, env = process.env) {
    const result = spawnSync('xcrun', ['simctl', ...args], { encoding: 'utf8', env });
    if (result.status !== 0) {
        throw new Error(`xcrun simctl ${args.join(' ')} failed (${result.status}):\n${result.stdout}\n${result.stderr}`);
    }
    return result.stdout;
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

async function runIos(mode, producerRoot, outputPath, commit, sdkVersion, targetFramework) {
    const input = mode === 'verify' ? JSON.stringify(await loadEnvelopes(producerRoot)) : null;
    let resolveResult;
    let rejectResult;
    const resultPromise = new Promise((resolve, reject) => {
        resolveResult = resolve;
        rejectResult = reject;
    });

    const server = http.createServer(async (request, response) => {
        try {
            const url = new URL(request.url, 'http://127.0.0.1');
            if (request.method === 'GET' && url.pathname === '/input.json') {
                if (input === null) {
                    response.writeHead(404);
                    response.end('no input');
                    return;
                }
                response.writeHead(200, { 'content-type': 'application/json; charset=utf-8' });
                response.end(input);
                return;
            }
            if (request.method === 'POST' && url.pathname === '/result') {
                const chunks = [];
                for await (const chunk of request) chunks.push(chunk);
                const body = Buffer.concat(chunks).toString('utf8');
                response.writeHead(204);
                response.end();
                resolveResult(body);
                return;
            }
            response.writeHead(404);
            response.end('not found');
        } catch (error) {
            response.writeHead(500);
            response.end('server error');
            rejectResult(error);
        }
    });

    await new Promise((resolve, reject) => {
        server.once('error', reject);
        server.listen(8124, '127.0.0.1', resolve);
    });

    try { simctl(['terminate', 'booted', 'com.sharplink.codeccompat.ios']); } catch {}
    const launchEnv = {
        ...process.env,
        SIMCTL_CHILD_SHARPLINK_MODE: mode,
        SIMCTL_CHILD_SHARPLINK_ENDPOINT: 'http://127.0.0.1:8124',
        SIMCTL_CHILD_SHARPLINK_COMMIT: commit,
        SIMCTL_CHILD_SHARPLINK_SDK_VERSION: sdkVersion,
        SIMCTL_CHILD_SHARPLINK_TARGET_FRAMEWORK: targetFramework
    };
    const launchOutput = simctl(
        ['launch', '--terminate-running-process', 'booted', 'com.sharplink.codeccompat.ios'],
        launchEnv);
    console.log(`iOS simulator launch: ${launchOutput.trim()}`);

    const timeout = setTimeout(() => {
        const diagnostics = [
            simctlDiagnostic(['get_app_container', 'booted', 'com.sharplink.codeccompat.ios', 'app']),
            simctlDiagnostic([
                'spawn', 'booted', 'log', 'show',
                '--last', '3m',
                '--style', 'compact',
                '--predicate', 'process CONTAINS[c] "SharpLink" OR eventMessage CONTAINS[c] "SharpLink codec"'
            ])
        ].join('\n\n');
        rejectResult(new Error(`iOS simulator probe timed out.\n${diagnostics}`));
    }, 120_000);

    try {
        const resultText = await resultPromise;
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
        clearTimeout(timeout);
        try { simctl(['terminate', 'booted', 'com.sharplink.codeccompat.ios']); } catch {}
        await new Promise(resolve => server.close(resolve));
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
