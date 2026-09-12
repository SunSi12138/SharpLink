# Health probe result contract

`ISharpLinkClient.CheckHealthAsync` and `ISharpLinkMultiClusterClient.CheckHealthAsync` are query APIs. Expected connectivity and peer-capability states are represented by `SharpLinkHealthCheckResult` rather than exception control flow.

## Result model

`SharpLinkHealthCheckResult.Outcome` describes the local probe result. `Status` is a remote health status and is populated only for `Outcome == SharpLinkHealthProbeOutcome.Success`.

| Probe outcome | `Status` | Meaning |
| --- | --- | --- |
| `Success` | `Ready`, `Draining`, or `Unhealthy` | The peer returned a valid protocol health response. |
| `NotReady` | `null` | No Ready connection was available when the probe started. The peer did not report `Unhealthy`. |
| `Unavailable` | `null` | A selected connection could not complete the probe, including connection loss or the bounded probe deadline expiring. |
| `Unsupported` | `null` | The selected peer did not negotiate the protocol health-check capability. |

Callers should branch on `Outcome` before consuming `Status`:

```csharp
var health = await client.CheckHealthAsync(cancellationToken);

if (health.Outcome == SharpLinkHealthProbeOutcome.Success)
{
    switch (health.Status)
    {
        case SharpLinkHealthStatus.Ready:
            break;
        case SharpLinkHealthStatus.Draining:
        case SharpLinkHealthStatus.Unhealthy:
            // Remote server returned this state explicitly.
            break;
    }
}
else
{
    switch (health.Outcome)
    {
        case SharpLinkHealthProbeOutcome.NotReady:
        case SharpLinkHealthProbeOutcome.Unavailable:
        case SharpLinkHealthProbeOutcome.Unsupported:
            // Local reachability/capability outcome; no remote status exists.
            break;
    }
}
```

The multi-cluster overload applies the same result contract to the selected configured cluster. Invalid cluster keys and configured-slot precondition errors remain programmer/configuration errors rather than health outcomes.

## Exception boundary

Expected `NotReady`, in-flight unavailability, and unsupported peer capability do not throw. The following boundaries remain exceptional:

- caller cancellation remains `OperationCanceledException`;
- malformed or protocol-invalid health responses retain protocol-failure semantics;
- invalid arguments and programmer/configuration errors retain their existing exceptions;
- client terminal lifecycle failures and internal invariants are not converted to health outcomes;
- unexpected runtime/fatal failures are not hidden in the result.

The result does not retain an exception graph or require callers to branch on diagnostic messages.

## Hosting mapping

`SharpLinkRemoteHealthCheck` maps structured results to Microsoft health checks without relying on broad exception handling for normal probe outcomes:

| SharpLink result | Microsoft health result |
| --- | --- |
| `Success / Ready` | `Healthy` |
| `Success / Draining` | `Degraded` |
| `Success / Unhealthy` | `Unhealthy` |
| `NotReady` | `Unhealthy` |
| `Unavailable` | `Unhealthy` |
| `Unsupported` | `Unhealthy` |

`Unsupported` intentionally maps to `Unhealthy`: the registered remote readiness check cannot prove remote readiness when the peer does not support the protocol health frame. Unexpected exceptions are still surfaced as an unhealthy Microsoft health result; caller cancellation is rethrown.

## Lifecycle and wire behavior

A health probe observes the currently published Ready connection snapshot. It does not establish a connection, start a reconnect loop, raise readiness targets, or otherwise make the client Ready. Ordinary RPC endpoint selection and the RPC hot path are unchanged.

Protocol v2 `HealthCheck` / `HealthResponse` wire grammar is unchanged. Only the public query result contract changes.

## Migration / public API audit

This is an intentional source-contract change tracked by #655 and the public API audit in #86. Existing code that only inspected `SharpLinkHealthCheckResult.Status` should first branch on `Outcome`; `Status` is now nullable because no remote status exists for `NotReady`, `Unavailable`, or `Unsupported`.
