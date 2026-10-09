; Assembly listing for method SharpLink.Server.SharpLinkServer:InvokeServiceAsync(SharpLink.Server.ServiceRegistration,SharpLink.Server.ServerConnectionState,SharpLink.Runtime.RpcSession,long,long,System.Buffers.ReadOnlySequence`1[byte],SharpLink.Abstractions.IRpcByteBufferWriter,System.Threading.CancellationToken,SharpLink.Abstractions.SharpLinkCallContextSnapshot):System.Threading.Tasks.ValueTask:this (FullOpts)
; Emitting BLENDED_CODE for generic X64 + VEX + EVEX on Unix
; FullOpts code
; optimized code
; rbp based frame
; fully interruptible
; No PGO data
; 3 inlinees with PGO data; 25 single block inlinees; 6 inlinees without PGO data

G_M000_IG01:                ;; offset=0x0000
       push     rbp
       push     r15
       push     r14
       push     r13
       push     r12
       push     rbx
       sub      rsp, 808
       lea      rbp, [rsp+0x350]
       vxorps   xmm8, xmm8, xmm8
       vmovdqa32 xmmword ptr [rbp-0x270], xmm8
       vmovdqa32 xmmword ptr [rbp-0x260], xmm8
       mov      rax, -480
       vmovdqa  xmmword ptr [rbp+rax-0x70], xmm8
       vmovdqa  xmmword ptr [rbp+rax-0x60], xmm8
       vmovdqa  xmmword ptr [rbp+rax-0x50], xmm8
       add      rax, 48
       jne      SHORT  -5 instr
       mov      qword ptr [rbp-0x70], rax
       mov      gword ptr [rbp-0x278], rdi
       mov      gword ptr [rbp-0x280], rsi
       mov      gword ptr [rbp-0x288], rcx
       mov      qword ptr [rbp-0x30], r8
       mov      qword ptr [rbp-0x38], r9
       mov      rbx, rsi
       mov      r15, rdx
       mov      r13, gword ptr [rbp+0x28]
       mov      r14, gword ptr [rbp+0x38]
       mov      r12, gword ptr [rbp+0x30]
 
G_M000_IG02:                ;; offset=0x0081
       mov      rsi, gword ptr [r15+0x58]
       movzx    rax, byte  ptr [rbx+0x52]
       test     eax, eax
       jne      G_M000_IG07
       cmp      dword ptr [rbx+0x4C], 0
       jne      G_M000_IG07
       cmp      dword ptr [rbx+0x48], 0
       jne      G_M000_IG40
       mov      rax, gword ptr [rbx+0x08]
       test     rax, rax
       jne      SHORT G_M000_IG03
       mov      rdi, rbx
       mov      rdx, r9
       call     [SharpLink.Server.ServiceRegistration:GetOrCreateSingleton(SharpLink.Abstractions.IRpcGeneratedServerBridge,long):System.Object:this]
 
G_M000_IG03:                ;; offset=0x00BA
       mov      rdx, rax
       mov      rsi, gword ptr [rbx+0x38]
       mov      gword ptr [rbp-0x290], rsi
       mov      r8, gword ptr [r15+0x58]
       vxorps   ymm0, ymm0, ymm0
       vmovdqu  ymmword ptr [rbp-0xD8], ymm0
       vmovdqu32 xmmword ptr [rbp-0xC0], xmm0
       mov      r9, qword ptr [rbp-0x38]
       mov      qword ptr [rsp], r9
       lea      rdi, [rsp+0x08]
       lea      rsi, [rbp+0x10]
       mov      rcx, gword ptr [rsi]
       mov      gword ptr [rsp+0x08], rcx
       add      rsi, 8
       add      rdi, 8
       mov      rcx, gword ptr [rsi]
       mov      gword ptr [rsp+0x10], rcx
       add      rsi, 8
       add      rdi, 8
       movsq    
       mov      gword ptr [rsp+0x20], r13
       mov      gword ptr [rsp+0x28], r12
       mov      gword ptr [rsp+0x30], r14
       vmovdqu  xmm0, xmmword ptr [rbp-0xD8]
       vmovdqu  xmmword ptr [rsp+0x38], xmm0
       vmovdqu  xmm0, xmmword ptr [rbp-0xC8]
       vmovdqu  xmmword ptr [rsp+0x48], xmm0
       mov      rdi, qword ptr [rbp-0xB8]
       mov      qword ptr [rsp+0x58], rdi
       mov      rdi, gword ptr [rbp-0x278]
       mov      rsi, gword ptr [rbp-0x290]
       mov      rcx, gword ptr [rbp-0x288]
       mov      r9, qword ptr [rbp-0x30]
       call     [SharpLink.Server.SharpLinkServer:InvokeServiceTrackedAsync(SharpLink.Abstractions.IRpcStub,System.Object,SharpLink.Runtime.RpcSession,SharpLink.Abstractions.IRpcGeneratedServerBridge,long,long,System.Buffers.ReadOnlySequence`1[byte],SharpLink.Abstractions.IRpcByteBufferWriter,System.Threading.CancellationToken,SharpLink.Abstractions.SharpLinkCallContextSnapshot,System.Nullable`1[SharpLink.Abstractions.RpcMethodDescriptor]):System.Threading.Tasks.ValueTask:this]
       mov      gword ptr [rbp-0x270], rax
       mov      qword ptr [rbp-0x268], rdx
 
G_M000_IG04:                ;; offset=0x0176
       vmovdqu32 xmm0, xmmword ptr [rbp-0x270]
       vmovdqu32 xmmword ptr [rbp-0x220], xmm0
 
G_M000_IG05:                ;; offset=0x0184
       mov      rax, gword ptr [rbp-0x220]
       mov      rdx, qword ptr [rbp-0x218]
 
G_M000_IG06:                ;; offset=0x0192
       vzeroupper 
       add      rsp, 808
       pop      rbx
       pop      r12
       pop      r13
       pop      r14
       pop      r15
       pop      rbp
       ret      
 
G_M000_IG07:                ;; offset=0x01A7
       mov      r8, qword ptr [rbp-0x30]
       xor      ecx, ecx
       xor      esi, esi
       mov      dword ptr [rbp-0x3C], esi
       vxorps   ymm0, ymm0, ymm0
       vmovdqu  ymmword ptr [rbp-0x68], ymm0
       vmovdqu  xmmword ptr [rbp-0x50], xmm0
       test     eax, eax
       je       SHORT G_M000_IG14
       test     r14, r14
       je       SHORT G_M000_IG10
 
G_M000_IG08:                ;; offset=0x01C9
       mov      rsi, 0x7F0716ED0900
       cmp      qword ptr [r14], rsi
       jne      SHORT G_M000_IG10
 
G_M000_IG09:                ;; offset=0x01D8
       vmovdqu  ymm0, ymmword ptr [r14+0xD0]
       vmovdqu  ymmword ptr [rbp-0x210], ymm0
       jmp      SHORT G_M000_IG11
 
G_M000_IG10:                ;; offset=0x01EB
       mov      rsi, gword ptr [rbx+0x38]
       lea      rdi, [rbp-0x210]
       mov      rdx, r8
       call     [SharpLink.Server.SharpLinkServer:GetMethodDescriptor(SharpLink.Abstractions.IRpcStub,long):SharpLink.Abstractions.RpcMethodDescriptor]
 
G_M000_IG11:                ;; offset=0x01FF
       movzx    rdx, byte  ptr [rbp-0x1F4]
       vmovdqu  ymm0, ymmword ptr [rbp-0x210]
       vmovdqu  ymmword ptr [rbp-0x60], ymm0
       mov      byte  ptr [rbp-0x44], dl
       mov      byte  ptr [rbp-0x68], 1
       lea      edi, [rdx-0x02]
       cmp      edi, 2
       setbe    cl
       movzx    rcx, cl
       cmp      edx, 2
       je       SHORT G_M000_IG12
       cmp      edx, 4
       je       SHORT G_M000_IG12
       xor      edx, edx
       jmp      SHORT G_M000_IG13
 
G_M000_IG12:                ;; offset=0x0234
       mov      edx, 1
 
G_M000_IG13:                ;; offset=0x0239
       mov      dword ptr [rbp-0x3C], edx
 
G_M000_IG14:                ;; offset=0x023C
       vxorps   xmm0, xmm0, xmm0
       vmovdqu  xmmword ptr [rbp-0x78], xmm0
 
G_M000_IG15:                ;; offset=0x0245
       mov      r8, gword ptr [r15+0x58]
       mov      gword ptr [rbp-0x298], r8
       cmp      byte  ptr [rbx+0x52], 0
       je       G_M000_IG17
       cmp      dword ptr [rbx+0x4C], 0
       jne      G_M000_IG17
       mov      rdi, gword ptr [rbx+0x40]
       cmp      dword ptr [rbx+0x48], 0
       jne      G_M000_IG18
       lea      rdx, [rbp-0x78]
       mov      esi, ecx
       cmp      dword ptr [rdi], edi
       call     [SharpLink.Runtime.SharpLinkDynamicModule:TryAcquire(bool,byref):bool:this]
       test     eax, eax
       je       G_M000_IG19
       mov      rdx, gword ptr [rbx+0x08]
       test     rdx, rdx
       jne      SHORT G_M000_IG16
       mov      rdi, rbx
       mov      rsi, gword ptr [rbp-0x298]
       mov      rdx, qword ptr [rbp-0x38]
       call     [SharpLink.Server.ServiceRegistration:GetOrCreateSingleton(SharpLink.Abstractions.IRpcGeneratedServerBridge,long):System.Object:this]
       mov      rdx, rax
 
G_M000_IG16:                ;; offset=0x02A8
       mov      r9, qword ptr [rbp-0x38]
       mov      qword ptr [rsp], r9
       lea      rdi, [rsp+0x08]
       lea      rsi, [rbp+0x10]
       mov      rcx, gword ptr [rsi]
       mov      gword ptr [rsp+0x08], rcx
       add      rsi, 8
       add      rdi, 8
       mov      rcx, gword ptr [rsi]
       mov      gword ptr [rsp+0x10], rcx
       add      rsi, 8
       add      rdi, 8
       movsq    
       mov      gword ptr [rsp+0x20], r13
       mov      gword ptr [rsp+0x28], r12
       mov      gword ptr [rsp+0x30], r14
       vmovdqu  xmm0, xmmword ptr [rbp-0x68]
       vmovdqu  xmmword ptr [rsp+0x38], xmm0
       vmovdqu  xmm0, xmmword ptr [rbp-0x58]
       vmovdqu  xmmword ptr [rsp+0x48], xmm0
       mov      r8, qword ptr [rbp-0x48]
       mov      qword ptr [rsp+0x58], r8
       mov      r8, gword ptr [r15+0x58]
       mov      rsi, gword ptr [rbx+0x38]
       mov      rdi, gword ptr [rbp-0x278]
       mov      rcx, gword ptr [rbp-0x288]
       mov      r9, qword ptr [rbp-0x30]
       call     [SharpLink.Server.SharpLinkServer:InvokeServiceTrackedAsync(SharpLink.Abstractions.IRpcStub,System.Object,SharpLink.Runtime.RpcSession,SharpLink.Abstractions.IRpcGeneratedServerBridge,long,long,System.Buffers.ReadOnlySequence`1[byte],SharpLink.Abstractions.IRpcByteBufferWriter,System.Threading.CancellationToken,SharpLink.Abstractions.SharpLinkCallContextSnapshot,System.Nullable`1[SharpLink.Abstractions.RpcMethodDescriptor]):System.Threading.Tasks.ValueTask:this]
       mov      gword ptr [rbp-0x1C0], rax
       mov      qword ptr [rbp-0x1B8], rdx
       mov      edi, dword ptr [rbp-0x3C]
       mov      dword ptr [rsp], edi
       mov      rdi, gword ptr [rbp-0x1C0]
       mov      rsi, qword ptr [rbp-0x1B8]
       mov      rdx, gword ptr [rbp-0x78]
       mov      rcx, qword ptr [rbp-0x70]
       mov      r8, gword ptr [rbp-0x288]
       mov      r9, qword ptr [rbp-0x38]
       call     [SharpLink.Server.SharpLinkServer:CompleteDynamicSingletonInvocationAsync(System.Threading.Tasks.ValueTask,SharpLink.Runtime.SharpLinkDynamicModuleLease,SharpLink.Runtime.RpcSession,long,bool):System.Threading.Tasks.ValueTask]
       mov      gword ptr [rbp-0xE8], rax
       mov      qword ptr [rbp-0xE0], rdx
       jmp      SHORT G_M000_IG20
 
G_M000_IG17:                ;; offset=0x0374
       vxorps   xmm0, xmm0, xmm0
       vmovdqu  xmmword ptr [rbp-0x78], xmm0
       jmp      SHORT G_M000_IG22
 
G_M000_IG18:                ;; offset=0x037F
       mov      rdi, rbx
       call     [System.ThrowHelper:ThrowObjectDisposedException(System.Object)]
       int3     
 
G_M000_IG19:                ;; offset=0x0389
       mov      rdi, 0x7F0716DA1B78
       call     CORINFO_HELP_NEWSFAST
       mov      r15, rax
       mov      edi, 0x4225
       mov      rsi, 0x7F0716305E48
       call     [CORINFO_HELP_STRCNS]
       mov      rdx, rax
       mov      rdi, r15
       mov      esi, 10
       call     [SharpLink.Abstractions.SharpLinkException:.ctor(int,System.String):this]
       mov      rdi, r15
       call     CORINFO_HELP_THROW
       int3     
 
G_M000_IG20:                ;; offset=0x03CA
       mov      rax, gword ptr [rbp-0xE8]
       mov      rdx, qword ptr [rbp-0xE0]
 
G_M000_IG21:                ;; offset=0x03D8
       vzeroupper 
       add      rsp, 808
       pop      rbx
       pop      r12
       pop      r13
       pop      r14
       pop      r15
       pop      rbp
       ret      
 
G_M000_IG22:                ;; offset=0x03ED
       lea      rsi, [rbp-0xB0]
       mov      rdi, rbx
       mov      rdx, r15
       mov      r9, qword ptr [rbp-0x38]
       call     [SharpLink.Server.ServiceRegistration:AcquireAsync(SharpLink.Server.ServerConnectionState,bool,SharpLink.Abstractions.IRpcGeneratedServerBridge,long):System.Threading.Tasks.ValueTask`1[SharpLink.Server.ServiceLease]:this]
       nop      
 
