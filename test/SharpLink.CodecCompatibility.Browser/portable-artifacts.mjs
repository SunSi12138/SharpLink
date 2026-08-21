import { createHash } from 'node:crypto';
import fs from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const BUILTIN_RAW_CATEGORY = 'builtin-semantic-raw';
const RAW_FIXTURE_IDS = Object.freeze([
    'DateOnlyRaw',
    'DateTimeRaw',
    'DateTimeOffsetRaw',
    'TimeOnlyRaw',
    'TimeSpanRaw',
    'IndexRaw',
    'RangeRaw',
    'RuneRaw',
    'DecimalRaw'
]);
const RAW_FIXTURE_ID_SET = new Set(RAW_FIXTURE_IDS);
const CONSUMER_IDENTITY_FIELDS = Object.freeze([
    'platformTag',
    'targetFramework',
    'runtimeFamily',
    'runtimeIdentifier',
    'executionEnvironment',
    'os',
    'processArchitecture',
    'osArchitecture',
    'pointerSize',
    'isLittleEndian',
    'compilationMode'
]);

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

function validateSchemaVersion(value, source) {
    if (value?.schemaVersion !== 1) {
        throw new Error(`Unsupported or missing schemaVersion in ${source}.`);
    }
}

function validateVerificationReportSchema(report, source) {
    validateSchemaVersion(report, source);
    validateSchemaVersion(report?.consumer, `${source} consumer manifest`);
    if (!Array.isArray(report?.results)) {
        throw new Error(`Verification report ${source} is missing results.`);
    }
}

function assertExactSet(actualValues, expectedValues, label) {
    const actual = [...new Set(actualValues)].sort((left, right) => left.localeCompare(right));
    const expected = [...new Set(expectedValues)].sort((left, right) => left.localeCompare(right));
    if (actual.length !== expected.length || actual.some((value, index) => value !== expected[index])) {
        throw new Error(`${label} mismatch: expected=[${expected.join(', ')}], actual=[${actual.join(', ')}].`);
    }
}

function validateBuiltinRawCategoryBoundary(manifest, source) {
    for (const item of manifest?.cases ?? []) {
        const trustedRaw = RAW_FIXTURE_ID_SET.has(item.id);
        const declaredRaw = item.category === BUILTIN_RAW_CATEGORY;
        if (trustedRaw !== declaredRaw) {
            throw new Error(
                `${source} raw safety metadata mismatch for ${item.id}: trustedRaw=${trustedRaw}, category=${item.category ?? '<missing>'}.`);
        }
    }
}

function validateRawFixtureSet(envelope, source) {
    validateBuiltinRawCategoryBoundary(envelope.manifest, source);
    const rawCases = (envelope.manifest.cases ?? []).filter(item => RAW_FIXTURE_ID_SET.has(item.id));
    const duplicateIds = rawCases
        .map(item => item.id)
        .filter((id, index, ids) => ids.indexOf(id) !== index);
    if (duplicateIds.length !== 0) {
        throw new Error(`${source} contains duplicate raw fixture IDs: ${[...new Set(duplicateIds)].sort().join(', ')}.`);
    }

    assertExactSet(rawCases.map(item => item.id), RAW_FIXTURE_IDS, `${source} raw fixture IDs`);
    return new Map(rawCases.map(item => [item.id, item]));
}

function validateSameCommit(producerManifest, consumerManifest, source) {
    const producerCommit = String(producerManifest?.sharpLinkCommit ?? '');
    const consumerCommit = String(consumerManifest?.sharpLinkCommit ?? '');
    if (producerCommit !== consumerCommit) {
        throw new Error(
            `SharpLink commit mismatch for ${source}: producer=${producerCommit || '<missing>'}, consumer=${consumerCommit || '<missing>'}.`);
    }
}

function validateConsumerIdentity(localManifest, consumerManifest, source) {
    for (const field of CONSUMER_IDENTITY_FIELDS) {
        const localValue = localManifest?.[field];
        const consumerValue = consumerManifest?.[field];
        if (localValue !== consumerValue) {
            throw new Error(
                `${source} consumer identity mismatch for ${field}: local=${String(localValue)}, report=${String(consumerValue)}.`);
        }
    }
}

