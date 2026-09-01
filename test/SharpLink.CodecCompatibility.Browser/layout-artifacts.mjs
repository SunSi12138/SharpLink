import fs from 'node:fs/promises';
import path from 'node:path';

export async function findLayoutManifestFiles(root) {
    const result = [];
    async function visit(directory) {
        for (const entry of await fs.readdir(directory, { withFileTypes: true })) {
            const fullPath = path.join(directory, entry.name);
            if (entry.isDirectory()) await visit(fullPath);
            else if (entry.isFile() && entry.name === 'layout-manifest.json') result.push(fullPath);
        }
    }
    await visit(root);
    return result.sort((a, b) => a.localeCompare(b));
}

export async function loadLayoutEnvelopes(root) {
    const manifests = await findLayoutManifestFiles(root);
    if (manifests.length === 0) throw new Error(`No layout-manifest.json files found under ${root}.`);
    const envelopes = [];
    for (const manifestPath of manifests) {
        const envelope = JSON.parse(await fs.readFile(manifestPath, 'utf8'));
        const manifestRoot = path.dirname(manifestPath);
        for (const item of envelope.cases ?? []) {
            const bytes = await fs.readFile(path.join(manifestRoot, item.wireFile));
            const expected = envelope.caseBytesBase64?.[item.id];
            if (!expected || bytes.toString('base64') !== expected) {
                throw new Error(`Binary/layout-manifest mismatch for ${manifestPath}/${item.id}.`);
            }
        }
        envelopes.push(envelope);
    }
    return envelopes;
}

export async function writeLayoutCorpus(envelope, outputPath) {
    await fs.mkdir(outputPath, { recursive: true });
    for (const item of envelope.cases ?? []) {
        const encoded = envelope.caseBytesBase64?.[item.id];
        if (!encoded) throw new Error(`Missing encoded bytes for ${item.id}.`);
        const wirePath = path.join(outputPath, item.wireFile);
        await fs.mkdir(path.dirname(wirePath), { recursive: true });
        await fs.writeFile(wirePath, Buffer.from(encoded, 'base64'));
    }
    await fs.writeFile(path.join(outputPath, 'layout-manifest.json'), JSON.stringify(envelope, null, 2) + '\n', 'utf8');
}
