#!/usr/bin/env python3
"""Bounded explicit-state safety checks for a proposed cold-control protocol.

This is NOT a model of current SharpLink and NOT a liveness proof. Physical
ownership is an abstract oracle, deliberately separated from logical completion.
All protocol state is cold operation state; `work` is observational test state.
"""
from __future__ import annotations

from collections import deque
from dataclasses import dataclass, replace
import json
from pathlib import Path
import time

N = 2
ALL = (1 << N) - 1
IDLE, PREPARE, FENCE, COMMIT, RESUME, DONE, FAILED = range(7)
PHASES = ('idle', 'prepare', 'fence', 'commit', 'resume', 'done', 'failed')
# Messages are (kind, operation, digest, participant, incarnation).
P, PA, F, FA, C, CA, R, RA = range(8)
KINDS = ('Prepare', 'Prepared', 'Fence', 'Fenced', 'Commit', 'Committed', 'Resume', 'Resumed')
# Participant stages: untouched, prepared, gate closed, fenced, committed, resumed.


def set_at(values, i, value):
    return values[:i] + (value,) + values[i + 1:]


def bit_for(op, participant):
    return 1 << ((op - 1) * N + participant)


@dataclass(frozen=True, slots=True)
class State:
    phase: int = IDLE
    op: int = 0
    writer: tuple = ()
    frozen: bool = False
    cohort: tuple = (0, 0)
    incarnation: tuple = (0, 0)
    connected: tuple = (True, True)
    stage: tuple = (0, 0)
    local_op: tuple = (0, 0)
    local_digest: tuple = (0, 0)
    generation: tuple = (0, 0)
    gate: tuple = (0, 0)  # -1 is closed; otherwise currently admitted generation.
    retired: tuple = (0, 0)  # Permanent bit set per old generation at gate close.
    work: tuple = (2, 2)  # 2: logically + physically active; 1: abandoned physical; 0: none.
    prepared: int = 0
    fenced: int = 0
    committed: int = 0
    resumed: int = 0
    actual_commits: int = 0
    bad_commits: int = 0
    actual_resumes: int = 0
    fence_with_work: bool = False
    stale_resurrection: bool = False
    cancelled: bool = False
    pending: tuple = ()
    history: tuple = ()
    newcomer: int = -1  # -1 absent, else candidate generation.
    newcomer_ready: bool = False


@dataclass(frozen=True, slots=True)
class Config:
    name: str
    operations: int = 1
    replay: bool = False
    new_connections: bool = False
    failures: bool = True
    readmissions: bool = True
    wrong_candidate: bool = False
    mutant: str = ''


def add_message(s, message):
    return replace(s, pending=tuple(sorted(set(s.pending) | {message})),
                   history=tuple(sorted(set(s.history) | {message})))


def send_all(s, kind):
    for i in range(N):
        s = add_message(s, (kind, s.op, s.op * 101, i, s.cohort[i]))
    return s


def describe(m):
    k, op, digest, i, inc = m
    return f'{KINDS[k]}(op={op},digest={digest},p={i},inc={inc})'


