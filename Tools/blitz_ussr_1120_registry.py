"""Append the USSR 11.20 overlay (Object 907) without changing any previous card."""
import copy
import hashlib
import json
from pathlib import Path

DATA = Path(__file__).resolve().parents[1] / 'Assets/Blitz/Resources/Data'
CARD_FILE = DATA / 'blitz_ussr_1120_cards.json'
PRESERVATION = DATA / 'blitz_ussr_1120_preservation.json'
OVERLAY = 'ussr_1120'


def card_hash(card):
    # The tree row belongs to tech_tree_layout.py, so a re-layout is not a
    # card change.
    card = {key: value for key, value in card.items() if key != 'tree_branch'}
    payload = json.dumps(card, ensure_ascii=False, sort_keys=True,
                         separators=(',', ':')).encode('utf-8')
    return hashlib.sha256(payload).hexdigest()


def merge_into(document):
    if not CARD_FILE.exists():
        return document
    overlay = json.loads(CARD_FILE.read_text(encoding='utf-8'))
    new = overlay['tanks']
    if len(new) != 1 or new[0].get('name') != 'object_907' or new[0].get('source_overlay') != OVERLAY:
        raise ValueError('USSR overlay must contain exactly the authored Object 907')
    names = {c['name'] for c in new}
    collisions = [c['name'] for c in document['tanks']
                  if c['name'] in names and c.get('source_overlay') != OVERLAY]
    if collisions:
        raise ValueError('Overlay would overwrite existing cards: ' + ', '.join(collisions))
    previous = [c for c in document['tanks']
                if c.get('source_overlay') != OVERLAY and c['name'] not in names]
    document['tanks'] = previous + copy.deepcopy(new)
    sources = {c['source'] for c in new}
    links = [x for x in document.get('additional_research_links', [])
             if x.get('source') not in sources]
    document['additional_research_links'] = links + copy.deepcopy(
        overlay['additional_research_links'])
    document['tanks'].sort(key=lambda c: (
        c['nation'], c['lvl'], c['display_name'].casefold()))
    if PRESERVATION.exists():
        protected = json.loads(PRESERVATION.read_text(encoding='utf-8'))['cards']
        actual = {c['name']: card_hash(c) for c in previous}
        changed = [name for name, expected in protected.items()
                   if actual.get(name) != expected]
        if changed:
            raise RuntimeError('Existing cards changed: ' + ', '.join(changed))
    return document
