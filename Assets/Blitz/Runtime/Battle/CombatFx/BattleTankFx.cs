using UnityEngine;

namespace BlitzLegacy.Runtime.Battle.CombatFx
{
    /// <summary>
    /// The shot half of <c>tank_particles_manager.gd</c>: one
    /// <c>shoot_*.tscn</c> chosen by <c>gun_info.shot_sound</c>, instanced
    /// under <c>tank.gun</c> at <c>gun_shoot_point.position</c> and
    /// restarted on every <c>projectile_fired</c> unless the tank is an
    /// unspotted enemy. The effect object is not parented into the tank
    /// (spotting and the scope toggle switch the tank's renderers), so it
    /// re-reads the gun every frame instead.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BattleTankFx : MonoBehaviour
    {
        private BattleTank _tank;

        /// <summary>The persistent shot effect, or null if the gun has none.</summary>
        public GodotParticleEffect Shot { get; private set; }

        /// <summary>Attaches the shot effect to a tank once its turret is rigged.</summary>
        public static BattleTankFx Attach(BattleTank tank)
        {
            if (tank == null || tank.GunInfo == null || !GodotParticleEffect.Enabled)
            {
                return null;
            }

            var manifest = CombatFxManifest.Shared;
            var scene = manifest != null ? manifest.ShotScene(tank.GunInfo.ShotSound) : null;
            if (scene == null)
            {
                return null;
            }

            var fx = tank.GetComponent<BattleTankFx>();
            if (fx == null)
            {
                fx = tank.gameObject.AddComponent<BattleTankFx>();
            }

            fx.Bind(tank, scene);
            return fx;
        }

        private void Bind(BattleTank tank, GodotEffectData scene)
        {
            if (_tank != null)
            {
                _tank.ProjectileFired -= OnProjectileFired;
            }

            if (Shot != null)
            {
                Destroy(Shot.gameObject);
            }

            _tank = tank;
            Shot = GodotParticleEffect.Create(scene, "shot_effect_" + tank.name);
            var basis = scene.Root;
            basis.Origin = Vector3.zero;
            Shot.RootProvider = () => GunMuzzle() * basis;
            Shot.Concealed = () => _tank == null || _tank.ConcealedFromPlayer;
            Shot.Root = GunMuzzle() * basis;
            _tank.ProjectileFired += OnProjectileFired;
        }

        /// <summary><c>tank.gun</c>'s global basis at <c>gun_shoot_point</c>, in Godot space.</summary>
        private GodotXform GunMuzzle()
        {
            var turret = _tank != null ? _tank.Turret : null;
            if (turret == null || turret.GunPivot == null)
            {
                return _tank != null
                    ? GodotXform.FromUnity(_tank.transform.localToWorldMatrix)
                    : GodotXform.Identity;
            }

            var gun = turret.GunPivot;
            var point = turret.Muzzle != null ? turret.Muzzle.position : gun.position;
            return GodotXform.FromUnity(Matrix4x4.TRS(point, gun.rotation, gun.lossyScale));
        }

        private void OnProjectileFired(bool first, int slot)
        {
            if (Shot == null || _tank == null || _tank.ConcealedFromPlayer)
            {
                return;
            }

            // The 11.20 rocket keeps its own launch presentation.
            var shell = _tank.CurrentShell();
            if (shell != null && shell.Atgm)
            {
                return;
            }

            Shot.Root = GunMuzzle() * ShotBasis();
            Shot.Play(false);
        }

        private GodotXform ShotBasis()
        {
            var basis = Shot.Data.Root;
            basis.Origin = Vector3.zero;
            return basis;
        }

        private void OnDestroy()
        {
            if (_tank != null)
            {
                _tank.ProjectileFired -= OnProjectileFired;
            }

            if (Shot != null)
            {
                Destroy(Shot.gameObject);
            }
        }
    }
}
