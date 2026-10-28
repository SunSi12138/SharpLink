"""Bounded SC protocol model: two Signal callers, one stale timed callback,
then one untimed rearm. Models claim separately from completion. Not CLR/ARM proof.
"""
from collections import deque
from dataclasses import dataclass, replace
import json

@dataclass(frozen=True)
class S:
    word:int=6                    # timed generation 1
    generation:int=1
    counts:tuple=(0,0)
    ps:tuple=(('read',0,0),('read',0,0)) # pc, observed, claimed generation
    timer:str='claim'
    consumer:str='await1'

def successors(s,variant):
    out=[]
    def add(label,**kw): out.append((label,replace(s,**kw)))
    def producer(i,label,pc=None,observed=None,claimed=None,**kw):
        p=list(s.ps); old=p[i]
        p[i]=(pc if pc is not None else old[0],observed if observed is not None else old[1],claimed if claimed is not None else old[2])
        add(f'P{i}.{label}',ps=tuple(p),**kw)
    def complete(label,g,**kw):
        counts=list(s.counts);counts[g-1]+=1
        assert s.generation==g, (label,'stale core completion',s)
        assert counts[g-1]==1, (label,'double completion',s)
        add(label,counts=tuple(counts),**kw)
    for i,(pc,obs,g) in enumerate(s.ps):
        if pc=='read': producer(i,'read',pc='fence' if s.word==1 and variant=='fenced' else 'claim',observed=s.word)
        elif pc=='fence': producer(i,'fence',pc='reread')
        elif pc=='reread': producer(i,'reread',pc='done' if s.word==1 else 'claim',observed=s.word)
        elif pc=='claim':
            if obs>1 and s.word==obs: producer(i,'claim',pc='complete',claimed=obs>>2,word=0)
            else: producer(i,'claim-miss',pc='latch')
        elif pc=='latch': producer(i,'or-latch',pc='loopread',word=s.word|1)
        elif pc=='loopread': producer(i,'loopread',pc='done' if s.word<=1 else 'loopclaim',observed=s.word)
        elif pc=='loopclaim':
            if s.word==obs: producer(i,'loopclaim',pc='complete',claimed=obs>>2,word=0)
            else: producer(i,'loopclaim-miss',pc='loopread')
        elif pc=='complete':
            p=list(s.ps);p[i]=('done',obs,g);complete(f'P{i}.complete-generation-{g}',g,ps=tuple(p))
    if s.timer=='claim':
        if s.word==6: add('timer.claim',word=0,timer='complete')
        else: add('timer.stale-or-lost',timer='done')
    elif s.timer=='complete': complete('timer.complete-generation-1',1,timer='done')
    if s.consumer=='await1' and s.counts[0]: add('consumer.reset-core2',generation=2,consumer='exchange')
    elif s.consumer=='exchange': add('consumer.exchange-arm2',word=8,consumer='claimlatch' if s.word==1 else 'done')
    elif s.consumer=='claimlatch':
        if s.word==8: add('consumer.claim-old-latch',word=0,consumer='complete')
        else: add('consumer.old-latch-lost',consumer='done')
    elif s.consumer=='complete': complete('consumer.complete-generation-2',2,consumer='done')
    return out

def run(v):
    todo=deque([S()]);seen=set();terminal=0
    while todo:
        s=todo.popleft()
        if s in seen:continue
        seen.add(s);nxt=successors(s,v)
        if not nxt:terminal+=1
        todo.extend(n for _,n in nxt)
    return {'variant':v,'states':len(seen),'terminalStates':terminal,'doubleOrStaleCompletions':0}

if __name__=='__main__':
    print(json.dumps({'scope':'bounded SC interleaving model, not weak-memory or actual runtime proof','results':[run('original'),run('fenced')]},indent=2))