G_M000_IG23:                ;; offset=0x0405
       mov      rax, gword ptr [rbp-0xB0]
       mov      gword ptr [rbp-0x2A0], rax
       test     rax, rax
       je       G_M000_IG27
       mov      rsi, rax
       mov      rdi, 0x7F0716ED49B8
       call     CORINFO_HELP_ISINSTANCEOFCLASS
       test     rax, rax
       jne      G_M000_IG26
       mov      rdi, gword ptr [rbp-0x2A0]
       movsx    rsi, word  ptr [rbp-0xA8]
       mov      r11, 0x7F0714C20920
       call     [r11]System.Threading.Tasks.Sources.IValueTaskSource`1[SharpLink.Server.ServiceLease]:GetStatus(short):int:this
       cmp      eax, 1
       je       G_M000_IG27
 
G_M000_IG24:                ;; offset=0x045C
       lea      rdi, [rsp]
       lea      rsi, [rbp-0xB0]
       mov      rcx, gword ptr [rsi]
       mov      gword ptr [rsp], rcx
       add      rsi, 8
       add      rdi, 8
       movsq    
       mov      rcx, gword ptr [rsi]
       mov      gword ptr [rsp+0x10], rcx
       add      rsi, 8
       add      rdi, 8
       mov      rcx, gword ptr [rsi]
       mov      gword ptr [rsp+0x18], rcx
       add      rsi, 8
       add      rdi, 8
       movsq    
       mov      rcx, gword ptr [rsi]
       mov      gword ptr [rsp+0x28], rcx
       add      rsi, 8
       add      rdi, 8
       movsq    
       lea      rdi, [rsp+0x38]
       lea      rsi, [rbp+0x10]
       mov      rcx, gword ptr [rsi]
       mov      gword ptr [rsp+0x38], rcx
       add      rsi, 8
       add      rdi, 8
       mov      rcx, gword ptr [rsi]
       mov      gword ptr [rsp+0x40], rcx
       add      rsi, 8
       add      rdi, 8
       movsq    
       mov      gword ptr [rsp+0x50], r13
       mov      gword ptr [rsp+0x58], r12
       mov      gword ptr [rsp+0x60], r14
       mov      esi, dword ptr [rbp-0x3C]
       mov      dword ptr [rsp+0x68], esi
       vmovdqu  xmm0, xmmword ptr [rbp-0x68]
       vmovdqu  xmmword ptr [rsp+0x70], xmm0
       vmovdqu  xmm0, xmmword ptr [rbp-0x58]
       vmovdqu32 xmmword ptr [rsp+0x80], xmm0
       mov      rsi, qword ptr [rbp-0x48]
       mov      qword ptr [rsp+0x90], rsi
       mov      rsi, gword ptr [rbx+0x38]
       mov      rcx, gword ptr [r15+0x58]
       mov      rdi, gword ptr [rbp-0x278]
       mov      rdx, gword ptr [rbp-0x288]
       mov      r8, qword ptr [rbp-0x30]
       mov      r9, qword ptr [rbp-0x38]
       call     [SharpLink.Server.SharpLinkServer:InvokeServiceAfterAcquisitionAsync(System.Threading.Tasks.ValueTask`1[SharpLink.Server.ServiceLease],SharpLink.Abstractions.IRpcStub,SharpLink.Runtime.RpcSession,SharpLink.Abstractions.IRpcGeneratedServerBridge,long,long,System.Buffers.ReadOnlySequence`1[byte],SharpLink.Abstractions.IRpcByteBufferWriter,System.Threading.CancellationToken,SharpLink.Abstractions.SharpLinkCallContextSnapshot,bool,System.Nullable`1[SharpLink.Abstractions.RpcMethodDescriptor]):System.Threading.Tasks.ValueTask:this]
       mov      gword ptr [rbp-0x260], rax
       mov      qword ptr [rbp-0x258], rdx
       mov      rax, gword ptr [rbp-0x260]
       mov      rdx, qword ptr [rbp-0x258]
 
