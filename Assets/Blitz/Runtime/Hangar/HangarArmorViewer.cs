using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Tanks;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Battle;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlitzLegacy.Runtime.Hangar
{
    /// <summary>
    /// Native hangar armour inspector. It rebuilds the same exported plates the
    /// battle uses, clips them to the visible tank stencil and exposes a real
    /// MeshCollider ray test for the penetration tool.
    /// </summary>
    public sealed class HangarArmorViewer : MonoBehaviour
    {
        /// <summary>The solid fill pass the viewer builds its plates with.</summary>
        public const string FillShaderName = "BlitzLegacy/Hangar/Armor Fill";

        public enum ViewMode { Sight, Fill, Penetration }

        public struct HitInfo
        {
            public string Plate;
            public int NominalArmor;
            public float EffectiveArmor;
            public float AngleDegrees;
            public HitOutcome Outcome;
            public bool Screen;
        }

        private sealed class PlateView
        {
            public TankArmorTable.Plate Data;
            public GameObject Host;
            public MeshRenderer Renderer;
            public MeshCollider Collider;
            public Material Sight;
            public Material Fill;
        }

        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private readonly List<PlateView> _plates = new List<PlateView>();
        private readonly List<Material> _materials = new List<Material>();
        private readonly Dictionary<MeshRenderer, Material[]> _originalMaterials =
            new Dictionary<MeshRenderer, Material[]>();
        private readonly HashSet<string> _moduleNodes = new HashSet<string>();

        private HangarWorld _world;
        private GameObject _plateRoot;
        private Material _maskMaterial;
        private GameObject _marker;
        private PlateView _selected;
        private ViewMode _mode;
        private ShellType _shellType = ShellType.Ap;
        private int _caliber = 100;
        private float _penetration = 175f;

        public int PlateCount { get { return _plates.Count; } }
        public ViewMode Mode { get { return _mode; } }
        public int Caliber { get { return _caliber; } }
        public float Penetration { get { return _penetration; } }
        public ShellType ShellType { get { return _shellType; } }

        public static HangarArmorViewer Create(HangarWorld world)
        {
            if (world == null) { return null; }
            var viewer = world.GetComponent<HangarArmorViewer>();
            if (viewer == null) { viewer = world.gameObject.AddComponent<HangarArmorViewer>(); }
            viewer._world = world;
            return viewer;
        }

        public bool Open(TankInfo card, TankSaveData save)
        {
            Close();
            if (_world == null || card == null) { return false; }

            // The viewer used to refuse the whole vehicle when the hangar had
            // not built its model yet, which is what reported the armour as
            // unavailable on the cards the player opens straight after a
            // selection change. Building it here is the same call the hangar
            // makes, so the answer no longer depends on the order of screens.
            if (_world.TankModel == null && !_world.ShowTank(card)) { return false; }
            if (_world.TankModel == null) { return false; }

            var gun = card.GunFor(save == null ? 0 : save.TurretId, save == null ? 0 : save.GunId);
            if (gun != null && gun.Shells.Count > 0)
            {
                var shell = gun.Shells[0];
                _shellType = shell.Type;
                _caliber = Mathf.Max(1, shell.Caliber);
                _penetration = Mathf.Max(0f, shell.Penetration);
            }

            // The stamp has to be taken from the visible tank alone. Building
            // the plates first put them under TankModel, so the sweep below
            // handed every plate its own stencil stamp and each one then
            // passed its own clip test: the overlay was free to float wherever
            // the low-poly shell happened to stick out.
            AttachStencilMask();
            if (!BuildPlates(card, save)) { Close(); return false; }
            SetMode(ViewMode.Sight);
            return true;
        }

        public void Close()
        {
            RestoreVisibleMaterials();
            if (_marker != null) { Destroy(_marker); }
            _marker = null;
            _selected = null;
            if (_plateRoot != null) { Destroy(_plateRoot); }
            _plateRoot = null;
            _plates.Clear();
            _moduleNodes.Clear();
            for (var i = 0; i < _materials.Count; i++)
            {
                if (_materials[i] != null) { Destroy(_materials[i]); }
            }
            _materials.Clear();
            _maskMaterial = null;
        }

        public void SetMode(ViewMode mode)
        {
            _mode = mode;
            _selected = null;
            if (_marker != null) { Destroy(_marker); }
            _marker = null;

            // The scope view is the tank with the overlay painted on it. The
            // armour map and the penetration test are the collision shell
            // itself, so the tank steps aside and the plates sort against one
            // another with real depth.
            ShowVisibleTank(mode == ViewMode.Sight);
            for (var i = 0; i < _plates.Count; i++)
            {
                var plate = _plates[i];
                plate.Renderer.sharedMaterial = mode == ViewMode.Sight ? plate.Sight : plate.Fill;
                plate.Renderer.enabled = true;
                RefreshFill(plate, false, default(HitOutcome));
            }
        }

        public void SetShell(ShellType type, int caliber, float penetration)
        {
            _shellType = type;
            _caliber = Mathf.Clamp(caliber, 1, 400);
            _penetration = Mathf.Clamp(penetration, 0f, 1000f);
            for (var i = 0; i < _plates.Count; i++) { ApplyShell(_plates[i]); }
        }

        public bool TryShoot(Vector2 screenPosition, out HitInfo info)
        {
            info = default(HitInfo);
            if (_mode != ViewMode.Penetration || _world == null || _world.HangarCamera == null)
            {
                return false;
            }

            var ray = _world.HangarCamera.ScreenPointToRay(screenPosition);
            var hits = Physics.RaycastAll(ray, 200f, ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            for (var i = 0; i < hits.Length; i++)
            {
                var plate = Find(hits[i].collider);
                if (plate == null) { continue; }

                var angle = ArmorMath.ImpactAngle(Vector3.Dot(hits[i].normal, ray.direction));
                var effective = ArmorMath.Overmatches(
                    _shellType, _caliber, plate.Data.Armor)
                    ? 0f
                    : ArmorMath.EffectiveArmor(
                        _shellType, _caliber, plate.Data.Armor, angle);
                var outcome = HitResolver.ResolveArmor(
                    _shellType, _caliber, _penetration,
                    plate.Data.Armor, angle, false);

                info = new HitInfo
                {
                    Plate = plate.Data.Name,
                    NominalArmor = plate.Data.Armor,
                    EffectiveArmor = effective,
                    AngleDegrees = angle * Mathf.Rad2Deg,
                    Outcome = outcome,
                    Screen = plate.Data.Screen,
                };
                Select(plate, hits[i].point, hits[i].normal, outcome);
                return true;
            }

            return false;
        }

        public static Color ArmorColor(int armor, float alpha = 0.62f)
        {
            var t = Mathf.Clamp01(armor / 300f);
            Color color;
            if (t < 0.5f)
            {
                color = Color.Lerp(new Color(0.08f, 0.82f, 0.18f), new Color(1f, 0.78f, 0.05f), t * 2f);
            }
            else
            {
                color = Color.Lerp(new Color(1f, 0.78f, 0.05f), new Color(0.92f, 0.06f, 0.03f), (t - 0.5f) * 2f);
            }
            color.a = alpha;
            return color;
        }

        public static Color OutcomeColor(HitOutcome outcome)
        {
            switch (outcome)
            {
                case HitOutcome.Penetration: return new Color(0.12f, 1f, 0.2f, 0.92f);
                case HitOutcome.Overmatch: return new Color(0.15f, 0.75f, 1f, 0.92f);
                case HitOutcome.Ricochet: return new Color(1f, 0.72f, 0.05f, 0.92f);
                default: return new Color(1f, 0.08f, 0.04f, 0.92f);
            }
        }

        /// <summary>
        /// How far each plate escapes the visible part it is bolted to, in
        /// metres. A collision shell models the mesh it rides, so it lives
        /// inside it; anything much above zero is a mount placed in the wrong
        /// space, which is what let the overlay float over the turret.
        /// </summary>
        public IList<string> Audit(out float worstLeak)
        {
            var lines = new List<string>();
            worstLeak = 0f;
            for (var i = 0; i < _plates.Count; i++)
            {
                var plate = _plates[i];
                var host = MountBounds(plate.Data.Mount);
                if (!host.HasValue) { continue; }
                var leak = Escape(plate.Renderer.bounds, host.Value);
                if (leak > worstLeak) { worstLeak = leak; }
                lines.Add(plate.Data.Name + " mount=" + plate.Data.Mount +
                    " leak=" + leak.ToString("F3") +
                    " plate=" + plate.Renderer.bounds.center.ToString("F3") +
                    plate.Renderer.bounds.size.ToString("F3") +
                    " visible=" + host.Value.center.ToString("F3") +
                    host.Value.size.ToString("F3"));
            }

            return lines;
        }

        /// <summary>The world box of the visible mesh one mount draws.</summary>
        private Bounds? MountBounds(string mount)
        {
            if (_world == null || _world.TankModel == null) { return null; }
            var renderers = _world.TankModel.GetComponentsInChildren<MeshRenderer>(false);
            Bounds? total = null;
            for (var i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (_plateRoot != null &&
                    renderer.transform.IsChildOf(_plateRoot.transform)) { continue; }
                var name = TankModuleView.TrimCloneSuffix(renderer.gameObject.name);
                var isMount = mount == TankArmorTable.HullMount
                    ? !_moduleNodes.Contains(name)
                    : name == mount;
                if (!isMount) { continue; }
                if (total.HasValue)
                {
                    var grown = total.Value;
                    grown.Encapsulate(renderer.bounds);
                    total = grown;
                }
                else
                {
                    total = renderer.bounds;
                }
            }

            return total;
        }

        /// <summary>The largest distance by which one box leaves another.</summary>
        private static float Escape(Bounds inner, Bounds outer)
        {
            var low = outer.min - inner.min;
            var high = inner.max - outer.max;
            return Mathf.Max(0f, Mathf.Max(
                Mathf.Max(low.x, Mathf.Max(low.y, low.z)),
                Mathf.Max(high.x, Mathf.Max(high.y, high.z))));
        }

        private bool BuildPlates(TankInfo card, TankSaveData save)
        {
            var assets = TankModelAssets.For(card);
            var tableAsset = TankAssetSource.ArmorAsset(assets);
            var prefab = TankAssetSource.ArmorModel(assets);
            var filter = prefab != null ? prefab.GetComponentInChildren<MeshFilter>(true) : null;
            var source = filter != null ? filter.sharedMesh : null;
            var nodeAsset = TankAssetSource.NodesAsset(assets);
            if (tableAsset == null || source == null || nodeAsset == null) { return false; }

            var table = TankArmorTable.Parse(tableAsset.text);
            var nodes = TankNodeTable.Parse(nodeAsset.text);
            string turretMesh;
            string gunMesh;
            TankLoadoutMeshes.Resolve(card, save, out turretMesh, out gunMesh);
            var turret = nodes.ResolveTurret(turretMesh);
            var gun = nodes.ResolveGun(turret, gunMesh);
            var fillShader = Shader.Find(FillShaderName);
            var sightShader = ArmorPreviewShader.Load();
            // The fill pass is a nicety; a player build that stripped it used
            // to lose the whole viewer, so fall back to the sight material.
            if (fillShader == null) { fillShader = sightShader; }
            if (fillShader == null || sightShader == null) { return false; }

            _moduleNodes.Clear();
            var turretNodes = nodes.TurretNodes;
            for (var i = 0; i < turretNodes.Count; i++)
            {
                _moduleNodes.Add(turretNodes[i]);
                var guns = nodes.GunsOf(turretNodes[i]);
                for (var g = 0; g < guns.Count; g++) { _moduleNodes.Add(guns[g]); }
            }

            _plateRoot = new GameObject("HangarArmor");
            _plateRoot.transform.SetParent(_world.TankModel.transform, false);

            for (var i = 0; i < table.Count; i++)
            {
                var data = table.Plates[i];
                if (data.Module == TankModuleIds.Tracks && card.Tracks.Count > 0)
                {
                    var track = card.Tracks[Mathf.Clamp(save != null ? save.TrackId : 0, 0, card.Tracks.Count - 1)];
                    if (track.Armor > 0) { data.Armor = track.Armor; }
                }
                if (!string.IsNullOrEmpty(data.RequiredTurret) && data.RequiredTurret != turret) { continue; }
                if (data.Mount != TankArmorTable.HullMount &&
                    data.Mount != turret && data.Mount != gun) { continue; }
                var mesh = PlateMesh(source, data);
                if (mesh == null) { continue; }

                var host = new GameObject(data.Name);
                host.transform.SetParent(_plateRoot.transform, false);
                // ModelWorld, not World: the hangar draws the imported model
                // as it is, so the mount origin takes only the importer's X
                // flip, not the battle mirror.
                var correction = ToVector(table.ModelOffsetOf(data.Mount));
                host.transform.localPosition = correction +
                    (data.Mount == TankArmorTable.HullMount
                        ? Vector3.zero
                        : ToVector(nodes.ModelWorld(data.Mount)));
                var meshFilter = host.AddComponent<MeshFilter>();
                meshFilter.sharedMesh = mesh;
                var renderer = host.AddComponent<MeshRenderer>();
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                var collider = host.AddComponent<MeshCollider>();
                collider.sharedMesh = mesh;

                var sight = ArmorPreviewShader.NewMaterial(sightShader);
                // The scope overlay fights the visible hull for depth, which
                // is why it only ever showed up as slivers here. The hangar
                // draws it straight over the tank and lets the stencil keep it
                // inside the silhouette, so it also needs no extrusion.
                sight.SetFloat(ArmorPreviewShader.ZTest, (float)CompareFunction.Always);
                sight.SetFloat(ArmorPreviewShader.Extrude, 0f);
                var fill = new Material(fillShader) { name = "armor_fill_" + data.Armor };
                _materials.Add(sight);
                _materials.Add(fill);
                var view = new PlateView
                {
                    Data = data,
                    Host = host,
                    Renderer = renderer,
                    Collider = collider,
                    Sight = sight,
                    Fill = fill,
                };
                sight.SetFloat(ArmorPreviewShader.NominalArmor, data.Armor);
                ApplyShell(view);
                RefreshFill(view, false, default(HitOutcome));
                _plates.Add(view);
            }

            return _plates.Count > 0;
        }

        private void ApplyShell(PlateView plate)
        {
            if (plate == null || plate.Sight == null) { return; }
            plate.Sight.SetFloat(ArmorPreviewShader.Caliber, _caliber);
            plate.Sight.SetFloat(ArmorPreviewShader.Penetration, _penetration);
            plate.Sight.SetFloat(ArmorPreviewShader.Normalization,
                ArmorMath.Normalization(_shellType) * Mathf.Deg2Rad);
            plate.Sight.SetFloat(ArmorPreviewShader.He,
                _shellType == ShellType.He || _shellType == ShellType.Hesh ? 1f : 0f);
            plate.Sight.SetFloat(ArmorPreviewShader.Heat, _shellType == ShellType.Heat ? 1f : 0f);
        }

        private void RefreshFill(PlateView plate, bool selected, HitOutcome outcome)
        {
            if (plate == null || plate.Fill == null) { return; }
            plate.Fill.SetColor(ColorId,
                selected ? OutcomeColor(outcome) : ArmorColor(plate.Data.Armor, 1f));
        }

        private void Select(PlateView plate, Vector3 point, Vector3 normal, HitOutcome outcome)
        {
            if (_selected != null) { RefreshFill(_selected, false, default(HitOutcome)); }
            _selected = plate;
            RefreshFill(plate, true, outcome);
            if (_marker != null) { Destroy(_marker); }
            _marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _marker.name = "ArmorImpact";
            _marker.transform.position = point + normal * 0.025f;
            _marker.transform.localScale = Vector3.one * 0.09f;
            var markerCollider = _marker.GetComponent<Collider>();
            if (markerCollider != null) { Destroy(markerCollider); }
            var material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            material.name = "armor_impact";
            material.SetColor("_BaseColor", OutcomeColor(outcome));
            _marker.GetComponent<MeshRenderer>().sharedMaterial = material;
            _materials.Add(material);
        }

        private PlateView Find(Collider collider)
        {
            for (var i = 0; i < _plates.Count; i++)
            {
                if (_plates[i].Collider == collider) { return _plates[i]; }
            }
            return null;
        }

        private void AttachStencilMask()
        {
            var shader = TankOutlineShader.LoadMask();
            if (shader == null || _world.TankModel == null) { return; }
            _maskMaterial = TankOutlineShader.NewMaskMaterial(shader);
            var color = TankOutlineShader.EnemyColor;
            color.a = 1f;
            _maskMaterial.SetColor(TankOutlineShader.Color, color);
            _materials.Add(_maskMaterial);
            var renderers = _world.TankModel.GetComponentsInChildren<MeshRenderer>(true);
            for (var i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                // Never stamp the overlay itself, whatever the build order.
                if (_plateRoot != null &&
                    renderer.transform.IsChildOf(_plateRoot.transform)) { continue; }
                var original = renderer.sharedMaterials;
                _originalMaterials[renderer] = original;
                var grown = new Material[original.Length + 1];
                Array.Copy(original, grown, original.Length);
                grown[original.Length] = _maskMaterial;
                renderer.sharedMaterials = grown;
            }
        }

        /// <summary>Shows or hides the tank's own meshes, plates excluded.</summary>
        private void ShowVisibleTank(bool shown)
        {
            foreach (var pair in _originalMaterials)
            {
                if (pair.Key != null) { pair.Key.enabled = shown; }
            }
        }

        private void RestoreVisibleMaterials()
        {
            foreach (var pair in _originalMaterials)
            {
                if (pair.Key == null) { continue; }
                pair.Key.sharedMaterials = pair.Value;
                pair.Key.enabled = true;
            }
            _originalMaterials.Clear();
        }

        private static Mesh PlateMesh(Mesh source, TankArmorTable.Plate plate)
        {
            if (source == null || plate.Submesh < 0 || plate.Submesh >= source.subMeshCount)
            {
                return null;
            }
            var sourceTriangles = source.GetTriangles(plate.Submesh);
            var sourceVertices = source.vertices;
            var remap = new Dictionary<int, int>();
            var vertices = new List<Vector3>();
            var triangles = new int[sourceTriangles.Length];
            for (var i = 0; i < sourceTriangles.Length; i++)
            {
                int index;
                if (!remap.TryGetValue(sourceTriangles[i], out index))
                {
                    index = vertices.Count;
                    remap[sourceTriangles[i]] = index;
                    vertices.Add(sourceVertices[sourceTriangles[i]]);
                }
                triangles[i] = index;
            }
            var mesh = new Mesh { name = plate.Name };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Vector3 ToVector(Vec3 value)
        {
            return new Vector3(value.X, value.Y, value.Z);
        }

        private void OnDestroy()
        {
            Close();
        }
    }
}
