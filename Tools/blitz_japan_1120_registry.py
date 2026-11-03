"""Append the Japanese 11.20 overlay (Type 5 Heavy) without changing any previous card."""
import copy
import hashlib
import json
from pathlib import Path

DATA = Path(__file__).resolve().parents[1] / 'Assets/Blitz/Resources/Data'
CARD_FILE = DATA / 'blitz_japan_1120_cards.json'
PRESERVATION = DATA / 'blitz_japan_1120_preservation.json'


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
    names = {c['name'] for c in new}
    previous = [c for c in document['tanks']
                if c.get('source_overlay') != 'japan_1120' and c['name'] not in names]
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
