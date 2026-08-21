import fs from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const BUILTIN_RAW_CATEGORY = 'builtin-semantic-raw';

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

export async function loadEnvelopes(root, options = {}) {
    const manifestFiles = await findNamedFiles(root, 'manifest.json');
    if (manifestFiles.length === 0) {
        throw new Error(`No manifest.json files found under ${root}`);
    }

    const excludeBuiltinRaw = options.excludeBuiltinRaw
        ?? process.env.SHARPLINK_SKIP_BUILTIN_RAW === '1';
    const envelopes = [];
    for (const manifestFile of manifestFiles) {
        const originalManifest = JSON.parse(await fs.readFile(manifestFile, 'utf8'));
        const cases = (originalManifest.cases ?? []).filter(
            item => !excludeBuiltinRaw || item.category !== BUILTIN_RAW_CATEGORY);
        const manifest = { ...originalManifest, cases };
        const corpusRoot = path.dirname(manifestFile);
        const caseBytesBase64 = {};
        for (const item of cases) {
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

function firstDifference(left, right) {
    const common = Math.min(left.length, right.length);
    for (let index = 0; index < common; index++) {
        if (left[index] !== right[index]) return index;
    }
    return left.length === right.length ? null : common;
}

export async function appendRawLayoutEvidence(reportFile, producerRoot, localCorpusRoot, blocking = false) {
    const report = JSON.parse(await fs.readFile(reportFile, 'utf8'));
    const producers = await loadEnvelopes(producerRoot, { excludeBuiltinRaw: false });
    const localEnvelopes = await loadEnvelopes(localCorpusRoot, { excludeBuiltinRaw: false });
    if (localEnvelopes.length !== 1) {
        throw new Error(`Expected exactly one local corpus under ${localCorpusRoot}, found ${localEnvelopes.length}.`);
    }

    const local = localEnvelopes[0];
    const localCases = new Map((local.manifest.cases ?? []).map(item => [item.id, item]));
    for (const producer of producers) {
        for (const producerCase of (producer.manifest.cases ?? []).filter(item => item.category === BUILTIN_RAW_CATEGORY)) {
            const localCase = localCases.get(producerCase.id);
            if (!localCase) {
                throw new Error(`Local corpus is missing raw framework fixture ${producerCase.id}.`);
            }
            const producerBytes = Buffer.from(producer.caseBytesBase64[producerCase.id], 'base64');
            const localBytes = Buffer.from(local.caseBytesBase64[producerCase.id], 'base64');
            const byteEqual = producerBytes.equals(localBytes);
            const representationCompatible = producerCase.size === localCase.size && byteEqual;
            report.results.push({
                producer: producer.manifest.platformTag,
                consumer: local.manifest.platformTag,
                fixture: producerCase.id,
                category: producerCase.category,
                codecPath: producerCase.codecPath,
                producerSize: producerCase.size,
                consumerSize: localCase.size,
                producerPointerSize: producer.manifest.pointerSize,
                consumerPointerSize: local.manifest.pointerSize,
                producerFieldOffsets: producerCase.fieldOffsets ?? {},
                consumerFieldOffsets: localCase.fieldOffsets ?? {},
                producerWireHash: producerCase.wireSha256,
                consumerLocalWireHash: localCase.wireSha256,
                crossDeserializeResult: null,
                logicalEquality: null,
                segmentedCrossDeserializeResult: null,
                segmentedLogicalEquality: null,
                byteForByteEquality: byteEqual,
                firstDifferingByteOffset: firstDifference(producerBytes, localBytes),
                classification: representationCompatible
                    ? 'IDENTICAL_RAW_REPRESENTATION'
                    : 'RAW_BUILTIN_REPRESENTATION_MISMATCH',
                blocking: blocking && !representationCompatible,
                expectedLogicalValue: producerCase.expectedLogicalValue ?? '',
                actualLogicalValue: '',
                exceptionType: null,
                exceptionMessage: representationCompatible
                    ? 'Semantic cross-deserialize was not run for this framework-owned raw type; producer and consumer representations are byte-identical.'
                    : 'Semantic cross-deserialize was not run for this framework-owned raw type because representations differ; directly materializing incompatible raw bytes can create invalid runtime state.'
            });
        }
    }

    await fs.writeFile(reportFile, JSON.stringify(report, null, 2) + '\n', 'utf8');
    return report.results.length;
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
    const args = process.argv.slice(2);
    const command = args[0];
    if (command === 'unpack' && args.length === 3) {
        const envelope = JSON.parse(await fs.readFile(args[1], 'utf8'));
        await writeCorpus(envelope, args[2]);
        return;
    }
    if (command === 'pack' && args.length === 3) {
        const count = await writePackedInput(args[1], args[2]);
        console.log(`Packed ${count} producer corpus envelope(s).`);
        return;
    }
    if (command === 'append-raw' && (args.length === 4 || args.length === 5)) {
        const count = await appendRawLayoutEvidence(args[1], args[2], args[3], args[4] === 'blocking');
        console.log(`Portable report now contains ${count} result(s), including raw framework layout evidence.`);
        return;
    }
    if (command === 'check-report' && args.length === 2) {
        const count = await checkVerificationReport(args[1]);
        console.log(`Verified portable report with ${count} result(s) and no blockers.`);
        return;
    }
    throw new Error('Usage: portable-artifacts.mjs <unpack|pack|append-raw|check-report> ...');
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
    main().catch(error => {
        console.error(error.stack ?? error);
        process.exitCode = 1;
    });
}
