"""Compile the DAVA scenario graphs of the Miku hangar into programs the client runs natively.

A scenario graph (`ScenarioSingletonComponent.ssc.scripts[<id>]`) has `nodes`
(name -> {type, node, connections}) and `variables` (name -> any). Every
connection is listed on both of its nodes: `pinIn` is the pin of the node that
lists it, `target`/`pinOut` the pin on the other end. Execution starts at the
`Engine Tick` events, which run once a frame in the order of their node names,
and follows the exec pins (`event`, `true`, `false`, `exit` -> `exec`). Data pins
are pulled when an executing node needs them, so pure nodes become expressions
at the place of use and variables are read when they are pulled.

Only the node kinds the hangar uses are compiled; any other kind, a dangling
exec pin or an ambiguous connection stops the export instead of guessing:

* events: `Engine Tick` (`Delta Time` is the frame's delta time);
* flow: Branch, SetVar, SetMember `Entity.scenarioVisibility`;
* functions: `DateTime.Now`, int64 `==` `<=` `>=` `Add` `Divide (a / b)`
  `Mod (a | b)` (C++ integer semantics), bool `Or`, float32 `Lerp`, `Vector4.Make`,
  `Quaternion.MakeFastAroundX/Y/Z` feeding `Entity.Rotate`,
  `Entity.FindChildByName` (resolved by the exporter),
  `Entity.SetMaterialParameter`;
* GetVar, GetMember of `DateTime` (Month counts from 0, Day from 1), SceneInfo
  `GetCurrentEntity`.

The program of a graph (`PortedHangarScenario` runs it) is plain JSON:

* `events`: one statement list per Engine Tick event, in the client's order;
  `shared`: statement lists reached from more than one exec pin, by node name;
* statements: `set` (var, value), `visible` (target, value), `rotate` (axis,
  degrees), `material` (property, value), `if` (condition, then, else), `call`
  (name of a shared list);
* expressions: `const` (type int / float / bool / vector4 / string, value),
  `dt`, `var` (name), `now` (field), `==` `<=` `>=` `+` `div` `mod` `or` (a, b),
  `lerp` (a, b, t), `vector4` (x, y, z, w).

Unity differences the compiler bridges:

* `Entity.Rotate` turns the node about its local axis. A DAVA axis is the Unity
  axis of the coordinate swap (x, y, z) -> (x, z, y), and the swap is a
  reflection, so the program names the Unity axis negated. The client rotates by
  the authored angle once per frame; the runtime scales it by
  `dt * REFERENCE_TICK_RATE`, the speed the hangar has at 60 frames a second;
* `SetMaterialParameter` names the port shader's property (`flatColor` ->
  `_FlatColor`); the runtime sets it on the renderer alone, as the graph does
  not modify parent materials.
"""
from __future__ import annotations

import re
from dataclasses import dataclass, field

REFERENCE_TICK_RATE = 60

EXEC_OUT = {'event', 'true', 'false', 'exit'}
DATETIME_FIELDS = ('Year', 'Month', 'Day', 'Hour', 'Minute', 'Second')
# DAVA material property -> the port shader property (HalloweenHangar/Textured).
MATERIAL_PROPERTIES = {'flatColor': '_FlatColor'}
# Quaternion.MakeFastAround<axis>: DAVA axis -> Unity local axis, negated by the reflection.
ROTATION_AXES = {'X': [-1, 0, 0], 'Y': [0, 0, -1], 'Z': [0, -1, 0]}
CONSTANT_TYPES = {'bool': 'bool', 'int64': 'int', 'int32': 'int', 'uint32': 'int', 'uint64': 'int',
                  'float32': 'float', 'float64': 'float', 'String': 'string', 'Vector4': 'vector4'}
BINARY = {('int64', '=='): '==', ('int64', '<='): '<=', ('int64', '>='): '>=', ('int64', 'Add'): '+',
          ('int64', 'Divide (a / b)'): 'div', ('int64', 'Mod (a | b)'): 'mod', ('bool', 'Or'): 'or'}


class ScenarioError(SystemExit):
    pass


def natural_key(name: str):
    return [int(part) if part.isdigit() else part for part in re.split(r'(\d+)', name)]


def constant(any_value: dict) -> dict:
    kind = CONSTANT_TYPES.get(any_value.get('any.type'))
    value = any_value.get('any.value')
    if kind is None:
        raise ScenarioError(f'unsupported constant type {any_value.get("any.type")}')
    if kind == 'bool':
        value = bool(value)
    elif kind == 'int':
        value = int(value)
    elif kind == 'float':
        value = float(value)
    elif kind == 'string':
        value = str(value)
    else:
        value = [float(v) for v in value]
        if len(value) != 4:
            raise ScenarioError(f'Vector4 constant with {len(value)} components')
    return {'k': 'const', 'type': kind, 'value': value}


