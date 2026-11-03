using System;
using System.Collections.Generic;
using System.Text;
using BlitzLegacy.Core.Maps;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>The VegetationRenderObject of an 11.20 scene, as the porter writes it.</summary>
    [Serializable]
    public sealed class ClientMapVegetationInfo
    {
        public string payload;
        public string albedo;
        public string colorMap;
        public int queue = 2000;
        public bool cull = true;
        /// <summary>Landscape width, depth and height in metres.</summary>
        public float[] worldSize;
        /// <summary>Side of one density cell.</summary>
        public float unit;
        /// <summary>Side of one pattern: four by four cells.</summary>
        public float pattern;
        public float cameraBias;
        /// <summary>Squared distance band (from, to] of each resolution.</summary>
        public float[] ranges;
        /// <summary>Squared distance the clusters end at and start to shrink at.</summary>
        public float[] visibility;
        public float baseColorMult = 1f;
        public bool fog = true;
        public int densitySide;
    }

    /// <summary>
    /// Draws the small clusters of an 11.20 VegetationRenderObject the way the
    /// client does. The landscape is cut into 4 x 4-cell nodes; the nodes and
    /// their halves and cells near the camera point (the camera moved
    /// `cameraBias` along its heading) are drawn with the pattern mesh of the
    /// matching resolution: whole patterns past the second LOD range, quarter
    /// patterns past the first, single cells closer in. grass-vp then lifts
    /// every cluster onto the terrain under its pivot, thins it by the colour
    /// map's density and shrinks the clusters a coarser resolution drops as
    /// the band ends, and the whole field as it nears the visibility distance.
    /// </summary>
    public sealed class ClientMapVegetation : MonoBehaviour
    {
        public const string Magic = "BLTZVEG1";
        public const int NodeCells = 4;
        public const int Resolutions = 3;

        /// <summary>The cells a draw of each resolution may cover.</summary>
        private static readonly int[] CellSquare = { 1, 4, 16 };

        private static readonly int TileId = Shader.PropertyToID("_Tile");

        /// <summary>One draw: a submesh of a resolution's pattern at a node.</summary>
        public struct Draw
        {
            public int Resolution;
            public int Submesh;
            /// <summary>Unity XZ of the pattern's corner.</summary>
            public Vector2 Origin;
            public float DistanceScale;
            public float LodMorph;
            public int SwitchLod;
            public float Distance;
        }

        private readonly List<Draw> _draws = new List<Draw>();
        private readonly List<UnityEngine.Object> _owned = new List<UnityEngine.Object>();
        private MaterialPropertyBlock _block;
        private int[] _densitySum;
        private int _side;
        private int _nodes;
        private Vector2 _min;
        private float _maxDistance;
        private float _fadeDistance;

        public ClientMapVegetationInfo Info { get; private set; }
        public Mesh[] Meshes { get; private set; }
        public Material Material { get; private set; }
        public Texture2D HeightTexture { get; private set; }

        /// <summary>Vertices of every resolution's pattern.</summary>
        public int VertexCount { get; private set; }

        /// <summary>Density cells that grow clusters.</summary>
        public int FilledCells { get; private set; }

        public IList<Draw> Draws
        {
            get { return _draws; }
        }

        public static bool Present(ClientMapVegetationInfo info)
        {
            return info != null && !string.IsNullOrEmpty(info.payload);
        }

        /// <summary>
        /// Builds the vegetation of a scene over its landscape. `origin` is
        /// the manifest's terrain corner; the height texture holds the same
        /// grid BattleTerrain builds the ground from.
        /// </summary>
        public static ClientMapVegetation Build(
            Transform parent, ClientMapVegetationInfo info, TerrainHeightField heights, float[] origin)
        {
            if (!Present(info))
            {
                throw new ArgumentException("client map vegetation is empty");
            }
            if (heights == null)
            {
                throw new InvalidOperationException("client map vegetation needs the landscape heights");
            }

            var asset = Resources.Load<TextAsset>(info.payload);
            if (asset == null)
            {
                throw new InvalidOperationException("missing client map vegetation: " + info.payload);
            }

            var go = new GameObject("Vegetation");
            go.transform.SetParent(parent, false);
            var vegetation = go.AddComponent<ClientMapVegetation>();
            try
            {
                vegetation.Info = info;
                vegetation.Parse(asset.bytes);
                vegetation.BuildHeights(heights, origin);
                vegetation.BuildMaterial();
            }
            catch
            {
                Release(go);
                throw;
            }
            finally
            {
                Resources.UnloadAsset(asset);
            }
            return vegetation;
        }

        private static void Release(UnityEngine.Object target)
        {
            if (Application.isPlaying) Destroy(target); else DestroyImmediate(target);
        }

        private void OnEnable()
        {
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
        }

        private void OnDestroy()
        {
            for (var i = 0; i < _owned.Count; i++)
            {
                if (_owned[i] != null) Release(_owned[i]);
            }
            _owned.Clear();
        }

        private void Parse(byte[] data)
        {
            var offset = 0;
            if (data.Length < 16 || Encoding.ASCII.GetString(data, 0, Magic.Length) != Magic)
            {
                throw new InvalidOperationException("invalid client map vegetation payload");
            }
            offset += Magic.Length;
            _side = Int(data, ref offset);
            if (_side != Info.densitySide || _side % NodeCells != 0 || offset + _side * _side > data.Length)
            {
                throw new InvalidOperationException("client map vegetation density map mismatch");
            }

            // A summed-area table: whether a node grows anything is then
            // four lookups.
            _densitySum = new int[(_side + 1) * (_side + 1)];
            for (var y = 0; y < _side; y++)
            {
                for (var x = 0; x < _side; x++)
                {
                    var filled = data[offset + y * _side + x] != 0 ? 1 : 0;
                    FilledCells += filled;
                    _densitySum[(y + 1) * (_side + 1) + x + 1] = filled +
                        _densitySum[y * (_side + 1) + x + 1] +
                        _densitySum[(y + 1) * (_side + 1) + x] -
                        _densitySum[y * (_side + 1) + x];
                }
            }
            offset += _side * _side;

            var count = Int(data, ref offset);
            if (count != Resolutions)
            {
                throw new InvalidOperationException("client map vegetation has " + count + " resolutions");
            }

            var height = Info.worldSize[2];
            Meshes = new Mesh[count];
            for (var r = 0; r < count; r++)
            {
                var vertices = Int(data, ref offset);
                var positions = Floats<Vector3>(data, ref offset, vertices, 3);
                var uvs = Floats<Vector2>(data, ref offset, vertices, 2);
                var pivots = Floats<Vector4>(data, ref offset, vertices, 4);
                var mesh = new Mesh
                {
                    name = "vegetation_r" + r,
                    indexFormat = vertices > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16,
                };
                _owned.Add(mesh);
                mesh.SetVertices(positions);
                mesh.SetUVs(0, uvs);
                mesh.SetUVs(1, pivots);
                var submeshes = Int(data, ref offset);
                if (submeshes != CellSquare[Resolutions - 1 - r])
                {
                    throw new InvalidOperationException("client map vegetation resolution " + r + " has " + submeshes + " cells");
                }
                mesh.subMeshCount = submeshes;
                for (var s = 0; s < submeshes; s++)
                {
                    var indices = Ints(data, ref offset, Int(data, ref offset));
                    mesh.SetIndices(indices, MeshTopology.Triangles, s, false);
                }

                // grass-vp lifts every cluster onto the ground, which the
                // pattern itself knows nothing of.
                mesh.bounds = new Bounds(
                    new Vector3(Info.pattern * 0.5f, height * 0.5f, Info.pattern * 0.5f),
                    new Vector3(Info.pattern + 2f, height + 10f, Info.pattern + 2f));
                Meshes[r] = mesh;
                VertexCount += vertices;
            }

            if (offset != data.Length)
            {
                throw new InvalidOperationException("trailing client map vegetation bytes");
            }

            _nodes = _side / NodeCells;
            _min = new Vector2(-Info.worldSize[0] * 0.5f, -Info.worldSize[1] * 0.5f);
            _maxDistance = Info.visibility[0];
            _fadeDistance = Info.visibility[1];
        }

        /// <summary>
        /// The landscape grid as an R32 texture laid along Unity X and Z:
        /// texel (i, j) sits at (origin.x + i * step, -origin.z - extent + j * step).
        /// Read with Load and blended by hand: GLES3 does not filter floats.
        /// </summary>
        private void BuildHeights(TerrainHeightField heights, float[] origin)
        {
            var side = heights.Side;
            var format = SystemInfo.SupportsTextureFormat(TextureFormat.RFloat)
                ? TextureFormat.RFloat : TextureFormat.RHalf;
            HeightTexture = new Texture2D(side, side, format, false, true)
            {
                name = "vegetation_heights",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            _owned.Add(HeightTexture);
            var lift = origin != null && origin.Length >= 2 ? origin[1] : 0f;
            if (format == TextureFormat.RFloat)
            {
                var texels = new float[side * side];
                for (var j = 0; j < side; j++)
                    for (var i = 0; i < side; i++)
                        texels[j * side + i] = heights.HeightAt(i, side - 1 - j) + lift;
                HeightTexture.SetPixelData(texels, 0);
            }
            else
            {
                var texels = new ushort[side * side];
                for (var j = 0; j < side; j++)
                    for (var i = 0; i < side; i++)
                        texels[j * side + i] = Mathf.FloatToHalf(heights.HeightAt(i, side - 1 - j) + lift);
                HeightTexture.SetPixelData(texels, 0);
            }
            HeightTexture.Apply(false, true);

            var x0 = origin != null && origin.Length >= 3 ? origin[0] : 0f;
            var z0 = origin != null && origin.Length >= 3 ? -origin[2] - heights.ExtentZ : 0f;
            HeightOrigin = new Vector2(x0, z0);
            HeightStep = new Vector2(heights.StepX, heights.StepZ);
            HeightSide = side;
        }

        public Vector2 HeightOrigin { get; private set; }
        public Vector2 HeightStep { get; private set; }
        public int HeightSide { get; private set; }

        private void BuildMaterial()
        {
            var shader = Shader.Find(ClientMapScene.VegetationShader);
            if (shader == null)
            {
                throw new InvalidOperationException("missing client map shader: " + ClientMapScene.VegetationShader);
            }

            Material = new Material(shader) { name = "vegetation", renderQueue = Info.queue };
            _owned.Add(Material);
            Material.SetTexture("_Albedo", LoadTexture(Info.albedo));
            Material.SetTexture("_ColorMap", LoadTexture(Info.colorMap));
            Material.SetTexture("_Heights", HeightTexture);
            Material.SetVector("_HeightGrid", new Vector4(
                HeightOrigin.x, HeightOrigin.y, 1f / HeightStep.x, 1f / HeightStep.y));
            Material.SetFloat("_HeightSide", HeightSide);
            Material.SetVector("_World", new Vector4(
                Info.worldSize[0], Info.worldSize[1], Info.baseColorMult, Info.fog ? 1f : 0f));
            Material.SetFloat("_Cull", Info.cull ? (float)CullMode.Back : (float)CullMode.Off);
            _block = new MaterialPropertyBlock();
        }

        private static Texture2D LoadTexture(string resource)
        {
            var texture = Resources.Load<Texture2D>(resource);
            if (texture == null)
            {
                throw new InvalidOperationException("missing client map texture: " + resource);
            }
            return texture;
        }

        private void OnBeginCamera(ScriptableRenderContext context, Camera camera)
        {
            if (camera == null || Material == null ||
                camera.cameraType != CameraType.Game && camera.cameraType != CameraType.SceneView)
            {
                return;
            }

            var view = camera.transform;
            Collect(view.position, view.forward);
            var layer = gameObject.layer;
            for (var i = 0; i < _draws.Count; i++)
            {
                var draw = _draws[i];
                _block.SetVector(TileId, new Vector4(
                    draw.DistanceScale, draw.LodMorph, draw.SwitchLod, 0f));
                var matrix = Matrix4x4.Translate(new Vector3(draw.Origin.x, 0f, draw.Origin.y));
                Graphics.DrawMesh(
                    Meshes[draw.Resolution], matrix, Material, layer, camera, draw.Submesh,
                    _block, ShadowCastingMode.Off, true);
            }
        }

        /// <summary>
        /// The client's quadtree walk for one camera: Unity XZ squared
        /// distances from the biased camera point to a node's corners pick
        /// its resolution band; a node is drawn whole once its band's cells
        /// cover it, else its quarters are tried, down to single cells.
        /// </summary>
        public IList<Draw> Collect(Vector3 cameraPosition, Vector3 cameraForward)
        {
            _draws.Clear();
            var heading = new Vector2(cameraForward.x, cameraForward.z);
            var point = new Vector2(cameraPosition.x, cameraPosition.z);
            if (heading.sqrMagnitude > 1e-8f)
            {
                point += heading.normalized * Info.cameraBias;
            }

            for (var nz = 0; nz < _nodes; nz++)
            {
                for (var nx = 0; nx < _nodes; nx++)
                {
                    var cellX = nx * NodeCells;
                    var cellZ = nz * NodeCells;
                    if (RectDistance(point, cellX, cellZ, NodeCells) > _maxDistance)
                    {
                        continue;
                    }
                    var origin = _min + new Vector2(cellX, cellZ) * Info.unit;
                    AddNode(point, origin, cellX, cellZ, NodeCells);
                }
            }
            return _draws;
        }

        private void AddNode(Vector2 point, Vector2 pattern, int cellX, int cellZ, int width)
        {
            if (!Filled(cellX, cellZ, width))
            {
                return;
            }

            var distance = CornerDistance(point, cellX, cellZ, width);
            var band = Band(distance);
            if (width == 1 || CellSquare[band] >= width * width)
            {
                if (distance > _maxDistance)
                {
                    return;
                }

                var resolution = width == 1 ? 0 : width == 2 ? 1 : 2;
                var perRow = NodeCells / width;
                var localX = (cellX % NodeCells) / width;
                var localZ = (cellZ % NodeCells) / width;
                var morph = Info.ranges[band * 2 + 1] > 0f
                    ? Mathf.Clamp01(1f - distance / Info.ranges[band * 2 + 1])
                    : 1f;
                var fade = distance > _fadeDistance && _maxDistance > _fadeDistance
                    ? Mathf.Clamp01(1f - (distance - _fadeDistance) / (_maxDistance - _fadeDistance))
                    : 1f;
                _draws.Add(new Draw
                {
                    Resolution = resolution,
                    Submesh = localX + localZ * perRow,
                    Origin = pattern,
                    DistanceScale = fade,
                    LodMorph = morph,
                    SwitchLod = Mathf.Min(band + 1, Resolutions - 1),
                    Distance = distance,
                });
                return;
            }

            var half = width / 2;
            for (var z = 0; z < 2; z++)
            {
                for (var x = 0; x < 2; x++)
                {
                    var childX = cellX + x * half;
                    var childZ = cellZ + z * half;
                    if (RectDistance(point, childX, childZ, half) <= _maxDistance)
                    {
                        AddNode(point, pattern, childX, childZ, half);
                    }
                }
            }
        }

        /// <summary>MapToResolution: the band (from, to] the distance falls in, else the first.</summary>
        public int Band(float distance)
        {
            for (var r = 0; r < Resolutions; r++)
            {
                if (distance > Info.ranges[r * 2] && distance <= Info.ranges[r * 2 + 1])
                {
                    return r;
                }
            }
            return 0;
        }

        private bool Filled(int cellX, int cellZ, int width)
        {
            var stride = _side + 1;
            var sum = _densitySum[(cellZ + width) * stride + cellX + width]
                - _densitySum[cellZ * stride + cellX + width]
                - _densitySum[(cellZ + width) * stride + cellX]
                + _densitySum[cellZ * stride + cellX];
            return sum > 0;
        }

        private float CornerDistance(Vector2 point, int cellX, int cellZ, int width)
        {
            var min = _min + new Vector2(cellX, cellZ) * Info.unit;
            var max = min + Vector2.one * (width * Info.unit);
            var best = (point - min).sqrMagnitude;
            best = Mathf.Min(best, (point - new Vector2(max.x, min.y)).sqrMagnitude);
            best = Mathf.Min(best, (point - new Vector2(min.x, max.y)).sqrMagnitude);
            return Mathf.Min(best, (point - max).sqrMagnitude);
        }

        private float RectDistance(Vector2 point, int cellX, int cellZ, int width)
        {
            var min = _min + new Vector2(cellX, cellZ) * Info.unit;
            var max = min + Vector2.one * (width * Info.unit);
            var dx = Mathf.Max(0f, Mathf.Max(min.x - point.x, point.x - max.x));
            var dz = Mathf.Max(0f, Mathf.Max(min.y - point.y, point.y - max.y));
            return dx * dx + dz * dz;
        }

        private static int Int(byte[] data, ref int offset)
        {
            if (offset + 4 > data.Length)
            {
                throw new InvalidOperationException("client map vegetation payload is truncated");
            }
            var value = BitConverter.ToInt32(data, offset);
            offset += 4;
            return value;
        }

        private static int[] Ints(byte[] data, ref int offset, int count)
        {
            if (count < 0 || count % 3 != 0 || offset + count * 4 > data.Length)
            {
                throw new InvalidOperationException("client map vegetation indices are truncated");
            }
            var result = new int[count];
            Buffer.BlockCopy(data, offset, result, 0, count * 4);
            offset += count * 4;
            return result;
        }

        private static T[] Floats<T>(byte[] data, ref int offset, int count, int components) where T : struct
        {
            var bytes = count * components * 4;
            if (count < 0 || offset + bytes > data.Length)
            {
                throw new InvalidOperationException("client map vegetation vertices are truncated");
            }
            var result = System.Runtime.InteropServices.MemoryMarshal
                .Cast<byte, T>(new ReadOnlySpan<byte>(data, offset, bytes)).ToArray();
            offset += bytes;
            return result;
        }
    }
}
