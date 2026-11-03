using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// The handles of <c>ArmorPreview.shader</c>, the port of the source's
    /// <c>armor_new.gdshader</c>. <c>add_armor_from_mesh</c> duplicates that
    /// shader per plate, so each plate here owns its own material too.
    /// </summary>
    public static class ArmorPreviewShader
    {
        public const string Name = "BlitzLegacy/Battle/Armor Preview";

        public static readonly int NominalArmor = Shader.PropertyToID("_NominalArmor");
        public static readonly int Caliber = Shader.PropertyToID("_Caliber");
        public static readonly int Penetration = Shader.PropertyToID("_Penetration");
        public static readonly int Normalization = Shader.PropertyToID("_Normalization");
        public static readonly int He = Shader.PropertyToID("_HE");
        public static readonly int Heat = Shader.PropertyToID("_HEAT");
        public static readonly int StencilComp = Shader.PropertyToID("_StencilComp");
        public static readonly int ZTest = Shader.PropertyToID("_ZTest");
        public static readonly int SrcBlend = Shader.PropertyToID("_SrcBlend");
        public static readonly int DstBlend = Shader.PropertyToID("_DstBlend");
        public static readonly int Extrude = Shader.PropertyToID("_Extrude");
        public static readonly int Alpha = Shader.PropertyToID("_Alpha");

        /// <summary><c>ALPHA</c> in the source shader.</summary>
        public const float SourceAlpha = 0.33f;

        /// <summary>The armour overlay shader, or null when it is missing.</summary>
        public static Shader Load()
        {
            return Shader.Find(Name);
        }

        /// <summary>
        /// A fresh material for one plate. The source sets
        /// <c>render_priority = 1</c> so the overlay draws over the hull it
        /// covers; Unity spells that as a render queue nudge.
        /// </summary>
        public static Material NewMaterial(Shader shader)
        {
            if (shader == null) { return null; }

            var material = new Material(shader);
            material.name = "armor_preview";
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + 1;
            material.SetFloat(
                StencilComp,
                (float)UnityEngine.Rendering.CompareFunction.Equal);
            return material;
        }
    }
}
