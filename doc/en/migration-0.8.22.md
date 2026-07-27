# SharpLink 0.8.22 migration guide

Chinese: [`../migration-0.8.22.md`](../migration-0.8.22.md)

0.8.22 does not change Protocol v2 framing, generated DTO field IDs, fixed wire types, payload sizes, or the Manifest version. Valid 0.8.21 payloads remain readable by 0.8.22, and valid 0.8.22 payloads retain the prior sizes and field layouts.

Behavior changes only for malformed input and DateTimeOffset padding. Generated DTO Boolean, Rune, decimal, DateOnly, DateTime, TimeOnly, and DateTimeOffset fields, including nullable siblings, now report `DataLoss` for invalid bit patterns. DateTimeOffset writers canonicalize the six non-value padding bytes in the 16-byte native representation to zero. Readers do not require padding from an older valid payload to be zero, so rolling upgrades are not rejected for that reason.
