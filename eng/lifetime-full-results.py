"""Pure fourteen-case lifetime projection from ORIGINAL full ordinary reports.

Caller first closes the owner, seals reports, reconciles the COMPLETE native/
JUnit/discovery domain and records every original global failure. This helper
runs no commands, writes no files, imports no runner/catalog, mutates no inputs
and cannot qualify the suite. Return the complete projection atomically or keep
its rejection alongside unchanged global facts/errors. Never synthesize reports.
"""
from collections import Counter
import hashlib
import math
from pathlib import Path
import re
import xml.etree.ElementTree as ET

CODES=frozenset(('LP_SOURCE','LP_GATE','LP_SELECTION','LP_IDENTITY','LP_SHAPE',
                 'LP_LIMIT','LP_EXCEPTION','LP_RETRY','LP_JUNIT'))
SOURCE_PINS = {'eng/protocol-lifetime-history/argument-display-name-proof.json': '37147528b72191820bfc81686c74f37baf6ac2f62369d6222c58697f81edd82b',
 'src/SharpLink.Client/SharpLinkClient.ContractRoutes.cs': '1fb2ca46f003cb3c675c5f6bd1a7f694d26f3bd9409e313248c1945d9923e298',
 'src/SharpLink.Client/SharpLinkClient.cs': '0096ed43927669451702fc148fd0e933a05ec4005ee5a0c5b2bd4ff24bbf0af8',
 'src/SharpLink.Runtime/FrameworkTaskSupervisor.cs': 'f0bd27e13492e2888dc1c850eaafeabd2166568704543fe88bf4d1f42e068c51',
 'src/SharpLink.Runtime/PhysicalTaskFailureException.cs': 'dbfb8036397db61943739d98834414f1b956f42351fcaeab730f8fc907053ca3',
 'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs': 'de1e0217562144fa8d2b427e72d321aa71928d5007cd28ac37aae19a2438ab2f',
 'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs': '1ecb51b1ebc4c880a927e9c94c27522a151efbd4fe67f049ecb2903a3cb45191',
 'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs': '9e92501f78b14811cb2bbbdcbf59fcbd9e93b0a93d6d9d428d88c0bad8209bca',
 'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs': '00417460067c00b87f52882434d161480aea0401d4a79c1018e5b12d836dce8c'}

CASES = (('test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'SharpLink.UnitTests.Client',
  'InterceptedResponseLifetimeRecoveryTests',
  'DeadlineReturnsButSameResponseLeaseProtectsHeldManagerDecode',
  26,
  57),
 ('test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'SharpLink.UnitTests.Client',
  'InterceptedResponseLifetimeRecoveryTests',
  'DeadlineReturnsButSameResponseLeaseProtectsHeldManagerDecode',
  26,
  57),
 ('test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'SharpLink.UnitTests.Client',
  'InterceptedResponseLifetimeRecoveryTests',
  'LateSuccessfulInterceptorResultIsDisposedBeforeModuleRetires',
  60,
  78),
 ('test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'SharpLink.UnitTests.Client',
  'InterceptedResponseLifetimeRecoveryTests',
  'SuccessfulNonforwardingReplacementStillJoinsOriginalCoreWithoutDeadline',
  81,
  103),
 ('test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  'SharpLink.UnitTests.Runtime',
  'SharpLinkTimerCleanupOwnershipTests',
  'OverlappingCallbackFailureMustSurviveExpirySignalAndJoinEveryCallback',
  159,
  205),
 ('test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  'SharpLink.UnitTests.Runtime',
  'SharpLinkTimerCleanupOwnershipTests',
  'CallbackRearmFailureMustNotifyCloseAndJoinCallback',
  130,
  156),
 ('test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  'SharpLink.UnitTests.Runtime',
  'SharpLinkTimerCleanupOwnershipTests',
  'SourceSuccessMustAlsoJoinHeldTimerCallback',
  101,
  127),
 ('test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  'SharpLink.UnitTests.Client',
  'ContractScopedRouteFailureControls',
  'SitePrebuildFailuresAreAtomic',
  57,
  105),
 ('test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  'SharpLink.UnitTests.Client',
  'ContractScopedRouteFailureControls',
  'SitePrebuildFailuresAreAtomic',
  57,
  105),
 ('test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  'SharpLink.UnitTests.Client',
  'ContractScopedRouteFailureControls',
  'SitePrebuildFailuresAreAtomic',
  57,
  105),
 ('test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  'SharpLink.UnitTests.Client',
  'ContractScopedRouteFailureControls',
  'SitePrebuildFailuresAreAtomic',
  57,
  105),
 ('test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  'SharpLink.UnitTests.Client',
  'ContractScopedRouteFailureControls',
  'SitePrebuildFailuresAreAtomic',
  57,
  105),
 ('test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  'SharpLink.UnitTests.Client',
  'ContractScopedRouteFailureControls',
  'SitePrebuildFailuresAreAtomic',
  57,
  105),
 ('test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  'SharpLink.UnitTests.Client',
  'ContractScopedRouteFailureControls',
  'PendingBuildFailureDoesNotImposeAllBoundOnFreshPrepare',
  108,
  136))

ATTRIBUTE_LINES = (23, 23, 59, 80, 158, 129, 100, 50, 50, 50, 50, 50, 50, 107)

PARAMETER_TYPES = [['System.Boolean'],
 ['System.Boolean'],
 [],
 [],
 [],
 [],
 [],
 ['System.String'],
 ['System.String'],
 ['System.String'],
 ['System.String'],
 ['System.String'],
 ['System.String'],
 []]

RETURN_TYPES = ['System.Threading.Tasks.Task',
 'System.Threading.Tasks.Task',
 'System.Threading.Tasks.Task',
 'System.Threading.Tasks.Task',
 'System.Threading.Tasks.Task',
 'System.Threading.Tasks.Task',
 'System.Threading.Tasks.Task',
 'System.Threading.Tasks.Task',
 'System.Threading.Tasks.Task',
 'System.Threading.Tasks.Task',
 'System.Threading.Tasks.Task',
 'System.Threading.Tasks.Task',
 'System.Threading.Tasks.Task',
 'System.Threading.Tasks.Task']

