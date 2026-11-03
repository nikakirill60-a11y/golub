#!/usr/bin/env python3
"""Hand-made tech-tree rows for the hybrid 4.8 + 11.20 nations.

The USA and German trees mix 4.8.0 cards, which keep the 4.8 client YAML
rows, with 11.20 additions, whose rows come from a different client with
different tiers. The result had research arrows that crossed half of the
tree (A100 T49 -> T92E1 spanned four rows) and lines that changed row at
random. These tables re-lay both nations so that every research arrow moves
at most one row; the only exception is the tier I starter, which fans out to
three lines.

Every port writer applies the tables right before it writes ``tanks.json``,
and running this script applies them to the live file in place.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

TANKS = Path(__file__).resolve().parents[1] / 'Assets/Blitz/Resources/Data/tanks.json'

NATION_USA = 3
NATION_GERMANY = 2

# Rows top to bottom: medium/light offshoot, light, tank destroyers (two
# lines), medium, heavy, heavy offshoot.
USA = {
    # -3: M7 -> T21 -> T71 -> T69 -> T54E1 -> T57 Heavy
    'm7_med': -3, 't21': -3, 't71': -3, 't69': -3, 't54e1': -3, 't57_58': -3,
    # -2: M5 Stuart -> M24 -> T37 -> M41 -> T49 -> T92E1 -> Sheridan
    'm5_stuart': -2, 'm24_chaffee': -2, 'a94_t37': -2, 'm41_bulldog': -2,
    'a100_t49': -2, 'a99_t92e1': -2, 'a116_xm551': -2,
    # -1: M2 Light -> M3 Stuart, then M8A1 -> T49 -> ... -> T110E4
    'm2_lt': -1, 'm3_stuart': -1, 'm8a1': -1, 't49': -1, 'm18_hellcat': -1,
    't25_2': -1, 't28_prototype': -1, 't30': -1, 't110e4': -1,
    # 0: T1 Cunningham, T18 -> T82 -> T40 -> ... -> T110E3
    't1_cunningham': 0, 't18': 0, 't82': 0, 't40': 0, 'm10_wolverine': 0,
    'm36_slagger': 0, 't25_at': 0, 'usa-t28': 0, 't95': 0, 't110e3': 0,
    # 1: T2 Medium -> ... -> M48 Patton
    't2_med': 1, 'm2_med': 1, 'm3_grant': 1, 'm4_sherman': 1,
    'm4a3e8_sherman': 1, 't20': 1, 'pershing': 1, 'm46_patton': 1, 'm48a1': 1,
    # 2: T1 Heavy, Sherman Jumbo, T29 -> T32 -> M103 -> T110E5
    't1_hvy': 2, 'sherman_jumbo': 2, 't29': 2, 't32': 2, 'm103': 2, 't110': 2,
    # 3: M6 and the M-Y line it opens
    'm6': 3, 'm-7-y': 3, 'm-3-y': 3, 'm-5-y': 3, 'm-6-y': 3,
}

# Rows top to bottom: two tank-destroyer lines, the Tiger and Maus heavies,
# the Tiger II / E 75 heavies, three medium lines and the light line.
GERMANY = {
    # -3: Sturer Emil -> Waffentrager auf Pz. IV line
    'sturer_emil': -3, 'rhb_waffentrager': -3, 'waffentrager_iv': -3,
    'g121_grille_15_l63': -3,
    # -2: Pz.Sfl. IVc -> Nashorn, Jagdpanther II -> Jagdtiger -> JPz E 100
    'pz_sfl_ivc': -2, 'nashorn': -2, 'jagdpantherii': -2, 'jagdtiger': -2,
    'jagdpz_e100': -2,
    # -1: Panzerjager I -> Marder II -> Hetzer -> StuG -> JPz IV -> Jagdpanther -> Ferdinand
    'panzerjager_i': -1, 'g20_marder_ii': -1, 'hetzer': -1, 'stugiii': -1,
    'jagdpziv': -1, 'jagdpanther': -1, 'ferdinand': -1, 'vk7201': -1,
    # 0: Leichttraktor -> Pz 35(t) -> Pz 38(t) -> Pz 38 nA -> Pz IV -> VK 30.01 P -> Tiger P -> VK 45.02 A -> VK 45.02 B -> Maus
    'ltraktor': 0, 'pz35t': 0, 'pz38t': 0, 'pz38_na': 0, 'pziv': 0,
    'vk3001p': 0, 'pzvi_tiger_p': 0, 'vk4502a': 0, 'vk4502p': 0, 'maus': 0,
    # 1: Pz IV A -> Pz IV D, VK 30.01 H -> VK 36.01 H -> Tiger I, VK 100.01 P -> Mauschen
    'pz_iv_ausfa': 1, 'pz_iv_ausfd': 1, 'vk3001h': 1, 'vk3601h': 1, 'pzvi': 1,
    'g115_vk10001p': 1, 'g110_mauschen': 1,
    # 2: Pz II -> Pz III A, D.W. 2 -> VK 30.02 M -> Panther -> Tiger II -> E 75 -> E 100
    'pzii': 2, 'pziii_a': 2, 'dw_ii': 2, 'vk3002m': 2, 'pzv': 2,
    'pzvib_tiger_ii': 2, 'e-75': 2, 'e-100': 2,
    # 3: Pz II G, Pz III -> Pz III/IV -> VK 30.02 DB -> Panther II -> E 50 -> E 50 M
    'pz_ii_ausfg': 3, 'pziii': 3, 'pziii_iv': 3, 'vk3002db_v1': 3, 'vk3002db': 3,
    'panther_ii': 3, 'e-50': 3, 'e50_ausf_m': 3,
    # 4: Luchs -> VK 16.02 -> VK 28.01 -> Spahpanzer I C -> Indien-Panzer -> Pr. A -> Leopard 1
    'pzii_luchs': 4, 'vk1602': 4, 'vk2801': 4, 'g113_sp_i_c': 4,
    'indien_panzer': 4, 'pro_ag_a': 4, 'leopard1': 4,
    # 5: Spahpanzer Ru 251 -> HWK 12 -> Spz 57 Rh
    'g103_ru_251': 5, 'g126_hwk_12': 5, 'g125_spz_57_rh': 5,
}

LAYOUTS = {NATION_USA: USA, NATION_GERMANY: GERMANY}


def branch(nation: int, name: str, default: int) -> int:
    """The laid-out row for a tree card, or ``default`` for other nations."""
    return LAYOUTS.get(nation, {}).get(name, default)


def apply(document: dict) -> dict:
    """Moves every laid-out researchable card to its row.

    Premium cards keep their premium-row branches. A researchable card of a
    laid-out nation that is missing from its table is an error, so a newly
    ported tank cannot silently fall back to a source row.
    """
    for card in document['tanks']:
        layout = LAYOUTS.get(card.get('nation'))
        if layout is None or not card.get('tree_tank') or card.get('prem'):
            continue
        if card['name'] not in layout:
            raise RuntimeError(f'tech_tree_layout: no row for {card["name"]}')
        card['tree_branch'] = layout[card['name']]
    return document


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    current = TANKS.read_text(encoding='utf-8')
    payload = json.dumps(apply(json.loads(current)), ensure_ascii=False, indent=1, sort_keys=True) + '\n'
    if args.check:
        if payload != current:
            raise SystemExit('tanks.json tech-tree rows are stale')
        print('tanks.json tech-tree rows are up to date')
        return
    TANKS.write_text(payload, encoding='utf-8')
    print(f'wrote {TANKS}')


if __name__ == '__main__':
    main()
