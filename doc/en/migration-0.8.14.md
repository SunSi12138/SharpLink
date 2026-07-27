# SharpLink 0.8.14 Migration Guide

Chinese: [`../migration-0.8.14.md`](../migration-0.8.14.md)

0.8.14 changes no public API, Protocol v2, or generated Manifest. `NamedPipeServerTransportListener.maxServerInstances` now accepts only `NamedPipeServerStream.MaxAllowedServerInstances` (`-1`) or 1 through 254. Client `UseTcp` and `SocketClientTransportFactory` no longer accept port zero for remote TCP/DNS endpoints; Server port-zero ephemeral binding is unchanged. Code relying on strict global flow-control waiter ordering should note that another stream with available connection and stream credit may now progress when the head lacks only its own stream credit. FIFO is retained when shared connection credit is insufficient.
