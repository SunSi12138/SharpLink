# Rejected packed binding-owner source evidence

`ServerBindingTableTests.cs.txt` preserves the exact current test source from parent commit `fb5344b65abf3192ee4cad9c1b56509e758112dd` before the selected B v4 cold-facts migration. It includes the rejected per-RPC binding-group counter and strong-payload lease oracles. This noncompiled historical input is not a passing current test, a runtime validation result, or an alternative supported mode.

Current `ServerBindingTableTests.cs` instead checks cold identity, weak selection, retirement, and token/quota ownership. Actual RPC lifetime protection belongs to request permits and the single existing module lease acquired post-permit/pre-decode; table tests do not prove those dispatch/physical-tail obligations.
