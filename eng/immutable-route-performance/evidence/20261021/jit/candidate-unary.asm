; Assembly listing for method SharpLink.Client.SharpLinkClient:InvokeUnaryCoreAsync[SharpLink.Abstractions.RpcEmptyRequest,int](SharpLink.Abstractions.RpcMethodDescriptor,SharpLink.Abstractions.RpcEmptyRequest,SharpLink.Abstractions.IRpcCodec`1[SharpLink.Abstractions.RpcEmptyRequest],SharpLink.Abstractions.IRpcCodec`1[int],SharpLink.Client.SharpLinkClient+ResolvedCallControl,System.Threading.CancellationToken):System.Threading.Tasks.ValueTask`1[int]:this (FullOpts)
; Emitting BLENDED_CODE for generic X64 + VEX + EVEX on Unix
; FullOpts code
; optimized code
; rbp based frame
; fully interruptible
; No PGO data
; 1 inlinees with PGO data; 13 single block inlinees; 3 inlinees without PGO data

G_M000_IG01:                ;; offset=0x0000
       push     rbp
       push     r15
       push     r14
       push     r13
       push     r12
       push     rbx
       sub      rsp, 264
       lea      rbp, [rsp+0x130]
       vxorps   xmm8, xmm8, xmm8
       vmovdqu  xmmword ptr [rbp-0x48], xmm8
       xor      eax, eax
       mov      qword ptr [rbp-0x38], rax
       mov      rbx, rdi
       mov      r12d, esi
       mov      r14, rdx
       mov      r15, rcx
       mov      r13, r8
 
G_M000_IG02:                ;; offset=0x0038
       movzx    rax, byte  ptr [rbp+0x35]
       mov      dword ptr [rbp-0x88], eax
       cmp      gword ptr [rbx+0x108], 0
       jne      SHORT G_M000_IG03
       xor      rcx, rcx
       mov      rax, rcx
       jmp      G_M000_IG04
 
G_M000_IG03:                ;; offset=0x0056
       mov      rdi, 0x7F532B0F70D0
       call     CORINFO_HELP_NEWSFAST
       mov      rcx, rax
       mov      gword ptr [rbp-0xB8], rcx
       lea      rdi, bword ptr [rcx+0x08]
       mov      rsi, rbx
       call     CORINFO_HELP_ASSIGN_REF
       mov      rcx, gword ptr [rbp-0xB8]
       lea      rdi, bword ptr [rcx+0x38]
       vmovdqu  ymm0, ymmword ptr [rbp+0x10]
       vmovdqu  ymmword ptr [rdi], ymm0
       mov      rax, qword ptr [rbp+0x30]
       mov      qword ptr [rdi+0x20], rax
       mov      eax, dword ptr [rbp-0x88]
       mov      byte  ptr [rdi+0x25], al
       mov      rdi, gword ptr [rcx+0x08]
       mov      rdi, gword ptr [rdi+0x50]
       mov      rdi, gword ptr [rdi+0x40]
       mov      rdx, qword ptr [rdi]
       mov      rdx, qword ptr [rdx+0x40]
       call     [rdx+0x38]System.TimeProvider:GetTimestamp():long:this
       mov      rcx, gword ptr [rbp-0xB8]
       mov      qword ptr [rcx+0x18], rax
       mov      rdi, 0x7F4E3E401578
       mov      rdi, gword ptr [rdi]
       mov      esi, 1
       call     [SharpLink.Abstractions.SharpLinkTelemetryObserverIsolation:Add(System.Diagnostics.Metrics.Counter`1[long],long)]
       mov      rcx, gword ptr [rbp-0xB8]
       mov      rax, rcx
 
G_M000_IG04:                ;; offset=0x00E3
       mov      gword ptr [rbp-0x98], rax
       mov      gword ptr [rbp-0xA8], rax
       test     rax, rax
       je       SHORT G_M000_IG06
 
G_M000_IG05:                ;; offset=0x00F6
       mov      rax, gword ptr [rbp-0xA8]
       jmp      SHORT G_M000_IG07
 
