"""Fixed BS1 source pins and balanced schedule; importing does not execute a workload."""
SOURCES = {
    'dev': '57f06f172d29fa5cfd4ea8e9b86a23f122df24ab',
    'a': 'ed34529fd660484c546f9ac96f73e322b5e04312',
    'b': '13e75f15f16cb98960c97f9500e17d26ba81f43b',
}
TREES = {
    'dev': 'e5b72625377ffdcf5f5470e2ddd69be12a449d8e',
    'a': '806337ad391f691b477a47d9349694fac95ed23f',
    'b': '025dba3a4756227e062eab005b2693fc24a62f6a',
}
COMPARISONS = {'dev-to-a': ('dev', 'a'), 'a-to-b': ('a', 'b')}
DEADLINES = ('off', 'on')
ROUNDS = 6
WARMUP = 3
SECONDS = 6

def schedule():
    cells = [(contrast, deadline) for deadline in DEADLINES for contrast in COMPARISONS]
    result = []
    for round_number in range(1, ROUNDS + 1):
        offset = (round_number - 1) % len(cells)
        for contrast, deadline in cells[offset:] + cells[:offset]:
            for index in ((0, 1) if round_number % 2 else (1, 0)):
                variant = ('baseline', 'candidate')[index]
                result.append({'contrast': contrast, 'deadline': deadline, 'round': round_number,
                    'variant': variant, 'arm': COMPARISONS[contrast][index],
                    'sample': f'static-intercepted-n1-c1-r{round_number}-{variant}'})
    return result

def validate_complete_summary(summary, integrity):
    if (integrity.get('evidenceValidation'), integrity.get('inventoryErrors'),
        integrity.get('rawSamples'), integrity.get('completedRawSamples'),
        integrity.get('failedSamples'), integrity.get('incompletePairs'),
        integrity.get('bootstrapReplicates'), integrity.get('bootstrapSeed')) != (
            'Passed', [], 12, 12, [], [], 20_000, 529):
        raise ValueError('Incomplete or invalid summary integrity')
    if len(summary) != 1 or (summary[0]['mode'], summary[0]['items'], summary[0]['workers'], summary[0]['pairs']) != ('static-intercepted', 1, 1, 6):
        raise ValueError('Summary cell/pair census mismatch')
    metrics = summary[0]['metrics']
    expected = {'operationsPerSecond', 'p50Us', 'p99Us', 'p999Us', 'allocatedBytesPerOperation', 'cpuUsPerOperation'}
    if set(metrics) != expected or any(len(metric['pairedRatios']) != 6 for metric in metrics.values()):
        raise ValueError('Summary metrics/paired ratio census mismatch')