G_M000_IG25:                ;; offset=0x0551
       vzeroupper 
       add      rsp, 808
       pop      rbx
       pop      r12
       pop      r13
       pop      r14
       pop      r15
       pop      rbp
       ret      
 
G_M000_IG26:                ;; offset=0x0566
       mov      esi, dword ptr [rax+0x34]
       and      esi, 0x1600000
       cmp      esi, 0x1000000
       jne      G_M000_IG24
 
G_M000_IG27:                ;; offset=0x057B
       mov      rbx, gword ptr [rbx+0x38]
       mov      rax, gword ptr [rbp-0xB0]
       mov      gword ptr [rbp-0x2A8], rax
       test     rax, rax
       je       SHORT G_M000_IG31
       mov      rsi, rax
       mov      rdi, 0x7F0716ED49B8
       call     CORINFO_HELP_ISINSTANCEOFCLASS
       mov      gword ptr [rbp-0x2B0], rax
       test     rax, rax
       jne      SHORT G_M000_IG28
       lea      rsi, [rbp-0x190]
       mov      rdi, gword ptr [rbp-0x2A8]
       movsx    rdx, word  ptr [rbp-0xA8]
       mov      r11, 0x7F0714C20928
       call     [r11]System.Threading.Tasks.Sources.IValueTaskSource`1[SharpLink.Server.ServiceLease]:GetResult(short):SharpLink.Server.ServiceLease:this
       jmp      SHORT G_M000_IG32
 
G_M000_IG28:                ;; offset=0x05D5
       mov      edi, dword ptr [rax+0x34]
       and      edi, 0x11000000
       cmp      edi, 0x1000000
       jne      G_M000_IG41
 
G_M000_IG29:                ;; offset=0x05EA
       vmovdqu  ymm0, ymmword ptr [rax+0x38]
       vmovdqu  ymmword ptr [rbp-0x190], ymm0
       mov      rcx, qword ptr [rax+0x58]
       mov      qword ptr [rbp-0x170], rcx
 
G_M000_IG30:                ;; offset=0x0602
       jmp      SHORT G_M000_IG32
 
G_M000_IG31:                ;; offset=0x0604
       vmovdqu32 ymm0, ymmword ptr [rbp-0xA0]
       vmovdqu  ymmword ptr [rbp-0x190], ymm0
       mov      rcx, qword ptr [rbp-0x80]
       mov      qword ptr [rbp-0x170], rcx
 
G_M000_IG32:                ;; offset=0x061E
       mov      r8, gword ptr [r15+0x58]
       cmp      gword ptr [rbp-0x190], 0
       jne      SHORT G_M000_IG33
       cmp      gword ptr [rbp-0x178], 0
       je       G_M000_IG36
 
G_M000_IG33:                ;; offset=0x063A
       lea      rdi, [rsp]
       lea      rsi, [rbp-0x190]
       mov      rcx, gword ptr [rsi]
       mov      gword ptr [rsp], rcx
       add      rsi, 8
       add      rdi, 8
       mov      rcx, gword ptr [rsi]
       mov      gword ptr [rsp+0x08], rcx
       add      rsi, 8
       add      rdi, 8
       movsq    
       mov      rcx, gword ptr [rsi]
       mov      gword ptr [rsp+0x18], rcx
       add      rsi, 8
       add      rdi, 8
       movsq    
       lea      rdi, [rsp+0x28]
       lea      rsi, [rbp+0x10]
       mov      rcx, gword ptr [rsi]
       mov      gword ptr [rsp+0x28], rcx
       add      rsi, 8
       add      rdi, 8
       mov      rcx, gword ptr [rsi]
       mov      gword ptr [rsp+0x30], rcx
       add      rsi, 8
       add      rdi, 8
       movsq    
       mov      gword ptr [rsp+0x40], r13
       mov      gword ptr [rsp+0x48], r12
       mov      gword ptr [rsp+0x50], r14
       mov      edi, dword ptr [rbp-0x3C]
       mov      dword ptr [rsp+0x58], edi
       vmovdqu  xmm0, xmmword ptr [rbp-0x68]
       vmovdqu  xmmword ptr [rsp+0x60], xmm0
       vmovdqu  xmm0, xmmword ptr [rbp-0x58]
       vmovdqu  xmmword ptr [rsp+0x70], xmm0
       mov      rdi, qword ptr [rbp-0x48]
       mov      qword ptr [rsp+0x80], rdi
       mov      rdi, gword ptr [rbp-0x278]
       mov      rsi, rbx
       mov      rdx, gword ptr [rbp-0x288]
       mov      rcx, r8
       mov      r8, qword ptr [rbp-0x30]
       mov      r9, qword ptr [rbp-0x38]
       call     [SharpLink.Server.SharpLinkServer:InvokeServiceWithLeaseAsync(SharpLink.Abstractions.IRpcStub,SharpLink.Server.ServiceLease,SharpLink.Runtime.RpcSession,SharpLink.Abstractions.IRpcGeneratedServerBridge,long,long,System.Buffers.ReadOnlySequence`1[byte],SharpLink.Abstractions.IRpcByteBufferWriter,System.Threading.CancellationToken,SharpLink.Abstractions.SharpLinkCallContextSnapshot,bool,System.Nullable`1[SharpLink.Abstractions.RpcMethodDescriptor]):System.Threading.Tasks.ValueTask:this]
       mov      gword ptr [rbp-0x240], rax
       mov      qword ptr [rbp-0x238], rdx
 
G_M000_IG34:                ;; offset=0x070B
       vmovdqu32 xmm0, xmmword ptr [rbp-0x240]
       vmovdqu32 xmmword ptr [rbp-0x230], xmm0
 
G_M000_IG35:                ;; offset=0x0719
       jmp      G_M000_IG38
 
G_M000_IG36:                ;; offset=0x071E
       mov      r9, qword ptr [rbp-0x38]
       mov      qword ptr [rsp], r9
       lea      rdi, [rsp+0x08]
       lea      rsi, [rbp+0x10]
       mov      rcx, gword ptr [rsi]
       mov      gword ptr [rsp+0x08], rcx
       add      rsi, 8
       add      rdi, 8
       mov      rcx, gword ptr [rsi]
       mov      gword ptr [rsp+0x10], rcx
       add      rsi, 8
       add      rdi, 8
       movsq    
       mov      gword ptr [rsp+0x20], r13
       mov      gword ptr [rsp+0x28], r12
       mov      gword ptr [rsp+0x30], r14
       vmovdqu  xmm0, xmmword ptr [rbp-0x68]
       vmovdqu  xmmword ptr [rsp+0x38], xmm0
       vmovdqu  xmm0, xmmword ptr [rbp-0x58]
       vmovdqu  xmmword ptr [rsp+0x48], xmm0
       mov      rdi, qword ptr [rbp-0x48]
       mov      qword ptr [rsp+0x58], rdi
       mov      rdi, gword ptr [rbp-0x278]
       mov      rsi, rbx
       mov      rdx, gword ptr [rbp-0x188]
       mov      rcx, gword ptr [rbp-0x288]
       mov      r9, qword ptr [rbp-0x30]
       call     [SharpLink.Server.SharpLinkServer:InvokeServiceTrackedAsync(SharpLink.Abstractions.IRpcStub,System.Object,SharpLink.Runtime.RpcSession,SharpLink.Abstractions.IRpcGeneratedServerBridge,long,long,System.Buffers.ReadOnlySequence`1[byte],SharpLink.Abstractions.IRpcByteBufferWriter,System.Threading.CancellationToken,SharpLink.Abstractions.SharpLinkCallContextSnapshot,System.Nullable`1[SharpLink.Abstractions.RpcMethodDescriptor]):System.Threading.Tasks.ValueTask:this]
       mov      gword ptr [rbp-0x250], rax
       mov      qword ptr [rbp-0x248], rdx
 
G_M000_IG37:                ;; offset=0x07AF
       vmovdqu32 xmm0, xmmword ptr [rbp-0x250]
       vmovdqu32 xmmword ptr [rbp-0x230], xmm0
 
G_M000_IG38:                ;; offset=0x07BD
       mov      rax, gword ptr [rbp-0x230]
       mov      rdx, qword ptr [rbp-0x228]
 
G_M000_IG39:                ;; offset=0x07CB
       vzeroupper 
       add      rsp, 808
       pop      rbx
       pop      r12
       pop      r13
       pop      r14
       pop      r15
       pop      rbp
       ret      
 
G_M000_IG40:                ;; offset=0x07E0
       mov      rdi, rbx
       call     [System.ThrowHelper:ThrowObjectDisposedException(System.Object)]
       int3     
 
G_M000_IG41:                ;; offset=0x07EA
       mov      rdi, rax
       xor      esi, esi
       call     [System.Runtime.CompilerServices.TaskAwaiter:HandleNonSuccessAndDebuggerNotification(System.Threading.Tasks.Task,int)]
       mov      rax, gword ptr [rbp-0x2B0]
       jmp      G_M000_IG29
 
G_M000_IG42:                ;; offset=0x0801
       sub      rsp, 152
 
G_M000_IG43:                ;; offset=0x0808
       mov      r15, rdi
       vmovdqu  ymm0, ymmword ptr [rbp-0x68]
       vmovdqu  ymmword ptr [rbp-0xD8], ymm0
       mov      rdi, qword ptr [rbp-0x48]
       mov      qword ptr [rbp-0xB8], rdi
       lea      rdi, [rbp-0xD8]
       call     [System.Nullable`1[SharpLink.Abstractions.RpcMethodDescriptor]:get_HasValue():bool:this]
       test     eax, eax
       jne      SHORT G_M000_IG44
       mov      rdi, gword ptr [rbp-0x280]
       call     [SharpLink.Server.ServiceRegistration:get_Stub():SharpLink.Abstractions.IRpcStub:this]
       mov      rsi, rax
       lea      rdi, [rbp-0x1E0]
       mov      rdx, qword ptr [rbp-0x30]
       call     [SharpLink.Server.SharpLinkServer:GetMethodDescriptor(SharpLink.Abstractions.IRpcStub,long):SharpLink.Abstractions.RpcMethodDescriptor]
       jmp      SHORT G_M000_IG45
 
