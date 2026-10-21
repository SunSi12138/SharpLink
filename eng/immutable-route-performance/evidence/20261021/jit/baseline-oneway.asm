; Assembly listing for method SharpLink.Client.SharpLinkClient+<InvokeOneWayPlainCoreAsync>d__176`1[SharpLink.Abstractions.RpcEmptyRequest]:MoveNext():this (FullOpts)
; Emitting BLENDED_CODE for generic X64 + VEX + EVEX on Unix
; FullOpts code
; optimized code
; rbp based frame
; fully interruptible
; No PGO data
; 8 inlinees with PGO data; 57 single block inlinees; 2 inlinees without PGO data

G_M000_IG01:                ;; offset=0x0000
       push     rbp
       push     r15
       push     r14
       push     r13
       push     rbx
       sub      rsp, 480
       lea      rbp, [rsp+0x200]
       vxorps   xmm8, xmm8, xmm8
       vmovdqu32 zmmword ptr [rbp-0x190], zmm8
       vmovdqu32 zmmword ptr [rbp-0x150], zmm8
       vmovdqu32 zmmword ptr [rbp-0x110], zmm8
       vmovdqu32 zmmword ptr [rbp-0xD0], zmm8
       vmovdqu32 zmmword ptr [rbp-0x90], zmm8
       vmovdqu  ymmword ptr [rbp-0x50], ymm8
       xor      ebx, ebx
       mov      qword ptr [rbp-0x30], rbx
       mov      bword ptr [rbp-0x198], rdi
       mov      rdx, rdi
       xor      r15d, r15d
 
G_M000_IG02:                ;; offset=0x0066
       mov      edi, dword ptr [rdx+0x20]
       mov      dword ptr [rbp-0x24], edi
       mov      r14, gword ptr [rdx]
 
G_M000_IG03:                ;; offset=0x006F
       cmp      dword ptr [rbp-0x24], 1
       jbe      G_M000_IG13
       cmp      gword ptr [r14+0x108], 0
       jne      SHORT G_M000_IG04
       xor      rsi, rsi
       jmp      SHORT G_M000_IG05
 
G_M000_IG04:                ;; offset=0x0087
       mov      rdi, 0x7F7F350F1E20
       call     CORINFO_HELP_NEWSFAST
       mov      rbx, rax
       mov      rdx, bword ptr [rbp-0x198]
       vmovdqu  ymm0, ymmword ptr [rdx+0x30]
       vmovdqu32 ymmword ptr [rbp-0xA0], ymm0
       lea      rdi, bword ptr [rbx+0x08]
       mov      rsi, r14
       call     CORINFO_HELP_ASSIGN_REF
       vmovdqu32 ymm0, ymmword ptr [rbp-0xA0]
       vmovdqu  ymmword ptr [rbx+0x38], ymm0
       mov      rdi, gword ptr [rbx+0x08]
       mov      rdi, gword ptr [rdi+0x50]
       mov      rdi, gword ptr [rdi+0x40]
       mov      rax, qword ptr [rdi]
       mov      rax, qword ptr [rax+0x40]
       call     [rax+0x38]System.TimeProvider:GetTimestamp():long:this
       mov      qword ptr [rbx+0x18], rax
       mov      rdi, 0x7F7A46401558
       mov      rdi, gword ptr [rdi]
       mov      esi, 1
       call     [SharpLink.Abstractions.SharpLinkTelemetryObserverIsolation:Add(System.Diagnostics.Metrics.Counter`1[long],long)]
       mov      rsi, rbx
 
