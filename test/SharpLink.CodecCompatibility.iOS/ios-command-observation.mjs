// Opt-in observations only: no output content, extra I/O per chunk, drain waits, or verdict changes.
import fs from 'node:fs';
import { monitorEventLoopDelay } from 'node:perf_hooks';

export function createCommandObservation({ monotonicMs, monitorFactory = monitorEventLoopDelay,
    cpuUsage = (...args) => process.cpuUsage(...args), stdout = process.stdout, stderr = process.stderr,
    fstatSync = fd => fs.fstatSync(fd), runtime = { node: process.version, libuv: process.versions.uv, execPath: process.execPath } } = {}) {
    const record = {
        schemaVersion: 1, observationStatus: 'collecting',
        timingMeaning: 'Callback observation time, not kernel output production time. Measurements perturb execution.',
        scope: 'ELD/CPU: command try entry through final-cleanup entry, including metadata/console work inside that interval. Per-write statistics: raw writes and forwarded child chunks only. APIs ignore later cleanup output after finish.',
        callTimingMeaning: 'Monotonic elapsed around calls includes descheduling/preemption; not pure kernel I/O time or proof of slow disk.',
        runtime, outputDescriptors: {}, monotonicClock: 'ios-command timelineOriginAt / process.hrtime.bigint relative',
        sampleRetention: 'First 128 and most recent 128 chunks, without duplicate sequences; totals include discarded samples.',
        outputQueueMeaning: 'Supervisor writableLength may include supervisor messages. Peak is sampled after forwarding and at finish, not a continuous kernel queue maximum.',
        sampleLimit: 256, samples: [], totalChunkCount: 0, droppedSampleCount: 0,
        errors: [], droppedErrorCount: 0, streams: {},
        eventLoopDelay: { status: 'unknown', resolutionMs: 20, unit: 'nanoseconds', count: 0, caveat: 'Histogram callbacks can miss a final stall before disable; no samples is unknown, never zero delay.' }, cpuUsageDeltaMicros: null, cpuScope: 'Supervisor process only; excludes child CPU and cannot prove host contention.'
    };
    const first = [], tail = [];
    let tailCursor = 0, histogram, initialCpu, finished = false;
    const error = (stage, cause) => {
        if (record.errors.length < 16) record.errors.push({ stage, code: cause?.code == null ? null : String(cause.code).slice(0, 60), message: String(cause?.message ?? cause).slice(0, 300) });
        else record.droppedErrorCount++;
    };
    // Every observer API is fail-contained. Incomplete measurements cannot change subprocess cleanup/verdicts.
    const safely = (stage, fn) => { try { return fn(); } catch (cause) { error(stage, cause); return undefined; } };
    const clock = () => {
        const value = monotonicMs();
        if (!Number.isFinite(value) || value < 0) throw new Error('Invalid observation monotonic clock.');
        return value;
    };
    const queueLength = stream => {
        const value = stream.writableLength;
        if (!Number.isSafeInteger(value) || value < 0) throw new Error('Output writableLength unavailable.');
        return value;
    };
    for (const [name, stream] of Object.entries({ stdout, stderr })) {
        record.streams[name] = { chunkCount: 0, byteCount: 0, firstArrivalMonotonicMs: null, lastArrivalMonotonicMs: null, maxArrivalGapMs: 0, rawWriteCalls: 0, rawWriteTotalMs: 0, rawWriteMaxMs: 0,
            chunkCallbackTotalMs: 0, chunkCallbackMaxMs: 0, forwardWriteCalls: 0, forwardWriteTotalMs: 0,
            forwardWriteMaxMs: 0, forwardFalseCount: 0, queuePeakBytes: 0, queueFinalBytes: null };
        safely(`descriptor-${name}`, () => {
            const stat = fstatSync(stream.fd);
            const types = [['file', 'isFile'], ['fifo', 'isFIFO'], ['socket', 'isSocket'], ['character-device', 'isCharacterDevice']];
            record.outputDescriptors[name] = { fd: stream.fd, isTTY: Boolean(stream.isTTY),
                type: types.find(([, method]) => stat[method]())?.[0] ?? 'other' };
        });
    }
    safely('start-clock', () => { record.startedMonotonicMs = clock(); });
    safely('cpu-start', () => { initialCpu = cpuUsage(); });
    safely('event-loop-enable', () => { histogram = monitorFactory({ resolution: 20 }); histogram.enable(); });
    const beginChunk = (stream, bytes) => finished ? undefined : safely('chunk-begin', () => {
        const stats = record.streams[stream];
        if (!stats || !Number.isSafeInteger(bytes) || bytes < 0) throw new Error('Invalid chunk observation.');
        stats.chunkCount++; stats.byteCount += bytes; record.totalChunkCount++;
        const arrivalMonotonicMs = clock();
        stats.firstArrivalMonotonicMs ??= arrivalMonotonicMs;
        if (stats.lastArrivalMonotonicMs !== null) stats.maxArrivalGapMs = Math.max(stats.maxArrivalGapMs, arrivalMonotonicMs - stats.lastArrivalMonotonicMs);
        stats.lastArrivalMonotonicMs = arrivalMonotonicMs;
        const sample = { sequence: record.totalChunkCount, stream, bytes, arrivalMonotonicMs,
            rawWriteCalls: 0, rawWriteTotalMs: 0, rawWriteMaxMs: 0 };
        if (first.length < 128) first.push(sample);
        else { tail[tailCursor] = sample; tailCursor = (tailCursor + 1) % 128; }
        record.droppedSampleCount = Math.max(0, record.totalChunkCount - first.length - tail.length);
        return sample;
    });
    const duration = (start, end) => {
        if (!Number.isFinite(start) || !Number.isFinite(end) || end < start) throw new Error('Invalid observation duration.');
        return end - start;
    };
    const rawWrite = (sample, start, end) => finished ? undefined : safely('raw-write', () => {
        if (!sample) return;
        const ms = duration(start, end), stats = record.streams[sample.stream];
        stats.rawWriteCalls++; stats.rawWriteTotalMs += ms; stats.rawWriteMaxMs = Math.max(stats.rawWriteMaxMs, ms);
        sample.rawWriteCalls++; sample.rawWriteTotalMs += ms; sample.rawWriteMaxMs = Math.max(sample.rawWriteMaxMs, ms);
        sample.rawWriteFirstStartMonotonicMs ??= start; sample.rawWriteLastEndMonotonicMs = end;
    });
    const forwardWrite = (sample, start, end, returned, stream) => finished ? undefined : safely('forward-write', () => {
        if (!sample) return;
        const ms = duration(start, end), stats = record.streams[sample.stream];
        stats.forwardWriteCalls++; stats.forwardWriteTotalMs += ms; stats.forwardWriteMaxMs = Math.max(stats.forwardWriteMaxMs, ms);
        if (returned === false) stats.forwardFalseCount++;
        const queue = queueLength(stream);
        stats.queuePeakBytes = Math.max(stats.queuePeakBytes, queue);
        Object.assign(sample, { forwardStartMonotonicMs: start, forwardEndMonotonicMs: end,
            forwardDurationMs: ms, forwardReturned: typeof returned === 'boolean' ? returned : null, queuedBytesAfter: queue });
    });
    const endChunk = sample => finished ? undefined : safely('chunk-end', () => {
        if (!sample) return;
        const now = clock(), ms = duration(sample.arrivalMonotonicMs, now), stats = record.streams[sample.stream];
        sample.callbackEndMonotonicMs = now; sample.callbackDurationMs = ms;
        stats.chunkCallbackTotalMs += ms; stats.chunkCallbackMaxMs = Math.max(stats.chunkCallbackMaxMs, ms);
    });
    const finish = () => {
        if (finished) return record;
        finished = true;
        // Disable first, independently of summary access or other telemetry failures.
        safely('event-loop-disable', () => { histogram?.disable(); });
        safely('finish-clock', () => { record.endedMonotonicMs = clock(); });
        safely('cpu-finish', () => {
            if (!initialCpu) throw new Error('Initial CPU sample unavailable.');
            const delta = cpuUsage(initialCpu);
            if (![delta.user, delta.system].every(value => Number.isFinite(value) && value >= 0)) throw new Error('Invalid CPU delta.');
            record.cpuUsageDeltaMicros = delta;
        });
        safely('event-loop-summary', () => {
            if (!histogram) throw new Error('Event-loop monitor unavailable.');
            const count = histogram.count;
            if (!Number.isSafeInteger(count) || count < 0) throw new Error('Event-loop sample count unavailable.');
            record.eventLoopDelay.count = count;
            if (count === 0) { record.eventLoopDelay.reason = 'no-samples'; return; }
            const values = { min: histogram.min, max: histogram.max, mean: histogram.mean, p99: histogram.percentile(99) };
            if (!Object.values(values).every(value => Number.isFinite(value) && value >= 0)) throw new Error('Invalid event-loop histogram.');
            Object.assign(record.eventLoopDelay, { status: 'available', ...values });
        });
        for (const [name, stream] of Object.entries({ stdout, stderr })) safely(`queue-final-${name}`, () => {
            const value = queueLength(stream);
            record.streams[name].queueFinalBytes = value;
            record.streams[name].queuePeakBytes = Math.max(record.streams[name].queuePeakBytes, value);
        });
        record.samples = [...first, ...tail].sort((a, b) => a.sequence - b.sequence);
        record.observationStatus = record.errors.length || record.droppedErrorCount || record.eventLoopDelay.status !== 'available' ? 'partial' : 'complete';
        return record;
    };
    return { record, beginChunk, rawWrite, forwardWrite, endChunk, finish };
}