GROUPS = [{'count': 2, 'first': 0, 'kind': 'response-lease', 'literalLines': [24, 25], 'literals': [False, True], 'parameter': 'duplex'},
 {'count': 1, 'first': 2, 'kind': 'response-lease', 'literalLines': [], 'literals': [], 'parameter': None},
 {'count': 1, 'first': 3, 'kind': 'response-lease', 'literalLines': [], 'literals': [], 'parameter': None},
 {'count': 1, 'first': 4, 'kind': 'timer', 'literalLines': [], 'literals': [], 'parameter': None},
 {'count': 1, 'first': 5, 'kind': 'timer', 'literalLines': [], 'literals': [], 'parameter': None},
 {'count': 1, 'first': 6, 'kind': 'timer', 'literalLines': [], 'literals': [], 'parameter': None},
 {'count': 6,
  'first': 7,
  'kind': 'prebuild',
  'literalLines': [51, 52, 53, 54, 55, 56],
  'literals': ['targets', 'buckets', 'providers', 'state', 'signal', 'state-timeout'],
  'parameter': 'stage'},
 {'count': 1, 'first': 13, 'kind': 'prebuild', 'literalLines': [], 'literals': [], 'parameter': None}]

EXCEPTION_CATEGORIES = {'SharpLink.Abstractions.SharpLinkException': 'sharplink-exception',
 'SharpLink.Client.SharpLinkClient+ContractRouteFailure': 'contract-route-failure',
 'SharpLink.Runtime.PhysicalTaskFailureException': 'physical-task-failure',
 'SharpLink.Runtime.SharpLinkProtocolViolationException': 'protocol-violation',
 'System.AggregateException': 'aggregate',
 'System.ArgumentException': 'argument',
 'System.ArgumentNullException': 'argument-null',
 'System.ArgumentOutOfRangeException': 'argument-range',
 'System.BadImageFormatException': 'bad-image',
 'System.Exception': 'exception',
 'System.IO.FileLoadException': 'file-load',
 'System.IO.FileNotFoundException': 'file-not-found',
 'System.InvalidOperationException': 'invalid-operation',
 'System.MethodAccessException': 'method-access',
 'System.MissingMethodException': 'missing-method',
 'System.NullReferenceException': 'null-reference',
 'System.OperationCanceledException': 'operation-cancelled',
 'System.Threading.Tasks.TaskCanceledException': 'task-cancelled',
 'System.TimeoutException': 'timeout',
 'System.TypeInitializationException': 'type-initialization',
 'System.TypeLoadException': 'type-load',
 'TUnit.Assertions.Exceptions.AssertionException': 'assertion'}