G_M000_IG05:                ;; offset=0x00F9
       mov      rdx, bword ptr [rbp-0x198]
       lea      rdi, bword ptr [rdx+0x10]
       call     CORINFO_HELP_CHECKED_ASSIGN_REF
       mov      rdx, bword ptr [rbp-0x198]
       cmp      gword ptr [rdx+0x10], 0
       jne      SHORT G_M000_IG06
       mov      rdi, 0x7F7A46401558
       mov      rdi, gword ptr [rdi]
       mov      esi, 1
       call     [SharpLink.Abstractions.SharpLinkTelemetryObserverIsolation:Add(System.Diagnostics.Metrics.Counter`1[long],long)]
 
G_M000_IG06:                ;; offset=0x012F
       xor      rdi, rdi
       mov      rdx, bword ptr [rbp-0x198]
       mov      gword ptr [rdx+0x18], rdi
 
G_M000_IG07:                ;; offset=0x013C
       mov      dword ptr [rbp-0x28], edi
 
G_M000_IG08:                ;; offset=0x013F
       lea      rbx, bword ptr [rdx+0x50]
       mov      rdi, rbx
       call     [SharpLink.Client.SharpLinkClient:EnsureLogicalCallProgress(byref)]
       mov      rdx, bword ptr [rbp-0x198]
       lea      rdi, bword ptr [rdx+0x30]
       vmovdqu  xmm0, xmmword ptr [rdi]
       vmovdqu  xmmword ptr [rsp], xmm0
       vmovdqu  xmm0, xmmword ptr [rdi+0x10]
       vmovdqu  xmmword ptr [rsp+0x10], xmm0
       mov      rdx, gword ptr [rdx+0x10]
       mov      rdi, r14
       xor      rsi, rsi
       call     [SharpLink.Client.SharpLinkClient:GetReadyConnection(SharpLink.Abstractions.RpcMethodDescriptor,SharpLink.Client.SharpLinkClient+EndpointRetrySelectionState,SharpLink.Client.SharpLinkClient+AttemptOutcomeState):SharpLink.Client.ClientConnection:this]
       mov      rdx, bword ptr [rbp-0x198]
       lea      rdi, bword ptr [rdx+0x18]
       mov      rsi, rax
       call     CORINFO_HELP_CHECKED_ASSIGN_REF
       mov      dword ptr [rbp-0x28], 1
       mov      rdi, rbx
       call     [SharpLink.Client.SharpLinkClient:EnsureLogicalCallProgress(byref)]
       nop      
 
G_M000_IG09:                ;; offset=0x019E
       mov      r15d, 64
       mov      edi, 96
       mov      rdx, bword ptr [rbp-0x198]
       cmp      byte  ptr [rdx+0x91], 0
       cmovne   r15d, edi
 
G_M000_IG10:                ;; offset=0x01BB
       mov      rdi, rbx
       call     [SharpLink.Client.SharpLinkClient:EnsureLogicalCallProgress(byref)]
       mov      rdx, bword ptr [rbp-0x198]
       mov      rdi, gword ptr [rdx+0x18]
       mov      rdi, gword ptr [rdi+0x40]
       cmp      dword ptr [rdi], edi
       call     [SharpLink.Client.PendingRequestTable:AllocateRequestId():long:this]
       mov      r13, rax
       jmp      G_M000_IG60
 
G_M000_IG11:                ;; offset=0x01E3
       mov      rdx, bword ptr [rbp-0x198]
       mov      rdi, gword ptr [rdx+0x18]
       cmp      dword ptr [rdi], edi
       call     [SharpLink.Client.ClientConnection:TryBeginUntrackedCall():bool:this]
       test     eax, eax
       je       G_M000_IG64
 
G_M000_IG12:                ;; offset=0x01FE
       mov      ebx, r15d
       mov      r15, r13
 
G_M000_IG13:                ;; offset=0x0204
       cmp      dword ptr [rbp-0x24], 0
       jne      SHORT G_M000_IG18
       mov      rdx, bword ptr [rbp-0x198]
       lea      rdi, bword ptr [rdx+0xB0]
 
G_M000_IG14:                ;; offset=0x0218
       vmovdqu  xmm0, xmmword ptr [rdi]
       vmovdqu  xmmword ptr [rbp-0x48], xmm0
 
G_M000_IG15:                ;; offset=0x0221
       xor      esi, esi
       mov      qword ptr [rdi], rsi
       mov      qword ptr [rdi+0x08], rsi
       mov      dword ptr [rbp-0x24], -1
       mov      dword ptr [rdx+0x20], -1
 
G_M000_IG16:                ;; offset=0x0238
       mov      rbx, gword ptr [rbp-0x48]
       test     rbx, rbx
       jne      G_M000_IG44
 
G_M000_IG17:                ;; offset=0x0245
       mov      rdx, bword ptr [rbp-0x198]
       mov      rdi, gword ptr [rdx+0x10]
       test     rdi, rdi
       je       G_M000_IG58
       mov      esi, 6
       xor      rdx, rdx
       call     [SharpLink.Client.SharpLinkClient+AttemptOutcomeState:Report(byte,System.Exception):this]
       jmp      G_M000_IG58
 
G_M000_IG18:                ;; offset=0x026B
       cmp      dword ptr [rbp-0x24], 1
       jne      G_M000_IG25
       mov      rdx, bword ptr [rbp-0x198]
       lea      rdi, bword ptr [rdx+0xB0]
 
G_M000_IG19:                ;; offset=0x0283
       vmovdqu  xmm0, xmmword ptr [rdi]
       vmovdqu  xmmword ptr [rbp-0x48], xmm0
 
G_M000_IG20:                ;; offset=0x028C
       xor      esi, esi
       mov      qword ptr [rdi], rsi
       mov      qword ptr [rdi+0x08], rsi
       mov      dword ptr [rbp-0x24], -1
       mov      dword ptr [rdx+0x20], -1
 
G_M000_IG21:                ;; offset=0x02A3
       mov      r15, gword ptr [rbp-0x48]
       test     r15, r15
       je       SHORT G_M000_IG17
 
G_M000_IG22:                ;; offset=0x02AC
       mov      rsi, r15
       mov      rdi, 0x7F7F33AD5A88
       call     CORINFO_HELP_ISINSTANCEOFCLASS
       test     rax, rax
       jne      G_M000_IG54
 
G_M000_IG23:                ;; offset=0x02C7
       mov      rdi, 0x7F7F34651640
       cmp      qword ptr [r15], rdi
       je       G_M000_IG56
 
G_M000_IG24:                ;; offset=0x02DA
       mov      rdi, r15
       movsx    rsi, word  ptr [rbp-0x40]
       mov      r11, 0x7F7F329F19B0
       call     [r11]System.Threading.Tasks.Sources.IValueTaskSource:GetResult(short):this
       jmp      G_M000_IG17
 
G_M000_IG25:                ;; offset=0x02F4
       mov      rdx, bword ptr [rbp-0x198]
       mov      rdi, gword ptr [rdx+0x18]
       mov      rsi, gword ptr [rdi+0x38]
       mov      rcx, qword ptr [rdx+0x30]
       mov      r8, qword ptr [rdx+0x38]
       lea      rdi, bword ptr [rdx+0xA8]
       mov      r9, gword ptr [rdx+0x08]
       vmovdqu  ymm0, ymmword ptr [rdx+0x78]
       vmovdqu  ymmword ptr [rbp-0x80], ymm0
       mov      rax, qword ptr [rdx+0x98]
       mov      qword ptr [rbp-0x60], rax
       mov      rax, gword ptr [rdx+0x50]
       movzx    r10, byte  ptr [rdx+0x91]
       mov      bword ptr [rsp], rdi
       mov      gword ptr [rsp+0x08], r9
       vmovdqu  xmm0, xmmword ptr [rbp-0x80]
       vmovdqu  xmmword ptr [rsp+0x10], xmm0
       vmovdqu  xmm0, xmmword ptr [rbp-0x70]
       vmovdqu  xmmword ptr [rsp+0x20], xmm0
       mov      rdi, qword ptr [rbp-0x60]
       mov      qword ptr [rsp+0x30], rdi
       mov      gword ptr [rsp+0x38], rax
       mov      dword ptr [rsp+0x40], r10d
       xor      rdi, rdi
       mov      gword ptr [rsp+0x48], rdi
 
G_M000_IG26:                ;; offset=0x0370
       mov      gword ptr [rsp+0x50], rdi
 
G_M000_IG27:                ;; offset=0x0375
       mov      gword ptr [rsp+0x58], rdi
       mov      rdi, r14
       mov      rdx, rcx
       mov      rcx, r8
       mov      r8, r15
       mov      r9d, ebx
       call     [SharpLink.Client.SharpLinkClient:SendRpcCall[SharpLink.Abstractions.RpcEmptyRequest](SharpLink.Runtime.RpcSession,long,long,long,byte,byref,SharpLink.Abstractions.IRpcCodec`1[SharpLink.Abstractions.RpcEmptyRequest],SharpLink.Abstractions.RpcDeadline,SharpLink.Sdk.SharpLinkMetadata,bool,System.Threading.CancellationToken,SharpLink.Runtime.IRequestEmissionFailureObserver,SharpLink.Client.PendingRequestTable):System.Threading.Tasks.ValueTask:this]
       mov      gword ptr [rbp-0x38], rax
       mov      qword ptr [rbp-0x30], rdx
       mov      rdx, bword ptr [rbp-0x198]
       cmp      byte  ptr [rdx+0x91], 0
       jne      G_M000_IG33
       movsx    rsi, word  ptr [rbp-0x30]
       mov      rdi, gword ptr [rbp-0x38]
       mov      gword ptr [rbp-0x48], rdi
       mov      word  ptr [rbp-0x40], si
       mov      byte  ptr [rbp-0x3E], 0
       mov      r14, gword ptr [rbp-0x48]
       test     r14, r14
       je       G_M000_IG21
 
