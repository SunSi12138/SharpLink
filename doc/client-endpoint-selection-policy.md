# Client endpoint-selection policy

Issue #591 makes multi-endpoint load balancing a runtime-replaceable next-attempt policy without turning endpoint selection into a plugin registry or coupling it to topology mutation.

## Scope

The policy applies only to multi-endpoint static clusters and dynamic-resolver clusters. Fixed and single-endpoint Clients retain their specialized path because there is no meaningful endpoint-selection choice.

Supported built-in strategies remain:

- `PowerOfTwoChoices`
- `Random`
- `RoundRobin`
- `LeastPending`

The existing synchronous `ISharpLinkEndpointSelector` remains the application extension point. Runtime publication can move in either direction between built-in and custom policies, including replacing one custom selector instance with another. Runtime registration of new `SharpLinkLoadBalancingStrategy` enum values is deliberately not part of this model.

## Configuration surface

Builder configuration still establishes the initial policy:

```csharp
builder.UseLoadBalancing(SharpLinkLoadBalancingStrategy.PowerOfTwoChoices);
// or
builder.UseEndpointSelector(mySelector);
```

A running multi-endpoint Client can observe and replace the current generation through the `SharpLink.Client` extension methods on `ISharpLinkClient`:

```csharp
var before = client.GetEndpointSelectionPolicySnapshot();

client.UpdateLoadBalancing(SharpLinkLoadBalancingStrategy.LeastPending);
client.UpdateEndpointSelector(mySelector);
```

`SharpLinkEndpointSelectionPolicySnapshot` reports the monotonically increasing generation, whether the generation is built-in or custom, and the built-in strategy when applicable. Publishing the already-current built-in strategy or the same custom selector instance is a no-op and does not advance the generation.

## Physical-attempt capture boundary

Endpoint selection is a next-physical-attempt policy.

A static or dynamic cluster captures exactly once, at the beginning of `GetReadyConnection` for one physical attempt:

1. the current immutable Ready endpoint snapshot;
2. the current immutable endpoint-selection policy generation.

All local exclusions/re-selections performed while that physical attempt is finding an admitted Ready connection reuse those two captured values. A policy publication that races the attempt therefore cannot make one attempt call two selectors or combine two strategy generations.

Once an endpoint/connection has been selected, publication never moves that in-flight attempt. A later retry attempt performs a new capture and may observe the newer policy.

The Ready topology and selection policy remain independent publications. Resolver changes continue to own endpoint membership/generations; selection policy changes do not add, remove, reconnect, retire, or otherwise mutate endpoints.

## Built-in state

`RoundRobin` and `LeastPending` cursors remain topology-owned runtime state rather than fields of the immutable policy generation. Switching away from and later back to one of those policies therefore does not allocate or migrate cursor state, and repeated policy publication cannot accumulate cursor generations.

Custom selector state is application-owned. Replacing selector A with selector B does not migrate state from A to B and does not invent new disposal semantics. An in-flight attempt can temporarily retain the selector generation it captured; once those attempts finish, the framework does not retain an obsolete generation chain.

## Lifecycle

Policy publication is serialized with the existing Client lifecycle control gate. Publication is rejected after draining/Stop/fault sealing begins. Reading the current snapshot remains a lock-free observation.

Publishing a policy does not start connection work. In particular, updating a static or dynamic Client before `ConnectAsync` does not dial endpoints or start a resolver/reconnect lifecycle.

## Hot-path cost

The multi-endpoint physical-attempt path adds one `Volatile.Read` of the current policy-generation reference. The captured reference is then reused through the attempt's existing selection/exclusion loop.

Built-in selection keeps the existing switch and topology-owned cursor code. There is no request-path global lock, registry lookup, configuration-object allocation, or additional built-in virtual dispatch. Custom selection continues to perform exactly one call to the captured `ISharpLinkEndpointSelector` per selection operation.

The fixed/single-endpoint path does not gain a dynamic-selection branch.
