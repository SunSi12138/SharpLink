import { createHash } from 'node:crypto';
import fs from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const BUILTIN_RAW_CATEGORY = 'builtin-semantic-raw';
const BROWSER_PLATFORM_TAG = 'browser-wasm-browser-mono-net10';
const EXPECTED_FIXTURE_POLICY_SHA256 = '9e3c6ed421a21c15ffba4ee7027fa8aab166bf385247cd9e8d65de8a68a62cf5';
const ONE_BYTE_FIXTURE_ID_SET = new Set(['Byte', 'ByteEnum']);
const EXPECTED_PADDING_POISON_FIXTURE_IDS = Object.freeze(['ByteInt32', 'Int64Byte']);
const DESKTOP_PLATFORM_TAGS = Object.freeze([
    'linux-x64-hosted-desktop-coreclr-net10',
    'linux-arm64-hosted-desktop-coreclr-net10',
    'windows-x64-hosted-desktop-coreclr-net10',
    'windows-arm64-hosted-desktop-coreclr-net10',
    'macos-arm64-hosted-desktop-coreclr-net10',
    'macos-x64-hosted-desktop-coreclr-net10'
]);
const CONSUMER_IDENTITY_FIELDS = Object.freeze([
    'platformTag',
    'targetFramework',
    'frameworkDescription',
    'runtimeFamily',
    'runtimeFamilySource',
    'runtimeVersion',
    'sdkVersion',
    'runtimeIdentifier',
    'executionEnvironment',
    'os',
    'processArchitecture',
    'osArchitecture',
    'pointerSize',
    'isLittleEndian',
    'compilationMode'
]);
const EXACT_RUNTIME_FIELDS = Object.freeze([
    'sharpLinkCommit',
    'frameworkDescription',
    'runtimeVersion',
    'sdkVersion',
    'osVersion',
    'osArchitecture',
    'compilationMode'
]);
const KNOWN_RUNTIME_IDENTITIES = Object.freeze({
    'linux-x64-hosted-desktop-coreclr-net10': Object.freeze({
        os: 'linux', processArchitecture: 'x64', executionEnvironment: 'hosted-desktop', runtimeFamily: 'CoreCLR',
        runtimeFamilySource: 'runtime-reflection', runtimeIdentifier: 'linux-x64', targetFramework: 'net10.0', pointerSize: 8
    }),
    'linux-arm64-hosted-desktop-coreclr-net10': Object.freeze({
        os: 'linux', processArchitecture: 'arm64', executionEnvironment: 'hosted-desktop', runtimeFamily: 'CoreCLR',
        runtimeFamilySource: 'runtime-reflection', runtimeIdentifier: 'linux-arm64', targetFramework: 'net10.0', pointerSize: 8
    }),
    'windows-x64-hosted-desktop-coreclr-net10': Object.freeze({
        os: 'windows', processArchitecture: 'x64', executionEnvironment: 'hosted-desktop', runtimeFamily: 'CoreCLR',
        runtimeFamilySource: 'runtime-reflection', runtimeIdentifier: 'win-x64', targetFramework: 'net10.0', pointerSize: 8
    }),
    'windows-arm64-hosted-desktop-coreclr-net10': Object.freeze({
        os: 'windows', processArchitecture: 'arm64', executionEnvironment: 'hosted-desktop', runtimeFamily: 'CoreCLR',
        runtimeFamilySource: 'runtime-reflection', runtimeIdentifier: 'win-arm64', targetFramework: 'net10.0', pointerSize: 8
    }),
    'macos-x64-hosted-desktop-coreclr-net10': Object.freeze({
        os: 'macos', processArchitecture: 'x64', executionEnvironment: 'hosted-desktop', runtimeFamily: 'CoreCLR',
        runtimeFamilySource: 'runtime-reflection', runtimeIdentifier: 'osx-x64', targetFramework: 'net10.0', pointerSize: 8
    }),
    'macos-arm64-hosted-desktop-coreclr-net10': Object.freeze({
        os: 'macos', processArchitecture: 'arm64', executionEnvironment: 'hosted-desktop', runtimeFamily: 'CoreCLR',
        runtimeFamilySource: 'runtime-reflection', runtimeIdentifier: 'osx-arm64', targetFramework: 'net10.0', pointerSize: 8
    }),
    'browser-wasm-browser-mono-net10': Object.freeze({
        os: 'browser', processArchitecture: 'wasm', executionEnvironment: 'browser', runtimeFamily: 'Mono',
        runtimeFamilySource: 'platform-runtime-pack', runtimeIdentifier: 'browser-wasm', targetFramework: 'net10.0/browser-wasm', pointerSize: 4
    }),
    'android-x64-emulator-mono-net10': Object.freeze({
        os: 'android', processArchitecture: 'x64', executionEnvironment: 'emulator', runtimeFamily: 'Mono',
        runtimeFamilySource: 'loaded-runtime-library', runtimeIdentifier: 'android-x64', targetFramework: 'net10.0-android/android-x64', pointerSize: 8
    }),
    'android-x64-emulator-coreclr-net10': Object.freeze({
        os: 'android', processArchitecture: 'x64', executionEnvironment: 'emulator', runtimeFamily: 'CoreCLR',
        runtimeFamilySource: 'loaded-runtime-library', runtimeIdentifier: 'android-x64', targetFramework: 'net10.0-android/android-x64', pointerSize: 8
    }),
    'ios-x64-simulator-mono-net10': Object.freeze({
        os: 'ios', processArchitecture: 'x64', executionEnvironment: 'simulator', runtimeFamily: 'Mono',
        runtimeFamilySource: 'platform-runtime-pack', runtimeIdentifier: 'iossimulator-x64', targetFramework: 'net10.0-ios/iossimulator-x64', pointerSize: 8
    }),
    'ios-arm64-simulator-mono-net10': Object.freeze({
        os: 'ios', processArchitecture: 'arm64', executionEnvironment: 'simulator', runtimeFamily: 'Mono',
        runtimeFamilySource: 'platform-runtime-pack', runtimeIdentifier: 'iossimulator-arm64', targetFramework: 'net10.0-ios/iossimulator-arm64', pointerSize: 8
    }),
    'android-arm64-physical-device-mono-net10': Object.freeze({
        os: 'android', processArchitecture: 'arm64', executionEnvironment: 'physical-device', runtimeFamily: 'Mono',
        runtimeFamilySource: 'loaded-runtime-library', runtimeIdentifier: 'android-arm64', targetFramework: 'net10.0-android/android-arm64', pointerSize: 8
    }),
    'android-arm64-physical-device-coreclr-net10': Object.freeze({
        os: 'android', processArchitecture: 'arm64', executionEnvironment: 'physical-device', runtimeFamily: 'CoreCLR',
        runtimeFamilySource: 'loaded-runtime-library', runtimeIdentifier: 'android-arm64', targetFramework: 'net10.0-android/android-arm64', pointerSize: 8
    })
});

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

