"""Offline closure regression: private12 references, exact shared-framework14 execution."""
import copy
import io
import hashlib
import tarfile
from unittest.mock import patch
import identity
from pathlib import Path
import tempfile
import unittest
from identity import FRAMEWORK, IdentityError, framework_policy, validate_policy, archive_framework_policy, validate_framework_assemblies
import test_protocol
from validate import validate_sample


class IdentityTests(unittest.TestCase):
    def sample(self, arm='official'):
        return list(test_protocol.ProtocolTests().sample(arm))

    def test_old_snapshot_only_rule_rejects_valid_historical_framework14_closure(self):
        for arm in ('official','dev'):
            args=self.sample(arm)
            data,_,prepared,manifest,runtime,policy,*_=args
            framework=[assembly for assembly in data['loadedAssemblies'] if assembly['name'] in FRAMEWORK]
            self.assertEqual(4,len(framework))
            # This is exactly the former faulty predicate, not a second RPC/probe run.
            self.assertTrue(all(Path(a['location']).parent != prepared/arm for a in framework))
            self.assertTrue(all(a['sha256'] != manifest['arms'][arm]['snapshot'][a['name']+'.dll'] for a in framework))
            validate_policy(policy,runtime)
            validate_sample(*args)

    def test_every_framework_policy_field_is_fixed_not_user_whitelisted(self):
        for name in FRAMEWORK:
            for field in ('path','sha256','mvid','productVersion'):
                args=self.sample(); args[5][name][field]='wrong'
                with self.subTest(name=name,field=field),self.assertRaises(IdentityError): validate_sample(*args)
        for policy in (None,{},[],{'undeclared':{}}):
            args=self.sample();args[5]=policy
            with self.assertRaises(IdentityError): validate_sample(*args)
        args=self.sample(); args[5]['undeclared']=copy.deepcopy(next(iter(args[5].values())))
        with self.assertRaises(IdentityError):validate_sample(*args)

    def test_each_framework_loaded_identity_rejects_wrong_hash_mvid_version_or_origin(self):
        for arm in ('official','dev'):
            original=self.sample(arm)
            for index,assembly in enumerate(original[0]['loadedAssemblies']):
                if assembly['name'] not in FRAMEWORK: continue
                name=assembly['name']
                for field,value in [
                    ('location',str(original[2]/arm/(name+'.dll'))),
                    ('location','/usr/share/dotnet/shared/Microsoft.AspNetCore.App/10.0.14/'+name+'.dll'),
                    ('location','/runtime/shared/Microsoft.AspNetCore.App/10.0.12/'+name+'.dll'),
                    ('location','/runtime/shared/Microsoft.AspNetCore.App/10.0.14/wrong.dll'),
                    ('sha256','0'*64),('mvid','00000000-0000-0000-0000-000000000000'),
                    ('productVersion','10.0.12'),('productVersion','10.0.14+wrong')]:
                    args=copy.deepcopy(original);args[0]['loadedAssemblies'][index][field]=value
                    with self.subTest(arm=arm,name=name,field=field,value=value),self.assertRaises(IdentityError):
                        validate_sample(*args)

    def test_actual_runtime_location_must_match_fixed_policy(self):
        args=self.sample();args[4]=Path('/other-runtime/dotnet')
        # Framework locations are separately pinned by validate_sample as well.
        with self.assertRaises(ValueError): validate_sample(*args)
        with self.assertRaises(IdentityError): validate_policy(args[5],args[4])

    def test_own_product_and_fixture_identity_remain_unchanged(self):
        for arm in ('official','dev'):
            original=self.sample(arm)
            for index,assembly in enumerate(original[0]['loadedAssemblies']):
                if assembly['name'] in FRAMEWORK: continue
                name=assembly['name']
                mutations=[('location','/prepared/'+('dev' if arm=='official' else 'official')+'/'+name+'.dll'),
                           ('location','/runtime/shared/Microsoft.AspNetCore.App/10.0.14/'+name+'.dll'),
                           ('sha256','wrong'),('mvid','')]
                if name.startswith('SharpLink.'):mutations.append(('productVersion','3.0.0+wrong-source'))
                for field,value in mutations:
                    args=copy.deepcopy(original);args[0]['loadedAssemblies'][index][field]=value
                    with self.subTest(arm=arm,name=name,field=field),self.assertRaises(IdentityError):validate_sample(*args)

    def test_no_extra_or_duplicate_assemblies_even_if_in_snapshot(self):
        args=self.sample()
        extra=dict(name='Undeclared.Library',location='/prepared/official/Undeclared.Library.dll',
                   sha256='extra',mvid='01234567-89ab-cdef-0123-456789abcdef',productVersion='1')
        args[0]['binaryHashes']['Undeclared.Library.dll']='extra'
        args[3]['arms']['official']['snapshot']['Undeclared.Library.dll']='extra'
        args[0]['loadedAssemblies'].append(extra)
        with self.assertRaises(IdentityError):validate_sample(*args)
        args=self.sample();args[0]['loadedAssemblies'].append(copy.deepcopy(args[0]['loadedAssemblies'][0]))
        with self.assertRaises(IdentityError):validate_sample(*args)

    def test_all_required_assemblies_present_sdk_optional(self):
        args=self.sample()
        for index,assembly in enumerate(args[0]['loadedAssemblies']):
            changed=copy.deepcopy(args);changed[0]['loadedAssemblies'].pop(index);changed[0]['loadedAssemblyCount']-=1
            if assembly['name']=='SharpLink.Sdk':validate_sample(*changed)
            else:
                with self.subTest(name=assembly['name']),self.assertRaises(IdentityError):validate_sample(*changed)

    def test_snapshot_still_checks_all_four_private_reference_files(self):
        for name in FRAMEWORK:
            args=self.sample();args[0]['binaryHashes']=dict(args[0]['binaryHashes'])
            args[0]['binaryHashes'][name+'.dll']='wrong'
            with self.assertRaises(ValueError):validate_sample(*args)

    def test_disk_materializer_rejects_unverified_bytes_without_policy_patch(self):
        with tempfile.TemporaryDirectory() as td:
            runtime=Path(td)/'dotnet'
            directory=runtime.parent/'shared/Microsoft.AspNetCore.App/10.0.14';directory.mkdir(parents=True)
            for name in FRAMEWORK:(directory/(name+'.dll')).write_bytes(b'unverified')
            with self.assertRaises(IdentityError) as caught:framework_policy(runtime)
            self.assertEqual('framework_disk_hash',caught.exception.code)


    def test_all_loaded_record_fields_reject_malformed_types_without_attribute_errors(self):
        for name in ('SharpLink.Client','PhysicalTcpProbe',next(iter(FRAMEWORK))):
            for key in ('name','location','sha256','mvid','productVersion'):
                for value in (None,12,[],{}):
                    args=self.sample(); record=next(a for a in args[0]['loadedAssemblies'] if a['name']==name)
                    record[key]=value
                    with self.subTest(name=name,key=key,value=value),self.assertRaises(IdentityError):validate_sample(*args)

    def test_mvid_requires_canonical_lowercase_uuid_shape(self):
        for value in ('nonempty','0123456789abcdef0123456789abcdef',
                      '01234567-89AB-CDEF-0123-456789ABCDEF','g1234567-89ab-cdef-0123-456789abcdef',
                      '01234567-89ab-cdef-0123-456789abcdef-extra'):
            args=self.sample(); record=next(a for a in args[0]['loadedAssemblies'] if a['name']=='SharpLink.Client')
            record['mvid']=value
            with self.subTest(value=value),self.assertRaises(IdentityError):validate_sample(*args)

    def test_own_and_known_framework_paths_reject_lexical_aliases(self):
        for name in ('SharpLink.Client',next(iter(FRAMEWORK))):
            for rewrite in (lambda p:p.replace('/','//',1),lambda p:p.rsplit('/',1)[0]+'/../'+p.rsplit('/',2)[1]+'/'+p.rsplit('/',1)[1]):
                args=self.sample();record=next(a for a in args[0]['loadedAssemblies'] if a['name']==name)
                record['location']=rewrite(record['location'])
                with self.assertRaises(IdentityError):validate_sample(*args)


