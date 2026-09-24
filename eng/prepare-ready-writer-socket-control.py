#!/usr/bin/env python3
"""Apply one hash-checked socket-option control to a disposable diagnostic checkout."""
import argparse
import hashlib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PAIR = Path('test/SharpLink.Benchmarks/PhaseBTransportPair.cs')
BASE_BLOB = '0f6fcfdd7400066a8146b4ba731c4840dae1ae8e'
PROFILES = ('default', '262144')


def transform(source):
    raw = source.encode()
    blob = hashlib.sha1(b'blob ' + str(len(raw)).encode() + b'\0' + raw).hexdigest()
    if blob != BASE_BLOB:
        raise ValueError('Transport-pair source drift: review before applying a socket control')
    anchor = '        var name = "phase-b-" + Guid.NewGuid().ToString("N");'
    replacement = '''        // Diagnostic overlay only: ordinary benchmark sources are unchanged.
#if !SHARPLINK_READY_WRITER_DIAGNOSTIC
#error Socket-buffer control must never be built as performance evidence.
#endif
        var requested = Environment.GetEnvironmentVariable("SHARPLINK_TCP_RCVBUF_CONTROL") ?? "default";
        SocketTransportOptions? socketOptions = requested switch
        {
            "default" => null,
            "262144" => new SocketTransportOptions { ReceiveBufferBytes = 262144 },
            _ => throw new ArgumentException("Unrecognized socket-buffer control.")
        };
        Console.Error.WriteLine($"SOCKET-CONTROL transport={kind}; requested={requested}; not-production-default-or-performance-evidence");
'''+anchor
    assert source.count(anchor) == 1
    source = source.replace(anchor, replacement)
    for old, new in (
        ('new SocketServerTransportListener(new IPEndPoint(IPAddress.Loopback, 0))',
         'new SocketServerTransportListener(new IPEndPoint(IPAddress.Loopback, 0), options: socketOptions)'),
        ('new SocketClientTransportFactory(listener.LocalEndPoint!)',
         'new SocketClientTransportFactory(listener.LocalEndPoint!, socketOptions)')):
        assert source.count(old) == 1
        source = source.replace(old, new)
    return source


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--root', type=Path, default=ROOT)
    args = parser.parse_args()
    path = args.root / PAIR
    path.write_text(transform(path.read_text()))
    print('Applied diagnostic-only identical endpoint control; original timeout/credit/flush unchanged')


if __name__ == '__main__':
    main()
