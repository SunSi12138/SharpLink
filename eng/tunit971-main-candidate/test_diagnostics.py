"""Python-only allowlist and read-safety controls; no SDK or real child."""
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import failure_diagnostics as d
import run_hosted as r

class DiagnosticControls(unittest.TestCase):
 def test_empty_and_known_codes_have_no_raw_text(self):
  self.assertEqual(d.stream_summary(b'')['codeOccurrences'],0)
  value=d.stream_summary(b'SECRET/path.csproj : error NU1301: SECRET URL\nerror MSB4018: SECRET\nwarning NETSDK1234: SECRET\nerror NU1301: SECRET\n')
  self.assertEqual(value['codeOccurrences'],4);self.assertEqual(value['codeKindCount'],3);self.assertNotIn('SECRET',json.dumps(value))
 def test_noncode_tokens_wrong_shapes_not_exposed(self):
  value=d.stream_summary(b'error SECRET1234: SECRET\nerror NU12345: SECRET\nerror ANU1234: SECRET\nNU1301 warning only\n')
  self.assertEqual(value['codes'],[]);self.assertNotIn('SECRET',json.dumps(value))
 def test_codes_bounded_with_total_count(self):
  value=d.stream_summary('\n'.join(f'error NU{i:04}: private' for i in range(30)).encode())
  self.assertEqual(len(value['codes']),16);self.assertEqual(value['codeKindCount'],30);self.assertTrue(value['codesTruncated'])
 def test_fixed_exception_types_only_and_exact_boundaries(self):
  value=d.stream_summary(b'System.ArgumentNullException: SECRET\nSystem.ArgumentNullException: SECRET\nVendor.SecretException: SECRET\nXSystem.ArgumentExceptionXXX')
  self.assertEqual(value['knownExceptionTypes'],[{'type':'System.ArgumentNullException','count':2}]);self.assertNotIn('SECRET',json.dumps(value))
 def test_invalid_utf8_stays_count_only(self):
  value=d.stream_summary(b'\xffSECRET');self.assertEqual(value['replacementCharacterCount'],1);self.assertNotIn('SECRET',json.dumps(value))
 def test_only_literal_exact_valueerror_reasons(self):
  self.assertEqual(d.known_reason(ValueError('Protected source/output changed')),'Protected source/output changed')
  for error in (ValueError('SECRET'),ValueError(['SECRET']),ValueError('Protected source/output changed SECRET'),RuntimeError('Protected source/output changed')):
   self.assertIsNone(d.known_reason(error))
  class Sub(ValueError):pass
  self.assertIsNone(d.known_reason(Sub('Protected source/output changed')))
 def test_size_and_nonbytes_fail_closed(self):
  for raw in ('SECRET',None,b'x'*(d.MAX_CAPTURE+1)):
   with self.assertRaises(ValueError):d.stream_summary(raw)
 def fixture(self,root):
  label='restore-baseline';directory=root/label;directory.mkdir()
  for stream in ('stdout','stderr'):(directory/(label+'.'+stream)).write_bytes(b'error NU1301: SECRET')
  return label,directory
 def test_attach_is_only_additive_and_preserves_original_group(self):
  with tempfile.TemporaryDirectory() as t:
   root=Path(t);label,directory=self.fixture(root);body=ValueError('body');original=BaseExceptionGroup('original',[body]);d.attach(original,root,directory,label)
   self.assertIs(original.exceptions[0],body);self.assertEqual(original.command_codes.public()['stdout']['codeOccurrences'],1);self.assertNotIn('SECRET',json.dumps(original.command_codes.public()))
 def test_no_missing_capture_bypass_and_body_first(self):
  with tempfile.TemporaryDirectory() as t:
   root=Path(t);original=ValueError('original')
   with self.assertRaises(BaseExceptionGroup) as caught:d.attach(original,root,root/'restore-baseline','restore-baseline')
   self.assertIs(caught.exception.exceptions[0],original)
 def test_symlink_directory_and_capture_do_not_read_content(self):
  with tempfile.TemporaryDirectory() as t:
   root=Path(t);label,directory=self.fixture(root);outside=root/'outside';outside.mkdir();directory.rename(root/'original');directory.symlink_to(outside,target_is_directory=True)
   with patch.object(Path,'open',side_effect=AssertionError('must not open')):
    with self.assertRaises(ValueError):d.read_capture(root,directory,label,'stdout')
   directory.unlink();(root/'original').rename(directory);path=directory/(label+'.stdout');path.unlink();path.symlink_to(root/'unbound')
   with patch.object(Path,'open',side_effect=AssertionError('must not open')):
    with self.assertRaises(ValueError):d.read_capture(root,directory,label,'stdout')
 def test_special_and_oversize_capture_fail_before_read(self):
  with tempfile.TemporaryDirectory() as t:
   root=Path(t);label,directory=self.fixture(root);path=directory/(label+'.stdout');path.unlink();os.mkfifo(path)
   with patch.object(Path,'open',side_effect=AssertionError('must not open')):
    with self.assertRaises(ValueError):d.read_capture(root,directory,label,'stdout')
 def test_unknown_labels_and_external_path_do_not_read(self):
  with tempfile.TemporaryDirectory() as t:
   root=Path(t);label,directory=self.fixture(root)
   for path,name,stream in ((directory,'body','stdout'),(root/'outside',label,'stdout'),(directory,label,'SECRET')):
    with patch.object(Path,'open',side_effect=AssertionError('must not open')):
     with self.assertRaises(ValueError):d.read_capture(root,path,name,stream)
 def test_public_summary_adds_only_known_guard_label(self):
  output=r.safe_leaves([ValueError('Protected source/output changed'),ValueError('SECRET')]);self.assertEqual(output['leaves'][0]['reason'],'Protected source/output changed');self.assertNotIn('reason',output['leaves'][1]);self.assertNotIn('SECRET',json.dumps(output))
 def test_reader_failure_keeps_original_first(self):
  original=ValueError('original')
  with patch.object(d,'read_capture',side_effect=OSError('private read')):
   with self.assertRaises(BaseExceptionGroup) as caught:d.attach(original,Path('/work'),Path('/work/restore-baseline'),'restore-baseline')
  self.assertIs(caught.exception.exceptions[0],original);self.assertIsInstance(caught.exception.exceptions[1],OSError)



 def test_swapped_handle_is_rejected_before_content_read(self):
  with tempfile.TemporaryDirectory() as t:
   root=Path(t);label,directory=self.fixture(root);other=root/'other';other.write_bytes(b'SECRET');fd=os.open(other,os.O_RDONLY)
   with patch.object(os,'open',return_value=fd),patch.object(os,'fdopen',side_effect=AssertionError('must not read substituted handle')):
    with self.assertRaises(ValueError):d.read_capture(root,directory,label,'stdout')
   with self.assertRaises(OSError):os.fstat(fd) # rejected original fd was closed
 def test_fifo_swap_between_lstat_and_open_uses_nonblock_before_fstat(self):
  with tempfile.TemporaryDirectory() as t:
   root=Path(t);label,directory=self.fixture(root);path=directory/(label+'.stdout');real_open=os.open
   def swap(name,flags):
    self.assertTrue(flags&os.O_NONBLOCK);self.assertTrue(flags&os.O_NOFOLLOW)
    path.unlink();os.mkfifo(path)
    return real_open(name,flags)
   with patch.object(os,'open',side_effect=swap),patch.object(os,'fdopen',side_effect=AssertionError('must not read FIFO')):
    with self.assertRaises(ValueError):d.read_capture(root,directory,label,'stdout')
 def test_open_failure_and_read_close_failures_remain_body_first(self):
  with tempfile.TemporaryDirectory() as t:
   root=Path(t);label,directory=self.fixture(root);body=OSError('open')
   with patch.object(os,'open',side_effect=body):
    with self.assertRaises(OSError) as caught:d.read_capture(root,directory,label,'stdout')
   self.assertIs(caught.exception,body)
   class Broken:
    def __init__(self,fd):self.fd=fd
    def read(self,*args):raise OSError('read')
    def close(self):os.close(self.fd);raise OSError('close')
   with patch.object(os,'fdopen',side_effect=lambda fd,*args:Broken(fd)):
    with self.assertRaises(BaseExceptionGroup) as caught:d.read_capture(root,directory,label,'stdout')
   self.assertEqual([x.args for x in caught.exception.exceptions],[('read',),('close',)])
 def test_workflow_preserves_both_gate_but_push_selects_windows(self):
  import re
  file=Path(__file__).parents[1]/'workflow/validation-tunit971-hosted.yml'
  # Private SOURCE control; public synthetic placement uses the same exact workflow.
  if not file.exists():file=Path(__file__).parents[2]/'.github/workflows/validation-tunit971-hosted.yml'
  text=file.read_text();self.assertIn("default: windows",text);self.assertIn("inputs.platform_scope == 'both'",text)
  arrays=re.findall(r"'(\[\{.*?\])'",text)
  matrices=[json.loads(x) for x in arrays]
  self.assertEqual(matrices,[[{'os':'windows-2025','architecture':'x64'},{'os':'macos-15','architecture':'arm64'}],[{'os':'windows-2025','architecture':'x64'}]])
  self.assertIn('test_diagnostics.py',text)

