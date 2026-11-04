"""Fixed finite repeat policy. Exhaustion is failure, never partial success."""
from dataclasses import dataclass
import math
import time
import results as r

ROUNDS=100
BODY_SECONDS=150
CLEANUP_SECONDS=16
FINAL_RESERVE_SECONDS=60
TOTAL_SECONDS=3600
JOB_SECONDS=4500
SETUP=(("git-head",10),("git-tree",10),("sdk-version",30),
       ("restore",600),("build",600),("discovery",120))

@dataclass(frozen=True)
class Case:
    round: int
    mode: str
    @property
    def label(self): return f"r{self.round:03d}-{self.mode}"

def cases():
    return tuple(Case(n,m) for n in range(1,ROUNDS+1) for m in r.MODES)

def require_budget(started, seconds, clock=time.monotonic):
    now=clock()
    r.require(math.isfinite(now) and math.isfinite(started) and now>=started and
              now-started+seconds+FINAL_RESERVE_SECONDS<TOTAL_SECONDS,
              "Insufficient owned command budget")

def execute(started, operation, clock=time.monotonic, before_case=lambda case:None):
    completed=[]
    for case in cases():
        before_case(case)
        # Never begin a round without room for its worst-case bodies and cleanup.
        if case.mode==r.MODES[0]:
            require_budget(started,len(r.MODES)*(BODY_SECONDS+CLEANUP_SECONDS),clock)
        require_budget(started,BODY_SECONDS+CLEANUP_SECONDS,clock)
        operation(case)  # exceptions propagate; no next case, no retries
        completed.append(case)
    require_complete(completed)
    return completed

def require_complete(completed):
    r.require(tuple(completed)==cases(),"Missing selected cases")


def command_reserve(label,seconds):
    # First body rechecks whole-round room after expensive immutable-evidence guards.
    if label in {c.label for c in cases() if c.mode==r.MODES[0]}:
        return len(r.MODES)*(BODY_SECONDS+CLEANUP_SECONDS)
    return seconds+CLEANUP_SECONDS
