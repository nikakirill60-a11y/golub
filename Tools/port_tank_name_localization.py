#!/usr/bin/env python3
"""Source-backed tank titles, identified by immutable card/source IDs, not transliteration."""
import argparse
import hashlib
import json
import re
from collections import defaultdict
from pathlib import Path
from xml.etree import ElementTree as ET
import yaml

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT/'Assets/Blitz/Resources/Data'
OUTPUT = DATA/'tank_name_localization.json'
PACKS = [ROOT.parent/'wotblitz4.8.0files', ROOT.parent/'wotblitz11.20.0files']
STRINGS = ['from_apk/Data/Strings', 'from_apk/Strings']


def generate():
    cards = json.loads((DATA/'tanks.json').read_text())['tanks']
    tables = [{lang: yaml.safe_load((p/STRINGS[i]/(lang+'.yaml')).read_text())
               for lang in ('en','ru')} for i,p in enumerate(PACKS)]
    lists, entries, keys, aliases = {}, {}, {}, defaultdict(list)
    for card in cards:
        candidates = [(i,p/card['source']) for i,p in enumerate(PACKS)
                      if (p/card['source']).is_file()]
        exact = [(i,p) for i,p in candidates if hashlib.sha256(p.read_bytes()).hexdigest()==card['source_sha256']]
        if not exact: raise ValueError('No exact source: '+card['name'])
        index, path = exact[0]
        listing_path = path.parent/'list.xml'
        if listing_path not in lists: lists[listing_path] = ET.parse(listing_path).getroot()
        listing = lists[listing_path].find(path.stem)
        if listing is None: raise ValueError('No source list record: '+card['name'])
        full = listing.findtext('userString','').strip()
        short = listing.findtext('shortUserString','').strip() or full
        fields = {}
        for field, source_key in [('name',full),('short',short)]:
            row, references = {}, {}
            for lang in ('en','ru'):
                used_key = source_key
                match = next(((j,tables[j][lang][used_key]) for j in [index,1-index]
                              if tables[j][lang].get(used_key)),None)
                # Some client prototypes have only an authored short title.
                if match is None:
                    used_key = short if field=='name' else full
                    match = next(((j,tables[j][lang][used_key]) for j in [index,1-index]
                                  if tables[j][lang].get(used_key)),None)
                if match:
                    j,value = match
                    row[lang] = value
                    references[lang] = {'version': ['4.8.0','11.20.0'][j], 'key':used_key}
                else:
                    # Two already-ported internal variants have no client key.
                    # Retain the existing Latin donor+variant name, never invent a translation.
                    value = card['display_name' if field=='name' else 'display_name_short']
                    if re.search('[А-Яа-яЁё]',value):
                        raise ValueError('Needs external source: '+card['name']+' '+field+' '+lang)
                    row[lang] = value
                    references[lang] = {'policy':card.get('display_name_policy','existing untranslated title')}
            if re.search('[А-Яа-яЁё]',row['en']): raise ValueError('Cyrillic English title: '+card['name'])
            key = 'tank:'+card['name']+'/'+field
            keys[key] = row
            fields[field] = {'translations':row,'source':references}
            for label in {card['display_name' if field=='name' else 'display_name_short'],*row.values()}:
                aliases[label].append(row)
        entries[card['name']] = {'source':card['source'],'source_sha256':card['source_sha256'],**fields}
    ambiguous = []
    for label, rows in aliases.items():
        if all(row==rows[0] for row in rows): keys[label] = rows[0]
        else: ambiguous.append(label)  # Stable tank-ID lookups never depend on these aliases.
    sources = []
    for i,p in enumerate(PACKS):
        for lang in ('en','ru'):
            path=p/STRINGS[i]/(lang+'.yaml')
            sources.append({'version':['4.8.0','11.20.0'][i], 'path':str(path.relative_to(p)),
                            'language':lang,'sha256':hashlib.sha256(path.read_bytes()).hexdigest()})
    return {'languages':['en','ru'],'sources':sources,'tanks':entries,
            'ambiguousAliases':sorted(ambiguous),'keys':dict(sorted(keys.items()))}


def serialized(value): return json.dumps(value,ensure_ascii=False,indent=2)+'\n'


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--check',action='store_true');args=parser.parse_args()
    result=generate(); text=serialized(result)
    lang=json.loads((DATA/'lang.json').read_text())
    if OUTPUT.is_file():
        previous = json.loads(OUTPUT.read_text())
        for key in previous.get('keys', {}):
            lang['keys'].pop(key, None)
    lang['keys'].update(result['keys'])
    if args.check:
        if not OUTPUT.is_file() or OUTPUT.read_text()!=text: raise SystemExit('Tank localization is stale')
        current=json.loads((DATA/'lang.json').read_text())['keys']
        if any(current.get(k)!=v for k,v in result['keys'].items()): raise SystemExit('lang.json tank rows are stale')
        manifest=json.loads((DATA/'hangar_source_manifest.json').read_text())
        if manifest['localization']['keys']!=len(current): raise SystemExit('Localization manifest count is stale')
    else:
        OUTPUT.write_text(text)
        (DATA/'lang.json').write_text(json.dumps(lang,ensure_ascii=False,indent=2)+'\n')
        manifest_path=DATA/'hangar_source_manifest.json'
        manifest=json.loads(manifest_path.read_text())
        manifest['localization']['keys']=len(lang['keys'])
        manifest['localization']['languages']=len(lang['languages'])
        manifest_path.write_text(serialized(manifest))
    print(json.dumps({'tanks':len(result['tanks']),'keys':len(result['keys']),
                      'ambiguousAliases':result['ambiguousAliases']},ensure_ascii=False))

if __name__=='__main__': main()