G_M000_IG28:                ;; offset=0x03CD
       mov      rsi, r14
       mov      rdi, 0x7F7F33AD5A88
       call     CORINFO_HELP_ISINSTANCEOFCLASS
       test     rax, rax
       jne      G_M000_IG52
 
G_M000_IG29:                ;; offset=0x03E8
       mov      rdi, 0x7F7F34651640
       cmp      qword ptr [r14], rdi
       je       G_M000_IG53
 
G_M000_IG30:                ;; offset=0x03FB
       mov      rdi, r14
       movsx    rsi, word  ptr [rbp-0x40]
       mov      r11, 0x7F7F329F19A8
       call     [r11]System.Threading.Tasks.Sources.IValueTaskSource:GetStatus(short):int:this
 
G_M000_IG31:                ;; offset=0x0410
       test     eax, eax
       jne      G_M000_IG21
 
G_M000_IG32:                ;; offset=0x0418
       mov      dword ptr [rbp-0x24], 1
       mov      rdx, bword ptr [rbp-0x198]
       mov      dword ptr [rdx+0x20], 1
       lea      rdi, bword ptr [rdx+0xB0]
       lea      rsi, bword ptr [rbp-0x48]
       call     CORINFO_HELP_ASSIGN_BYREF
       movsq    
       lea      rsi, bword ptr [rdx+0x28]
       mov      rdi, rdx
       call     [System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Threading.Tasks.VoidTaskResult]:GetStateMachineBox[SharpLink.Client.SharpLinkClient+<InvokeOneWayPlainCoreAsync>d__176`1[SharpLink.Abstractions.RpcEmptyRequest]](byref,byref):System.Runtime.CompilerServices.IAsyncStateMachineBox]
       mov      rsi, rax
       lea      rdi, [rbp-0x48]
       call     [System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Threading.Tasks.VoidTaskResult]:AwaitUnsafeOnCompleted[System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable+ConfiguredValueTaskAwaiter](byref,System.Runtime.CompilerServices.IAsyncStateMachineBox)]
       jmp      G_M000_IG57
 
G_M000_IG33:                ;; offset=0x045E
       mov      rdx, bword ptr [rbp-0x198]
       mov      rdi, gword ptr [rdx+0x18]
       lea      rbx, bword ptr [rdx+0x50]
 
G_M000_IG34:                ;; offset=0x046D
       vmovdqu32 zmm0, zmmword ptr [rbx]
       vmovdqu32 zmmword ptr [rbp-0x190], zmm0
       vmovdqu  xmm0, xmmword ptr [rbx+0x40]
       vmovdqu32 xmmword ptr [rbp-0x150], xmm0
 
G_M000_IG35:                ;; offset=0x0489
       xor      eax, eax
       mov      qword ptr [rbp-0x118], rax
       mov      gword ptr [rbp-0x140], r14
       mov      rax, gword ptr [rbp-0x38]
       mov      gword ptr [rbp-0x110], rax
       movsx    rax, word  ptr [rbp-0x30]
       mov      word  ptr [rbp-0x108], ax
       movzx    rax, byte  ptr [rbp-0x2E]
       mov      byte  ptr [rbp-0x106], al
       mov      gword ptr [rbp-0x138], rdi
       mov      qword ptr [rbp-0x128], r15
 
G_M000_IG36:                ;; offset=0x04C8
       vmovdqu32 zmm0, zmmword ptr [rbp-0x190]
       vmovdqu32 zmmword ptr [rbp-0x100], zmm0
       vmovdqu32 xmm0, xmmword ptr [rbp-0x150]
       vmovdqu32 xmmword ptr [rbp-0xC0], xmm0
 
G_M000_IG37:                ;; offset=0x04E7
       mov      dword ptr [rbp-0x120], -1
       lea      rdi, [rbp-0x140]
       call     [System.Runtime.CompilerServices.AsyncMethodBuilderCore:Start[SharpLink.Client.SharpLinkClient+<AwaitPlainOneWayEmissionOrDeadlineAsync>d__173](byref)]
       lea      rdi, [rbp-0x118]
       call     [System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder:get_Task():System.Threading.Tasks.ValueTask:this]
       mov      gword ptr [rbp-0x58], rax
       mov      qword ptr [rbp-0x50], rdx
       mov      rsi, gword ptr [rbp-0x58]
       movsx    rdi, word  ptr [rbp-0x50]
       mov      gword ptr [rbp-0x48], rsi
       mov      word  ptr [rbp-0x40], di
       mov      byte  ptr [rbp-0x3E], 0
       mov      rbx, gword ptr [rbp-0x48]
       test     rbx, rbx
       je       G_M000_IG16
 
G_M000_IG38:                ;; offset=0x0535
       mov      rsi, rbx
       mov      rdi, 0x7F7F33AD5A88
       call     CORINFO_HELP_ISINSTANCEOFCLASS
       test     rax, rax
       jne      G_M000_IG46
 
G_M000_IG39:                ;; offset=0x0550
       mov      rdi, 0x7F7F34651640
       cmp      qword ptr [rbx], rdi
       je       G_M000_IG47
 
G_M000_IG40:                ;; offset=0x0563
       mov      rdi, rbx
       movsx    rsi, word  ptr [rbp-0x40]
       mov      r11, 0x7F7F329F1998
       call     [r11]System.Threading.Tasks.Sources.IValueTaskSource:GetStatus(short):int:this
 
G_M000_IG41:                ;; offset=0x0578
       test     eax, eax
       jne      G_M000_IG16
 
G_M000_IG42:                ;; offset=0x0580
       xor      edi, edi
       mov      dword ptr [rbp-0x24], edi
 
G_M000_IG43:                ;; offset=0x0585
       mov      rdx, bword ptr [rbp-0x198]
       mov      dword ptr [rdx+0x20], edi
       lea      rdi, bword ptr [rdx+0xB0]
       lea      rsi, bword ptr [rbp-0x48]
       call     CORINFO_HELP_ASSIGN_BYREF
       movsq    
       lea      rsi, bword ptr [rdx+0x28]
       mov      rdi, rdx
       call     [System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Threading.Tasks.VoidTaskResult]:GetStateMachineBox[SharpLink.Client.SharpLinkClient+<InvokeOneWayPlainCoreAsync>d__176`1[SharpLink.Abstractions.RpcEmptyRequest]](byref,byref):System.Runtime.CompilerServices.IAsyncStateMachineBox]
       mov      rsi, rax
       lea      rdi, [rbp-0x48]
       call     [System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Threading.Tasks.VoidTaskResult]:AwaitUnsafeOnCompleted[System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable+ConfiguredValueTaskAwaiter](byref,System.Runtime.CompilerServices.IAsyncStateMachineBox)]
       jmp      G_M000_IG59
 
