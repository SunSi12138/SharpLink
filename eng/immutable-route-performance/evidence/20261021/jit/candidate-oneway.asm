; Assembly listing for method SharpLink.Client.SharpLinkClient+<InvokeOneWayPlainCoreAsync>d__190`1[SharpLink.Abstractions.RpcEmptyRequest]:MoveNext():this (FullOpts)
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
       xor      ebx, ebx
       mov      qword ptr [rbp-0x198], rbx
       vxorps   xmm8, xmm8, xmm8
       vmovdqu32 zmmword ptr [rbp-0x190], zmm8
       vmovdqu32 zmmword ptr [rbp-0x150], zmm8
       vmovdqu32 zmmword ptr [rbp-0x110], zmm8
       vmovdqu32 zmmword ptr [rbp-0xD0], zmm8
       vmovdqu32 zmmword ptr [rbp-0x90], zmm8
       vmovdqu  ymmword ptr [rbp-0x50], ymm8
       mov      qword ptr [rbp-0x30], rbx
       mov      bword ptr [rbp-0x1A0], rdi
       mov      rdx, rdi
       xor      r15d, r15d
 
G_M000_IG02:                ;; offset=0x006D
       mov      edi, dword ptr [rdx+0x20]
       mov      dword ptr [rbp-0x24], edi
       mov      r14, gword ptr [rdx]
 
G_M000_IG03:                ;; offset=0x0076
       cmp      dword ptr [rbp-0x24], 1
       jbe      G_M000_IG13
       cmp      gword ptr [r14+0x108], 0
       jne      SHORT G_M000_IG04
       xor      rsi, rsi
       jmp      G_M000_IG05
 
G_M000_IG04:                ;; offset=0x0091
       mov      rdi, 0x7F532B0F70D0
       call     CORINFO_HELP_NEWSFAST
       mov      rbx, rax
       mov      rdx, bword ptr [rbp-0x1A0]
       vmovdqu  ymm0, ymmword ptr [rdx+0x30]
       vmovdqu  ymmword ptr [rbp-0xA8], ymm0
       mov      rdi, qword ptr [rdx+0x50]
       mov      qword ptr [rbp-0x88], rdi
       lea      rdi, bword ptr [rbx+0x08]
       mov      rsi, r14
       call     CORINFO_HELP_ASSIGN_REF
       vmovdqu  ymm0, ymmword ptr [rbp-0xA8]
       vmovdqu  ymmword ptr [rbx+0x38], ymm0
       mov      rdi, qword ptr [rbp-0x88]
       mov      qword ptr [rbx+0x58], rdi
       mov      rdi, gword ptr [rbx+0x08]
       mov      rdi, gword ptr [rdi+0x50]
       mov      rdi, gword ptr [rdi+0x40]
       mov      rax, qword ptr [rdi]
       mov      rax, qword ptr [rax+0x40]
       call     [rax+0x38]System.TimeProvider:GetTimestamp():long:this
       mov      qword ptr [rbx+0x18], rax
       mov      rdi, 0x7F4E3E401578
       mov      rdi, gword ptr [rdi]
       mov      esi, 1
       call     [SharpLink.Abstractions.SharpLinkTelemetryObserverIsolation:Add(System.Diagnostics.Metrics.Counter`1[long],long)]
       mov      rsi, rbx
 
