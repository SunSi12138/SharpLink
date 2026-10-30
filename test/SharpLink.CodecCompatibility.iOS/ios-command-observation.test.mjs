import assert from 'node:assert/strict';
import test from 'node:test';
import { createCommandObservation } from './ios-command-observation.mjs';

// Pure injected observations: these tests never invoke mobile tools or real descriptors.
function setup(overrides = {}) {
    let now = 100, enabled = 0, disabled = 0;
    const histogram = {
        count: 3, min: 10, max: 90, mean: 30,
        percentile(percentile) { assert.equal(percentile, 99); return 80; },
        enable() { enabled++; }, disable() { disabled++; }
    };
    const stdout = { fd: 1, isTTY: false, writableLength: 0 };
    const stderr = { fd: 2, isTTY: false, writableLength: 0 };
    let cpuCalls = 0;
    const observation = createCommandObservation({
        monotonicMs: () => now,
        monitorFactory: () => histogram,
        cpuUsage: previous => {
            cpuCalls++;
            if (!previous) return { user: 100, system: 50 };
            assert.deepEqual(previous, { user: 100, system: 50 });
            return { user: 40, system: 20 };
        },
        stdout, stderr,
        fstatSync: fd => ({ isFIFO: () => fd === 1, isFile: () => fd === 2, isSocket: () => false, isCharacterDevice: () => false }),
        runtime: { node: 'test-node', uv: 'test-uv', platform: 'mock', arch: 'mock' },
        ...overrides
    });
    return { observation, histogram, stdout, stderr, setNow(value) { now = value; },
        get enabled() { return enabled; }, get disabled() { return disabled; }, get cpuCalls() { return cpuCalls; } };
}

function chunk(observation, stream, bytes, { rawStart = 1, rawEnd = 3, forwardStart = 4, forwardEnd = 7, returned = true, target = { writableLength: 0 } } = {}) {
    const token = observation.beginChunk(stream, bytes);
    observation.rawWrite(token, rawStart, rawEnd);
    observation.forwardWrite(token, forwardStart, forwardEnd, returned, target);
    observation.endChunk(token);
    return token;
}

test('bounded first and last samples preserve exact aggregate counts for every chunk', () => {
    const { observation } = setup();
    for (let index = 0; index < 600; index++) chunk(observation, index % 2 ? 'stderr' : 'stdout', index + 1);
    observation.finish();
    const record = observation.record;
    assert.equal(record.totalChunkCount, 600);
    assert.equal(record.samples.length, 256);
    assert.equal(record.droppedSampleCount, 344);
    assert.equal(record.streams.stdout.chunkCount, 300);
    assert.equal(record.streams.stderr.chunkCount, 300);
    assert.equal(record.streams.stdout.byteCount, 90000);
    assert.equal(record.streams.stderr.byteCount, 90300);
    assert.equal(record.streams.stdout.rawWriteCalls, 300);
    assert.equal(record.streams.stderr.forwardWriteCalls, 300);
    assert.equal(record.streams.stdout.rawWriteTotalMs, 600);
    assert.equal(record.streams.stderr.forwardWriteTotalMs, 900);
    const sequences = record.samples.map(sample => sample.sequence);
    assert.deepEqual(sequences, [...Array.from({ length: 128 }, (_, index) => index + 1), ...Array.from({ length: 128 }, (_, index) => index + 473)]);
});

test('non-UTF8 output is counted as raw bytes, not decoded replacement characters', () => {
    const { observation } = setup();
    const bytes = Buffer.from([0xff, 0xfe, 0x00, 0xc3, 0x28, 0x80]);
    chunk(observation, 'stdout', bytes.length);
    observation.finish();
    assert.equal(observation.record.streams.stdout.byteCount, 6);
    assert.equal(observation.record.samples.length, 1);
    assert.equal(observation.record.samples[0].bytes, 6);
    assert.equal(observation.record.samples[0].arrivalMonotonicMs, 100);
});