function validateFixtureRegistry(manifest, source) {
    const entries = manifest?.fixtureRegistry;
    if (!Array.isArray(entries) || entries.length === 0) {
        throw new Error(`${source} is missing shared fixture registry metadata.`);
    }
    const ids = entries.map(item => String(item?.id ?? ''));
    if (ids.some(id => id.length === 0)) {
        throw new Error(`${source} fixture registry contains a missing fixture id.`);
    }
    const duplicates = [...new Set(ids.filter((id, index) => ids.indexOf(id) !== index))].sort();
    if (duplicates.length !== 0) {
        throw new Error(`${source} fixture registry contains duplicate ids: ${duplicates.join(', ')}.`);
    }
    for (const item of entries) {
        if (typeof item.category !== 'string' || typeof item.nativeWidth !== 'boolean') {
            throw new Error(`${source} has invalid fixture registry metadata for ${String(item.id)}.`);
        }
    }

    const sorted = [...entries]
        .map(item => ({ id: String(item.id), category: item.category, nativeWidth: item.nativeWidth }))
        .sort((left, right) => left.id.localeCompare(right.id));
    const policyCanonical = sorted
        .map(item => `${item.id}\t${item.category}\t${item.nativeWidth ? 1 : 0}\t${ONE_BYTE_FIXTURE_ID_SET.has(item.id) ? 0 : 1}\n`)
        .join('');
    const policyHash = sha256(Buffer.from(policyCanonical, 'utf8'));
    if (policyHash !== EXPECTED_FIXTURE_POLICY_SHA256) {
        throw new Error(
            `${source} fixture registry does not match compatibility baseline policy: ` +
            `expected=${EXPECTED_FIXTURE_POLICY_SHA256}, actual=${policyHash}.`);
    }

    const byId = new Map(sorted.map(item => [item.id, item]));
    return {
        entries: sorted,
        byId,
        fixtureIds: sorted.map(item => item.id),
        rawFixtureIds: sorted.filter(item => item.category === BUILTIN_RAW_CATEGORY).map(item => item.id),
        rawFixtureIdSet: new Set(sorted.filter(item => item.category === BUILTIN_RAW_CATEGORY).map(item => item.id)),
        nativeWidthFixtureIdSet: new Set(sorted.filter(item => item.nativeWidth).map(item => item.id)),
        requiresSegmentedFixtureIdSet: new Set(sorted.filter(item => !ONE_BYTE_FIXTURE_ID_SET.has(item.id)).map(item => item.id)),
        key: JSON.stringify(sorted)
    };
}

