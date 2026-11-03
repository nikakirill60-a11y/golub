using System.Collections.Generic;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The Godot Button draw states, from assets/fonts/ui_theme_blitz.tres and
    /// scripts/ui_elements/tab_container_custom.gd: a pressed StyleBox while
    /// the button is held, and the same StyleBox for as long as a toggle_mode
    /// button stays pressed.
    /// </summary>
    public sealed class GodotButtonStateTests
    {
        private GameObject _root;

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
            {
                Object.DestroyImmediate(_root);
            }
        }

        private Button NewButton(out Image image, out Text caption)
        {
            if (_root == null)
            {
                _root = new GameObject("root", typeof(RectTransform));
            }

            var button = UiFactory.TextButton(
                "btn", _root.transform, null, Resources.GetBuiltinResource<Font>(
                    "LegacyRuntime.ttf"), 16, "CAPTION");
            image = button.GetComponent<Image>();
            image.color = Color.white;
            caption = button.transform.Find("label").GetComponent<Text>();
            return button;
        }

        [Test]
        public void HoldingAButtonDrawsThePressedStyle()
        {
            Image image;
            Text caption;
            var button = NewButton(out image, out caption);
            var state = GodotButtonState.Of(button);
            Assert.IsNotNull(state, "every ported button carries the state");

            var data = new PointerEventData(null);
            state.OnPointerDown(data);
            Assert.IsTrue(state.DrawsPressed);
            Assert.AreEqual(UiTheme.ButtonPressTint, image.color);
            Assert.AreEqual(UiTheme.ButtonFontPressed, caption.color);

            state.OnPointerUp(data);
            Assert.IsFalse(state.DrawsPressed);
            Assert.AreEqual(Color.white, image.color);
            Assert.AreEqual(UiTheme.ButtonFont, caption.color);
        }

        [Test]
        public void AnAuthoredPressedSpriteWinsOverTheDefaultDarkening()
        {
            Image image;
            Text caption;
            var button = NewButton(out image, out caption);
            var pressed = Sprite.Create(
                new Texture2D(4, 4), new Rect(0f, 0f, 4f, 4f), Vector2.zero);
            var state = GodotButtonState.Of(button);
            state.SetPressedStyle(pressed, UiTheme.ButtonPressTint);

            state.OnPointerDown(new PointerEventData(null));
            Assert.AreSame(pressed, image.sprite);
            Assert.AreEqual(UiTheme.ButtonPressTint, image.color);
        }

        [Test]
        public void AToggleButtonKeepsThePressedStyleAfterTheClick()
        {
            Image image;
            Text caption;
            var button = NewButton(out image, out caption);
            var state = GodotButtonState.Of(button);
            state.ToggleMode = true;
            state.Latched = true;

            var data = new PointerEventData(null);
            state.OnPointerDown(data);
            state.OnPointerUp(data);
            Assert.IsTrue(state.DrawsPressed, "the open screen stays marked");
            Assert.AreEqual(UiTheme.ButtonPressTint, image.color);

            state.Latched = false;
            Assert.AreEqual(Color.white, image.color);
        }

        [Test]
        public void SelectPressesOnlyTheButtonOfTheOpenTab()
        {
            var buttons = new List<Button>();
            var images = new List<Image>();
            for (var i = 0; i < 3; i++)
            {
                Image image;
                Text caption;
                buttons.Add(NewButton(out image, out caption));
                images.Add(image);
            }

            GodotButtonState.Select(buttons, 1);
            Assert.AreEqual(Color.white, images[0].color);
            Assert.AreEqual(UiTheme.ButtonPressTint, images[1].color);
            Assert.AreEqual(Color.white, images[2].color);

            GodotButtonState.Select(buttons, 2);
            Assert.AreEqual(Color.white, images[1].color);
            Assert.AreEqual(UiTheme.ButtonPressTint, images[2].color);
        }
    }
}