G_M000_IG44:                ;; offset=0x0857
       lea      rdi, [rbp-0xD8]
       lea      rsi, [rbp-0x1E0]
       call     [System.Nullable`1[SharpLink.Abstractions.RpcMethodDescriptor]:GetValueOrDefault():SharpLink.Abstractions.RpcMethodDescriptor:this]
 
G_M000_IG45:                ;; offset=0x086B
       vmovdqu32 xmm0, xmmword ptr [rbp-0x1E0]
       vmovdqu  xmmword ptr [rsp], xmm0
       vmovdqu32 xmm0, xmmword ptr [rbp-0x1D0]
       vmovdqu  xmmword ptr [rsp+0x10], xmm0
       lea      rsi, [rbp-0x128]
       mov      rdi, gword ptr [rbp-0x278]
       mov      rdx, qword ptr [rbp-0x38]
       call     [SharpLink.Server.SharpLinkServer:StartServerTelemetryCall(SharpLink.Abstractions.RpcMethodDescriptor,long):SharpLink.Abstractions.SharpLinkTelemetry+CallScope:this]
       lea      rdi, [rbp-0x128]
       mov      rsi, r15
       call     [SharpLink.Abstractions.SharpLinkTelemetry+CallScope:Complete(System.Exception):this]
       mov      rdi, r15
       call     [System.Threading.Tasks.ValueTask:FromException(System.Exception):System.Threading.Tasks.ValueTask]
       mov      gword ptr [rbp-0x1F0], rax
       mov      qword ptr [rbp-0x1E8], rdx
       mov      edi, dword ptr [rbp-0x3C]
       mov      dword ptr [rsp], edi
       mov      rdi, gword ptr [rbp-0x1F0]
       mov      rsi, qword ptr [rbp-0x1E8]
       mov      rdx, gword ptr [rbp-0x78]
       mov      rcx, qword ptr [rbp-0x70]
       mov      r8, gword ptr [rbp-0x288]
       mov      r9, qword ptr [rbp-0x38]
       call     [SharpLink.Server.SharpLinkServer:CompleteDynamicSingletonInvocationAsync(System.Threading.Tasks.ValueTask,SharpLink.Runtime.SharpLinkDynamicModuleLease,SharpLink.Runtime.RpcSession,long,bool):System.Threading.Tasks.ValueTask]
       mov      gword ptr [rbp-0xE8], rax
       mov      qword ptr [rbp-0xE0], rdx
       lea      rax, G_M000_IG20
 
G_M000_IG46:                ;; offset=0x0905
       vzeroupper 
       add      rsp, 152
       ret      
 
G_M000_IG47:                ;; offset=0x0910
       sub      rsp, 152
 
G_M000_IG48:                ;; offset=0x0917
       mov      r15, rdi
       vmovdqu  ymm0, ymmword ptr [rbp-0x68]
       vmovdqu  ymmword ptr [rbp-0xD8], ymm0
       mov      rdi, qword ptr [rbp-0x48]
       mov      qword ptr [rbp-0xB8], rdi
       lea      rdi, [rbp-0xD8]
       call     [System.Nullable`1[SharpLink.Abstractions.RpcMethodDescriptor]:get_HasValue():bool:this]
       test     eax, eax
       jne      SHORT G_M000_IG49
       mov      rdi, gword ptr [rbp-0x280]
       call     [SharpLink.Server.ServiceRegistration:get_Stub():SharpLink.Abstractions.IRpcStub:this]
       mov      rsi, rax
       lea      rdi, [rbp-0x1B0]
       mov      rdx, qword ptr [rbp-0x30]
       call     [SharpLink.Server.SharpLinkServer:GetMethodDescriptor(SharpLink.Abstractions.IRpcStub,long):SharpLink.Abstractions.RpcMethodDescriptor]
       jmp      SHORT G_M000_IG50
 
