#!/usr/bin/env python3
"""Lossless LOD0/module/plate gates for the additive 11.20 port.

Compares source SCG face counts with the shipped OBJ, not just with the GLB
that the same converter emitted. Fails closed on missing streams, unknown
visible nodes, missing wheels, duplicate slots or altered plate topology.
"""
import collections
import json
import re
import sys
from pathlib import Path
from xml.etree import ElementTree as ET
import numpy as np
from PIL import Image
from scipy.spatial import cKDTree
import port_blitz_1120 as p
import pvr2png
import audit_collision_alignment as alignment
import audit_tank_visible_geometry as visible
import blitz_1120_collision as plates


def counts(path):
    result = collections.Counter()
    current = None
    for line in path.read_text().splitlines():
        if line.startswith('o '):
            current=line[2:]
        elif line.startswith('f '):
            result[current]+=1
    return result


def obj_corners(path):
    verts=[]
    uvs=[]
    normals=[]
    rows=collections.defaultdict(list)
    current=None
    for line in path.read_text().splitlines():
        if line.startswith('o '): current=line[2:]
        elif line.startswith('v '): verts.append(tuple(map(float,line.split()[1:])))
        elif line.startswith('vt '): uvs.append(tuple(map(float,line.split()[1:])))
        elif line.startswith('vn '): normals.append(tuple(map(float,line.split()[1:])))
        elif line.startswith('f '):
            for token in line.split()[1:]:
                v,t,n=token.split('/')
                rows[current].append(verts[int(v)-1]+uvs[int(t)-1]+normals[int(n)-1])
    return rows


