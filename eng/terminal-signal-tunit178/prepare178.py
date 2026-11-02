"""NEW three-cell preparation; exact old fixture/preparer stays unchanged."""
import argparse
import hashlib
from pathlib import Path
import sys
import prepare

VERSIONS = {'reference':'1.73.19', 'baseline':'1.78.0', 'fixed':'1.78.0'}
AFTER_PACKAGES_BLOB = 'ec1efcd161c38e72b1d7cd4db3a0d5bda7b41229'


def upgrade_bytes(before):
    before_blob=hashlib.sha1(b'blob '+str(len(before)).encode()+b'\0'+before).hexdigest()
    prepare.require(before_blob==prepare.PACKAGES_BLOB,'Wrong central package preimage bytes')
    old=b'<PackageVersion Include="TUnit" Version="1.73.19" />'
    prepare.require(before.count(old)==1,'Ambiguous TUnit package declaration')
    after=before.replace(old,b'<PackageVersion Include="TUnit" Version="1.78.0" />')
    prepare.require(hashlib.sha1(b'blob '+str(len(after)).encode()+b'\0'+after).hexdigest()==AFTER_PACKAGES_BLOB,
                    'Package postimage differs from exact PR971')
    return after


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--checkout',type=Path,required=True)
    parser.add_argument('--variant',choices=tuple(VERSIONS),required=True)
    parser.add_argument('--manifest-sha256',required=True)
    args=parser.parse_args()
    original=sys.argv
    try:
        sys.argv=[str(Path(__file__).with_name('prepare.py')),'--checkout',str(args.checkout),
                  '--variant','baseline' if args.variant=='baseline' else 'fixed',
                  '--manifest-sha256',args.manifest_sha256]
        prepare.main()
    finally:sys.argv=original
    root=args.checkout.resolve(strict=True)
    packages=root/'Directory.Packages.props'
    prepare.require(prepare.blob(packages)==prepare.PACKAGES_BLOB,'Wrong central package preimage')
    if VERSIONS[args.variant]=='1.78.0':
        after=upgrade_bytes(packages.read_bytes())
        packages.write_bytes(after)
    expected=[] if args.variant=='baseline' else [prepare.COORDINATOR]
    if args.variant!='reference':expected.append('Directory.Packages.props')
    prepare.require(sorted(prepare.git(root,'diff','--name-only').splitlines())==sorted(expected),
                    'Unexpected three-cell source mutation')
    prepare.require(prepare.blob(packages)==(prepare.PACKAGES_BLOB if args.variant=='reference' else AFTER_PACKAGES_BLOB),
                    'Wrong prepared package identity')
    print('THREE_CELL_SOURCE_PREPARED '+args.variant+' TUnit='+VERSIONS[args.variant])


if __name__=='__main__':main()