@dataclass
class Program:
    """What one graph compiles to and what the exporter has to check or keep."""
    script_id: str
    events: list[list[dict]]
    shared: dict[str, list[dict]]
    variables: dict[str, dict]
    targets: list[str] = field(default_factory=list)        # FindChildByName results, relative paths
    material_properties: list[str] = field(default_factory=list)
    rotations: list[str] = field(default_factory=list)       # DAVA axis per Rotate node
    uses_clock: bool = False
    nodes: int = 0

    def record(self) -> dict:
        return {'events': self.events, 'shared': self.shared, 'variables': self.variables}


class Graph:
    def __init__(self, script_id: str, script: dict, entity_path: str, resolve_child):
        self.id = script_id
        self.path = entity_path
        self.resolve_child = resolve_child
        self.nodes = {}
        for name, entry in (script.get('nodes') or {}).items():
            node = {k.split('.')[-1]: v for k, v in ((entry or {}).get('node') or {}).items()}
            connections = [dict(c) for c in ((entry or {}).get('connections') or {}).values()]
            self.nodes[str(name)] = {'type': str(entry.get('type')), 'node': node, 'connections': connections}
        self.variables = {str(k): v for k, v in (script.get('variables') or {}).items()}
        self.targets: list[str] = []
        self.target_index: dict[str, int] = {}
        self.material_properties: list[str] = []
        self.rotations: list[str] = []
        self.uses_clock = False
        self.shared: dict[str, list[dict]] = {}
        self.written_vars = {self.node(n)['node'].get('varPath') for n in self.nodes
                             if self.nodes[n]['type'] == 'ScenarioSetVarNode'}
        self.check_connections()

    # ---------------------------------------------------------------- graph access
    def fail(self, message: str):
        raise ScenarioError(f'scenario {self.id} ({self.path}): {message}')

    def node(self, name: str) -> dict:
        if name not in self.nodes:
            self.fail(f'connection to missing node {name}')
        return self.nodes[name]

    def kind(self, name: str) -> tuple:
        n = self.node(name)
        return n['type'], n['node'].get('className'), n['node'].get('functionName')

    def check_connections(self) -> None:
        """Every connection must be listed by both of its nodes."""
        for name, n in self.nodes.items():
            for c in n['connections']:
                other = self.node(str(c['target']))
                mirror = [m for m in other['connections']
                          if str(m['target']) == name and m['pinIn'] == c['pinOut'] and m['pinOut'] == c['pinIn']]
                if not mirror:
                    self.fail(f'{name}.{c["pinIn"]} -> {c["target"]}.{c["pinOut"]} is listed on one side only')

    def inputs(self, name: str, pin: str) -> list[tuple[str, str]]:
        return [(str(c['target']), str(c['pinOut'])) for c in self.node(name)['connections'] if c['pinIn'] == pin]

    def default(self, name: str, pin: str):
        return (self.node(name)['node'].get('defaults') or {}).get(pin)

    def data_source(self, name: str, pin: str) -> tuple[str, str] | None:
        sources = self.inputs(name, pin)
        if len(sources) > 1:
            self.fail(f'{name}.{pin} has {len(sources)} incoming data connections')
        return sources[0] if sources else None

    def exec_next(self, name: str, pin: str) -> str | None:
        targets = self.inputs(name, pin)
        if len(targets) > 1:
            self.fail(f'{name}.{pin} starts {len(targets)} exec connections')
        if not targets:
            return None
        target, target_pin = targets[0]
        if target_pin != 'exec':
            self.fail(f'{name}.{pin} leads to {target}.{target_pin}, not to an exec pin')
        return target

    def exec_predecessors(self, name: str) -> list[tuple[str, str]]:
        return [(t, p) for t, p in self.inputs(name, 'exec') if p in EXEC_OUT]

    # ---------------------------------------------------------------- expressions
    def value(self, name: str, pin: str) -> dict:
        """The expression of input `pin` of node `name`."""
        source = self.data_source(name, pin)
        if source is None:
            default = self.default(name, pin)
            if not isinstance(default, dict) or 'any.type' not in default:
                self.fail(f'{name}.{pin} is neither connected nor has a default')
            return constant(default)
        return self.output(*source)

    def output(self, name: str, pin: str) -> dict:
        n = self.node(name)
        kind, cls, fn = self.kind(name)
        if kind == 'ScenarioEventNode' and pin == 'Delta Time':
            return {'k': 'dt'}
        if kind == 'ScenarioGetVarNode' and pin == 'value':
            var = str(n['node'].get('varPath'))
            if var not in self.variables:
                self.fail(f'{name} reads undeclared variable {var}')
            return {'k': 'var', 'name': var}
        if kind == 'ScenarioGetMemberNode':
            if cls != 'DateTime' or pin != n['node'].get('fieldName') or pin not in DATETIME_FIELDS:
                self.fail(f'{name}: unsupported member {cls}.{pin}')
            source = self.data_source(name, 'object')
            if source is None or self.kind(source[0])[1:] != ('DateTime', 'Now'):
                self.fail(f'{name}: DateTime member of something other than DateTime.Now')
            self.uses_clock = True
            return {'k': 'now', 'field': pin}
        if kind == 'ScenarioFunctionNode' and pin == 'result':
            return self.function(name, cls, fn)
        if kind == 'ScenarioSceneInfoNode':
            self.fail(f'{name}: the current entity is only supported as the target of entity functions')
        self.fail(f'{name}.{pin}: unsupported data output of {kind} {cls}.{fn}')

    def function(self, name: str, cls, fn) -> dict:
        if (cls, fn) in BINARY:
            return {'k': BINARY[(cls, fn)], 'a': self.value(name, 'a'), 'b': self.value(name, 'b')}
        if (cls, fn) == ('float32', 'Lerp (t=0 => a; t=1 => b)'):
            return {'k': 'lerp', 'a': self.value(name, 'a'), 'b': self.value(name, 'b'), 't': self.value(name, 't')}
        if (cls, fn) == ('Vector4', 'Make'):
            return dict({'k': 'vector4'}, **{c: self.value(name, c) for c in 'xyzw'})
        if (cls, fn) == ('Entity', 'FindChildByName'):
            self.fail(f'{name}: FindChildByName only names the target of scenarioVisibility')
        self.fail(f'{name}: unsupported function {cls}.{fn}')

    def require_self(self, name: str, pin: str) -> None:
        source = self.data_source(name, pin)
        if source is None or self.node(source[0])['type'] != 'ScenarioSceneInfoNode' \
                or self.node(source[0])['node'].get('infoKind') != 'GetCurrentEntity':
            self.fail(f'{name}.{pin} must be the current entity')

    def target(self, name: str) -> int:
        """Index of the FindChildByName node `name` in the program's target list."""
        if self.kind(name)[1:] != ('Entity', 'FindChildByName'):
            self.fail(f'{name}: not a FindChildByName node')
        self.require_self(name, 'entity')
        child = self.default(name, 'name')
        if self.data_source(name, 'name') is not None or not isinstance(child, dict):
            self.fail(f'{name}: FindChildByName needs a constant name')
        child = str(child['any.value'])
        if child not in self.target_index:
            self.target_index[child] = len(self.targets)
            self.targets.append(self.resolve_child(child))
        return self.target_index[child]

    def constant_string(self, name: str, pin: str) -> str:
        """A string input that cannot change at run time: a default or a variable no SetVar writes."""
        source = self.data_source(name, pin)
        if source is None:
            default = self.default(name, pin) or {}
            if default.get('any.type') != 'String':
                self.fail(f'{name}.{pin} is not a string')
            return str(default['any.value'])
        node = self.node(source[0])
        var = node['node'].get('varPath')
        if node['type'] != 'ScenarioGetVarNode' or var in self.written_vars or var not in self.variables:
            self.fail(f'{name}.{pin} is not a constant string')
        value = self.variables[var]
        if value.get('any.type') != 'String':
            self.fail(f'{name}.{pin} reads the non-string variable {var}')
        return str(value['any.value'])

    # ---------------------------------------------------------------- statements
    def statement(self, name: str) -> tuple[dict, str]:
        """The statement of exec node `name` and the exec pin execution continues from."""
        n = self.node(name)
        kind, cls, fn = self.kind(name)
        if kind == 'ScenarioSetVarNode':
            var = str(n['node'].get('varPath'))
            if var not in self.variables:
                self.fail(f'{name} writes undeclared variable {var}')
            return {'op': 'set', 'var': var, 'value': self.value(name, 'set')}, 'exit'
        if kind == 'ScenarioSetMemberNode':
            if (cls, n['node'].get('fieldName')) != ('Entity', 'scenarioVisibility'):
                self.fail(f'{name}: unsupported member {cls}.{n["node"].get("fieldName")}')
            source = self.data_source(name, 'object')
            if source is None or source[1] != 'result':
                self.fail(f'{name}: scenarioVisibility of something other than FindChildByName')
            return {'op': 'visible', 'target': self.target(source[0]), 'value': self.value(name, 'set')}, 'exit'
        if kind == 'ScenarioFunctionNode' and (cls, fn) == ('Entity', 'Rotate'):
            self.require_self(name, 'self')
            source = self.data_source(name, 'value')
            maker = self.kind(source[0]) if source else None
            if maker is None or maker[1] != 'Quaternion' or not str(maker[2]).startswith('MakeFastAround'):
                self.fail(f'{name}: Rotate by something other than Quaternion.MakeFastAround<axis>')
            axis = str(maker[2])[len('MakeFastAround'):]
            if axis not in ROTATION_AXES:
                self.fail(f'{name}: unknown rotation axis {axis}')
            self.rotations.append(axis)
            return {'op': 'rotate', 'axis': ROTATION_AXES[axis], 'degrees': self.value(source[0], 'deg')}, 'exit'
        if kind == 'ScenarioFunctionNode' and (cls, fn) == ('Entity', 'SetMaterialParameter'):
            self.require_self(name, 'entity')
            modify_parents = self.default(name, 'modify parent materials') or {}
            if self.data_source(name, 'modify parent materials') is not None or modify_parents.get('any.value'):
                self.fail(f'{name}: modifying parent materials is not supported')
            prop = self.constant_string(name, 'propName')
            if prop not in MATERIAL_PROPERTIES:
                self.fail(f'{name}: material parameter {prop} has no port shader property')
            if prop not in self.material_properties:
                self.material_properties.append(prop)
            return {'op': 'material', 'property': MATERIAL_PROPERTIES[prop], 'value': self.value(name, 'value')}, 'exit'
        self.fail(f'{name}: unsupported exec node {kind} {cls}.{fn}')

    def chain(self, name: str | None, entry: str | None, depth: int = 0) -> list[dict]:
        """The statements of the exec chain from `name`: nodes reached from more than
        one exec pin become shared lists, everything else is inlined."""
        statements = []
        visited = set()
        while name is not None:
            if depth > 400 or name in visited:
                self.fail(f'exec cycle through {name}')
            visited.add(name)
            if name != entry and len(self.exec_predecessors(name)) > 1:
                if name not in self.shared:
                    self.shared[name] = []  # reserve before compiling to stop recursion
                    self.shared[name] = self.chain(name, name, depth + 1)
                statements.append({'op': 'call', 'name': name})
                return statements
            if self.node(name)['type'] == 'ScenarioBranchNode':
                condition = self.value(name, 'condition')
                on_true = self.chain(self.exec_next(name, 'true'), None, depth + 1)
                on_false = self.chain(self.exec_next(name, 'false'), None, depth + 1)
                if condition.get('k') == 'const':
                    if condition['type'] != 'bool':
                        self.fail(f'{name}: branch on a {condition["type"]} constant')
                    statements.extend(on_true if condition['value'] else on_false)
                else:
                    statements.append({'op': 'if', 'condition': condition, 'then': on_true, 'else': on_false})
                return statements
            statement, out = self.statement(name)
            statements.append(statement)
            name = self.exec_next(name, out)
            depth += 1
        return statements

    # ---------------------------------------------------------------- program
    def compile(self) -> Program:
        events = sorted((n for n, v in self.nodes.items() if v['type'] == 'ScenarioEventNode'), key=natural_key)
        if not events:
            self.fail('no event node')
        bodies = []
        for name in events:
            event = self.node(name)['node'].get('eventName')
            if event != 'Engine Tick':
                self.fail(f'{name}: unsupported event {event}')
            bodies.append(self.chain(self.exec_next(name, 'event'), None))
        for name, n in self.nodes.items():
            if n['type'] in ('ScenarioBranchNode', 'ScenarioSetVarNode', 'ScenarioSetMemberNode') \
                    and not self.exec_predecessors(name):
                self.fail(f'{name} is never executed')
        variables = {k: constant(v) for k, v in sorted(self.variables.items())}
        shared = {k: self.shared[k] for k in sorted(self.shared, key=natural_key)}
        return Program(self.id, bodies, shared, variables, list(self.targets), list(self.material_properties),
                       list(self.rotations), self.uses_clock, len(self.nodes))


def compile_graph(script_id: str, script: dict, entity_path: str, resolve_child) -> Program:
    return Graph(script_id, script, entity_path, resolve_child).compile()
