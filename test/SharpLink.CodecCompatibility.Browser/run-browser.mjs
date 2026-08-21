import fs from 'node:fs/promises';
import fsSync from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawn, spawnSync } from 'node:child_process';
import { loadEnvelopes, writeCorpus } from './portable-artifacts.mjs';

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const contentTypes = new Map([
    ['.html', 'text/html; charset=utf-8'],
    ['.js', 'text/javascript; charset=utf-8'],
    ['.mjs', 'text/javascript; charset=utf-8'],
    ['.json', 'application/json; charset=utf-8'],
    ['.wasm', 'application/wasm'],
    ['.dll', 'application/octet-stream'],
    ['.dat', 'application/octet-stream'],
    ['.webcil', 'application/octet-stream']
]);

function findChrome() {
    if (process.env.CHROME_BIN && fsSync.existsSync(process.env.CHROME_BIN)) {
        return process.env.CHROME_BIN;
    }
    for (const candidate of ['google-chrome', 'google-chrome-stable', 'chromium', 'chromium-browser']) {
        const result = spawnSync('which', [candidate], { encoding: 'utf8' });
        if (result.status === 0 && result.stdout.trim()) {
            return result.stdout.trim();
        }
    }
    throw new Error('No Chrome/Chromium executable was found on the runner.');
}

async function findWebRoot(root) {
    const candidates = [];
    async function visit(directory) {
        for (const entry of await fs.readdir(directory, { withFileTypes: true })) {
            const fullPath = path.join(directory, entry.name);
            if (entry.isDirectory()) {
                await visit(fullPath);
            } else if (entry.isFile() && entry.name === 'dotnet.js' && path.basename(directory) === '_framework') {
                candidates.push(path.dirname(directory));
            }
        }
    }
    await visit(root);
    if (candidates.length === 0) {
        throw new Error(`Could not find a published _framework/dotnet.js under ${root}.`);
    }
    candidates.sort((left, right) => left.length - right.length || left.localeCompare(right));
    return candidates[0];
}

async function prepareWebRoot(publishDirectory, mode, producerRoot) {
    const webRoot = await findWebRoot(publishDirectory);
    await fs.copyFile(path.join(scriptDirectory, 'index.html'), path.join(webRoot, 'index.html'));
    await fs.copyFile(path.join(scriptDirectory, 'main.js'), path.join(webRoot, 'main.js'));
    if (mode === 'verify') {
        const envelopes = await loadEnvelopes(producerRoot);
        await fs.writeFile(path.join(webRoot, 'input.json'), JSON.stringify(envelopes), 'utf8');
    }
    console.log(`Serving browser WASM app bundle from ${webRoot}.`);
    return webRoot;
}

async function serveFile(root, requestPath, response) {
    const normalized = requestPath === '/' ? '/index.html' : requestPath;
    const decoded = decodeURIComponent(normalized.split('?')[0]);
    const fullPath = path.resolve(root, `.${decoded}`);
    if (!fullPath.startsWith(path.resolve(root) + path.sep) && fullPath !== path.resolve(root, 'index.html')) {
        response.writeHead(403);
        response.end('forbidden');
        return;
    }
    try {
        const data = await fs.readFile(fullPath);
        response.writeHead(200, {
            'content-type': contentTypes.get(path.extname(fullPath)) ?? 'application/octet-stream',
            'cache-control': 'no-store',
            'cross-origin-opener-policy': 'same-origin',
            'cross-origin-embedder-policy': 'require-corp'
        });
        response.end(data);
    } catch (error) {
        if (error?.code === 'ENOENT') {
            response.writeHead(404);
            response.end('not found');
            return;
        }
        throw error;
    }
}

