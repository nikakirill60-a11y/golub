using System;
using UnityEngine;

namespace BlitzLegacy.Runtime.Hangar
{
    /// <summary>Source SC2/DVAF data, exported by port_blitz_480_hangars.py.</summary>
    [Serializable] public sealed class ClientHangarDefinition
    {
        public string id, scene, meshResource;
        public float standHeight;
        public ClientHangarBatch[] batches;
        public ClientHangarMaterial[] materials;
        public ClientHangarEffect[] effects;
        public ClientHangarLight[] lights;
        public ClientHangarAnimation[] animations;
        public ClientHangarSound[] sounds;
        public ClientHangarEnvironment environment;
        public static ClientHangarDefinition Load(bool premium)
        {
            var id = premium ? "hangar_prem_2018" : "hangar";
            var asset = Resources.Load<TextAsset>("Hangars480/" + id + "/scene");
            if (asset == null) throw new InvalidOperationException("Missing 4.8 hangar: " + id);
            return JsonUtility.FromJson<ClientHangarDefinition>(asset.text);
        }
        public static Vector3 V(float[] a) { return new Vector3(a[0], a[1], a[2]); }
        public static Matrix4x4 M(float[] a)
        {
            var m = new Matrix4x4();
            for (var r = 0; r < 4; r++) for (var c = 0; c < 4; c++) m[r,c] = a[r*4+c];
            return m;
        }
    }
    [Serializable] public sealed class ClientHangarBatch
    {
        public string name; public int material, animation, vertexCount, triangleCount;
        public float[] min, max;
    }
    [Serializable] public sealed class ClientHangarMaterial
    {
        public string name, effect, albedo, secondary;
        public float secondaryMultiplier, cutoff;
        public float[] uvScale, uvOffset, tint;
        public bool alphaBlend, alphaTest, noCull;
    }
    [Serializable] public sealed class ClientHangarEnvironment
    {
        public float[] color, sky, sun;
        public float start, end, limit, distance, scattering, density;
        public bool atmosphere, linear;
    }
    [Serializable] public sealed class ClientHangarLight
    {
        public string name; public int type, flags;
        public float[] position, direction, color, ambient; public float intensity;
    }
    [Serializable] public sealed class ClientHangarSound
    {
        public string name, clip; public float[] position;
    }
    [Serializable] public sealed class ClientHangarKey
    {
        public float time; public float[] value;
    }
    [Serializable] public sealed class ClientHangarLayer
    {
        public string name, source, sourceLayer;
        public string[] frames;
        public bool single, additive, fog, fresnel, randomFrame;
        public float life, lifeVariation, number, velocity, velocityVariation,
            spin, spinVariation, angle, angleVariation, start, end, fresnelPower,
            fresnelBias, frameFps, drag;
        public int orientation;
        public float[] size, pivot;
        public ClientHangarKey[] sizeKeys, alphaKeys, colorKeys, forceKeys;
        public ClientHangarPlane[] planes;
    }
    [Serializable] public sealed class ClientHangarPlane
    {
        public float[] position, normal;
        public float bounceMin, bounceMax;
    }
    [Serializable] public sealed class ClientHangarEffect
    {
        public string name;
        public float[] position, direction, box, matrix;
        public float emissionAngle, duration;
        public ClientHangarLayer[] layers;
    }
    [Serializable] public sealed class ClientHangarJoint
    {
        public string uid; public int parent; public float[] bind, inverse;
    }
    [Serializable] public sealed class ClientHangarChannel
    {
        public string target; public ClientHangarKey[] keys;
    }
    [Serializable] public sealed class ClientHangarTrack
    {
        public string uid; public ClientHangarChannel[] channels;
    }
    [Serializable] public sealed class ClientHangarAnimation
    {
        public string name; public float[] matrix; public float duration, rate, delay;
        public ClientHangarJoint[] joints; public ClientHangarTrack[] tracks;
    }
}