test('multiple raw writes and forwarding calls aggregate durations and maxima independently', () => {
    const { observation } = setup();
    const token = observation.beginChunk('stderr', 7);
    observation.rawWrite(token, 10, 12);
    observation.rawWrite(token, 12, 17);
    observation.forwardWrite(token, 20, 23, true, { writableLength: 0 });
    observation.forwardWrite(token, 24, 28, true, { writableLength: 0 });
    observation.endChunk(token);
    observation.finish();
    const stream = observation.record.streams.stderr;
    assert.equal(stream.rawWriteCalls, 2);
    assert.equal(stream.rawWriteTotalMs, 7);
    assert.equal(stream.rawWriteMaxMs, 5);
    assert.equal(stream.forwardWriteCalls, 2);
    assert.equal(stream.forwardWriteTotalMs, 7);
    assert.equal(stream.forwardWriteMaxMs, 4);
    assert.equal(observation.record.samples[0].rawWriteCalls, 2);
    assert.equal(observation.record.samples[0].rawWriteTotalMs, 7);
    assert.equal(observation.record.samples[0].rawWriteFirstStartMonotonicMs, 10);
    assert.equal(observation.record.samples[0].rawWriteLastEndMonotonicMs, 17);
});

test('write false and queue occupancy are observations only, with no pause, drain listener, or extra write', () => {
    const { observation, stdout } = setup();
    for (const method of ['pause', 'resume', 'on', 'once', 'write']) stdout[method] = () => assert.fail(`observer must not call ${method}`);
    stdout.writableLength = 128;
    chunk(observation, 'stdout', 4, { returned: false, target: stdout });
    stdout.writableLength = 32;
    chunk(observation, 'stdout', 4, { returned: true, target: stdout });
    stdout.writableLength = 16;
    observation.finish();
    assert.equal(observation.record.streams.stdout.forwardFalseCount, 1);
    assert.equal(observation.record.streams.stdout.queuePeakBytes, 128);
    assert.equal(observation.record.streams.stdout.queueFinalBytes, 16);
});

test('event-loop histogram reports measured nanoseconds and always disables the monitor', () => {
    const state = setup();
    state.observation.finish();
    const record = state.observation.record.eventLoopDelay;
    assert.equal(state.enabled, 1);
    assert.equal(state.disabled, 1);
    assert.equal(record.unit, 'nanoseconds');
    assert.equal(record.count, 3);
    assert.equal(record.min, 10);
    assert.equal(record.max, 90);
    assert.equal(record.mean, 30);
    assert.equal(record.p99, 80);
    assert.ok(record.resolutionMs > 0);
});

test('empty histogram is explicitly unknown instead of publishing sentinel minima or NaN', () => {
    const state = setup();
    Object.assign(state.histogram, { count: 0, min: 9223372036854776000, max: 0, mean: NaN });
    state.observation.finish();
    const record = state.observation.record.eventLoopDelay;
    assert.equal(record.status, 'unknown');
    assert.equal(record.count, 0);
    for (const key of ['min', 'max', 'mean', 'p99']) assert.ok(record[key] == null, `${key} must be unknown, not a histogram sentinel`);
    assert.equal(record.reason, 'no-samples');
    assert.equal(state.observation.record.observationStatus, 'partial');
    assert.equal(state.disabled, 1);
});

test('monitor construction failure is retained as unknown and does not throw', () => {
    const { observation } = setup({ monitorFactory() { throw new Error('MOCK_MONITOR_FACTORY'); } });
    assert.doesNotThrow(() => chunk(observation, 'stdout', 2));
    assert.doesNotThrow(() => observation.finish());
    assert.equal(observation.record.eventLoopDelay.status, 'unknown');
    assert.match(JSON.stringify(observation.record.errors), /MOCK_MONITOR_FACTORY/);
});

test('histogram read failure still disables the monitor and remains nonfatal', () => {
    const state = setup();
    state.histogram.percentile = () => { throw new Error('MOCK_HISTOGRAM_READ'); };
    assert.doesNotThrow(() => state.observation.finish());
    assert.equal(state.disabled, 1);
    assert.equal(state.observation.record.eventLoopDelay.status, 'unknown');
    assert.match(JSON.stringify(state.observation.record.errors), /MOCK_HISTOGRAM_READ/);
});

test('monitor disable failure remains a telemetry error rather than command failure', () => {
    const state = setup();
    state.histogram.disable = () => { throw new Error('MOCK_MONITOR_DISABLE'); };
    assert.doesNotThrow(() => state.observation.finish());
    assert.match(JSON.stringify(state.observation.record.errors), /MOCK_MONITOR_DISABLE/);
});

test('CPU deltas and descriptor types use injected metadata without copying environment or arguments', () => {
    const state = setup();
    state.observation.finish();
    assert.equal(state.cpuCalls, 2);
    assert.deepEqual(state.observation.record.cpuUsageDeltaMicros, { user: 40, system: 20 });
    assert.match(JSON.stringify(state.observation.record.runtime), /test-node/);
    assert.match(JSON.stringify(state.observation.record.runtime), /test-uv/);
    assert.deepEqual(state.observation.record.outputDescriptors.stdout, { fd: 1, isTTY: false, type: 'fifo' });
    assert.deepEqual(state.observation.record.outputDescriptors.stderr, { fd: 2, isTTY: false, type: 'file' });
});

