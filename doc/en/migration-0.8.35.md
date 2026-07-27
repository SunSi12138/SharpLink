# SharpLink 0.8.35 migration guide

Chinese: [`../migration-0.8.35.md`](../migration-0.8.35.md)

0.8.35 does not change Protocol v2 framing, route hashes, valid payloads, or the public isolated-copy behavior of `SharpLinkRuntimeContext.Options`.

`LogEvents.Client.ResolverUpdateFailed = 6102` is additive. Resolver failures owned by retry are now Warning `6102`; truly unhandled background failures remain Error `6002`. Ordinary transport/session termination no longer produces an unhandled Error. Chaos JSON adds `ServerErrors`, gates on Errors from both sides, and returns exit 6 when an explicitly requested report cannot be written.

No application migration is required for protocol teardown or the internal profile-read optimization.
