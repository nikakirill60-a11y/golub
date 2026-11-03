using System;
using System.Collections.Generic;
using System.Linq;
using BlitzLegacy.Runtime.Hangar.Halloween;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Tests
{
    /// <summary>The scenario programs' interpreter on hand-written programs.</summary>
    public sealed class PortedHangarScenarioTests
    {
        private GameObject _node;

        [TearDown]
        public void TearDown()
        {
            if (_node != null) Object.DestroyImmediate(_node);
        }

        private PortedHangarScenario Build(string program, params Transform[] targets)
        {
            _node = _node != null ? _node : new GameObject("Scenario");
            var nodes = new List<Transform> { _node.transform };
            nodes.AddRange(targets);
            var indices = string.Join(",", Enumerable.Range(1, targets.Length));
            var record = HalloweenJson.Parse("{\"node\":0,\"path\":\"/test\",\"targets\":[" + indices + "],\"program\":" + program + "}");
            var scenario = _node.AddComponent<PortedHangarScenario>();
            scenario.Configure(record, nodes.ToArray());
            return scenario;
        }

        private static string Int(long value) => "{\"k\":\"const\",\"type\":\"int\",\"value\":" + value + "}";

        [Test]
        public void IntegerMathFollowsCpp()
        {
            Assert.AreEqual(-3, PortedHangarScenario.Divide(-7, 2), "Division truncates toward zero");
            Assert.AreEqual(-1, PortedHangarScenario.Remainder(-7, 2), "The remainder keeps the dividend's sign");
            Assert.AreEqual(0, PortedHangarScenario.Divide(5, 0));
            Assert.AreEqual(0, PortedHangarScenario.Remainder(5, 0));
            Assert.AreEqual(0, PortedHangarScenario.Divide(long.MinValue, -1));
            Assert.AreEqual(0, PortedHangarScenario.Remainder(long.MinValue, -1));
        }

        [Test]
        public void ProgramReadsTheClockAndSharesItsLists()
        {
            var targetObject = new GameObject("A");
            targetObject.transform.SetParent(new GameObject("Holder").transform, false);
            _node = targetObject.transform.parent.gameObject;
            var scenario = Build(
                "{\"variables\":{\"N\":" + Int(0) + ",\"On\":{\"k\":\"const\",\"type\":\"bool\",\"value\":true}}," +
                "\"shared\":{\"Show\":[{\"op\":\"visible\",\"target\":0,\"value\":{\"k\":\"var\",\"name\":\"On\"}}]}," +
                "\"events\":[[{\"op\":\"set\",\"var\":\"N\",\"value\":{\"k\":\"+\",\"a\":{\"k\":\"now\",\"field\":\"Month\"},\"b\":" + Int(1) + "}}]," +
                "[{\"op\":\"if\",\"condition\":{\"k\":\">=\",\"a\":{\"k\":\"var\",\"name\":\"N\"},\"b\":" + Int(6) + "}," +
                "\"then\":[{\"op\":\"set\",\"var\":\"On\",\"value\":{\"k\":\"const\",\"type\":\"bool\",\"value\":false}},{\"op\":\"call\",\"name\":\"Show\"}]," +
                "\"else\":[{\"op\":\"set\",\"var\":\"On\",\"value\":{\"k\":\"const\",\"type\":\"bool\",\"value\":true}},{\"op\":\"call\",\"name\":\"Show\"}]}]]}",
                targetObject.transform);
            Assert.IsTrue(scenario.UsesClock);
            Assert.AreEqual(1, scenario.TargetCount);

            scenario.Tick(0f, new DateTime(2026, 5, 1));
            Assert.AreEqual(5, scenario.Read("N").Int, "DAVA's month counts from 0");
            Assert.IsTrue(targetObject.activeSelf);
            scenario.Tick(0f, new DateTime(2026, 6, 1));
            Assert.AreEqual(6, scenario.Read("N").Int);
            Assert.IsFalse(targetObject.activeSelf);
        }

        [Test]
        public void MalformedProgramsStopTheBuild()
        {
            // A bool variable set from an int.
            Assert.Throws<InvalidOperationException>(() => Build(
                "{\"variables\":{\"B\":{\"k\":\"const\",\"type\":\"bool\",\"value\":false}},\"shared\":{}," +
                "\"events\":[[{\"op\":\"set\",\"var\":\"B\",\"value\":" + Int(1) + "}]]}"));
            Object.DestroyImmediate(_node);
            // Shared lists that call each other forever.
            Assert.Throws<InvalidOperationException>(() => Build(
                "{\"variables\":{},\"shared\":{\"A\":[{\"op\":\"call\",\"name\":\"B\"}],\"B\":[{\"op\":\"call\",\"name\":\"A\"}]}," +
                "\"events\":[[{\"op\":\"call\",\"name\":\"A\"}]]}"));
            Object.DestroyImmediate(_node);
            // A statement the runtime does not know.
            Assert.Throws<InvalidOperationException>(() => Build(
                "{\"variables\":{},\"shared\":{},\"events\":[[{\"op\":\"teleport\"}]]}"));
            Object.DestroyImmediate(_node);
            // A material parameter on a node without a renderer.
            Assert.Throws<InvalidOperationException>(() => Build(
                "{\"variables\":{},\"shared\":{},\"events\":[[{\"op\":\"material\",\"property\":\"_FlatColor\",\"value\":" +
                "{\"k\":\"const\",\"type\":\"vector4\",\"value\":[1,1,1,1]}}]]}"));
        }
    }
}
