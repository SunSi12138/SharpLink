import fs from 'node:fs/promises';
import http from 'node:http';
import path from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import { loadEnvelopes, writeCorpus } from '../SharpLink.CodecCompatibility.Browser/portable-artifacts.mjs';

function adb(args, options = {}) {
    const result = spawnSync('adb', args, { encoding: 'utf8', ...options });
    if (result.status !== 0) {
        throw new Error(`adb ${args.join(' ')} failed (${result.status}):\n${result.stdout}\n${result.stderr}`);
    }
    return result.stdout;
}

async function runAndroid(mode, producerRoot, outputPath, commit, sdkVersion) {
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
        server.listen(8123, '0.0.0.0', resolve);
    });

    const endpoint = 'http://10.0.2.2:8123';
    adb(['shell', 'am', 'force-stop', 'com.sharplink.codeccompat']);
    adb([
        'shell', 'am', 'start',
        '-n', 'com.sharplink.codeccompat/.MainActivity',
        '--es', 'mode', mode,
        '--es', 'endpoint', endpoint,
        '--es', 'commit', commit,
        '--es', 'sdk', sdkVersion
    ]);

    const timeout = setTimeout(() => {
        const logcat = spawnSync('adb', ['logcat', '-d', '-t', '500'], { encoding: 'utf8' });
        rejectResult(new Error(`Android probe timed out.\n${logcat.stdout}\n${logcat.stderr}`));
    }, 120_000);

    try {
        const resultText = await resultPromise;
        const parsed = JSON.parse(resultText);
        if (parsed?.portableProbeError) {
            throw new Error(parsed.portableProbeError);
        }

        if (mode === 'produce') {
            await writeCorpus(parsed, outputPath);
            console.log(`Android producer wrote ${parsed.manifest?.cases?.length ?? 0} fixtures for ${parsed.manifest?.platformTag}.`);
        } else {
            await fs.mkdir(path.dirname(outputPath), { recursive: true });
            await fs.writeFile(outputPath, JSON.stringify(parsed, null, 2) + '\n', 'utf8');
            const blocking = (parsed.results ?? []).filter(item => item.blocking).length;
            console.log(`Android consumer verified ${parsed.results?.length ?? 0} entries; blocking failures: ${blocking}.`);
            if (blocking !== 0) process.exitCode = 1;
        }
    } finally {
        clearTimeout(timeout);
        try { adb(['shell', 'am', 'force-stop', 'com.sharplink.codeccompat']); } catch {}
        await new Promise(resolve => server.close(resolve));
    }
}

const args = process.argv.slice(2);
const mode = args[0];
if (mode === 'produce' && args.length === 4) {
    runAndroid('produce', null, args[1], args[2], args[3]).catch(error => {
        console.error(error.stack ?? error);
        process.exitCode = 1;
    });
} else if (mode === 'verify' && args.length === 5) {
    runAndroid('verify', args[1], args[2], args[3], args[4]).catch(error => {
        console.error(error.stack ?? error);
        process.exitCode = 1;
    });
} else {
    console.error('Usage: run-android.mjs produce <corpus-output> <commit> <sdk>');
    console.error('   or: run-android.mjs verify <producer-root> <report-output> <commit> <sdk>');
    process.exit(2);
}
