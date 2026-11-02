"""Actual official restored closure delta; central declaration != one binary change."""
from pathlib import Path
from zipfile import ZipFile
import xml.etree.ElementTree as ET
import audit
from classify import require,read_json


def package_graph(root, packages, restored):
    """Keep all nuspec groups as evidence; assets, not this code, chooses the TFM."""
    manifests={}; graph={}
    for identity,receipt in restored['cache'].items():
        identifier,version=identity.rsplit('/',1)
        archive=packages/identifier/version/(identifier+'.'+version+'.nupkg')
        require(audit.digest(archive)==receipt['archiveSHA256'],'Archive changed before dependency graph audit')
        with ZipFile(archive) as z:
            names=[n for n in z.namelist() if n.lower().endswith('.nuspec') and '/' not in n]
            require(len(names)==1,'Ambiguous actual package dependency manifest')
            tree=ET.fromstring(z.read(names[0]))
        edges=[]
        for element in tree.iter():
            if element.tag.split('}')[-1]=='dependency':
                name=element.get('id');version_range=element.get('version')
                require(isinstance(name,str) and name and isinstance(version_range,str) and version_range,
                        'Missing actual nuspec dependency identity/range')
                edges.append({'id':name.lower(),'range':version_range})
        manifests[identity]={'dependencies':sorted(edges,key=lambda e:(e['id'],e['range'])),
                             'archiveSHA256':receipt['archiveSHA256']}
        graph.setdefault(identifier,set()).update(e['id'] for e in edges)
    focused=root/audit.PROJECTS[audit.NAMES[-1]]
    assets=read_json(focused.parent/'obj/project.assets.json')
    require(set(assets['targets'])=={'net10.0'},'Unexpected actual asset target graph')
    active={key.lower():value for key,value in assets['targets']['net10.0'].items()}
    packages_by_name={key.rsplit('/',1)[0].lower():key.lower() for key in restored['packages']}
    require(len(packages_by_name)==len(restored['packages']),'Multiple active versions of one package')
    selected={}
    for name,key in packages_by_name.items():
        item=active[key];deps=item.get('dependencies',{})
        require(isinstance(deps,dict),'Invalid actual dependency edge map')
        require(all(dep.lower() in graph.get(name,set()) for dep in deps),'Asset dependency not declared in official nuspec')
        require(all(dep.lower() in packages_by_name for dep in deps),'Unresolved actual active package dependency')
        selected[key]=sorted(packages_by_name[dep.lower()] for dep in deps)
    reachable=set();todo=['tunit']
    while todo:
        name=todo.pop()
        if name in reachable:continue
        reachable.add(name);todo.extend(graph.get(name,()))
    return {'manifests':manifests,'activeEdges':selected,'tunitReachableNames':sorted(reachable)}


def compare(reference, upgraded, ref_graph, upgrade_graph):
    require(reference['projectEdges']==upgraded['projectEdges'],'Upgrade changed actual production graph')
    def by_name(cache):
        result={}
        for key,receipt in cache.items():result.setdefault(key.rsplit('/',1)[0],{})[key]=receipt
        return result
    before=by_name(reference['cache']);after=by_name(upgraded['cache'])
    changed=sorted(name for name in before.keys()|after.keys() if before.get(name)!=after.get(name))
    reachable=set(ref_graph['tunitReachableNames'])|set(upgrade_graph['tunitReachableNames'])
    require(changed and all(name in reachable for name in changed),'Unexplained package drift outside TUnit dependency closure')
    for key in reference['cache'].keys() & upgraded['cache'].keys():
        require(reference['cache'][key]==upgraded['cache'][key],'Same official package identity changed bytes/content hash')
    return {'changedPackageNames':changed,'beforeActive':reference['packages'],'afterActive':upgraded['packages'],
            'beforeCache':sorted(reference['cache']),'afterCache':sorted(upgraded['cache']),
            'qualification':'central TUnit declaration only; actual transitive dependency/cache delta recorded, not one runtime binary change'}