G_M000_IG05:                ;; offset=0x011B
       mov      rdx, bword ptr [rbp-0x1A0]
       lea      rdi, bword ptr [rdx+0x10]
       call     CORINFO_HELP_CHECKED_ASSIGN_REF
       mov      rdx, bword ptr [rbp-0x1A0]
       cmp      gword ptr [rdx+0x10], 0
       jne      SHORT G_M000_IG06
       mov      rdi, 0x7F4E3E401578
       mov      rdi, gword ptr [rdi]
       mov      esi, 1
       call     [SharpLink.Abstractions.SharpLinkTelemetryObserverIsolation:Add(System.Diagnostics.Metrics.Counter`1[long],long)]
 
G_M000_IG06:                ;; offset=0x0151
       xor      rdi, rdi
       mov      rdx, bword ptr [rbp-0x1A0]
       mov      gword ptr [rdx+0x18], rdi
 
G_M000_IG07:                ;; offset=0x015E
       mov      dword ptr [rbp-0x28], edi
 
G_M000_IG08:                ;; offset=0x0161
       lea      rbx, bword ptr [rdx+0x58]
       mov      rdi, rbx
       call     [SharpLink.Client.SharpLinkClient:EnsureLogicalCallProgress(byref)]
       mov      rdx, bword ptr [rbp-0x1A0]
       lea      rdi, bword ptr [rdx+0x30]
       vmovdqu  xmm0, xmmword ptr [rdi]
       vmovdqu  xmmword ptr [rsp], xmm0
       vmovdqu  xmm0, xmmword ptr [rdi+0x10]
       vmovdqu  xmmword ptr [rsp+0x10], xmm0
       mov      rsi, qword ptr [rdi+0x20]
       mov      qword ptr [rsp+0x20], rsi
       mov      rdx, gword ptr [rdx+0x10]
       mov      rdi, r14
       xor      rsi, rsi
       call     [SharpLink.Client.SharpLinkClient:GetReadyConnection(SharpLink.Abstractions.RpcMethodDescriptor,SharpLink.Client.SharpLinkClient+EndpointRetrySelectionState,SharpLink.Client.SharpLinkClient+AttemptOutcomeState):SharpLink.Client.ClientConnection:this]
       mov      rdx, bword ptr [rbp-0x1A0]
       lea      rdi, bword ptr [rdx+0x18]
       mov      rsi, rax
       call     CORINFO_HELP_CHECKED_ASSIGN_REF
       mov      dword ptr [rbp-0x28], 1
       mov      rdi, rbx
       call     [SharpLink.Client.SharpLinkClient:EnsureLogicalCallProgress(byref)]
       nop      
 
G_M000_IG09:                ;; offset=0x01C9
       mov      r15d, 64
       mov      edi, 96
       mov      rdx, bword ptr [rbp-0x1A0]
       cmp      byte  ptr [rdx+0x99], 0
       cmovne   r15d, edi
 
G_M000_IG10:                ;; offset=0x01E6
       mov      rdi, rbx
       call     [SharpLink.Client.SharpLinkClient:EnsureLogicalCallProgress(byref)]
       mov      rdx, bword ptr [rbp-0x1A0]
       mov      rdi, gword ptr [rdx+0x18]
       mov      rdi, gword ptr [rdi+0x40]
       cmp      dword ptr [rdi], edi
       call     [SharpLink.Client.PendingRequestTable:AllocateRequestId():long:this]
       mov      r13, rax
       jmp      G_M000_IG60
 
G_M000_IG11:                ;; offset=0x020E
       mov      rdx, bword ptr [rbp-0x1A0]
       mov      rdi, gword ptr [rdx+0x18]
       cmp      dword ptr [rdi], edi
       call     [SharpLink.Client.ClientConnection:TryBeginUntrackedCall():bool:this]
       test     eax, eax
       je       G_M000_IG64
 
G_M000_IG12:                ;; offset=0x0229
       mov      ebx, r15d
       mov      r15, r13
 
G_M000_IG13:                ;; offset=0x022F
       cmp      dword ptr [rbp-0x24], 0
       jne      SHORT G_M000_IG18
       mov      rdx, bword ptr [rbp-0x1A0]
       lea      rdi, bword ptr [rdx+0xB8]
 
G_M000_IG14:                ;; offset=0x0243
       vmovdqu  xmm0, xmmword ptr [rdi]
       vmovdqu  xmmword ptr [rbp-0x48], xmm0
 
G_M000_IG15:                ;; offset=0x024C
       xor      esi, esi
       mov      qword ptr [rdi], rsi
       mov      qword ptr [rdi+0x08], rsi
       mov      dword ptr [rbp-0x24], -1
       mov      dword ptr [rdx+0x20], -1
 
G_M000_IG16:                ;; offset=0x0263
       mov      rbx, gword ptr [rbp-0x48]
       test     rbx, rbx
       jne      G_M000_IG44
 
G_M000_IG17:                ;; offset=0x0270
       mov      rdx, bword ptr [rbp-0x1A0]
       mov      rdi, gword ptr [rdx+0x10]
       test     rdi, rdi
       je       G_M000_IG58
       mov      esi, 6
       xor      rdx, rdx
       call     [SharpLink.Client.SharpLinkClient+AttemptOutcomeState:Report(byte,System.Exception):this]
       jmp      G_M000_IG58
 
G_M000_IG18:                ;; offset=0x0296
       cmp      dword ptr [rbp-0x24], 1
       jne      G_M000_IG25
       mov      rdx, bword ptr [rbp-0x1A0]
       lea      rdi, bword ptr [rdx+0xB8]
 
G_M000_IG19:                ;; offset=0x02AE
       vmovdqu  xmm0, xmmword ptr [rdi]
       vmovdqu  xmmword ptr [rbp-0x48], xmm0
 
G_M000_IG20:                ;; offset=0x02B7
       xor      esi, esi
       mov      qword ptr [rdi], rsi
       mov      qword ptr [rdi+0x08], rsi
       mov      dword ptr [rbp-0x24], -1
       mov      dword ptr [rdx+0x20], -1
 
G_M000_IG21:                ;; offset=0x02CE
       mov      r15, gword ptr [rbp-0x48]
       test     r15, r15
       je       SHORT G_M000_IG17
 
G_M000_IG22:                ;; offset=0x02D7
       mov      rsi, r15
       mov      rdi, 0x7F5329AB5A88
       call     CORINFO_HELP_ISINSTANCEOFCLASS
       test     rax, rax
       jne      G_M000_IG54
 
G_M000_IG23:                ;; offset=0x02F2
       mov      rdi, 0x7F532A631CD0
       cmp      qword ptr [r15], rdi
       je       G_M000_IG56
 
G_M000_IG24:                ;; offset=0x0305
       mov      rdi, r15
       movsx    rsi, word  ptr [rbp-0x40]
       mov      r11, 0x7F53289D1AD0
       call     [r11]System.Threading.Tasks.Sources.IValueTaskSource:GetResult(short):this
       jmp      G_M000_IG17
 
G_M000_IG25:                ;; offset=0x031F
       mov      rdx, bword ptr [rbp-0x1A0]
       mov      rdi, gword ptr [rdx+0x18]
       mov      rsi, gword ptr [rdi+0x38]
       mov      rcx, qword ptr [rdx+0x38]
       mov      r8, qword ptr [rdx+0x40]
       lea      rdi, bword ptr [rdx+0xB0]
       mov      r9, gword ptr [rdx+0x08]
       vmovdqu32 ymm0, ymmword ptr [rdx+0x80]
       vmovdqu  ymmword ptr [rbp-0x80], ymm0
       mov      rax, qword ptr [rdx+0xA0]
       mov      qword ptr [rbp-0x60], rax
       mov      rax, gword ptr [rdx+0x58]
       movzx    r10, byte  ptr [rdx+0x99]
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
 
G_M000_IG26:                ;; offset=0x039D
       mov      gword ptr [rsp+0x50], rdi
 
G_M000_IG27:                ;; offset=0x03A2
       mov      gword ptr [rsp+0x58], rdi
       mov      rdi, r14
       mov      rdx, rcx
       mov      rcx, r8
       mov      r8, r15
       mov      r9d, ebx
       call     [SharpLink.Client.SharpLinkClient:SendRpcCall[SharpLink.Abstractions.RpcEmptyRequest](SharpLink.Runtime.RpcSession,long,long,long,byte,byref,SharpLink.Abstractions.IRpcCodec`1[SharpLink.Abstractions.RpcEmptyRequest],SharpLink.Abstractions.RpcDeadline,SharpLink.Sdk.SharpLinkMetadata,bool,System.Threading.CancellationToken,SharpLink.Runtime.IRequestEmissionFailureObserver,SharpLink.Client.PendingRequestTable):System.Threading.Tasks.ValueTask:this]
       mov      gword ptr [rbp-0x38], rax
       mov      qword ptr [rbp-0x30], rdx
       mov      rdx, bword ptr [rbp-0x1A0]
       cmp      byte  ptr [rdx+0x99], 0
       jne      G_M000_IG33
       movsx    rsi, word  ptr [rbp-0x30]
       mov      rdi, gword ptr [rbp-0x38]
       mov      gword ptr [rbp-0x48], rdi
       mov      word  ptr [rbp-0x40], si
       mov      byte  ptr [rbp-0x3E], 0
       mov      r14, gword ptr [rbp-0x48]
       test     r14, r14
       je       G_M000_IG21
 
