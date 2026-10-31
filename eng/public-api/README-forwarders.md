# SDK type forwarding identity gate

The nine *.api.txt baselines preserve public declaration shapes, but a same-shaped
local type does not have the CLR identity of a type forwarded to another assembly.

The packed-DLL extractor now reads ECMA-335 ExportedType/AssemblyRef metadata via
System.Reflection.Metadata rather than relying on runtime GetType() resolution.
`eng/public-api/3.0.0/SharpLink.Sdk.forwarders.txt` locks exactly the 20 reviewed
`SharpLink.Sdk` names forwarded directly to `SharpLink.Abstractions`.
Missing, newly added, renamed, or retargeted forwarders fail the package API gate.
Nested exported type scopes are resolved to their actual parent AssemblyRef.

Only the assembly's **simple name** is compared for forward targets. AssemblyRef
version, culture and token are intentionally ignored by this inventory to avoid
forcing a public API rebaseline for ordinary 3.x package servicing. The existing
package identity gate governs release package versions independently.

To explicitly review an intended forwarding change, run the package API checker
with `--update` against freshly built packages and inspect both the text API
and SDK forwarding inventory diff. Do not rewrite archived 2.0 baselines.

Negative control: replace the SDK ExportedType for `SharpLink.Sdk.IService` with
a local interface of the same shape. The declaration API remains identical, but
the forwarder inventory loses a line and the checker must reject the package.
