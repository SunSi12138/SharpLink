import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import test from 'node:test';
import { captureOwnedGroupSnapshot } from './ios-process-snapshot.mjs';

const pgid = 43210;
const row = `43210 123 ${pgid} 501 501 S 00:01 xcrun\n`;
function mockSpawn(behavior, observe = () => {}) {
    return (command, args, options) => {
        const child = new EventEmitter();
        child.pid = 99001;
        child.stdout = new PassThrough(); child.stderr = new PassThrough();
        child.kills = [];
        child.kill = signal => {
            child.kills.push(signal);
            queueMicrotask(() => child.emit('close', null, signal));
            return true;
        };
        child.unref = () => {};
        observe({ command, args, options, child });
        queueMicrotask(() => behavior(child));
        return child;
    };
}

test('snapshot uses only the absolute ps command and scoped fields, retaining verified group rows', async () => {
    let invocation;
    const result = await captureOwnedGroupSnapshot(pgid, { spawnImpl: mockSpawn(child => {
        child.stdout.write(row); child.emit('close', 0, null);
    }, call => { invocation = call; }) });
    assert.equal(invocation.command, '/bin/ps');
    assert.deepEqual(invocation.args, ['-x', '-g', String(pgid), '-o', 'pid=,ppid=,pgid=,uid=,ruid=,stat=,etime=,ucomm=']);
    assert.deepEqual(invocation.options.env, { COMMAND_MODE: 'unix2003', LC_ALL: 'C' });
    assert.equal(invocation.options.detached, false);
    assert.equal(result.status, 'observed-members');
    assert.deepEqual(result.rows, [{ pid: pgid, ppid: 123, pgid, uid: 501, ruid: 501, stat: 'S', elapsed: '00:01', program: 'xcrun' }]);
    assert.equal(result.snapshotPid, 99001);
});

for (const [name, output, code, reason] of [
    ['empty output', '', 0, 'empty-output'],
    ['wrong PGID', row + '12345 123 99999 501 501 S 00:01 unrelated\n', 0, 'wrong-pgid'],
    ['truncated output', row.trimEnd(), 0, 'truncated-output'],
    ['malformed output', 'not a process record\n', 0, 'malformed-output'],
    ['nonzero exit', row, 1, 'nonzero-exit']
]) {
    test(`snapshot ${name} is unknown, never proof of an empty or valid group`, async () => {
        const result = await captureOwnedGroupSnapshot(pgid, { spawnImpl: mockSpawn(child => {
            child.stdout.write(output); child.emit('close', code, null);
        }) });
        assert.equal(result.status, 'unknown');
        assert.equal(result.reason, reason);
        assert.deepEqual(result.rows, []);
    });
}

test('snapshot overflow is capped and kills only its own exact child', async () => {
    let child;
    const result = await captureOwnedGroupSnapshot(pgid, { spawnImpl: mockSpawn(instance => {
        instance.stdout.write(Buffer.alloc(16 * 1024 + 1, 120));
    }, call => { child = call.child; }) });
    assert.equal(result.reason, 'output-overflow');
    assert.equal(result.status, 'unknown');
    assert.deepEqual(result.rows, []);
    assert.deepEqual(child.kills, ['SIGKILL']);
});

test('snapshot timeout is bounded, kills only its child, and late close cannot promote it to success', async () => {
    let child;
    const start = Date.now();
    const result = await captureOwnedGroupSnapshot(pgid, { spawnImpl: mockSpawn(() => {}, call => { child = call.child; }) });
    assert.ok(Date.now() - start < 1500);
    assert.equal(result.reason, 'timeout');
    assert.equal(result.status, 'unknown');
    assert.deepEqual(child.kills, ['SIGKILL']);
    child.stdout.write(row); child.emit('close', 0, null);
    assert.equal(result.status, 'unknown');
    assert.deepEqual(result.rows, []);
});

test('snapshot spawn errors and unsafe PGIDs are unknown without broadening the process query', async () => {
    let calls = 0;
    const throwing = () => { calls++; throw Object.assign(new Error('mock denied ps'), { code: 'EPERM' }); };
    for (const invalid of [0, 1, -1, NaN, Infinity, Number.MAX_SAFE_INTEGER + 1]) {
        assert.equal((await captureOwnedGroupSnapshot(invalid, { spawnImpl: throwing })).reason, 'invalid-pgid');
    }
    assert.equal(calls, 0);
    const result = await captureOwnedGroupSnapshot(pgid, { spawnImpl: throwing });
    assert.equal(result.reason, 'spawn-error');
    assert.equal(result.spawnError.code, 'EPERM');
    assert.equal(calls, 1);
});

test('combined stdout/stderr cap does not retain a partial apparent process list', async () => {
    const result = await captureOwnedGroupSnapshot(pgid, { spawnImpl: mockSpawn(child => {
        child.stdout.write(row);
        child.stderr.write(Buffer.alloc(16 * 1024, 120));
    }) });
    assert.equal(result.status, 'unknown');
    assert.equal(result.reason, 'output-overflow');
    assert.deepEqual(result.rows, []);
});

test('snapshot delayed event-loop close after its monotonic budget is unknown even before timer dispatch', async () => {
    const result = await captureOwnedGroupSnapshot(pgid, { spawnImpl: mockSpawn(child => {
        child.stdout.write(row);
        const end = Date.now() + 300;
        while (Date.now() < end) {}
        child.emit('close', 0, null);
    }) });
    assert.equal(result.status, 'unknown');
    assert.equal(result.reason, 'timeout');
    assert.deepEqual(result.rows, []);
});