G_M000_IG28:                ;; offset=0x03FA
       mov      rsi, r14
       mov      rdi, 0x7F5329AB5A88
       call     CORINFO_HELP_ISINSTANCEOFCLASS
       test     rax, rax
       jne      G_M000_IG52
 
G_M000_IG29:                ;; offset=0x0415
       mov      rdi, 0x7F532A631CD0
       cmp      qword ptr [r14], rdi
       je       G_M000_IG53
 
G_M000_IG30:                ;; offset=0x0428
       mov      rdi, r14
       movsx    rsi, word  ptr [rbp-0x40]
       mov      r11, 0x7F53289D1AC8
       call     [r11]System.Threading.Tasks.Sources.IValueTaskSource:GetStatus(short):int:this
 
G_M000_IG31:                ;; offset=0x043D
       test     eax, eax
       jne      G_M000_IG21
 
G_M000_IG32:                ;; offset=0x0445
       mov      dword ptr [rbp-0x24], 1
       mov      rdx, bword ptr [rbp-0x1A0]
       mov      dword ptr [rdx+0x20], 1
       lea      rdi, bword ptr [rdx+0xB8]
       lea      rsi, bword ptr [rbp-0x48]
       call     CORINFO_HELP_ASSIGN_BYREF
       movsq    
       lea      rsi, bword ptr [rdx+0x28]
       mov      rdi, rdx
       call     [System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Threading.Tasks.VoidTaskResult]:GetStateMachineBox[SharpLink.Client.SharpLinkClient+<InvokeOneWayPlainCoreAsync>d__190`1[SharpLink.Abstractions.RpcEmptyRequest]](byref,byref):System.Runtime.CompilerServices.IAsyncStateMachineBox]
       mov      rsi, rax
       lea      rdi, [rbp-0x48]
       call     [System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Threading.Tasks.VoidTaskResult]:AwaitUnsafeOnCompleted[System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable+ConfiguredValueTaskAwaiter](byref,System.Runtime.CompilerServices.IAsyncStateMachineBox)]
       jmp      G_M000_IG57
 
G_M000_IG33:                ;; offset=0x048B
       mov      rdx, bword ptr [rbp-0x1A0]
       mov      rdi, gword ptr [rdx+0x18]
       lea      rbx, bword ptr [rdx+0x58]
 
G_M000_IG34:                ;; offset=0x049A
       vmovdqu32 zmm0, zmmword ptr [rbx]
       vmovdqu32 zmmword ptr [rbp-0x198], zmm0
       vmovdqu  xmm0, xmmword ptr [rbx+0x40]
       vmovdqu  xmmword ptr [rbp-0x158], xmm0
 
G_M000_IG35:                ;; offset=0x04B7
       xor      eax, eax
       mov      qword ptr [rbp-0x120], rax
       mov      gword ptr [rbp-0x148], r14
       mov      rax, gword ptr [rbp-0x38]
       mov      gword ptr [rbp-0x118], rax
       movsx    rax, word  ptr [rbp-0x30]
       mov      word  ptr [rbp-0x110], ax
       movzx    rax, byte  ptr [rbp-0x2E]
       mov      byte  ptr [rbp-0x10E], al
       mov      gword ptr [rbp-0x140], rdi
       mov      qword ptr [rbp-0x130], r15
 
G_M000_IG36:                ;; offset=0x04F6
       vmovdqu32 zmm0, zmmword ptr [rbp-0x198]
       vmovdqu32 zmmword ptr [rbp-0x108], zmm0
       vmovdqu  xmm0, xmmword ptr [rbp-0x158]
       vmovdqu  xmmword ptr [rbp-0xC8], xmm0
 
G_M000_IG37:                ;; offset=0x051A
       mov      dword ptr [rbp-0x128], -1
       lea      rdi, [rbp-0x148]
       call     [System.Runtime.CompilerServices.AsyncMethodBuilderCore:Start[SharpLink.Client.SharpLinkClient+<AwaitPlainOneWayEmissionOrDeadlineAsync>d__187](byref)]
       lea      rdi, [rbp-0x120]
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
 
G_M000_IG38:                ;; offset=0x0568
       mov      rsi, rbx
       mov      rdi, 0x7F5329AB5A88
       call     CORINFO_HELP_ISINSTANCEOFCLASS
       test     rax, rax
       jne      G_M000_IG46
 
G_M000_IG39:                ;; offset=0x0583
       mov      rdi, 0x7F532A631CD0
       cmp      qword ptr [rbx], rdi
       je       G_M000_IG47
 
G_M000_IG40:                ;; offset=0x0596
       mov      rdi, rbx
       movsx    rsi, word  ptr [rbp-0x40]
       mov      r11, 0x7F53289D1AB8
       call     [r11]System.Threading.Tasks.Sources.IValueTaskSource:GetStatus(short):int:this
 
G_M000_IG41:                ;; offset=0x05AB
       test     eax, eax
       jne      G_M000_IG16
 
G_M000_IG42:                ;; offset=0x05B3
       xor      edi, edi
       mov      dword ptr [rbp-0x24], edi
 
G_M000_IG43:                ;; offset=0x05B8
       mov      rdx, bword ptr [rbp-0x1A0]
       mov      dword ptr [rdx+0x20], edi
       lea      rdi, bword ptr [rdx+0xB8]
       lea      rsi, bword ptr [rbp-0x48]
       call     CORINFO_HELP_ASSIGN_BYREF
       movsq    
       lea      rsi, bword ptr [rdx+0x28]
       mov      rdi, rdx
       call     [System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Threading.Tasks.VoidTaskResult]:GetStateMachineBox[SharpLink.Client.SharpLinkClient+<InvokeOneWayPlainCoreAsync>d__190`1[SharpLink.Abstractions.RpcEmptyRequest]](byref,byref):System.Runtime.CompilerServices.IAsyncStateMachineBox]
       mov      rsi, rax
       lea      rdi, [rbp-0x48]
       call     [System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Threading.Tasks.VoidTaskResult]:AwaitUnsafeOnCompleted[System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable+ConfiguredValueTaskAwaiter](byref,System.Runtime.CompilerServices.IAsyncStateMachineBox)]
       jmp      G_M000_IG59
 
