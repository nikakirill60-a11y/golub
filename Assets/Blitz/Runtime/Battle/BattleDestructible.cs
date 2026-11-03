using BlitzLegacy.Core.Maps;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// Port of <c>map_object_destructible.gd</c>: the intact and wreck visuals,
    /// projectile health, collider state and the force a prop gives a tank that
    /// drives through its physical-body area.
    ///
    /// The source keeps both states in the scene at once. The port keeps one
    /// renderer and one shell collider and swaps what they point at, which has
    /// the same result without doubling every destructible renderer.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BattleDestructible : MonoBehaviour
    {
        /// <summary><c>destructible_on_tank_enter</c>'s shared coefficient.</summary>
        public const float RamCoefficient = 0.1f;

        private MeshFilter _filter;
        private MeshRenderer _renderer;
        private MeshCollider _collider;
        private Mesh _wreckMesh;
        private Mesh _wreckCollision;

        /// <summary>
        /// The source bakes the intact mesh and leaves the wreck out of the
        /// atlas, so a broken prop drops back to the realtime material of its
        /// own family instead of sampling a rect that does not describe it.
        /// </summary>
        private Material _wreckMaterial;
        private Material[] _wreckMaterials;
        private Renderer[] _sceneIntact;
        private Renderer[] _sceneWreck;

        /// <summary>The name the source scene gave this prop.</summary>
        public string ObjectName { get; private set; }

        /// <summary>map_object_destructible.gd health: 10 unless overridden.</summary>
        public int Health { get; private set; }

        /// <summary>An index into map_object_destructible.gd's Effect enum.</summary>
        public int Effect { get; private set; }

        public bool Destroyed { get; private set; }

        internal void Bind(
            MapObjectInstance instance,
            MeshFilter filter,
            MeshRenderer renderer,
            MeshCollider collider,
            Mesh wreckMesh,
            Mesh wreckCollision,
            Material wreckMaterial)
        {
            ObjectName = instance.Name;
            Health = instance.Health;
            Effect = instance.Effect;
            _filter = filter;
            _renderer = renderer;
            _collider = collider;
            _wreckMesh = wreckMesh;
            _wreckCollision = wreckCollision;
            _wreckMaterial = wreckMaterial;
            _wreckMaterials = wreckMaterial != null
                ? new[] { wreckMaterial }
                : null;
        }

        /// <summary>
        /// Binds an imported client-scene prop. The intact/wreck meshes already
        /// include the authored world transform; the wreck collider therefore
        /// reuses the same mesh as its visual state.
        /// </summary>
        internal void BindExternal(
            string objectName,
            int health,
            MeshFilter filter,
            MeshRenderer renderer,
            MeshCollider collider,
            Mesh wreckMesh,
            Material[] wreckMaterials)
        {
            ObjectName = objectName;
            Health = Mathf.Max(health, 1);
            Effect = 0;
            _filter = filter;
            _renderer = renderer;
            _collider = collider;
            _wreckMesh = wreckMesh;
            _wreckCollision = wreckMesh;
            _wreckMaterial = null;
            _wreckMaterials = wreckMaterials != null && wreckMaterials.Length > 0
                ? wreckMaterials
                : null;
        }

        /// <summary>
        /// Subtracts damage the way the source does and returns true only for
        /// the hit that finished the prop.
        /// </summary>
        /// <summary>
        /// A Godot render scene draws the prop as two nodes, as
        /// map_object_destructible.gd keeps them: breaking hides
        /// mesh_instance_normal and shows mesh_instance_destroyed.
        /// </summary>
        internal void BindSceneVisuals(Renderer[] intact, Renderer[] wreck)
        {
            _sceneIntact = intact;
            _sceneWreck = wreck;
        }

        public bool Damage(int amount)
        {
            if (Destroyed || amount <= 0)
            {
                return false;
            }

            Health -= amount;
            if (Health > 0)
            {
                return false;
            }

            Health = 0;
            Break();
            return true;
        }

        /// <summary>
        /// <c>destructible_on_tank_enter</c>: the first physical body entering
        /// the source Area3D breaks the prop immediately, then receives one
        /// force and one torque. Later contacts are no-ops.
        /// </summary>
        public bool Ram(BattleTank tank)
        {
            if (Destroyed || tank == null)
            {
                return false;
            }

            var body = tank.GetComponent<Rigidbody>();
            if (body == null)
            {
                return false;
            }

            var healthBefore = Health;
            Break();

            // Imported 4.8.0 map meshes are world-baked into their OBJ groups,
            // so the GameObject origin is not the prop's physical center.
            var propCenter = _collider != null
                ? _collider.bounds.center
                : transform.position;
            var toTank = (tank.transform.position - propCenter).normalized;
            var impulse = RamImpulse(healthBefore, body.mass);
            body.AddTorque(RamTorque(toTank, impulse), ForceMode.Force);
            body.AddForce(RamForce(toTank, impulse), ForceMode.Force);
            return true;
        }

        /// <summary>The source's deliberately mass-dominated ram response.</summary>
        public static float RamImpulse(int health, float tankMass)
        {
            if (tankMass <= 0f)
            {
                return 0f;
            }

            return Mathf.Max(
                health * RamCoefficient / tankMass * RamCoefficient,
                tankMass);
        }

        public static Vector3 RamForce(Vector3 toTank, float impulse)
        {
            return toTank * impulse * RamCoefficient;
        }

        public static Vector3 RamTorque(Vector3 toTank, float impulse)
        {
            return Vector3.Cross(toTank, -Vector3.up) *
                impulse * RamCoefficient * 0.5f;
        }

        private void Break()
        {
            Destroyed = true;
            if (_sceneIntact != null)
            {
                for (var i = 0; i < _sceneIntact.Length; i++) _sceneIntact[i].enabled = false;
                for (var i = 0; i < _sceneWreck.Length; i++) _sceneWreck[i].enabled = true;
            }

            if (_filter != null)
            {
                _filter.sharedMesh = _wreckMesh;
            }

            if (_renderer != null)
            {
                if (_wreckMaterials != null)
                {
                    _renderer.sharedMaterials = _wreckMaterials;
                }
                else if (_wreckMaterial != null)
                {
                    _renderer.sharedMaterial = _wreckMaterial;
                }
                else if (_wreckMesh == null)
                {
                    // A 4.8.0 prop with health and a collision type but no
                    // client-authored wreck mesh is removed when its explicit
                    // gameplay health reaches zero.
                    _renderer.enabled = false;
                }

                _renderer.SetPropertyBlock(null);
            }

            if (_collider == null)
            {
                return;
            }

            if (_wreckCollision == null)
            {
                // The source disables both static bodies when the wreck has no
                // authored shape. Shells and tanks pass through after the swap.
                _collider.enabled = false;
                return;
            }

            // A solid wreck moves from the destructible layer to the ordinary
            // static layer in map_object_destructible.gd.
            _collider.sharedMesh = _wreckCollision;
            gameObject.layer = BattleLayers.Static;
        }
    }

    /// <summary>
    /// The source uses the intact shape twice: a layer-9 StaticBody3D for shell
    /// traces and a layerless Area3D that watches only physical tank bodies.
    /// Unity needs the same second collider because its collision matrix is not
    /// directional. It lives on the Physics layer, so shell raycasts ignore it.
    /// </summary>
    public sealed class BattleDestructibleTrigger : MonoBehaviour
    {
        private BattleDestructible _destructible;

        public void Bind(BattleDestructible destructible)
        {
            _destructible = destructible;
        }

        private void OnTriggerEnter(Collider other)
        {
            if (_destructible == null || other == null)
            {
                return;
            }

            var tank = other.GetComponentInParent<BattleTank>();
            if (tank != null)
            {
                _destructible.Ram(tank);
            }
        }
    }
}
