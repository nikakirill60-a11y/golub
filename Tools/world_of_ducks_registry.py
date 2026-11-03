"""World of Ducks vehicles of the shared tanks.json.

The event battles play their own 4.8 WarDuck card (``world_of_ducks.json``,
``port_world_of_ducks.py card``), while the 11.20 mixed-nation overlay also
brings WarDuck in as an ordinary premium card. Godot never registers its
WarDuck in TanksDataBase (``hidden``, ``ignore_matchmaker``), so the shared
table marks the card as the event's own and TanksDatabase hides it from the
hangar carousel, the Sandbox and every bot roll. The marks live in a
document-level map: no card, and so no hash of the preservation files, changes.
"""

# Card name -> the event mode that owns it.
EVENT_VEHICLES = {"oth10_warduck": "world_of_ducks"}


def merge_into(document):
    # A table built without the 11.20 mixed-nation overlay has no WarDuck to mark.
    names = {card["name"] for card in document["tanks"]}
    present = {name: mode for name, mode in sorted(EVENT_VEHICLES.items()) if name in names}
    if present:
        document["event_vehicles"] = present
    else:
        document.pop("event_vehicles", None)
    return document
