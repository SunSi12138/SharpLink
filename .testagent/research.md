# 0.8.15 regression-test research

## Target inventory and evidence candidates

- `SocketServerTransportListener`: Unix-domain listener construction deletes any pre-existing path before binding, including an ordinary caller-owned file.
- `SocketClientTransportFactory`: the factory retains a caller-owned mutable `IPEndPoint`, so changing its port after construction silently changes later connection attempts and bypasses the constructor snapshot.
- `SharpLinkTransportFactories`: built-in endpoint-factory delegates retain mutable socket, TLS, and shared-memory option objects, allowing later topology generations to receive different configuration from earlier generations.
- `SharpClientBuilder`: direct transports and endpoint resolvers are documented as owned by the built Client, but the builder can transfer the same instance into multiple Clients.
- `SharpLinkServerBuilder`: the same listener can be transferred into multiple Servers, while a failed build does not release the listener even though the analogous Client rollback does.

## Acceptance checklist

- Unix-domain bind never deletes a pre-existing filesystem entry and still removes a path created by a successfully owned listener.
- Built-in socket factories snapshot supported endpoint values, including a mutable `IPEndPoint` and IPv6 address scope.
- Endpoint transport delegates freeze every option object at delegate creation; later caller mutations cannot split endpoint generations.
- A direct Client transport or resolver is transferred once and the builder requires an explicitly supplied replacement before another build.
- Server listener ownership transfers once on success and is released exactly once, with all failures preserved, when a build attempt fails after Runtime Context creation.

## Audit guardrails

This pass reviews transport filesystem safety and builder/configuration ownership. Style-only analyzer suggestions and extreme-duration validation ideas remain below P2 and are not counted. Multi-endpoint builders remain reusable because they create fresh factories for each build; only already-instantiated single-owner resources are consumed.
