#!/usr/bin/env python3
"""Retain actual rooted RPC machine code for ownership/result-shape review."""
import pathlib
import re
import subprocess
import sys

binary, output = map(pathlib.Path, sys.argv[1:3])
pattern = re.compile(r'PumpGeneratedOutboundStreamAsync|SendClientStreamAsync|AwaitResolvedSendCreditAsync|AwaitPreCreditBudgetAnd|SendStreamChunkKnownSize|SendClientStreamChunkKnownSize|RecordConsumed|AcceptReceived|TryReserveOrderedSendCredit')
lines = subprocess.check_output(['nm', '--defined-only', str(binary)], text=True).splitlines()
symbols = []
for line in lines:
    parts = line.split(maxsplit=2)
    if len(parts) == 3 and parts[1] in ('t', 'T') and pattern.search(parts[2]):
        symbols.append(parts[2])
assert symbols, 'No expected rooted RPC symbols; native code inspection unavailable'
with output.open('w') as file:
    for symbol in symbols:
        subprocess.run(['objdump', '-d', '--disassemble=' + symbol, str(binary)], stdout=file, stderr=subprocess.STDOUT, check=True)
output.with_suffix('.symbols.json').write_text(__import__('json').dumps(symbols, indent=2) + '\n')
print(f'Retained {len(symbols)} actual native RPC code symbols for {binary}')
