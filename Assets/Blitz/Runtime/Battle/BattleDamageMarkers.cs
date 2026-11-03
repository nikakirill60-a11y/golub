using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Core.Ui;
using BlitzLegacy.Runtime.Ui;
using UnityEngine;
using UnityEngine.UI;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// The hit direction markers of the player HUD: the <c>markers</c> node of
    /// ui_player.tscn with copies of <c>marker_template_dmg</c>, driven by
    /// <c>add_dmg_marker</c> / <c>update_markers</c> of ui_player_battle.gd
    /// and <c>ui_damage_indicator.gd</c>.
    ///
    /// Every shell, explosion or ability hit on the player tank leaves a soft
    /// arc round the screen centre that points at the attacker - red when it
    /// went through the armour, grey when it was blocked. It follows the
    /// attacker for five seconds, fades out over one more, and goes as soon as
    /// the same attacker damages the player again (the new hit brings its own
    /// marker).
    /// </summary>
    [DefaultExecutionOrder(3)]
    public sealed class BattleDamageMarkers : MonoBehaviour
    {
        private sealed class Marker
        {
            public RectTransform Node;
            public Image Head;
            public BattleTank Attacker;
            public Vector3 HitPosition;
            public float Born;
            public float[] Tint;
        }

        private readonly List<Marker> _markers = new List<Marker>();
        private RectTransform _handle;
        private Sprite _sprite;
        private BattleTank _tank;
        private Func<Camera> _camera;
        private HealthComponent _health;
        private bool _combatVisible = true;

        /// <summary>Live markers.</summary>
        public int Count { get { return _markers.Count; } }

        /// <summary>The <c>markers</c> Control.</summary>
        public RectTransform Handle { get { return _handle; } }

        public static BattleDamageMarkers Create(
            RectTransform parent, Sprite sprite, BattleTank tank, Func<Camera> camera)
        {
            var handle = UiFactory.Node("markers", parent);
            GodotRect.Centered(
                handle,
                -DamageMarkerMath.HandleHalfSize, -DamageMarkerMath.HandleHalfSize,
                DamageMarkerMath.HandleHalfSize, DamageMarkerMath.HandleHalfSize);
            var markers = handle.gameObject.AddComponent<BattleDamageMarkers>();
            markers._handle = handle;
            markers._sprite = sprite;
            markers._camera = camera;
            markers.Bind(tank);
            return markers;
        }

        private void Bind(BattleTank tank)
        {
            _tank = tank;
            if (_tank == null)
            {
                return;
            }

            // health_component.on_damage_info_taken on the simulating peer and
            // the host's hit feedback on a network client.
            _health = _tank.Health;
            if (_health != null) { _health.OnDamageInfoTaken += OnDamageInfo; }
            _tank.NetDamageFeedback += OnDamageInfo;
        }

        private void OnDestroy()
        {
            if (_health != null) { _health.OnDamageInfoTaken -= OnDamageInfo; }
            if (_tank != null) { _tank.NetDamageFeedback -= OnDamageInfo; }
        }

        private void OnDamageInfo(DamageInfo info)
        {
            Receive(info, Time.time);
        }

        /// <summary>
        /// One damage record taken by the player tank. Markers of earlier hits
        /// by the same attacker go first when this one is not a block
        /// (<c>on_enemy_damage</c> reaches only indicators connected before
        /// this hit), then <c>_on_player_damage_info</c> may add a new one.
        /// </summary>
        public void Receive(DamageInfo info, float now)
        {
            if (info == null)
            {
                return;
            }

            var attacker = info.AttackerTank as BattleTank;
            if (attacker != null && !info.IsBlock())
            {
                RemoveFrom(attacker, now);
            }

            if (!_combatVisible || _tank == null || _tank.Health == null || _tank.Health.Dead)
            {
                return;
            }

            if (attacker == null || !info.ShouldHaveMarker())
            {
                return;
            }

            Add(attacker, !info.IsBlock(), now);
        }

        /// <summary><c>add_dmg_marker</c>.</summary>
        private void Add(BattleTank attacker, bool penetrated, float now)
        {
            var node = UiFactory.Node("marker_dmg", _handle);
            GodotRect.Centered(node, 0f, 0f, 0f, 0f);

            var head = UiFactory.Picture("head", node, _sprite);
            GodotRect.Apply(
                head.rectTransform, 0.5f, 0.5f, 0.5f, 0.5f,
                DamageMarkerMath.HeadLeft, DamageMarkerMath.HeadTop,
                DamageMarkerMath.HeadRight, DamageMarkerMath.HeadBottom,
                new Vector2(
                    DamageMarkerMath.HeadPivotX
                        / (DamageMarkerMath.HeadRight - DamageMarkerMath.HeadLeft),
                    1f - DamageMarkerMath.HeadPivotY
                        / (DamageMarkerMath.HeadBottom - DamageMarkerMath.HeadTop)));
            head.rectTransform.localRotation = Quaternion.Euler(
                0f, 0f, DamageMarkerMath.UnityZDegrees(DamageMarkerMath.HeadRotation));
            head.rectTransform.localScale = new Vector3(
                DamageMarkerMath.HeadScale, DamageMarkerMath.HeadScale, 1f);

            var marker = new Marker
            {
                Node = node,
                Head = head,
                Attacker = attacker,
                HitPosition = attacker.Pos(),
                Born = now,
                Tint = DamageMarkerMath.Tint(penetrated),
            };
            _markers.Add(marker);
            Refresh(marker, now, _camera != null ? _camera() : null);
        }

        private void RemoveFrom(BattleTank attacker, float now)
        {
            for (var i = _markers.Count - 1; i >= 0; i--)
            {
                var marker = _markers[i];
                if (marker.Attacker == attacker && marker.Born < now)
                {
                    Release(i);
                }
            }
        }

        private void LateUpdate()
        {
            Tick(Time.time);
        }

        /// <summary>The lifetime timers, the fade tweens and <c>update_markers</c>.</summary>
        public void Tick(float now)
        {
            var camera = _camera != null ? _camera() : null;
            for (var i = _markers.Count - 1; i >= 0; i--)
            {
                var marker = _markers[i];
                if (marker.Node == null || DamageMarkerMath.Expired(now - marker.Born))
                {
                    Release(i);
                    continue;
                }

                Refresh(marker, now, camera);
            }
        }

        private void Refresh(Marker marker, float now, Camera camera)
        {
            var tint = marker.Tint;
            marker.Head.color = new Color(
                tint[0], tint[1], tint[2],
                tint[3] * DamageMarkerMath.Fade(now - marker.Born));

            // An attacker that is gone leaves its marker where it was.
            if (camera == null || marker.Attacker == null)
            {
                return;
            }

            // A hit the player dealt to itself keeps the spot it came from.
            var position = marker.Attacker == _tank ? marker.HitPosition : marker.Attacker.Pos();
            var view = camera.transform;
            var rotation = DamageMarkerMath.RotationFromMirrored(
                ToVec(view.position), ToVec(position),
                ToVec(view.right), ToVec(view.up), ToVec(view.forward));
            marker.Node.localRotation = Quaternion.Euler(
                0f, 0f, DamageMarkerMath.UnityZDegrees(rotation));
        }

        /// <summary>The marker's current rotation, as a Godot Control angle in radians.</summary>
        public float RotationOf(int index)
        {
            var z = _markers[index].Node.localEulerAngles.z;
            if (z > 180f) { z -= 360f; }

            return -z * Mathf.Deg2Rad;
        }

        /// <summary>The tint the marker is drawn with right now.</summary>
        public Color ColorOf(int index)
        {
            return _markers[index].Head.color;
        }

        public BattleTank AttackerOf(int index)
        {
            return _markers[index].Attacker;
        }

        public RectTransform HeadOf(int index)
        {
            return _markers[index].Head.rectTransform;
        }

        /// <summary>
        /// <c>set_combat_hud_visible</c>: hiding the combat HUD frees every
        /// marker, and <c>add_dmg_marker</c> refuses new ones until it is back.
        /// </summary>
        public void SetCombatVisible(bool on)
        {
            _combatVisible = on;
            if (!on)
            {
                Clear();
            }
        }

        public void Clear()
        {
            for (var i = _markers.Count - 1; i >= 0; i--)
            {
                Release(i);
            }
        }

        private void Release(int index)
        {
            var node = _markers[index].Node;
            _markers.RemoveAt(index);
            if (node == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(node.gameObject);
            }
            else
            {
                DestroyImmediate(node.gameObject);
            }
        }

        private static Vec3 ToVec(Vector3 v)
        {
            return new Vec3(v.x, v.y, v.z);
        }
    }
}