G_M000_IG44:                ;; offset=0x05C0
       mov      rsi, rbx
       mov      rdi, 0x7F7F33AD5A88
       call     CORINFO_HELP_ISINSTANCEOFCLASS
       test     rax, rax
       jne      SHORT G_M000_IG48
 
G_M000_IG45:                ;; offset=0x05D7
       mov      rdi, 0x7F7F34651640
       cmp      qword ptr [rbx], rdi
       jne      SHORT G_M000_IG51
       jmp      SHORT G_M000_IG50
 
G_M000_IG46:                ;; offset=0x05E8
       test     dword ptr [rax+0x34], 0x1600000
       jne      G_M000_IG16
       jmp      SHORT G_M000_IG42
 
G_M000_IG47:                ;; offset=0x05F7
       movsx    rsi, word  ptr [rbp-0x40]
       lea      rdi, bword ptr [rbx+0x40]
       call     [System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore`1[long]:GetStatus(short):int:this]
       jmp      G_M000_IG41
 
G_M000_IG48:                ;; offset=0x060B
       mov      edi, dword ptr [rax+0x34]
       and      edi, 0x11000000
       cmp      edi, 0x1000000
       je       G_M000_IG17
 
G_M000_IG49:                ;; offset=0x0620
       mov      rdi, rax
       xor      esi, esi
       call     [System.Runtime.CompilerServices.TaskAwaiter:HandleNonSuccessAndDebuggerNotification(System.Threading.Tasks.Task,int)]
       jmp      G_M000_IG17
 
G_M000_IG50:                ;; offset=0x0630
       movsx    rsi, word  ptr [rbp-0x40]
       mov      rdi, rbx
       call     [Microsoft.Win32.SafeHandles.SafeFileHandle+ThreadPoolValueTaskSource:GetResult(short):long:this]
       jmp      G_M000_IG17
 
G_M000_IG51:                ;; offset=0x0643
       mov      rdi, rbx
       movsx    rsi, word  ptr [rbp-0x40]
       mov      r11, 0x7F7F329F19A0
       call     [r11]System.Threading.Tasks.Sources.IValueTaskSource:GetResult(short):this
       jmp      G_M000_IG17
 
G_M000_IG52:                ;; offset=0x065D
       test     dword ptr [rax+0x34], 0x1600000
       jne      G_M000_IG21
       jmp      G_M000_IG32
 
G_M000_IG53:                ;; offset=0x066F
       movsx    rsi, word  ptr [rbp-0x40]
       lea      rdi, bword ptr [r14+0x40]
       call     [System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore`1[long]:GetStatus(short):int:this]
       jmp      G_M000_IG31
 