G_M000_IG06:                ;; offset=0x00FF
       mov      rdi, 0x7F4E3E401578
       mov      rdi, gword ptr [rdi]
       mov      esi, 1
       call     [SharpLink.Abstractions.SharpLinkTelemetryObserverIsolation:Add(System.Diagnostics.Metrics.Counter`1[long],long)]
       mov      rax, gword ptr [rbp-0xA8]
 
G_M000_IG07:                ;; offset=0x011E
       xor      rdi, rdi
       mov      gword ptr [rbp-0xA0], rdi
 
G_M000_IG08:                ;; offset=0x0127
       mov      dword ptr [rbp-0x2C], edi
 
G_M000_IG09:                ;; offset=0x012A
       mov      ecx, dword ptr [rbp-0x88]
       mov      byte  ptr [rbp+0x35], cl
       vmovdqu  xmm0, xmmword ptr [rbp+0x10]
       vmovdqu  xmmword ptr [rsp], xmm0
       vmovdqu  xmm0, xmmword ptr [rbp+0x20]
       vmovdqu  xmmword ptr [rsp+0x10], xmm0
       mov      rdi, qword ptr [rbp+0x30]
       mov      qword ptr [rsp+0x20], rdi
       mov      rdi, rbx
       mov      gword ptr [rbp-0xA8], rax
       mov      rdx, rax
       xor      rsi, rsi
       call     [SharpLink.Client.SharpLinkClient:GetReadyConnection(SharpLink.Abstractions.RpcMethodDescriptor,SharpLink.Client.SharpLinkClient+EndpointRetrySelectionState,SharpLink.Client.SharpLinkClient+AttemptOutcomeState):SharpLink.Client.ClientConnection:this]
       mov      gword ptr [rbp-0xA0], rax
       mov      dword ptr [rbp-0x2C], 1
       lea      rdi, [rbp+0x38]
       call     [SharpLink.Client.SharpLinkClient:EnsureLogicalCallProgress(byref)]
       mov      rax, gword ptr [rbp-0xA0]
       mov      rax, gword ptr [rax+0x40]
       mov      gword ptr [rbp-0xB0], rax
       vmovdqu  ymm0, ymmword ptr [rbp+0x60]
       vmovdqu  ymmword ptr [rbp-0x70], ymm0
       mov      rcx, qword ptr [rbp+0x80]
       mov      qword ptr [rbp-0x50], rcx
       mov      ecx, dword ptr [rbp-0x88]
       mov      edx, ecx
       and      edx, 1
       mov      dword ptr [rbp-0x8C], edx
       mov      dword ptr [rbp-0x74], edx
       test     cl, 16
       setne    cl
       movzx    rcx, cl
       mov      dword ptr [rbp-0x84], ecx
       cmp      byte  ptr [rax], al
       test     r15, r15
       je       G_M000_IG11
       cmp      dword ptr [rax+0x5C], 0
       jne      G_M000_IG12
       call     [SharpLink.Client.PendingRequestTable+RpcOperationPool`1[int]:Rent():SharpLink.Client.RpcRequestOperation`1[int]]
       mov      gword ptr [rbp-0xC0], rax
       vmovdqu  xmm0, xmmword ptr [rbp-0x70]
       vmovdqu  xmmword ptr [rsp], xmm0
       vmovdqu  xmm0, xmmword ptr [rbp-0x60]
       vmovdqu  xmmword ptr [rsp+0x10], xmm0
       mov      r9, qword ptr [rbp-0x50]
       mov      qword ptr [rsp+0x20], r9
       mov      gword ptr [rsp+0x28], r15
       mov      r15, gword ptr [rbp-0xA8]
       mov      gword ptr [rsp+0x30], r15
       mov      r15d, dword ptr [rbp-0x74]
       mov      dword ptr [rsp+0x38], r15d
 
G_M000_IG10:                ;; offset=0x0222
       mov      r15d, dword ptr [rbp-0x84]
       mov      dword ptr [rsp+0x40], r15d
       lea      r9, [rbp-0x38]
       mov      rdi, gword ptr [rbp-0xB0]
       mov      rdx, rax
       mov      r8, r13
       xor      esi, esi
       xor      rcx, rcx
       call     [SharpLink.Client.PendingRequestTable:TryRegister[int](byte,SharpLink.Client.RpcRequestOperation`1[int],SharpLink.Runtime.IStreamDispatcher,SharpLink.Abstractions.RpcDeadline,System.Threading.CancellationToken,byref,SharpLink.Abstractions.IRpcCodec`1[int],SharpLink.Client.IPendingCallCompletionObserver,bool,bool):bool:this]
       test     eax, eax
       je       G_M000_IG13
       xor      ecx, ecx
       mov      dword ptr [rbp-0x2C], ecx
       mov      rdx, qword ptr [rbp+0x18]
       mov      rcx, qword ptr [rbp+0x20]
       mov      qword ptr [rbp-0x80], rcx
       mov      r8, qword ptr [rbp-0x38]
       mov      dword ptr [rsp], r12d
       mov      gword ptr [rsp+0x08], r14
       mov      r15, gword ptr [rbp-0xC0]
       mov      gword ptr [rsp+0x10], r15
       lea      rdi, [rsp+0x18]
       lea      rsi, [rbp+0x38]
       mov      rcx, gword ptr [rsi]
       mov      gword ptr [rsp+0x18], rcx
       add      rsi, 8
       add      rdi, 8
       mov      rcx, gword ptr [rsi]
       mov      gword ptr [rsp+0x20], rcx
       add      rsi, 8
       add      rdi, 8
       mov      rcx, gword ptr [rsi]
       mov      gword ptr [rsp+0x28], rcx
       add      rsi, 8
       add      rdi, 8
       mov      rcx, gword ptr [rsi]
       mov      gword ptr [rsp+0x30], rcx
       add      rsi, 8
       add      rdi, 8
       mov      ecx, 6
       rep movsq 
       mov      gword ptr [rsp+0x68], r13
       mov      r9d, dword ptr [rbp-0x8C]
       mov      rdi, rbx
       mov      rsi, gword ptr [rbp-0xA0]
       mov      rcx, qword ptr [rbp-0x80]
       call     [SharpLink.Client.SharpLinkClient:StartUnaryCall[SharpLink.Abstractions.RpcEmptyRequest,int](SharpLink.Client.ClientConnection,long,long,long,bool,SharpLink.Abstractions.RpcEmptyRequest,SharpLink.Abstractions.IRpcCodec`1[SharpLink.Abstractions.RpcEmptyRequest],SharpLink.Client.RpcRequestOperation`1[int],SharpLink.Client.SharpLinkClient+ResolvedCallControl,System.Threading.CancellationToken):System.Threading.Tasks.ValueTask`1[int]:this]
       mov      gword ptr [rbp-0x48], rax
       mov      qword ptr [rbp-0x40], rdx
       jmp      SHORT G_M000_IG14
 
G_M000_IG11:                ;; offset=0x02F6
       mov      edi, 0x251D
       mov      rsi, 0x7F532A0542F0
       call     [CORINFO_HELP_STRCNS]
       mov      rdi, rax
       call     [System.ArgumentNullException:Throw(System.String)]
       int3     
 
G_M000_IG12:                ;; offset=0x0315
       mov      rax, gword ptr [rbp-0xB0]
       mov      rdi, rax
       call     [System.ThrowHelper:ThrowObjectDisposedException(System.Object)]
       int3     
 
G_M000_IG13:                ;; offset=0x0326
       mov      r13, gword ptr [rbp-0xC0]
       cmp      byte  ptr [r13], r13b
       mov      rdi, r13
       call     [SharpLink.Client.RpcRequestOperation`1[int]:ReturnToPool():this]
       call     [SharpLink.Client.PendingRequestTable:CreateResourceExhaustedException():SharpLink.Abstractions.SharpLinkException]
       mov      rdi, rax
       call     CORINFO_HELP_THROW
       int3     
 
G_M000_IG14:                ;; offset=0x0349
       mov      rax, gword ptr [rbp-0x48]
       mov      rdx, qword ptr [rbp-0x40]
 
G_M000_IG15:                ;; offset=0x0351
       vzeroupper 
       add      rsp, 264
       pop      rbx
       pop      r12
       pop      r13
       pop      r14
       pop      r15
       pop      rbp
       ret      
 
G_M000_IG16:                ;; offset=0x0366
       sub      rsp, 120
 
G_M000_IG17:                ;; offset=0x036A
       mov      rbx, rdi
       cmp      dword ptr [rbp-0x2C], 0
       je       SHORT G_M000_IG18
       mov      rdi, gword ptr [rbp-0xA0]
       cmp      dword ptr [rdi], edi
       call     [SharpLink.Client.ClientConnection:ReleaseCallAdmissionReservation():this]
 
G_M000_IG18:                ;; offset=0x0382
       lea      rdi, [rbp+0x38]
       mov      rsi, rbx
       call     [SharpLink.Client.SharpLinkClient:ArbitrateLogicalCallFailure(byref,System.Exception):System.Exception]
       mov      rbx, rax
       cmp      gword ptr [rbp-0x98], 0
       je       SHORT G_M000_IG19
       mov      rdi, gword ptr [rbp-0x98]
       mov      rsi, rbx
       call     [SharpLink.Client.SharpLinkClient+AttemptOutcomeState:CompleteLocalFailure(System.Exception):this]
 
G_M000_IG19:                ;; offset=0x03AC
       mov      rdi, rbx
       call     [System.Threading.Tasks.ValueTask:FromException[int](System.Exception):System.Threading.Tasks.ValueTask`1[int]]
       mov      gword ptr [rbp-0x48], rax
       mov      qword ptr [rbp-0x40], rdx
       lea      rax, G_M000_IG14
 
G_M000_IG20:                ;; offset=0x03C4
       vzeroupper 
       add      rsp, 120
       ret      
 
; Total bytes of code 972