class ArchiveIdentityTests(unittest.TestCase):
    core='shared/Microsoft.NETCore.App/10.0.14/System.Private.CoreLib.dll'
    asp='shared/Microsoft.AspNetCore.App/10.0.14/Microsoft.AspNetCore.dll'

    def tar(self,entries):
        stream=io.BytesIO()
        with tarfile.open(fileobj=stream,mode='w:gz') as archive:
            root=tarfile.TarInfo('./');root.type=tarfile.DIRTYPE;archive.addfile(root)
            for name,content,kind in entries:
                info=tarfile.TarInfo(name);info.type=kind
                if kind in (tarfile.SYMTYPE,tarfile.LNKTYPE):info.linkname='untrusted-target'
                info.size=len(content) if kind==tarfile.REGTYPE else 0
                archive.addfile(info,io.BytesIO(content) if kind==tarfile.REGTYPE else None)
        return stream.getvalue()

    def prepare(self,root,core_entries=None,asp_entries=None):
        runtime=root/'runtime/dotnet';runtime.parent.mkdir()
        archives=root/'archives';archives.mkdir()
        core_entries=core_entries if core_entries is not None else [('./'+self.core,b'core',tarfile.REGTYPE)]
        asp_entries=asp_entries if asp_entries is not None else [('./'+self.core,b'core',tarfile.REGTYPE),('./'+self.asp,b'asp',tarfile.REGTYPE)]
        pins={}
        for filename,entries in [('dotnet.tar.gz',core_entries),('aspnetcore.tar.gz',asp_entries)]:
            payload=self.tar(entries);(archives/filename).write_bytes(payload);pins[filename]=hashlib.sha512(payload).hexdigest()
        for name,content in [(self.core,b'core'),(self.asp,b'asp')]:
            path=runtime.parent/name;path.parent.mkdir(parents=True,exist_ok=True);path.write_bytes(content)
        return runtime,archives,pins

    def policy(self):return {self.core:hashlib.sha256(b'core').hexdigest(),self.asp:hashlib.sha256(b'asp').hexdigest()}

    def records(self,runtime=Path('/runtime/dotnet')):
        return [dict(name=Path(path).stem,location=str(runtime.parent/path),sha256=sha) for path,sha in self.policy().items()]

    def test_two_archive_policy_allows_identical_core_duplicates_and_checks_actual_files(self):
        with tempfile.TemporaryDirectory() as td:
            runtime,archives,pins=self.prepare(Path(td))
            # Tiny synthetic archives exercise parsing/extracted-file validation; production
            # entry points expose no pin override. Actual frozen archives are separately checked.
            with patch.dict(identity.ARCHIVE_SHA512,pins,clear=True):
                policy=archive_framework_policy(runtime,archives)
                self.assertEqual(self.policy(),policy)
                validate_framework_assemblies(self.records(runtime),runtime,policy)
                (runtime.parent/self.core).write_bytes(b'tampered')
                with self.assertRaises(IdentityError):archive_framework_policy(runtime,archives)

    def test_production_archive_pins_reject_unapproved_archives(self):
        self.assertEqual('634326028645f38731c8c4e71a68356e4ca0d6b6607139a234ab760687f742063edaf33a1f20dda615742d2dc8300ca2ff857619d51fbb38090383b71334af89',identity.ARCHIVE_SHA512['dotnet.tar.gz'])
        self.assertEqual('dd024fd2616926a555b75cbf7a836f6dce998199f0596d9d75676dca2b9d59c8ee8673c6ac23473c0f1a4bdd59eb5cd02fe9ab097eeaf2dd8c8bd117c85e7031',identity.ARCHIVE_SHA512['aspnetcore.tar.gz'])
        with tempfile.TemporaryDirectory() as td:
            runtime,archives,_=self.prepare(Path(td))
            with self.assertRaises(IdentityError) as caught:archive_framework_policy(runtime,archives)
            self.assertEqual('archive_integrity',caught.exception.code)

    def test_archive_rejects_traversal_noncanonical_versions_links_and_conflicting_duplicates(self):
        cases=[[(name,b'bad',tarfile.REGTYPE)] for name in ('../bad.dll','/bad.dll','././'+self.core,
               'shared//Microsoft.NETCore.App/10.0.14/Bad.dll',self.core.replace('10.0.14','10.0.13'),
               'shared/Microsoft.NETCore.App/10.0.14/../Bad.dll','shared/Unknown/10.0.14/Bad.dll')]
        cases += [[(self.core,b'',kind)] for kind in (tarfile.SYMTYPE,tarfile.LNKTYPE,tarfile.FIFOTYPE)]
        cases += [[(self.core,b'core',tarfile.REGTYPE),(self.core,b'conflict',tarfile.REGTYPE)]]
        for entries in cases:
            with self.subTest(entries=entries),tempfile.TemporaryDirectory() as td:
                runtime,archives,pins=self.prepare(Path(td),core_entries=entries)
                with patch.dict(identity.ARCHIVE_SHA512,pins,clear=True),self.assertRaises(IdentityError):
                    archive_framework_policy(runtime,archives)

    def test_extracted_symlink_or_parent_symlink_is_rejected(self):
        for parent_link in (False,True):
            with tempfile.TemporaryDirectory() as td:
                root=Path(td);runtime,archives,pins=self.prepare(root)
                target=runtime.parent/self.core
                if parent_link:
                    directory=target.parent;destination=root/'moved';directory.rename(destination);directory.symlink_to(destination,target_is_directory=True)
                else:
                    destination=root/'real-core';target.rename(destination);target.symlink_to(destination)
                with patch.dict(identity.ARCHIVE_SHA512,pins,clear=True),self.assertRaises(IdentityError):
                    archive_framework_policy(runtime,archives)

    def test_framework_records_reject_foo_even_present_in_framework_directory(self):
        with tempfile.TemporaryDirectory() as td:
            runtime,archives,pins=self.prepare(Path(td))
            with patch.dict(identity.ARCHIVE_SHA512,pins,clear=True):policy=archive_framework_policy(runtime,archives)
            foo=runtime.parent/'shared/Microsoft.NETCore.App/10.0.14/Foo.dll';foo.write_bytes(b'foo')
            records=self.records(runtime)+[dict(name='Foo',location=str(foo),sha256=hashlib.sha256(b'foo').hexdigest())]
            with self.assertRaises(IdentityError):validate_framework_assemblies(records,runtime,policy)

    def test_framework_record_hash_path_anchor_duplicate_and_types_fail_closed(self):
        runtime=Path('/runtime/dotnet');policy=self.policy();records=self.records(runtime)
        validate_framework_assemblies(records,runtime,policy)
        for key,value in [('sha256','0'*64),('name','Wrong'),('location','/other/'+self.core),
                           ('location','/runtime/shared/Microsoft.NETCore.App/10.0.14/../10.0.14/System.Private.CoreLib.dll')]:
            bad=copy.deepcopy(records);bad[0][key]=value
            with self.assertRaises(IdentityError):validate_framework_assemblies(bad,runtime,policy)
        for key in ('name','location','sha256'):
            for value in (None,1,[],{}):
                bad=copy.deepcopy(records);bad[0][key]=value
                with self.assertRaises(IdentityError):validate_framework_assemblies(bad,runtime,policy)
        for bad in (records[:1],[],records+[records[0]],None):
            with self.assertRaises(IdentityError):validate_framework_assemblies(bad,runtime,policy)

    def test_framework_record_existing_symlink_is_rejected(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td);runtime,_,_=self.prepare(root)
            target=runtime.parent/self.core;destination=root/'real';target.rename(destination);target.symlink_to(destination)
            with self.assertRaises(IdentityError):validate_framework_assemblies(self.records(runtime),runtime,self.policy())


if __name__=='__main__':unittest.main()