G_M000_IG49:                ;; offset=0x0966
       lea      rdi, [rbp-0xD8]
       lea      rsi, [rbp-0x1B0]
       call     [System.Nullable`1[SharpLink.Abstractions.RpcMethodDescriptor]:GetValueOrDefault():SharpLink.Abstractions.RpcMethodDescriptor:this]
 
G_M000_IG50:                ;; offset=0x097A
       vmovdqu32 xmm0, xmmword ptr [rbp-0x1B0]
       vmovdqu  xmmword ptr [rsp], xmm0
       vmovdqu32 xmm0, xmmword ptr [rbp-0x1A0]
       vmovdqu  xmmword ptr [rsp+0x10], xmm0
       lea      rsi, [rbp-0x168]
       mov      rdi, gword ptr [rbp-0x278]
       mov      rdx, qword ptr [rbp-0x38]
       call     [SharpLink.Server.SharpLinkServer:StartServerTelemetryCall(SharpLink.Abstractions.RpcMethodDescriptor,long):SharpLink.Abstractions.SharpLinkTelemetry+CallScope:this]
       lea      rdi, [rbp-0x168]
       mov      rsi, r15
       call     [SharpLink.Abstractions.SharpLinkTelemetry+CallScope:Complete(System.Exception):this]
       call     CORINFO_HELP_RETHROW
       int3     
 
; Total bytes of code 2497

; Assembly listing for method SharpLink.Server.SharpLinkServer:InvokeServiceTrackedAsync(SharpLink.Abstractions.IRpcStub,System.Object,SharpLink.Runtime.RpcSession,SharpLink.Abstractions.IRpcGeneratedServerBridge,long,long,System.Buffers.ReadOnlySequence`1[byte],SharpLink.Abstractions.IRpcByteBufferWriter,System.Threading.CancellationToken,SharpLink.Abstractions.SharpLinkCallContextSnapshot,System.Nullable`1[SharpLink.Abstractions.RpcMethodDescriptor]):System.Threading.Tasks.ValueTask:this (FullOpts)
; Emitting BLENDED_CODE for generic X64 + VEX + EVEX on Unix
; FullOpts code
; optimized code
; rbp based frame
; fully interruptible
; No PGO data
; 1 inlinees with PGO data; 10 single block inlinees; 1 inlinees without PGO data

G_M000_IG01:                ;; offset=0x0000
       push     rbp
       push     r15
       push     r14
       push     r13
       push     r12
       push     rbx
       sub      rsp, 376
       lea      rbp, [rsp+0x1A0]
       vxorps   xmm8, xmm8, xmm8
       vmovdqu32 zmmword ptr [rbp-0x160], zmm8
       vmovdqu32 zmmword ptr [rbp-0x120], zmm8
       vmovdqu32 zmmword ptr [rbp-0xE0], zmm8
       vmovdqu32 zmmword ptr [rbp-0xA0], zmm8
       vmovdqu32 zmmword ptr [rbp-0x70], zmm8
       xor      eax, eax
       mov      qword ptr [rbp-0x30], rax
       mov      gword ptr [rbp-0x168], r8
       mov      rbx, rdi
       mov      r15, rsi
       mov      r13, rdx
       mov      r12, rcx
       mov      r14, r9
 
G_M000_IG02:                ;; offset=0x006C
       movzx    rdi, byte  ptr [rbp+0x48]
       test     edi, edi
       je       SHORT G_M000_IG04
 
G_M000_IG03:                ;; offset=0x0075
       vmovdqu  ymm0, ymmword ptr [rbp+0x50]
       vmovdqu  ymmword ptr [rbp-0xA8], ymm0
       jmp      SHORT G_M000_IG05
 
G_M000_IG04:                ;; offset=0x0084
       lea      rdi, [rbp-0xA8]
       mov      rsi, r15
       mov      rdx, r14
       call     [SharpLink.Server.SharpLinkServer:GetMethodDescriptor(SharpLink.Abstractions.IRpcStub,long):SharpLink.Abstractions.RpcMethodDescriptor]
 
G_M000_IG05:                ;; offset=0x0097
       mov      rdi, rbx
       call     [SharpLink.Server.SharpLinkServer:CaptureTelemetryDetailGeneration():SharpLink.Abstractions.SharpLinkTelemetryDetailGeneration:this]
       movzx    rdi, byte  ptr [rax+0x10]
       xor      eax, eax
       cmp      edi, 1
       cmove    rax, qword ptr [rbp+0x10]
       mov      qword ptr [rbp-0xB0], rax
       test     byte  ptr [(reloc 0x7f0716ed3880)], 1
       je       G_M000_IG20
       mov      rax, qword ptr [rbp-0xB0]
 
G_M000_IG06:                ;; offset=0x00CA
       mov      rsi, 0x7F022A401908
       mov      rsi, gword ptr [rsi]
       vmovdqu  xmm0, xmmword ptr [rbp-0xA8]
       vmovdqu  xmmword ptr [rsp], xmm0
       vmovdqu  xmm0, xmmword ptr [rbp-0x98]
       vmovdqu  xmmword ptr [rsp+0x10], xmm0
       lea      rdi, [rbp-0x68]
       mov      r8, rax
       mov      edx, 1
       mov      rcx, 0x7F0790608390
       call     [SharpLink.Abstractions.SharpLinkTelemetry:StartCall(System.Diagnostics.ActivitySource,int,System.String,SharpLink.Abstractions.RpcMethodDescriptor,long):SharpLink.Abstractions.SharpLinkTelemetry+CallScope]
       nop      
 
G_M000_IG07:                ;; offset=0x010F
       mov      rcx, qword ptr [rbp+0x10]
       mov      qword ptr [rsp], rcx
       lea      rdi, [rsp+0x08]
       lea      rsi, [rbp+0x18]
       mov      rcx, gword ptr [rsi]
       mov      gword ptr [rsp+0x08], rcx
       add      rsi, 8
       add      rdi, 8
       mov      rcx, gword ptr [rsi]
       mov      gword ptr [rsp+0x10], rcx
       add      rsi, 8
       add      rdi, 8
       movsq    
       mov      rdi, gword ptr [rbp+0x30]
       mov      gword ptr [rsp+0x20], rdi
       mov      rdi, gword ptr [rbp+0x38]
       mov      gword ptr [rsp+0x28], rdi
       mov      rdi, gword ptr [rbp+0x40]
       mov      gword ptr [rsp+0x30], rdi
       mov      rdi, rbx
       mov      rsi, r15
       mov      rdx, r13
       mov      rcx, r12
       mov      r8, gword ptr [rbp-0x168]
       mov      r9, r14
       call     [SharpLink.Server.SharpLinkServer:InvokeServiceCoreAsync(SharpLink.Abstractions.IRpcStub,System.Object,SharpLink.Runtime.RpcSession,SharpLink.Abstractions.IRpcGeneratedServerBridge,long,long,System.Buffers.ReadOnlySequence`1[byte],SharpLink.Abstractions.IRpcByteBufferWriter,System.Threading.CancellationToken,SharpLink.Abstractions.SharpLinkCallContextSnapshot):System.Threading.Tasks.ValueTask:this]
       mov      gword ptr [rbp-0x78], rax
       mov      qword ptr [rbp-0x70], rdx
       cmp      gword ptr [rbp-0x68], 0
       je       SHORT G_M000_IG10
 