G_M000_IG44:                ;; offset=0x05F3
       mov      rsi, rbx
       mov      rdi, 0x7F5329AB5A88
       call     CORINFO_HELP_ISINSTANCEOFCLASS
       test     rax, rax
       jne      SHORT G_M000_IG48
 
G_M000_IG45:                ;; offset=0x060A
       mov      rdi, 0x7F532A631CD0
       cmp      qword ptr [rbx], rdi
       jne      SHORT G_M000_IG51
       jmp      SHORT G_M000_IG50
 
G_M000_IG46:                ;; offset=0x061B
       test     dword ptr [rax+0x34], 0x1600000
       jne      G_M000_IG16
       jmp      SHORT G_M000_IG42
 
G_M000_IG47:                ;; offset=0x062A
       movsx    rsi, word  ptr [rbp-0x40]
       lea      rdi, bword ptr [rbx+0x40]
       call     [System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore`1[long]:GetStatus(short):int:this]
       jmp      G_M000_IG41
 
G_M000_IG48:                ;; offset=0x063E
       mov      edi, dword ptr [rax+0x34]
       and      edi, 0x11000000
       cmp      edi, 0x1000000
       je       G_M000_IG17
 
G_M000_IG49:                ;; offset=0x0653
       mov      rdi, rax
       xor      esi, esi
       call     [System.Runtime.CompilerServices.TaskAwaiter:HandleNonSuccessAndDebuggerNotification(System.Threading.Tasks.Task,int)]
       jmp      G_M000_IG17
 
G_M000_IG50:                ;; offset=0x0663
       movsx    rsi, word  ptr [rbp-0x40]
       mov      rdi, rbx
       call     [Microsoft.Win32.SafeHandles.SafeFileHandle+ThreadPoolValueTaskSource:GetResult(short):long:this]
       jmp      G_M000_IG17
 
G_M000_IG51:                ;; offset=0x0676
       mov      rdi, rbx
       movsx    rsi, word  ptr [rbp-0x40]
       mov      r11, 0x7F53289D1AC0
       call     [r11]System.Threading.Tasks.Sources.IValueTaskSource:GetResult(short):this
       jmp      G_M000_IG17
 
G_M000_IG52:                ;; offset=0x0690
       test     dword ptr [rax+0x34], 0x1600000
       jne      G_M000_IG21
       jmp      G_M000_IG32
 
G_M000_IG53:                ;; offset=0x06A2
       movsx    rsi, word  ptr [rbp-0x40]
       lea      rdi, bword ptr [r14+0x40]
       call     [System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore`1[long]:GetStatus(short):int:this]
       jmp      G_M000_IG31
 