G_M000_IG54:                ;; offset=0x0683
       mov      edi, dword ptr [rax+0x34]
       and      edi, 0x11000000
       cmp      edi, 0x1000000
       je       G_M000_IG17
 
G_M000_IG55:                ;; offset=0x0698
       mov      rdi, rax
       xor      esi, esi
       call     [System.Runtime.CompilerServices.TaskAwaiter:HandleNonSuccessAndDebuggerNotification(System.Threading.Tasks.Task,int)]
       jmp      G_M000_IG17
 
G_M000_IG56:                ;; offset=0x06A8
       movsx    rsi, word  ptr [rbp-0x40]
       mov      rdi, r15
       call     [Microsoft.Win32.SafeHandles.SafeFileHandle+ThreadPoolValueTaskSource:GetResult(short):long:this]
       jmp      G_M000_IG17
 
G_M000_IG57:                ;; offset=0x06BB
       call     G_M000_IG89
       jmp      G_M000_IG72
 
G_M000_IG58:                ;; offset=0x06C5
       cmp      dword ptr [rbp-0x24], 0
       jge      G_M000_IG70
       mov      rdx, bword ptr [rbp-0x198]
       mov      rdi, gword ptr [rdx+0x18]
       cmp      byte  ptr [rdi], dil
       call     [SharpLink.Client.ClientConnection:ReleaseAuxiliaryActiveCall():this]
       jmp      G_M000_IG70
 
