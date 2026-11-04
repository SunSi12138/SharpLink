import copy
import importlib.util
from pathlib import Path
import unittest
spec = importlib.util.spec_from_file_location('phase_runner', Path(__file__).with_name('run.py'))
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


def rows():
    shapes = [('process','process-start',-1,0)]
    for cell in ['I/T','P/T','I/U','P/U']:
        shapes += [(cell,'cell-start',-1,0),(cell,'warmup-end',-1,1000)]
        for chunk in range(5):
            shapes += [(cell,'chunk-start',chunk,1000+chunk*2000),(cell,'chunk-end',chunk,3000+chunk*2000)]
        shapes += [(cell,'cell-end',-1,11000)]
    shapes += [('process','process-end',-1,44000)]
    return [dict(sequence=i,cell=x[0],boundary=x[1],chunk=x[2],completed=x[3],timestamp=i+1,frequency=1000000000) for i,x in enumerate(shapes)]


class Validation(unittest.TestCase):
    def test_complete(self): runner.validate_phases(rows())
    def test_missing(self):
        with self.assertRaises(ValueError): runner.validate_phases(rows()[:-1])
    def test_wrong_cell(self):
        r=rows(); r[2]['cell']='P/T'
        with self.assertRaises(ValueError): runner.validate_phases(r)
    def test_wrong_count(self):
        r=rows(); r[3]['completed']=2000
        with self.assertRaises(ValueError): runner.validate_phases(r)
    def test_reordered(self):
        r=rows(); r[3],r[4]=r[4],r[3]
        with self.assertRaises(ValueError): runner.validate_phases(r)
    def test_clock(self):
        r=rows(); r[4]['timestamp']=1
        with self.assertRaises(ValueError): runner.validate_phases(r)
    def test_frequency(self):
        r=rows(); r[4]['frequency']=1
        with self.assertRaises(ValueError): runner.validate_phases(r)
    def test_no_timing_loop_change(self):
        p=Path(__file__).parent
        original = p/'original' if (p/'original').exists() else p.parent/'method-facts-causal'
        before=(original/'Program.cs').read_text()
        after=(p/'probe/Program.cs').read_text()
        self.assertEqual(before[before.index('    private static async Task RunCalls'):],after[after.index('    private static async Task RunCalls'):after.index('\n[EventSource')])
        self.assertEqual((original/'OriginalService.cs').read_bytes(),(p/'probe/OriginalService.cs').read_bytes())

if __name__=='__main__':unittest.main()
