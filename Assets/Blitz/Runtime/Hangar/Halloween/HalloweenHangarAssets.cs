// Shared helpers of the runtime build of the ported hangars and the texture
// side of the port: the decoded client textures and particle sprites under
// Resources, the prefiltered IBL cube maps, the DFG lookup table and the stub
// texture the client binds for an empty texture slot. Each export (see
// PortedHangar) has its own Resources folder; the importer settings of its
// textures follow its import.json (BlitzLegacy.EditorTools.HalloweenHangarImport).
using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;

namespace BlitzLegacy.Runtime.Hangar.Halloween
{
    public static class HH
    {
        /// <summary>
        /// The export being built; the Halloween hangar until a build of
        /// another one starts.
        /// </summary>
        public static PortedHangar Export { get; private set; } = PortedHangar.Halloween2026;

        /// <summary>Resources folder of the export.</summary>
        public static string Root => Export.Root;

        /// <summary>Name prefix of the objects the build generates.</summary>
        public static string Generated => Root;

        public static string MapName => Export.MapName;

        public static readonly List<string> Report = new List<string>();
        public static readonly List<string> Warnings = new List<string>();

        static List<Object> owned;

        /// <summary>
        /// Starts a build of <paramref name="export"/>: the report is cleared
        /// and every object stored from now on is handed to
        /// <paramref name="sink"/>, whose owner destroys it.
        /// </summary>
        public static void Begin(List<Object> sink, PortedHangar export)
        {
            Export = export ?? throw new ArgumentNullException(nameof(export));
            owned = sink;
            Report.Clear();
            Warnings.Clear();
        }

        public static void End() => owned = null;

        public static void Log(string line) => Report.Add(line);

        public static void Warn(string line)
        {
            Warnings.Add(line);
            Report.Add("WARNING: " + line);
        }

        public static void Require(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }

        /// <summary>"Textures/a.png" -> "&lt;Root&gt;/Textures/a".</summary>
        public static string ResourcePath(string file)
        {
            int dot = file.LastIndexOf('.');
            int slash = file.LastIndexOf('/');
            return Root + "/" + (dot > slash ? file.Substring(0, dot) : file);
        }

        public static T Load<T>(string resource) where T : Object
        {
            var obj = Resources.Load<T>(resource);
            Require(obj != null, "Missing resource: " + resource);
            return obj;
        }

        /// <summary>
        /// A generated object: named after the asset the editor port stores it
        /// as, owned by the scene being built.
        /// </summary>
        public static T Store<T>(T obj, string path) where T : Object
        {
            int slash = path.LastIndexOf('/');
            string file = slash >= 0 ? path.Substring(slash + 1) : path;
            int dot = file.LastIndexOf('.');
            obj.name = dot > 0 ? file.Substring(0, dot) : file;
            owned?.Add(obj);
            return obj;
        }

        public static string Safe(string name)
        {
            var chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (!(char.IsLetterOrDigit(chars[i]) || chars[i] == '_' || chars[i] == '-'))
                    chars[i] = '_';
            return new string(chars);
        }
    }

    public static class HalloweenHangarAssets
    {
        public static string CubemapFolder => HH.Generated + "/Cubemaps";
        public static string TextureFolder => HH.Generated + "/Textures";

        static readonly Dictionary<string, Texture2D> loaded = new Dictionary<string, Texture2D>();
        static List<Object> unload;

        /// <summary>
        /// Starts a build: textures loaded from now on are listed in
        /// <paramref name="sink"/> so their owner can unload them again.
        /// </summary>
        public static void Begin(List<Object> sink)
        {
            loaded.Clear();
            unload = sink;
        }

        public static void End()
        {
            loaded.Clear();
            unload = null;
        }

        static Texture2D LoadTexture(string file)
        {
            if (loaded.TryGetValue(file, out var texture))
                return texture;
            texture = HH.Load<Texture2D>(HH.ResourcePath(file));
            loaded[file] = texture;
            unload?.Add(texture);
            return texture;
        }

        public static Texture2D Texture(HalloweenJson scene, int index)
        {
            var record = scene.Req("textures")[index];
            HH.Require(record.Exists, "Texture index " + index + " outside the texture table");
            return LoadTexture(record.Req("file").S());
        }

