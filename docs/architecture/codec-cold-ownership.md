# Cold codec ownership

This source slice separates three boundaries:

* Logical unpublication blocks new global selection. Existing owner-local resolution remains
  available while the exact physical module and dependencies remain owned.
* Final physical retirement closes the owner's scalar identity, invalidates dependent resolution
  caches, joins admitted cold factory callbacks, then independently disposes adapter scopes.
* Immutable metadata references may outlive physical retirement. They do not keep adapter scopes
  active and are never counted in the callback drain that retirement waits for.

Warm global hits read only the cache, publication scalar, and binding-owner scalar. Warm owner
hits read the owner scalar, cache and dependency-origin scalar. No warm path loads a publication
map or acquires a management admission, lock, counter, quota reservation, or operation lease.
A codec already read just before invalidation still requires the caller's actual-use ownership.

Cold misses acquire exact owner and transitive bound-dependency users. Each acquisition is fresh;
flowed ExecutionContext markers confer no ownership. Expired markers do not block new admission.
All held ancestors are checked before synchronous retirement, preventing A→B→Dispose(A) self-wait.
The full dependency closure is held through the factory callback, including use of a dependency
codec returned by an earlier nested lookup. Recursive acquisition uses a per-operation visited
set, not a shared hot-path counter. Factory outcomes are installed only after closure and current
publication checks under the cache's existing cold gate.

Dependencies bind once to exact Type, owner generation and hash during private preparation. A
later publication of the same Type never redirects an old owner's graph. A dependency's final
retirement closes dependent resolution identities and evicts whole dependent caches, because an
owner codec can embed a codec returned by a nested dependency lookup. This does not physically
retire dependent adapter scopes or prove application callback/serialization ordering.

## Scoped exports and publication

Captured codec snapshots are internal lexical, nonconcurrent operation views. No map, enumerator,
registration or immutable-payload reference may escape the using scope. Access must not overlap
Dispose on the same view. Source view disposal clears its roots before dropping metadata references
and accounting. It has no user callback, physical-retirement join, or fresh-quota requirement.
Client/Server publication callers capture outside their outer registry gates and retain the exact
view through prepare, commit or rollback. Preparation copies inputs before commit. Runtime/provider
version changes cause safe rejection; unrelated-publication rebasing remains a separate gate.
Commit transfers old maps/cache roots into the caller-owned publication token. Clearing and refunds
happen when that token is disposed outside outer registry gates. Stale candidates clear their own
maps and metadata references and refund only their own storage.

## Costs and limits

All figures below are logical cold accounting units, not measured CLR sizes. Existing policy uses
256-unit collection/control headers and 128-unit slots, with exact registration scopes where
applicable. Each owner pre-owns independent storage, cleanup-frame budget, observer/copy headers,
and two completion promises before any retirement. Cleanup never asks a saturated authority or a
disposed Runtime root for fresh admission. Active cold frames carry a charged header. Nonempty
transitive acquisitions additionally own visited/work/lease-list and lease-array containers;
slots grow before insertion. None of these costs applies to a warm GetCodec hit.

Immutable maps, temporary copies, metadata reference arrays, provider caches, reverse observers
and source views have separate charges. Observer entries include retirement-copy capacity before
publication. Cache slots are charged to their exact owner scope and removed before refund. Factory
result cache entries retain scalar identities, not registration objects. Retired owners clear
adapter/prepared-codec/provider/dependency roots; retained immutable metadata is released only by
its final standing owner. Raw cleanup errors may remain in the retirement completion outcome for
concurrent waiters; this is failure ownership, not a claimed universal ALC unloading result.

Manual codec/provider references are borrowed, not module leases. Runtime-alive alone cannot
protect an independently retired module or dependency. Callers must coordinate exact registration
and dependency physical lifetime; concurrent retirement during borrowed use is unsupported.
Framework intercepted response physical-disposal lease transfer and dependency adapter-disposal
ordering remain separate actual-use gates. This source does not add per-RPC ambient management,
claim broad ALC reclamation, or assert throughput/allocation performance. Fresh focused controls
require independent source review and new execution evidence; no historical result transfers.
