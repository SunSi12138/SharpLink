import copy
from pathlib import Path
import xml.etree.ElementTree as ET
import accept_results as a
import classify as c
SOURCE=Path(__file__).with_name('fixture.Lifecycle.cs')
ROWS={a.TARGET:(283,302),a.CONTROL:(262,281)}
TRACE='\n'.join([
 '   at SharpLink.Server.SharpLinkServer.RunAcceptLoopAsync(TaskCompletionSource`1 acceptStarted, TaskCompletionSource`1 running, CancellationToken acceptToken) in /_/src/SharpLink.Server/SharpLinkServer.RunLoop.cs:line 34',
 '   at SharpLink.Server.SharpLinkServer.RunAcceptLoopAsync(TaskCompletionSource`1 acceptStarted, TaskCompletionSource`1 running, CancellationToken acceptToken) in /_/src/SharpLink.Server/SharpLinkServer.RunLoop.cs:line 91',
 '   at SharpLink.Server.SharpLinkServer.ServerLifecycleCoordinator.WaitForAcceptRuntimeShutdownAsync() in /_/src/SharpLink.Server/ServerLifecycleCoordinator.cs:line 323',
 '   at SharpLink.Server.SharpLinkServer.ServerLifecycleCoordinator.CompleteFaultedStopAsync() in /_/src/SharpLink.Server/ServerLifecycleCoordinator.cs:line 306',
 '   at SharpLink.IntegrationTests.TransportConnectionIntegrationTests.EnsureThrows[TException](Task task, String name) in /_/test/SharpLink.IntegrationTests/TransportConnectionIntegrationTests.cs:line 66'])


def sample(scope='baseline'):
 method=a.CONTROL if scope=='control' else a.TARGET;event=scope=='baseline';uid='synthetic-actual-uid'
 row={'uid':uid,'displayName':method,'type':{'namespace':a.NS,'typeName':a.CLASS,'methodName':method,'methodArity':0,
      'parameterTypeFullNames':[],'returnTypeFullName':'System.Threading.Tasks.Task',
      'assemblyFullName':a.ASSEMBLY+', Version=2.0.4.0, Culture=neutral, PublicKeyToken=null'},
      'location':{'file':a.REPORTED,'lineStart':ROWS[method][0],'lineEnd':ROWS[method][1]}}
 test={'id':uid,'displayName':method,'methodName':method,'className':a.CLASS,'filePath':a.REPORTED,
       'lineNumber':ROWS[method][0],'endLineNumber':ROWS[method][1],'status':'passed','durationMs':1}
 native={'schemaVersion':1,'assemblyName':a.ASSEMBLY,'tunitVersion':'1.78.0.0','runtimeVersion':'.NET 10.0.14',
         'publicationGeneration':'a'*32,'summary':c.summary(2 if event else 1,int(event)),
         'groups':[{'className':a.CLASS,'namespace':a.NS,'summary':c.summary(1,0),'tests':[test]}]}
 attrs={'name':a.ASSEMBLY,'tests':str(2 if event else 1),'errors':str(int(event)),'failures':'0','skipped':'0'}
 root=ET.Element('testsuites',attrs);suite=ET.SubElement(root,'testsuite',attrs)
 ET.SubElement(suite,'testcase',{'name':method,'classname':a.CLASS})
 if event:
  failure={'type':'System.IO.IOException','message':'accept failed','stackTrace':TRACE}
  native['unobservedTaskExceptions']=[failure]
  name='Unobserved task exception - IOException: accept failed'
  node={'id':'12345678-1234-4234-9234-123456789abc','displayName':name,'methodName':name,'className':'UnknownClass','status':'error','durationMs':0,'exception':copy.deepcopy(failure)}
  native['groups'].append({'className':'UnknownClass','namespace':'','summary':c.summary(1,1),'tests':[node]})
  case=ET.SubElement(suite,'testcase',{'name':name,'classname':'UnknownClass'})
  error=ET.SubElement(case,'error',{'type':'System.IO.IOException','message':'accept failed'});error.text=a.exception_text(failure)
  output=ET.SubElement(suite,'system-err');output.text='Unobserved task exception: '+a.exception_text(failure)+'\n'
 return native,root,{uid:row},scope


def classify(parts,**kw):
 n,x,ledger,scope=parts
 return a.classify(n,ET.tostring(x,encoding='unicode'),ledger,scope,SOURCE,{'10.0.14'},**kw)
