// One DAVA scenario graph of a ported hangar, compiled to a program by the
// exporter (Tools/miku_hangar_scenario.py, scene.json "scenarios") and run the
// way the client runs the graph: its Engine Tick events once a frame, in
// order. The Miku hangar's graphs switch the segments of the clock digits,
// tint the Earth by the hour and turn the disks and the light ray.
using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Hangar;
using UnityEngine;

namespace BlitzLegacy.Runtime.Hangar.Halloween
{
    public sealed class PortedHangarScenario : MonoBehaviour
    {
        /// <summary>
        /// The frame rate the client's per-frame turns are authored at: a
        /// Rotate turns by its angle once a frame there, here by the angle
        /// times the frame's share of that rate.
        /// </summary>
        public const float ReferenceTickRate = 60f;

        public enum ValueKind
        {
            Int,
            Float,
            Bool,
            Vector4,
            String,
        }

        /// <summary>A graph value: DAVA's int64, float32, bool, Vector4 or String.</summary>
        public readonly struct Value
        {
            public readonly ValueKind Kind;
            public readonly long Int;
            public readonly float Float;
            public readonly Vector4 Vector;
            public readonly string Text;

            private Value(ValueKind kind, long i, float f, Vector4 v, string s)
            {
                Kind = kind;
                Int = i;
                Float = f;
                Vector = v;
                Text = s;
            }

            public bool Bool => Int != 0;

            public static Value Of(long value) => new Value(ValueKind.Int, value, 0f, default, null);
            public static Value Of(float value) => new Value(ValueKind.Float, 0, value, default, null);
            public static Value Of(bool value) => new Value(ValueKind.Bool, value ? 1 : 0, 0f, default, null);
            public static Value Of(Vector4 value) => new Value(ValueKind.Vector4, 0, 0f, value, null);
            public static Value Of(string value) => new Value(ValueKind.String, 0, 0f, default, value);
        }

        private enum ClockField
        {
            Year,
            Month,
            Day,
            Hour,
            Minute,
            Second,
        }

        private readonly Dictionary<string, int> _variableIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _sharedIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<string> _variableNames = new List<string>();
        private Value[] _variables = new Value[0];
        private Action[] _events = new Action[0];
        private Action[] _shared = new Action[0];
        private List<int>[] _sharedCalls = new List<int>[0];
        private List<int> _compilingCalls;
        private GameObject[] _targets = new GameObject[0];
        private bool[] _visible = new bool[0];
        private Renderer _renderer;
        private MaterialPropertyBlock _block;
        private readonly Dictionary<int, Vector4> _materialValues = new Dictionary<int, Vector4>();
        private string _path = string.Empty;
        private float _dt;
        private DateTime _now;

        /// <summary>Nodes the program shows and hides (FindChildByName results).</summary>
        public int TargetCount => _targets.Length;

        public int RotationCount { get; private set; }
        public int MaterialSetterCount { get; private set; }

        /// <summary>Whether the program reads DateTime.Now.</summary>
        public bool UsesClock { get; private set; }

        /// <summary>
        /// Compiles <paramref name="record"/> (one entry of scene.json
        /// "scenarios") for the node it is added to; <paramref name="nodes"/>
        /// resolves its targets. Malformed programs stop the build.
        /// </summary>
        public void Configure(HalloweenJson record, Transform[] nodes)
        {
            _path = record["path"].S(name);
            var targets = record.Req("targets").Ints();
            _targets = new GameObject[targets.Length];
            _visible = new bool[targets.Length];
            for (int i = 0; i < targets.Length; i++)
            {
                HH.Require(targets[i] >= 0 && targets[i] < nodes.Length, $"{_path}: scenario target node {targets[i]}");
                _targets[i] = nodes[targets[i]].gameObject;
                _visible[i] = _targets[i].activeSelf;
            }

            var program = record.Req("program");
            var variables = program.Req("variables");
            _variables = new Value[variables.Count];
            foreach (var key in variables.Keys)
            {
                _variableIndex[key] = _variableNames.Count;
                _variables[_variableNames.Count] = Constant(variables.Req(key));
                _variableNames.Add(key);
            }

            var shared = program.Req("shared");
            foreach (var key in shared.Keys)
                _sharedIndex[key] = _sharedIndex.Count;
            _shared = new Action[_sharedIndex.Count];
            _sharedCalls = new List<int>[_sharedIndex.Count];
            foreach (var key in shared.Keys)
            {
                int index = _sharedIndex[key];
                _compilingCalls = _sharedCalls[index] = new List<int>();
                _shared[index] = Block(shared.Req(key));
            }
            _compilingCalls = null;
            for (int i = 0; i < _shared.Length; i++)
                RequireNoCycle(i, new HashSet<int>());

            var events = program.Req("events").Items;
            _events = new Action[events.Count];
            for (int i = 0; i < events.Count; i++)
                _events[i] = Block(events[i]);
        }

