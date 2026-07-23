### New Rules

 Rule ID      | Category            | Severity | Notes                                                   
--------------|---------------------|----------|---------------------------------------------------------
 SHARPLINK001 | SharpLink.Generator | Error    | Invalid RPC return type                                 
 SHARPLINK002 | SharpLink.Generator | Error    | More than one CancellationToken parameter on RPC method 
 SHARPLINK003 | SharpLink.Generator | Error    | Stream parameter count exceeds sbyte range
 SHARPLINK004 | SharpLink.Generator | Warning  | RPC method lacks CancellationToken and is not explicitly NonCancellable
 SHARPLINK014 | SharpLink.Generator | Error    | Streaming RPC lacks CancellationToken and is not explicitly NonCancellable
 SHARPLINK015 | SharpLink.Generator | Error    | RPC method declares both NonCancellable and CancellationToken
 SHARPLINK005 | SharpLink.Generator | Error    | Generic Type Parameter Not Supported in RPC Contract    
 SHARPLINK006 | SharpLink.Generator | Error    | RpcContract Interface Must Inherit IService             
 SHARPLINK007 | SharpLink.Generator | Error    | More than one SharpLinkCallOptions parameter
 SHARPLINK008 | SharpLink.Generator | Error    | RPC control parameters must be trailing
 SHARPLINK009 | SharpLink.Generator | Error    | DTO type is outside the native generated Codec subset
 SHARPLINK010 | SharpLink.Generator | Error    | Cyclic DTO graph is unsupported
 SHARPLINK011 | SharpLink.Generator | Error    | DTO member IDs collide
 SHARPLINK012 | SharpLink.Generator | Error    | DTO has no accessible construction plan
 SHARPLINK013 | SharpLink.Generator | Error    | DTO graph exceeds the maximum generated depth
 SHARPLINK016 | SharpLink.Generator | Error    | RpcService does not implement an RpcContract
 SHARPLINK017 | SharpLink.Generator | Error    | RpcService implements multiple RpcContracts
 SHARPLINK018 | SharpLink.Generator | Error    | RpcService type is abstract or open generic
 SHARPLINK019 | SharpLink.Generator | Error    | RpcService constructor cannot be selected
 SHARPLINK020 | SharpLink.Generator | Error    | RpcService lifetime is invalid
 SHARPLINK021 | SharpLink.Generator | Error    | Static contract route ownership conflict
 SHARPLINK022 | SharpLink.Generator | Error    | Static method descriptor conflict
 SHARPLINK023 | SharpLink.Generator | Error    | Static service ownership conflict
 SHARPLINK024 | SharpLink.Generator | Error    | Contract baseline is missing or malformed
 SHARPLINK025 | SharpLink.Generator | Error    | Contract baseline format version is unsupported
 SHARPLINK026 | SharpLink.Compatibility | Error | Contract ID changed or collided
 SHARPLINK027 | SharpLink.Compatibility | Error | Method ID changed or collided
 SHARPLINK028 | SharpLink.Compatibility | Error | DTO member ID changed
 SHARPLINK029 | SharpLink.Compatibility | Error | RPC call shape changed
 SHARPLINK030 | SharpLink.Compatibility | Error | Request, response, stream item, or DTO wire type changed
 SHARPLINK031 | SharpLink.Compatibility | Error | Required DTO member was added, removed, or tightened
 SHARPLINK032 | SharpLink.Compatibility | Error | Enum underlying type changed
 SHARPLINK033 | SharpLink.Compatibility | Error | Union tag was assigned to a different type
 SHARPLINK034 | SharpLink.Compatibility | Error | Existing RPC method was removed
 SHARPLINK035 | SharpLink.Compatibility | Error | Existing RPC contract was removed
 SHARPLINK036 | SharpLink.Generator | Error    | Contract Manifest output could not be written
 SHARPLINK037 | SharpLink.Compatibility | Error | Existing service route was removed
 SHARPLINK038 | SharpLink.Generator | Error | Multi-cluster key is invalid
 SHARPLINK039 | SharpLink.Generator | Error | Contract assembly has conflicting cluster routes
 SHARPLINK040 | SharpLink.Generator | Error | Cluster route marker lacks generated manifest
 SHARPLINK041 | SharpLink.Generator | Error | Multi-cluster route attribute is invalid
