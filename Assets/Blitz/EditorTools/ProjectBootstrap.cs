using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlitzLegacy.EditorTools
{
    /// <summary>
    /// Reproducible project configuration, ported one-to-one from the Godot
    /// project.godot and export_presets.cfg values. Re-running is idempotent,
    /// so project settings never have to be touched in the GUI.
    /// </summary>
    public static class ProjectBootstrap
    {
        /// <summary>
        /// Closed-test identifier. It deliberately differs from the shipping
        /// Godot build's "ru.blitzlegacy.remastered" so the rewrite installs
        /// side by side instead of replacing it, and so Android keeps the two
        /// save directories apart (persistentDataPath is package-scoped).
        /// </summary>
        public const string ApplicationIdentifier = "ru.blitzlegacy.remastered.supertest";

        /// <summary>
        /// Launcher label. Differs from the Godot build's "Blitz Legacy:
        /// Remastered" so both icons are told apart on a test device.
        /// </summary>
        public const string ProductName = "Blitz Legacy: Remastered Deluxe Edition";

        public const string BundleVersion = BlitzLegacy.Core.BlitzVersion.Current;
        public const int BundleVersionCode = BlitzLegacy.Core.BlitzVersion.Code;

        /// <summary>Godot layer names 1..9 land on Unity user layers 8..16.</summary>
        private static readonly string[] UserLayers =
        {
            "static",
            "physics",
            "wheel",
            "armor",
            "screen",
            "foliage",
            "visibility",
            "modules",
            "destructibles",
        };

        // HangarWorld and BattleWorld create these materials in code.
        // Shader.Find works in the editor even when a shader would otherwise
        // be stripped from a player, so every runtime-only shader must be part
        // of the build explicitly. The sky is the cautionary tale: the editor
        // renders the terrain under it and the phone showed a black dome.
        private static readonly string[] RuntimeShaderNames =
        {
            BlitzLegacy.Runtime.Battle.BattleObjects.LitShaderName,
            // The sky dome inside the client map objects and the armour
            // viewer build URP Unlit in code, so stripping it made the phone
            // refuse the battle while the editor still found the shader.
            BlitzLegacy.Runtime.Battle.BattleObjects.UnlitShaderName,
            BlitzLegacy.Runtime.Battle.BattleTank.TankShaderName,
            "Universal Render Pipeline/Particles/Unlit",
            BlitzLegacy.Runtime.Battle.BattleWorld.SkyShaderName,
            BlitzLegacy.Runtime.Battle.BattleWorld.SkyFaceShaderName,
            BlitzLegacy.Runtime.Battle.BattleWorld.LandscapeShaderName,
            BlitzLegacy.Runtime.Battle.BattleObjects.VegetationShaderName,
            BlitzLegacy.Runtime.Battle.BattleObjects.WaterShaderName,
            BlitzLegacy.Runtime.Lighting.BakedLighting.ShaderName,
            BlitzLegacy.Runtime.Hangar.ClientHangarScene.SurfaceShader,
            BlitzLegacy.Runtime.Hangar.ClientHangarScene.ParticleShader,
            // The container opening builds its chest and particle materials in code.
            BlitzLegacy.Runtime.Chest.Chest480View.UnlitShader,
            BlitzLegacy.Runtime.Chest.Chest480View.BoxShader,
            BlitzLegacy.Runtime.Chest.Chest480View.ParticleShader,
            BlitzLegacy.Runtime.Chest.Chest480View.PresentShader,
            BlitzLegacy.Runtime.Lighting.SourceAdjustments.ShaderName,
            // The armour viewer builds its plates in code too, so without
            // these two the phone found no shader and refused the vehicle.
            BlitzLegacy.Runtime.Hangar.HangarArmorViewer.FillShaderName,
            BlitzLegacy.Runtime.Battle.ArmorPreviewShader.Name,
            // The aim outline and its stencil stamp are built in code as well.
            // Without the stamp the armour overlay, which only draws where the
            // stamp wrote stencil 1, vanished on the phone together with the
            // outline, while the editor still found both shaders.
            BlitzLegacy.Runtime.Battle.TankOutlineShader.Name,
            BlitzLegacy.Runtime.Battle.TankOutlineShader.MaskName,
            // The tracer falls back to Sprites/Default when this is stripped,
            // so the phone drew a different shell trail than the editor.
            BlitzLegacy.Runtime.Battle.BattleShellTrail.ShaderName,
            // Combat effects ported from Godot build their materials in code.
            BlitzLegacy.Runtime.Battle.CombatFx.GodotParticleEffect.ShaderName,
            BlitzLegacy.Runtime.Battle.CombatFx.BattleGpuTrail.ShaderName,
            // Binary client map scenes (Mirage) build every material in code.
            BlitzLegacy.Runtime.Battle.ClientMapScene.SurfaceShader,
            BlitzLegacy.Runtime.Battle.ClientMapScene.SkyShader,
            BlitzLegacy.Runtime.Battle.ClientMapScene.WaterShader,
            BlitzLegacy.Runtime.Battle.ClientMapScene.ShadowVolumeShader,
            BlitzLegacy.Runtime.Battle.ClientMapScene.LandscapeShader,
            // 11.20 client scenes (Sea of Tranquility) add lit props and vegetation.
            BlitzLegacy.Runtime.Battle.ClientMapScene.LitShader,
            BlitzLegacy.Runtime.Battle.ClientMapScene.VegetationShader,
            // Godot-ported map scenes (Rudniki) mirror the Godot mobile renderer.
            BlitzLegacy.Runtime.Battle.ClientMapScene.GodotObjectShader,
            BlitzLegacy.Runtime.Battle.ClientMapScene.GodotWaterShader,
            BlitzLegacy.Runtime.Battle.ClientMapScene.GodotVegetationShader,
            BlitzLegacy.Runtime.Battle.ClientMapScene.GodotSpriteShader,
            BlitzLegacy.Runtime.Battle.ClientMapScene.GodotSkyShader,
            BlitzLegacy.Runtime.Battle.ClientMapScene.GodotLandscapeShader,
            // The Halloween 2026 hangar builds all of its materials in code.
            BlitzLegacy.Runtime.Hangar.Halloween.HalloweenHangarScene.PbrShaderName,
            BlitzLegacy.Runtime.Hangar.Halloween.HalloweenHangarScene.TexturedShaderName,
            BlitzLegacy.Runtime.Hangar.Halloween.HalloweenHangarScene.SkyShaderName,
            BlitzLegacy.Runtime.Hangar.Halloween.HalloweenHangarScene.ParticleShaderName,
        };

        private static readonly string[] TerrainTexturePaths =
        {
            "Assets/Blitz/Resources/Maps/Rudniki/colormap.png",
            "Assets/Blitz/Resources/Maps/Rudniki/tilemask.png",
            "Assets/Blitz/Resources/Maps/Rudniki/tiletextures.png",
        };

        private static readonly string[] VegetationTexturePaths =
        {
            "Assets/Blitz/Resources/Maps/Rudniki/Objects/Vegetation/vegetation_bush01.png",
            "Assets/Blitz/Resources/Maps/Rudniki/Objects/Vegetation/vegetation_bush02.png",
            "Assets/Blitz/Resources/Maps/Rudniki/Objects/Vegetation/vegetation_bush02_2.png",
            "Assets/Blitz/Resources/Maps/Rudniki/Objects/Vegetation/vegetation_bush03.png",
            "Assets/Blitz/Resources/Maps/Rudniki/Objects/Vegetation/vegetation_tree_02_2.png",
            "Assets/Blitz/Resources/Maps/Rudniki/Objects/Vegetation/vegetation_tree_K4_1.png",
        };

        public static void ApplyAll()
        {
            ApplyEditorSettings();
            ApplyPlayerSettings();
            ApplyGraphicsApis();
            ReimportTerrainTextures();
            ReimportVegetationTextures();
            if (!EnsureRuntimeShaders())
            {
                EditorApplication.Exit(1);
                return;
            }

            if (!EnsureRuntimeMaterials())
            {
                EditorApplication.Exit(1);
                return;
            }

            ApplyPhysicsSettings();
            ApplyQualitySettings();
            ApplyLayers();
            AudioProjectSettings.Apply();

            AssetDatabase.SaveAssets();
            Debug.Log("[Blitz] project settings applied; package=" + ApplicationIdentifier);
            EditorApplication.Exit(0);
        }

        private static void ReimportTerrainTextures()
        {
            // AssetPostprocessor code can change without the PNG bytes doing
            // so. Force these three source-backed maps through the current
            // rules whenever setup runs; otherwise an old local .meta can keep
            // the packed detail data in sRGB or at a softer ASTC block size.
            for (var i = 0; i < TerrainTexturePaths.Length; i++)
            {
                AssetDatabase.ImportAsset(
                    TerrainTexturePaths[i], ImportAssetOptions.ForceUpdate);
            }
        }

        private static void ReimportVegetationTextures()
        {
            // The Rudniki porter generates these six foliage sheets. Force them through the
            // current colour/alpha rules when setup runs so a stale local meta
            // cannot soften a cutout or strip its mipmaps on Android.
            for (var i = 0; i < VegetationTexturePaths.Length; i++)
            {
                AssetDatabase.ImportAsset(
                    VegetationTexturePaths[i], ImportAssetOptions.ForceUpdate);
            }
        }

        /// <summary>
        /// Keeps shaders referenced only by code in Android players. Build
        /// entry points also call this so a clean checkout cannot silently
        /// produce the editor-only failure mode where the hangar stops before
        /// its UI is constructed.
        /// </summary>
        public static bool EnsureRuntimeShaders()
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath(
                "ProjectSettings/GraphicsSettings.asset");
            if (assets == null || assets.Length == 0)
            {
                Debug.LogError("[Blitz] GraphicsSettings.asset not found");
                return false;
            }

            var graphicsSettings = new SerializedObject(assets[0]);
            var alwaysIncluded = graphicsSettings.FindProperty(
                "m_AlwaysIncludedShaders");
            if (alwaysIncluded == null || !alwaysIncluded.isArray)
            {
                Debug.LogError("[Blitz] always-included shader list not found");
                return false;
            }

            var foundAll = true;
            for (var i = 0; i < RuntimeShaderNames.Length; i++)
            {
                var shader = Shader.Find(RuntimeShaderNames[i]);
                if (shader == null)
                {
                    Debug.LogError(
                        "[Blitz] required runtime shader is unavailable: " +
                        RuntimeShaderNames[i]);
                    foundAll = false;
                    continue;
                }

                var alreadyIncluded = false;
                for (var index = 0; index < alwaysIncluded.arraySize; index++)
                {
                    if (alwaysIncluded.GetArrayElementAtIndex(index)
                            .objectReferenceValue == shader)
                    {
                        alreadyIncluded = true;
                        break;
                    }
                }

                if (!alreadyIncluded)
                {
                    var index = alwaysIncluded.arraySize;
                    alwaysIncluded.arraySize = index + 1;
                    alwaysIncluded.GetArrayElementAtIndex(index)
                        .objectReferenceValue = shader;
                }
            }

            graphicsSettings.ApplyModifiedPropertiesWithoutUndo();
            return foundAll;
        }

        /// <summary>Material asset that keeps the smoke shader variants alive.</summary>
        public const string SmokeMaterialPath =
            "Assets/Blitz/Resources/Materials/HangarSmoke.mat";

        /// <summary>Opaque MS-1 variant retained in Android players.</summary>
        public const string BattleTankBodyMaterialPath =
            "Assets/Blitz/Resources/Materials/MS1BattleBody.mat";

        /// <summary>Alpha-tested MS-1 track variant retained in Android players.</summary>
        public const string BattleTankTrackMaterialPath =
            "Assets/Blitz/Resources/Materials/MS1BattleTracks.mat";

        /// <summary>
        /// Blend variants of the wreck explosion. explosion1.tscn mixes ADD
        /// and MIX quads, and each one needs a serialized material so the
        /// player keeps its keyword combination.
        /// </summary>
        public const string BattleParticleAddMaterialPath =
            "Assets/Blitz/Resources/Materials/BattleParticleAdd.mat";

        public const string BattleParticleMixMaterialPath =
            "Assets/Blitz/Resources/Materials/BattleParticleMix.mat";

        private const string RuntimeMaterialFolder = "Assets/Blitz/Resources/Materials";
        private const string SmokeTextureResource = "Models/Hangar/Textures/Smoke";

        /// <summary>
        /// Generates runtime-configured materials as real assets. Keeping only
        /// the shader is insufficient: Unity strips keyword combinations that
        /// no serialized material uses. That once made smoke opaque white and
        /// also made the battle-only MS-1 variant white/glassy on Android.
        /// </summary>
        public static bool EnsureRuntimeMaterials()
        {
            if (!AssetDatabase.IsValidFolder(RuntimeMaterialFolder))
            {
                AssetDatabase.CreateFolder("Assets/Blitz/Resources", "Materials");
            }

            var smokeShader = Shader.Find(
                BlitzLegacy.Runtime.Hangar.HangarWorld.SmokeShaderName);
            var smokeTexture = Resources.Load<Texture2D>(SmokeTextureResource);
            if (smokeShader == null || smokeTexture == null)
            {
                Debug.LogError("[Blitz] smoke material inputs are unavailable");
                return false;
            }

            var smoke = AssetDatabase.LoadAssetAtPath<Material>(SmokeMaterialPath);
            var smokeCreated = smoke == null;
            if (smokeCreated) { smoke = new Material(smokeShader); }
            else if (smoke.shader != smokeShader) { smoke.shader = smokeShader; }
            BlitzLegacy.Runtime.Hangar.HangarWorld.ConfigureSmokeMaterial(
                smoke, smokeTexture);
            SaveMaterial(smoke, SmokeMaterialPath, smokeCreated);

            var tankShader = Shader.Find(
                BlitzLegacy.Runtime.Battle.BattleTank.TankShaderName);
            var tankTexture = Resources.Load<Texture2D>(
                BlitzLegacy.Runtime.Battle.BattleTank.TextureResource);
            if (tankShader == null || tankTexture == null)
            {
                Debug.LogError("[Blitz] battle tank material inputs are unavailable");
                return false;
            }

            EnsureTankMaterial(
                BattleTankBodyMaterialPath,
                "MS1BattleBody",
                tankShader,
                tankTexture,
                false);
            EnsureTankMaterial(
                BattleTankTrackMaterialPath,
                "MS1BattleTracks",
                tankShader,
                tankTexture,
                true);

            var particleShader = Shader.Find(
                BlitzLegacy.Runtime.Battle.BattleTankWreck.ParticleShaderName);
            if (particleShader == null)
            {
                Debug.LogError("[Blitz] particle material inputs are unavailable");
                return false;
            }

            EnsureParticleMaterial(
                BattleParticleAddMaterialPath, "BattleParticleAdd", particleShader, true);
            EnsureParticleMaterial(
                BattleParticleMixMaterialPath, "BattleParticleMix", particleShader, false);

            AssetDatabase.SaveAssets();
            Debug.Log("[Blitz] runtime material variants ready");
            return true;
        }

        private static void EnsureTankMaterial(
            string path,
            string name,
            Shader shader,
            Texture2D texture,
            bool alphaClip)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            var created = material == null;
            if (created) { material = new Material(shader); }
            else if (material.shader != shader) { material.shader = shader; }

            material.name = name;
            BlitzLegacy.Runtime.Battle.BattleTank.ConfigureTankMaterial(
                material, texture, alphaClip);
            SaveMaterial(material, path, created);
        }

        /// <summary>
        /// The two wreck-particle blend presets. The sprite sheet itself is
        /// per emitter and is bound when the wreck is built, so the asset only
        /// pins the shader and the blend state.
        /// </summary>
        private static void EnsureParticleMaterial(
            string path, string name, Shader shader, bool additive)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            var created = material == null;
            if (created) { material = new Material(shader); }
            else if (material.shader != shader) { material.shader = shader; }

            material.name = name;
            BlitzLegacy.Runtime.Battle.BattleTankWreck.ConfigureParticleMaterial(
                material, null, additive);
            SaveMaterial(material, path, created);
        }

        private static void SaveMaterial(Material material, string path, bool created)
        {
            if (created) { AssetDatabase.CreateAsset(material, path); }
            else { EditorUtility.SetDirty(material); }
        }

        private static void ApplyEditorSettings()
        {
            // Text serialization + visible meta files are what make the project
            // reviewable and AI-editable outside the GUI.
            EditorSettings.serializationMode = SerializationMode.ForceText;
            EditorSettings.lineEndingsForNewScripts = LineEndingsMode.Unix;
            EditorSettings.projectGenerationRootNamespace = "BlitzLegacy";
            VersionControlSettings.mode = "Visible Meta Files";
        }

        private static void ApplyPlayerSettings()
        {
            PlayerSettings.companyName = "Blitz Legacy";
            PlayerSettings.productName = ProductName;
            PlayerSettings.bundleVersion = BundleVersion;
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, ApplicationIdentifier);
            PlayerSettings.Android.bundleVersionCode = BundleVersionCode;

            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.gpuSkinning = true;

            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetApiCompatibilityLevel(NamedBuildTarget.Android, ApiCompatibilityLevel.NET_Standard);
            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.Android, ManagedStrippingLevel.Low);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

            // Godot's preset left min_sdk empty (its default is 24), but Unity 6
            // clamps Android to API 26, so 26 is the honest floor for this port.
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;

            // Godot handheld/orientation=4 is sensor landscape.
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;

            // Godot immersive_mode=true, edge_to_edge=false.
            PlayerSettings.Android.startInFullscreen = true;

            // Godot draws its viewport over the whole window and never asks for
            // the display safe area, so a camera hole overlaps the UI instead of
            // pushing it aside. Keeping this false left an empty band along the
            // cutout edge that the original build does not have.
            PlayerSettings.Android.renderOutsideSafeArea = true;
            PlayerSettings.Android.androidIsGame = true;
            PlayerSettings.Android.optimizedFramePacing = true;

            // Godot boot_splash/show_image=false.
            PlayerSettings.SplashScreen.show = false;
            PlayerSettings.SplashScreen.showUnityLogo = false;

            EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC;
            EditorUserBuildSettings.selectedBuildTargetGroup = BuildTargetGroup.Android;
        }

        private static void ApplyGraphicsApis()
        {
            // Unity cannot change graphics API at runtime. It walks this ordered
            // list at startup and falls back to the next entry when device
            // initialization fails, which is exactly the automatic Vulkan/GLES3
            // selection we want with no user-facing setting.
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(
                BuildTarget.Android,
                new[] { GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLES3 });
        }

        private static void ApplyPhysicsSettings()
        {
            // Godot: default_gravity=10, solver_iterations=4, velocity_steps=2,
            // sleep_threshold_linear=0.02, bounce_velocity_threshold=10.
            Physics.gravity = new Vector3(0f, -10f, 0f);
            Physics.defaultSolverIterations = 4;
            Physics.defaultSolverVelocityIterations = 2;
            Physics.sleepThreshold = 0.02f;
            Physics.bounceThreshold = 10f;
        }

        private static void ApplyQualitySettings()
        {
            // Godot anisotropic_filtering_level=3 -> per-texture anisotropy.
            QualitySettings.anisotropicFiltering = AnisotropicFiltering.Enable;
            QualitySettings.vSyncCount = 0;

            // The map is now genuinely realtime-lit. Keep the phone bounded to
            // two low-cost cascades while the desktop gets enough reach to keep
            // buildings grounded at normal sniper-camera distances.
            ConfigureShadowPipeline(
                "Assets/Settings/Mobile_RPAsset.asset",
                55f,
                2,
                1024,
                1,
                0.35f,
                0.6f,
                0.15f);
            ConfigureShadowPipeline(
                "Assets/Settings/PC_RPAsset.asset",
                100f,
                4,
                2048,
                3,
                0.1f,
                0.5f,
                0.1f);
        }

        private static void ConfigureShadowPipeline(
            string path,
            float distance,
            int cascades,
            int resolution,
            int softShadowQuality,
            float depthBias,
            float normalBias,
            float cascadeBorder)
        {
            var asset = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(path);
            if (asset == null)
            {
                throw new BuildFailedException("render pipeline asset is missing: " + path);
            }

            var settings = new SerializedObject(asset);
            SetFloat(settings, "m_ShadowDistance", distance, path);
            SetInt(settings, "m_ShadowCascadeCount", cascades, path);
            SetInt(settings, "m_MainLightShadowmapResolution", resolution, path);
            SetBool(settings, "m_MainLightShadowsSupported", true, path);
            SetBool(settings, "m_SoftShadowsSupported", true, path);
            SetInt(settings, "m_SoftShadowQuality", softShadowQuality, path);
            SetFloat(settings, "m_ShadowDepthBias", depthBias, path);
            SetFloat(settings, "m_ShadowNormalBias", normalBias, path);
            SetFloat(settings, "m_CascadeBorder", cascadeBorder, path);
            if (cascades == 2)
            {
                SetFloat(settings, "m_Cascade2Split", 0.35f, path);
            }

            settings.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
        }

        private static void SetFloat(
            SerializedObject settings, string name, float value, string path)
        {
            var property = RequireProperty(settings, name, path);
            property.floatValue = value;
        }

        private static void SetInt(
            SerializedObject settings, string name, int value, string path)
        {
            var property = RequireProperty(settings, name, path);
            property.intValue = value;
        }

        private static void SetBool(
            SerializedObject settings, string name, bool value, string path)
        {
            var property = RequireProperty(settings, name, path);
            property.boolValue = value;
        }

        private static SerializedProperty RequireProperty(
            SerializedObject settings, string name, string path)
        {
            var property = settings.FindProperty(name);
            if (property == null)
            {
                throw new BuildFailedException(
                    "render pipeline property '" + name + "' is missing in " + path);
            }

            return property;
        }

        private static void ApplyLayers()
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (assets == null || assets.Length == 0)
            {
                Debug.LogWarning("[Blitz] TagManager.asset not found; layers unchanged");
                return;
            }

            var tagManager = new SerializedObject(assets[0]);
            var layers = tagManager.FindProperty("layers");
            if (layers == null)
            {
                Debug.LogWarning("[Blitz] layers property not found; layers unchanged");
                return;
            }

            for (var i = 0; i < UserLayers.Length; i++)
            {
                var index = 8 + i;
                if (index >= layers.arraySize)
                {
                    break;
                }

                layers.GetArrayElementAtIndex(index).stringValue = UserLayers[i];
            }

            tagManager.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