EXCEPTION_TAGS = (((0, 1),
  'System.InvalidOperationException',
  'the real response route is manager-bound before codec Deserialize',
  'response-route-bound',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  38),
 ((0, 1),
  'System.InvalidOperationException',
  'logical deadline remains prompt',
  'response-logical-deadline',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  41),
 ((0, 1),
  'System.InvalidOperationException',
  'actual Pending deadline publishes its own original response error',
  'response-pending-deadline',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  44),
 ((0, 1),
  'System.InvalidOperationException',
  'logical completion cannot refund the same response lease while actual Decode remains held',
  'response-held-decode-lease',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  47),
 ((0, 1),
  'System.InvalidOperationException',
  'existing intercepted CTS remains physically owned',
  'response-held-cts',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  48),
 ((0, 1),
  'System.InvalidOperationException',
  'module releases only after original decoder exits',
  'response-module-release-after-decode',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  50),
 ((0, 1),
  'System.InvalidOperationException',
  'last physical owner releases lease and original CTS once',
  'response-lease-cts-refund',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  51),
 ((0, 1),
  'System.InvalidOperationException',
  'Stop retains exactly the original unconsumed Pending deadline',
  'response-stop-original-deadline-identity',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  55),
 ((2,),
  'System.InvalidOperationException',
  'late invocation still returns logical deadline',
  'response-late-result-logical-deadline',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  70),
 ((2,),
  'System.InvalidOperationException',
  'pending successful result still owns the same module lease',
  'response-late-result-lease-held',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  71),
 ((2,),
  'System.InvalidOperationException',
  'late successful enumerable is physically acquired and disposed, not merely Task-observed and discarded',
  'response-late-result-disposal',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  74),
 ((2,),
  'System.InvalidOperationException',
  'late-result cleanup completes before refund',
  'response-late-result-refund',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  76),
 ((3,),
  'System.InvalidOperationException',
  'ordinary successful replacement cleanup joins the original sink inline, even without abandoned deadline stage',
  'response-replacement-original-core-join',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  96),
 ((3,),
  'System.InvalidOperationException',
  'replacement remains an empty successful stream',
  'response-replacement-empty-success',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  97),
 ((3,),
  'System.InvalidOperationException',
  'original codec dispatch and original route are reconciled before module release',
  'response-replacement-core-reconciled',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  99),
 ((3,),
  'System.InvalidOperationException',
  'nonforwarding result cannot strand original stream or Pending registration',
  'response-replacement-no-stranded-owners',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  101),
 ((0, 1, 2, 3),
  'System.InvalidOperationException',
  'one genuine connected Session',
  'response-one-connected-session',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  196),
 ((0, 1, 2, 3),
  'System.InvalidOperationException',
  'peer binds only this exact current fixture contract identity',
  'response-exact-bind-identity',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  201),
 ((0, 1, 2, 3),
  'System.InvalidOperationException',
  'actual Connect/handshake and exact Bind reached ready state',
  'response-ready-bind',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  209),
 ((0, 1, 2, 3),
  'System.InvalidOperationException',
  'request owns an actual full Pending ID',
  'response-live-pending-id',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  225),
 ((0, 1, 2, 3),
  'System.InvalidOperationException',
  'arbitrary enumerator Dispose must not overlap MoveNext',
  'response-dispose-not-overlap-move',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  350),
 ((0, 1, 2, 3),
  'System.InvalidOperationException',
  'Expected actual failure.',
  'response-expected-failure-missing',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  428),
 ((0, 1, 2, 3),
  'System.TimeoutException',
  'codec release',
  'response-codec-release-timeout',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  364),
 ((0, 1, 2, 3),
  'System.TimeoutException',
  'owned fixture worker did not physically exit',
  'response-owned-worker-unjoined',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  269),
 ((0, 1),
  'System.TimeoutException',
  'only the original response lease remains',
  'response-original-lease-count-timeout',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  36),
 ((6,),
  'System.Exception',
  'source success must not detach a still-running callback',
  'timer-source-success-callback-held',
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  116),
 ((6,),
  'System.Exception',
  'source success before expiry must be preserved after quiescence',
  'timer-source-success-result',
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  124),
 ((6,),
  'System.Exception',
  'successful teardown must release the timer without closing a healthy Session',
  'timer-source-success-teardown',
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  126),
 ((5,),
  'System.Exception',
  'callback failure must not skip callback quiescence',
  'timer-rearm-failure-callback-held',
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  144),
 ((5,),
  'System.Exception',
  'callback rearm failure must reach the coordinator rather than escape the timer thread',
  'timer-rearm-failure-identity',
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  153),
 ((5,),
  'System.Exception',
  'failed callback must retain one timer teardown owner',
  'timer-rearm-one-owner',
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  155),
 ((4,),
  'System.Exception',
  'the regression requires two simultaneously active timer callbacks',
  'timer-two-overlapping-callbacks',
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  184),
 ((4,),
  'System.Exception',
  'one callback exiting must not satisfy teardown while the other is still active',
  'timer-one-callback-still-held',
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  189),
 ((4,),
  'System.Exception',
  'a callback fault after successful expiry signaling must be retained exactly once',
  'timer-late-rearm-failure-identity',
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  201),
 ((4,),
  'System.Exception',
  'all callbacks and timer ownership must join without a duplicate close notification',
  'timer-all-owners-joined',
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  204),
 ((4, 5, 6),
  'System.InvalidOperationException',
  'Change failed',
  'timer-injected-change-failure',
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  258),
 ((4, 5, 6),
  'System.InvalidOperationException',
  'timer not created',
  'timer-not-created',
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  284),
 ((7, 8, 9, 10, 11, 12),
  'System.InvalidOperationException',
  'the exact existing attempt and original public Prepare waiter precede the failing Bind commit',
  'prebuild-initial-attempt',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  65),
 ((7, 8, 9, 10, 11, 12),
  'System.InvalidOperationException',
  'the fault boundary must capture its current pending notification, not an earlier completed generation',
  'prebuild-current-notification',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  78),
 ((7, 8, 9, 10, 11, 12),
  'System.InvalidOperationException',
  'failed prebuild preserves view/attempt completion but immediately publishes the exact cold failure on the existing typed '
  'signal',
  'prebuild-atomic-publication',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  89),
 ((7, 8, 9, 10, 11, 12),
  'System.InvalidOperationException',
  'original and fresh Prepare must fail promptly while original Bind/cleanup and their physical candidate permits remain owned',
  'prebuild-held-cleanup',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  94),
 ((7, 8, 9, 10, 11, 12),
  'System.InvalidOperationException',
  'the failed exact candidate and its one cleanup owner are both physically released',
  'prebuild-candidate-refund',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  100),
 ((13,),
  'System.InvalidOperationException',
  'A remains exactly eligible while B owns its pending infrastructure failure',
  'prebuild-control-eligible-route',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  128),
 ((13,),
  'System.InvalidOperationException',
  "fresh at-least-one Prepare/RPC cannot wait for B's blocked cleanup",
  'prebuild-control-no-allbound-wait',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  131),
 ((13,),
  'System.InvalidOperationException',
  'R23 other-endpoint pending construction failure',
  'prebuild-control-injected-marker',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  115),
 ((7, 8, 9, 10, 11, 12),
  'System.TimeoutException',
  'R23 original construction timeout, not a Prepare wait timeout',
  'prebuild-injected-state-timeout',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  68),
 ((7, 8, 9, 10, 11, 12, 13),
  'System.InvalidOperationException',
  'one failed target must have exactly one cleanup owner',
  'prebuild-one-cleanup-owner',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  400),
 ((7, 8, 9, 10, 11, 12, 13),
  'System.InvalidOperationException',
  "the failed candidate's exact token owns the one real ReleaseBinding frame",
  'prebuild-exact-release-token',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  417),
 ((7, 8, 9, 10, 11, 12, 13),
  'System.InvalidOperationException',
  'the original cleanup Task physically exited successfully',
  'prebuild-cleanup-task-success',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  419),
 ((7, 8, 9, 10, 11, 12, 13),
  'System.InvalidOperationException',
  'both original Bind tasks were captured before any confirmation',
  'prebuild-two-original-binds',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  425),
 ((7, 8, 9, 10, 11, 12, 13),
  'System.InvalidOperationException',
  'the injected infrastructure failure cannot be reported as semantic Bind success',
  'prebuild-no-false-bind-success',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  427),
 ((7, 8, 9, 10, 11, 12, 13),
  'System.InvalidOperationException',
  "explicit cold retry publishes only A's independently confirmed new incarnation",
  'prebuild-retry-only-new-a',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  480),
 ((7, 8, 9, 10, 11, 12, 13),
  'System.InvalidOperationException',
  'the old successful notification value retains its original construction error after current signal rotation',
  'prebuild-captured-notification-retains-marker',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  486),
 ((7, 8, 9, 10, 11, 12, 13),
  'System.InvalidOperationException',
  'The actual cold ready-check must prefer captured failure over later readiness.',
  'prebuild-old-notification-priority',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  495),
 ((7, 8, 9, 10, 11, 12, 13),
  'System.InvalidOperationException',
  'A later successful route erased the captured old notification failure.',
  'prebuild-old-notification-not-erased',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  498),
 ((7, 8, 9, 10, 11, 12, 13),
  'System.InvalidOperationException',
  'Synchronous Prepare must retain the exact construction failure.',
  'prebuild-sync-prepare-original-error',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  526),
 ((7, 8, 9, 10, 11, 12, 13),
  'System.InvalidOperationException',
  'expected infrastructure error must be one real fault, not cancellation or an unknown sibling graph',
  'prebuild-one-original-fault-graph',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  554),
 ((7, 8, 9, 10, 11, 12, 13),
  'System.InvalidOperationException',
  'terminal audit cannot silently become an unbounded join',
  'prebuild-terminal-audit-no-hidden-join',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  630),
 ((7, 8, 9, 10, 11, 12, 13),
  'System.InvalidOperationException',
  'Original task exit is unknown; fixture roots retained. Isolate/terminate this test process.',
  'prebuild-owner-outcome-unknown',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  289),
 ((7, 8, 9, 10, 11, 12, 13),
  'System.TimeoutException',
  'Both pending original BindContractDemand tasks were not registered.',
  'prebuild-original-binds-unregistered',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  180),
 ((7, 8, 9, 10, 11, 12, 13),
  'System.TimeoutException',
  'The exact cold publication predicate was not observed.',
  'prebuild-view-predicate-timeout',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  367),
 ((7, 8, 9, 10, 11, 12, 13),
  'System.TimeoutException',
  'The original candidate cleanup Task was not registered.',
  'prebuild-cleanup-owner-unregistered',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  403),
 ((7, 8, 9, 10, 11, 12, 13),
  'System.TimeoutException',
  'Original cold writer barrier was not released.',
  'prebuild-cold-writer-release-timeout',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  666),
 ((7, 8, 9, 10, 11, 12),
  'System.InvalidOperationException',
  'R23 exact targets prebuild marker',
  'prebuild-injected-stage-marker',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  69),
 ((7, 8, 9, 10, 11, 12),
  'System.InvalidOperationException',
  'R23 exact buckets prebuild marker',
  'prebuild-injected-stage-marker',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  69),
 ((7, 8, 9, 10, 11, 12),
  'System.InvalidOperationException',
  'R23 exact providers prebuild marker',
  'prebuild-injected-stage-marker',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  69),
 ((7, 8, 9, 10, 11, 12),
  'System.InvalidOperationException',
  'R23 exact state prebuild marker',
  'prebuild-injected-stage-marker',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  69),
 ((7, 8, 9, 10, 11, 12),
  'System.InvalidOperationException',
  'R23 exact signal prebuild marker',
  'prebuild-injected-stage-marker',
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  69),
 ((0, 1, 2, 3),
  'System.InvalidOperationException',
  'Missing actual diagnostic field _error',
  'response-core-error-null-or-field-absent',
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  420))

