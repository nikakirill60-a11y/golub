"""Add the Mark I Male collectible without modifying any existing card."""
import copy
import json
from pathlib import Path

CARD_FILE = Path(__file__).resolve().parents[1] / "Assets/Blitz/Resources/Data/blitz_mark_i_male_1120_cards.json"


def merge_into(document):
    if not CARD_FILE.exists():
        return document
    overlay = json.loads(CARD_FILE.read_text(encoding="utf-8"))
    new = overlay["tanks"]
    names = {card["name"] for card in new}
    if len(names) != len(new):
        raise RuntimeError("Duplicate cards in Mark I Male overlay")
    document["tanks"] = [card for card in document["tanks"] if card["name"] not in names]
    document["tanks"].extend(copy.deepcopy(new))
    document["tanks"].sort(key=lambda card: (
        card["nation"], card["lvl"], card["display_name"].casefold()))
    return document