test('CPU, descriptor, and queue getter exceptions are bounded diagnostic unknowns', () => {
    const badStream = { fd: 1, get writableLength() { throw new Error('MOCK_QUEUE'); } };
    const { observation } = setup({ stdout: badStream, stderr: badStream,
        cpuUsage() { throw new Error('MOCK_CPU'); }, fstatSync() { throw new Error('MOCK_FSTAT'); } });
    for (let index = 0; index < 100; index++) assert.doesNotThrow(() => chunk(observation, 'stdout', 1, { target: badStream }));
    assert.doesNotThrow(() => observation.finish());
    assert.ok(observation.record.errors.length <= 16);
    assert.ok(observation.record.droppedErrorCount > 0);
    assert.equal(observation.record.totalChunkCount, 100);
    assert.equal(observation.record.streams.stdout.byteCount, 100);
    assert.equal(observation.record.cpuUsageDeltaMicros, null);
});

test('callback timing and final queue measurements use the same injected monotonic clock', () => {
    const state = setup();
    const token = state.observation.beginChunk('stdout', 2);
    state.setNow(125);
    state.observation.endChunk(token);
    state.setNow(150);
    state.observation.finish();
    assert.equal(state.observation.record.startedMonotonicMs, 100);
    assert.equal(state.observation.record.endedMonotonicMs, 150);
    assert.equal(state.observation.record.samples[0].callbackDurationMs, 25);
    assert.equal(state.observation.record.streams.stdout.chunkCallbackTotalMs, 25);
    assert.equal(state.observation.record.streams.stdout.chunkCallbackMaxMs, 25);
});

test('invalid clocks and durations remain bounded partial telemetry without throwing', () => {
    const { observation } = setup({ monotonicMs() { throw new Error('MOCK_CLOCK'); } });
    assert.doesNotThrow(() => observation.beginChunk('stdout', 3));
    assert.doesNotThrow(() => observation.finish());
    assert.equal(observation.record.observationStatus, 'partial');
    assert.match(JSON.stringify(observation.record.errors), /MOCK_CLOCK/);
    const state = setup();
    const token = state.observation.beginChunk('stdout', 2);
    assert.doesNotThrow(() => state.observation.rawWrite(token, 5, 4));
    assert.doesNotThrow(() => state.observation.forwardWrite(token, NaN, 4, true, state.stdout));
    state.observation.finish();
    assert.equal(state.observation.record.streams.stdout.rawWriteCalls, 0);
    assert.equal(state.observation.record.streams.stdout.forwardWriteCalls, 0);
    assert.equal(state.observation.record.observationStatus, 'partial');
});

test('monitor enable failure still attempts disable once and finish is idempotent', () => {
    let disabled = 0;
    const { observation } = setup({ monitorFactory: () => ({ count: 0,
        enable() { throw new Error('MOCK_ENABLE'); }, disable() { disabled++; } }) });
    assert.doesNotThrow(() => observation.finish());
    assert.doesNotThrow(() => observation.finish());
    assert.equal(disabled, 1);
    assert.match(JSON.stringify(observation.record.errors), /MOCK_ENABLE/);
    assert.equal(observation.record.observationStatus, 'partial');
});

test('finish freezes telemetry against late callbacks and previously issued chunk tokens', () => {
    const state = setup();
    const oldToken = state.observation.beginChunk('stdout', 3);
    state.observation.rawWrite(oldToken, 100, 102);
    state.setNow(110);
    state.observation.finish();
    const frozen = JSON.parse(JSON.stringify(state.observation.record));
    state.setNow(200);
    const lateToken = state.observation.beginChunk('stderr', 99);
    state.observation.rawWrite(oldToken, 110, 150);
    state.observation.forwardWrite(oldToken, 151, 160, false, { writableLength: 999 });
    state.observation.endChunk(oldToken);
    state.observation.rawWrite(lateToken, 170, 180);
    state.observation.forwardWrite(lateToken, 180, 190, false, { writableLength: 999 });
    state.observation.endChunk(lateToken);
    state.observation.finish();
    assert.deepEqual(JSON.parse(JSON.stringify(state.observation.record)), frozen);
    assert.equal(state.disabled, 1);
});
