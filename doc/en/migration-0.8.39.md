# SharpLink 0.8.39 migration guide

Version 0.8.39 does not change valid Protocol v2 framing, route hashes, or payload layouts. Response-bearing Server interceptors must invoke `next`; returning without it was never a valid short circuit because the API cannot supply a replacement response. OneWay interceptors may still return directly. Server interceptor catch blocks now observe terminal status, code, and exception before unwind.

Client short circuits must return the exact scalar or stream result shape, while OneWay must return null. Invalid shapes still throw `InvalidCastException`, but the context now correctly records `Failed`. Framework consumption of application client streams no longer captures the caller synchronization context.

Malformed Boolean markers, truncation, invalid lengths, required nulls, and trailing bytes in generated or empty request payloads now surface as `DataLoss`. Monitoring that treated these peer-controlled failures as `Internal` should update its classification. An `InvalidDataException` thrown by application code remains hidden as `Internal` by default.
