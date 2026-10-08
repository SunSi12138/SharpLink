"""No installation, privilege escalation, security mutation, or tracing fallback."""
import hashlib
import json
import os
import pathlib
import re
import signal
import struct
import subprocess
import time

KIT = pathlib.Path(__file__).resolve().parent
DEV = '0fe26024b114bb6e78411a9b86276086c045d03d'
FUSED = '0390a381d0aa692ecc1ddc7625dcfc3c9ce842d7'
EVENT = 'cpu-clock:u'
FREQUENCY = 99
FIELDS = 'pid,tid,time,period,event,ip,sym,dso'

class Blocked(RuntimeError):
    pass

def sha(path):
    h = hashlib.sha256()
    with pathlib.Path(path).open('rb') as f:
        while data := f.read(1024 * 1024):
            h.update(data)
    return h.hexdigest()

def write_json(path, value):
    pathlib.Path(path).write_text(json.dumps(value, indent=2, allow_nan=False) + '\n')

def host_identity():
    def read(name):
        p = pathlib.Path(name)
        return p.read_text().strip() if p.exists() else None
    return dict(hostname=os.uname().nodename, kernel=os.uname().release,
                machine=os.uname().machine, boot_id=read('/proc/sys/kernel/random/boot_id'),
                uid=os.getuid(), euid=os.geteuid(), affinity=sorted(os.sched_getaffinity(0)),
                perf_event_paranoid=read('/proc/sys/kernel/perf_event_paranoid'),
                cap_eff=next((line.split(':', 1)[1].strip() for line in read('/proc/self/status').splitlines()
                              if line.startswith('CapEff:')), None))

def safe_env():
    env = dict(os.environ, LC_ALL='C', LANG='C', DEBUGINFOD_URLS='', PERF_PAGER='cat')
    return env

def run(command, out, stem, *, timeout=120, env=None, cwd=None):
    """Keep command/stdout/stderr/exit including timeouts; never overwrite a prior attempt."""
    out = pathlib.Path(out)
    paths = [out / f'{stem}.{suffix}' for suffix in ('command.json', 'stdout', 'stderr', 'result.json')]
    if any(p.exists() for p in paths):
        raise Blocked(f'Refusing to overwrite retained command {stem}')
    write_json(paths[0], dict(command=list(map(str, command)), cwd=str(cwd) if cwd else None,
                              timeout_seconds=timeout))
    started = time.monotonic()
    with paths[1].open('w') as stdout, paths[2].open('w') as stderr:
        try:
            process = subprocess.Popen(list(map(str, command)), stdout=stdout, stderr=stderr,
                                       env=env or safe_env(), cwd=cwd, start_new_session=True)
            try:
                code = process.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGTERM)
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait()
                code = 124
        except OSError as error:
            stderr.write(repr(error) + '\n')
            code = 127
    result = dict(exit_code=code, elapsed_seconds=time.monotonic()-started)
    write_json(paths[3], result)
    return code

def require(command, out, stem, **kwargs):
    code = run(command, out, stem, **kwargs)
    if code:
        tail = (pathlib.Path(out) / f'{stem}.stderr').read_text()[-5000:]
        raise Blocked(f'{stem} failed (exit {code}): {tail}')
    return (pathlib.Path(out) / f'{stem}.stdout').read_text()

def build_id(readelf, binary, out, stem):
    text = require([readelf, '-n', binary], out, stem)
    ids = re.findall(r'Build ID:\s*([0-9a-fA-F]+)', text)
    if len(set(ids)) != 1:
        raise Blocked(f'Expected one ELF build ID for {binary}, found {ids}')
    return ids[0].lower()

def record_command(perf, trace, executable, arguments):
    return [perf, 'record', '-e', EVENT, '-F', str(FREQUENCY), '-P', '-T',
            '--clockid', 'mono', '--call-graph', 'dwarf,16384', '-o', str(trace),
            '--', str(executable), *map(str, arguments)]