G_M000_IG08:                ;; offset=0x0188
       cmp      gword ptr [rbp-0x78], 0
       jne      SHORT G_M000_IG11
 
G_M000_IG09:                ;; offset=0x018F
       lea      rdi, [rbp-0x68]
       xor      rsi, rsi
       call     [SharpLink.Abstractions.SharpLinkTelemetry+CallScope:Complete(System.Exception):this]
 
G_M000_IG10:                ;; offset=0x019B
       mov      rdi, gword ptr [rbp-0x78]
       mov      gword ptr [rbp-0x88], rdi
       movsx    rdi, word  ptr [rbp-0x70]
       mov      word  ptr [rbp-0x80], di
       movzx    rdi, byte  ptr [rbp-0x6E]
       mov      byte  ptr [rbp-0x7E], dil
       jmp      G_M000_IG18
 
G_M000_IG11:                ;; offset=0x01BD
       mov      rsi, gword ptr [rbp-0x78]
       mov      rdi, 0x7F0715DE8A90
       call     CORINFO_HELP_ISINSTANCEOFCLASS
       test     rax, rax
       je       SHORT G_M000_IG12
       mov      esi, dword ptr [rax+0x34]
       and      esi, 0x1600000
       cmp      esi, 0x1000000
       sete     bl
       movzx    rbx, bl
       jmp      SHORT G_M000_IG13
 