function validateExactRuntimeIdentity(manifest, source) {
    for (const field of EXACT_RUNTIME_FIELDS) {
        const value = String(manifest?.[field] ?? '');
        if (value.length === 0 || value.toLowerCase() === 'unknown') {
            throw new Error(`${source} requires known exact-runtime identity field ${field}; actual=${value || '<missing>'}.`);
        }
    }
}

function validateRuntimeManifestIdentity(manifest, source) {
    validateSchemaVersion(manifest, source);
    const registry = validateFixtureRegistry(manifest, source);
    const platformTag = String(manifest?.platformTag ?? '');
    const derivedTag = `${String(manifest?.os ?? '')}-${String(manifest?.processArchitecture ?? '')}-${String(manifest?.executionEnvironment ?? '')}-${String(manifest?.runtimeFamily ?? '').toLowerCase()}-net10`;
    if (platformTag !== derivedTag) {
        throw new Error(`${source} platformTag mismatch: recorded=${platformTag || '<missing>'}, derived=${derivedTag}.`);
    }

    const expected = KNOWN_RUNTIME_IDENTITIES[platformTag];
    if (expected) {
        for (const [field, expectedValue] of Object.entries(expected)) {
            if (manifest?.[field] !== expectedValue) {
                throw new Error(
                    `${source} runtime identity mismatch for ${platformTag}/${field}: ` +
                    `expected=${String(expectedValue)}, actual=${String(manifest?.[field])}.`);
            }
        }
        validateExactRuntimeIdentity(manifest, source);
    }
    return registry;
}

function validatePaddingPoisonEvidence(manifest, source) {
    const items = manifest?.paddingPoison;
    if (!Array.isArray(items)) {
        throw new Error(`${source} is missing padding-poison evidence.`);
    }
    const fixtureIds = items.map(item => String(item?.fixture ?? ''));
    assertExactSet(fixtureIds, EXPECTED_PADDING_POISON_FIXTURE_IDS, `${source} padding-poison fixture IDs`);
    if (fixtureIds.length !== EXPECTED_PADDING_POISON_FIXTURE_IDS.length) {
        throw new Error(`${source} padding-poison evidence contains duplicate or extra rows.`);
    }

    for (const item of items) {
        const fixture = String(item.fixture ?? '');
        const size = Number(item.size);
        const differing = item.differingByteOffsets;
        const padding = item.paddingByteOffsets;
        if (!Number.isInteger(size) || size <= 0 || item.logicalValuesEqual !== true
            || !Array.isArray(differing) || !Array.isArray(padding) || padding.length === 0) {
            throw new Error(`${source} has invalid padding-poison metadata for ${fixture}.`);
        }
        const uniqueDiffering = new Set(differing);
        const uniquePadding = new Set(padding);
        if (uniqueDiffering.size !== differing.length || uniquePadding.size !== padding.length
            || differing.some(offset => !Number.isInteger(offset) || offset < 0 || offset >= size)
            || padding.some(offset => !Number.isInteger(offset) || offset < 0 || offset >= size)) {
            throw new Error(`${source} has invalid padding-poison offsets for ${fixture}.`);
        }

        const expectedWireEqual = differing.length === 0;
        const expectedOnlyPadding = differing.every(offset => uniquePadding.has(offset));
        if (item.wireBytesEqual !== expectedWireEqual || item.differencesOnlyInPadding !== expectedOnlyPadding) {
            throw new Error(`${source} has inconsistent padding-poison result flags for ${fixture}.`);
        }
        for (const field of ['sourceAHash', 'sourceBHash', 'wireAHash', 'wireBHash']) {
            if (!/^[0-9a-fA-F]{64}$/.test(String(item?.[field] ?? ''))) {
                throw new Error(`${source} has invalid padding-poison hash ${field} for ${fixture}.`);
            }
        }

        const manifestCase = (manifest?.cases ?? []).find(candidate => candidate?.id === fixture);
        if (manifestCase && Number(manifestCase.size) !== size) {
            throw new Error(
                `${source} padding-poison size mismatch for ${fixture}: evidence=${size}, case=${String(manifestCase.size)}.`);
        }
    }
}