EXTRA_FRAMES = (((0, 1, 2, 3),
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'SharpLink.UnitTests.Client.InterceptedResponseLifetimeRecoveryTests',
  'Ensure',
  'Boolean condition, String message',
  423,
  423),
 ((0, 1, 2, 3),
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'SharpLink.UnitTests.Client.InterceptedResponseLifetimeRecoveryTests',
  'Failure',
  'Task task',
  424,
  429),
 ((0, 1, 2, 3),
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'SharpLink.UnitTests.Client.InterceptedResponseLifetimeRecoveryTests',
  'Until',
  'Func`1 predicate, String label',
  430,
  434),
 ((0, 1, 2, 3),
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'SharpLink.UnitTests.Client.InterceptedResponseLifetimeRecoveryTests',
  'WithFixture',
  'Interceptor interceptor, Func`2 body',
  159,
  165),
 ((0, 1, 2, 3),
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'SharpLink.UnitTests.Client.InterceptedResponseLifetimeRecoveryTests.Fixture',
  'InitializeAsync',
  '',
  183,
  210),
 ((0, 1, 2, 3),
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'SharpLink.UnitTests.Client.InterceptedResponseLifetimeRecoveryTests+Fixture',
  'InitializeAsync',
  '',
  183,
  210),
 ((0, 1, 2, 3),
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'SharpLink.UnitTests.Client.InterceptedResponseLifetimeRecoveryTests.Fixture',
  'RequestAsync',
  'Boolean duplex',
  222,
  229),
 ((0, 1, 2, 3),
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'SharpLink.UnitTests.Client.InterceptedResponseLifetimeRecoveryTests+Fixture',
  'RequestAsync',
  'Boolean duplex',
  222,
  229),
 ((0, 1, 2, 3),
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'SharpLink.UnitTests.Client.InterceptedResponseLifetimeRecoveryTests.Fixture',
  'CleanupAsync',
  'Exception primary',
  238,
  276),
 ((0, 1, 2, 3),
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'SharpLink.UnitTests.Client.InterceptedResponseLifetimeRecoveryTests+Fixture',
  'CleanupAsync',
  'Exception primary',
  238,
  276),
 ((0, 1, 2, 3),
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'SharpLink.UnitTests.Client.InterceptedResponseLifetimeRecoveryTests.HeldCodec',
  'Deserialize',
  'ReadOnlySequence`1& bytes',
  361,
  366),
 ((0, 1, 2, 3),
  'test/SharpLink.UnitTests/Client/InterceptedResponseLifetimeRecoveryTests.cs',
  'SharpLink.UnitTests.Client.InterceptedResponseLifetimeRecoveryTests+HeldCodec',
  'Deserialize',
  'ReadOnlySequence`1& bytes',
  361,
  366),
 ((4, 5, 6),
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  'SharpLink.UnitTests.Runtime.SharpLinkTimerCleanupOwnershipTests',
  'Ensure',
  'Boolean condition, String message',
  239,
  243),
 ((4, 5, 6),
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  'SharpLink.UnitTests.Runtime.SharpLinkTimerCleanupOwnershipTests',
  'CaptureFailureAsync',
  'Task task',
  233,
  237),
 ((4, 5, 6),
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  'SharpLink.UnitTests.Runtime.SharpLinkTimerCleanupOwnershipTests.TrackingTimeProvider.TrackingTimer',
  'Change',
  'TimeSpan dueTime, TimeSpan period',
  307,
  319),
 ((4, 5, 6),
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  'SharpLink.UnitTests.Runtime.SharpLinkTimerCleanupOwnershipTests+TrackingTimeProvider+TrackingTimer',
  'Change',
  'TimeSpan dueTime, TimeSpan period',
  307,
  319),
 ((4, 5, 6),
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  'SharpLink.UnitTests.Runtime.SharpLinkTimerCleanupOwnershipTests.TrackingTimeProvider.TrackingTimer',
  'Invoke',
  '',
  321,
  345),
 ((4, 5, 6),
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  'SharpLink.UnitTests.Runtime.SharpLinkTimerCleanupOwnershipTests+TrackingTimeProvider+TrackingTimer',
  'Invoke',
  '',
  321,
  345),
 ((4, 5, 6),
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  'SharpLink.UnitTests.Runtime.SharpLinkTimerCleanupOwnershipTests.TrackingTimeProvider.TrackingTimer',
  'DisposeAsync',
  '',
  355,
  369),
 ((4, 5, 6),
  'test/SharpLink.UnitTests/Runtime/SharpLinkTimerCleanupOwnershipTests.cs',
  'SharpLink.UnitTests.Runtime.SharpLinkTimerCleanupOwnershipTests+TrackingTimeProvider+TrackingTimer',
  'DisposeAsync',
  '',
  355,
  369),
 ((7, 8, 9, 10, 11, 12, 13),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureControls.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls',
  'WithFixture',
  'Func`2 body',
  138,
  145),
 ((7, 8, 9, 10, 11, 12, 13),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls',
  'Ensure',
  'Boolean condition, String message',
  687,
  690),
 ((7, 8, 9, 10, 11, 12, 13),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls.FailureFixture',
  'CleanupAsync',
  'Exception primary',
  185,
  276),
 ((7, 8, 9, 10, 11, 12, 13),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls+FailureFixture',
  'CleanupAsync',
  'Exception primary',
  185,
  276),
 ((7, 8, 9, 10, 11, 12, 13),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls.FailureFixture',
  'ThrowCleanupFailures',
  'List`1 failures',
  293,
  297),
 ((7, 8, 9, 10, 11, 12, 13),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls+FailureFixture',
  'ThrowCleanupFailures',
  'List`1 failures',
  293,
  297),
 ((7, 8, 9, 10, 11, 12, 13),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls.FailureFixture',
  'ExpectFailureAsync',
  'OwnedOperation operation, Func`2 expected',
  542,
  575),
 ((7, 8, 9, 10, 11, 12, 13),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls+FailureFixture',
  'ExpectFailureAsync',
  'OwnedOperation operation, Func`2 expected',
  542,
  575),
 ((7, 8, 9, 10, 11, 12, 13),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls.FailureFixture',
  'ObserveAsync',
  'OwnedOperation operation',
  589,
  604),
 ((7, 8, 9, 10, 11, 12, 13),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls+FailureFixture',
  'ObserveAsync',
  'OwnedOperation operation',
  589,
  604),
 ((7, 8, 9, 10, 11, 12, 13),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls.FailureFixture',
  'JoinAsync',
  'OwnedOperation operation, List`1 failures',
  606,
  626),
 ((7, 8, 9, 10, 11, 12, 13),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls+FailureFixture',
  'JoinAsync',
  'OwnedOperation operation, List`1 failures',
  606,
  626),
 ((7, 8, 9, 10, 11, 12, 13),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls.FailureFixture',
  'CaptureTerminalFailureAsync',
  'Task source',
  628,
  633),
 ((7, 8, 9, 10, 11, 12, 13),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls+FailureFixture',
  'CaptureTerminalFailureAsync',
  'Task source',
  628,
  633),
 ((7, 8, 9, 10, 11, 12, 13),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls.FailureFixture',
  'CaptureCandidateCleanupAsync',
  '',
  393,
  406),
 ((7, 8, 9, 10, 11, 12, 13),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls+FailureFixture',
  'CaptureCandidateCleanupAsync',
  '',
  393,
  406),
 ((7, 8, 9, 10, 11, 12, 13),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls.FailureFixture',
  'FinishCandidateCleanupAsync',
  'OwnedOperation cleanup, Int32 endpoint',
  408,
  420),
 ((7, 8, 9, 10, 11, 12, 13),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls+FailureFixture',
  'FinishCandidateCleanupAsync',
  'OwnedOperation cleanup, Int32 endpoint',
  408,
  420),
 ((7, 8, 9, 10, 11, 12, 13),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls.FailureFixture',
  'AssertCapturedNotificationStillFails',
  'Task`1 notification, Exception marker',
  483,
  499),
 ((7, 8, 9, 10, 11, 12, 13),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls+FailureFixture',
  'AssertCapturedNotificationStillFails',
  'Task`1 notification, Exception marker',
  483,
  499),
 ((7, 8, 9, 10, 11, 12, 13),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls.ColdBarrier',
  'HoldOnce',
  '',
  661,
  667),
 ((7, 8, 9, 10, 11, 12, 13),
  'test/SharpLink.UnitTests/Client/ContractScopedRouteFailureFixture.cs',
  'SharpLink.UnitTests.Client.ContractScopedRouteFailureControls+ColdBarrier',
  'HoldOnce',
  '',
  661,
  667),
 ((0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13),
  'src/SharpLink.Client/SharpLinkClient.cs',
  'SharpLink.Client.SharpLinkClient',
  'StopCoreAsync',
  '',
  161,
  218),
 ((0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13),
  'src/SharpLink.Client/SharpLinkClient.cs',
  'SharpLink.Client.SharpLinkClient',
  'ThrowStopCleanupFailures',
  'List`1 failures',
  255,
  262),
 ((0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13),
  'src/SharpLink.Runtime/FrameworkTaskSupervisor.cs',
  'SharpLink.Runtime.FrameworkTaskSupervisor',
  'DrainCoreAsync',
  'Task signal',
  409,
  436))

