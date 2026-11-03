"""Role-aware, deterministic Object 907 tuning against the game's own roster."""
from __future__ import annotations
import copy
import math
import statistics

import port_blitz_usa_1120 as stats


def benchmark(cards):
    """One observation per researchable tier X medium in its final mount."""
    peers = sorted((c for c in cards if c['lvl'] == 10 and c['tank_class'] == 1
                    and c.get('tree_tank') and not c.get('prem') and not c.get('collector')),
                   key=lambda c: c['name'])
    if len(peers) < 5 or len({c['name'] for c in peers}) != len(peers):
        raise ValueError('Expected at least five distinct researchable tier X mediums')
    singles = [c for c in peers if not stats.top_gun(c).get('autoreload_times')
               and stats.shots(stats.top_gun(c)) == 1]
    if len(singles) < 5:
        raise ValueError('Too few comparable single-shot tier X medium guns')
    observations = []
    for peer in peers:
        gun = stats.top_gun(peer)
        values = stats.metrics(peer)
        if any(not math.isfinite(v) or v <= 0 for v in values.values()):
            raise ValueError('Invalid peer metrics: ' + peer['name'])
        observations.append(dict(name=peer['name'], single_shot=peer in singles,
                                 alpha=gun['shells'][0]['dmg'], reload=gun['reload'],
                                 dispersion=gun['disp'], aim_time=gun['aim_time'], **values))
    return peers, singles, observations


def calibrate(card, competitors):
    """Keep the 11.20 armour/projectiles/HP/handling; tune only reload and mobility ceilings.

    DPM is capped at the median of CURRENT researchable single-shot tier X mediums,
    using each tank's final turret/gun and standard shell. Magazine/autoreload guns
    stay in the mobility comparison but not in this single-shot firepower budget.
    Reload rounds upward to 0.1 s, never making the source gun faster. Forward/reverse
    speed and installed specific power cannot exceed the best current tier X medium.
    The source's lower 1800 HP, 248 mm standard penetration and 0.12 movement bloom
    remain trade-offs for its accurate gun and mobility; no armour or shell is edited.
    """
    if (card.get('name') != 'object_907' or card['lvl'] != 10 or card['tank_class'] != 1
            or any(len(card.get(group, [])) != 1
                   for group in ('guns', 'turrets', 'tracks', 'engines', 'radios'))):
        raise ValueError('This balance profile is specific to the source Object 907')
    mounts = card['turrets'][0].get('guns', [])
    if len(mounts) != 1 or mounts[0]['name'] != card['guns'][0]['name']:
        raise ValueError('Object 907 must retain its authored single gun mount')
    metrics = stats.metrics(card)
    values = list(metrics.values()) + [card['guns'][0]['reload'], mounts[0]['reload']]
    if any(not math.isfinite(value) or value <= 0 for value in values):
        raise ValueError('Invalid Object 907 source metrics')
    before = copy.deepcopy(card)
    peers, singles, rows = benchmark(competitors)
    gun = card['guns'][0]
    if stats.shots(gun) != 1 or gun.get('autoreload_times'):
        raise ValueError('Object 907 source gun is no longer single-shot')
    dpm_limit = statistics.median(stats.dpm(stats.top_gun(p)) for p in singles)
    needed = 60.0 * gun['shells'][0]['dmg'] / dpm_limit
    mounts = [m for t in card['turrets'] for m in t['guns']]
    for mount in mounts:
        mount['reload'] = math.ceil(max(mount['reload'], needed) * 10.0 - 1e-9) / 10.0
    gun['reload'] = max(m['reload'] for m in mounts)
    speed_caps = {axis: max(p['speed_limits'][axis] for p in peers) for axis in ('x', 'y')}
    for axis, cap in speed_caps.items():
        card['speed_limits'][axis] = min(card['speed_limits'][axis], cap)
    power_cap = max(stats.metrics(p)['power_per_ton'] for p in peers)
    for engine in card['engines']:
        engine['power'] = min(engine['power'], math.floor(power_cap * stats.mass(card, engine) / 1000.0))
    after = stats.metrics(card)
    if after['max_sustained_dpm'] > dpm_limit + 1e-6 or after['power_per_ton'] > power_cap + 1e-6:
        raise ValueError('Quantized tuning exceeds the peer budget')
    return {'name': card['name'], 'level': 10, 'class': 1,
            'competitors': [p['name'] for p in peers],
            'single_shot_competitors': [p['name'] for p in singles],
            'peer_observations': rows,
            'limits': {'single_shot_dpm_median': dpm_limit,
                       'max_power_per_ton': power_cap, 'speed_limits': speed_caps,
                       'reload_step_seconds': 0.1},
            'original_metrics': stats.metrics(before), 'balanced_metrics': after,
            'original': before, 'balanced': copy.deepcopy(card)}