class InventoryDiagnosticControls(unittest.TestCase):
 def test_exact_known_changed_removed_and_unknown_added_counts(self):
  old={'Directory.Build.props':'a'*64,'known/removed.cs':'b'*64};new={'Directory.Build.props':'c'*64,'PRIVATE/token-file':'d'*64}
  value=d.inventory_difference(old,new,frozenset(old))
  self.assertEqual((value['changed'],value['added'],value['removed']),(1,1,1));self.assertEqual(value['differenceCount'],3)
  self.assertNotIn('PRIVATE',json.dumps(value));self.assertNotIn('token-file',json.dumps(value))
  known=next(x for x in value['records'] if x.get('knownRelativePath')=='Directory.Build.props')
  self.assertEqual(known['beforeSHA256'],'a'*64);self.assertEqual(known['afterSHA256'],'c'*64)
 def test_unknown_absolute_traversal_case_alias_not_published(self):
  paths=['/SECRET/path','../SECRET','D:/SECRET','Directory.build.props']
  value=d.inventory_difference({},dict.fromkeys(paths,'a'*64),frozenset(('Directory.Build.props',)))
  self.assertTrue(all(x['pathClass']=='unknown-relative-hash-only' and 'knownRelativePath' not in x for x in value['records']))
  self.assertNotIn('SECRET',json.dumps(value));self.assertNotIn('Directory.build.props',json.dumps(value))
 def test_at_most_sixteen_records_complete_counts(self):
  value=d.inventory_difference({}, {f'PRIVATE/{x}':'a'*64 for x in range(30)},frozenset())
  self.assertEqual(value['differenceCount'],30);self.assertEqual(value['added'],30);self.assertEqual(len(value['records']),16);self.assertTrue(value['truncated'])
 def test_no_filesystem_access_or_equality_change(self):
  with patch.object(Path,'open',side_effect=AssertionError('no filesystem')),patch.object(Path,'resolve',side_effect=AssertionError('no resolve')):
   self.assertIsNone(d.require_inventory({'x':'a'*64},{'x':'a'*64},'Protected source/output changed','fixed','current',frozenset()))
   with self.assertRaises(ValueError) as caught:d.require_inventory({'x':'b'*64},{'x':'a'*64},'Protected source/output changed','fixed','current',frozenset())
  self.assertEqual(caught.exception.args,('Protected source/output changed',));self.assertEqual(caught.exception.protected_inventory.public()['changed'],1)
 def test_diagnostic_failure_preserves_original_guard_first(self):
  diagnostic=OSError('PRIVATE')
  with patch.object(d,'inventory_difference',side_effect=diagnostic):
   with self.assertRaises(BaseExceptionGroup) as caught:d.require_inventory({'x':'a'*64},{},'Source/protected output mutation','baseline','phase',frozenset())
  self.assertEqual(caught.exception.exceptions[0].args,('Source/protected output mutation',));self.assertIs(caught.exception.exceptions[1],diagnostic)
 def test_invalid_catalog_and_digest_reject_without_echo(self):
  for catalog in (frozenset(('../PRIVATE',)),frozenset(('/PRIVATE',)),frozenset(('D:/PRIVATE',)), {'safe'}):
   with self.assertRaises(ValueError):d.inventory_difference({}, {'PRIVATE':'a'*64},catalog)
  with self.assertRaises(ValueError):d.inventory_difference({}, {'PRIVATE':'not-hash'},frozenset())
 def test_real_inventory_still_excludes_only_declared_obj_and_rejects_other_mutation(self):
  with tempfile.TemporaryDirectory() as tmp:
   root=Path(tmp);(root/'.git').mkdir();obj=root/'project/obj';obj.mkdir(parents=True)
   known=root/'Directory.Build.props';known.write_text('old');before=r.inventory(root,[obj],checkout_metadata=True)
   (obj/'project.assets.json').write_text('allowed');self.assertEqual(r.inventory(root,[obj],checkout_metadata=True),before)
   known.write_text('new');actual=r.inventory(root,[obj],checkout_metadata=True)
   with self.assertRaises(ValueError) as caught:d.require_inventory(actual,before,'Source/protected output mutation','baseline','phase',r.KNOWN_DIAGNOSTIC_PATHS)
   self.assertEqual(caught.exception.protected_inventory.public()['records'][0]['knownRelativePath'],'Directory.Build.props')
 def test_current_and_phase_hooks_keep_same_original_inventory_arguments(self):
  import ast,inspect
  tree=ast.parse(inspect.getsource(r));calls=[n for n in ast.walk(tree) if isinstance(n,ast.Call) and isinstance(n.func,ast.Attribute) and n.func.attr=='require_inventory']
  self.assertEqual(len(calls),2)
  reasons={n.args[2].value for n in calls};self.assertEqual(reasons,{'Protected source/output changed','Source/protected output mutation'})
  self.assertEqual({n.args[4].value for n in calls},{'current','phase'})
  for n in calls:
   self.assertEqual(n.args[0].func.id,'inventory');self.assertEqual(n.args[0].keywords[0].arg,'checkout_metadata');self.assertIs(n.args[0].keywords[0].value.value,True)
  self.assertIn("'protected_inventory'",inspect.getsource(r.finish))
 def test_public_inventory_console_failure_cannot_mask_original(self):
  from types import SimpleNamespace
  try:d.require_inventory({'PRIVATE':'a'*64},{},'Protected source/output changed','baseline','current',frozenset())
  except ValueError as error:original=error
  sink=SimpleNamespace(write=lambda x:None,close=lambda:None)
  lane=SimpleNamespace(final_guards=lambda:[],stage='restore-baseline',results=[],commands_started=7,sink=sink)
  console=OSError('console')
  with patch('builtins.print',side_effect=console):
   with self.assertRaises(BaseExceptionGroup) as caught:r.finish(lane,[original])
  self.assertIs(caught.exception.exceptions[0],original);self.assertIs(caught.exception.exceptions[1],console)
 def test_fixed_tool_literals_uncoded_error_and_encoding_are_count_only(self):
  raw=('PRIVATE : error : PRIVATE\n'+d.TOOL_LITERALS['msbuild-fatal']+' PRIVATE').encode()
  value=d.stream_summary(raw);self.assertEqual(value['uncodedErrorLineCount'],1)
  self.assertEqual(value['fixedToolLiterals'],[{'category':'msbuild-fatal','count':1}]);self.assertNotIn('PRIVATE',json.dumps(value))
  for raw,bom in ((b'\xff\xfeA\0','utf16le'),(b'\xfe\xff\0A','utf16be'),(b'\xef\xbb\xbfA','utf8')):
   value=d.stream_summary(raw);self.assertEqual(value['encodingBOM'],bom);self.assertEqual(value['nulByteCount'],raw.count(b'\0'))
   self.assertEqual(value['fixedToolLiterals'],[]) # no speculative decoding or inferred root cause

if __name__=='__main__':unittest.main(verbosity=2)
