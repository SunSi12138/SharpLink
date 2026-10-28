"""Abstract TSO store-buffer litmus, not a CLR/ARM proof or runtime benchmark.
One producer publishes a mailbox item then signals. Consumer clears an old latch,
checks mailbox, and arms a wait. Interlocked RMW and explicit fences flush the
producer store buffer. Explores every enabled interleaving and spontaneous flush.
"""
from collections import deque
from dataclasses import dataclass, replace
import json

@dataclass(frozen=True)
class S:
    p: str = 'publish'
    c: str = 'clear'
    latch: int = 1
    mailbox: int = 0
    buffered: int = 0
    first: int = -1
    woke: bool = False

def successors(s, variant):
    r=[]
    def add(label, **kw): r.append((label, replace(s, **kw)))
    if s.buffered: add('P.storebuffer.flush(mailbox=1)', mailbox=1, buffered=0)
    if s.p=='publish': add('P.enqueue release-store buffered', buffered=1,p='read')
    elif s.p=='read':
        nxt = ('done' if variant=='unsafe_read_only' else 'fence') if s.latch==1 and variant!='original' else 'rmw'
        add(f'P.acquire-read latch={s.latch}',first=s.latch,p=nxt)
    elif s.p=='fence': add('P.full-fence publishes mailbox',mailbox=s.mailbox|s.buffered,buffered=0,p='done' if variant=='fence_without_reread' else 'reread')
    elif s.p=='reread': add(f'P.post-fence reread latch={s.latch}',p='done' if s.latch==1 else 'rmw')
    elif s.p=='rmw':
        # Abstract the original claim/latch/recheck into one RMW: if armed,
        # complete that arm; otherwise leave one latch. Fences publication.
        add('P.original full-fence claim-or-latch',mailbox=s.mailbox|s.buffered,buffered=0,
            latch=0 if s.latch>1 else 1,woke=s.woke or s.latch>1,p='done')
    if s.c=='clear': add('C.Interlocked clear old latch',latch=0 if s.latch==1 else s.latch,c='peek')
    elif s.c=='peek': add(f'C.mailbox acquire-read={s.mailbox}',c='done' if s.mailbox else 'arm')
    elif s.c=='arm': add(f'C.exchange arm; previous={s.latch}',latch=0 if s.latch==1 else 4,
                         woke=s.woke or s.latch==1,c='done')
    return r

def run(v):
    todo=deque([(S(),[])])
    seen=set(); bad=[]; terminal=0
    while todo:
        s,path=todo.popleft()
        if s in seen: continue
        seen.add(s)
        nxt=successors(s,v)
        if not nxt:
            terminal+=1
            if s.mailbox and s.latch==4 and not s.woke: bad.append(path)
        for label,n in nxt: todo.append((n,path+[label]))
    return {'variant':v,'states':len(seen),'terminalStates':terminal,'lostWakeTerminalStates':len(bad),'example':bad[0] if bad else None}

if __name__=='__main__':
    results=[run(v) for v in ['original','unsafe_read_only','fence_without_reread','fenced_reread']]
    assert results[0]['lostWakeTerminalStates']==0
    assert results[1]['lostWakeTerminalStates']>0
    assert results[2]['lostWakeTerminalStates']>0
    assert results[3]['lostWakeTerminalStates']==0
    print(json.dumps({'scope':'bounded abstract TSO model only; not runtime/performance or full weak-memory proof','results':results},indent=2))