def inspect_perf_data(path):
    """Audit the documented perf.data file/record headers; no root, IPC or libperf needed.

    Fail closed on compressed/truncated/unsupported layouts. Decode all records to
    establish actual lost/throttle counts instead of trusting a text summary.
    """
    size = pathlib.Path(path).stat().st_size
    with pathlib.Path(path).open('rb') as f:
        header = f.read(104)
        if len(header) < 104 or header[:8] != b'PERFILE2':
            raise Blocked('Unsupported perf.data magic/endianness or incomplete header')
        header_size, attr_stride, attrs_offset, attrs_size, data_offset, data_size = struct.unpack_from('<6Q', header, 8)
        if header_size < 104 or attr_stride < 112 or attrs_size != attr_stride:
            raise Blocked(f'Expected exactly one uncompressed software event attribute, got stride={attr_stride} size={attrs_size}')
        if attrs_offset + attrs_size > size or data_offset + data_size > size:
            raise Blocked('Truncated perf.data section')
        f.seek(attrs_offset)
        attr = f.read(attr_stride)
        event_type, attr_size = struct.unpack_from('<II', attr)
        config, requested_frequency, sample_type = struct.unpack_from('<QQQ', attr, 8)
        flags = struct.unpack_from('<Q', attr, 40)[0]
        if attr_size < 96:
            raise Blocked('perf_event_attr lacks explicit clockid')
        clockid = struct.unpack_from('<i', attr, 92)[0]
        checks = dict(software_cpu_clock=event_type == 1 and config == 0,
                      user_only=bool(flags & (1 << 5)) and not bool(flags & (1 << 4)),
                      explicit_monotonic=bool(flags & (1 << 25)) and clockid == 1,
                      inherits_threads=bool(flags & (1 << 1)),
                      frequency_mode=bool(flags & (1 << 10)) and requested_frequency == FREQUENCY,
                      has_time=bool(sample_type & (1 << 2)), has_period=bool(sample_type & (1 << 8)),
                      has_tid=bool(sample_type & (1 << 1)),
                      has_dwarf_stack=bool(sample_type & (1 << 12)) and bool(sample_type & (1 << 13)))
        if not all(checks.values()):
            raise Blocked(f'Unexpected collector attributes: {checks}')
        counts = {}
        lost = throttle = 0
        offset = data_offset
        while offset < data_offset + data_size:
            f.seek(offset)
            record = f.read(8)
            if len(record) != 8:
                raise Blocked('Truncated perf record header')
            kind, misc, length = struct.unpack('<IHH', record)
            if length < 8 or offset + length > data_offset + data_size:
                raise Blocked('Invalid/truncated perf record length')
            counts[kind] = counts.get(kind, 0) + 1
            if kind == 2:  # PERF_RECORD_LOST: id then lost count
                if length < 24:
                    raise Blocked('Malformed PERF_RECORD_LOST')
                lost += struct.unpack('<QQ', f.read(16))[1]
            elif kind == 13:  # PERF_RECORD_LOST_SAMPLES
                if length < 16:
                    raise Blocked('Malformed PERF_RECORD_LOST_SAMPLES')
                lost += struct.unpack('<Q', f.read(8))[0]
            elif kind in (5, 6):  # THROTTLE / UNTHROTTLE
                throttle += 1
            elif kind == 81:  # PERF_RECORD_COMPRESSED
                raise Blocked('Compressed perf records were not requested and are unsupported')
            offset += length
        if offset != data_offset + data_size:
            raise Blocked('Record section did not end exactly')
        result = dict(checks=checks, raw_record_counts=counts, raw_sample_records=counts.get(9, 0),
                      events_lost=lost, throttle_records=throttle, clock_id=clockid,
                      event=EVENT, sample_frequency=FREQUENCY, sha256=sha(path))
        if lost or throttle or not counts.get(9):
            raise Blocked(f'Incomplete on-CPU population: {result}')
        return result

def decode(perf, trace, out, stem='script'):
    raw = inspect_perf_data(trace)
    write_json(pathlib.Path(out) / 'perf-data-audit.json', raw)
    require([perf, 'script', '--ns', '--show-lost-events', '-F', FIELDS, '-i', trace], out, stem,
            timeout=180)
    return raw