G_M000_IG59:                ;; offset=0x06E8
       call     G_M000_IG89
       jmp      G_M000_IG72
 
G_M000_IG60:                ;; offset=0x06F2
       mov      rdx, bword ptr [rbp-0x198]
       lea      rdi, bword ptr [rdx+0xA0]
       cmp      gword ptr [rdi], 0
       je       SHORT G_M000_IG62
 
G_M000_IG61:                ;; offset=0x0706
       mov      rax, gword ptr [rdi]
       cmp      dword ptr [rax+0x20], 0
       jne      SHORT G_M000_IG63
 
G_M000_IG62:                ;; offset=0x070F
       mov      rdi, rbx
       call     [SharpLink.Client.SharpLinkClient:EnsureLogicalCallProgress(byref)]
       jmp      G_M000_IG11
 
G_M000_IG63:                ;; offset=0x071D
       call     [System.Threading.CancellationToken:ThrowOperationCanceledException():this]
       int3     
 
G_M000_IG64:                ;; offset=0x0724
       mov      rdi, 0x7F7F34D234E0
       call     CORINFO_HELP_NEWSFAST
       mov      r15, rax
       mov      rdi, 0x80131500E0434352
       mov      qword ptr [r15+0x68], rdi
       mov      rdi, 0x7F7FAE4122A0
       mov      gword ptr [r15+0x10], rdi
       mov      edi, 10
       call     [SharpLink.Abstractions.SharpLinkException:ValidateCode(int):int]
       mov      dword ptr [r15+0x70], eax
       mov      word  ptr [r15+0x74], 0
       mov      rdi, rbx
       mov      rsi, r15
       call     [SharpLink.Client.SharpLinkClient:ArbitrateLogicalCallFailure(byref,System.Exception):System.Exception]
       mov      rbx, rax
       mov      rdx, bword ptr [rbp-0x198]
       mov      r15, gword ptr [rdx+0x10]
       test     r15, r15
       je       SHORT G_M000_IG69
       mov      rsi, rbx
       mov      rdi, 0x7F7F34D234E0
       call     CORINFO_HELP_ISINSTANCEOFCLASS
       test     rax, rax
       je       SHORT G_M000_IG65
       cmp      dword ptr [rax+0x70], 13
       je       SHORT G_M000_IG66
 
