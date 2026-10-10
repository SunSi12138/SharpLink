import json, math, pathlib, os
SCHEMA=json.loads(pathlib.Path(__file__).with_name('sample.schema.json').read_text())
def validate(data,source,diagnostic=False):
    if set(data)!=set(SCHEMA['required']):raise ValueError('Missing or unknown sample fields')
    for name,rule in SCHEMA['properties'].items():
        v=data[name];types=rule['type'];types=[types] if isinstance(types,str) else types
        good=any((t=='null' and v is None) or (t=='boolean' and type(v)==bool) or (t=='integer' and type(v)==int) or (t=='number' and type(v) in [int,float] and math.isfinite(v)) or (t=='string' and type(v)==str) for t in types)
        if not good:raise ValueError('Wrong type '+name)
    if data['schemaVersion']!=2 or data['sourceSha']!=source or data['traceEnabled'] or data['diagnostic']!=diagnostic:raise ValueError('Wrong sample identity/mode')
    if data['operations']<=0 or data['bytes']<0 or data['qps']<=0:raise ValueError('Invalid measures')
    if data['rpc'] in ['oneway','oneway-bounded','credit-control'] and data['received']!=data['operations']:raise ValueError('OneWay receive mismatch')
    if not math.isclose(data['bytesPerOperation'],data['bytes']/data['operations']):raise ValueError('B/op denominator mismatch')

    if data["receiveCreditBounded"]:
        if not (data["creditAcquired"]==data["creditReleased"]==data["received"]==data["operations"] and data["creditFinalAvailable"]==data["concurrency"] and 0<data["creditPeakOutstanding"]<=data["concurrency"]):raise ValueError("Credit accounting mismatch")

    if os.environ.get("ISSUE739_RUNTIME_VERSION") and data["runtime"] != os.environ["ISSUE739_RUNTIME_VERSION"]:raise ValueError("Wrong runtime pin")