STATUS_KIND = {'cancelled': 'error', 'error': 'error', 'failed': 'failure', 'passed': 'passed', 'skipped': 'skipped', 'timedOut': 'error'}

def keys(r, obj, required, optional=()):
    r.require(type(obj) is dict and set(required) <= set(obj) <= set(required) | set(optional), 'LP_SHAPE')


def string(r, value):
    r.require(type(value) is str and len(value) <= 1024 * 1024, 'LP_SHAPE')


def number(r, value):
    r.require(type(value) in (int, float) and math.isfinite(value), 'LP_SHAPE')


def strings(r, values):
    r.require(type(values) is list and len(values) <= 1024, 'LP_LIMIT')
    for value in values: string(r, value)


def pairs(r, values):
    r.require(type(values) is list and len(values) <= 1024, 'LP_LIMIT')
    for value in values:
        keys(r, value, ('key', 'value'))
        for item in value.values(): string(r, item)


def exception_shape(r, exception, depth=0):
    r.require(depth < 16, 'LP_LIMIT')
    keys(r, exception, ('type', 'message'), ('stackTrace', 'innerException'))
    for name in ('type', 'message', 'stackTrace'):
        if name in exception: string(r, exception[name])
    if 'innerException' in exception: exception_shape(r, exception['innerException'], depth + 1)


