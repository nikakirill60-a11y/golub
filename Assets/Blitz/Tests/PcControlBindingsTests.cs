using BlitzLegacy.Core.Input;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Serialization;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    /// <summary>Port checks for assets/data/battle_input_bindings.gd.</summary>
    public sealed class PcControlBindingsTests
    {
        [Test]
        public void TheActionTableIsTheSourceOne()
        {
            Assert.AreEqual(1, PcControlBindings.MaxSlots);
            Assert.AreEqual(20, PcControlBindings.Definitions.Count);
            Assert.AreEqual(
                "controls:action/move_fw",
                PcControlBindings.Find(PcControlBindings.MoveForward).LabelKey);
            Assert.AreEqual(
                "controls:category/movement",
                PcControlBindings.Find(PcControlBindings.MoveForward).CategoryKey);
        }

        [Test]
        public void TheDefaultsAreTheProjectInputMap()
        {
            var bindings = new PcControlBindings();
            Assert.AreEqual("W", bindings.Get(PcControlBindings.MoveForward));
            Assert.AreEqual("S", bindings.Get(PcControlBindings.MoveBack));
            Assert.AreEqual("A", bindings.Get(PcControlBindings.MoveLeft));
            Assert.AreEqual("D", bindings.Get(PcControlBindings.MoveRight));
            Assert.AreEqual(
                PcControlBindings.MouseLeft, bindings.Get(PcControlBindings.Attack));
            Assert.AreEqual(
                PcControlBindings.MouseRight,
                bindings.Get(PcControlBindings.BlockTurret));
            Assert.AreEqual(
                PcControlBindings.WheelUp, bindings.Get(PcControlBindings.ZoomIn));
            Assert.AreEqual(
                PcControlBindings.WheelDown, bindings.Get(PcControlBindings.ZoomOut));
            Assert.AreEqual("Escape", bindings.Get(PcControlBindings.Menu));
            Assert.AreEqual("Tab", bindings.Get(PcControlBindings.Scoreboard));
            Assert.IsTrue(bindings.IsDefault);
        }

        [Test]
        public void AnInputBelongsToExactlyOneAction()
        {
            var bindings = new PcControlBindings();

            var conflict = bindings.SetBinding(PcControlBindings.MoveBack, "W");

            Assert.AreEqual(PcControlBindings.MoveForward, conflict);
            Assert.AreEqual("W", bindings.Get(PcControlBindings.MoveBack));
            Assert.IsNull(bindings.Get(PcControlBindings.MoveForward));
        }

        [Test]
        public void ClearingLeavesTheActionUnbound()
        {
            var bindings = new PcControlBindings();

            bindings.ClearBinding(PcControlBindings.ReloadBurst);

            Assert.IsNull(bindings.Get(PcControlBindings.ReloadBurst));
            Assert.IsFalse(bindings.IsDefault);

            bindings.ResetToDefaults();

            Assert.AreEqual("C", bindings.Get(PcControlBindings.ReloadBurst));
            Assert.IsTrue(bindings.IsDefault);
        }

        [Test]
        public void OnlyTheOverridesSurviveASaveRoundTrip()
        {
            var bindings = new PcControlBindings();
            bindings.SetBinding(PcControlBindings.SniperMode, "LeftCtrl");
            bindings.ClearBinding(PcControlBindings.Fullscreen);

            var json = bindings.ToJson().ToJson();
            JsonNode node;
            Assert.IsTrue(JsonNode.TryParse(json, out node));
            var restored = PcControlBindings.FromJson(node);

            Assert.AreEqual("LeftCtrl", restored.Get(PcControlBindings.SniperMode));
            Assert.IsNull(restored.Get(PcControlBindings.Fullscreen));
            Assert.AreEqual("W", restored.Get(PcControlBindings.MoveForward));
        }

        [Test]
        public void TheSettingsFileCarriesTheBindings()
        {
            var settings = new SettingsData();
            settings.PcControls.SetBinding(PcControlBindings.Shell1, "Z");

            var restored = SettingsData.FromJson(settings.ToJson());

            Assert.AreEqual("Z", restored.PcControls.Get(PcControlBindings.Shell1));
        }

        [Test]
        public void MouseTokensAreLocalizedAndKeysAreNot()
        {
            Assert.AreEqual(
                "controls:mouse_left",
                PcControlBindings.TextKey(PcControlBindings.MouseLeft));
            Assert.AreEqual(
                "controls:wheel_down",
                PcControlBindings.TextKey(PcControlBindings.WheelDown));
            Assert.IsNull(PcControlBindings.TextKey("W"));
            Assert.AreEqual("1", PcControlBindings.KeyText("Digit1"));
            Assert.AreEqual("LeftShift", PcControlBindings.KeyText("LeftShift"));
        }
    }
}
