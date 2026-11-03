using BlitzLegacy.Core.Tanks;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// One armour plate of <c>add_armor_from_mesh</c>: the source hides the
    /// plate mesh, hangs a trimesh collider off it, parks it on the armour or
    /// screen collision layer and writes the thickness into node metadata.
    ///
    /// Godot keeps those bodies off every collision mask, so they never push
    /// anything around and only shell traces ever notice them. Unity spells
    /// that as a cleared row of the collision matrix, because a concave mesh
    /// collider cannot be a trigger, and this component is the metadata a shell
    /// reads back off the plate it hit.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BattleArmorPlate : MonoBehaviour
    {
        /// <summary>The tank this plate belongs to.</summary>
        public BattleTank Tank { get; private set; }

        /// <summary>Mesh name the porter exported, such as <c>_armor_13</c>.</summary>
        public string PlateName { get; private set; }

        /// <summary>Nominal thickness in millimetres, the source's <c>armor</c> meta.</summary>
        public int Armor { get; private set; }

        /// <summary>
        /// The part this plate rides: the hull, or the client slot of the
        /// installed turret or gun it was exported for.
        /// </summary>
        public string Mount { get; private set; }

        /// <summary>Spaced armour, which lives on its own layer in the source.</summary>
        public bool Screen { get; private set; }

        /// <summary>
        /// The tank module this plate stands for, empty for plain armour.
        /// <c>init_modules</c> tags the track collider <c>module = "tracks"</c>
        /// and the shell code reads it back off the body it hit.
        /// </summary>
        public string Module { get; private set; }

        /// <summary>Submesh the collider was built from.</summary>
        public int Submesh { get; private set; }

        /// <summary>The collider a shell trace hits.</summary>
        public MeshCollider Shape { get; private set; }

        /// <summary>
        /// <c>screen_use_nominal</c>: a screen that charges its nominal
        /// thickness instead of the angled one. Nothing on the MS-1 sets it,
        /// but the shell code reads it, so plates carry the flag.
        /// </summary>
        public bool UseNominalArmor { get; private set; }

        /// <summary>The overlay renderer, hidden until the scope asks for it.</summary>
        public MeshRenderer Preview { get; private set; }

        /// <summary>The material the overlay shader reads its shell out of.</summary>
        public Material PreviewMaterial { get; private set; }

        private Vector3[] _aimSamples;
        /// <summary>Decision probes cached per plate, avoiding mesh-array copies
        /// on every bot decision; lifetime is the collision plate's own.</summary>
        public Vector3[] AimSamples
        {
            get
            {
                if (_aimSamples != null) return _aimSamples;
                if (Shape == null || Shape.sharedMesh == null) return System.Array.Empty<Vector3>();
                var vertices=Shape.sharedMesh.vertices;var triangles=Shape.sharedMesh.triangles;
                var step=Mathf.Max(3,(triangles.Length/12/3)*3);
                var points=new System.Collections.Generic.List<Vector3>();
                for(var i=0;i+2<triangles.Length;i+=step)
                    points.Add((vertices[triangles[i]]+vertices[triangles[i+1]]+vertices[triangles[i+2]])/3f);
                return _aimSamples=points.ToArray();
            }
        }

        private Rigidbody _body;
        private Transform _mount;

        /// <summary>The authored hull, turret or gun transform this body follows.</summary>
        public Transform FollowMount { get { return _mount; } }
        private Vector3 _mountLocalPosition;
        private Quaternion _mountLocalRotation;

        /// <summary>
        /// Unity owns a kinematic body's world pose and does not reliably carry
        /// it with a dynamic Rigidbody parent. Keep the exact authored rest
        /// transform relative to the hull/turret/gun mount and drive both the
        /// render Transform and physics body explicitly.
        /// </summary>
        private void FixedUpdate()
        {
            if (_body == null || _mount == null) { return; }
            var position = _mount.TransformPoint(_mountLocalPosition);
            var rotation = _mount.rotation * _mountLocalRotation;
            // A body already sent to this pose is there: moving a kinematic
            // body onto its own pose changes nothing but costs a physics write.
            if (_bodyPlaced && position == _bodyPosition && rotation == _bodyRotation) { return; }
            _body.MovePosition(position);
            _body.MoveRotation(rotation);
            PlaceBody(position, rotation);
        }

        private bool _bodyPlaced;
        private Vector3 _bodyPosition;
        private Quaternion _bodyRotation;

        private void PlaceBody(Vector3 position, Quaternion rotation)
        {
            _bodyPlaced = true;
            _bodyPosition = position;
            _bodyRotation = rotation;
        }

        private void LateUpdate()
        {
            if (_body == null || _mount == null) { return; }
            // A plate whose mount has not moved since the last sync is already
            // in place; re-writing the same pose would only make physics
            // re-sync the body (parked, dead and respawning hulls).
            var position = _mount.TransformPoint(_mountLocalPosition);
            var rotation = _mount.rotation * _mountLocalRotation;
            if (_synced && position == _syncedPosition && rotation == _syncedRotation
                && transform.position == position)
            {
                return;
            }

            SyncToMount();
        }

        private bool _synced;
        private Vector3 _syncedPosition;
        private Quaternion _syncedRotation;

        /// <summary>Synchronises the collision and preview pose immediately.</summary>
        public void SyncToMount()
        {
            if (_body == null || _mount == null) { return; }
            var position = _mount.TransformPoint(_mountLocalPosition);
            var rotation = _mount.rotation * _mountLocalRotation;
            // Move the Transform as well as the physics body. Rigidbody.position
            // is not reflected into render transforms immediately in edit mode
            // and can leave the scope overlay one frame at its old height.
            transform.SetPositionAndRotation(position, rotation);
            _body.position = position;
            _body.rotation = rotation;
            PlaceBody(position, rotation);
            _synced = true;
            _syncedPosition = position;
            _syncedRotation = rotation;
        }

        /// <summary>
        /// <c>add_armor_from_mesh</c> gives every plate its own duplicate of
        /// the armour shader with this plate's thickness baked in, and
        /// <c>mdl.hide()</c> keeps it off screen until the scope wants it.
        /// </summary>
        internal void AttachPreview(MeshRenderer renderer, Material material)
        {
            Preview = renderer;
            PreviewMaterial = material;
            if (material != null)
            {
                material.SetFloat(ArmorPreviewShader.NominalArmor, Armor);
            }

            if (renderer != null) { renderer.enabled = false; }
        }

        /// <summary>
        /// The per-plate half of <c>preview_armor</c>: show or hide the
        /// overlay, and hand the shader the shell the player has loaded.
        /// </summary>
        public void ShowPreview(bool on, ShellInfo shell)
        {
            if (Preview != null) { Preview.enabled = on; }
            if (PreviewMaterial == null || shell == null) { return; }

            PreviewMaterial.SetFloat(ArmorPreviewShader.Caliber, shell.Caliber);
            PreviewMaterial.SetFloat(ArmorPreviewShader.Penetration, shell.Penetration);
            PreviewMaterial.SetFloat(
                ArmorPreviewShader.He, shell.Type == ShellType.He ? 1f : 0f);
            PreviewMaterial.SetFloat(
                ArmorPreviewShader.Heat, shell.Type == ShellType.Heat ? 1f : 0f);

            // set_shader_parameter("normalization", deg_to_rad(get_normalization())).
            PreviewMaterial.SetFloat(
                ArmorPreviewShader.Normalization,
                ArmorMath.Normalization(shell.Type) * Mathf.Deg2Rad);
        }

        internal void Bind(
            BattleTank tank,
            TankArmorTable.Plate plate,
            MeshCollider shape,
            Transform mount)
        {
            Tank = tank;
            PlateName = plate.Name;
            Armor = plate.Armor;
            Mount = plate.Mount;
            Screen = plate.Screen;
            Module = plate.Module;
            // setup_model: the track module body is the one screen the source
            // marks screen_use_nominal, so it always charges its plain
            // thickness no matter how the shell came in.
            UseNominalArmor = plate.IsModule;
            Submesh = plate.Submesh;
            Shape = shape;
            _body = GetComponent<Rigidbody>();
            _mount = mount != null ? mount : tank.transform;
            _mountLocalPosition = _mount.InverseTransformPoint(transform.position);
            _mountLocalRotation = Quaternion.Inverse(_mount.rotation) * transform.rotation;
            SyncToMount();
        }
    }
}
