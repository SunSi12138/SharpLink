#!/usr/bin/env python3
"""Small independent model of the physical producer + FIFO + remote-work fence.

Two pre-fence producers may serialize, abandon logically, enqueue, and finish at
different times. A parsed request starts asynchronous remote work. Neither send
ordering nor logical completion alone proves physical quiescence.
"""
from collections import deque
from dataclasses import dataclass, replace
import json
from pathlib import Path


@dataclass(frozen=True, slots=True)
class State:
    phase: int = 0  # open, closed, fence sent, acknowledgement sent, committed
    producer: tuple = (0, 0)  # not admitted, serializing, enqueued, physically done
    logical: tuple = (False, False)
    queue: tuple = ()  # data producer index, or -1 for fence
    remote: tuple = (0, 0)  # absent, physically active, logically abandoned, physically done
    outqueue: tuple = ()  # response index or -1 for FenceAck, server-to-client FIFO
    consumer: tuple = (0, 0)  # absent, physical decoder/consumer, abandoned, physically done
    fence_seen: bool = False
    ack: bool = False


def at(values, i, v):
    return values[:i] + (v,) + values[i+1:]


def successors(s, mutant):
    if s.phase == 0:
        yield 'close existing producer admission gate', replace(s, phase=1)
    for i in range(2):
        if s.phase == 0 and s.producer[i] == 0:
            yield f'admit producer {i} through existing gate', replace(s, producer=at(s.producer, i, 1), logical=at(s.logical, i, True))
        if s.producer[i] in (1, 2) and s.logical[i]:
            yield f'producer {i} logically abandoned; codec may still execute', replace(s, logical=at(s.logical, i, False))
        if s.producer[i] == 1:
            yield f'producer {i} finishes serialization and enqueues Data', replace(s, producer=at(s.producer, i, 2), queue=s.queue+(i,))
        if s.producer[i] == 2:
            yield f'producer {i} physically releases all code/send ownership', replace(s, producer=at(s.producer, i, 3), logical=at(s.logical, i, False))
        if s.remote[i] == 1:
            yield f'remote call {i} logically completes while generated producer remains', replace(s, remote=at(s.remote, i, 2))
        if s.remote[i] in (1, 2):
            yield f'remote call {i} physically drains and enqueues final response', replace(s, remote=at(s.remote, i, 3), outqueue=s.outqueue+(i,))
        if s.consumer[i] == 1:
            yield f'client response consumer {i} is logically abandoned but retains codec', replace(s, consumer=at(s.consumer, i, 2))
        if s.consumer[i] in (1, 2):
            yield f'client response consumer {i} physically drains decoder/buffer/dispose', replace(s, consumer=at(s.consumer, i, 3))

    producers_drained = all(p in (0, 3) for p in s.producer)
    if mutant == 'logical_producer_count':
        producers_drained = not any(s.logical)
    if s.phase == 1 and producers_drained:
        yield 'enqueue Fence after producer-drain observation', replace(s, phase=2, queue=s.queue+(-1,))

    if s.queue:
        positions = range(len(s.queue)) if mutant == 'non_fifo_transport' else (0,)
        for pos in positions:
            item = s.queue[pos]
            ns = replace(s, queue=s.queue[:pos]+s.queue[pos+1:])
            if item == -1:
                yield 'server parses Fence (prior dispatch may still be asynchronous)', replace(ns, fence_seen=True)
            else:
                yield f'server parses Data {item}; begins asynchronous remote work', replace(ns, remote=at(s.remote, item, 1))

    remote_drained = all(r in (0, 3) for r in s.remote)
    if mutant == 'ack_on_parse':
        remote_drained = True
    if mutant == 'logical_remote_count':
        remote_drained = all(r != 1 for r in s.remote)
    if s.phase == 2 and s.fence_seen and remote_drained:
        yield 'server enqueues FenceAck after preceding outputs', replace(s, phase=3, outqueue=s.outqueue+(-1,))
    if s.outqueue:
        item = s.outqueue[0]
        ns = replace(s, outqueue=s.outqueue[1:])
        if item == -1:
            yield 'client parses FenceAck', replace(ns, ack=True)
        else:
            yield f'client parses response {item} and hands it to decoder/consumer', replace(ns, consumer=at(s.consumer, item, 1))
    consumers_drained = all(c in (0, 3) for c in s.consumer)
    if mutant == 'commit_on_ack_parse':
        consumers_drained = True
    if mutant == 'logical_response_count':
        consumers_drained = all(c != 1 for c in s.consumer)
    if s.phase == 3 and s.ack and consumers_drained:
        yield 'coordinator certifies client quiescence and commits new generation', replace(s, phase=4, ack=False)


def violation(s):
    if s.phase == 4:
        if any(p in (1, 2) for p in s.producer):
            return 'commit while an old producer can still execute or enqueue'
        if any(x >= 0 for x in s.queue):
            return 'commit while old-generation data remains in transport'
        if any(r in (1, 2) for r in s.remote):
            return 'commit while old remote dispatch/stream/send/dispose remains physical'
        if any(x >= 0 for x in s.outqueue) or any(c in (1, 2) for c in s.consumer):
            return 'commit while old response payload/decoder/consumer remains physical'
    return None


def check(mutant):
    initial = State()
    parents = {initial: None}
    queue = deque([initial])
    completed = 0
    while queue:
        s = queue.popleft()
        problem = violation(s)
        if problem:
            trace = []
            while parents[s] is not None:
                prev, action = parents[s]
                trace.append(action)
                s = prev
            return dict(name=mutant or 'correct_physical_fifo_fence', result='COUNTEREXAMPLE',
                        states=len(parents), violation=problem, trace=trace[::-1])
        completed += s.phase == 4
        for action, ns in successors(s, mutant):
            if ns not in parents:
                parents[ns] = (s, action)
                queue.append(ns)
    return dict(name='correct_physical_fifo_fence', result='PASS (bounded exhaustive safety only)',
                states=len(parents), completed_states=completed)


def main():
    results = [check(m) for m in ('', 'logical_producer_count', 'ack_on_parse', 'logical_remote_count', 'non_fifo_transport', 'commit_on_ack_parse', 'logical_response_count')]
    Path('fence-results.json').write_text(json.dumps(results, indent=2)+'\n')
    for result in results:
        print(json.dumps(result))
    assert results[0]['result'].startswith('PASS')
    assert all(r['result']=='COUNTEREXAMPLE' for r in results[1:])


if __name__ == '__main__':
    main()
