# SharpLink 0.8.23 migration guide

Chinese: [`../migration-0.8.23.md`](../migration-0.8.23.md)

0.8.23 does not change Protocol v2 framing, collection counts, or element layouts. Valid 0.8.22 collection payloads remain readable. Invalid bit patterns previously accepted in Boolean, Rune, decimal, DateOnly, DateTime, TimeOnly, or DateTimeOffset collections now report `DataLoss`.

DateTimeOffset collection writers clear the six non-value padding bytes in every 16-byte native element, while readers do not require padding from an older payload to be zero. If a shared-memory peer closes before completing its server response, Client Connect now throws `SharpLinkException(Unavailable)` and retains the original `EndOfStreamException` or `IOException` as the inner cause. Caller cancellation semantics are unchanged.
