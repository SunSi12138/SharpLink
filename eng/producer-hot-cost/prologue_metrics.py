#!/usr/bin/env python3
"""Retain exact native prologues and emitted-version gaps; no timing inference."""
from pathlib import Path
import json,re,hashlib
r=Path(__file__).resolve().parent
normal=lambda s:re.sub(r'd__\d+','d__N',re.sub(r'0x[0-9A-Fa-f]{10,16}','0xADDRESS',s))
def category(label):
 if 'StreamProgress' in label:return 'healthy-stream'
 if 'ScanExpiredDeadlines' in label:return 'background-scan'
 if any(x in label for x in ['GetReadyConnection','EnsureExpansion','EnsureReconnectLoop']):return 'selection'
 if any(x in label for x in ['ProtocolV2FrameParser:','RpcSessionProtocolRules:','RpcSession:TryReadInboundFrame','RpcSession:EnsureInboundFrameAllowed','RpcSession:EnsureOutboundFrameAllowed']):return 'ordinary-parser-phase'
 if any(x in label for x in ['ProcessRequestLoop','DispatchRequestAsync','SharpLinkServer:DispatchRpcAsync','SharpLinkClient:DispatchRpc(']):return 'ordinary-reader-caller'
 return None
def metrics(body):
 match=re.search(r'G_M\d+_IG01:.*?\n(.*?)G_M\d+_IG02:',body,re.S)
 if not match:return None
 instructions=[x.strip() for x in match[1].splitlines() if x.startswith('       ') and x.strip()]
 zero=set();stores=[];loop=None
 sizes={'zmmword':64,'ymmword':32,'xmmword':16,'qword':8,'gword':8,'bword':8,'dword':4,'word':2,'byte':1}
 def reg(s):return s.replace('xmm','v').replace('ymm','v').replace('zmm','v').replace('eax','rax').replace('ecx','rcx').replace('edx','rdx')
 for i,s in enumerate(instructions):
  m=re.match(r'(?:v?xor\w*)\s+(\w+), (\w+)(?:, (\w+))?$',s)
  if m and reg(m[1])==reg(m[2]) and (not m[3] or reg(m[1])==reg(m[3])):zero.add(reg(m[1]));continue
  m=re.match(r'mov\s+(\w+), (-?(?:0x[\dA-Fa-f]+|\d+))$',s)
  if m:
   zero.discard(reg(m[1]));n=int(m[2],0)
   if n==0:zero.add(reg(m[1]))
   if m[1]=='rax' and n<0:loop={'Start':i,'Initial':n}
  if loop and 'End' in loop and re.match(r'jne\s+',s):zero.add('rax')
  m=re.match(r'\w+\s+(zmmword|ymmword|xmmword|qword|gword|bword|dword|word|byte) ptr (\[[^]]+\]), (\w+)$',s)
  if m and (reg(m[3]) in zero or m[3]=='0'):stores.append({'Index':i,'Width':sizes[m[1]],'Address':m[2],'Instruction':s,'Executions':1})
  if loop and re.match(r'add\s+rax, \d+$',s):loop.update(End=i,Step=int(s.rsplit(' ',1)[1]))
 if loop and 'End' in loop and any('jne' in s and 'instr' in s for s in instructions):
  iterations=-loop['Initial']//loop['Step'];loop['Iterations']=iterations
  for x in stores:
   if loop['Start']<x['Index']<loop['End']:x['Executions']=iterations
 else:loop=None
 slots=set();unresolved=[]
 for x in stores:
  for n in range(x['Executions']):
   address=x['Address']
   if 'rbp' not in address or re.search(r'[a-zA-Z]+',address.replace('rbp','').replace('rax','').replace('0x','').lower().replace('a','').replace('b','').replace('c','').replace('d','').replace('e','').replace('f','')):
    unresolved.append(address);continue
   expr=address[1:-1].replace('rbp','0')
   if 'rax' in expr:
    if not loop or not (loop['Start']<x['Index']<loop['End']):unresolved.append(address);continue
    expr=expr.replace('rax',str(loop['Initial']+n*loop['Step']))
   expr=expr.replace('+-','-').replace('--','+')
   values=re.findall(r'[+-]?(?:0x[0-9A-Fa-f]+|[0-9]+)',expr)
   offset=sum(int(value,0) for value in values)
   slots.update(range(offset,offset+x['Width']))
 reserve=re.search(r'sub\s+rsp, (0x[\dA-Fa-f]+|\d+)',match[1]);pushes=sum(x.startswith('push ') for x in instructions)
 return {'Reservation':int(reserve[1],0) if reserve else 0,'Pushes':pushes,'TotalReservationPlusPushes':(int(reserve[1],0) if reserve else 0)+8*pushes,'StaticInstructionCount':len(instructions),'ZeroStoreInstructions':len(stores),'ExecutedZeroStores':sum(x['Executions'] for x in stores),'ZeroStoreBytesWrittenIncludingOverlap':sum(x['Width']*x['Executions'] for x in stores),'DistinctZeroedStackBytes':len(slots) if not unresolved else None,'UnresolvedZeroAddresses':unresolved,'ZeroStores':stores,'InitializationLoop':loop,'Instructions':instructions}