G_M000_IG65:                ;; offset=0x07A4
       mov      edi, 8
       jmp      SHORT G_M000_IG67
 
G_M000_IG66:                ;; offset=0x07AB
       mov      edi, 4
 
G_M000_IG67:                ;; offset=0x07B0
       movzx    rsi, dil
       cmp      esi, 1
       jg       SHORT G_M000_IG68
       mov      dword ptr [r15+0x30], 1
 
G_M000_IG68:                ;; offset=0x07C1
       mov      rdi, r15
       mov      rdx, rbx
       call     [SharpLink.Client.SharpLinkClient+AttemptOutcomeState:Report(byte,System.Exception):this]
 
G_M000_IG69:                ;; offset=0x07CD
       mov      rdi, rbx
       call     CORINFO_HELP_THROW
       int3     
 
G_M000_IG70:                ;; offset=0x07D6
       mov      rdx, bword ptr [rbp-0x198]
       mov      dword ptr [rdx+0x20], -2
       xor      rdi, rdi
       mov      gword ptr [rdx+0x10], rdi
 
G_M000_IG71:                ;; offset=0x07EA
       mov      gword ptr [rdx+0x18], rdi
       lea      rdi, bword ptr [rdx+0x28]
       call     [System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder:SetResult():this]
 
G_M000_IG72:                ;; offset=0x07F8
       nop      
 
G_M000_IG73:                ;; offset=0x07F9
       vzeroupper 
       add      rsp, 480
       pop      rbx
       pop      r13
       pop      r14
       pop      r15
       pop      rbp
       ret      
 
G_M000_IG74:                ;; offset=0x080C
       sub      rsp, 104
 
G_M000_IG75:                ;; offset=0x0810
       mov      r15, rdi
       cmp      dword ptr [rbp-0x28], 0
       je       SHORT G_M000_IG76
       mov      rdx, bword ptr [rbp-0x198]
       mov      rdi, gword ptr [rdx+0x18]
       cmp      dword ptr [rdi], edi
       call     [SharpLink.Client.ClientConnection:ReleaseCallAdmissionReservation():this]
 
G_M000_IG76:                ;; offset=0x082C
       mov      rdx, bword ptr [rbp-0x198]
       lea      rbx, bword ptr [rdx+0x50]
       mov      rdi, rbx
       mov      rsi, r15
       call     [SharpLink.Client.SharpLinkClient:ArbitrateLogicalCallFailure(byref,System.Exception):System.Exception]
       mov      rbx, rax
       mov      rdx, bword ptr [rbp-0x198]
       mov      rdi, gword ptr [rdx+0x10]
       test     rdi, rdi
       je       SHORT G_M000_IG77
       mov      rsi, rbx
       call     [SharpLink.Client.SharpLinkClient+AttemptOutcomeState:CompleteLocalFailure(System.Exception):this]
 
G_M000_IG77:                ;; offset=0x085F
       mov      rdi, rbx
       call     CORINFO_HELP_THROW
       int3     
 
G_M000_IG78:                ;; offset=0x0868
       sub      rsp, 104
 
