using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// The handles of <c>TankOutline.shader</c>, the port of the source's
    /// <c>outline2.gdshader</c>. The source hangs one of these off every tank
    /// material as its <c>next_pass</c>, so a tank carries one outline
    /// material per visible material it owns.
    /// </summary>
    public static class TankOutlineShader
    {
        public const string Name = "BlitzLegacy/Battle/Tank Outline";

        /// <summary>
        /// The stencil stamp of <c>TankOutlineMask.shader</c>. The source gets
        /// this from <c>stencil_mode write</c> on the tank's own material; here
        /// it is a material of its own so the whole tank stamps itself in the
        /// opaque queue before any outline is drawn.
        /// </summary>
        public const string MaskName = "BlitzLegacy/Battle/Tank Outline Mask";

        public static readonly int Color = Shader.PropertyToID("_OutlineColor");
        public static readonly int Width = Shader.PropertyToID("_OutlineWidth");

        /// <summary><c>outline_switch</c> writes <c>outline_width = 4</c>.</summary>
        public const float DefaultWidth = 4f;

        /// <summary><c>Global.COLOR_ALLY</c>.</summary>
        public static readonly UnityEngine.Color AllyColor =
            new UnityEngine.Color(0.396f, 0.867f, 0f, 0f);

        /// <summary><c>Global.COLOR_ENEMY</c>.</summary>
        public static readonly UnityEngine.Color EnemyColor =
            new UnityEngine.Color(0.96f, 0f, 0f, 0f);

        /// <summary>The outline shader, or null when it is missing.</summary>
        public static Shader Load()
        {
            return Shader.Find(Name);
        }

        /// <summary>The stencil stamp shader, or null when it is missing.</summary>
        public static Shader LoadMask()
        {
            return Shader.Find(MaskName);
        }

        /// <summary>
        /// One outline material, born hidden: the source leaves the alpha at
        /// zero until the aim trace lands on the tank.
        /// </summary>
        public static Material NewMaterial(Shader shader)
        {
            if (shader == null) { return null; }

            var material = new Material(shader);
            material.name = "tank_outline";
            material.SetColor(Color, EnemyColor);
            material.SetFloat(Width, DefaultWidth);
            return material;
        }

        /// <summary>
        /// One stencil stamp material, born hidden like the outline it serves.
        /// It draws no colour, so it only ever costs the stencil buffer.
        /// </summary>
        public static Material NewMaskMaterial(Shader shader)
        {
            if (shader == null) { return null; }

            var material = new Material(shader);
            material.name = "tank_outline_mask";
            material.SetColor(Color, EnemyColor);
            material.SetFloat(Width, DefaultWidth);
            return material;
        }
    }
}
