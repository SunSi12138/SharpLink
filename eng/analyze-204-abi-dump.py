#!/usr/bin/env python3
"""Read one existing synthetic test dump, no test execution and no broad string/env inspection."""
import hashlib,json,pathlib,re,subprocess,sys,time
collector,dump,out=map(pathlib.Path,sys.argv[1:4]);out.mkdir(parents=True,exist_ok=True)
assert hashlib.sha256(dump.read_bytes()).hexdigest()=='904d23ea529f02e7f8845edfcfa83fec03223e774c6c9cc65dabab740a1fad95'
started=time.monotonic();queries=[];seen=set();serial=0

def query(command,label):
    global serial
    if time.monotonic()-started>420: raise TimeoutError('Analysis admission budget exceeded')
    serial+=1;path=out/f'{serial:03d}-{label}.txt'
    p=subprocess.run([str(collector),'analyze',str(dump),'--command',command,'--command','exit'],stdout=subprocess.PIPE,stderr=subprocess.STDOUT,timeout=45)
    text=p.stdout.decode(errors='replace');path.write_text(text)
    queries.append({'Command':command,'Path':path.name,'ExitCode':p.returncode})
    (out/'queries.json').write_text(json.dumps(queries,indent=2))
    print(f'{path.name}: exit={p.returncode}',flush=True)
    return text

def object_query(address,label):
    if int(address,16)==0 or address in seen: return ''
    seen.add(address)
    return query('dumpobj '+address,label)

# Raw fields only for named SharpLink lifecycle/flow-control objects, never broad strings or env.
types=[
 'SharpLink.Runtime.StreamFlowController+CreditWaiter',
 'SharpLink.Runtime.StreamFlowController+SendState',
 'SharpLink.Runtime.StreamFlowController',
 'SharpLink.Server.ServerCallCancellationState',
 'SharpLink.Client.ClientConnection',
 'SharpLink.Client.PendingRequestTable',
 'SharpLink.DynamicPlugin.DynamicPluginService',
 'SharpLink.Runtime.PreCreditSerializedBudget',
 'SharpLink.Runtime.PooledAsyncStreamDispatcher',
 'SharpLink.Runtime.RpcSession+SendPump',
]
query("gcroot 00007efd1b8c2320", "awaited-task-roots")
queue=[]
for index,name in enumerate(types):
    listing=query('dumpheap -type '+name+' -short',f'type-{index}')
    addresses=re.findall(r'(?m)^([0-9a-fA-F]{12,16})\s*$',listing)
    if len(addresses)>32: raise RuntimeError(f'Targeted type unexpectedly broad: {name} {len(addresses)}')
    for address in addresses:
        text=object_query(address,'object-'+address)
        queue.append((text,0))
for address in ['00007efd1b88c588','00007efd1b98ea08','00007efd1b8c2aa8','00007efd1b8c2320','00007efd1b8c2a40','00007efd1b98ea90']:
    queue.append((object_query(address,'async-'+address),0))

# Follow only explicit wait/cancellation/state references, not arbitrary graph roots.
reference_fields={'_invocationCancellation','_source','m_task','_task','Completion','Owner','Node','Rejection','_terminalException','AbortException','_serverStreamDisposed','_protocolState','<FlowController>k__BackingField','PendingCalls','<PendingCalls>k__BackingField','_pendingCalls','_sendStates','_receiveStates','_waiters','list','head','next','prev','_progressQueue','_normalQueue','_wakeup','_items','_consumerAbandonedAsync','_dispatchState','lifetimeCancellation','<lifetimeCancellation>5__2','_linkedCancellation','_cancellation'}
value_fields={'StateMachine','Key','cancellationToken','<cancellationToken>5__2','<>u__1','<>u__2','<>u__3','<>u__4','<>u__5','_sessionCancellation'}
value_seen=set()
while queue:
    text,depth=queue.pop(0)
    if depth>=3: continue
    for line in text.splitlines():
        parts=line.split()
        if len(parts)<8 or not re.fullmatch(r'[0-9a-fA-F]{12,16}',parts[0]): continue
        name=parts[-1];value=parts[-2]
        if not re.fullmatch(r'[0-9a-fA-F]{12,16}',value) or int(value,16)==0: continue
        if name in reference_fields:
            queue.append((object_query(value,'ref-'+name.replace('<','').replace('>','')+'-'+value),depth+1))
        elif name in value_fields and 'instance' in parts and (parts[0],value) not in value_seen:
            value_seen.add((parts[0],value))
            queue.append((query(f'dumpvc {parts[0]} {value}','value-'+value),depth+1))
            if name=='StateMachine' and 'ExecuteDynamicStreamExitAndUnloadAsync' in text:
                methods=query('dumpmt -md '+parts[0],'helper-methods')
                for method_line in methods.splitlines():
                    if 'MoveNext()' in method_line:
                        candidates=re.findall(r'\b[0-9a-fA-F]{12,16}\b',method_line)
                        for candidate in candidates[:2]:
                            query('dumpil '+candidate,'helper-il-'+candidate)
(out/'analysis-summary.json').write_text(json.dumps({'DumpSha256':'904d23ea529f02e7f8845edfcfa83fec03223e774c6c9cc65dabab740a1fad95','Queries':len(queries),'ElapsedSeconds':time.monotonic()-started,'ReadOnly':True},indent=2))
