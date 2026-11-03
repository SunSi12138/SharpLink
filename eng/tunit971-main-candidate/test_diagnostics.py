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

if __name__=='__main__':unittest.main(verbosity=2)
