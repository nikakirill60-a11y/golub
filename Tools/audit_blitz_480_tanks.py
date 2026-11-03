#!/usr/bin/env python3
"""Inventory every researchable (non-premium) tank in the Blitz 4.8.0 client.

The regular tree is not guessed from prices or tags.  It is the directed graph
reachable from each nation's tier-I vehicle through module ``unlocks/vehicle``
edges.  The report is therefore the exact set of tanks that can be researched
in the shipped client, while premiums/event/tutorial vehicles stay out.

Writes:
  Assets/Blitz/Resources/Data/blitz_480_tank_inventory.json
  TASK_PLAN_TANKS480.md (unless --inventory-only)
"""
from __future__ import annotations

import argparse
import json
import os
import re
from collections import defaultdict, deque
from pathlib import Path
from xml.etree import ElementTree as ET

UNITY = Path(__file__).resolve().parent.parent
# Same root resolution as `port_blitz_branch`: `BLITZ_PACK`, then whichever
# of the two known client-pack roots exists on this machine.
def _pack_root() -> Path:
    override = os.environ.get('BLITZ_PACK')
    if override:
        return Path(override)
    candidates = (
        Path.home() / 'Studio canaryProjects' / 'BlitzPACKSold',
        Path.home() / 'projects' / 'wotblitz4.8.0files',
    )
    for candidate in candidates:
        if candidate.exists():
            return candidate
    return candidates[0]


PACK = _pack_root()
VEHICLES = PACK / 'XML/item_defs/vehicles'
STRINGS = PACK / 'from_apk/Data/Strings/ru.yaml'
OUT = UNITY / 'Assets/Blitz/Resources/Data/blitz_480_tank_inventory.json'
PLAN = UNITY / 'TASK_PLAN_TANKS480.md'

NATIONS = {
    'ussr': ('СССР', 'MS-1'),
    'germany': ('Германия', 'Ltraktor'),
    'usa': ('США', 'T1_Cunningham'),
    'uk': ('Великобритания', 'GB01_Medium_Mark_I'),
    'france': ('Франция', 'RenaultFT'),
    'japan': ('Япония', 'NC27'),
    'china': ('Китай', 'Ch06_Renault_NC31'),
}
KIND_GROUPS = ('chassis', 'engines', 'radios')


def text(node, default=''):
    if node is None:
        return default
    value = ' '.join(''.join(node.itertext()).split())
    return value or default


def own_text(node, default=''):
    if node is None:
        return default
    value = (node.text or '').strip()
    return value or default


def integer(node, default=0):
    try:
        return int(round(float(text(node).split()[0])))
    except (ValueError, IndexError):
        return default


def strings_table():
    result = {}
    pattern = re.compile(r'^"([^"]+)"\s*:\s*"(.*)"\s*$')
    for line in STRINGS.read_text(encoding='utf-8', errors='replace').splitlines():
        match = pattern.match(line.strip())
        if match:
            # YAML export uses JSON-style unicode escapes inside quoted values.
            try:
                result[match.group(1)] = json.loads('"' + match.group(2) + '"')
            except json.JSONDecodeError:
                result[match.group(1)] = match.group(2)
    return result


def merged(base, local):
    if base is None:
        return local
    node = ET.Element(local.tag)
    for child in list(base) + list(local):
        previous = node.find(child.tag)
        if previous is not None:
            node.remove(previous)
        node.append(child)
    return node


def unlocks(node):
    block = node.find('unlocks') if node is not None else None
    if block is None:
        return []
    result = []
    for entry in block:
        target = own_text(entry)
        if target:
            result.append((entry.tag, target, integer(entry.find('cost'))))
    return result


def client_for(nation):
    directory = VEHICLES / nation
    shared = {}
    for kind in ('guns', 'turrets', 'chassis', 'engines', 'radios'):
        root = ET.parse(directory / 'components' / f'{kind}.xml').getroot()
        group = root.find('shared')
        shared[kind] = {child.tag: child for child in (group if group is not None else [])}
    listing = ET.parse(directory / 'list.xml').getroot()
    listed = {child.tag: child for child in listing}
    return directory, shared, listed


