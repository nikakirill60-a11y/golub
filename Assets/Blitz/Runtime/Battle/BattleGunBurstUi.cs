using System.Collections.Generic;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Ui;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// Port of <c>assets/gfx/ui/ui_gun_burst.tscn</c>: the fan of magazine
    /// rounds under the crosshair. Every round is a copy of the scene's
    /// <c>prefab</c> TextureRect, rotated around the same point one pixel below
    /// the screen centre, painted red to green while it is loaded and left
    /// white once it has been fired.
    ///
    /// The source instances this scene from <c>GunComponentBurst.setup</c> into
    /// <c>%UI_player</c>; here the HUD builds it and feeds it from
    /// <see cref="BattleGunBurst.BurstsChanged"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BattleGunBurstUi : MonoBehaviour
    {
        private readonly List<Image> _bursts = new List<Image>();
        private Sprite _loaded;
        private Sprite _empty;
        private RectTransform _root;

        /// <summary><c>bursts_init</c>.</summary>
        public int BurstsInit { get; private set; } = 1;

        /// <summary><c>angle_step</c>.</summary>
        public float AngleStep { get; private set; } = BurstUiTuning.AngleStep;

        /// <summary><c>burst_rects.size()</c>.</summary>
        public int IconCount { get { return _bursts.Count; } }

        public RectTransform Root { get { return _root; } }

        /// <summary><c>burst_rects[index]</c>.</summary>
        public Image Icon(int index)
        {
            return index >= 0 && index < _bursts.Count ? _bursts[index] : null;
        }

        public static BattleGunBurstUi Create(
            Transform parent, Sprite loaded, Sprite empty)
        {
            var root = UiFactory.Node("ui_gun_burst", parent);
            GodotRect.Centered(root, 0f, 0f, 0f, 0f);

            var ui = root.gameObject.AddComponent<BattleGunBurstUi>();
            ui._root = root;
            ui._loaded = loaded;
            ui._empty = empty;
            return ui;
        }

        public void SetVisible(bool on)
        {
            if (_root != null) { _root.gameObject.SetActive(on); }
        }

        public bool Visible
        {
            get { return _root != null && _root.gameObject.activeSelf; }
        }

        /// <summary><c>setup(bursts)</c>.</summary>
        public void Setup(int bursts)
        {
            BurstsInit = bursts;

            for (var i = 0; i < _bursts.Count; i++)
            {
                if (_bursts[i] != null)
                {
                    DestroyIcon(_bursts[i].gameObject);
                }
            }

            _bursts.Clear();

            for (var i = 0; i < bursts; i++)
            {
                // push_front: the array runs from the left edge of the fan.
                _bursts.Insert(0, BulletIcon(i));
            }

            UpdateBurstsPos();
            UpdateBursts(bursts);
        }

        /// <summary><c>sniper_mode(on)</c>.</summary>
        public void SniperMode(bool on)
        {
            AngleStep = on
                ? BurstUiTuning.AngleStepSniper
                : BurstUiTuning.AngleStep;
            UpdateBurstsPos();
        }

        /// <summary><c>update_bursts_pos()</c>.</summary>
        public void UpdateBurstsPos()
        {
            for (var i = 0; i < _bursts.Count; i++)
            {
                var icon = _bursts[i];
                if (icon == null) { continue; }

                var angle = BurstUiTuning.Rotation(i, BurstsInit, AngleStep);
                // Godot UI angles run clockwise, uGUI rotations counter.
                icon.rectTransform.localRotation =
                    Quaternion.Euler(0f, 0f, -angle * Mathf.Rad2Deg);
            }
        }

        /// <summary><c>update_bursts(new_bursts)</c>.</summary>
        public void UpdateBursts(int newBursts)
        {
            for (var i = 0; i < _bursts.Count; i++)
            {
                var icon = _bursts[i];
                if (icon == null) { continue; }

                if (BurstUiTuning.Loaded(i, newBursts))
                {
                    float red;
                    float green;
                    float blue;
                    BurstUiTuning.Tint(newBursts, BurstsInit, out red, out green, out blue);
                    icon.sprite = _loaded;
                    icon.color = new Color(red, green, blue, 1f);
                }
                else
                {
                    icon.sprite = _empty;
                    icon.color = Color.white;
                }
            }
        }

        /// <summary><c>bullet_icon()</c>: a copy of the scene's prefab.</summary>
        private Image BulletIcon(int index)
        {
            var icon = UiFactory.Picture(
                "burst_" + index.ToString("00"), _root, _loaded);
            var rt = icon.rectTransform;
            var width = BurstUiTuning.PrefabRight - BurstUiTuning.PrefabLeft;
            var height = BurstUiTuning.PrefabBottom - BurstUiTuning.PrefabTop;
            var pivot = new Vector2(
                BurstUiTuning.PrefabPivotX / width,
                1f - BurstUiTuning.PrefabPivotY / height);
            GodotRect.Apply(
                rt, 0.5f, 0.5f, 0.5f, 0.5f,
                BurstUiTuning.PrefabLeft,
                BurstUiTuning.PrefabTop,
                BurstUiTuning.PrefabRight,
                BurstUiTuning.PrefabBottom,
                pivot);
            rt.localScale = new Vector3(
                BurstUiTuning.PrefabScale, BurstUiTuning.PrefabScale, 1f);
            return icon;
        }

        private static void DestroyIcon(GameObject go)
        {
            if (Application.isPlaying)
            {
                Destroy(go);
            }
            else
            {
                DestroyImmediate(go);
            }
        }
    }
}
