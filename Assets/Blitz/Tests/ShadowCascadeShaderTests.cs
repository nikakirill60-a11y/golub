using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// With shadow cascades the cascade has to be picked per pixel. A shadow
    /// coordinate computed in the vertex stage and interpolated mixes two
    /// atlas tiles across every triangle on a cascade border, which drew a
    /// bright, ragged ring through the shadows that followed the camera.
    /// </summary>
    public sealed class ShadowCascadeShaderTests
    {
        private static readonly Regex VertexShadowCoord =
            new Regex(@"output\.shadowCoord\s*=", RegexOptions.Compiled);

        [Test]
        public void NoProjectShaderInterpolatesTheMainLightShadowCoord()
        {
            var root = Path.Combine(Application.dataPath, "Blitz");
            var offenders = new System.Collections.Generic.List<string>();
            foreach (var pattern in new[] { "*.shader", "*.hlsl" })
            {
                foreach (var path in Directory.GetFiles(root, pattern, SearchOption.AllDirectories))
                {
                    if (VertexShadowCoord.IsMatch(File.ReadAllText(path)))
                    {
                        offenders.Add(path.Substring(root.Length + 1));
                    }
                }
            }

            Assert.That(offenders, Is.Empty,
                "compute TransformWorldToShadowCoord(positionWS) in the fragment");
        }
    }
}
