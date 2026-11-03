// Client material records -> Unity materials of the ported shaders. The
// client sets the light, fog and shadow constants per draw; here they are
// material constants, and the two nearest point lights of a renderer make a
// material instance per (material, light pair, skinning) combination.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlitzLegacy.Runtime.Hangar.Halloween
{
    public sealed class MaterialContext
    {
        public HalloweenJson scene;
        public Texture2D dfg, stub;
        public Cubemap diffuse, specular;
        public Shader pbr, textured, sky;
        public Vector3 toLight;
        public Color sunRadiance;
        public Vector4 fog;
        public float fogLimit = 1f;
        public float environmentRotation;
        public float cutoffX = 0.92f, cutoffY = 0.96f, litNormalScale = 0.15f, gateX = 0.5f, gateY = 1.5f;
        public Vector4 shadowColor = new Vector4(0.65f, 0.726f, 0.77f, 1f);
    }

    public static class HalloweenHangarMaterials
    {
        public static string Folder => HH.Generated + "/Materials";
        static readonly Dictionary<string, Material> cache = new Dictionary<string, Material>();
        static readonly Dictionary<string, int> shaderCounts = new Dictionary<string, int>();

        public static void Reset()
        {
            cache.Clear();
            shaderCounts.Clear();
        }

        public static int Count => cache.Count;
        public static IEnumerable<Material> All => cache.Values;
        public static IReadOnlyDictionary<string, int> ShaderCounts => shaderCounts;

        // --------------------------------------------------------- record access
        public static bool Define(HalloweenJson material, string name) => DefineValue(material, name) != 0;

        public static int DefineValue(HalloweenJson material, string name)
        {
            foreach (var d in material["defines"].Items)
                if (d["name"].S() == name)
                    return d["value"].I(1);
            return 0;
        }

        public static float[] Property(HalloweenJson material, string name)
        {
            foreach (var p in material["properties"].Items)
                if (p["name"].S() == name)
                    return p["values"].Floats();
            return null;
        }

        // A material property the material does not set falls back to the scene's
        // global material (SceneRenderConfig), then to the shader default.
        public static Vector4 Value(MaterialContext ctx, HalloweenJson material, string name, Vector4 shaderDefault)
        {
            var f = Property(material, name);
            if (f == null)
            {
                foreach (var g in ctx.scene["environment"]["globals"].Items)
                    if (g["name"].S() == name)
                        f = g["values"].Floats();
            }
            if (f == null)
                return shaderDefault;
            var v = shaderDefault;
            for (int i = 0; i < Math.Min(4, f.Length); i++)
                v[i] = f[i];
            return v;
        }

        public static int TextureIndex(HalloweenJson material, string slot)
        {
            foreach (var t in material["textures"].Items)
                if (t["slot"].S() == slot)
                    return t["texture"].I(-1);
            return -1;
        }

        static int Queue(string layer)
        {
            switch (layer)
            {
                case "OpaqueRenderLayer": return 2000;
                case "AlphaTestRenderLayer": return 2450;
                case "AfterOpaqueRenderLayer": return 2460;
                case "TransclucentRenderLayer": return 3000;
            }
            throw new InvalidOperationException("Unknown render layer " + layer);
        }

        static CullMode Cull(string mode)
        {
            switch (mode)
            {
                case "FACE_BACK": return CullMode.Back;
                case "FACE_FRONT": return CullMode.Front;
                case "NONE": return CullMode.Off;
            }
            throw new InvalidOperationException("Unknown cull mode " + mode);
        }

        static CompareFunction Depth(HalloweenJson material)
        {
            if (!material["depthTest"].B(true))
                return CompareFunction.Always;
            switch (material["depthFunc"].S("CMP_LESS"))
            {
                case "CMP_LESS": return CompareFunction.Less;
                case "CMP_LEQUAL": return CompareFunction.LessEqual;
                case "CMP_EQUAL": return CompareFunction.Equal;
                case "CMP_GREATER": return CompareFunction.Greater;
                case "CMP_GEQUAL": return CompareFunction.GreaterEqual;
                case "CMP_ALWAYS": return CompareFunction.Always;
                case "CMP_NEVER": return CompareFunction.Never;
                case "CMP_NOTEQUAL": return CompareFunction.NotEqual;
            }
            throw new InvalidOperationException("Unknown depth function " + material["depthFunc"].S());
        }

        // BLENDING define of the client materials (materials-fp.sl blend table).
        public static void Blend(int blending, bool blend, out BlendMode src, out BlendMode dst)
        {
            switch (blending)
            {
                case 0:
                    src = blend ? BlendMode.SrcAlpha : BlendMode.One;
                    dst = blend ? BlendMode.OneMinusSrcAlpha : BlendMode.Zero;
                    return;
                case 1: src = BlendMode.SrcAlpha; dst = BlendMode.OneMinusSrcAlpha; return;
                case 2: src = BlendMode.One; dst = BlendMode.One; return;
                case 3: src = BlendMode.SrcAlpha; dst = BlendMode.One; return;
                case 5: src = BlendMode.DstColor; dst = BlendMode.Zero; return;
                case 7:
                case 8: src = BlendMode.One; dst = BlendMode.OneMinusSrcAlpha; return;
            }
            throw new InvalidOperationException("Unsupported BLENDING value " + blending);
        }

        // ------------------------------------------------------------ building
        public static Material Get(MaterialContext ctx, int index, int light0, int light1, bool skinned)
        {
            var record = ctx.scene.Req("materials")[index];
            HH.Require(record.Exists, "Material index " + index + " outside the material table");
            string shaderName = record.Req("shader").S();
            bool lit = shaderName == "PBR";
            if (!lit)
                light0 = light1 = -1;
            string key = $"{index}|{light0}|{light1}|{skinned}";
            if (cache.TryGetValue(key, out var cached))
                return cached;

            Material material;
            switch (shaderName)
            {
                case "PBR": material = new Material(ctx.pbr); Pbr(ctx, record, material, light0, light1, skinned); break;
                case "Textured": material = new Material(ctx.textured); Textured(ctx, record, material, skinned); break;
                case "Sky": material = new Material(ctx.sky); Sky(ctx, record, material); break;
                default: throw new InvalidOperationException("Unknown client shader " + shaderName);
            }
            Common(record, material);
            string name = $"M{index:D3}_{HH.Safe(record["template"].S("material").Replace(".material", ""))}";
            if (lit) name += $"_L{light0}_{light1}";
            if (skinned) name += "_S";
            material = HH.Store(material, $"{Folder}/{name}.mat");
            cache[key] = material;
            shaderCounts[shaderName] = (shaderCounts.TryGetValue(shaderName, out int n) ? n : 0) + 1;
            return material;
        }

        static void Common(HalloweenJson record, Material material)
        {
            int queue = Queue(record.Req("layer").S());
            bool blend = record["blend"].B();
            Blend(DefineValue(record, "BLENDING"), blend, out var src, out var dst);
            if (material.HasProperty("_SrcBlend")) material.SetFloat("_SrcBlend", (float)src);
            if (material.HasProperty("_DstBlend")) material.SetFloat("_DstBlend", (float)dst);
            if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", record["depthWrite"].B(true) ? 1f : 0f);
            if (material.HasProperty("_ZTest")) material.SetFloat("_ZTest", (float)Depth(record));
            if (material.HasProperty("_Cull")) material.SetFloat("_Cull", (float)Cull(record.Req("cullMode").S()));
            material.renderQueue = queue;
            material.SetOverrideTag("RenderType", queue >= 3000 ? "Transparent" : queue >= 2450 && queue < 2460 ? "TransparentCutout" : "Opaque");
            material.enableInstancing = false;
            material.SetShaderPassEnabled("ShadowCaster", record["castsShadow"].B(true));
        }

        static void Toggle(Material material, string property, string keyword, bool on)
        {
            material.SetFloat(property, on ? 1f : 0f);
            if (on) material.EnableKeyword(keyword);
            else material.DisableKeyword(keyword);
        }

        static void Texture(MaterialContext ctx, HalloweenJson record, Material material, string slot, string property, bool required)
        {
            int index = TextureIndex(record, slot);
            if (index >= 0)
                material.SetTexture(property, HalloweenHangarAssets.Texture(ctx.scene, index));
            else if (required)
                material.SetTexture(property, ctx.stub);
        }

        static void Environment(MaterialContext ctx, Material material)
        {
            if (material.HasProperty("_SunDirection")) material.SetVector("_SunDirection", ctx.toLight);
            if (material.HasProperty("_FogColor")) material.SetVector("_FogColor", ctx.fog);
            if (material.HasProperty("_FogLimit")) material.SetFloat("_FogLimit", ctx.fogLimit);
        }

        static void Pbr(MaterialContext ctx, HalloweenJson record, Material material, int light0, int light1, bool skinned)
        {
            HH.Require(ctx.dfg != null && ctx.diffuse != null && ctx.specular != null,
                $"PBR material {record["name"].S()} needs the IBL cube maps and the DFG table");
            Texture(ctx, record, material, "baseColorMap", "_BaseColorMap", true);
            Texture(ctx, record, material, "baseNormalMap", "_BaseNormalMap", true);
            Texture(ctx, record, material, "baseRMMap", "_BaseRMMap", true);
            Texture(ctx, record, material, "miscMap", "_MiscMap", true);
            bool lightmap = Define(record, "PBR_LIGHTMAP");
            if (lightmap)
                Texture(ctx, record, material, "pbrLightmap", "_PbrLightmap", true);
            Toggle(material, "_HHLightmap", "_HH_LIGHTMAP", lightmap);
            var scale = Value(ctx, record, "pbrUvScale", Vector4.zero);
            var offset = Value(ctx, record, "pbrUvOffset", Vector4.zero);
            material.SetVector("_PbrUvScaleOffset", new Vector4(scale.x, scale.y, offset.x, offset.y));
            material.SetVector("_BaseColorFactor", Value(ctx, record, "baseColorFactor", Vector4.one));
            var rm = Value(ctx, record, "roughnessMetallicFactor", new Vector4(1, 1, 0, 0));
            material.SetVector("_RoughnessMetallicFactor", new Vector4(rm.x, rm.y, 0, 0));
            var emissive = Define(record, "EMISSIVE_COLOR") ? Value(ctx, record, "emissiveColor", new Vector4(1, 1, 1, 0)) : Vector4.zero;
            material.SetVector("_EmissiveColor", new Vector4(emissive.x, emissive.y, emissive.z, 0));
            material.SetFloat("_NormalScale", Value(ctx, record, "normalScale", Vector4.one).x);
            bool alphaTest = Define(record, "ALPHATEST");
            Toggle(material, "_HHAlphaTest", "_HH_ALPHATEST", alphaTest);
            material.SetFloat("_AlphatestThreshold", Value(ctx, record, "alphatestThreshold", Vector4.zero).x);
            Toggle(material, "_HHShadowAlphaTest", "_HH_SHADOW_ALPHATEST", record["shadowAlphaTest"].B());
            Toggle(material, "_HHSkinned", "_HH_SKINNED", skinned);
            material.SetFloat("_AlphaBlend", Define(record, "ALPHABLEND") ? 1f : 0f);

            material.SetTexture("_SpecularBRDF", ctx.dfg);
            material.SetTexture("_DiffuseIrradianceMap", ctx.diffuse);
            material.SetTexture("_SpecularReflectionMap", ctx.specular);
            material.SetVector("_SunColor", new Vector4(ctx.sunRadiance.r, ctx.sunRadiance.g, ctx.sunRadiance.b, 0));
            material.SetVector("_ShadowParams", new Vector4(ctx.cutoffX, ctx.cutoffY, ctx.litNormalScale, 0));
            material.SetVector("_IBLParams", new Vector4(ctx.specular.mipmapCount, ctx.environmentRotation, 0, 0));
            Environment(ctx, material);
            PointLight(ctx, material, 0, light0);
            PointLight(ctx, material, 1, light1);
            var intensity = new Vector4(Intensity(ctx, light0), Intensity(ctx, light1), 0, 0);
            material.SetVector("_PointLightIntensity", intensity);
        }

        static float Intensity(MaterialContext ctx, int light) =>
            light < 0 ? 0f : ctx.scene["lights"][light].Req("intensity").F();

        static void PointLight(MaterialContext ctx, Material material, int slot, int light)
        {
            if (light < 0)
            {
                material.SetVector($"_PointLight{slot}", Vector4.zero);
                material.SetVector($"_PointLight{slot}Color", new Vector4(0, 0, 0, 2));
                return;
            }
            var l = ctx.scene.Req("lights")[light];
            HH.Require(l.Exists, "Light index " + light + " outside the light table");
            var p = l.Req("position").V3();
            var c = l.Req("color").V3();
            material.SetVector($"_PointLight{slot}", new Vector4(p.x, p.y, p.z, l.Req("radius").F()));
            material.SetVector($"_PointLight{slot}Color", new Vector4(c.x, c.y, c.z, l.Req("falloff").F()));
        }

        static void Textured(MaterialContext ctx, HalloweenJson record, Material material, bool skinned)
        {
            Texture(ctx, record, material, "albedo", "_Albedo", true);
            bool lightmap = Define(record, "MATERIAL_LIGHTMAP") && TextureIndex(record, "lightmap") >= 0;
            if (Define(record, "MATERIAL_LIGHTMAP"))
                Texture(ctx, record, material, "lightmap", "_Lightmap", true);
            Toggle(material, "_HHLightmap", "_HH_LIGHTMAP", Define(record, "MATERIAL_LIGHTMAP"));
            if (Define(record, "MATERIAL_LIGHTMAP") && !lightmap)
                HH.Warn($"Textured material {record["name"].S()} has MATERIAL_LIGHTMAP without a lightmap; the stub texture is bound as in the client");
            var scale = Value(ctx, record, "uvScale", Vector4.zero);
            var offset = Value(ctx, record, "uvOffset", Vector4.zero);
            material.SetVector("_UvScaleOffset", new Vector4(scale.x, scale.y, offset.x, offset.y));
            var shift = Define(record, "TEXTURE0_ANIMATION_SHIFT") ? Value(ctx, record, "tex0ShiftPerSecond", Vector4.zero) : Vector4.zero;
            material.SetVector("_Tex0ShiftPerSecond", new Vector4(shift.x, shift.y, 0, 0));
            material.SetVector("_FlatColor", Define(record, "FLATCOLOR") ? Value(ctx, record, "flatColor", Vector4.one) : Vector4.one);
            material.SetFloat("_AlphaBlend", Define(record, "ALPHABLEND") ? 1f : 0f);
            material.SetFloat("_AlphatestThreshold", Value(ctx, record, "alphatestThreshold", Vector4.zero).x);
            Toggle(material, "_HHReceiveShadow", "_HH_RECEIVE_SHADOW", Define(record, "SHADOW_RECEIVER"));
            Toggle(material, "_HHAlphaTest", "_HH_ALPHATEST", Define(record, "ALPHATEST"));
            Toggle(material, "_HHShadowAlphaTest", "_HH_SHADOW_ALPHATEST", record["shadowAlphaTest"].B());
            Toggle(material, "_HHSkinned", "_HH_SKINNED", skinned);
            material.SetVector("_ShadowParams", new Vector4(ctx.cutoffX, ctx.cutoffY, ctx.gateX, ctx.gateY));
            material.SetVector("_ShadowColor", ctx.shadowColor);
            Environment(ctx, material);
            var density = Property(record, "fogDensity");
            if (density != null && density.Length > 0)
                material.SetVector("_FogColor", new Vector4(ctx.fog.x, ctx.fog.y, ctx.fog.z, density[0]));
        }

        static void Sky(MaterialContext ctx, HalloweenJson record, Material material)
        {
            Texture(ctx, record, material, "albedo", "_Albedo", true);
            bool flow = Define(record, "FLOWMAP");
            if (flow)
                Texture(ctx, record, material, "flowmap", "_Flowmap", true);
            Toggle(material, "_HHFlowmap", "_HH_FLOWMAP", flow);
            material.SetVector("_FlatColor", Define(record, "FLATCOLOR") ? Value(ctx, record, "flatColor", Vector4.one) : Vector4.one);
            material.SetVector("_FlowParams", new Vector4(
                Value(ctx, record, "flowAnimSpeed", Vector4.zero).x,
                Value(ctx, record, "flowAnimOffset", Vector4.zero).x, 1f, 0f));
        }
    }
}
