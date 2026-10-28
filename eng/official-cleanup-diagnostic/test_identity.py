import copy
from pathlib import Path
import unittest
from identity import FRAMEWORK, FRAMEWORK_PRODUCT_VERSION, validate_loaded, validate_policy


class IdentityTests(unittest.TestCase):
    def setUp(self):
        self.runtime = Path('/private/dotnet')
        self.policy = {n:dict(path=f'/private/shared/Microsoft.AspNetCore.App/10.0.14/{n}.dll',sha256=h,mvid=m,productVersion=FRAMEWORK_PRODUCT_VERSION)
                       for n,(h,m) in FRAMEWORK.items()}
        self.loaded = [dict(name=n,location=p['path'],sha256=p['sha256'],mvid=p['mvid'],productVersion=p['productVersion']) for n,p in self.policy.items()]
        self.snapshot = {}
        for name in ('SharpLink.Abstractions','SharpLink.Client','SharpLink.Runtime','SharpLink.Server','PhysicalTcpProbe','ScalarStreamModule'):
            self.snapshot[name+'.dll'] = name
            self.loaded.append(dict(name=name,location='/prepared/official/'+name+'.dll',sha256=name,mvid='one',productVersion='2.0.3+source'))

    def test_fixed_policy_and_loaded_closure(self):
        validate_policy(self.policy, self.runtime)
        validate_loaded(self.loaded,'official',Path('/prepared'),self.snapshot,'source',self.policy,self.runtime)

    def test_policy_cannot_whitelist_arbitrary_origin(self):
        for field in ('path','sha256','mvid','productVersion'):
            bad=copy.deepcopy(self.policy);bad[next(iter(bad))][field]='wrong'
            with self.assertRaises(ValueError):validate_policy(bad,self.runtime)

    def test_framework_cannot_be_relabeled_or_loaded_from_snapshot(self):
        for field,value in [('location','/prepared/official/Microsoft.Extensions.Logging.Abstractions.dll'),
                            ('location','/private/shared/Microsoft.AspNetCore.App/10.0.14/wrong.dll'),
                            ('sha256','wrong'),('mvid','wrong'),('productVersion','10.0.12')]:
            bad=copy.deepcopy(self.loaded);bad[0][field]=value
            with self.assertRaises(ValueError):validate_loaded(bad,'official',Path('/prepared'),self.snapshot,'source',self.policy,self.runtime)

    def test_product_cannot_escape_own_snapshot_or_source(self):
        for field,value in [('location','/prepared/dev/SharpLink.Server.dll'),('productVersion','3.0.0+wrong'),('sha256','wrong')]:
            bad=copy.deepcopy(self.loaded);bad[-3][field]=value
            with self.assertRaises(ValueError):validate_loaded(bad,'official',Path('/prepared'),self.snapshot,'source',self.policy,self.runtime)


if __name__ == '__main__':unittest.main()