G_M000_IG54:                ;; offset=0x06B6
       mov      edi, dword ptr [rax+0x34]
       and      edi, 0x11000000
       cmp      edi, 0x1000000
       je       G_M000_IG17
 
G_M000_IG55:                ;; offset=0x06CB
       mov      rdi, rax
       xor      esi, esi
       call     [System.Runtime.CompilerServices.TaskAwaiter:HandleNonSuccessAndDebuggerNotification(System.Threading.Tasks.Task,int)]
       jmp      G_M000_IG17
 
G_M000_IG56:                ;; offset=0x06DB
       movsx    rsi, word  ptr [rbp-0x40]
       mov      rdi, r15
       call     [Microsoft.Win32.SafeHandles.SafeFileHandle+ThreadPoolValueTaskSource:GetResult(short):long:this]
       jmp      G_M000_IG17
 
G_M000_IG57:                ;; offset=0x06EE
       call     G_M000_IG89
       jmp      G_M000_IG72
 
G_M000_IG58:                ;; offset=0x06F8
       cmp      dword ptr [rbp-0x24], 0
       jge      G_M000_IG70
       mov      rdx, bword ptr [rbp-0x1A0]
       mov      rdi, gword ptr [rdx+0x18]
       cmp      byte  ptr [rdi], dil
       call     [SharpLink.Client.ClientConnection:ReleaseAuxiliaryActiveCall():this]
       jmp      G_M000_IG70
 