def verify_sources(r, root):
    root = Path(root)
    for name, digest in SOURCE_PINS.items():
        # The caller's stable reader rejects symlinks and races and preserves
        # private filesystem failures. Nothing from a local path is projected.
        r.require(r.sha(root / name) == digest, 'LP_SOURCE')


def selected_test_shape(r, test):
    required = ('id','displayName','methodName','className','status','durationMs','filePath','lineNumber','endLineNumber')
    optional = ('startTime','endTime','exception','output','errorOutput','categories','customProperties',
                'sourceRelativePath','skipReason','retryAttempt','attempts','traceId','spanId','additionalTraceIds')
    keys(r,test,required,optional)
    r.require(not {'retryAttempt','attempts'} & set(test),'LP_RETRY')
    for name in set(required+optional)-{'durationMs','lineNumber','endLineNumber','exception','categories','customProperties','additionalTraceIds','retryAttempt','attempts'}:
        if name in test: string(r,test[name])
    number(r,test['durationMs']);r.require(test['durationMs']>=0,'LP_SHAPE')
    for name in ('categories','additionalTraceIds'):
        if name in test: strings(r,test[name])
    if 'customProperties' in test:pairs(r,test['customProperties'])
    status=test['status'];r.require(status in STATUS_KIND,'LP_EXCEPTION')
    if status in ('passed','skipped'):
        r.require('exception' not in test and (status=='skipped' or 'skipReason' not in test),'LP_EXCEPTION')
    else:
        r.require('exception' in test and 'skipReason' not in test,'LP_EXCEPTION')
        exception_shape(r,test['exception'])
    return test


def xml_name(value):
    # Exact pinned writer normalization, including non-BMP UTF-16 code units.
    raw=value.encode('utf-16-le',errors='surrogatepass');output=[]
    for i in range(0,len(raw),2):
        code=int.from_bytes(raw[i:i+2],'little')
        output.append(chr(code) if code in (9,10,13) or 0x20<=code<=0xD7FF or 0xE000<=code<=0xFFFD else '[0x'+format(code,'X')+']')
    return ''.join(output)


