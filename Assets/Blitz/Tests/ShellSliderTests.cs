using BlitzLegacy.Core.Localization;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The dragged HSlider of ui_shell_slider.tscn: the Range snapping, the
    /// centred fill Godot draws at the stylebox minimum height and the assets
    /// the row needs (SliderFrame, SliderThumb, the +/- icons and drag_01).
    /// </summary>
    public sealed class ShellSliderTests
    {
        [Test]
        public void Snap_RoundsToTheBurstAndClampsToTheRack()
        {
            Assert.AreEqual(0, ShellSliderMath.Snap(-12f, 50, 1));
            Assert.AreEqual(50, ShellSliderMath.Snap(120f, 50, 1));
            Assert.AreEqual(12, ShellSliderMath.Snap(11.6f, 50, 1));

            // step = the burst of an autoloader.
            Assert.AreEqual(10, ShellSliderMath.Snap(11.6f, 50, 5));
            Assert.AreEqual(15, ShellSliderMath.Snap(13f, 50, 5));
            Assert.AreEqual(50, ShellSliderMath.Snap(49f, 50, 5));

            // A gun with no rack cannot be dragged at all.
            Assert.AreEqual(0, ShellSliderMath.Snap(5f, 0, 1));
        }

        [Test]
        public void ValueAt_MapsThePointerAcrossTheGrabberArea()
        {
            // 649 px of row and a 66 px grabber: 583 px of travel that starts
            // 33 px in, exactly like Slider::gui_input reads it back.
            Assert.AreEqual(0, ShellSliderMath.ValueAt(-40f, 649f, 66f, 50, 1));
            Assert.AreEqual(0, ShellSliderMath.ValueAt(33f, 649f, 66f, 50, 1));
            Assert.AreEqual(25, ShellSliderMath.ValueAt(324.5f, 649f, 66f, 50, 1));
            Assert.AreEqual(50, ShellSliderMath.ValueAt(616f, 649f, 66f, 50, 1));
            Assert.AreEqual(50, ShellSliderMath.ValueAt(9000f, 649f, 66f, 50, 1));
            Assert.AreEqual(0, ShellSliderMath.ValueAt(10f, 0f, 66f, 50, 1));

            // A row narrower than its own grabber still answers a press.
            Assert.AreEqual(50, ShellSliderMath.ValueAt(40f, 40f, 66f, 50, 1));

            // The pointer and the drawn grabber agree: reading the value back
            // from the middle of the grabber returns the same count, which is
            // what keeps a touch from moving the row at all.
            for (var value = 0; value <= 50; value += 5)
            {
                Assert.AreEqual(
                    value,
                    ShellSliderMath.ValueAt(
                        ShellSliderMath.FillWidth(649f, 66f, value, 50),
                        649f, 66f, 50, 1),
                    "the grabber does not sit under the pointer");
            }
        }

        [Test]
        public void Fill_IsProportionalAndSitsOnTheMiddleLine()
        {
            // grabber_area is drawn up to the centre of the grabber, so an
            // empty rack still shows the half grabber and a full one stops
            // half a grabber short of the right edge instead of hanging over
            // it.
            Assert.AreEqual(33f, ShellSliderMath.FillWidth(649f, 66f, 0, 50), 1e-3f);
            Assert.AreEqual(
                616f, ShellSliderMath.FillWidth(649f, 66f, 50, 50), 1e-3f);
            Assert.AreEqual(
                324.5f, ShellSliderMath.FillWidth(649f, 66f, 25, 50), 1e-3f);
            Assert.AreEqual(0f, ShellSliderMath.FillWidth(649f, 66f, 5, 0));

            // The grabber itself never leaves the frame.
            Assert.AreEqual(0f, ShellSliderMath.GrabberLeft(649f, 66f, 0, 50), 1e-3f);
            Assert.AreEqual(
                583f, ShellSliderMath.GrabberLeft(649f, 66f, 50, 50), 1e-3f);

            // 66 px row, 12 px bar: 27 px of empty space above and below.
            Assert.AreEqual(27f, ShellSliderMath.CenteredTop(66f, 12f), 1e-3f);
            Assert.AreEqual(0f, ShellSliderMath.CenteredTop(66f, 66f), 1e-3f);
        }

        [Test]
        public void SliderArtAndDragSoundAreInResources()
        {
            var text = Resources.Load<TextAsset>("Data/atlas_regions");
            Assert.IsNotNull(text, "Data/atlas_regions.json is missing");
            var atlas = AtlasRegionTable.Parse(text.text);

            foreach (var region in new[]
                     {
                         "slider_frame", "slider_thumb",
                         "slider_plus", "slider_minus",
                     })
            {
                Assert.IsNotNull(atlas.Get(region), region + " is missing");
            }

            // SliderFrame is a NinePatchRect with 24 px patch margins.
            Assert.AreEqual(
                new Vector4(24f, 24f, 24f, 24f), atlas.Get("slider_frame").border);

            Assert.IsNotNull(
                BlitzResources.LoadAudio("drag_01"), "Audio/drag_01 is missing");
        }

        // ------------------------------------------------------------------
        // The built screen: the three complaints against the source.
        // ------------------------------------------------------------------

        private GameObject _root;
        private AccountData _account;
        private LoadoutScreen _screen;

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
            {
                Object.DestroyImmediate(_root);
                _root = null;
            }
        }

        private LoadoutScreen BuildScreen()
        {
            var kit = new HangarUiKit(
                AtlasRegionTable.Parse(
                    BlitzResources.LoadText(BlitzResources.AtlasRegionsData)),
                LocalizationTable.Parse(
                    BlitzResources.LoadText(BlitzResources.LanguageData)),
                BlitzResources.LoadFont(BlitzResources.RegularFontName),
                BlitzResources.LoadFont(BlitzResources.BoldFontName),
                null);

            var tanks = TanksDatabase.Parse(
                BlitzResources.LoadText(BlitzResources.TanksData));
            _account = new AccountData();
            _account.AddTank("ms-1", true);

            _root = new GameObject("loadout_test_root", typeof(RectTransform));
            var parent = (RectTransform)_root.transform;
            parent.sizeDelta = new Vector2(
                UiLayout.ReferenceWidth, UiLayout.ReferenceHeight);

            _screen = new LoadoutScreen(kit, tanks, _account);
            _screen.Build(parent);
            _screen.Init("ms-1");
            return _screen;
        }

        private static RectTransform Find(LoadoutScreen screen, string path)
        {
            var node = screen.Root.Find(path) as RectTransform;
            Assert.IsNotNull(node, path + " is missing");
            return node;
        }

        private static float CenterY(RectTransform rect)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            return (corners[0].y + corners[2].y) * 0.5f;
        }

        private static Rect World(RectTransform rect)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            return Rect.MinMaxRect(
                corners[0].x, corners[0].y, corners[2].x, corners[2].y);
        }

        [Test]
        public void ShellFill_SitsOnTheMiddleLineOfTheRow()
        {
            var screen = BuildScreen();
            var area = Find(screen, "shells/shell_slider_1/slider");
            var bar = Find(screen, "shells/shell_slider_1/slider/bar");
            var fill = Find(screen, "shells/shell_slider_1/slider/fill");

            Assert.AreEqual(CenterY(area), CenterY(bar), 0.01f);
            Assert.AreEqual(CenterY(area), CenterY(fill), 0.01f);
            Assert.AreEqual(12f, bar.rect.height, 0.01f);
            Assert.AreEqual(12f, fill.rect.height, 0.01f);
        }

        [Test]
        public void ShellRow_IsDraggableAndSnapsToTheBurst()
        {
            var screen = BuildScreen();
            var area = Find(screen, "shells/shell_slider_1/slider");
            var slider = area.GetComponent<UiDragSlider>();
            Assert.IsNotNull(slider, "the grabber area has no drag handler");

            var capacity = slider.MaxValue;
            Assert.Greater(capacity, 0);
            Assert.AreEqual(0, slider.ValueAt(0f));
            Assert.AreEqual(capacity, slider.ValueAt(area.rect.width));
            Assert.AreEqual(
                ShellSliderMath.Snap(capacity * 0.5f, capacity, slider.Step),
                slider.ValueAt(area.rect.width * 0.5f));

            var save = _account.GetTank("ms-1");
            screen.SetShells(0, capacity);

            // The sliders are pending values in the source: only btn_apply
            // writes curtank.shells_amounts.
            Assert.AreEqual(0, save.ShellsAmounts[0], "the rack changed before APPLY");
            screen.ApplyShells();
            Assert.AreEqual(capacity, save.ShellsAmounts[0]);

            // The fill and the grabber follow the value.
            var fill = Find(screen, "shells/shell_slider_1/slider/fill");
            var thumb = Find(screen, "shells/shell_slider_1/slider/grabber");
            Assert.AreEqual(
                ShellSliderMath.FillWidth(
                    area.rect.width, thumb.rect.width, capacity, capacity),
                fill.rect.width,
                0.01f);
            Assert.LessOrEqual(
                World(thumb).xMax, World(area).xMax + 0.01f,
                "the grabber hangs over the end of the row");

            // The rack is full, so the second type cannot take anything.
            screen.SetShells(1, 10);
            Assert.AreEqual(0, save.ShellsAmounts[1]);
        }

        /// <summary>
        /// The source keeps <c>slider.max_value</c> at the rack capacity and
        /// clamps the dragged value with a separate <c>max_capacity</c>. The
        /// port folded both into the pointer scale, so on a full rack a touch
        /// mapped the grabber to a fraction of its own position and the row
        /// walked down on every tap.
        /// </summary>
        [Test]
        public void ShellRow_OnAFullRackKeepsItsCountWhenTouched()
        {
            var screen = BuildScreen();
            var area = Find(screen, "shells/shell_slider_1/slider");
            var thumb = Find(screen, "shells/shell_slider_1/slider/grabber");
            var slider = area.GetComponent<UiDragSlider>();
            var capacity = slider.MaxValue;

            screen.SetShells(0, capacity);
            Assert.AreEqual(
                capacity, slider.MaxValue, "the pointer scale left the capacity");

            // Touching the grabber where the row draws it, twice.
            for (var touch = 0; touch < 2; touch++)
            {
                var grabbed = slider.ValueAt(
                    ShellSliderMath.FillWidth(
                        area.rect.width, thumb.rect.width, capacity, capacity));
                Assert.AreEqual(capacity, grabbed, "a touch moved the row");
                screen.SetShells(0, grabbed);
            }

            screen.ApplyShells();
            Assert.AreEqual(capacity, _account.GetTank("ms-1").ShellsAmounts[0]);

            // Half of a full rack is still reachable in one drag.
            var half = ShellSliderMath.Snap(
                capacity * 0.5f, capacity, slider.Step);
            screen.SetShells(0, slider.ValueAt(area.rect.width * 0.5f));
            screen.ApplyShells();
            Assert.AreEqual(half, _account.GetTank("ms-1").ShellsAmounts[0]);
        }

        [Test]
        public void EquipmentEntry_KeepsItsIconAndItsTapTargetInsideTheFrame()
        {
            var screen = BuildScreen();
            var cell = World(Find(screen, "equipment/item_rammer"));
            var frame = World(Find(screen, "equipment/item_rammer/bg"));
            var icon = World(Find(screen, "equipment/item_rammer/icon"));
            var button = World(Find(screen, "equipment/item_rammer/button"));

            // ui_selectable_list_entry: both TextureRects are KEEP_CENTERED,
            // so the plate is its own 206 px art at 0.635 and the icon is the
            // authored rammer art (132 x 170) at 0.66, never the cell size.
            Assert.AreEqual(160f, cell.width, 0.01f);
            Assert.AreEqual(130.81f, frame.width, 0.05f);
            Assert.AreEqual(130.81f, frame.height, 0.05f);
            Assert.AreEqual(132f * 0.66f, icon.width, 0.05f);
            Assert.AreEqual(170f * 0.66f, icon.height, 0.05f);
            Assert.Less(icon.width, frame.width, "the icon is wider than the plate");
            Assert.AreEqual(cell.center.x, icon.center.x, 0.01f);
            Assert.AreEqual(cell.center.y, icon.center.y, 0.01f);
            Assert.AreEqual(cell.center.x, frame.center.x, 0.01f);
            Assert.AreEqual(cell.center.y, frame.center.y, 0.01f);

            // The tap target covers the whole cell, not just the plate.
            Assert.AreEqual(cell.width, button.width, 0.01f);
            Assert.AreEqual(cell.height, button.height, 0.01f);
            Assert.AreEqual(cell.center.x, button.center.x, 0.01f);
            Assert.AreEqual(cell.center.y, button.center.y, 0.01f);

            // Two neighbours in the same grid row never share a pixel.
            var next = World(Find(screen, "equipment/item_vent/button"));
            Assert.IsFalse(button.Overlaps(next), "two entries overlap");

            // Neither does an icon of the row below.
            var below = World(Find(screen, "equipment/item_spallliner/icon"));
            Assert.IsFalse(icon.Overlaps(below), "two icon rows overlap");
        }

        [Test]
        public void EquipmentEntry_PriceStaysAboveTheNextRow()
        {
            var screen = BuildScreen();
            screen.Refresh();

            var label = Find(screen, "equipment/item_rammer/cost");
            var text = label.GetComponent<Text>();
            Assert.IsNotNull(text, "the entry has no price label");
            Assert.AreEqual(TextAnchor.UpperRight, text.alignment);
            Assert.IsNotEmpty(text.text, "the price is hidden");

            var cost = World(label);
            var coin = World(Find(screen, "equipment/item_rammer/credits"));
            var below = World(Find(screen, "equipment/item_spallliner/icon"));

            // The label box is taller than the printed line, so only the
            // first line counts: the price never prints over the icon of the
            // entry one row down.
            var printed = cost.yMax - text.preferredHeight;
            Assert.Greater(
                printed, below.center.y, "the price prints over the next row");
            Assert.Greater(
                coin.yMin, below.center.y, "the coin sits on the next row");
        }

        [Test]
        public void EmptySlot_DrawsNoIconAtAll()
        {
            var screen = BuildScreen();

            // init(name, desc, null, ...): an empty slot keeps the
            // CheckBoxEquipPower frame and nothing else.
            var icon = Find(screen, "equipment/slot1/icon").GetComponent<Image>();
            Assert.IsNotNull(icon);
            Assert.IsNull(icon.sprite, "an empty slot draws an icon");
            Assert.IsFalse(icon.enabled, "an empty slot still renders its icon");

            var frame = Find(screen, "equipment/slot1/bg").GetComponent<Image>();
            Assert.IsNotNull(frame.sprite, "the slot lost its frame");

            // The same holds for the crew page.
            var crew = Find(screen, "consumables/slot1/icon").GetComponent<Image>();
            Assert.IsNull(crew.sprite, "an empty crew slot draws an icon");
        }

        [Test]
        public void SlotIcon_KeepsTheAuthoredArtworkSize()
        {
            var screen = BuildScreen();
            _account.Credits = AccountData.DefaultCredits;
            screen.ChooseSlot(0);
            screen.ChooseItem("rammer");
            screen.ActionPressed();

            var slot = World(Find(screen, "equipment/slot1"));
            var icon = World(Find(screen, "equipment/slot1/icon"));

            // The slots run the same entry at scale 0.75 (120 px), so the
            // KEEP_CENTERED artwork is 132 x 170 * 0.66 * 120/160.
            Assert.AreEqual(120f, slot.width, 0.01f);
            Assert.AreEqual(132f * 0.66f * 0.75f, icon.width, 0.05f);
            Assert.AreEqual(170f * 0.66f * 0.75f, icon.height, 0.05f);
            Assert.AreEqual(slot.center.x, icon.center.x, 0.01f);
            Assert.AreEqual(slot.center.y, icon.center.y, 0.01f);
        }

        [Test]
        public void InstallingAModule_MovesTheCardToTheNextItem()
        {
            var screen = BuildScreen();
            _account.Credits = AccountData.DefaultCredits;
            screen.ChooseSlot(0);
            screen.ChooseItem("rammer");
            Assert.AreEqual("rammer", screen.SelectedItem);

            screen.ActionPressed();

            // update() -> select_slot_int(last_free_slot): the card leaves the
            // module that was just installed and the next free slot is armed.
            Assert.AreEqual(1, screen.SelectedSlot);
            Assert.AreNotEqual(
                "rammer", screen.SelectedItem, "the card kept the installed module");
            Assert.IsNotEmpty(screen.SelectedItem, "the card went blank");
        }

        [Test]
        public void AmmoRow_ShowsThePriceAndCommitsOnApply()
        {
            var screen = BuildScreen();

            var caption = Find(screen, "shells/shell_slider_1/l_price")
                .GetComponent<Text>();
            var price = Find(screen, "shells/shell_slider_1/price_text")
                .GetComponent<Text>();
            Assert.IsNotEmpty(caption.text, "the price caption is missing");
            Assert.IsNotEmpty(price.text, "the shell price is missing");

            var apply = Find(screen, "shells/btn_apply").GetComponent<Button>();
            Assert.IsNotNull(apply, "the ammo page has no APPLY button");
            Assert.IsFalse(apply.interactable, "APPLY is live without a change");

            screen.SetShells(0, 10);
            Assert.IsTrue(apply.interactable, "APPLY stayed disabled after a change");

            var save = _account.GetTank("ms-1");
            Assert.AreEqual(0, save.ShellsAmounts[0]);
            screen.ApplyShells();
            Assert.AreEqual(10, save.ShellsAmounts[0]);
            Assert.IsFalse(apply.interactable, "APPLY stayed live after the commit");
        }
    }
}