        /// <summary>The current value of a graph variable.</summary>
        public Value Read(string variable)
        {
            HH.Require(_variableIndex.TryGetValue(variable, out int index), $"{_path}: no variable {variable}");
            return _variables[index];
        }

        /// <summary>Runs the Engine Tick events once: a frame of <paramref name="dt"/> seconds at <paramref name="now"/>.</summary>
        public void Tick(float dt, DateTime now)
        {
            _dt = dt;
            _now = now;
            for (int i = 0; i < _events.Length; i++)
                _events[i]();
        }

        private void Update()
        {
            Tick(Time.deltaTime, LocalNow());
        }

        private static int _clockFrame = -1;
        private static DateTime _clockTime;

        /// <summary>
        /// DateTime.Now of the client: the game clock on the device's local
        /// calendar, read once a frame for all scenarios.
        /// </summary>
        public static DateTime LocalNow()
        {
            if (_clockFrame == Time.frameCount && Application.isPlaying)
                return _clockTime;
            _clockFrame = Time.frameCount;
            var bootstrap = BlitzBootstrap.Current;
            double unix = bootstrap != null && bootstrap.Clock != null
                ? bootstrap.Clock.NowUnixSeconds
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
            _clockTime = HangarSchedule.LocalTime(unix, null);
            return _clockTime;
        }

        // ------------------------------------------------------------ statements
        private Action Block(HalloweenJson list)
        {
            HH.Require(list.IsArray, $"{_path}: a statement list is not an array");
            var statements = new Action[list.Count];
            for (int i = 0; i < statements.Length; i++)
                statements[i] = Statement(list[i]);
            if (statements.Length == 1)
                return statements[0];
            return () =>
            {
                for (int i = 0; i < statements.Length; i++)
                    statements[i]();
            };
        }

        private Action Statement(HalloweenJson s)
        {
            string op = s.Req("op").S();
            switch (op)
            {
                case "set":
                {
                    string variable = s.Req("var").S();
                    HH.Require(_variableIndex.TryGetValue(variable, out int index), $"{_path}: set of undeclared {variable}");
                    var value = Typed(s.Req("value"), _variables[index].Kind, "set " + variable);
                    return () => _variables[index] = value();
                }
                case "visible":
                {
                    int target = s.Req("target").I(-1);
                    HH.Require(target >= 0 && target < _targets.Length, $"{_path}: visibility of target {target}");
                    var value = Typed(s.Req("value"), ValueKind.Bool, "visible");
                    return () => SetVisible(target, value().Bool);
                }
                case "rotate":
                {
                    Vector3 axis = s.Req("axis").V3();
                    HH.Require(axis.sqrMagnitude > 0.5f, $"{_path}: rotation axis {axis}");
                    var degrees = Typed(s.Req("degrees"), ValueKind.Float, "rotate");
                    RotationCount++;
                    return () => transform.Rotate(axis, degrees().Float * _dt * ReferenceTickRate, Space.Self);
                }
                case "material":
                {
                    string property = s.Req("property").S();
                    if (_renderer == null)
                        _renderer = GetComponent<Renderer>();
                    HH.Require(_renderer != null, $"{_path}: sets {property} but the node draws nothing");
                    foreach (var material in _renderer.sharedMaterials)
                        HH.Require(material != null && material.HasProperty(property),
                            $"{_path}: a material of the node has no {property}");
                    int id = Shader.PropertyToID(property);
                    var value = Typed(s.Req("value"), ValueKind.Vector4, "material " + property);
                    MaterialSetterCount++;
                    return () => SetMaterial(id, value().Vector);
                }
                case "if":
                {
                    var condition = Typed(s.Req("condition"), ValueKind.Bool, "if");
                    var then = Block(s.Req("then"));
                    var otherwise = Block(s.Req("else"));
                    return () =>
                    {
                        if (condition().Bool) then();
                        else otherwise();
                    };
                }
                case "call":
                {
                    string list = s.Req("name").S();
                    HH.Require(_sharedIndex.TryGetValue(list, out int index), $"{_path}: call of unknown {list}");
                    _compilingCalls?.Add(index);
                    return () => _shared[index]();
                }
            }
            throw new InvalidOperationException($"{_path}: unknown statement {op}");
        }

        // A shared list that calls itself again would never return.
        private void RequireNoCycle(int list, HashSet<int> path)
        {
            HH.Require(path.Add(list), $"{_path}: the shared statement lists call each other in a cycle");
            foreach (int callee in _sharedCalls[list])
                RequireNoCycle(callee, path);
            path.Remove(list);
        }

        private void SetVisible(int target, bool visible)
        {
            if (_visible[target] == visible)
                return;
            _visible[target] = visible;
            _targets[target].SetActive(visible);
        }

        private void SetMaterial(int property, Vector4 value)
        {
            if (_materialValues.TryGetValue(property, out var current) && current == value)
                return;
            _materialValues[property] = value;
            if (_block == null)
                _block = new MaterialPropertyBlock();
            _renderer.GetPropertyBlock(_block);
            _block.SetVector(property, value);
            _renderer.SetPropertyBlock(_block);
        }

