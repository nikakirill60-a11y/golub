"""Apply the persisted 11.20 overlay after validating the 4.8 baseline."""
import copy
import json
from pathlib import Path
CARD_FILE = Path(__file__).resolve().parents[1] / "Assets/Blitz/Resources/Data/blitz_1120_cards.json"
NAMES = {"F126_AMX_13_M24": "f126_amx_13_m24"}
BRANCHES = {"france": ["F126_AMX_13_M24"]}

def merge_into(document):
    if not CARD_FILE.exists():
        return document
    new = json.loads(CARD_FILE.read_text())['tanks']
    names = {c['name'] for c in new}
    document['tanks'] = [c for c in document['tanks'] if c['name'] not in names]+copy.deepcopy(new)
    amx = next(c for c in document['tanks'] if c['name']=='amx_12t')
    first = next(c for c in new if c['name']==NAMES[BRANCHES['france'][0]])
    amx['unlock_tanks'][first['name']] = first['cost_xp']
    amx['guns'][-1].setdefault('unlocks', {})['vehicle:'+first['name']] = first['cost_xp']
    for t in amx['turrets']:
        for slot in t['guns']:
            if slot['name']==amx['guns'][-1]['name']:
                slot['unlocks']['vehicle:'+first['name']] = first['cost_xp']
    document['tanks'].sort(key=lambda c:(c['nation'],c['lvl'],c['display_name'].casefold()))
    document['additive_versions'] = ['11.20']
    from blitz_germany_1120_registry import merge_into as merge_germany
    from blitz_usa_1120_registry import merge_into as merge_usa
    from blitz_other_1120_registry import merge_into as merge_other
    from blitz_japan_1120_registry import merge_into as merge_japan
    from blitz_branches_1120_registry import merge_into as merge_branches
    from blitz_ussr_1120_registry import merge_into as merge_ussr
    from blitz_mark_i_male_1120_registry import merge_into as merge_mark_i_male
    from world_of_ducks_registry import merge_into as merge_world_of_ducks
    return merge_world_of_ducks(merge_mark_i_male(merge_ussr(merge_branches(merge_japan(
        merge_other(merge_usa(merge_germany(document))))))))
