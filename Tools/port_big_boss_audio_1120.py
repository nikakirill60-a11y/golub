#!/usr/bin/env python3
"""Extract only original Big Boss Wwise media; never decode BNK prefetch as a full clip.
Event membership/loop declarations come from the supplied Android SoundbanksInfo; each
event's play/stop actions and its random, sequence and layer container tree come from the
bank's HIRC chunk. This exports provenance and container structure, not a claim of
recreating Wwise's entire RTPC mixer.
"""
import argparse, hashlib, json, shutil, struct, subprocess, sys, tempfile
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
SOUND, ACTION, EVENT, RANSEQ, LAYER = 2, 3, 4, 5, 9
PLAY, STOP = 0x403, 0x103
TRANSITIONS = {0: 'none', 1: 'crossfade', 2: 'crossfadePower', 3: 'delay', 4: 'sampleAccurate', 5: 'triggerRate'}
def hirc(sections):
    h = sections[b'HIRC']; n = struct.unpack_from('<I', h, 0)[0]; pos = 4; objects = {}
    for _ in range(n):
        kind = h[pos]; size = struct.unpack_from('<I', h, pos + 1)[0]; body = h[pos + 5:pos + 5 + size]
        objects[struct.unpack_from('<I', body, 0)[0]] = (kind, body); pos += 5 + size
    return objects
def children(objects, oid, body):
    # Container children follow the node parameters as a u32-counted id run; take the
    # longest run of existing sounds/containers (every tree is validated against media).
    best = (None, [])
    for p in range(4, len(body) - 4):
        count = struct.unpack_from('<I', body, p)[0]
        if 0 < count <= 16 and p + 4 + 4 * count <= len(body):
            ids = [struct.unpack_from('<I', body, p + 4 + 4 * k)[0] for k in range(count)]
            if all(x in objects and x != oid and objects[x][0] in (SOUND, RANSEQ, LAYER) for x in ids) and len(ids) > len(best[1]):
                best = (p, ids)
    if best[0] is None: raise ValueError('Wwise container without children: ' + str(oid))
    return best
def cue(objects, oid):
    kind, body = objects[oid]
    if kind == SOUND: return dict(media=struct.unpack_from('<I', body, 9)[0])
    p, ids = children(objects, oid, body)
    if kind == LAYER: return dict(type='layer', children=[cue(objects, c) for c in ids])
    if kind != RANSEQ: raise ValueError('Unsupported Wwise node %d type %d' % (oid, kind))
    loop = struct.unpack_from('<H', body, p - 24)[0]; seconds = struct.unpack_from('<f', body, p - 18)[0] / 1000
    avoid = struct.unpack_from('<H', body, p - 6)[0]; transition, mode = body[p - 4], body[p - 2]
    q = p + 4 + 4 * len(ids); count = struct.unpack_from('<H', body, q)[0]
    order = [struct.unpack_from('<I', body, q + 2 + 8 * k)[0] for k in range(count)]
    if sorted(order) != sorted(ids): raise ValueError('Wwise playlist differs from children: ' + str(oid))
    return dict(type='sequence' if mode == 1 else 'random', loop=loop, transition=TRANSITIONS[transition],
                transitionSeconds=round(seconds, 6), avoidRepeats=avoid, children=[cue(objects, c) for c in order])
def leaves(node):
    if 'media' in node: return {node['media']}
    out = set()
    for child in node['children']: out |= leaves(child)
    return out
def actions(objects, event_id):
    kind, body = objects[event_id]
    if kind != EVENT: raise ValueError('Missing Wwise event ' + str(event_id))
    out = []
    for k in range(body[4]):
        _, action = objects[struct.unpack_from('<I', body, 5 + 4 * k)[0]]
        out.append((struct.unpack_from('<H', action, 4)[0], struct.unpack_from('<I', action, 6)[0]))
    return out
