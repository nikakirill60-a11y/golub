using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Battle;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Tests
{
    public sealed class BattleChatLayoutTests
    {
        [Test]
        public void ChatButtonSitsRightOfMenuWithoutOverlap()
        {
            Assert.Greater(ControlsTuning.MenuOffsetLeft, ControlsTuning.HeaderRightEdge);
            Assert.Less(ControlsTuning.MenuOffsetLeft, 232f, "menu moved left of the authored spot");
            Assert.GreaterOrEqual(ControlsTuning.ChatOffsetLeft, ControlsTuning.MenuOffsetRight);
            Assert.AreEqual(ControlsTuning.MenuOffsetTop, ControlsTuning.ChatOffsetTop);
            Assert.AreEqual(ControlsTuning.MenuOffsetBottom, ControlsTuning.ChatOffsetBottom);
            Assert.Greater(ControlsTuning.ChatWidth, 0f);
        }

        [Test]
        public void FeedStaysSmallOnTheMobileCanvas()
        {
            // 1040x550 authored battle canvas: the 3 lines stay near the top,
            // the input strip takes well under a quarter of the height.
            Assert.Less(BattleChatView.FeedTop + ChatFeed.MaxVisible * BattleChatView.RowHeight, 550f * 0.25f);
            Assert.Less(BattleChatView.InputHeight, 550f * 0.25f);
            Assert.LessOrEqual(BattleChatView.MaxRowWidth, 1040f * 0.55f);
            Assert.GreaterOrEqual(BattleChatView.FeedTop, 36f, "below the clock and team counters");
            Assert.Greater(BattleChatView.FontSize, 15, "lines grew from the first version");
        }

        [Test]
        public void InputStripIsBottomEdgeToEdgeWithSquareClickableButtons()
        {
            var go = new GameObject("hud", typeof(RectTransform));
            try
            {
                var hud = go.AddComponent<Canvas>();
                hud.renderMode = RenderMode.ScreenSpaceOverlay;
                hud.sortingOrder = BattleHud.SortingOrder;
                var view = BattleChatView.Create((RectTransform)go.transform, null, null, false);
                Assert.IsNotNull(view);
                var strip = view.InputRoot;
                Assert.AreEqual(Vector2.zero, strip.anchorMin);
                Assert.AreEqual(new Vector2(1f, 0f), strip.anchorMax);
                Assert.AreEqual(0f, strip.sizeDelta.x, "edge to edge");
                Assert.AreEqual(BattleChatView.InputHeight, strip.sizeDelta.y);

                // Own canvas + raycaster above the controls touch surface.
                Assert.IsNotNull(view.InputCanvas);
                Assert.IsTrue(view.InputCanvas.overrideSorting);
                Assert.Greater(view.InputCanvas.sortingOrder, BattleHud.SortingOrder + 1);
                Assert.IsNotNull(strip.GetComponent<GraphicRaycaster>());

                foreach (var button in new[] { view.TeamButton, view.AllButton })
                {
                    Assert.IsNotNull(button);
                    var rt = (RectTransform)button.transform;
                    Assert.AreEqual(BattleChatView.ChannelButtonSize,
                        rt.rect.width, 0.01f, "square: width");
                    Assert.AreEqual(BattleChatView.ChannelButtonSize,
                        BattleChatView.InputHeight + rt.sizeDelta.y, 0.01f, "square: height");
                    Assert.IsTrue(button.targetGraphic.raycastTarget);
                }
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
