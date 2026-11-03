using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Hangar;
using BlitzLegacy.Core.Maps;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.Runtime.Tanks;
using BlitzLegacy.Runtime.Battle;
using BlitzLegacy.Runtime.Hangar.Halloween;
using BlitzLegacy.Runtime.Lighting;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace BlitzLegacy.Runtime.Hangar
{
    /// <summary>
    /// The hangar stand and its switchable environment: the source-backed
    /// 4.8 hangars (regular and premium) and the ported 11.x hangars
    /// (<see cref="PortedHangar"/>): the Halloween 2026 hangar
    /// <see cref="HangarSchedule"/> puts on during its event and the Miku
    /// hangar the settings offer.
    /// </summary>
    public sealed class HangarWorld : MonoBehaviour
    {
        public const string TankResource = "Models/Tanks/MS1/MS1";
        public const string SmokeShaderName = "Universal Render Pipeline/Particles/Unlit";

        private readonly List<Material> _materials = new List<Material>();
        private TankCamouflageView _camouflage;
        private TankInfo _tankCard;
        private TankModelAssets _tankAssets;
        private string _turretMesh;
        private string _gunMesh;

        public GameObject HangarModel { get; private set; }
        public GameObject TankModel { get; private set; }

        /// <summary>The camouflage the vehicle on the stand wears, empty when plain.</summary>
        public string ShownCamouflage
        {
            get { return _camouflage != null ? _camouflage.Shown : string.Empty; }
        }
        public Camera HangarCamera { get; private set; }
        public HangarOrbitController Orbit { get; private set; }
        public int SourceLightCount { get; private set; }
        public int SmokeEmitterCount { get; private set; }

        /// <summary>Legacy bake marker; always null in the new client path.</summary>
        public BakedLighting Lighting { get; private set; }

        /// <summary>Original client render batches, retained for log compatibility.</summary>
        public int BakedInstanceCount { get; private set; }

        /// <summary>
        /// <paramref name="tankSave"/> names the researched pair to mount. The
        /// stand resolves it itself instead of waiting for the screen, so a
        /// headless stage shows the same turret and gun the player installed.
        /// <paramref name="kind"/> picks the environment; without it
        /// <paramref name="premium"/> chooses between the two 4.8 hangars.
        /// </summary>
        public static HangarWorld Create(
            Transform parent,
            SettingsData settings,
            TankInfo tankCard = null,
            TankSaveData tankSave = null,
            bool premium = false,
            HangarKind? kind = null)
        {
            var go = new GameObject("HangarWorld");
            go.transform.SetParent(parent, false);
            var world = go.AddComponent<HangarWorld>();
            world._tankCard = tankCard;
            world._tankAssets = TankModelAssets.For(tankCard);
            TankLoadoutMeshes.Resolve(
                tankCard, tankSave, out world._turretMesh, out world._gunMesh);
            world.Kind = kind ?? (premium ? HangarKind.Premium : HangarKind.Regular);
            world.Build(settings ?? new SettingsData());
            return world;
        }

        private void Build(SettingsData settings)
        {
            SetKind(Kind, true);
            BuildTankModel();
            BuildCamera(settings.MouseSensitivity);
            Orbit.SetView(OpeningYaw + StandYaw, SourceViewPitch);
            ApplySceneToCamera();
        }

        /// <summary>The 4.8 client's opening view, relative to the vehicle.</summary>
        public const float SourceViewYaw = -145f;
        public const float SourceViewPitch = -12f;

        /// <summary>
        /// The yaw of the opening view of the hangar on show, relative to the
        /// vehicle: the 4.8 client's unless a ported hangar opens on its own.
        /// </summary>
        public float OpeningYaw
        {
            get
            {
                var opening = PortedScene != null ? PortedScene.Export.OpeningYaw : null;
                return opening ?? SourceViewYaw;
            }
        }

        /// <summary>The 4.8 environment on show; null while a ported hangar is up.</summary>
        public ClientHangarScene ClientScene { get; private set; }

        /// <summary>The ported 11.x hangar on show, if one is up.</summary>
        public HalloweenHangarScene PortedScene { get; private set; }

        /// <summary>Which environment the stand shows.</summary>
        public HangarKind Kind { get; private set; }

        public bool IsPremium { get { return Kind == HangarKind.Premium; } }

        /// <summary>Swap to one of the two 4.8 hangars by premium status.</summary>
        public bool SetPremium(bool premium, bool force = false)
        {
            return SetKind(premium ? HangarKind.Premium : HangarKind.Regular, force);
        }

        /// <summary>Swap only the environment. Camera, tank, mounted modules
        /// and the current orbit/armor view are deliberately retained; the
        /// vehicle moves onto the new hangar's stand and the orbit turns with
        /// it, so the view of the vehicle stays where it was relative to the
        /// hangar's opening view.</summary>
        public bool SetKind(HangarKind kind, bool force = false)
        {
            var built = ClientScene != null || PortedScene != null;
            if (!force && built && Kind == kind) return false;
            var previousView = StandYaw + OpeningYaw;
            var previousClient = ClientScene;
            var previousPorted = PortedScene;
            GameObject replacement;
            var ported = PortedHangar.For(kind);
            if (ported != null)
            {
                var scene = HalloweenHangarScene.Create(transform, ported);
                Hide(previousClient, previousPorted);
                ClientScene = null;
                PortedScene = scene;
                Kind = kind;
                HangarModel = scene.gameObject;
                BakedInstanceCount = scene.RendererCount;
                SourceLightCount = scene.LightCount;
                SmokeEmitterCount = 0;
                scene.ApplyEnvironment();
                replacement = scene.gameObject;
                Debug.Log("[Blitz] 11.21 hangar: " + scene.Id +
                    " renderers=" + scene.RendererCount +
                    " triangles=" + scene.TriangleCount +
                    " materials=" + scene.MaterialCount +
                    " particle systems=" + scene.ParticleSystemCount +
                    " animations=" + scene.AnimationCount +
                    " states=" + scene.StateSwitcherCount +
                    " scenarios=" + scene.ScenarioCount +
                    " sounds=" + (scene.Audio != null ? scene.Audio.EmitterCount : 0) +
                    " warnings=" + scene.Warnings.Count);
            }
            else
            {
                var premium = kind == HangarKind.Premium;
                var scene = ClientHangarScene.Create(transform, premium);
                Hide(previousClient, previousPorted);
                ClientScene = scene;
                PortedScene = null;
                Kind = kind;
                HangarModel = scene.gameObject;
                BakedInstanceCount = scene.BatchCount;
                SourceLightCount = scene.RuntimeLightCount;
                SmokeEmitterCount = scene.SmokeEmitterCount;
                scene.ApplyEnvironment();
                replacement = scene.gameObject;
                Debug.Log("[Blitz] 4.8 hangar: " + scene.Definition.id +
                    " batches=" + scene.BatchCount +
                    " triangles=" + scene.TriangleCount +
                    " particle layers=" + scene.ParticleLayerCount +
                    " motions=" + scene.AnimationCount);
            }
            replacement.SetActive(true);
            if (previousClient != null) ReleaseObject(previousClient.gameObject);
            if (previousPorted != null)
            {
                previousPorted.Release();
                ReleaseObject(previousPorted.gameObject);
            }
            PlaceTank();
            if (Orbit != null && built)
                Orbit.SetView(Orbit.Yaw + Mathf.DeltaAngle(previousView, StandYaw + OpeningYaw), Orbit.Pitch);
            ApplySceneToCamera();
            return true;
        }

        /// <summary>
        /// Gives back at once what the stand holds outside its own objects:
        /// the ported hangar's render state and the assets it loaded. A
        /// destroyed world does it in OnDestroy at the end of the frame, after
        /// a hangar built in the same frame has applied its own lighting;
        /// owners that replace the world call this first. Safe to repeat.
        /// </summary>
        public void Release()
        {
            if (PortedScene != null)
                PortedScene.Release();
        }

        private static void Hide(ClientHangarScene client, HalloweenHangarScene ported)
        {
            if (client != null) client.gameObject.SetActive(false);
            if (ported != null)
            {
                // Hand the render state back before the next hangar takes it.
                ported.ReleaseEnvironment();
                ported.gameObject.SetActive(false);
            }
        }

        private static void ReleaseObject(UnityEngine.Object obj)
        {
            if (Application.isPlaying) Destroy(obj);
            else DestroyImmediate(obj);
        }

        /// <summary>The stand shares the 4.8 client's metre/forward frame.</summary>
        public const float ModelYaw = 180f;

        /// <summary>
        /// Where the vehicle stands. The 4.8 hangars stand it at the origin in
        /// the client's unturned frame; a ported hangar names a spawn point
        /// and a heading in the same frame, applied on top of it.
        /// </summary>
        public Vector3 StandPosition
        {
            get { return PortedScene != null ? PortedScene.SpawnPosition : Vector3.zero; }
        }

        public Quaternion StandRotation
        {
            get
            {
                var model = Quaternion.Euler(0f, ModelYaw, 0f);
                return PortedScene != null ? PortedScene.SpawnRotation * model : model;
            }
        }

        /// <summary>The heading of <see cref="StandRotation"/> about the vertical, in degrees.</summary>
        public float StandYaw
        {
            get { return StandRotation.eulerAngles.y - ModelYaw; }
        }

        private void PlaceTank()
        {
            if (TankModel != null)
            {
                TankModel.transform.localPosition = StandPosition;
                TankModel.transform.localRotation = StandRotation;
            }
            if (Orbit != null)
                Orbit.transform.localPosition = StandPosition + new Vector3(0f, OrbitHeight, 0f);
        }

        /// <summary>Height of the orbit pivot above the stand.</summary>
        public const float OrbitHeight = 1.5f;

        /// <summary>The camera side of the environment: clear colour, the
        /// depth texture soft particles read, the listener of hangar sounds.</summary>
        private void ApplySceneToCamera()
        {
            if (HangarCamera == null) return;
            HangarCamera.backgroundColor = SceneBackground();
            var data = HangarCamera.GetUniversalAdditionalCameraData();
            if (data != null)
            {
                // The 11.21 soft particles fade against scene depth; a hangar
                // without particles and the 4.8 hangars get back whatever the
                // camera asked for before.
                if (PortedScene != null && PortedScene.ParticleSystemCount > 0)
                {
                    if (!_depthForced) _depthOption = data.requiresDepthOption;
                    _depthForced = true;
                    data.requiresDepthTexture = true;
                }
                else if (_depthForced)
                {
                    data.requiresDepthOption = _depthOption;
                    _depthForced = false;
                }
            }
            if (PortedScene != null && PortedScene.Audio != null)
                PortedScene.Audio.ListenFrom(HangarCamera);
        }

        private bool _depthForced;
        private CameraOverrideOption _depthOption;

        private Color SceneBackground()
        {
            if (PortedScene != null) return PortedScene.BackgroundColour;
            if (Lighting != null) return SourceEnvironment.AmbientColour(Lighting.Definition.Environment);
            var colour = ClientScene.Definition.environment.color;
            return new Color(colour[0], colour[1], colour[2]);
        }

        /// <summary>Replaces the displayed vehicle without rebuilding the hangar.</summary>
        public bool ShowTank(TankInfo tankCard)
        {
            var assets = TankModelAssets.For(tankCard);
            if (TankAssetSource.Model(assets) == null)
            {
                Debug.LogError("[Blitz] missing tank model: " + assets.ModelResource);
                return false;
            }

            if (TankModel != null)
            {
                TankModel.SetActive(false);
                Destroy(TankModel);
                TankModel = null;
            }

            _tankCard = tankCard;
            _tankAssets = assets;
            BuildTankModel();
            return TankModel != null;
        }

        private void BuildTankModel()
        {
            var assets = _tankAssets ?? TankModelAssets.For(_tankCard);
            var tankPrefab = TankAssetSource.Model(assets);
            if (tankPrefab == null)
            {
                Debug.LogError("[Blitz] missing tank model: " + assets.ModelResource);
                return;
            }

            TankModel = Instantiate(tankPrefab, transform, false);
            TankModel.name = assets.Id;
            TankModel.transform.localPosition = StandPosition;
            TankModel.transform.localRotation = StandRotation;

            Debug.Log("[Blitz] imported tank model " + assets.Id + ": " +
                DescribeImportedModel(TankModel));

            var bodyTexture = TankAssetSource.BodyTexture(assets);
            var trackTexture = TankAssetSource.TrackTexture(assets);
            var tankBody = CreateTank(assets.Id + "Body", bodyTexture, false);
            var tankTracks = CreateTank(assets.Id + "Tracks", trackTexture, true);

            var nodes = AssignTankMaterials(TankModel, assets, tankBody, tankTracks);
            ReleaseCamouflage();
            _camouflage = TankCamouflageView.Build(TankModel, assets, nodes, assets.TankName, tankTracks);
            ApplyModules();
        }

        /// <summary>
        /// Paints the vehicle on the stand in a camouflage, or plain for an
        /// empty id or one the vehicle cannot wear. Returns how many
        /// materials wear it.
        /// </summary>
        public int ShowCamouflage(string camouflage)
        {
            return _camouflage != null ? _camouflage.Show(camouflage) : 0;
        }

        private void ReleaseCamouflage()
        {
            if (_camouflage == null) { return; }
            _camouflage.Release();
            _camouflage = null;
        }

        /// <summary>
        /// Shows the turret and the gun the loadout installs. The export keeps
        /// one object per researchable slot of the client vehicle, so mounting a
        /// module is a visibility switch rather than another import.
        /// </summary>
        public int ShowModules(string turretMesh, string gunMesh)
        {
            _turretMesh = turretMesh;
            _gunMesh = gunMesh;
            return ApplyModules();
        }

        private int ApplyModules()
        {
            if (TankModel == null)
            {
                return 0;
            }

            var assets = _tankAssets ?? TankModelAssets.For(_tankCard);
            var json = TankAssetSource.NodesJson(assets);
            if (string.IsNullOrEmpty(json))
            {
                return 0;
            }

            var nodes = TankNodeTable.Parse(json);
            var turret = nodes.ResolveTurret(_turretMesh);
            var gun = nodes.ResolveGun(turret, _gunMesh);
            var hidden = TankModuleView.ShowInstalled(TankModel, nodes, turret, gun);
            Debug.Log("[Blitz] hangar modules: turret=" + turret +
                " gun=" + gun + " hidden=" + hidden);
            return hidden;
        }

        /// <summary>
        /// Builds the hangar the way the source bake describes it: one renderer
        /// per baked instance, drawing the unwrapped mesh the payload carries,
        /// placed by the transform the scene authored and shaded by its own
        /// source material times its rect of the atlas.
        ///
        /// This replaces an exported OBJ whose three material slots had to be
        /// guessed from a hand-written list of node names. The bake is the only
        /// description of the hangar that also carries the light, so taking the
        /// geometry from it makes the two agree by construction: an instance
        /// cannot end up wearing another instance's unwrap or material.
        /// </summary>
        /// <summary>
        /// Paints the import the way the client scene binds its sheets. Most
        /// vehicles carry one body atlas, but a card whose second turret ships
        /// its own sheet (IS-3's Mod. T-10 turret and its guns, E-100, Panther
        /// II ...) binds that sheet per node. Battle already reads those
        /// bindings; the hangar used to paint everything with the body atlas,
        /// so the IS-3 barrel sampled the hull sheet and read as torn paint
        /// with black patches where the unwrap fell into empty texels.
        /// </summary>
        private TankNodeTable AssignTankMaterials(
            GameObject model, TankModelAssets assets, Material body, Material tracks)
        {
            var nodesJson = TankAssetSource.NodesJson(assets);
            var nodes = !string.IsNullOrEmpty(nodesJson)
                ? TankNodeTable.Parse(nodesJson)
                : null;
            var albedoMaterials = new Dictionary<string, Material>();

            Material AlbedoMaterial(string objectName)
            {
                if (nodes == null) { return body; }

                var albedoId = nodes.AlbedoOf(objectName);
                if (string.IsNullOrEmpty(albedoId) || albedoId == assets.Id)
                {
                    return body;
                }

                Material material;
                var materialKey = nodes.HasMaterialSettings(objectName)
                    ? albedoId + "/" + objectName : albedoId;
                if (!albedoMaterials.TryGetValue(materialKey, out material))
                {
                    var texture = LoadTexture(assets.AlbedoTextureResource(albedoId));
                    material = texture != null
                        ? CreateTankNode(assets.Id + "_" + albedoId, texture, nodes, objectName)
                        : body;
                    albedoMaterials[materialKey] = material;
                }
                return material;
            }

            var renderers = model.GetComponentsInChildren<MeshRenderer>(true);
            if (renderers.Length == 1)
            {
                var renderer = renderers[0];
                if (GetSubMeshCount(renderer) == 2)
                {
                    // The deterministic OBJ writes Body before Tracks.
                    renderer.sharedMaterials = new[] { body, tracks };
                }
                else
                {
                    Debug.LogError(
                        "[Blitz] imported tank must contain body and track submeshes");
                    renderer.sharedMaterial = body;
                }
                ConfigureRenderer(renderer);
                return nodes;
            }

            foreach (var renderer in renderers)
            {
                var objectName = TrimCloneSuffix(renderer.gameObject.name);
                if (objectName == "tracks_crash")
                {
                    // The client only draws the broken tracks on a dead hull;
                    // the hangar shows the vehicle the player owns.
                    renderer.gameObject.SetActive(false);
                    continue;
                }

                renderer.sharedMaterial = objectName == "trackL" || objectName == "trackR"
                    ? tracks
                    : AlbedoMaterial(objectName);
                ConfigureRenderer(renderer);
            }

            return nodes;
        }

        private static int GetSubMeshCount(MeshRenderer renderer)
        {
            var filter = renderer != null ? renderer.GetComponent<MeshFilter>() : null;
            return filter != null && filter.sharedMesh != null
                ? filter.sharedMesh.subMeshCount
                : 0;
        }

        private static void ConfigureRenderer(MeshRenderer renderer)
        {
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;
        }

        private void BuildCamera(float sensitivity)
        {
            var pivot = new GameObject("CameraRoot");
            pivot.transform.SetParent(transform, false);
            pivot.transform.localPosition = StandPosition + new Vector3(0f, OrbitHeight, 0f);
            Orbit = pivot.AddComponent<HangarOrbitController>();

            var cameraObject = new GameObject("HangarCamera");
            cameraObject.transform.SetParent(transform, false);
            HangarCamera = cameraObject.AddComponent<Camera>();
            HangarCamera.clearFlags = CameraClearFlags.SolidColor;
            // The source scene owns the clear colour as well as its fog.
            HangarCamera.backgroundColor = SceneBackground();
            HangarCamera.nearClipPlane = 0.1f;
            HangarCamera.farClipPlane = 1500f;
            HangarCamera.allowHDR = true;
            HangarCamera.allowMSAA = true;

            // The graphics tab is opened from the hangar, so the hangar has to
            // react to it as much as a battle does. Keep the source sky dome
            // inside the far plane; its vertices are roughly 700 m away.
            GraphicsQuality.Register(HangarCamera, 1500f);
            Orbit.Configure(HangarCamera, sensitivity);
        }

        /// <summary>Legacy Godot smoke material helper, retained only for the
        /// existing importer/regression assets. 4.8 effects use ClientHangarScene.</summary>
        public static Texture2D SmokeTexture()
        {
            return LoadTexture("Models/Hangar/Textures/Smoke");
        }

        /// <summary>
        /// Blend state of Godot's StandardMaterial3D_s2tt0: transparency =
        /// ALPHA, blend_mode = ADD, cull_mode = DISABLED, shading = UNSHADED.
        /// Shared by the edit-time material generator and the runtime fallback
        /// so the two cannot drift apart.
        /// </summary>
        public static void ConfigureSmokeMaterial(Material material, Texture2D texture)
        {
            if (material == null)
            {
                return;
            }

            if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
            if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);
            if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);

            // StandardMaterial3D_s2tt0: transparency = ALPHA, blend_mode = ADD,
            // cull_mode = DISABLED, shading_mode = UNSHADED.
            //
            // _Surface and _Blend only drive URP's material inspector; the
            // render state itself is authored by the shader GUI at edit time.
            // A material built at runtime therefore keeps the shader's opaque
            // defaults, which is what turned the hangar smoke into a solid
            // white slab on the floor. Write the blend state explicitly.
            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
            if (material.HasProperty("_Blend")) material.SetFloat("_Blend", 2f);
            if (material.HasProperty("_BlendOp"))
            {
                material.SetFloat("_BlendOp", (float)UnityEngine.Rendering.BlendOp.Add);
            }
            if (material.HasProperty("_SrcBlend"))
            {
                material.SetFloat(
                    "_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            }
            if (material.HasProperty("_DstBlend"))
            {
                material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
            }
            if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
            if (material.HasProperty("_Cull"))
            {
                material.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
            }
            if (material.HasProperty("_AlphaClip")) material.SetFloat("_AlphaClip", 0f);
            if (material.HasProperty("_ReceiveShadows")) material.SetFloat("_ReceiveShadows", 0f);
            if (material.HasProperty("_ColorMode")) material.SetFloat("_ColorMode", 0f);
            if (material.HasProperty("_SoftParticlesEnabled"))
            {
                material.SetFloat("_SoftParticlesEnabled", 0f);
            }

            material.DisableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            material.DisableKeyword("_ALPHAMODULATE_ON");
            material.DisableKeyword("_SURFACE_TYPE_OPAQUE");
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.SetShaderPassEnabled("ShadowCaster", false);
            material.SetOverrideTag("RenderType", "Transparent");
            material.renderQueue = (int)RenderQueue.Transparent;
        }

        /// <summary>
        /// The hangar tank is shaded exactly like the battle tank, and for the
        /// same reason it clones a shipped material instead of assembling the
        /// keyword set at runtime: Unity's player variant stripper keeps only
        /// the combinations a serialized material uses, so a runtime-only
        /// alpha-tested variant falls back to opaque on a device and paints the
        /// transparent gaps of the track sheet solid white.
        /// </summary>
        private Material CreateTankNode(
            string name, Texture2D texture, TankNodeTable nodes, string objectName)
        {
            var material = BattleTank.CreateTankNodeMaterial(name, texture, nodes, objectName);
            _materials.Add(material);
            return material;
        }

        private Material CreateTank(string name, Texture2D texture, bool alphaClip)
        {
            var material = BattleTank.CreateTankMaterial(name, texture, alphaClip);
            _materials.Add(material);
            return material;
        }

        private static Texture2D LoadTexture(string path)
        {
            var texture = Resources.Load<Texture2D>(path);
            if (texture == null)
            {
                Debug.LogWarning("[Blitz] missing texture: " + path);
            }
            return texture;
        }

        private static string DescribeImportedModel(GameObject model)
        {
            var renderers = model.GetComponentsInChildren<MeshRenderer>(true);
            var descriptions = new List<string>();
            for (var i = 0; i < renderers.Length; i++)
            {
                var filter = renderers[i].GetComponent<MeshFilter>();
                var subMeshes = filter != null && filter.sharedMesh != null
                    ? filter.sharedMesh.subMeshCount
                    : 0;
                var slots = renderers[i].sharedMaterials;
                var slotNames = new string[slots.Length];
                for (var slot = 0; slot < slots.Length; slot++)
                {
                    slotNames[slot] = slots[slot] != null ? slots[slot].name : "<null>";
                }
                var indexCounts = new string[subMeshes];
                for (var subMesh = 0; subMesh < subMeshes; subMesh++)
                {
                    indexCounts[subMesh] =
                        (filter.sharedMesh.GetIndexCount(subMesh) / 3u).ToString();
                }
                var localBounds = filter != null && filter.sharedMesh != null
                    ? filter.sharedMesh.bounds
                    : new Bounds();
                descriptions.Add(
                    renderers[i].gameObject.name + ":submeshes=" + subMeshes +
                    ":triangles=[" + string.Join(",", indexCounts) + "]" +
                    ":slots=[" + string.Join(",", slotNames) + "]" +
                    ":local=" + localBounds.min.ToString("F3") + ".." +
                    localBounds.max.ToString("F3") +
                    ":world=" + renderers[i].bounds.min.ToString("F3") + ".." +
                    renderers[i].bounds.max.ToString("F3"));
            }
            return "renderers=" + renderers.Length + " " +
                string.Join(";", descriptions.ToArray());
        }

        private static string TrimCloneSuffix(string value)
        {
            const string suffix = "(Clone)";
            return value != null && value.EndsWith(suffix, StringComparison.Ordinal)
                ? value.Substring(0, value.Length - suffix.Length)
                : value;
        }

        private void OnDestroy()
        {
            ReleaseCamouflage();
            if (Lighting != null)
            {
                // The bake's meshes and materials are built at runtime and
                // belong to no scene, so they outlive the world unless they
                // are released with it.
                Lighting.Release();
                Lighting = null;
            }

            for (var i = 0; i < _materials.Count; i++)
            {
                if (_materials[i] != null)
                {
                    Destroy(_materials[i]);
                }
            }
            _materials.Clear();
        }
    }
}
