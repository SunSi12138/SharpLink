"""Frozen C1 CPU diagnostic constants; importing performs no probes."""
SOURCE = '57f06f172d29fa5cfd4ea8e9b86a23f122df24ab'
TREE = 'e5b72625377ffdcf5f5470e2ddd69be12a449d8e'
SDK = '10.0.102'
RUNTIME = '10.0.14'
CASES = (('static-intercepted', 1), ('static-intercepted', 100), ('dynamic-intercepted', 1))
CAP = 256 * 1024 * 1024
TIMEOUT = 180
# EventPipe keyword strings are hexadecimal: 0x19 == decimal 25 == GC1|Loader8|JIT0x10.
# Level4 Informational excludes allocation ticks (Verbose5). Method-load/version
# events survive, but JIT-start and IL maps do not: no complete JIT timeline proof.
PROVIDERS = 'Microsoft-Windows-DotNETRuntime:0x19:4,SharpLink-RpcCpu-Phase:0xffffffffffffffff:4'

def record_command(perf, path, child):
    return [str(perf), 'record', '-e', 'task-clock:u', '-F', '199', '--strict-freq',
            '--call-graph', 'fp,64', '--user-callchains', '--clockid', 'mono', '-T', '--no-buildid',
            '--no-buildid-cache', '-o', str(path), '--', *map(str, child)]

def script_command(perf, path):
    return [str(perf), 'script', '--ns', '--show-lost-events', '-i', str(path),
            '-F', 'comm,pid,tid,time,event,ip,sym,dso,period']
