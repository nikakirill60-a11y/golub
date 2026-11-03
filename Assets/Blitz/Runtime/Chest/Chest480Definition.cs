using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BlitzLegacy.Runtime.Chest
{
    /// <summary>
    /// The ported Blitz 4.8.0 container-opening scene (3d/Maps/chest/Box_anim.sc2),
    /// as written by Tools/port_blitz_480_chest.py into Resources/Chest480/.
    /// Every vector and matrix here stays in DAVA space: right-handed, Z up,
    /// row-vector matrices with the translation in row 3. Only meshes.bytes is
    /// already converted to Unity axes (x, z, y).
    /// </summary>
    [Serializable]
    public sealed class Chest480Definition
    {
        public const string SceneResource = "Chest480/scene";
        public const int PartCount = 6;
        public const int SampleCount = 42;
        public const int SpriteCount = 12;
        public const int EmitterCount = 19;
        public const int EffectCount = 19;
        public const int MeshCount = 7;
        private const string MeshMagic = "BLTZCHS1";

        public string id;
        public string scene;
        public string meshResource;
        public Chest480Camera camera;
        public Chest480Light light;
        public float[] hardpoint;
        public Chest480Background background;
        public Chest480Box box;
        public Chest480Part[] parts;
        public Chest480Sample[] samples;
        public float boxDuration;
        public string gradeVariable;
        public Chest480Sprite[] sprites;
        public Chest480Emitter[] emitters;
        public Chest480Effect[] effects;

        private static Chest480Definition _cached;

        /// <summary>The validated scene, parsed once per domain.</summary>
        public static Chest480Definition Load()
        {
            if (_cached != null) return _cached;
            var asset = Resources.Load<TextAsset>(SceneResource);
            if (asset == null) throw new InvalidDataException("Missing resource " + SceneResource);
            var definition = Parse(asset.text);
            _cached = definition;
            return definition;
        }

        public static Chest480Definition Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) throw new InvalidDataException("Empty chest scene");
            var definition = JsonUtility.FromJson<Chest480Definition>(json);
            if (definition == null) throw new InvalidDataException("Unreadable chest scene");
            definition.Validate();
            return definition;
        }

        /// <summary>Reject a truncated or foreign export before anything is simulated.</summary>
        public void Validate()
        {
            Require(id == "chest", "id");
            Require(camera != null && Length(camera.position, 3) && Length(camera.target, 3) && Length(camera.up, 3), "camera");
            Require(camera.fovX > 1f && camera.fovX < 179f && camera.znear > 0f && camera.zfar > camera.znear, "camera range");
            Require(light != null && Length(light.position, 3) && Length(light.color, 3) && Length(light.ambient, 3), "light");
            Require(Length(hardpoint, 3), "hardpoint");
            Require(background != null && background.texture != null && background.mesh == 0, "background");
            Require(box != null && box.albedo != null && box.normal != null && Length(box.fresnel, 3), "box material");
            Require(parts != null && parts.Length == PartCount, "parts");
            foreach (var part in parts)
            {
                Require(part != null && part.mesh >= 1 && part.mesh < MeshCount, "part mesh");
                Require(Length(part.local, 16) && Length(part.parentWorld, 16) && Length(part.invPose, 16), "part matrices");
                var keys = part.times == null ? 0 : part.times.Length;
                Require(keys > 0 && Length(part.translations, keys * 3) && Length(part.rotations, keys * 4)
                    && Length(part.scales, keys * 3), "part keys " + part.name);
                for (var i = 1; i < keys; i++) Require(part.times[i] > part.times[i - 1], "part key order " + part.name);
            }
            Require(samples != null && samples.Length == SampleCount, "samples");
            foreach (var sample in samples) Require(sample.part >= 0 && sample.part < PartCount && Length(sample.world, 16), "sample");
            Require(boxDuration > 0f, "boxDuration");
            Require(!string.IsNullOrEmpty(gradeVariable), "gradeVariable");
            Require(sprites != null && sprites.Length == SpriteCount, "sprites");
            foreach (var sprite in sprites) Require(sprite.frames != null && sprite.frames.Length == 1, "sprite frames " + sprite.name);
            Require(emitters != null && emitters.Length == EmitterCount, "emitters");
            foreach (var emitter in emitters)
            {
                Require(emitter.type >= 0 && emitter.type <= 3, "emitter type " + emitter.name);
                Require(emitter.layers != null && emitter.layers.Length > 0, "emitter layers " + emitter.name);
                ValidateLine(emitter.emissionVector, 3, emitter.name);
                ValidateLine(emitter.emissionRange, 1, emitter.name);
                ValidateLine(emitter.emissionAngle, 1, emitter.name);
                ValidateLine(emitter.radius, 1, emitter.name);
                ValidateLine(emitter.size, 3, emitter.name);
                ValidateLine(emitter.colorOverLife, 4, emitter.name);
                foreach (var layer in emitter.layers)
                {
                    var where = emitter.name + "/" + layer.name;
                    Require(layer.sprite >= 0 && layer.sprite < SpriteCount, "layer sprite " + where);
                    Require(layer.blending == 1 || layer.blending == 3, "layer blending " + where);
                    Require(layer.orientation > 0 && layer.orientation < 32, "layer orientation " + where);
                    Require(Length(layer.pivot, 2) && Length(layer.activeLods, 4), "layer arrays " + where);
                    Require(layer.endTime >= layer.startTime, "layer time " + where);
                    foreach (var line in layer.Lines()) ValidateLine(line.Key, line.Value, where);
                    Require(layer.forces != null, "layer forces " + where);
                    foreach (var force in layer.forces)
                    {
                        ValidateLine(force.force, 3, where);
                        ValidateLine(force.overLife, 1, where);
                    }
                }
            }
            Require(effects != null && effects.Length == EffectCount, "effects");
            foreach (var effect in effects)
            {
                Require(Length(effect.world, 16), "effect world " + effect.name);
                Require(effect.lod >= 0 && effect.lod < 4, "effect lod " + effect.name);
                Require(effect.delay >= 0f && effect.duration > 0f, "effect timing " + effect.name);
                Require(effect.instances != null && effect.instances.Length > 0, "effect instances " + effect.name);
                foreach (var instance in effect.instances)
                    Require(instance.emitter >= 0 && instance.emitter < EmitterCount && Length(instance.position, 3),
                        "effect instance " + effect.name);
            }
        }

        private static void ValidateLine(Chest480Line line, int dim, string where)
        {
            Require(line != null && line.dim == dim, "property line dimension in " + where);
            var keys = line.t == null ? 0 : line.t.Length;
            Require(Length(line.v, keys * dim), "property line values in " + where);
            for (var i = 1; i < keys; i++) Require(line.t[i] >= line.t[i - 1], "property line order in " + where);
            if (!string.IsNullOrEmpty(line.external))
            {
                Require(keys > 0, "modifiable line without values in " + where);
                var modifiers = line.mt == null ? 0 : line.mt.Length;
                Require(Length(line.mv, modifiers * dim), "modification line in " + where);
            }
        }

        private static bool Length(float[] values, int count)
        {
            if (values == null || values.Length != count) return false;
            foreach (var value in values) if (float.IsNaN(value) || float.IsInfinity(value)) return false;
            return true;
        }

        private static bool Length(int[] values, int count)
        {
            return values != null && values.Length == count;
        }

        private static void Require(bool condition, string what)
        {
            if (!condition) throw new InvalidDataException("Invalid chest scene: " + what);
        }

        /// <summary>
        /// Read meshes.bytes. Vertices are already in Unity axes and the winding
        /// is already flipped; binormals ride in uv1 for the box shader.
        /// </summary>
        public static Mesh[] ReadMeshes(byte[] data)
        {
            if (data == null || data.Length < 12) throw new InvalidDataException("Chest meshes are truncated");
            using (var reader = new BinaryReader(new MemoryStream(data, false)))
            {
                var magic = new string(reader.ReadChars(MeshMagic.Length));
                if (magic != MeshMagic) throw new InvalidDataException("Unknown chest mesh format");
                var count = reader.ReadInt32();
                if (count != MeshCount) throw new InvalidDataException("Unexpected chest mesh count " + count);
                var meshes = new Mesh[count];
                for (var m = 0; m < count; m++)
                {
                    var vertices = reader.ReadInt32();
                    var indices = reader.ReadInt32();
                    var remaining = data.Length - reader.BaseStream.Position;
                    if (vertices <= 0 || vertices > 65535 || indices <= 0 || indices % 3 != 0
                        || (long)vertices * 14 * 4 + (long)indices * 4 > remaining)
                        throw new InvalidDataException("Corrupt chest mesh " + m);
                    var positions = ReadVectors(reader, vertices);
                    var normals = ReadVectors(reader, vertices);
                    var tangents = ReadVectors(reader, vertices);
                    var binormals = ReadVectors(reader, vertices);
                    var uv = new Vector2[vertices];
                    for (var i = 0; i < vertices; i++) uv[i] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                    var triangles = new int[indices];
                    for (var i = 0; i < indices; i++)
                    {
                        triangles[i] = reader.ReadInt32();
                        if (triangles[i] < 0 || triangles[i] >= vertices) throw new InvalidDataException("Chest mesh index out of range");
                    }
                    var mesh = new Mesh { name = m == 0 ? "chest480_background" : "chest480_box0" + m };
                    mesh.vertices = positions;
                    mesh.normals = normals;
                    var tangents4 = new Vector4[vertices];
                    for (var i = 0; i < vertices; i++) tangents4[i] = new Vector4(tangents[i].x, tangents[i].y, tangents[i].z, 1f);
                    mesh.tangents = tangents4;
                    mesh.uv = uv;
                    mesh.SetUVs(1, new List<Vector3>(binormals));
                    mesh.triangles = triangles;
                    mesh.RecalculateBounds();
                    mesh.UploadMeshData(true);
                    meshes[m] = mesh;
                }
                return meshes;
            }
        }

        private static Vector3[] ReadVectors(BinaryReader reader, int count)
        {
            var result = new Vector3[count];
            for (var i = 0; i < count; i++) result[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            return result;
        }
    }

    [Serializable]
    public sealed class Chest480Camera
    {
        public float[] position;
        public float[] target;
        public float[] up;
        public float fovX;
        public float znear;
        public float zfar;
    }

    [Serializable]
    public sealed class Chest480Light
    {
        public float[] position;
        public float[] color;
        public float[] ambient;
        public int type;
    }

    [Serializable]
    public sealed class Chest480Texture
    {
        public string resource;
        public int wrap;
        public int wrapT;
        public int minFilter;
        public int magFilter;
        public int mipFilter;
    }

    [Serializable]
    public sealed class Chest480Background
    {
        public int mesh;
        public Chest480Texture texture;
    }

    [Serializable]
    public sealed class Chest480Box
    {
        public Chest480Texture albedo;
        public Chest480Texture normal;
        public float glossiness;
        public float specularity;
        public float[] fresnel;
        public float normalScale;
    }

    [Serializable]
    public sealed class Chest480Part
    {
        public string name;
        public int mesh;
        public float[] local;
        public float[] parentWorld;
        public float[] invPose;
        public float duration;
        public float[] times;
        public float[] translations;
        public float[] rotations;
        public float[] scales;
    }

    [Serializable]
    public sealed class Chest480Sample
    {
        public int part;
        public float time;
        public float[] world;
    }

    [Serializable]
    public sealed class Chest480Sprite
    {
        public string name;
        public string[] frames;
    }

    /// <summary>One serialized DAVA property line; an empty <c>t</c> is a null line.</summary>
    [Serializable]
    public sealed class Chest480Line
    {
        public int dim;
        public float[] t;
        public float[] v;
        public string external;
        public float[] mt;
        public float[] mv;
    }

    [Serializable]
    public sealed class Chest480Force
    {
        public Chest480Line force;
        public Chest480Line overLife;
    }

    [Serializable]
    public sealed class Chest480Layer
    {
        public string name;
        public int sprite;
        public int single;
        public int blending;
        public int orientation;
        public int isLong;
        public float scaleVelocityBase;
        public float scaleVelocityFactor;
        public float[] pivot;
        public float startTime;
        public float endTime;
        public int isLooped;
        public float deltaTime;
        public float deltaVariation;
        public float loopVariation;
        public float loopEndTime;
        public int inheritPosition;
        public int randomSpinDirection;
        public int[] activeLods;
        public int degradeStrategy;
        public Chest480Line life;
        public Chest480Line lifeVariation;
        public Chest480Line number;
        public Chest480Line numberVariation;
        public Chest480Line size;
        public Chest480Line sizeVariation;
        public Chest480Line sizeOverLife;
        public Chest480Line velocity;
        public Chest480Line velocityVariation;
        public Chest480Line velocityOverLife;
        public Chest480Line spin;
        public Chest480Line spinVariation;
        public Chest480Line angle;
        public Chest480Line angleVariation;
        public Chest480Line colorRandom;
        public Chest480Line colorOverLife;
        public Chest480Line alphaOverLife;
        public Chest480Force[] forces;

        /// <summary>Every property line with its dimension, for validation.</summary>
        public IEnumerable<KeyValuePair<Chest480Line, int>> Lines()
        {
            yield return new KeyValuePair<Chest480Line, int>(life, 1);
            yield return new KeyValuePair<Chest480Line, int>(lifeVariation, 1);
            yield return new KeyValuePair<Chest480Line, int>(number, 1);
            yield return new KeyValuePair<Chest480Line, int>(numberVariation, 1);
            yield return new KeyValuePair<Chest480Line, int>(size, 2);
            yield return new KeyValuePair<Chest480Line, int>(sizeVariation, 2);
            yield return new KeyValuePair<Chest480Line, int>(sizeOverLife, 2);
            yield return new KeyValuePair<Chest480Line, int>(velocity, 1);
            yield return new KeyValuePair<Chest480Line, int>(velocityVariation, 1);
            yield return new KeyValuePair<Chest480Line, int>(velocityOverLife, 1);
            yield return new KeyValuePair<Chest480Line, int>(spin, 1);
            yield return new KeyValuePair<Chest480Line, int>(spinVariation, 1);
            yield return new KeyValuePair<Chest480Line, int>(angle, 1);
            yield return new KeyValuePair<Chest480Line, int>(angleVariation, 1);
            yield return new KeyValuePair<Chest480Line, int>(colorRandom, 4);
            yield return new KeyValuePair<Chest480Line, int>(colorOverLife, 4);
            yield return new KeyValuePair<Chest480Line, int>(alphaOverLife, 1);
        }
    }

    [Serializable]
    public sealed class Chest480Emitter
    {
        public string name;
        public string source;
        public int type;
        public int shockwave;
        public Chest480Line emissionVector;
        public Chest480Line emissionRange;
        public Chest480Line emissionAngle;
        public Chest480Line radius;
        public Chest480Line size;
        public Chest480Line colorOverLife;
        public Chest480Layer[] layers;
    }

    [Serializable]
    public sealed class Chest480Instance
    {
        public int emitter;
        public float[] position;
    }

    [Serializable]
    public sealed class Chest480Effect
    {
        public string name;
        public float[] world;
        public float delay;
        public int byAction;
        public double repeats;
        public int stopWhenEmpty;
        public float duration;
        public int clearOnRestart;
        public int lod;
        public Chest480Instance[] instances;
    }
}