G_M000_IG79:                ;; offset=0x086C
       mov      r14, rdi
       mov      rdx, bword ptr [rbp-0x198]
       mov      rdi, gword ptr [rdx+0x18]
       cmp      dword ptr [rdi], edi
       call     [SharpLink.Client.ClientConnection:ReleaseCallAdmissionReservation():this]
       mov      rdx, bword ptr [rbp-0x198]
       lea      rbx, bword ptr [rdx+0x50]
       mov      rdi, rbx
       mov      rsi, r14
       call     [SharpLink.Client.SharpLinkClient:ArbitrateLogicalCallFailure(byref,System.Exception):System.Exception]
       mov      rbx, rax
       mov      rdx, bword ptr [rbp-0x198]
       mov      rdi, gword ptr [rdx+0x10]
       test     rdi, rdi
       je       SHORT G_M000_IG80
       mov      rsi, rbx
       call     [SharpLink.Client.SharpLinkClient+AttemptOutcomeState:CompleteLocalFailure(System.Exception):this]
 
G_M000_IG80:                ;; offset=0x08B5
       mov      rdi, rbx
       call     CORINFO_HELP_THROW
       int3     
 
G_M000_IG81:                ;; offset=0x08BE
       sub      rsp, 104
 
G_M000_IG82:                ;; offset=0x08C2
       mov      rdx, bword ptr [rbp-0x198]
       mov      rdi, gword ptr [rdx+0x18]
       cmp      dword ptr [rdi], edi
       call     [SharpLink.Client.ClientConnection:ReleaseCallAdmissionReservation():this]
       call     CORINFO_HELP_RETHROW
       int3     
 
G_M000_IG83:                ;; offset=0x08DB
       sub      rsp, 104
 
G_M000_IG84:                ;; offset=0x08DF
       mov      rsi, rdi
       mov      rdi, bword ptr [rbp-0x198]
       lea      rbx, bword ptr [rdi+0x50]
       mov      rdi, rbx
       call     [SharpLink.Client.SharpLinkClient:ArbitrateLogicalCallFailure(byref,System.Exception):System.Exception]
       mov      rbx, rax
       mov      rdi, bword ptr [rbp-0x198]
       mov      r15, gword ptr [rdi+0x10]
       test     r15, r15
       je       SHORT G_M000_IG88
       mov      rsi, rbx
       mov      rdi, 0x7F7F34D234E0
       call     CORINFO_HELP_ISINSTANCEOFCLASS
       test     rax, rax
       je       SHORT G_M000_IG85
       mov      rdi, rax
       call     [SharpLink.Abstractions.SharpLinkException:get_Code():int:this]
       cmp      eax, 13
       je       SHORT G_M000_IG86
 
G_M000_IG85:                ;; offset=0x092E
       mov      esi, 7
       jmp      SHORT G_M000_IG87
 
G_M000_IG86:                ;; offset=0x0935
       mov      esi, 4
 
G_M000_IG87:                ;; offset=0x093A
       mov      rdi, r15
       mov      rdx, rbx
       call     [SharpLink.Client.SharpLinkClient+AttemptOutcomeState:CompleteWithoutPending(byte,System.Exception):this]
 
G_M000_IG88:                ;; offset=0x0946
       mov      rdi, rbx
       call     CORINFO_HELP_THROW
       int3     
 
G_M000_IG89:                ;; offset=0x094F
       sub      rsp, 104
 
G_M000_IG90:                ;; offset=0x0953
       cmp      dword ptr [rbp-0x24], 0
       jge      SHORT G_M000_IG91
       mov      rdi, bword ptr [rbp-0x198]
       mov      rax, gword ptr [rdi+0x18]
       cmp      byte  ptr [rax], al
       mov      rdi, rax
       call     [SharpLink.Client.ClientConnection:ReleaseAuxiliaryActiveCall():this]
 
G_M000_IG91:                ;; offset=0x096F
       nop      
 
G_M000_IG92:                ;; offset=0x0970
       vzeroupper 
       add      rsp, 104
       ret      
 
G_M000_IG93:                ;; offset=0x0978
       sub      rsp, 104
 
G_M000_IG94:                ;; offset=0x097C
       mov      rsi, rdi
       mov      rdx, bword ptr [rbp-0x198]
       mov      dword ptr [rdx+0x20], -2
       xor      rdi, rdi
       mov      gword ptr [rdx+0x10], rdi
 
G_M000_IG95:                ;; offset=0x0993
       mov      gword ptr [rdx+0x18], rdi
       lea      rdi, bword ptr [rdx+0x28]
       call     [System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder:SetException(System.Exception):this]
       lea      rax, G_M000_IG72
 
G_M000_IG96:                ;; offset=0x09A8
       vzeroupper 
       add      rsp, 104
       ret      
 
; Total bytes of code 2480