        public static Texture2D Sprite(HalloweenJson scene, int index)
        {
            var record = scene.Req("sprites")[index];
            HH.Require(record.Exists, "Sprite index " + index + " outside the sprite table");
            return LoadTexture(record.Req("file").S());
        }

        // ----------------------------------------------------------- cube maps
        // Whether Unity's cube face rows (SetPixels / SetPixelData order) run
        // against the GL face's t coordinate. Measured on the GPU by
        // HalloweenHangarProbes (editor) for this Unity version: they do not.
        public static bool CubeRowsFlipped;

        // Faces are stored in the client's (GL) face order, which is Unity's
        // CubemapFace order, as raw RGBA8 texels: mip by mip, face by face,
        // each face's rows as the client uploads them, row 0 at t = 0.
        public static Cubemap BuildCubemap(HalloweenJson record)
        {
            string name = record.Req("name").S();
            int size = record.Req("size").I();
            int mips = Math.Max(1, record.Req("mips").I(1));
            var asset = HH.Load<TextAsset>(HH.ResourcePath(record.Req("file").S()));
            try
            {
                var data = asset.GetData<byte>();
                var cube = new Cubemap(size, GraphicsFormat.R8G8B8A8_UNorm,
                    mips > 1 ? TextureCreationFlags.MipChain : TextureCreationFlags.None);
                HH.Require(cube.mipmapCount == mips,
                    $"Cube map {name}: Unity allocates {cube.mipmapCount} mips, the client has {mips}");
                int offset = 0;
                for (int mip = 0; mip < mips; mip++)
                {
                    int faceSize = Math.Max(1, size >> mip);
                    int faceBytes = faceSize * faceSize * 4;
                    for (int face = 0; face < 6; face++)
                    {
                        HH.Require(offset + faceBytes <= data.Length,
                            $"Cube map {name}: {data.Length} bytes end inside mip {mip} face {face}");
                        if (CubeRowsFlipped)
                        {
                            var flipped = FlipRows(data, offset, faceSize);
                            cube.SetPixelData(flipped, mip, (CubemapFace)face);
                        }
                        else
                        {
                            cube.SetPixelData(data, mip, (CubemapFace)face, offset);
                        }
                        offset += faceBytes;
                    }
                }
                HH.Require(offset == data.Length, $"Cube map {name}: {data.Length} bytes, the mip chain holds {offset}");
                cube.filterMode = mips > 1 ? FilterMode.Trilinear : FilterMode.Bilinear;
                cube.wrapMode = TextureWrapMode.Clamp;
                cube.anisoLevel = 1;
                cube.Apply(false, true);
                return HH.Store(cube, $"{CubemapFolder}/{name}.cubemap");
            }
            finally
            {
                Resources.UnloadAsset(asset);
            }
        }

        static byte[] FlipRows(NativeArray<byte> data, int offset, int size)
        {
            int row = size * 4;
            var result = new byte[row * size];
            for (int y = 0; y < size; y++)
                NativeArray<byte>.Copy(data, offset + y * row, result, (size - 1 - y) * row, row);
            return result;
        }

        // Shader-side HHEnvironmentDirection: Unity world -> client world (x, z, y), turned about z.
        public static Vector3 DavaLookup(Vector3 unity, float environmentRotation)
        {
            var v = new Vector3(unity.x, unity.z, unity.y);
            float a = 2f * Mathf.PI * environmentRotation;
            float s = Mathf.Sin(a), c = Mathf.Cos(a);
            return new Vector3(v.x * c + v.y * s, -v.x * s + v.y * c, v.z);
        }

        // Direction through the centre of texel (x, y) in SetPixels / GetPixels
        // order of a face as the GPU samples it.
        public static Vector3 TexelDirection(CubemapFace face, int x, int y, int size)
        {
            float u = (x + 0.5f) / size, v = (y + 0.5f) / size;
            return GLDirection(face, u, CubeRowsFlipped ? 1f - v : v);
        }