def full_domains(r,native,junit,ordinary,facts):
    # Facts must be the terminal return from COMPLETE global reconciliation,
    # not partially populated facts left behind when its classifier raised.
    r.require(type(facts) is dict and facts.get('ownerClosed') is True and facts.get('executionClosureObserved') is True and
              type(facts.get('observed')) is int and facts['observed']==len(ordinary) and
              type(facts.get('expected')) is int and facts['expected']==len(ordinary) and facts.get('missingCount')==0,'LP_GATE')
    r.require(type(native) is dict and type(native.get('schemaVersion')) is int and native['schemaVersion']==1 and
              native.get('assemblyName')=='SharpLink.UnitTests' and native.get('tunitVersion')=='1.73.19.0' and
              native.get('runtimeVersion')=='.NET 10.0.14','LP_IDENTITY')
    r.require(facts.get('nativeReportSHA256')==r.digest(native) and facts.get('resultUIDSetSHA256')==r.digest(sorted(ordinary)) and
              facts.get('statusCounts')==native.get('summary') and type(facts.get('retriesObserved')) is bool and
              type(facts.get('allOrdinaryPassed')) is bool,'LP_GATE')
    groups=native.get('groups');r.require(type(groups) is list and 0<len(groups)<=100000,'LP_SHAPE')
    by_uid={};keys_expected=Counter();group_ids=set()
    for group in groups:
        r.require(type(group) is dict and type(group.get('namespace')) is str and type(group.get('className')) is str,'LP_SHAPE')
        identity=(group['namespace'],group['className']);r.require(identity not in group_ids,'LP_IDENTITY');group_ids.add(identity)
        tests=group.get('tests');r.require(type(tests) is list and 0<len(tests)<=len(ordinary),'LP_SHAPE')
        for test in tests:
            r.require(type(test) is dict and type(test.get('id')) is str,'LP_SHAPE');uid=test['id']
            r.require(uid in ordinary and uid not in by_uid,'LP_IDENTITY')
            row=ordinary[uid];meta=row['type'];loc=row['location'];status=test.get('status')
            r.require(type(status) is str and status in STATUS_KIND,'LP_IDENTITY')
            r.require(test.get('displayName')==row['displayName'] and test.get('methodName')==meta['methodName'] and
                      test.get('className')==group['className']==meta['typeName'] and group['namespace']==meta['namespace'] and
                      test.get('filePath')==loc['file'] and type(test.get('lineNumber')) is int and test['lineNumber']==loc['lineStart'] and
                      type(test.get('endLineNumber')) is int and test['endLineNumber']==loc['lineEnd'],'LP_IDENTITY')
            by_uid[uid]=test;keys_expected[(xml_name(row['displayName']),xml_name(meta['typeName']),STATUS_KIND[status])]+=1
    r.require(set(by_uid)==set(ordinary),'LP_IDENTITY')
    r.require(type(junit) is str and len(junit.encode())<=16*1024*1024 and '<!DOCTYPE' not in junit and '<!ENTITY' not in junit,'LP_JUNIT')
    try:xml=ET.fromstring(junit)
    except ET.ParseError as error:raise r.Rejected('LP_JUNIT') from error
    r.require(xml.tag=='testsuites' and len(xml)==1 and xml[0].tag=='testsuite','LP_JUNIT')
    suite=xml[0];r.require(all(x.tag in ('properties','testcase') for x in suite) and len(suite.findall('properties'))<=1,'LP_JUNIT')
    by_key={};counts=Counter()
    for case in suite.findall('testcase'):
        r.require(all(x.tag in ('failure','error','skipped') for x in case) and len(case)<=1,'LP_JUNIT')
        kind=case[0].tag if len(case) else 'passed';key=(case.get('name'),case.get('classname'),kind)
        by_key.setdefault(key,[]).append(case);counts[key]+=1
    r.require(counts==keys_expected,'LP_JUNIT')
    totals={'name':'SharpLink.UnitTests','tests':str(len(ordinary)),
            'failures':str(sum(v for k,v in counts.items() if k[2]=='failure')),
            'errors':str(sum(v for k,v in counts.items() if k[2]=='error')),
            'skipped':str(sum(v for k,v in counts.items() if k[2]=='skipped'))}
    r.require(all(all(node.get(k)==v for k,v in totals.items()) for node in (xml,suite)),'LP_JUNIT')
    return by_uid,by_key


def group_for(index):
    return next(group for group in GROUPS if group['first'] <= index < group['first'] + group['count'])


def expected_displays(group):
    method=CASES[group['first']][3]
    return {method+'('+('True' if value is True else 'False' if value is False else value)+')':(value,line)
            for value,line in zip(group['literals'],group['literalLines'])}


def argument_proofs(selections):
    proof={}
    for group in GROUPS:
        if not group['literals']:continue
        rows=selections[group['first']:group['first']+group['count']]
        names=[row['displayName'] for _,row in rows]
        proof[group['first']]=len(names)==group['count'] and set(names)==set(expected_displays(group))
    return proof


def argument_identity(index,selection,proofs=None):
    uid,row=selection;group=group_for(index);parameter=group['parameter']
    result={'caseID':hashlib.sha256(uid.encode()).hexdigest(),'kind':'opaque-parameter-case' if parameter else 'parameterless',
            'parameter':parameter,'value':None,'limitation':'source-literal-set-not-proven' if parameter else 'none'}
    if parameter and (proofs or {}).get(group['first']) is True:
        known=expected_displays(group)
        if row['displayName'] in known:
            value,line=known[row['displayName']]
            result.update(kind='source-literal-boolean' if type(value) is bool else 'source-literal-string',value=value,limitation='none',
                          literalSource={'source':CASES[index][0],'line':line},proof='pinned-default-display-name-exact-complete-set')
    return result


def source_frames(r,index,stack):
    source,namespace,cls,method,start,end=CASES[index];group=group_for(index)
    signature='Boolean duplex' if group['parameter']=='duplex' else 'String stage' if group['parameter']=='stage' else ''
    frame_map={}
    for line in range(start,end+1):frame_map[f'   at {namespace}.{cls}.{method}({signature}) in /_/{source}:line {line}']=(source,method,line)
    for indexes,path,typ,member,signature,first,last in EXTRA_FRAMES:
        if index in indexes:
            for line in range(first,last+1):frame_map[f'   at {typ}.{member}({signature}) in /_/{path}:line {line}']=(path,member,line)
    # Async bodies inside these unchanged source lambdas have compiler names.
    # Accept only anchored known class+method, bounded compiler ordinals, exact
    # source path, exact parameter signature and source-body bounds. Never emit
    # generated names or captured ordinals; unknown formatting remains unknown.
    patterns=[]
    if group['kind'] in ('response-lease','prebuild'):
        param='Fixture fixture' if group['kind']=='response-lease' else 'FailureFixture fixture'
        prefix=re.escape(namespace+'.'+cls)+r'\.(?:<>c__DisplayClass[0-9]{1,3}_[0-9]{1,3}|<>c)\.'
        method_pattern=re.escape(method)
        patterns.append(re.compile(r'^   at '+prefix+r'<'+method_pattern+r'>b__[0-9]{1,3}(?:_[0-9]{1,3})?\('+re.escape(param)+r'\) in /_/'+re.escape(source)+r':line ([0-9]{1,4})$'))
        patterns.append(re.compile(r'^   at '+prefix+r'<<'+method_pattern+r'>b__[0-9]{1,3}(?:_[0-9]{1,3})?>d\.MoveNext\(\) in /_/'+re.escape(source)+r':line ([0-9]{1,4})$'))
    lines=stack.splitlines()
    r.require(len(lines)<=4096,'LP_LIMIT')
    output=[];known=0
    for raw in lines:
        value=frame_map.get(raw)
        if value is None:
            for pattern in patterns:
                match=pattern.fullmatch(raw)
                if match and start<=int(match.group(1))<=end:
                    value=(source,method+'-body-lambda',int(match.group(1)));break
        if value is not None:
            known+=1
            if len(output)<64:output.append(dict(zip(('source','member','line'),value)))
    return output,len(lines)-known,max(0,known-64)


