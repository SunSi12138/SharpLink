# Contract-bound RPC channels in 3.0

<!-- current-generated-boundary:start -->
Current generated boundary: API **5**, ABI `sharplink-3.0-api5-contract-channel-complete-method-facts-v1`, Protocol **3**.
<!-- current-generated-boundary:end -->

## Current client construction and preparation

`IRpcChannel` is removed. Generated proxy factories receive `IRpcContractChannel`,
frozen to one exact local registration, scope, metadata and binding requirement.
Regenerate contracts with the matching 3.0 SDK and deploy matching Abstractions,
Runtime, Client and Server packages. `SharpLinkGeneratedManifestVersions` is the
authority; earlier API5 identities are not compatible merely because the integer is 5.

Application code uses the generated service through its client-created handle:

```csharp
var proxy = client.CreateProxy<IMyContract>();
await proxy.PrepareAsync(TimeSpan.FromSeconds(10), cancellationToken);
var service = proxy.Service;
// Invoke generated contract methods on service.
```

`CreateProxy` registers local demand but does not wait for connectivity or remote
binding. Keep the same handle when preparation of that exact site is required.
`PrepareAsync` requires a positive finite timeout no greater than 2,147,483,647 ms;
it validates timeout before cancellation and client/registration lifetime. Caller
cancellation stops only that wait, not shared demand. Reconnect/rebinding does not
restart the original budget; preparation never upgrades the selected registration.
Readiness is a point-in-time observation, not a lifetime lease, capacity reservation,
or guarantee that a later invocation succeeds. Calls fail fast until their site is bound.

Low-level callers select `client.GetLocalContract<TContract>()` (or the exact
registered Assembly/contract ID overload), then call
`client.CreateContractChannel(local, options)`. `RpcLocalContract` is client-issued;
its issuer, generation, contract ID and assembly hash identify the original local
registration, not a remote token. `RpcLocalContract` and `RpcProxy<TContract>`
constructors remain internal; do not fabricate selectors or pair a service with a
different channel. Re-registration does not update an old selector or handle.

`RpcProxyOptions` captures metadata, target scope and binding requirement at
construction. Minimum-version and exact-registration requirements need the full
original receipt matching the selected contract/hash; null scope is not a later
route lookup. Do not restore `Get<T>`/`GetWithMetadata<T>` or raw client-channel facades.

## Client stream ownership

Generated streaming uses the real connection-bound `IRpcClientStreamSink` selected
for an invocation. `IRpcContractChannel` does not inherit that sink. Application
callers use generated `[RpcContract]` streaming methods, not casts from the client
or channel to a sink. The removed `SharpLinkClient.SendClientStreamAsync<T>` facade
was not a usable stream owner.

The current combined migration uses Protocol 3. The earlier channel/sink split's
claim of unchanged Protocol v2 frames was specific to that development stage, not
this boundary. Source/API alignment here does not establish runtime, package,
Trim/AOT or final release acceptance.