def module_inventory(root, shared):
    counts = {'turrets': 0, 'guns': 0, 'chassis': 0, 'engines': 0, 'radios': 0}
    edges = []
    unique_guns = set()

    for group_name in KIND_GROUPS:
        group = root.find(group_name)
        for local in (group if group is not None else []):
            kind = 'chassis' if group_name == 'chassis' else group_name
            counts[group_name] += 1
            node = merged(shared[kind].get(local.tag), local)
            edges.extend(unlocks(node))

    turrets = root.find('turrets0')
    for local_turret in (turrets if turrets is not None else []):
        counts['turrets'] += 1
        turret = merged(shared['turrets'].get(local_turret.tag), local_turret)
        edges.extend(unlocks(turret))
        guns = local_turret.find('guns')
        for local_gun in (guns if guns is not None else []):
            unique_guns.add(local_gun.tag)
            gun = merged(shared['guns'].get(local_gun.tag), local_gun)
            edges.extend(unlocks(gun))
    counts['guns'] = len(unique_guns)
    return counts, edges


def build_inventory():
    strings = strings_table()
    result = {'client': '4.8.0', 'source': str(PACK), 'nations': {}, 'totals': {}}
    totals = defaultdict(int)

    for nation, (nation_name, root_id) in NATIONS.items():
        directory, shared, listed = client_for(nation)
        vehicles = {}
        all_edges = {}
        for path in sorted(directory.glob('*.xml')):
            if path.name in ('list.xml', 'customization.xml'):
                continue
            vehicle_id = path.stem
            root = ET.parse(path).getroot()
            counts, edges = module_inventory(root, shared)
            all_edges[vehicle_id] = [target for kind, target, _ in edges if kind == 'vehicle']
            listing = listed.get(vehicle_id)
            string_key = text(listing.find('userString')) if listing is not None else ''
            display = strings.get(string_key, vehicle_id)
            model_rel = text(root.find('blitz/blitzModelPath'))
            collision_rel = text(root.find('blitz/collisionMesh'))
            vehicles[vehicle_id] = {
                'id': vehicle_id,
                'display_name': display,
                'tier': integer(listing.find('level'), 1) if listing is not None else 1,
                'tank_class': (text(listing.find('tags')).split() or ['unknown'])[0] if listing is not None else 'unknown',
                'price_credits': integer(listing.find('price')) if listing is not None else 0,
                'source_xml': str(path.relative_to(PACK)),
                'model': f'3d/{model_rel}' if model_rel else '',
                'collision': f'3d/Tanks/{collision_rel}' if collision_rel else '',
                'model_exists': bool(model_rel and (PACK / '3d' / model_rel).is_file()),
                'collision_exists': bool(collision_rel and (PACK / '3d/Tanks' / collision_rel).is_file()),
                'modules': counts,
                'unlocks': [
                    {'kind': kind, 'target': target, 'xp': cost}
                    for kind, target, cost in edges
                ],
            }

        reachable = set()
        queue = deque([root_id])
        while queue:
            vehicle_id = queue.popleft()
            if vehicle_id in reachable or vehicle_id not in vehicles:
                continue
            reachable.add(vehicle_id)
            queue.extend(all_edges.get(vehicle_id, []))

        regular = [vehicles[key] for key in reachable]
        regular.sort(key=lambda item: (item['tier'], item['display_name'].casefold(), item['id']))
        for vehicle in regular:
            vehicle['next_tanks'] = [
                edge for edge in vehicle['unlocks']
                if edge['kind'] == 'vehicle' and edge['target'] in reachable
            ]
        nation_totals = defaultdict(int)
        nation_totals['tanks'] = len(regular)
        for vehicle in regular:
            for key, value in vehicle['modules'].items():
                nation_totals[key] += value
        result['nations'][nation] = {
            'display_name': nation_name,
            'root': root_id,
            'totals': dict(nation_totals),
            'tanks': regular,
        }
        for key, value in nation_totals.items():
            totals[key] += value

    result['totals'] = dict(totals)
    return result