G_M000_IG12:                ;; offset=0x01EC
       movsx    rsi, word  ptr [rbp-0x70]
       mov      rdi, gword ptr [rbp-0x78]
       mov      r11, 0x7F0714C20930
       call     [r11]System.Threading.Tasks.Sources.IValueTaskSource:GetStatus(short):int:this
       cmp      eax, 1
       sete     bl
       movzx    rbx, bl
 
G_M000_IG13:                ;; offset=0x020B
       test     ebx, ebx
       jne      SHORT G_M000_IG09
 
G_M000_IG14:                ;; offset=0x020F
       vmovdqu32 zmm0, zmmword ptr [rbp-0x68]
       vmovdqu32 zmmword ptr [rbp-0x160], zmm0
 
G_M000_IG15:                ;; offset=0x0223
       xor      edi, edi
       mov      qword ptr [rbp-0x118], rdi
       mov      rdi, gword ptr [rbp-0x78]
       mov      gword ptr [rbp-0x110], rdi
       movsx    rdi, word  ptr [rbp-0x70]
       mov      word  ptr [rbp-0x108], di
       movzx    rdi, byte  ptr [rbp-0x6E]
       mov      byte  ptr [rbp-0x106], dil
 
G_M000_IG16:                ;; offset=0x024F
       vmovdqu32 zmm0, zmmword ptr [rbp-0x160]
       vmovdqu32 zmmword ptr [rbp-0x100], zmm0
 