G_M000_IG59:                ;; offset=0x071B
       call     G_M000_IG89
       jmp      G_M000_IG72
 
G_M000_IG60:                ;; offset=0x0725
       mov      rdx, bword ptr [rbp-0x1A0]
       lea      rdi, bword ptr [rdx+0xA8]
       cmp      gword ptr [rdi], 0
       je       SHORT G_M000_IG62
 
G_M000_IG61:                ;; offset=0x0739
       mov      rax, gword ptr [rdi]
       cmp      dword ptr [rax+0x20], 0
       jne      SHORT G_M000_IG63
 
G_M000_IG62:                ;; offset=0x0742
       mov      rdi, rbx
       call     [SharpLink.Client.SharpLinkClient:EnsureLogicalCallProgress(byref)]
       jmp      G_M000_IG11
 
G_M000_IG63:                ;; offset=0x0750
       call     [System.Threading.CancellationToken:ThrowOperationCanceledException():this]
       int3     
 
G_M000_IG64:                ;; offset=0x0757
       mov      rdi, 0x7F532AD0E970
       call     CORINFO_HELP_NEWSFAST
       mov      r15, rax
       mov      rdi, 0x80131500E0434352
       mov      qword ptr [r15+0x68], rdi
       mov      rdi, 0x7F53A4412460
       mov      gword ptr [r15+0x10], rdi
       mov      edi, 10
       call     [SharpLink.Abstractions.SharpLinkException:ValidateCode(int):int]
       mov      dword ptr [r15+0x70], eax
       mov      word  ptr [r15+0x74], 0
       mov      rdi, rbx
       mov      rsi, r15
       call     [SharpLink.Client.SharpLinkClient:ArbitrateLogicalCallFailure(byref,System.Exception):System.Exception]
       mov      rbx, rax
       mov      rdx, bword ptr [rbp-0x1A0]
       mov      r15, gword ptr [rdx+0x10]
       test     r15, r15
       je       SHORT G_M000_IG69
       mov      rsi, rbx
       mov      rdi, 0x7F532AD0E970
       call     CORINFO_HELP_ISINSTANCEOFCLASS
       test     rax, rax
       je       SHORT G_M000_IG65
       cmp      dword ptr [rax+0x70], 13
       je       SHORT G_M000_IG66
 
