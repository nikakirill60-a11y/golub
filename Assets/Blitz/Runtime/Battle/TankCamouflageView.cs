using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Assets;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>The ported camouflage catalog and its textures, loaded once per run.</summary>
    public static class CamouflageAssets
    {
        public const string ShaderName = "BlitzLegacy/TankCamouflageLit";

        /// <summary>Pinned TankCamouflageLit twins of the MS1 battle materials (variant stripping).</summary>
        public const string BodyMaterialResource = "Materials/MS1BattleBodyCamouflage";
        public const string CutoutMaterialResource = "Materials/MS1BattleCutoutCamouflage";

        private static readonly Dictionary<string, Texture2D> Textures =
            new Dictionary<string, Texture2D>(StringComparer.Ordinal);

        private static CamouflageCatalog _catalog;

        public static CamouflageCatalog Catalog
        {
            get
            {
                if (_catalog == null) { _catalog = Load(); }
                return _catalog;
            }
        }

        private static CamouflageCatalog Load()
        {
            var text = BlitzResources.LoadText(CamouflageCatalog.DataResource);
            if (string.IsNullOrEmpty(text))
            {
                Debug.LogError("[Blitz] missing camouflage data: Data/" + CamouflageCatalog.DataResource);
                return CamouflageCatalog.Empty;
            }

            try
            {
                return CamouflageCatalog.Parse(text);
            }
            catch (InvalidOperationException error)
            {
                Debug.LogError("[Blitz] unreadable camouflage data: " + error.Message);
                return CamouflageCatalog.Empty;
            }
        }

        /// <summary>A mask or pattern texture by its Resources path, or null.</summary>
        public static Texture2D Texture(string resource)
        {
            if (string.IsNullOrEmpty(resource)) { return null; }
            Texture2D texture;
            if (Textures.TryGetValue(resource, out texture) && texture != null) { return texture; }
            texture = Resources.Load<Texture2D>(resource);
            if (texture == null)
            {
                Debug.LogWarning("[Blitz] missing camouflage texture: " + resource);
                return null;
            }

            Textures[resource] = texture;
            return texture;
        }

        /// <summary>
        /// The camouflage to paint on a tank in battle under the Camouflage
        /// graphics setting: everyone's, only the player's own, or none.
        /// </summary>
        public static bool Painted(int setting, bool own)
        {
            return setting == Core.Save.SettingsData.CamouflageForAll
                || (setting == Core.Save.SettingsData.CamouflageForPlayer && own);
        }
    }

    /// <summary>
    /// The camouflage of one tank model. Each hull, turret and gun material
    /// whose albedo has a mask gets a TankCamouflageLit twin, and the
    /// renderers swap between the plain materials and the twins. The tracks
    /// and any part without a mask keep their plain material, and so does
    /// every material another system put on a renderer since (outlines, the
    /// burnt sheet of a wreck).
    /// </summary>
    public sealed class TankCamouflageView
    {
        private static readonly int CamoMask = Shader.PropertyToID("_CamoMask");
        private static readonly int CamoTex = Shader.PropertyToID("_CamoTex");
        private static readonly int CamoColor = Shader.PropertyToID("_CamoColor");

        private readonly string _tank;
        private readonly List<Renderer> _renderers = new List<Renderer>();
        private readonly Dictionary<Material, string> _albedos = new Dictionary<Material, string>();
        private readonly Dictionary<Material, Material> _twins = new Dictionary<Material, Material>();
        private readonly Dictionary<Material, Material> _plains = new Dictionary<Material, Material>();
        private string _shown = string.Empty;

        private TankCamouflageView(string tank)
        {
            _tank = tank ?? string.Empty;
        }

        /// <summary>The camouflage the model currently wears, empty when plain.</summary>
        public string Shown
        {
            get { return _shown; }
        }

        /// <summary>
        /// Reads which albedo every tank material of a freshly painted model
        /// shows: a node's own sheet where the node table binds one, the body
        /// sheet otherwise. <paramref name="plainOnly"/> lists the track
        /// materials; outline passes are not drawn with the tank shader and
        /// are left alone as well.
        /// </summary>
        public static TankCamouflageView Build(
            GameObject model, TankModelAssets assets, TankNodeTable nodes, string tank,
            params Material[] plainOnly)
        {
            var view = new TankCamouflageView(tank);
            if (model == null || assets == null) { return view; }
            var renderers = model.GetComponentsInChildren<MeshRenderer>(true);
            for (var i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                var objectName = TankModuleView.TrimCloneSuffix(renderer.gameObject.name);
                if (objectName == "trackL" || objectName == "trackR" || objectName == "tracks_crash")
                {
                    continue;
                }

                var albedo = nodes != null ? nodes.AlbedoOf(objectName) : null;
                if (string.IsNullOrEmpty(albedo)) { albedo = assets.Id; }
                var materials = renderer.sharedMaterials;
                var paintable = false;
                for (var m = 0; m < materials.Length; m++)
                {
                    var material = materials[m];
                    if (material == null || material.shader == null
                        || material.shader.name != BattleTank.TankShaderName
                        || Array.IndexOf(plainOnly, material) >= 0)
                    {
                        continue;
                    }

                    if (!view._albedos.ContainsKey(material)) { view._albedos[material] = albedo; }
                    paintable = true;
                }

                if (paintable) { view._renderers.Add(renderer); }
            }

            return view;
        }

        /// <summary>
        /// Paints the camouflage, or the plain sheets for an empty or unknown
        /// id. Returns how many materials wear it.
        /// </summary>
        public int Show(string camouflage)
        {
            var catalog = CamouflageAssets.Catalog;
            var tank = catalog.Tank(_tank);
            var paint = tank != null && catalog.Get(camouflage) != null ? tank.PaintOf(camouflage) : null;
            var pattern = paint != null ? CamouflageAssets.Texture(paint.Texture) : null;
            if (pattern == null) { paint = null; }
            _shown = paint != null ? camouflage : string.Empty;

            var painted = 0;
            for (var r = 0; r < _renderers.Count; r++)
            {
                var renderer = _renderers[r];
                if (renderer == null) { continue; }
                var materials = renderer.sharedMaterials;
                var changed = false;
                for (var m = 0; m < materials.Length; m++)
                {
                    var current = materials[m];
                    if (current == null) { continue; }
                    Material plain;
                    if (!_plains.TryGetValue(current, out plain)) { plain = current; }
                    if (!_albedos.ContainsKey(plain)) { continue; }
                    var next = paint != null ? Twin(plain, tank, paint, pattern) : null;
                    if (next == null) { next = plain; } else { painted++; }
                    if (next != current)
                    {
                        materials[m] = next;
                        changed = true;
                    }
                }

                if (changed) { renderer.sharedMaterials = materials; }
            }

            return painted;
        }

        /// <summary>Destroys the twins; the plain materials belong to the model's owner.</summary>
        public void Release()
        {
            foreach (var twin in _twins.Values)
            {
                if (twin == null) { continue; }
                if (Application.isPlaying) { UnityEngine.Object.Destroy(twin); }
                else { UnityEngine.Object.DestroyImmediate(twin); }
            }

            _twins.Clear();
            _plains.Clear();
        }

        private Material Twin(Material plain, TankCamouflage tank, CamouflagePaint paint, Texture2D pattern)
        {
            string albedo;
            if (!_albedos.TryGetValue(plain, out albedo)) { return null; }
            var mask = CamouflageAssets.Texture(tank.MaskOf(albedo));
            if (mask == null) { return null; }

            Material twin;
            if (!_twins.TryGetValue(plain, out twin) || twin == null)
            {
                twin = CreateTwin(plain);
                _twins[plain] = twin;
                _plains[twin] = plain;
            }

            twin.SetTexture(CamoMask, mask);
            twin.SetTexture(CamoTex, pattern);
            twin.SetTextureScale(CamoTex, new Vector2(paint.ScaleU, paint.ScaleV));
            twin.SetTextureOffset(CamoTex, UnityOffset(paint));
            twin.SetColor(CamoColor, new Color(1f, 1f, 1f, paint.Alpha));
            return twin;
        }

        /// <summary>
        /// decalTileCoordOffset in Unity's texture space: the source V runs
        /// top-down, so v' = 1 - ((1 - v) * scale + offset).
        /// </summary>
        public static Vector2 UnityOffset(CamouflagePaint paint)
        {
            return new Vector2(paint.OffsetU, 1f - paint.ScaleV - paint.OffsetV);
        }

        /// <summary>
        /// The TankCamouflageLit twin of a tank material: the same sheet and
        /// render state, cloned from a pinned material for the same reason
        /// <see cref="BattleTank.CreateTankMaterial"/> clones one.
        /// </summary>
        private static Material CreateTwin(Material plain)
        {
            var alphaClip = plain.IsKeywordEnabled("_ALPHATEST_ON");
            var resource = alphaClip
                ? CamouflageAssets.CutoutMaterialResource
                : CamouflageAssets.BodyMaterialResource;
            var template = Resources.Load<Material>(resource);
            Material twin;
            if (template != null)
            {
                twin = new Material(template);
            }
            else
            {
                Debug.LogWarning("[Blitz] missing pinned camouflage material: " + resource);
                var shader = Shader.Find(CamouflageAssets.ShaderName);
                twin = new Material(shader != null ? shader : plain.shader);
            }

            twin.name = plain.name + "Camouflage";
            BattleTank.ConfigureTankMaterial(twin, plain.GetTexture("_BaseMap") as Texture2D, alphaClip);
            if (plain.HasProperty("_Cutoff") && twin.HasProperty("_Cutoff"))
            {
                twin.SetFloat("_Cutoff", plain.GetFloat("_Cutoff"));
            }

            if (plain.HasProperty("_Cull") && twin.HasProperty("_Cull"))
            {
                twin.SetFloat("_Cull", plain.GetFloat("_Cull"));
            }

            return twin;
        }
    }
}