def run():
    p.setup_exporter()
    records = json.loads(p.INVENTORY.read_text())['records']
    cards = {c['name']:c for c in json.loads(p.CARD_FILE.read_text())['tanks']}
    errors=[]
    report=[]
    for r in records:
        p.configure(r)
        card=cards[r['name']]
        tank_id=p.exporter.unity_id(r['name'])
        model= p.ROOT / f'Assets/Blitz/Resources/Models/Tanks/{tank_id}/{tank_id}.obj'
        manifest=json.loads((p.DATA/'tank_models_source_manifest.json').read_text())['tanks'][r['name']]
        for texture in manifest['textures']:
            _header,w,h,pixels=pvr2png.decode(str(p.PACK/texture['source']),0,False)
            image=Image.open(model.parent/'Textures'/texture['destination']).convert('RGBA')
            if texture['destination'].endswith('_tracks.png') and image.size == (w,h):
                # Alpha and drawn texels must match the source exactly; RGB under
                # the clip must carry the drawn paint (no white filter fringe).
                import numpy as np
                got=np.array(image).astype(int); src=np.frombuffer(bytes(pixels),np.uint8).reshape(h,w,4).astype(int)
                drawn=src[:,:,3]>=p.exporter.TRACK_CUTOFF
                if (got[:,:,3]!=src[:,:,3]).any() or (got[drawn]!=src[drawn]).any():
                    errors.append(f'{r["name"]}/{texture["destination"]}: decoded source alpha/drawn pixels changed')
                ring=0; bad=0
                for y,x in zip(*np.nonzero(~drawn)):
                    ns=[((y+dy)%h,(x+dx)%w) for dy,dx in ((1,0),(-1,0),(0,1),(0,-1))]
                    ns=[n for n in ns if drawn[n]]
                    if not ns: continue
                    ring+=1
                    avg=sum(got[n][:3] for n in ns)//len(ns)
                    if (abs(got[y,x,:3]-avg)>1).any(): bad+=1
                if bad:
                    errors.append(f'{r["name"]}/{texture["destination"]}: {bad}/{ring} clipped edge texels not bled')
            elif image.size != (w,h) or image.tobytes() != bytes(pixels):
                errors.append(f'{r["name"]}/{texture["destination"]}: decoded source pixels changed')
        actual=counts(model)
        actual_corners=obj_corners(model)
        expected_corners=collections.defaultdict(list)
        groups=p.blitz2glb.read_groups(str((p.PACK/r['scene']).with_suffix('.scg')))
        nodes=p.blitz2glb.read_scene(str(p.PACK/r['scene']),0)
        expected=collections.Counter()
        def measure(out,sources):
            for n in sources:
                for gid,mat,lod in n['batches']:
                    if gid not in groups:
                        errors.append(f'{r["name"]}: missing source group {gid}')
                        continue
                    stream=p.dava.decode_vertices(groups[gid])
                    ind=p.dava.decode_indices(groups[gid])
                    if not stream.get('uv0'):
                        errors.append(f'{r["name"]}/{n["name"]}: LOD0 visible UV missing')
                    if len(ind)%3 or any(i>=len(stream['position']) for i in ind):
                        errors.append(f'{r["name"]}/{n["name"]}: bad source indices')
                    expected[out]+=len(ind)//3
                    for index in ind:
                        x,y,z=stream['position'][index]
                        u,v=stream['uv0'][index]
                        nx,ny,nz=stream['normal'][index]
                        length=max((nx*nx+ny*ny+nz*nz)**0.5, 1e-30)
                        expected_corners[out].append((x,z,-y,u,1-v,nx/length,nz/length,-ny/length))
        measure('hull_vis',p.blitz2glb.pick_part(nodes,'hull','chassis_chassis_L','chassis_chassis_R'))
        for out,names in [('trackL',['chassis_track_L']),('trackR',['chassis_track_R']),('tracks_crash',['chassis_track_crash_L','chassis_track_crash_R'])]:
            measure(out,p.blitz2glb.pick(nodes,*names))
        for node in nodes:
            name=p.blitz2glb.wheel_name(node['name'])
            if name:
                measure(name,[node])
        seen=set()
        for turret in card['turrets']:
            slot=turret['mesh']
            measure(slot,p.blitz2glb.pick_part(nodes,slot))
            for gun in turret['guns']:
                slot=gun['mesh']
                if slot not in seen:
                    seen.add(slot)
                    measure(slot,p.blitz2glb.pick_part(nodes,slot,slot+'_mask'))
        for name,number in expected.items():
            if actual[name]!=number:
                errors.append(f'{r["name"]}/{name}: source {number} != OBJ {actual[name]} triangles')
        for name,source_corners in expected_corners.items():
            if source_corners and actual_corners.get(name):
                source=np.asarray(source_corners)
                destination=np.asarray(actual_corners[name])
                distance=max(float(cKDTree(source).query(destination)[0].max()),float(cKDTree(destination).query(source)[0].max()))
                if distance>0.00002:
                    errors.append(f'{r["name"]}/{name}: position/UV/normal drift {distance:.7f}')
        for name in actual:
            if name not in expected:
                errors.append(f'{r["name"]}: unexpected exported object {name}')
        for name,number in visible.obj_duplicates(model).items():
            errors.append(f'{r["name"]}/{name}: duplicate object count {number}')
        # Independent collision source comparison: per native ID, per mount.
        cg=p.blitz2glb.read_groups(str((p.PACK/r['collision']).with_suffix('.scg')))
        cn=p.blitz2glb.read_scene(str(p.PACK/r['collision']),0)
        cm={n['name']:n for n in cn}
        table=json.loads((p.DATA/(tank_id.lower()+'_armor.json')).read_text())
        root=ET.parse(p.exporter.VEHICLES/(r['vehicle_id']+'.xml')).getroot()
        native=collections.Counter()
        wanted={'hull'} | {t['mesh'] for t in card['turrets']} | seen
        for mount in wanted:
            for gid,mat,lod in cm.get(mount,{}).get('batches',[]):
                stream=p.dava.decode_vertices(cg[gid]);idx=p.dava.decode_indices(cg[gid]);ids=stream['hardJointIndex']
                for off in range(0,len(idx),3):
                    values={int(ids[i][0]) for i in idx[off:off+3]}
                    if len(values)!=1:
                        errors.append(f'{r["name"]}: mixed source plate IDs')
                    contexts = [t['mesh'] for t in card['turrets'] if any(g['mesh']==mount for g in t['guns'])] if mount in seen else ['']
                    for context in contexts:
                        native[(mount,context,next(iter(values)))]+=1
        output=collections.Counter()
        for panel in table['plates']:
            if 'source_plate_id' not in panel:
                if panel.get('module')!='tracks':
                    errors.append(f'{r["name"]}: plate without source ID {panel["name"]}')
                continue
            output[(panel['mount'],panel.get('required_turret',''),panel['source_plate_id'])]+=panel['triangles']
            if panel['armor']!=round(panel['source_armor']):
                errors.append(f'{r["name"]}: armour numeric drift {panel["name"]}')
        for key in native.keys()|output.keys():
            if native[key]!=output[key]:
                errors.append(f'{r["name"]}/{key}: source collision {native[key]} != exported {output[key]}')
        # Author-provided XML pivots must reach the runtime node table exactly.
        nt=json.loads((p.DATA/(tank_id.lower()+'_nodes.json')).read_text())['nodes']
        for turret in root.find('turrets0'):
            for xml,space in [(turret,'local')]+[(g,'world') for g in turret.find('guns')]:
                slot=p.exporter.part_slot(xml);pivot=p.exporter.authored_pivot(xml)
                if pivot is not None:
                    expected_p=(pivot[0],pivot[2],-pivot[1])
                    if max(abs(a-b) for a,b in zip(nt[slot][space],expected_p))>.002:
                        errors.append(f'{r["name"]}/{slot}: authored pivot mismatch')
        suspects=[x for x in alignment.audit(model.parent) if alignment.suspect(x)]
        if suspects:
            errors.extend(f'{r["name"]}: alignment {s}' for s in suspects)
        report.append({'name':r['name'],'visible_triangles':sum(actual.values()),
                       'source_triangles':sum(expected.values()),
                       'wheels':sum(name.startswith(('wheelL','wheelR')) for name in actual),
                       'collision_triangles':sum(native.values()),'exact_plates':len(output),
                       'configurations':sum(len(t['guns']) for t in card['turrets'])})
    p.save(p.DATA/'blitz_1120_geometry_audit.json',{'policy':'independent SCG LOD0 face counts and positions/UV/normals within 0.00002 + decoded source RGBA (track RGB bled under the clip) + native collision plate IDs + XML pivots',
                                                'tanks':report,'errors':errors})
    print(json.dumps({'tanks':len(report),'configurations':sum(r['configurations'] for r in report),
                      'wheels':sum(r['wheels'] for r in report),'errors':len(errors)}))
    for error in errors:
        print(error)
    return bool(errors)

if __name__=='__main__':
    raise SystemExit(run())
