import fs from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

async function findNamedFiles(root, fileName) {
    const found = [];
    async function visit(directory) {
        for (const entry of await fs.readdir(directory, { withFileTypes: true })) {
            const fullPath = path.join(directory, entry.name);
            if (entry.isDirectory()) {
                await visit(fullPath);
            } else if (entry.isFile() && entry.name === fileName) {
                found.push(fullPath);
            }
        }
    }
    await visit(root);
    found.sort((left, right) => left.localeCompare(right));
    return found;
}

export async function loadEnvelopes(root) {
    const manifestFiles = await findNamedFiles(root, 'manifest.json');
    if (manifestFiles.length === 0) {
        throw new Error(`No manifest.json files found under ${root}`);
    }

    const envelopes = [];
    for (const manifestFile of manifestFiles) {
        const manifest = JSON.parse(await fs.readFile(manifestFile, 'utf8'));
        const corpusRoot = path.dirname(manifestFile);
        const caseBytesBase64 = {};
        for (const item of manifest.cases ?? []) {
            const wirePath = path.join(corpusRoot, ...item.wireFile.split('/'));
            caseBytesBase64[item.id] = (await fs.readFile(wirePath)).toString('base64');
        }
        envelopes.push({ schemaVersion: 1, manifest, caseBytesBase64 });
    }
    return envelopes;
}

export async function writeCorpus(envelope, outputDirectory) {
    if (!envelope?.manifest || !envelope?.caseBytesBase64) {
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
        const wirePath = path.join(outputDirectory, ...item.wireFile.split('/'));
        await fs.mkdir(path.dirname(wirePath), { recursive: true });
        await fs.writeFile(wirePath, Buffer.from(encoded, 'base64'));
    }
}

export async function writePackedInput(producerRoot, outputFile) {
    const envelopes = await loadEnvelopes(producerRoot);
    await fs.mkdir(path.dirname(outputFile), { recursive: true });
    await fs.writeFile(outputFile, JSON.stringify(envelopes), 'utf8');
    return envelopes.length;
}

export async function checkVerificationReport(reportFile) {
    const report = JSON.parse(await fs.readFile(reportFile, 'utf8'));
    if (report?.browserProbeError || report?.portableProbeError) {
        throw new Error(report.browserProbeError ?? report.portableProbeError);
    }
    const blocking = (report.results ?? []).filter(item => item.blocking).length;
    if (blocking !== 0) {
        throw new Error(`Portable verification contains ${blocking} blocking failure(s).`);
    }
    return (report.results ?? []).length;
}

async function main() {
    const [command, input, output] = process.argv.slice(2);
    if (command === 'unpack') {
        const envelope = JSON.parse(await fs.readFile(input, 'utf8'));
        await writeCorpus(envelope, output);
        return;
    }
    if (command === 'pack') {
        const count = await writePackedInput(input, output);
        console.log(`Packed ${count} producer corpus envelope(s).`);
        return;
    }
    if (command === 'check-report') {
        const count = await checkVerificationReport(input);
        console.log(`Verified portable report with ${count} result(s) and no blockers.`);
        return;
    }
    throw new Error('Usage: portable-artifacts.mjs <unpack|pack|check-report> <input> [output]');
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
    main().catch(error => {
        console.error(error.stack ?? error);
        process.exitCode = 1;
    });
}