async function runBrowser(mode, publishDirectory, producerRoot, outputPath, commit, sdkVersion) {
    const webRoot = await prepareWebRoot(publishDirectory, mode, producerRoot);

    let resolveResult;
    let rejectResult;
    const resultPromise = new Promise((resolve, reject) => {
        resolveResult = resolve;
        rejectResult = reject;
    });

    const server = http.createServer(async (request, response) => {
        try {
            const url = new URL(request.url, 'http://127.0.0.1');
            if (request.method === 'POST' && url.pathname === '/result') {
                const chunks = [];
                for await (const chunk of request) chunks.push(chunk);
                const body = Buffer.concat(chunks).toString('utf8');
                response.writeHead(204, {
                    'cross-origin-opener-policy': 'same-origin',
                    'cross-origin-embedder-policy': 'require-corp'
                });
                response.end();
                resolveResult(body);
                return;
            }
            await serveFile(webRoot, url.pathname, response);
        } catch (error) {
            response.writeHead(500);
            response.end('server error');
            rejectResult(error);
        }
    });

    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const address = server.address();
    const url = new URL(`http://127.0.0.1:${address.port}/`);
    url.searchParams.set('mode', mode);
    url.searchParams.set('commit', commit);
    url.searchParams.set('sdk', sdkVersion);

    const chrome = spawn(findChrome(), [
        '--headless=new',
        '--no-sandbox',
        '--disable-gpu',
        '--disable-dev-shm-usage',
        '--disable-background-networking',
        '--disable-component-update',
        '--enable-logging=stderr',
        url.toString()
    ], { stdio: ['ignore', 'pipe', 'pipe'] });

    let chromeLog = '';
    chrome.stdout.on('data', chunk => chromeLog += chunk.toString());
    chrome.stderr.on('data', chunk => chromeLog += chunk.toString());
    chrome.on('error', rejectResult);
    chrome.on('exit', code => {
        if (code !== null && code !== 0) {
            rejectResult(new Error(`Chrome exited with code ${code}.\n${chromeLog}`));
        }
    });

    const timeout = setTimeout(() => {
        rejectResult(new Error(`Browser probe timed out.\n${chromeLog}`));
    }, 120_000);

    try {
        const resultText = await resultPromise;
        const parsed = JSON.parse(resultText);
        if (parsed?.browserProbeError) {
            throw new Error(parsed.browserProbeError);
        }

        if (mode === 'produce') {
            await writeCorpus(parsed, outputPath);
            console.log(`Browser producer wrote ${parsed.manifest?.cases?.length ?? 0} fixtures for ${parsed.manifest?.platformTag}.`);
        } else {
            await fs.mkdir(path.dirname(outputPath), { recursive: true });
            await fs.writeFile(outputPath, JSON.stringify(parsed, null, 2) + '\n', 'utf8');
            const blocking = (parsed.results ?? []).filter(item => item.blocking).length;
            console.log(`Browser consumer verified ${parsed.results?.length ?? 0} entries; blocking failures: ${blocking}.`);
            if (blocking !== 0) process.exitCode = 1;
        }
    } finally {
        clearTimeout(timeout);
        chrome.kill('SIGKILL');
        await new Promise(resolve => server.close(resolve));
    }
}

const [mode, publishDirectory, inputOrOutput, outputOrCommit, commitOrSdk, maybeSdk] = process.argv.slice(2);
if (!['produce', 'verify'].includes(mode)) {
    console.error('Usage: run-browser.mjs produce <publish-dir> <corpus-output> <commit> <sdk>');
    console.error('   or: run-browser.mjs verify <publish-dir> <producer-root> <report-output> <commit> <sdk>');
    process.exit(2);
}

const producerRoot = mode === 'verify' ? inputOrOutput : null;
const outputPath = mode === 'verify' ? outputOrCommit : inputOrOutput;
const commit = mode === 'verify' ? commitOrSdk : outputOrCommit;
const sdk = mode === 'verify' ? maybeSdk : commitOrSdk;

runBrowser(mode, publishDirectory, producerRoot, outputPath, commit ?? 'unknown', sdk ?? 'unknown')
    .catch(error => {
        console.error(error.stack ?? error);
        process.exitCode = 1;
    });
