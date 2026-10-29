"""Fixed standalone marker roundtrip contract. No product or trace launch here."""
import math
from common import SOURCE

FIELDS = ['sourceRevision','variant','mode','itemCount','workSeconds','window','timestamp']
CENSUS = ('matchedProviderEvents','startIdCount','stopIdCount','malformedPhaseEvents',
          'otherPhaseEvents','recognizedStarts','recognizedStops')
EXPECTED_CENSUS = dict(zip(CENSUS,(2,1,1,0,0,1,1)))
FAILURES = {'invalid-pid','lost-or-truncated','marker-cardinality','marker-identity','marker-measurement',
            'window-boundary','malformed-or-reversed-event','malformed-event','parse-failure',
            'marker-census','unknown-coverage'}

def require(value, code):
    if not value: raise ValueError(code)

def count(value):
    require(type(value) is int and 0 <= value <= 9223372036854775807, 'MARKER_COUNT')
    return value

def exact(value, expected):
    if type(value) is not type(expected): return False
    if type(value) is list: return len(value)==len(expected) and all(exact(x,y) for x,y in zip(value,expected))
    return value==expected

def validate_emitter(v, pid):
    expected = dict(schemaVersion=1,purpose='MarkerCompatibilityOnly',listenerPassed=True,
        runtimeVersion='10.0.14',pid=pid,sourceRevision=SOURCE,variant='trace',mode='synthetic-marker',
        itemCount=100,workSeconds=1,window='marker-smoke',eventCount=2,
        constructionExceptionNull=True,enabled=True,eventIds=[1,2],eventNames=['WorkStart','WorkStop'],payloadNames=FIELDS)
    require(type(v) is dict and set(v)==set(expected)|{'startTick','stopTick','frequency'}, 'MARKER_EMITTER_SCHEMA')
    for k,want in expected.items():
        require(exact(v[k],want), 'MARKER_EMITTER_IDENTITY')
    for k in ('startTick','stopTick','frequency'): require(count(v[k])>0, 'MARKER_TICK')
    require(.5 <= (v['stopTick']-v['startTick'])/v['frequency'] <= 5, 'MARKER_TICK_WINDOW')
    return v

def render_marker(v):
    require(type(v) is dict and type(v.get('schema')) is int and v['schema']==1 and
        v.get('kind')=='synthetic-marker-check' and v.get('status') in ('Complete','Incomplete'), 'MARKER_ANALYSIS_SCHEMA')
    failures=v.get('failures')
    require(type(failures) is list and all(type(f) is str and f in FAILURES for f in failures), 'MARKER_REASON')
    require(v['status']!='Complete' or not failures, 'MARKER_COMPLETENESS')
    lines=['MARKER_ROUNDTRIP status='+v['status']+' reasons='+(','.join(sorted(set(failures))) or 'none')]
    if 'parse-failure' in failures:return lines
    c=v['markerCensus']; require(type(c) is dict and set(c)==set(CENSUS), 'MARKER_CENSUS_SCHEMA')
    lines.append('MARKER_CENSUS provider=matched-expected ID1=start ID2=stop '+' '.join(k+'='+str(count(c[k])) for k in CENSUS))
    lines.append('MARKER_TRACE observedSampleCount='+str(count(v['observedSampleCount']))+' lostEvents='+str(count(v['lostEvents'])))
    return lines

def validate_analysis(v):
    render_marker(v)
    require(set(v)=={'schema','kind','status','failures','markerCensus','observedSampleCount','lostEvents',
        'windowMilliseconds','canonicalStartRecognized','canonicalStopRecognized'}, 'MARKER_ANALYSIS_SCHEMA')
    require(v['status']=='Complete' and v['failures']==[] and v['markerCensus']==EXPECTED_CENSUS and
        v['lostEvents']==0 and v['canonicalStartRecognized'] is True and v['canonicalStopRecognized'] is True, 'MARKER_ROUNDTRIP_INCOMPLETE')
    span=v['windowMilliseconds']
    require(type(span) in (int,float) and math.isfinite(span) and 500<=span<=5000, 'MARKER_WINDOW')
    return v