def projected_exception(r, index, exception):
    exception_shape(r, exception)
    result = []; current = exception
    while current is not None:
        frames, unknown, omitted = source_frames(r,index, current.get('stackTrace', ''))
        row = {'depth': len(result), 'category': EXCEPTION_CATEGORIES.get(current['type'], 'unknown'),
               'assertionTag': 'unknown', 'sourceFrames': frames, 'unrecognizedStackLineCount': unknown, 'omittedRecognizedStackLineCount': omitted}
        for indexes, typ, message, tag, path, line in EXCEPTION_TAGS:
            if index in indexes and (current['type'], current['message']) == (typ, message):
                row.update(assertionTag=tag, exactMessageSource={'source': path, 'line': line})
        result.append(row); current = current.get('innerException')
    return result


def select_fourteen(r, ordinary, explicit):
    r.require(type(ordinary) is dict and 14 <= len(ordinary) <= 100000, 'LP_SELECTION')
    r.require(type(explicit) is set and not (set(ordinary) & explicit), 'LP_SELECTION')
    output = []
    for group in GROUPS:
        index=group['first']
        source, namespace, cls, method, start, end = CASES[index]
        matches = []
        for uid, row in ordinary.items():
            r.require(type(row) is dict and type(row.get('type')) is dict, 'LP_SHAPE')
            meta = row['type']
            if (meta.get('namespace'),meta.get('typeName'),meta.get('methodName')) != (namespace,cls,method): continue
            keys(r,meta,('assemblyFullName','namespace','typeName','methodName','methodArity','returnTypeFullName','parameterTypeFullNames'))
            r.require(meta['assemblyFullName'] == 'SharpLink.UnitTests, Version=3.0.0.0, Culture=neutral, PublicKeyToken=null' and
                      type(meta['methodArity']) is int and meta['methodArity'] == 0 and
                      meta['returnTypeFullName'] == RETURN_TYPES[index] and
                      meta['parameterTypeFullNames'] == PARAMETER_TYPES[index], 'LP_SELECTION')
            r.require(type(uid) is str and 0 < len(uid) <= 8192 and not any(ord(c)<32 for c in uid) and row.get('uid') == uid, 'LP_SELECTION')
            string(r,row.get('displayName'))
            loc=row.get('location');keys(r,loc,('file','lineStart','lineEnd'))
            r.require(loc['file']=='/_/'+source and type(loc['lineStart']) is int and loc['lineStart']==ATTRIBUTE_LINES[index] and
                      type(loc['lineEnd']) is int and loc['lineEnd']==end,'LP_SELECTION')
            matches.append((uid,row))
        r.require(len(matches)==group['count'],'LP_SELECTION')
        output.extend(sorted(matches,key=lambda pair:hashlib.sha256(pair[0].encode()).hexdigest()))
    r.require(len(output)==14 and len({uid for uid,_ in output})==14,'LP_SELECTION')
    return output


def project(r,native,junit,ordinary,explicit,facts,source_root):
    """Return safe fourteen-case evidence; never write or mutate global facts.

    r is the caller's already hash-verified census module (require, Rejected,
    sha and digest). Run only after the caller's global classify returns.
    Parent source/report seals and owner receipts remain authoritative.
    """
    verify_sources(r,source_root)
    selections=select_fourteen(r,ordinary,explicit)
    by_uid,by_key=full_domains(r,native,junit,ordinary,facts)
    proven=argument_proofs(selections);output=[]
    for index,(uid,row) in enumerate(selections):
        test=selected_test_shape(r,by_uid[uid]);status=test['status']
        key=(xml_name(row['displayName']),xml_name(row['type']['typeName']),STATUS_KIND[status])
        matches=by_key[key];r.require(len(matches)==1 and sum(len(v) for k,v in by_key.items() if k[:2]==key[:2])==1,'LP_JUNIT');case=matches[0]
        r.require(set(case.attrib)=={'name','classname','time'},'LP_JUNIT')
        if 'exception' in test:
            child=case[0];r.require(set(child.attrib)=={'message','type'} and len(child)==0,'LP_JUNIT')
            r.require(child.get('message')==xml_name(test['exception']['message']) and child.get('type')==test['exception']['type'],'LP_JUNIT')
        source,namespace,cls,method,_,_=CASES[index]
        output.append({'case':index+1,'source':source,'method':namespace+'.'+cls+'.'+method,
            'argumentIdentity':argument_identity(index,(uid,row),proven),'status':status,'selectedPassed':status=='passed',
            'exceptions':projected_exception(r,index,test['exception']) if 'exception' in test else [],
            'exceptionEvidenceScope':'serialized-innerException-chain-only','aggregateSiblingCausesObserved':False,
            'caseGroup':group_for(index)['kind'],'privateRouteFailurePayloadObserved':False,
            'bodyVersusCleanup':'inconclusive' if status!='passed' else 'not-applicable'})
    return {'schemaVersion':1,'scope':'fourteen-existing-lifetime-cases-within-full-ordinary-run','complete':True,
            'selectedCaseCount':14,'allSelectedPassed':all(x['selectedPassed'] for x in output),
            'doesNotQualifyFullSuite':True,'historicalFullRunArgumentMappingAvailable':False,
            'nativeFullReportCanonicalSHA256':facts['nativeReportSHA256'],
            'junitFullReportDecodedTextSHA256':hashlib.sha256(junit.encode()).hexdigest(),
            'fullResultUIDSetSHA256':facts['resultUIDSetSHA256'],'sourcePinsSHA256':r.digest(SOURCE_PINS),
            'parameterSetsProven':{'duplex':proven[0],'stage':proven[7]},'cases':output}