function validateResultConsumers(report, source) {
    const consumer = String(report?.consumer?.platformTag ?? '');
    if (consumer.length === 0) {
        throw new Error(`Verification report ${source} has no consumer platformTag.`);
    }

    const mismatched = report.results.filter(item => String(item.consumer ?? '') !== consumer);
    if (mismatched.length !== 0) {
        const observed = [...new Set(mismatched.map(item => String(item.consumer ?? '<missing>')))].sort();
        throw new Error(
            `Verification report ${source} contains result rows for consumers other than ${consumer}: ${observed.join(', ')}.`);
    }
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
        validateSchemaVersion(originalManifest, manifestFile);
        validateBuiltinRawCategoryBoundary(originalManifest, manifestFile);
        const cases = (originalManifest.cases ?? []).filter(
            item => !excludeBuiltinRaw || !RAW_FIXTURE_ID_SET.has(item.id));
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
    validateSchemaVersion(envelope, 'portable producer envelope');
    validateSchemaVersion(envelope.manifest, 'portable producer manifest');
    validateBuiltinRawCategoryBoundary(envelope.manifest, 'portable producer manifest');

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

function sha256(bytes) {
    return createHash('sha256').update(bytes).digest('hex');
}

function validateWireHash(platformTag, item, bytes) {
    const expected = String(item.wireSha256 ?? '').toLowerCase();
    const observed = sha256(bytes);
    if (expected.length === 0 || observed !== expected) {
        throw new Error(
            `Wire hash mismatch for ${platformTag}/${item.id}: manifest=${expected || '<missing>'}, observed=${observed}.`);
    }
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
    validateVerificationReportSchema(report, reportFile);
    validateResultConsumers(report, reportFile);
    const producers = await loadEnvelopes(producerRoot, { excludeBuiltinRaw: false });
    const localEnvelopes = await loadEnvelopes(localCorpusRoot, { excludeBuiltinRaw: false });
    if (localEnvelopes.length !== 1) {
        throw new Error(`Expected exactly one local corpus under ${localCorpusRoot}, found ${localEnvelopes.length}.`);
    }

    const local = localEnvelopes[0];
    validateSameCommit(local.manifest, report.consumer, `${local.manifest.platformTag} local raw corpus`);
    validateConsumerIdentity(local.manifest, report.consumer, `${local.manifest.platformTag} local raw corpus`);
    const localCases = validateRawFixtureSet(local, `${local.manifest.platformTag} local corpus`);
    for (const producer of producers) {
        validateSameCommit(producer.manifest, report.consumer, `${producer.manifest.platformTag} raw producer`);
        const producerCases = validateRawFixtureSet(producer, `${producer.manifest.platformTag} producer`);
        for (const fixtureId of RAW_FIXTURE_IDS) {
            const producerCase = producerCases.get(fixtureId);
            const localCase = localCases.get(fixtureId);
            const producerEncoded = producer.caseBytesBase64[fixtureId];
            const localEncoded = local.caseBytesBase64[fixtureId];
            if (typeof producerEncoded !== 'string' || typeof localEncoded !== 'string') {
                throw new Error(`Raw framework fixture ${fixtureId} is missing encoded wire bytes.`);
            }
            const producerBytes = Buffer.from(producerEncoded, 'base64');
            const localBytes = Buffer.from(localEncoded, 'base64');
            validateWireHash(producer.manifest.platformTag, producerCase, producerBytes);
            validateWireHash(local.manifest.platformTag, localCase, localBytes);

            const byteEqual = producerBytes.equals(localBytes);
            const representationCompatible = producerCase.size === localCase.size && byteEqual;
            report.results.push({
                producer: producer.manifest.platformTag,
                consumer: report.consumer.platformTag,
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

    validateResultConsumers(report, reportFile);
    await fs.writeFile(reportFile, JSON.stringify(report, null, 2) + '\n', 'utf8');
    return report.results.length;
}

export async function checkVerificationReport(reportFile) {
    const report = JSON.parse(await fs.readFile(reportFile, 'utf8'));
    validateVerificationReportSchema(report, reportFile);
    validateResultConsumers(report, reportFile);
    if (report?.browserProbeError || report?.portableProbeError) {
        throw new Error(report.browserProbeError ?? report.portableProbeError);
    }
    const blocking = report.results.filter(item => item.blocking).length;
    if (blocking !== 0) {
        throw new Error(`Portable verification contains ${blocking} blocking failure(s).`);
    }
    return report.results.length;
}

export async function checkDesktopIdentities(reportRoot, expectedCsv) {
    const expected = expectedCsv.split(',').map(value => value.trim()).filter(Boolean);
    if (expected.length === 0 || new Set(expected).size !== expected.length) {
        throw new Error('Expected desktop identity list must be non-empty and unique.');
    }

    const reportFiles = await findNamedFiles(reportRoot, 'verification.json');
    if (reportFiles.length !== expected.length) {
        throw new Error(`Expected ${expected.length} desktop verification reports, found ${reportFiles.length}.`);
    }

    const consumers = [];
    for (const reportFile of reportFiles) {
        const report = JSON.parse(await fs.readFile(reportFile, 'utf8'));
        validateVerificationReportSchema(report, reportFile);
        validateResultConsumers(report, reportFile);
        const consumer = String(report.consumer.platformTag ?? '');
        if (consumer.length === 0) {
            throw new Error(`Verification report ${reportFile} has no consumer platformTag.`);
        }
        consumers.push(consumer);

        const producers = report.results.map(item => String(item.producer ?? ''));
        assertExactSet(producers, expected, `${reportFile} producer identities`);
    }

    assertExactSet(consumers, expected, 'desktop consumer identities');
    return reportFiles.length;
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
    if (command === 'check-desktop-identities' && args.length === 3) {
        const count = await checkDesktopIdentities(args[1], args[2]);
        console.log(`Verified ${count} desktop reports with the expected producer/consumer identity set.`);
        return;
    }
    throw new Error('Usage: portable-artifacts.mjs <unpack|pack|append-raw|check-report|check-desktop-identities> ...');
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
    main().catch(error => {
        console.error(error.stack ?? error);
        process.exitCode = 1;
    });
}