function assertSameFixtureRegistry(leftManifest, rightManifest, label) {
    const left = validateFixtureRegistry(leftManifest, `${label} left registry`);
    const right = validateFixtureRegistry(rightManifest, `${label} right registry`);
    if (left.key !== right.key) {
        throw new Error(`${label} fixture registry mismatch.`);
    }
    return left;
}

function validateVerificationReportSchema(report, source) {
    validateSchemaVersion(report, source);
    validateRuntimeManifestIdentity(report?.consumer, `${source} consumer manifest`);
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

function resultKey(producer, fixture) {
    return `${producer}\u001f${fixture}`;
}

function assertExactResultKeySet(report, expectedProducers, expectedFixtures, label) {
    const expectedKeys = new Set(
        expectedProducers.flatMap(producer => expectedFixtures.map(fixture => resultKey(producer, fixture))));
    const actualKeys = report.results.map(item => resultKey(String(item.producer ?? ''), String(item.fixture ?? '')));
    const duplicates = [...new Set(actualKeys.filter((key, index) => actualKeys.indexOf(key) !== index))].sort();
    if (duplicates.length !== 0) {
        throw new Error(`${label} contains duplicate producer/fixture keys: ${duplicates.join(', ')}.`);
    }

    const actualSet = new Set(actualKeys);
    const missing = [...expectedKeys].filter(key => !actualSet.has(key)).sort();
    const unexpected = [...actualSet].filter(key => !expectedKeys.has(key)).sort();
    if (missing.length !== 0 || unexpected.length !== 0) {
        throw new Error(
            `${label} mismatch: missing=[${missing.join(', ')}], unexpected=[${unexpected.join(', ')}].`);
    }
}

function validateBuiltinRawCategoryBoundary(manifest, source, registry = validateFixtureRegistry(manifest, source)) {
    for (const item of manifest?.cases ?? []) {
        const metadata = registry.byId.get(String(item.id));
        if (!metadata) {
            throw new Error(`${source} contains fixture ${String(item.id)} that is absent from the shared fixture registry.`);
        }
        if (item.category !== metadata.category) {
            throw new Error(
                `${source} fixture category mismatch for ${item.id}: expected=${metadata.category}, actual=${item.category ?? '<missing>'}.`);
        }
    }
}

function validateRawFixtureSet(envelope, source, expectedManifest = envelope.manifest) {
    const registry = assertSameFixtureRegistry(envelope.manifest, expectedManifest, `${source} shared registry`);
    validateBuiltinRawCategoryBoundary(envelope.manifest, source, registry);
    const rawCases = (envelope.manifest.cases ?? []).filter(item => registry.rawFixtureIdSet.has(item.id));
    const duplicateIds = rawCases
        .map(item => item.id)
        .filter((id, index, ids) => ids.indexOf(id) !== index);
    if (duplicateIds.length !== 0) {
        throw new Error(`${source} contains duplicate raw fixture IDs: ${[...new Set(duplicateIds)].sort().join(', ')}.`);
    }

    assertExactSet(rawCases.map(item => item.id), registry.rawFixtureIds, `${source} raw fixture IDs`);
    return { cases: new Map(rawCases.map(item => [item.id, item])), registry };
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
    assertSameFixtureRegistry(localManifest, consumerManifest, `${source} registry`);
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

function validateExpectedNativeWidthDifference(item, fixture, source) {
    const producerPointerSize = Number(item.producerPointerSize);
    const consumerPointerSize = Number(item.consumerPointerSize);
    const producerSize = Number(item.producerSize);
    const consumerSize = Number(item.consumerSize);
    const nativeSlots = fixture === 'NativePair' ? 2 : 1;
    if (!Number.isInteger(producerPointerSize)
        || producerPointerSize <= 0
        || !Number.isInteger(consumerPointerSize)
        || consumerPointerSize <= 0
        || producerPointerSize === consumerPointerSize
        || producerSize !== producerPointerSize * nativeSlots
        || consumerSize !== consumerPointerSize * nativeSlots) {
        throw new Error(
            `${source} has invalid EXPECTED_ARCH_DEPENDENT evidence: producer=${String(item.producer)}, fixture=${fixture}, ` +
            `producerPointerSize=${String(item.producerPointerSize)}, consumerPointerSize=${String(item.consumerPointerSize)}, ` +
            `producerSize=${String(item.producerSize)}, consumerSize=${String(item.consumerSize)}.`);
    }
}

function validateByteClassification(item, source, raw) {
    const byteEqual = item.byteForByteEquality === true;
    const expectedClassification = raw
        ? (byteEqual ? 'IDENTICAL_RAW_REPRESENTATION' : 'RAW_BUILTIN_REPRESENTATION_MISMATCH')
        : (byteEqual ? 'IDENTICAL_BYTES_AND_COMPATIBLE' : 'DIFFERENT_BYTES_BUT_CROSS_COMPATIBLE');
    if (item.classification !== expectedClassification) {
        throw new Error(
            `${source} classification/byte invariant mismatch: producer=${String(item.producer)}, fixture=${String(item.fixture)}, ` +
            `classification=${String(item.classification)}, expected=${expectedClassification}, byteEqual=${String(item.byteForByteEquality)}.`);
    }
    if (byteEqual ? item.firstDifferingByteOffset != null : item.firstDifferingByteOffset == null) {
        throw new Error(
            `${source} first-difference invariant mismatch: producer=${String(item.producer)}, fixture=${String(item.fixture)}, ` +
            `byteEqual=${String(item.byteForByteEquality)}, firstDiff=${String(item.firstDifferingByteOffset)}.`);
    }
}

function validateStrictResultSemantics(
    report,
    source,
    allowPortableRawRepresentation = true,
    allowExpectedNativeWidthDifference = false) {
    const registry = validateFixtureRegistry(report.consumer, `${source} fixture registry`);
    for (const item of report.results) {
        const fixture = String(item.fixture ?? '');
        const metadata = registry.byId.get(fixture);
        if (!metadata || item.category !== metadata.category) {
            throw new Error(
                `${source} result fixture metadata mismatch: fixture=${fixture}, category=${String(item.category)}, ` +
                `expectedCategory=${String(metadata?.category)}.`);
        }

        const portableRawRepresentation = allowPortableRawRepresentation
            && registry.rawFixtureIdSet.has(fixture)
            && item.category === BUILTIN_RAW_CATEGORY
            && item.crossDeserializeResult == null
            && item.logicalEquality == null
            && item.segmentedCrossDeserializeResult == null
            && item.segmentedLogicalEquality == null;
        if (portableRawRepresentation) {
            if (item.classification !== 'IDENTICAL_RAW_REPRESENTATION'
                && item.classification !== 'RAW_BUILTIN_REPRESENTATION_MISMATCH') {
                throw new Error(
                    `${source} has raw representation-only row with unexpected classification ${String(item.classification)}: ` +
                    `producer=${String(item.producer)}, fixture=${fixture}.`);
            }
            validateByteClassification(item, source, true);
            continue;
        }

        const expectedNativeWidthDifference = allowExpectedNativeWidthDifference
            && registry.nativeWidthFixtureIdSet.has(fixture)
            && item.classification === 'EXPECTED_ARCH_DEPENDENT'
            && item.crossDeserializeResult == null
            && item.logicalEquality == null
            && item.segmentedCrossDeserializeResult == null
            && item.segmentedLogicalEquality == null;
        if (expectedNativeWidthDifference) {
            validateExpectedNativeWidthDifference(item, fixture, source);
            continue;
        }

        if (item.classification === 'EXPECTED_ARCH_DEPENDENT'
            || item.crossDeserializeResult !== true
            || item.logicalEquality !== true) {
            throw new Error(
                `${source} requires semantic cross-deserialization success: producer=${String(item.producer)}, fixture=${fixture}, ` +
                `classification=${String(item.classification)}, cross=${String(item.crossDeserializeResult)}, logical=${String(item.logicalEquality)}.`);
        }

        if (registry.requiresSegmentedFixtureIdSet.has(fixture)
            && (item.segmentedCrossDeserializeResult !== true || item.segmentedLogicalEquality !== true)) {
            throw new Error(
                `${source} requires segmented semantic success for policy multi-byte fixture: producer=${String(item.producer)}, fixture=${fixture}, ` +
                `segmentedCross=${String(item.segmentedCrossDeserializeResult)}, segmentedLogical=${String(item.segmentedLogicalEquality)}.`);
        }
        validateByteClassification(item, source, false);
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
    let expectedRegistryKey = null;
    for (const manifestFile of manifestFiles) {
        const originalManifest = JSON.parse(await fs.readFile(manifestFile, 'utf8'));
        const registry = validateRuntimeManifestIdentity(originalManifest, manifestFile);
        validatePaddingPoisonEvidence(originalManifest, manifestFile);
        if (expectedRegistryKey !== null && registry.key !== expectedRegistryKey) {
            throw new Error(`Producer fixture registry mismatch in ${manifestFile}.`);
        }
        expectedRegistryKey ??= registry.key;
        validateBuiltinRawCategoryBoundary(originalManifest, manifestFile, registry);
        const cases = (originalManifest.cases ?? []).filter(
            item => !excludeBuiltinRaw || !registry.rawFixtureIdSet.has(item.id));
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
    const registry = validateRuntimeManifestIdentity(envelope.manifest, 'portable producer manifest');
    validatePaddingPoisonEvidence(envelope.manifest, 'portable producer manifest');
    validateBuiltinRawCategoryBoundary(envelope.manifest, 'portable producer manifest', registry);

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

function validateWireSize(platformTag, item, bytes) {
    const expected = Number(item.size);
    if (!Number.isInteger(expected) || expected < 0 || bytes.length !== expected) {
        throw new Error(
            `Wire size mismatch for ${platformTag}/${item.id}: manifest=${String(item.size)}, observed=${bytes.length}.`);
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
    const reportRegistry = validateFixtureRegistry(report.consumer, `${reportFile} fixture registry`);
    const producers = await loadEnvelopes(producerRoot, { excludeBuiltinRaw: false });
    const localEnvelopes = await loadEnvelopes(localCorpusRoot, { excludeBuiltinRaw: false });
    if (localEnvelopes.length !== 1) {
        throw new Error(`Expected exactly one local corpus under ${localCorpusRoot}, found ${localEnvelopes.length}.`);
    }

    const local = localEnvelopes[0];
    validateSameCommit(local.manifest, report.consumer, `${local.manifest.platformTag} local raw corpus`);
    validateConsumerIdentity(local.manifest, report.consumer, `${local.manifest.platformTag} local raw corpus`);
    const localRaw = validateRawFixtureSet(local, `${local.manifest.platformTag} local corpus`, report.consumer);
    for (const producer of producers) {
        validateSameCommit(producer.manifest, report.consumer, `${producer.manifest.platformTag} raw producer`);
        const producerRaw = validateRawFixtureSet(producer, `${producer.manifest.platformTag} producer`, report.consumer);
        for (const fixtureId of reportRegistry.rawFixtureIds) {
            const producerCase = producerRaw.cases.get(fixtureId);
            const localCase = localRaw.cases.get(fixtureId);
            const producerEncoded = producer.caseBytesBase64[fixtureId];
            const localEncoded = local.caseBytesBase64[fixtureId];
            if (typeof producerEncoded !== 'string' || typeof localEncoded !== 'string') {
                throw new Error(`Raw framework fixture ${fixtureId} is missing encoded wire bytes.`);
            }
            const producerBytes = Buffer.from(producerEncoded, 'base64');
            const localBytes = Buffer.from(localEncoded, 'base64');
            validateWireHash(producer.manifest.platformTag, producerCase, producerBytes);
            validateWireHash(local.manifest.platformTag, localCase, localBytes);
            validateWireSize(producer.manifest.platformTag, producerCase, producerBytes);
            validateWireSize(local.manifest.platformTag, localCase, localBytes);

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
    validateStrictResultSemantics(report, reportFile, true, true);
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

export async function checkBrowserEvidence(forwardReportFile, reverseReportRoot) {
    const forward = JSON.parse(await fs.readFile(forwardReportFile, 'utf8'));
    validateVerificationReportSchema(forward, forwardReportFile);
    validateResultConsumers(forward, forwardReportFile);
    if (String(forward.consumer.platformTag ?? '') !== BROWSER_PLATFORM_TAG) {
        throw new Error(
            `Browser forward consumer identity mismatch: expected=${BROWSER_PLATFORM_TAG}, actual=${String(forward.consumer.platformTag ?? '<missing>')}.`);
    }
    const registry = validateFixtureRegistry(forward.consumer, 'Browser forward fixture registry');
    const expectedForwardProducers = [...DESKTOP_PLATFORM_TAGS, BROWSER_PLATFORM_TAG];
    assertExactSet(
        forward.results.map(item => String(item.producer ?? '')),
        expectedForwardProducers,
        'Browser forward producer identities');
    assertExactResultKeySet(
        forward,
        expectedForwardProducers,
        registry.fixtureIds,
        'Browser forward result keys');
    validateStrictResultSemantics(forward, forwardReportFile, true, true);

    const expectedCommit = String(forward.consumer.sharpLinkCommit ?? '');
    if (expectedCommit.length === 0 || expectedCommit.toLowerCase() === 'unknown') {
        throw new Error('Browser forward report must contain a known SharpLink commit.');
    }

    const reverseReportFiles = await findNamedFiles(reverseReportRoot, 'verification.json');
    if (reverseReportFiles.length !== DESKTOP_PLATFORM_TAGS.length) {
        throw new Error(
            `Expected ${DESKTOP_PLATFORM_TAGS.length} Browser-to-desktop verification reports, found ${reverseReportFiles.length}.`);
    }

    const reverseConsumers = [];
    let reverseRows = 0;
    for (const reportFile of reverseReportFiles) {
        const report = JSON.parse(await fs.readFile(reportFile, 'utf8'));
        validateVerificationReportSchema(report, reportFile);
        validateResultConsumers(report, reportFile);
        assertSameFixtureRegistry(report.consumer, forward.consumer, `${reportFile} Browser evidence registry`);
        const consumer = String(report.consumer.platformTag ?? '');
        reverseConsumers.push(consumer);
        if (String(report.consumer.sharpLinkCommit ?? '') !== expectedCommit) {
            throw new Error(
                `Browser evidence commit mismatch in ${reportFile}: expected=${expectedCommit}, actual=${String(report.consumer.sharpLinkCommit ?? '<missing>')}.`);
        }
        assertExactSet(
            report.results.map(item => String(item.producer ?? '')),
            [BROWSER_PLATFORM_TAG],
            `${reportFile} Browser producer identity`);
        assertExactResultKeySet(
            report,
            [BROWSER_PLATFORM_TAG],
            registry.fixtureIds,
            `${reportFile} Browser-to-desktop result keys`);
        validateStrictResultSemantics(report, reportFile, true, true);
        reverseRows += report.results.length;
    }

    assertExactSet(reverseConsumers, DESKTOP_PLATFORM_TAGS, 'Browser reverse desktop consumer identities');
    return {
        forwardRows: forward.results.length,
        reverseReports: reverseReportFiles.length,
        reverseRows
    };
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
        console.log(`Verified portable report with ${count} result(s), required semantics, and no blockers.`);
        return;
    }
    if (command === 'check-desktop-identities' && args.length === 3) {
        const count = await checkDesktopIdentities(args[1], args[2]);
        console.log(`Verified ${count} desktop reports with the expected producer/consumer identity set.`);
        return;
    }
    if (command === 'check-browser-evidence' && args.length === 3) {
        const result = await checkBrowserEvidence(args[1], args[2]);
        console.log(
            `Verified bidirectional Browser evidence: ${result.forwardRows} Browser-consumer rows and ` +
            `${result.reverseRows} Browser-to-desktop rows across ${result.reverseReports} desktop consumers.`);
        return;
    }
    throw new Error(
        'Usage: portable-artifacts.mjs <unpack|pack|append-raw|check-report|check-desktop-identities|check-browser-evidence> ...');
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
    main().catch(error => {
        console.error(error.stack ?? error);
        process.exitCode = 1;
    });
}
