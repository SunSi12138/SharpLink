"""Strict dependency origin policy for the observed actual framework14 closure."""
import hashlib
from pathlib import Path

FRAMEWORK_PRODUCT_VERSION = '10.0.14+0427b9abbead3fa64c6655eb4290c537397131fa'
FRAMEWORK = {
    'Microsoft.Extensions.Logging.Abstractions': ('c014eba79da8764b050bee1f1f70db623dd5026e855b5bc7bf463b10913643b2', '9c0203ae-8da7-43b6-ba77-11ebeca53df9'),
    'Microsoft.Extensions.DependencyInjection': ('12a361d88bf9c0ef4f4efe388415aa3820bb15c115002be4fd6f219fab527170', '7b80704b-1f41-48f7-9b28-da04ae40188e'),
    'Microsoft.Extensions.DependencyInjection.Abstractions': ('8bc15b2a240737a54c253711054d140db8995055d0209d98f9148e04daec27f4', 'e74460a9-5d22-43dd-b598-66467a162477'),
    'System.Threading.RateLimiting': ('6d7816840be1cc3e03693c99e5949e4bf6c4833ce79a0a1c3dd6e925032201c3', '0798f765-10d2-4f7a-aa9e-9a7639dc46b2'),
}


def framework_policy(runtime):
    directory = runtime.parent / 'shared/Microsoft.AspNetCore.App/10.0.14'
    policy = {}
    for name, (sha, mvid) in FRAMEWORK.items():
        path = directory / (name + '.dll')
        if hashlib.sha256(path.read_bytes()).hexdigest() != sha:
            raise ValueError(f'Private framework14 hash mismatch {name}')
        policy[name] = dict(path=str(path), sha256=sha, mvid=mvid, productVersion=FRAMEWORK_PRODUCT_VERSION)
    return policy


def validate_policy(policy, runtime):
    if set(policy) != set(FRAMEWORK):
        raise ValueError('Unexpected framework dependency policy closure')
    for name, (sha, mvid) in FRAMEWORK.items():
        expected = dict(path=str(runtime.parent / 'shared/Microsoft.AspNetCore.App/10.0.14' / (name + '.dll')),
                        sha256=sha, mvid=mvid, productVersion=FRAMEWORK_PRODUCT_VERSION)
        if policy[name] != expected:
            raise ValueError(f'Policy differs from fixed framework14 origin: {name}')


def validate_loaded(loaded, arm, prepared, snapshot, source, policy, runtime):
    validate_policy(policy, runtime)
    required = {'SharpLink.Abstractions', 'SharpLink.Client', 'SharpLink.Runtime', 'SharpLink.Server',
                'PhysicalTcpProbe', 'ScalarStreamModule'} | set(FRAMEWORK)
    names = [a['name'] for a in loaded]
    if len(names) != len(set(names)) or not required <= set(names):
        raise ValueError('Missing/duplicate executed identity')
    for assembly in loaded:
        name = assembly['name']
        if Path(assembly['location']).name != name + '.dll':
            raise ValueError('Loaded path basename does not match assembly identity')
        if name in FRAMEWORK:
            expected = policy[name]
            if (assembly['location'], assembly['sha256'], assembly['mvid'], assembly['productVersion']) != (
                    expected['path'], expected['sha256'], expected['mvid'], expected['productVersion']):
                raise ValueError(f'Framework identity differs from fixed private14 policy: {name}')
        else:
            path = Path(assembly['location'])
            if path.parent != prepared / arm or assembly['sha256'] != snapshot.get(path.name) or not assembly['mvid']:
                raise ValueError(f'Product/fixture escaped own-arm snapshot: {name}')
            if name.startswith('SharpLink.') and assembly['productVersion'].split('+')[-1] != source:
                raise ValueError('Wrong product source identity')