G_M000_IG17:                ;; offset=0x0260
       mov      dword ptr [rbp-0x120], -1
       lea      rdi, [rbp-0x120]
       call     [System.Runtime.CompilerServices.AsyncMethodBuilderCore:Start[SharpLink.Server.SharpLinkServer+<ObserveServerCallAsync>d__297](byref)]
       lea      rdi, [rbp-0x118]
       call     [System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder:get_Task():System.Threading.Tasks.ValueTask:this]
       mov      gword ptr [rbp-0x88], rax
       mov      qword ptr [rbp-0x80], rdx
 
G_M000_IG18:                ;; offset=0x028F
       mov      rax, gword ptr [rbp-0x88]
       mov      rdx, qword ptr [rbp-0x80]
 
G_M000_IG19:                ;; offset=0x029A
       vzeroupper 
       add      rsp, 376
       pop      rbx
       pop      r12
       pop      r13
       pop      r14
       pop      r15
       pop      rbp
       ret      
 
G_M000_IG20:                ;; offset=0x02AF
       mov      rdi, 0x7F0716ED3818
       call     CORINFO_HELP_GET_NONGCSTATIC_BASE
       mov      rax, qword ptr [rbp-0xB0]
       jmp      G_M000_IG06
 
G_M000_IG21:                ;; offset=0x02CA
       sub      rsp, 56
 
G_M000_IG22:                ;; offset=0x02CE
       mov      rsi, rdi
       lea      rdi, [rbp-0x68]
       call     [SharpLink.Abstractions.SharpLinkTelemetry+CallScope:Complete(System.Exception):this]
       call     CORINFO_HELP_RETHROW
       int3     
 
; Total bytes of code 737

