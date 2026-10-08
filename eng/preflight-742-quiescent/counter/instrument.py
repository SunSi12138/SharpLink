import re,sys,json
from pathlib import Path
root=Path(sys.argv[1]); runtime=root/'src/SharpLink.Runtime'; sites=[]
helper='''namespace SharpLink.Runtime;

// Diagnostic-only: each original atomic operation is evaluated before observation.
internal static class LifecycleDiagnosticCounters
{
    private static readonly ThreadLocal<Dictionary<string, long[]>> Counters = new(static () => [], trackAllValues: true);
    internal static string? Phase;
    private static void Record(string site, bool success = false)
    {
        var phase = Volatile.Read(ref Phase);
        if (phase is null) return;
        var key = phase + "|" + site;
        var values = Counters.Value!;
        if (!values.TryGetValue(key, out var count)) values.Add(key, count = new long[2]);
        count[0]++;
        if (success) count[1]++;
    }
    internal static int Observe(string site, int result) { Record(site); return result; }
    internal static long Observe(string site, long result) { Record(site); return result; }
    internal static T? Observe<T>(string site, T? result) where T : class { Record(site); return result; }
    internal static int ObserveCas(string site, int result, int expected) { Record(site, result == expected); return result; }
    internal static long ObserveCas(string site, long result, long expected) { Record(site, result == expected); return result; }
    internal static T? ObserveCas<T>(string site, T? result, T? expected) where T : class
    { Record(site, ReferenceEquals(result, expected)); return result; }
    internal static void Hit(string site) => Record(site);
    internal static void Reset() { Phase = null; foreach (var values in Counters.Values) values.Clear(); }
    internal static void Dump(string scenario)
    {
        Phase = null;
        Dictionary<string, long[]> total = [];
        foreach (var values in Counters.Values)
            foreach (var item in values)
            {
                if (!total.TryGetValue(item.Key, out var counts)) total.Add(item.Key, counts = new long[2]);
                counts[0] += item.Value[0]; counts[1] += item.Value[1];
            }
        foreach (var item in total.OrderBy(static pair => pair.Key))
            Console.WriteLine($"{scenario}|{item.Key}|{item.Value[0]}|{item.Value[1]}");
    }
}
'''
(runtime/'LifecycleDiagnosticCounters.cs').write_text(helper)
files=sorted(set(runtime.glob('StreamManager*.cs'))|set(runtime.glob('StreamFlowController*.cs'))|{runtime/'PooledAsyncStreamDispatcher.cs'})
for p in files:
 s=p.read_text();edits=[]
 entry=re.search(r'\b(?:partial )?class DispatcherEntry\b',s);request=re.search(r'\bclass RequestDispatchers\b',s)
 def owner(pos):
  if p.name.startswith('StreamFlow'):return 'flow'
  if p.name.startswith('Pooled'):return 'pooled'
  if entry and pos>=entry.start():return 'entry'
  if request and pos>=request.start():return 'request'
  return 'manager'
 for m in re.finditer(r'Interlocked\.(\w+)\(',s):
  op=m.group(1);i=m.end();depth=1
  while depth:
   depth+=(s[i]=='(')-(s[i]==')');i+=1
  call=s[m.start():i];line=s[:m.start()].count('\n')+1
  site=f'{owner(m.start())}|{op}|{p.name}:{line}'
  if op=='CompareExchange':
   expected=call[:-1].rsplit(',',1)[1].strip();assert re.fullmatch(r'[A-Za-z_]\w*|[012]',expected),expected
   replacement=f'LifecycleDiagnosticCounters.ObserveCas("{site}", {call}, {expected})'
  else: replacement=f'LifecycleDiagnosticCounters.Observe("{site}", {call})'
  edits.append((m.start(),i,replacement));sites.append({'site':site,'original':call})
 for m in re.finditer(r'\block\s*\(',s):
  i=m.end();depth=1
  while depth:
   depth+=(s[i]=='(')-(s[i]==')');i+=1
  while s[i].isspace():i+=1
  line=s[:m.start()].count('\n')+1;site=f'{owner(m.start())}|lock|{p.name}:{line}'
  indent=' '*((len(s[s.rfind('\n',0,m.start())+1:m.start()]))+4)
  if s[i]=='{': edits.append((i+1,i+1,f'\n{indent}LifecycleDiagnosticCounters.Hit("{site}");'))
  else:
   end=s.index(';',i)+1
   # Single-statement locks contain no atomics in the selected source.
   assert 'Interlocked.' not in s[i:end]
   edits.append((i,end,'{ LifecycleDiagnosticCounters.Hit("'+site+'"); '+s[i:end]+' }'))
  sites.append({'site':site,'original':s[m.start():i]})
 if p.name=='PooledAsyncStreamDispatcher.cs':
  old='void IStreamDispatchLease.OnDispatchesDrained() => TryReturnToPool();'
  assert old in s
  pos=s.index(old);edits.append((pos,pos+len(old),'void IStreamDispatchLease.OnDispatchesDrained() { LifecycleDiagnosticCounters.Hit("pooled|callback|OnDispatchesDrained"); TryReturnToPool(); }'))
 for st,en,new in sorted(edits,reverse=True):s=s[:st]+new+s[en:]
 p.write_text(s)
(root.parent/f'{root.name}-sites.json').write_text(json.dumps(sites,indent=2)+'\n')
print(root.name,len(sites))