G_M000_IG65:                ;; offset=0x07D7
       mov      edi, 8
       jmp      SHORT G_M000_IG67
 
G_M000_IG66:                ;; offset=0x07DE
       mov      edi, 4
 
G_M000_IG67:                ;; offset=0x07E3
       movzx    rsi, dil
       cmp      esi, 1
       jg       SHORT G_M000_IG68
       mov      dword ptr [r15+0x30], 1
 
G_M000_IG68:                ;; offset=0x07F4
       mov      rdi, r15
       mov      rdx, rbx
       call     [SharpLink.Client.SharpLinkClient+AttemptOutcomeState:Report(byte,System.Exception):this]
 
G_M000_IG69:                ;; offset=0x0800
       mov      rdi, rbx
       call     CORINFO_HELP_THROW
       int3     
 
G_M000_IG70:                ;; offset=0x0809
       mov      rdx, bword ptr [rbp-0x1A0]
       mov      dword ptr [rdx+0x20], -2
       xor      rdi, rdi
       mov      gword ptr [rdx+0x10], rdi
 
G_M000_IG71:                ;; offset=0x081D
       mov      gword ptr [rdx+0x18], rdi
       lea      rdi, bword ptr [rdx+0x28]
       call     [System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder:SetResult():this]
 
G_M000_IG72:                ;; offset=0x082B
       nop      
 
G_M000_IG73:                ;; offset=0x082C
       vzeroupper 
       add      rsp, 480
       pop      rbx
       pop      r13
       pop      r14
       pop      r15
       pop      rbp
       ret      
 
G_M000_IG74:                ;; offset=0x083F
       sub      rsp, 104
 
G_M000_IG75:                ;; offset=0x0843
       mov      r15, rdi
       cmp      dword ptr [rbp-0x28], 0
       je       SHORT G_M000_IG76
       mov      rdx, bword ptr [rbp-0x1A0]
       mov      rdi, gword ptr [rdx+0x18]
       cmp      dword ptr [rdi], edi
       call     [SharpLink.Client.ClientConnection:ReleaseCallAdmissionReservation():this]
 
G_M000_IG76:                ;; offset=0x085F
       mov      rdx, bword ptr [rbp-0x1A0]
       lea      rbx, bword ptr [rdx+0x58]
       mov      rdi, rbx
       mov      rsi, r15
       call     [SharpLink.Client.SharpLinkClient:ArbitrateLogicalCallFailure(byref,System.Exception):System.Exception]
       mov      rbx, rax
       mov      rdx, bword ptr [rbp-0x1A0]
       mov      rdi, gword ptr [rdx+0x10]
       test     rdi, rdi
       je       SHORT G_M000_IG77
       mov      rsi, rbx
       call     [SharpLink.Client.SharpLinkClient+AttemptOutcomeState:CompleteLocalFailure(System.Exception):this]
 
G_M000_IG77:                ;; offset=0x0892
       mov      rdi, rbx
       call     CORINFO_HELP_THROW
       int3     
 
G_M000_IG78:                ;; offset=0x089B
       sub      rsp, 104
 
G_M000_IG79:                ;; offset=0x089F
       mov      r14, rdi
       mov      rdx, bword ptr [rbp-0x1A0]
       mov      rdi, gword ptr [rdx+0x18]
       cmp      dword ptr [rdi], edi
       call     [SharpLink.Client.ClientConnection:ReleaseCallAdmissionReservation():this]
       mov      rdx, bword ptr [rbp-0x1A0]
       lea      rbx, bword ptr [rdx+0x58]
       mov      rdi, rbx
       mov      rsi, r14
       call     [SharpLink.Client.SharpLinkClient:ArbitrateLogicalCallFailure(byref,System.Exception):System.Exception]
       mov      rbx, rax
       mov      rdx, bword ptr [rbp-0x1A0]
       mov      rdi, gword ptr [rdx+0x10]
       test     rdi, rdi
       je       SHORT G_M000_IG80
       mov      rsi, rbx
       call     [SharpLink.Client.SharpLinkClient+AttemptOutcomeState:CompleteLocalFailure(System.Exception):this]
 