        // OpenGL's cube map table read backwards: the direction of face
        // coordinates (s, t) in [0, 1], t = 0 being the face's first row.
        public static Vector3 GLDirection(CubemapFace face, float u, float v)
        {
            float s = 2f * u - 1f;
            float t = 2f * v - 1f;
            switch (face)
            {
                case CubemapFace.PositiveX: return new Vector3(1f, -t, -s).normalized;
                case CubemapFace.NegativeX: return new Vector3(-1f, -t, s).normalized;
                case CubemapFace.PositiveY: return new Vector3(s, 1f, t).normalized;
                case CubemapFace.NegativeY: return new Vector3(s, -1f, -t).normalized;
                case CubemapFace.PositiveZ: return new Vector3(s, -t, 1f).normalized;
                default: return new Vector3(-s, -t, -1f).normalized;
            }
        }

        public static Color SampleCube(Color[][] faces, int size, Vector3 d)
        {
            float ax = Mathf.Abs(d.x), ay = Mathf.Abs(d.y), az = Mathf.Abs(d.z);
            CubemapFace face;
            float s, t, m;
            if (ax >= ay && ax >= az)
            {
                m = ax;
                if (d.x > 0) { face = CubemapFace.PositiveX; s = -d.z; t = -d.y; }
                else { face = CubemapFace.NegativeX; s = d.z; t = -d.y; }
            }
            else if (ay >= az)
            {
                m = ay;
                if (d.y > 0) { face = CubemapFace.PositiveY; s = d.x; t = d.z; }
                else { face = CubemapFace.NegativeY; s = d.x; t = -d.z; }
            }
            else
            {
                m = az;
                if (d.z > 0) { face = CubemapFace.PositiveZ; s = d.x; t = -d.y; }
                else { face = CubemapFace.NegativeZ; s = -d.x; t = -d.y; }
            }
            float u = (s / m + 1f) * 0.5f * size - 0.5f;
            float v = (t / m + 1f) * 0.5f * size - 0.5f;
            if (CubeRowsFlipped)
                v = size - 1 - v;
            int x0 = Mathf.Clamp(Mathf.FloorToInt(u), 0, size - 1), y0 = Mathf.Clamp(Mathf.FloorToInt(v), 0, size - 1);
            int x1 = Mathf.Min(x0 + 1, size - 1), y1 = Mathf.Min(y0 + 1, size - 1);
            float fx = Mathf.Clamp01(u - x0), fy = Mathf.Clamp01(v - y0);
            var p = faces[(int)face];
            var a = Color.Lerp(p[y0 * size + x0], p[y0 * size + x1], fx);
            var b = Color.Lerp(p[y1 * size + x0], p[y1 * size + x1], fx);
            return Color.Lerp(a, b, fy);
        }

        // ----------------------------------------------------------- DFG LUT
        // Split-sum DFG table of the client's PBR shader, RG half floats.
        public static Texture2D BuildDfg(HalloweenJson scene)
        {
            var info = scene.Req("environment").Req("dfgLut");
            int size = info.Req("size").I();
            HH.Require(info["format"].S("RGHalf") == "RGHalf", "dfg_lut: format " + info["format"].S());
            var asset = HH.Load<TextAsset>(HH.ResourcePath(info.Req("file").S()));
            try
            {
                var data = asset.GetData<byte>();
                HH.Require(data.Length == size * size * 4, $"dfg_lut.bytes: {data.Length} bytes, expected {size * size * 4}");
                var texture = new Texture2D(size, size, GraphicsFormat.R16G16_SFloat, TextureCreationFlags.None);
                texture.SetPixelData(data, 0);
                texture.filterMode = FilterMode.Bilinear;
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.anisoLevel = 0;
                texture.Apply(false, true);
                return HH.Store(texture, TextureFolder + "/dfg_lut.asset");
            }
            finally
            {
                Resources.UnloadAsset(asset);
            }
        }

        // The client binds its pink stub texture to a slot the material leaves empty.
        public static Texture2D StubTexture()
        {
            var texture = new Texture2D(4, 4, TextureFormat.RGBA32, false, true);
            var pixels = new Color32[16];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Color32(255, 0, 255, 255);
            texture.SetPixels32(pixels);
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Repeat;
            texture.Apply(false, true);
            return HH.Store(texture, TextureFolder + "/stub_pink.asset");
        }
    }
}