def main():
    ap=argparse.ArgumentParser();ap.add_argument('--source',type=Path,default=ROOT.parent/'wotblitz11.20.0files');ap.add_argument('--decoder',default=str(Path.home()/'.local/opt/vgmstream/vgmstream-cli'))
    ap.add_argument('--reencode',action='store_true',help='re-encode clips that already exist');a=ap.parse_args()
    sys.path.insert(0,str(ROOT.parent/'blitz-legacy-godot/tools'));import dvpl
    src=a.source;out=ROOT/'Assets/Blitz/Resources/BigBoss/Audio';out.mkdir(parents=True,exist_ok=True)
    metadata=src/'from_apk/WwiseSound/SoundbanksInfo.json';data=json.loads(metadata.read_text())['SoundBanksInfo'];bank=next(b for b in data['SoundBanks'] if b.get('ShortName')=='ability_halloween_2021')
    bnk=src/'WwiseSound/ability_halloween_2021.bnk.dvpl';pck=src/'WwiseSound/ability_halloween_2021.pck.dvpl'
    raw=dvpl.unpack_bytes(bnk.read_bytes());sections={};pos=0
    while pos+8<=len(raw):
        tag=raw[pos:pos+4];size=struct.unpack_from('<I',raw,pos+4)[0];sections[tag]=raw[pos+8:pos+8+size];pos+=size+8
    objects=hirc(sections)
    media={}
    for pos in range(0,len(sections.get(b'DIDX',b'')),12):
        uid,offset,size=struct.unpack_from('<III',sections[b'DIDX'],pos);media[uid]=sections[b'DATA'][offset:offset+size]
    raw=dvpl.unpack_bytes(pck.read_bytes());assert raw[:4]==b'AKPK';lang,banks=struct.unpack_from('<II',raw,12);table=28+lang+banks;n=struct.unpack_from('<I',raw,table)[0]
    for i in range(n):
        uid,block,size,offset,_=struct.unpack_from('<IIIII',raw,table+4+i*20);payload=raw[block*offset:block*offset+size];assert len(payload)==size;media[uid]=payload
    events={};clips={};targets={}
    with tempfile.TemporaryDirectory(prefix='bigboss1120-audio-') as tmp:
        for event in bank['IncludedEvents']:
            ids=[]
            for f in event.get('ReferencedStreamedFiles',[])+event.get('IncludedMemoryFiles',[]):
                uid=int(f['Id'])
                if uid in ids:continue
                if uid not in media:raise ValueError('Missing source Wwise media '+str(uid))
                payload=media[uid]
                if uid not in clips:
                    dest=out/(str(uid)+'.ogg')
                    if a.reencode or not dest.exists():
                        wem=Path(tmp)/(str(uid)+'.wem');wav=Path(tmp)/(str(uid)+'.wav');wem.write_bytes(payload)
                        subprocess.run([a.decoder,'-i','-o',str(wav),str(wem)],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.PIPE)
                        subprocess.run(['ffmpeg','-v','error','-y','-i',str(wav),'-c:a','libvorbis','-q:a','6','-fflags','+bitexact','-flags:a','+bitexact',str(dest)],check=True)
                    clips[uid]=dict(resource='BigBoss/Audio/'+str(uid),sourceName=f['ShortName'],mediaSha256=hashlib.sha256(payload).hexdigest(),outputSha256=hashlib.sha256(dest.read_bytes()).hexdigest())
                ids.append(uid)
            plays=[t for k,t in actions(objects,int(event['Id'])) if k==PLAY]
            if len(plays)!=1:raise ValueError('Expected one play action: '+event['Name'])
            tree=cue(objects,plays[0])
            if leaves(tree)!=set(ids):raise ValueError('Wwise tree media differs from SoundbanksInfo: '+event['Name'])
            targets[plays[0]]=event['Name']
            events[event['Name']]=dict(id=event['Id'],loop=event.get('DurationType')=='Infinite',maxAttenuation=float(event.get('MaxAttenuation','250')),media=ids,cue=tree)
        for event in bank['IncludedEvents']:
            stops=[targets[t] for k,t in actions(objects,int(event['Id'])) if k==STOP]
            if stops:events[event['Name']]['stops']=stops
    provenance=[dict(source=str(p.relative_to(src)),sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in (metadata,bnk,pck)]
    result=dict(sourceVersion='11.20.0',events=events,clips=clips,sources=provenance,limitation='Original event media, HIRC random/sequence/layer containers, transitions and stop actions; RTPC curves and the native Wwise mixer are not reproduced.')
    (ROOT/'Assets/Blitz/Resources/Data/big_boss_audio_1120.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(dict(events=len(events),clips=len(clips))))
if __name__=='__main__':main()