def transitions(s: State, cfg: Config):
    active = PREPARE <= s.phase <= RESUME
    if s.phase in (IDLE, DONE) and s.op < cfg.operations:
        op = s.op + 1
        ns = replace(s, phase=PREPARE, op=op, writer=(op,), frozen=True,
                     cohort=s.incarnation, stage=(0, 0), local_op=(0, 0), local_digest=(0, 0),
                     prepared=0, fenced=0, committed=0, resumed=0,
                     cancelled=False)
        yield f'begin op {op}: freeze cohort and acquire sole writer', send_all(ns, P)

    if active and cfg.mutant == 'overlap' and len(s.writer) == 1:
        yield 'begin another update without exclusive writer ownership', replace(s, writer=s.writer + (s.op + 1,))

    if active and not s.cancelled:
        ns = replace(s, cancelled=True)
        if cfg.mutant == 'cancel_reopens_old':
            ns = replace(ns, gate=tuple(g if g >= 0 else s.op - 1 for g in ns.gate))
        yield 'caller cancels its wait (not the protocol)', ns

    for i in range(N):
        if s.work[i] == 2:
            yield f'p{i}: logical call completes/abandons while physical producer remains', replace(s, work=set_at(s.work, i, 1))
        if s.work[i] != 0:
            yield f'p{i}: physical codec/producer/queued-send/remote ownership drains', replace(s, work=set_at(s.work, i, 0))
        if cfg.readmissions and s.gate[i] >= 0 and s.connected[i] and s.work[i] == 0:
            yield f'p{i}: ordinary admission through existing generation {s.gate[i]} gate', replace(s, work=set_at(s.work, i, 2))

        if s.stage[i] == 2 and s.connected[i] and s.local_op[i] == s.op:
            drained = s.work[i] == 0
            if cfg.mutant == 'logical_fence':
                drained = s.work[i] <= 1
            if drained:
                ns = replace(s, stage=set_at(s.stage, i, 3),
                             fence_with_work=s.fence_with_work or s.work[i] != 0)
                yield f'p{i}: issue quiescence certificate', add_message(ns, (FA, s.op, s.op * 101, i, s.incarnation[i]))

        if active and cfg.failures and s.connected[i]:
            # Disconnection invalidates the local incarnation. It does not undo a
            # committed remote participant or retract already-sent messages.
            ns = replace(s, phase=FAILED, frozen=True, writer=(),
                         connected=set_at(s.connected, i, False),
                         incarnation=set_at(s.incarnation, i, s.incarnation[i] + 1),
                         gate=set_at(s.gate, i, -1))
            yield f'p{i}: disconnect; invalidate incarnation; fail closed', ns

        if not s.connected[i] and s.phase == FAILED:
            yield f'p{i}: reconnect with fresh incarnation, quarantined', replace(s, connected=set_at(s.connected, i, True))

    if s.phase == PREPARE and s.prepared == ALL:
        yield 'all fixed participants prepared; send fences', send_all(replace(s, phase=FENCE), F)
    if s.phase == FENCE and s.fenced == ALL:
        yield 'all fixed participants fenced; send commits', send_all(replace(s, phase=COMMIT), C)
    if s.phase == COMMIT and (s.committed == ALL or (cfg.mutant == 'early_resume' and s.committed)):
        yield 'authorize resume from received commit acknowledgements', send_all(replace(s, phase=RESUME), R)
    if s.phase == RESUME and s.resumed == ALL:
        yield 'all participants resumed; unfreeze connection publication', replace(s, phase=DONE, frozen=False, writer=())

    if cfg.new_connections and s.phase != IDLE and s.newcomer < 0:
        # Handshake began against the prior generation while topology is frozen.
        yield 'new pooled connection arrives with prior generation', replace(s, newcomer=s.op - 1)
    if cfg.new_connections and s.newcomer >= 0 and not s.newcomer_ready:
        if s.phase == DONE and s.newcomer != s.op:
            yield 'quarantined connection binds committed generation', replace(s, newcomer=s.op)
        if (s.phase == DONE and s.newcomer == s.op and not s.frozen) or cfg.mutant == 'publish_during_freeze':
            yield 'publish new connection into caller-visible pool', replace(s, newcomer_ready=True)

    # Every emitted message can be delayed independently. Replays have the exact
    # original identity, not fabricated acknowledgements from untrusted peers.
    messages = set(s.pending)
    if cfg.replay:
        messages.update(m for m in s.history if m[1] < s.op or m[4] != s.incarnation[m[3]])
    for m in sorted(messages):
        kind, op, digest, i, inc = m
        ns = replace(s, pending=tuple(x for x in s.pending if x != m))
        label = ('deliver ' if m in s.pending else 'replay ') + describe(m)
        digest_matches = digest == s.op * 101 or cfg.mutant == 'ignore_digest'
        strict_identity = op == s.op and digest_matches and inc == s.cohort[i]
        receiver_identity = op == s.op and digest_matches and inc == s.incarnation[i]
        is_ack = kind % 2 == 1
        if is_ack:
            accepted_identity = strict_identity or cfg.mutant == 'ignore_ack_identity'
            expected_phase = {PA: PREPARE, FA: FENCE, CA: COMMIT, RA: RESUME}[kind]
            if s.phase == expected_phase and accepted_identity:
                field = {PA: 'prepared', FA: 'fenced', CA: 'committed', RA: 'resumed'}[kind]
                ns = replace(ns, **{field: getattr(ns, field) | (1 << i)})
        elif s.connected[i]:
            accepted_identity = receiver_identity or cfg.mutant == 'ignore_receiver_identity'
            if accepted_identity:
                stale = not receiver_identity
                if kind == P and s.stage[i] == 0:
                    if cfg.wrong_candidate:
                        if cfg.mutant == 'ignore_digest':
                            wrong = replace(ns, stage=set_at(ns.stage, i, 1), local_op=set_at(ns.local_op, i, op),
                                            local_digest=set_at(ns.local_digest, i, digest + 1))
                            yield label + ' but local candidate has mismatched digest', add_message(wrong, (PA, op, digest + 1, i, inc))
                        elif ns != s:
                            yield label + ' rejects local candidate with mismatched digest', ns
                    ns = replace(ns, stage=set_at(ns.stage, i, 1), local_op=set_at(ns.local_op, i, op),
                                 local_digest=set_at(ns.local_digest, i, digest),
                                 stale_resurrection=ns.stale_resurrection or stale)
                    ns = add_message(ns, (PA, op, digest, i, inc))
                elif kind == F and s.stage[i] == 1 and (s.local_op[i] == op or cfg.mutant == 'ignore_receiver_identity'):
                    ns = replace(ns, stage=set_at(ns.stage, i, 2), gate=set_at(ns.gate, i, -1),
                                 retired=set_at(ns.retired, i, ns.retired[i] | (1 << ns.generation[i])),
                                 stale_resurrection=ns.stale_resurrection or stale)
                elif kind == C and s.stage[i] == 3 and (s.local_op[i] == op or cfg.mutant == 'ignore_receiver_identity') and (s.local_digest[i] == digest or cfg.mutant == 'ignore_digest'):
                    ns = replace(ns, stage=set_at(ns.stage, i, 4), generation=set_at(ns.generation, i, op),
                                 actual_commits=ns.actual_commits | bit_for(op, i),
                                 bad_commits=ns.bad_commits | (bit_for(op, i) if s.local_digest[i] != op * 101 else 0),
                                 stale_resurrection=ns.stale_resurrection or stale)
                    ns = add_message(ns, (CA, op, s.local_digest[i], i, inc))
                elif kind == R and s.stage[i] == 4 and (s.local_op[i] == op or cfg.mutant == 'ignore_receiver_identity'):
                    ns = replace(ns, stage=set_at(ns.stage, i, 5), gate=set_at(ns.gate, i, op),
                                 actual_resumes=ns.actual_resumes | bit_for(op, i),
                                 stale_resurrection=ns.stale_resurrection or stale)
                    ns = add_message(ns, (RA, op, digest, i, inc))
        if ns != s:
            yield label, ns