G_M000_IG80:                ;; offset=0x08E8
       mov      rdi, rbx
       call     CORINFO_HELP_THROW
       int3     
 
G_M000_IG81:                ;; offset=0x08F1
       sub      rsp, 104
 
G_M000_IG82:                ;; offset=0x08F5
       mov      rdx, bword ptr [rbp-0x1A0]
       mov      rdi, gword ptr [rdx+0x18]
       cmp      dword ptr [rdi], edi
       call     [SharpLink.Client.ClientConnection:ReleaseCallAdmissionReservation():this]
       call     CORINFO_HELP_RETHROW
       int3     
 
G_M000_IG83:                ;; offset=0x090E
       sub      rsp, 104
 
G_M000_IG84:                ;; offset=0x0912
       mov      rsi, rdi
       mov      rdi, bword ptr [rbp-0x1A0]
       lea      rbx, bword ptr [rdi+0x58]
       mov      rdi, rbx
       call     [SharpLink.Client.SharpLinkClient:ArbitrateLogicalCallFailure(byref,System.Exception):System.Exception]
       mov      rbx, rax
       mov      rdi, bword ptr [rbp-0x1A0]
       mov      r15, gword ptr [rdi+0x10]
       test     r15, r15
       je       SHORT G_M000_IG88
       mov      rsi, rbx
       mov      rdi, 0x7F532AD0E970
       call     CORINFO_HELP_ISINSTANCEOFCLASS
       test     rax, rax
       je       SHORT G_M000_IG85
       mov      rdi, rax
       call     [SharpLink.Abstractions.SharpLinkException:get_Code():int:this]
       cmp      eax, 13
       je       SHORT G_M000_IG86
 
G_M000_IG85:                ;; offset=0x0961
       mov      esi, 7
       jmp      SHORT G_M000_IG87
 
G_M000_IG86:                ;; offset=0x0968
       mov      esi, 4
 
G_M000_IG87:                ;; offset=0x096D
       mov      rdi, r15
       mov      rdx, rbx
       call     [SharpLink.Client.SharpLinkClient+AttemptOutcomeState:CompleteWithoutPending(byte,System.Exception):this]
 
G_M000_IG88:                ;; offset=0x0979
       mov      rdi, rbx
       call     CORINFO_HELP_THROW
       int3     
 
G_M000_IG89:                ;; offset=0x0982
       sub      rsp, 104
 
G_M000_IG90:                ;; offset=0x0986
       cmp      dword ptr [rbp-0x24], 0
       jge      SHORT G_M000_IG91
       mov      rdi, bword ptr [rbp-0x1A0]
       mov      rax, gword ptr [rdi+0x18]
       cmp      byte  ptr [rax], al
       mov      rdi, rax
       call     [SharpLink.Client.ClientConnection:ReleaseAuxiliaryActiveCall():this]
 
G_M000_IG91:                ;; offset=0x09A2
       nop      
 
G_M000_IG92:                ;; offset=0x09A3
       vzeroupper 
       add      rsp, 104
       ret      
 
G_M000_IG93:                ;; offset=0x09AB
       sub      rsp, 104
 
G_M000_IG94:                ;; offset=0x09AF
       mov      rsi, rdi
       mov      rdx, bword ptr [rbp-0x1A0]
       mov      dword ptr [rdx+0x20], -2
       xor      rdi, rdi
       mov      gword ptr [rdx+0x10], rdi
 
G_M000_IG95:                ;; offset=0x09C6
       mov      gword ptr [rdx+0x18], rdi
       lea      rdi, bword ptr [rdx+0x28]
       call     [System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder:SetException(System.Exception):this]
       lea      rax, G_M000_IG72
 
G_M000_IG96:                ;; offset=0x09DB
       vzeroupper 
       add      rsp, 104
       ret      
 
; Total bytes of code 2531

