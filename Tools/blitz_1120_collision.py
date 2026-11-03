"""Exact SCG collision plate IDs (hardJointIndex), not normal-zone guesses.

Blitz 11.20 collision groups carry a constant integer plate ID on all three
corners of each face. That ID addresses XML armor_<id>, and the XML damage
factor distinguishes an external screen from HP-bearing oscillating armour.
"""
import collections
import re
from pathlib import Path
from xml.etree import ElementTree as ET
import blitz2glb as b
import dava

ACTIVE = {}


def values(node):
    armor = node.find('armor')
    result = {int(p.tag.split('_')[-1]): (float((p.text or '').split()[0]),
              float(p.findtext('vehicleDamageFactor', '1')))
              for p in armor if re.fullmatch(r'armor_\d+',p.tag)} if armor is not None else {}
    # The client reserves hardJointIndex=-1 for the damageable gun barrel.
    # Its authored thickness is armor/gun, not an invented armor_-1 entry.
    gun = armor.find('gun') if armor is not None else None
    if gun is not None and gun.text:
        result[-1] = (float(gun.text.split()[0]), float(gun.findtext('vehicleDamageFactor', '0')))
    return result


def install(exporter):
    original_convert = exporter.convert
    def convert(card_id,file_name,parts,turret_slot,gun_slot,rebuild):
        ACTIVE.clear()
        ACTIVE.update(armor_hull=values(parts['hull']),
                      armor_turret=values(parts['turrets'][turret_slot]['node']),
                      _gun_mask=values(parts['turrets'][turret_slot]['guns'][gun_slot]))
        return original_convert(card_id,file_name,parts,turret_slot,gun_slot,rebuild)
    exporter.convert = convert
    original_zones = b.MeshMaker.zoned_meshes
    def exact(self,prefix,sources,thickness,pivot=(0.,0.,0.),source_vertical_limit=None):
        if prefix not in ACTIVE:
            return original_zones(self,prefix,sources,thickness,pivot,source_vertical_limit)
        matrix = b.mat_mul(b.DAVA_TO_GLTF,b.translation_matrix(tuple(-x for x in pivot)))
        buckets = collections.defaultdict(lambda:([],[]))
        for node in sources:
            for group_id,_mat,_lod in node['batches']:
                group = self.groups.get(group_id)
                if group is None:
                    raise RuntimeError(f'Missing collision group {group_id}')
                streams = dava.decode_vertices(group)
                indices = dava.decode_indices(group)
                plate_ids = streams.get('hardJointIndex')
                if not plate_ids:
                    raise RuntimeError(f'Collision group {group_id} has no plate stream')
                positions = streams['position']
                normals = streams.get('normal')
                for offset in range(0,len(indices),3):
                    ids = indices[offset:offset+3]
                    plates = {int(plate_ids[i][0]) for i in ids}
                    if len(plates)!=1:
                        raise RuntimeError(f'Mixed plate IDs {plates}')
                    plate = next(iter(plates))
                    if plate not in ACTIVE[prefix]:
                        raise RuntimeError(f'{prefix}: no XML armor_{plate}')
                    points = [b.xform_point(matrix,positions[i]) for i in ids]
                    face_normal = b.normalized(b.triangle_normal(*points))
                    out_points,out_normals = buckets[plate]
                    for i,point in zip(ids,points):
                        out_points.append(point)
                        out_normals.append(b.xform_dir(matrix,normals[i]) if normals else face_normal)
        panels = []
        for plate,(points,normals) in sorted(buckets.items()):
            millimeters,_factor = ACTIVE[prefix][plate]
            label = 'mgun' if plate == -1 else str(plate)
            name = f'{prefix}_plate{label}_{round(millimeters)}'
            primitive = {'attributes':{'POSITION':self.gltf.vec3(points,with_bounds=True),
                                       'NORMAL':self.gltf.vec3(normals)},
                         'indices':self.gltf.indices(list(range(len(points)))), 'material':0,'mode':4}
            self.vertices+=len(points)
            self.triangles+=len(points)//3
            panels.append((name,self.gltf.add_mesh(name,[primitive])))
        return panels
    b.MeshMaker.zoned_meshes = exact
    # The legacy converter treats the complete gun collision donor as one
    # mantlet. Modern oscillating turrets carry many HP-bearing plates there.
    # Adapt its single-mesh node to a transform-only group of exact panels.
    pending = {}
    original_mesh = b.MeshMaker.mesh_from
    def mesh_from(self,name,sources,pivot=(0.,0.,0.)):
        if name.startswith('_gun_mask_'):
            panels = exact(self,'_gun_mask',sources,{},pivot)
            if not panels:
                # A source-authored empty gun donor has no mantlet to invent.
                if any(node['batches'] for node in sources):
                    raise RuntimeError('Nonempty gun donor lost its panels')
                return None
            pending[(id(self.gltf),name)] = panels
            return panels[0][1]
        return original_mesh(self,name,sources,pivot)
    b.MeshMaker.mesh_from = mesh_from
    original_node = b.Gltf.add_node
    def add_node(self,name,**kwargs):
        panels = pending.pop((id(self),name),None)
        if panels is None:
            return original_node(self,name,**kwargs)
        children = [original_node(self,panel,mesh=mesh) for panel,mesh in panels]
        return original_node(self,'_collision_gun_panels',
                             translation=kwargs.get('translation'),children=children)
    b.Gltf.add_node = add_node


def annotate(table,root):
    parts={'hull':root.find('hull')}
    for turret in root.find('turrets0'):
        parts[turret.findtext('blitz/blitzPartName')] = turret
        for gun in turret.find('guns'):
            parts[gun.findtext('blitz/blitzPartName')] = gun
    for plate in table['plates']:
        match = re.search(r'_plate(\d+|mgun)_',plate['name'])
        if not match:
            continue
        index = -1 if match.group(1) == 'mgun' else int(match.group(1))
        mount = plate['mount']
        context = re.search(r'_mask_(turret_\d+)_plate',plate['name'])
        if context:
            turret_slot = context.group(1)
            turret = next(t for t in root.find('turrets0') if t.findtext('blitz/blitzPartName')==turret_slot)
            gun = next(g for g in turret.find('guns') if g.findtext('blitz/blitzPartName')==mount)
            source = values(gun)
            plate['required_turret'] = turret_slot
        else:
            source = values(parts[mount])
        millimeters,factor=source[index]
        if abs(factor) not in (0,1):
            raise RuntimeError(f'Unsupported source damage factor {factor}')
        plate['screen'] = factor==0
        if index == -1:
            plate['module'] = 'gun'
        plate['source_plate_id']=index
        plate['source_armor']=millimeters
        plate['vehicle_damage_factor']=factor
    table['policy']='SCG hardJointIndex -> XML armor_<id>; unchanged source collision topology'
    return table