def violation(s: State):
    if len(s.writer) > 1:
        return 'overlapping update owns the same registry/cohort'
    if s.frozen and s.newcomer_ready:
        return 'unfenced new connection published outside frozen cohort'
    if s.fence_with_work:
        return 'quiescence certificate issued while physical ownership remains'
    if s.stale_resurrection:
        return 'stale operation/incarnation mutates a participant'
    for i in range(N):
        if s.gate[i] >= 0 and s.retired[i] & (1 << s.gate[i]):
            return 'permanently retired generation gate reopened'
    for op in range(1, s.op + 1):
        mask = ALL << ((op - 1) * N)
        if s.actual_resumes & mask and s.actual_commits & mask != mask:
            return f'participant resumes generation {op} before all fixed participants actually committed it'
        if s.actual_resumes & mask and s.bad_commits & mask:
            return f'participant resumes generation {op} with divergent committed candidate digests'
    return None


def brief(s):
    return dict(phase=PHASES[s.phase], op=s.op, frozen=s.frozen, stage=s.stage,
                gen=s.generation, digest=s.local_digest, gate=s.gate, work=s.work, incarnation=s.incarnation,
                acks=(s.prepared, s.fenced, s.committed, s.resumed),
                pending=[describe(m) for m in s.pending])


def check(cfg):
    started = time.monotonic()
    initial = State()
    parents = {initial: None}
    queue = deque([initial])
    count = 0
    done = failed = 0
    while queue:
        s = queue.popleft()
        why = violation(s)
        if why:
            trace = []
            cur = s
            while parents[cur] is not None:
                prev, label = parents[cur]
                trace.append(dict(action=label, state=brief(cur)))
                cur = prev
            trace.reverse()
            return dict(name=cfg.name, result='COUNTEREXAMPLE', violation=why,
                        visited=len(parents), expanded=count, elapsed_s=round(time.monotonic()-started, 3), trace=trace)
        count += 1
        done += s.phase == DONE and s.op == cfg.operations
        failed += s.phase == FAILED
        for label, ns in transitions(s, cfg):
            if ns not in parents:
                parents[ns] = (s, label)
                queue.append(ns)
    return dict(name=cfg.name, result='PASS (bounded exhaustive safety only)',
                visited=len(parents), expanded=count, done_states=done, failed_closed_states=failed,
                elapsed_s=round(time.monotonic()-started, 3))


def main():
    scenarios = [
        Config('correct_one_update_with_topology', new_connections=True),
        Config('correct_two_updates_with_replay', operations=2, replay=True,
               failures=False, readmissions=False),
        Config('correct_disconnect_reconnect_replay', replay=True, readmissions=False),
        Config('correct_reject_wrong_candidate_digest', wrong_candidate=True, failures=False, readmissions=False),
        Config('mutant_resume_after_one_commit', mutant='early_resume', failures=False, readmissions=False),
        Config('mutant_publish_during_freeze', mutant='publish_during_freeze', new_connections=True, failures=False, readmissions=False),
        Config('mutant_cancel_reopens_retired_gate', mutant='cancel_reopens_old', failures=False, readmissions=False),
        Config('mutant_logical_count_is_fence', mutant='logical_fence', failures=False, readmissions=False),
        Config('mutant_stale_ack_identity', mutant='ignore_ack_identity', operations=2, replay=True, failures=False, readmissions=False),
        Config('mutant_receiver_incarnation', mutant='ignore_receiver_identity', replay=True, readmissions=False),
        Config('mutant_digest_validation', mutant='ignore_digest', wrong_candidate=True, failures=False, readmissions=False),
        Config('mutant_overlap', mutant='overlap', failures=False, readmissions=False),
    ]
    results = []
    for cfg in scenarios:
        result = check(cfg)
        results.append(result)
        print(json.dumps({k: v for k, v in result.items() if k != 'trace'}), flush=True)
        Path('results.json').write_text(json.dumps(results, indent=2) + '\n')
    assert all(r['result'].startswith('PASS') for r in results[:4])
    assert all(r['result'] == 'COUNTEREXAMPLE' for r in results[4:])


if __name__ == '__main__':
    main()
