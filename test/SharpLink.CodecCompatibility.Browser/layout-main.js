import { dotnet } from './_framework/dotnet.js';

async function postResult(body) {
    await fetch('/result', { method: 'POST', headers: { 'content-type': 'application/json' }, body });
}

try {
    const params = new URLSearchParams(globalThis.location.search);
    const mode = params.get('mode') ?? 'produce';
    const profile = params.get('profile') ?? 'fixed-width';
    const commit = params.get('commit') ?? 'unknown';
    const sdk = params.get('sdk') ?? 'unknown';
    const { getAssemblyExports, getConfig } = await dotnet.create();
    const config = getConfig();
    const exports = await getAssemblyExports(config.mainAssemblyName);
    const probe = exports.SharpLink.CodecCompatibility.BrowserExports;
    let result;
    if (mode === 'produce') {
        result = probe.LayoutProduce(profile, commit, sdk);
    } else if (mode === 'verify') {
        const input = await fetch('/input.json').then(response => {
            if (!response.ok) throw new Error(`Failed to load layout producer input: ${response.status}`);
            return response.text();
        });
        result = probe.LayoutVerify(input, commit, sdk);
    } else {
        throw new Error(`Unknown layout browser mode: ${mode}`);
    }
    document.querySelector('#output').textContent = result;
    document.body.dataset.done = 'true';
    await postResult(result);
    await dotnet.run();
} catch (error) {
    const message = JSON.stringify({ browserProbeError: String(error?.stack ?? error) });
    document.querySelector('#output').textContent = message;
    document.body.dataset.done = 'error';
    await postResult(message);
}
