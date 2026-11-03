#!/usr/bin/env python3
"""Dump and port the active Godot profile, battle history row and medal popup."""
import csv, hashlib, json, subprocess, tempfile
from pathlib import Path
from PIL import Image
ROOT=Path(__file__).resolve().parents[1]
SOURCE=Path('/home/smh01/projects/blitz-legacy-godot')
RES=ROOT/'Assets/Blitz/Resources'
def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest()
def dump(script,path):
 run=subprocess.run(['godot','--headless','--path',str(SOURCE),'--script',str(ROOT/'Tools'/script),'--',str(path)],stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True,timeout=120)
 if run.returncode or not path.exists(): raise RuntimeError(run.stdout[-3000:])
 return json.loads(path.read_text())
def main():
 with tempfile.TemporaryDirectory() as td:
  scenes=dump('godot_profile_dump.gd',Path(td)/'profile.json')
  rewards=dump('godot_result_rewards_dump.gd',Path(td)/'rewards.json')
 table=json.loads((RES/'Data/atlas_regions.json').read_text())
 images={}; texts=set()
 def walk(n):
  if n.get('text'): texts.add(n['text'])
  for prop in ['texture','icon']:
   t=n.get(prop,{})
   if not t.get('path'): continue
   path=SOURCE/t['path'].removeprefix('res://')
   name='profile_'+hashlib.sha256(t['path'].encode()).hexdigest()[:12]
   region=name+'_'+hashlib.sha256(json.dumps(t['rect']).encode()).hexdigest()[:12]
   if name not in images:
    dst=RES/'Textures'/ (name+'.png')
    with Image.open(path) as im:
     rgba=im.convert('RGBA'); rgba.save(dst); table['textures'][name]={'width':rgba.width,'height':rgba.height}
    images[name]={'source':t['path'],'sha256':sha(path),'destination':dst.name}
   x,y,w,h=t['rect']; table['regions'][region]={'texture':name,'x':x,'y':y,'w':w,'h':h}
   t['region']=region
  for c in n.get('children',[]): walk(c)
 for n in scenes.values():walk(n)
 # Keep runtime data and popup wording aligned with the same live registry.
 definitions=RES/'Data/battle_reward_definitions.json'; data=json.loads(definitions.read_text());data['definitions']=rewards
 definitions.write_text(json.dumps(data,ensure_ascii=False,indent=2,sort_keys=True)+'\n')
 for r in rewards.values():texts.update([r['nameKey'],r['descKey']])
 lang=json.loads(subprocess.check_output(['git','show','HEAD:Assets/Blitz/Resources/Data/lang.json']))
 for file in ['lang_dump.csv','lang_extra.csv']:
  with (SOURCE/file).open(newline='',encoding='utf-8-sig') as f:
   reader=csv.reader(f); header=next(reader)
   for row in reader:
    if row and row[0] in texts:
     lang['keys'][row[0]]={k:v for k,v in zip(header[1:],row[1:]) if v}
 with tempfile.TemporaryDirectory() as td:
  lang['keys'].update(dump('godot_reward_locale_dump.gd',Path(td)/'locales.json'))
 (RES/'Data/lang.json').write_text(json.dumps(lang,ensure_ascii=False,indent='\t')+'\n')
 table['regions']['profile_medal_number']={'texture':'profile_120b452d8a01','x':817,'y':385,'w':50,'h':42}
 for name,y,x in [('draw',293,204),('defeat',147,406)]:table['regions']['profile_status_'+name]={'texture':'profile_ca5e1e3d3896','x':x,'y':y,'w':200,'h':144}
 tablepath=RES/'Data/atlas_regions.json';tablepath.write_text(json.dumps(table,ensure_ascii=False,indent=2,sort_keys=True)+'\n')
 data={'source':{'path':'assets/gfx/ui/hangar/ui_hangar.tscn','sha256':sha(SOURCE/'assets/gfx/ui/hangar/ui_hangar.tscn')},'images':images,'scenes':scenes}
 (RES/'Data/profile_source_manifest.json').write_text(json.dumps(data,ensure_ascii=False,indent=2,sort_keys=True)+'\n')
 print('Profile scenes:',len(scenes),'sheets:',len(images),'source captions:',len(texts))
if __name__=='__main__':main()