        // ----------------------------------------------------------- expressions
        private Func<Value> Typed(HalloweenJson e, ValueKind kind, string use)
        {
            var compiled = Expression(e, out var actual);
            HH.Require(actual == kind, $"{_path}: {use} needs a {kind}, the program gives a {actual}");
            return compiled;
        }

        private Func<Value> Expression(HalloweenJson e, out ValueKind kind)
        {
            string k = e.Req("k").S();
            switch (k)
            {
                case "const":
                {
                    var value = Constant(e);
                    kind = value.Kind;
                    return () => value;
                }
                case "dt":
                    kind = ValueKind.Float;
                    return () => Value.Of(_dt);
                case "var":
                {
                    string variable = e.Req("name").S();
                    HH.Require(_variableIndex.TryGetValue(variable, out int index), $"{_path}: read of undeclared {variable}");
                    kind = _variables[index].Kind;
                    return () => _variables[index];
                }
                case "now":
                {
                    string name = e.Req("field").S();
                    HH.Require(Enum.TryParse(name, false, out ClockField field), $"{_path}: DateTime has no field {name}");
                    UsesClock = true;
                    kind = ValueKind.Int;
                    return () => Value.Of(Clock(field));
                }
                case "==":
                case "<=":
                case ">=":
                {
                    var a = Typed(e.Req("a"), ValueKind.Int, k);
                    var b = Typed(e.Req("b"), ValueKind.Int, k);
                    kind = ValueKind.Bool;
                    if (k == "==") return () => Value.Of(a().Int == b().Int);
                    if (k == "<=") return () => Value.Of(a().Int <= b().Int);
                    return () => Value.Of(a().Int >= b().Int);
                }
                case "+":
                case "div":
                case "mod":
                {
                    var a = Typed(e.Req("a"), ValueKind.Int, k);
                    var b = Typed(e.Req("b"), ValueKind.Int, k);
                    kind = ValueKind.Int;
                    if (k == "+") return () => Value.Of(unchecked(a().Int + b().Int));
                    if (k == "div") return () => Value.Of(Divide(a().Int, b().Int));
                    return () => Value.Of(Remainder(a().Int, b().Int));
                }
                case "or":
                {
                    var a = Typed(e.Req("a"), ValueKind.Bool, k);
                    var b = Typed(e.Req("b"), ValueKind.Bool, k);
                    kind = ValueKind.Bool;
                    return () => Value.Of(a().Bool || b().Bool);
                }
                case "lerp":
                {
                    var a = Typed(e.Req("a"), ValueKind.Float, k);
                    var b = Typed(e.Req("b"), ValueKind.Float, k);
                    var t = Typed(e.Req("t"), ValueKind.Float, k);
                    kind = ValueKind.Float;
                    // DAVA's Lerp is unclamped: a + (b - a) * t.
                    return () =>
                    {
                        float from = a().Float;
                        return Value.Of(from + (b().Float - from) * t().Float);
                    };
                }
                case "vector4":
                {
                    var x = Typed(e.Req("x"), ValueKind.Float, k);
                    var y = Typed(e.Req("y"), ValueKind.Float, k);
                    var z = Typed(e.Req("z"), ValueKind.Float, k);
                    var w = Typed(e.Req("w"), ValueKind.Float, k);
                    kind = ValueKind.Vector4;
                    return () => Value.Of(new Vector4(x().Float, y().Float, z().Float, w().Float));
                }
            }
            throw new InvalidOperationException($"{_path}: unknown expression {k}");
        }

        private Value Constant(HalloweenJson e)
        {
            HH.Require(e.Req("k").S() == "const", $"{_path}: a variable starts from something other than a constant");
            var value = e.Req("value");
            switch (e.Req("type").S())
            {
                case "int": return Value.Of(value.L());
                case "float": return Value.Of(value.F());
                case "bool": return Value.Of(value.B());
                case "string": return Value.Of(value.S(string.Empty));
                case "vector4":
                    HH.Require(value.Count == 4, $"{_path}: a Vector4 constant with {value.Count} components");
                    return Value.Of(value.V4());
            }
            throw new InvalidOperationException($"{_path}: constant of unknown type {e.Req("type").S()}");
        }

        // DAVA's DateTime: the month counts from 0, the day from 1.
        private long Clock(ClockField field)
        {
            switch (field)
            {
                case ClockField.Year: return _now.Year;
                case ClockField.Month: return _now.Month - 1;
                case ClockField.Day: return _now.Day;
                case ClockField.Hour: return _now.Hour;
                case ClockField.Minute: return _now.Minute;
                default: return _now.Second;
            }
        }

        // C++ int64 division truncates toward zero; the port gives 0 where the
        // client would divide by zero.
        public static long Divide(long a, long b) => b == 0 || (a == long.MinValue && b == -1) ? 0 : a / b;

        public static long Remainder(long a, long b) => b == 0 || b == -1 ? 0 : a % b;
    }
}