def write_plan(inventory):
    totals = inventory['totals']
    data_path = UNITY / 'Assets/Blitz/Resources/Data/tanks.json'
    manifest_path = UNITY / 'Assets/Blitz/Resources/Data/tank_models_source_manifest.json'
    cards = json.loads(data_path.read_text(encoding='utf-8'))['tanks'] if data_path.exists() else []
    source_to_name = {card.get('source'): card.get('name') for card in cards}
    manifest = json.loads(manifest_path.read_text(encoding='utf-8')) if manifest_path.exists() else {}
    ported = set(manifest.get('tanks', {}))
    lines = [
        '# Полный порт прокачиваемых танков Blitz 4.8.0 — план',
        '',
        'Источник истины: `/home/smh01/Studio canaryProjects/BlitzPACKSold/`.',
        'В выборку входят только машины, достижимые из стартового танка нации через',
        '`unlocks/vehicle`. Премиумные, коллекционные, ивентовые и tutorial-машины',
        'не включены. Инвентарь генерирует `Tools/audit_blitz_480_tanks.py`.',
        '',
        f"**Объём:** {totals.get('tanks', 0)} танков; {totals.get('turrets', 0)} башен; "
        f"{totals.get('guns', 0)} уникальных орудий в пределах карточек; "
        f"{totals.get('chassis', 0)} ходовых; {totals.get('engines', 0)} двигателей; "
        f"{totals.get('radios', 0)} радиостанций.",
        f"**Ассеты экспортированы:** {len(ported)}/{totals.get('tanks', 0)} танков.",
        '',
        '## Статусы',
        '',
        '- [ ] не начато',
        '- [/] в работе',
        '- [x] готово и проверено',
        '',
        '## Общие этапы',
        '',
        '- [x] Автоматически выявить полный непремиумный граф исследований 4.8.0.',
        '- [x] Зафиксировать все танки и количество модулей в этом плане и JSON-инвентаре.',
        '- [x] Обобщить генерацию данных с одной советской ветки на все 229 карточек (`tanks_480.json`).',
        '- [x] Сделать радиостанции полноценным типом модуля: данные, сохранение, исследование, установка и UI.',
        '- [/] Проверить полные ТТХ корпуса, башен, орудий, снарядов, двигателей, ходовых и радио из XML перед включением таблицы в игру.',
        '- [ ] Перенести точный граф исследований, XP/кредитные цены и совместимость башня–орудие.',
        '- [x] Обобщить экспорт моделей/коллизий/брони/пивотов/текстур на все нации (`port_blitz_480_models.py`).',
        f"- [/] Пакетно экспортировать все 229 машин: {len(ported)}/{totals.get('tanks', 0)} готовы.",
        '- [ ] Добавить все карточки в дерево развития и корректно разложить ветвящийся граф в UI.',
        '- [ ] Добавить/проверить локализованные названия без хардкода пользовательских строк.',
        '- [ ] Написать проверки полноты: XML ↔ JSON ↔ модели ↔ броня ↔ пивоты ↔ UI.',
        '- [ ] Прогнать compile, все EditMode-тесты, hangar-smoke и battle-smoke.',
        '- [ ] Визуально проверить каждую конфигурацию башня–орудие и выборочно все уровни/нации.',
        '- [ ] Зафиксировать методику и обнаруженные исключения в `PORTING_NOTES.md`.',
        '',
        '## Нации и танки',
        '',
    ]
    for nation_key, nation in inventory['nations'].items():
        nt = nation['totals']
        expected_models = {
            source_to_name.get(tank['source_xml']) for tank in nation['tanks']
        }
        expected_models.discard(None)
        model_done = len(expected_models) == len(nation['tanks']) and expected_models <= ported
        lines += [
            f"### {nation['display_name']} — {nt['tanks']} танков",
            '',
            '| Готово | Ур. | Танк | Файл | Башни | Орудия | Ходовые | Двигатели | Радио | Открывает |',
            '|---|---:|---|---|---:|---:|---:|---:|---:|---|',
        ]
        for tank in nation['tanks']:
            m = tank['modules']
            next_tanks = ', '.join(
                f"{edge['target']} ({edge['xp']} XP)" for edge in tank['next_tanks']
            ) or '—'
            lines.append(
                f"| [ ] | {tank['tier']} | {tank['display_name']} | `{tank['id']}` | "
                f"{m['turrets']} | {m['guns']} | {m['chassis']} | {m['engines']} | {m['radios']} | {next_tanks} |"
            )
        lines += ['', f"- [ ] Данные и граф исследований: {nation['display_name']}",
                  f"- [{'x' if model_done else ' '}] Модели, текстуры, коллизии и броня: {nation['display_name']}",
                  f"- [ ] UI, ангар и бой: {nation['display_name']}", '']

    lines += [
        '## Гейты готовности одного танка', '',
        'Танк отмечается `[x]` только если:', '',
        '1. все модули и все unlock-рёбра совпадают с клиентским XML;',
        '2. каждая допустимая пара башня–орудие имеет отдельный меш, корректные пивоты и дуло;',
        '3. корпус, ходовая, броня, текстуры и коллизия загружены из пака 4.8.0;',
        '4. исследование, покупка, установка и несовместимость работают в UI;',
        '5. танк корректно создаётся в ангаре и бою, стреляет всеми типами снарядов;',
        '6. автоматические тесты проходят без исключений и пропущенных ассетов.',
        '',
        '## Текущий следующий шаг', '',
        '- [x] Реализовать универсальный генератор `tanks_480.json` для всех карточек, не опираясь на старые десять карточек как источник баланса.',
        '- [/] Расширить модельный порт на пути `blitzModelPath`/`collisionMesh` из XML и прогонять пакетами по нации.',
        '- [ ] После готовности ассетов атомарно заменить live `tanks.json` на проверенную таблицу 4.8.0.',
        '',
        '## Контрольная точка перед перезапуском ПК', '',
        '- [x] США: экспортированы и проверены модели, текстуры, коллизии и броня для 46/46 танков.',
        '- [x] Unity compile после экспорта США завершён без ошибок.',
        '- [x] EditMode: 459/459 тестов пройдено.',
        '- [ ] Следом экспортировать Германию (55 танков): `python3 Tools/port_blitz_480_models.py --nation germany --textures`.',
        '- [ ] Повторно сгенерировать план: `python3 Tools/audit_blitz_480_tanks.py`.',
        '- [ ] Выполнить итоговый аудит данных и графа исследований всех семи наций.',
        '- [ ] После 229/229 ассетов переключить live `tanks.json` на таблицу 4.8.0.',
        '- [ ] Доработать ветвящийся Tech Tree UI и поддержку Франции/Китая в отображении наций.',
        '- [ ] Прогнать полные EditMode, hangar-smoke, battle-smoke и визуальные проверки.',
    ]
    PLAN.write_text('\n'.join(lines) + '\n', encoding='utf-8')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--inventory-only', action='store_true')
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    inventory = build_inventory()
    payload = json.dumps(inventory, ensure_ascii=False, indent=2, sort_keys=True) + '\n'
    if args.check:
        expected = OUT.read_text(encoding='utf-8') if OUT.exists() else ''
        if expected != payload:
            raise SystemExit('inventory is stale')
        print('inventory is up to date')
        return
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(payload, encoding='utf-8')
    if not args.inventory_only:
        write_plan(inventory)
    print(json.dumps(inventory['totals'], ensure_ascii=False, sort_keys=True))


if __name__ == '__main__':
    main()
