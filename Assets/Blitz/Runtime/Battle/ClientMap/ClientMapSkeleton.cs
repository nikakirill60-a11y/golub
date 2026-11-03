using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>One joint of a skinned client prop, its bind pose in Unity axes.</summary>
    [Serializable]
    public sealed class ClientMapJointInfo
    {
        public string name;
        public string uid;
        public int parent = -1;
        public float[] position;
        public float[] rotation;
        public float scale = 1f;
        /// <summary>
        /// Row-major inverse bind matrix in the skeleton root's space, only
        /// written for a skeleton that drives soft-skinned meshes.
        /// </summary>
        public float[] inverseBind;
    }

    /// <summary>A node of the scene drawn by one joint.</summary>
    [Serializable]
    public sealed class ClientMapPartInfo
    {
        public string node;
        public int joint;
    }

    /// <summary>
    /// A skinned 11.20 prop: the entity's transform, its joints, the nodes
    /// they carry and when its MotionComponent clip plays.
    /// </summary>
    [Serializable]
    public sealed class ClientMapSkeletonInfo
    {
        public string name;
        public float[] origin;
        public float[] rotation;
        public float[] scale;
        public ClientMapJointInfo[] joints;
        public ClientMapPartInfo[] parts;
        /// <summary>Soft-skinned nodes drawn by a SkinnedMeshRenderer.</summary>
        public string[] skins;
        public int clip = -1;
        public float duration;
        public float playbackRate = 1f;
        public bool autoplay;
        public float delay;
        public float delayVariation;
        public int repeats;
    }

    /// <summary>One AnimationChannel of a client clip, keys in Unity axes.</summary>
    public sealed class ClientMapChannel
    {
        public const int Translation = 0;
        public const int Rotation = 1;
        public const int Scale = 2;
        public const int Linear = 0;
        public const int SphericalLinear = 1;

        public int Joint;
        public int Target;
        public int Interpolation;
        public int Dimension;
        public float[] Times;
        /// <summary>Dimension values per key.</summary>
        public float[] Values;

        /// <summary>
        /// AnimationChannel::Evaluate: the first key before the clip starts,
        /// the last after it ends, else the two keys around the time blended
        /// linearly or by DAVA's normalised slerp.
        /// </summary>
        public void Evaluate(float time, float[] result)
        {
            var count = Times.Length;
            var next = UpperBound(time);
            if (next == 0 || next == count)
            {
                Array.Copy(Values, (next == 0 ? 0 : count - 1) * Dimension, result, 0, Dimension);
                return;
            }

            var a = (next - 1) * Dimension;
            var b = next * Dimension;
            var t = (time - Times[next - 1]) / (Times[next] - Times[next - 1]);
            if (Interpolation == Linear || Dimension != 4)
            {
                for (var i = 0; i < Dimension; i++)
                {
                    result[i] = Values[a + i] + (Values[b + i] - Values[a + i]) * t;
                }
                return;
            }

            Slerp(Values, a, b, t, result);
        }

        /// <summary>The number of keys at or before the time.</summary>
        private int UpperBound(float time)
        {
            var low = 0;
            var high = Times.Length;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (Times[middle] <= time) low = middle + 1; else high = middle;
            }
            return low;
        }

        /// <summary>Quaternion::Slerp followed by Normalize.</summary>
        public static void Slerp(float[] values, int a, int b, float t, float[] result)
        {
            var cosom = 0f;
            for (var i = 0; i < 4; i++) cosom += values[a + i] * values[b + i];
            var sign = 1f;
            if (cosom < 0f)
            {
                cosom = -cosom;
                sign = -1f;
            }

            float scale0, scale1;
            if (1f - cosom > 0.05f)
            {
                var omega = Mathf.Acos(Mathf.Min(1f, cosom));
                var sinom = Mathf.Sin(omega);
                scale0 = Mathf.Sin((1f - t) * omega) / sinom;
                scale1 = Mathf.Sin(t * omega) / sinom;
            }
            else
            {
                scale0 = 1f - t;
                scale1 = t;
            }

            var length = 0f;
            for (var i = 0; i < 4; i++)
            {
                result[i] = scale0 * values[a + i] + scale1 * sign * values[b + i];
                length += result[i] * result[i];
            }
            length = Mathf.Sqrt(length);
            if (length <= 0f) return;
            for (var i = 0; i < 4; i++) result[i] /= length;
        }
    }

    /// <summary>A client clip: its length and channels.</summary>
    public sealed class ClientMapClip
    {
        public float Duration;
        public ClientMapChannel[] Channels;
    }

    /// <summary>
    /// Plays a skinned 11.20 prop's clip on a GameObject per joint, the way
    /// MotionSystem and ActionComponent time it: an action started when the
    /// scene loads waits `delay` plus a random share of `delayVariation`,
    /// time runs at `playbackRate`, a clip with `repeats` stops after that
    /// many plays and holds its last pose, one without loops forever. Until
    /// then the joints keep their bind pose.
    /// </summary>
    public sealed class ClientMapSkeleton : MonoBehaviour
    {
        public const string Magic = "BLTZANM1";

        public enum PlayState
        {
            Idle,
            Waiting,
            Playing,
            Stopped,
        }

        private ClientMapClip _clip;
        private bool[] _bound;
        private Vector3[] _bindPosition;
        private Quaternion[] _bindRotation;
        private float[] _bindScale;
        private float[] _value = new float[4];
        private Vector3[] _position;
        private Quaternion[] _rotation;
        private Vector3[] _scale;
        private float _duration;
        private float _rate;
        private int _repeatsLeft;
        private bool _loops;

        public ClientMapSkeletonInfo Info { get; private set; }
        public Transform[] Joints { get; private set; }
        public PlayState State { get; private set; }

        /// <summary>Seconds left before the clip starts.</summary>
        public float Wait { get; private set; }

        /// <summary>Clip time, before the playback rate is applied.</summary>
        public float Time { get; private set; }

        public ClientMapClip Clip
        {
            get { return _clip; }
        }

        /// <summary>Reads `BLTZANM1`; an empty resource has no clips.</summary>
        public static ClientMapClip[] LoadClips(string resource)
        {
            if (string.IsNullOrEmpty(resource))
            {
                return new ClientMapClip[0];
            }

            var asset = Resources.Load<TextAsset>(resource);
            if (asset == null)
            {
                throw new InvalidOperationException("missing client map clips: " + resource);
            }

            try
            {
                return ParseClips(asset.bytes);
            }
            finally
            {
                Resources.UnloadAsset(asset);
            }
        }

        public static ClientMapClip[] ParseClips(byte[] data)
        {
            var offset = 0;
            if (data == null || data.Length < 12 ||
                Encoding.ASCII.GetString(data, 0, Magic.Length) != Magic)
            {
                throw new InvalidOperationException("invalid client map clip payload");
            }
            offset += Magic.Length;
            var clips = new ClientMapClip[Int(data, ref offset)];
            for (var c = 0; c < clips.Length; c++)
            {
                var duration = Float(data, ref offset);
                var channels = new ClientMapChannel[Int(data, ref offset)];
                for (var i = 0; i < channels.Length; i++)
                {
                    var channel = new ClientMapChannel
                    {
                        Joint = Int(data, ref offset),
                        Target = Int(data, ref offset),
                        Interpolation = Int(data, ref offset),
                        Dimension = Int(data, ref offset),
                    };
                    var keys = Int(data, ref offset);
                    if (keys <= 0 || channel.Dimension <= 0 || channel.Dimension > 4)
                    {
                        throw new InvalidOperationException("invalid client map clip channel");
                    }
                    channel.Times = new float[keys];
                    channel.Values = new float[keys * channel.Dimension];
                    for (var k = 0; k < keys; k++)
                    {
                        channel.Times[k] = Float(data, ref offset);
                        for (var d = 0; d < channel.Dimension; d++)
                        {
                            channel.Values[k * channel.Dimension + d] = Float(data, ref offset);
                        }
                    }
                    channels[i] = channel;
                }
                clips[c] = new ClientMapClip { Duration = duration, Channels = channels };
            }

            if (offset != data.Length)
            {
                throw new InvalidOperationException("trailing client map clip bytes");
            }
            return clips;
        }

        private static int Int(byte[] data, ref int offset)
        {
            if (offset + 4 > data.Length)
            {
                throw new InvalidOperationException("client map clip payload is truncated");
            }
            var value = BitConverter.ToInt32(data, offset);
            offset += 4;
            return value;
        }

        private static float Float(byte[] data, ref int offset)
        {
            if (offset + 4 > data.Length)
            {
                throw new InvalidOperationException("client map clip payload is truncated");
            }
            var value = BitConverter.ToSingle(data, offset);
            offset += 4;
            return value;
        }

        /// <summary>
        /// The entity at its world transform, a GameObject per joint in its
        /// bind pose and every part node at identity under its joint.
        /// </summary>
        public static ClientMapSkeleton Build(
            Transform parent, ClientMapSkeletonInfo info, ClientMapClip[] clips,
            IDictionary<string, ClientMapNode> nodes)
        {
            if (info == null || info.joints == null || info.joints.Length == 0)
            {
                throw new InvalidOperationException("client map skeleton has no joints");
            }

            var root = new GameObject(info.name);
            root.transform.SetParent(parent, false);
            root.transform.localPosition = Vector(info.origin, Vector3.zero);
            root.transform.localRotation = Rotation(info.rotation);
            root.transform.localScale = Vector(info.scale, Vector3.one);

            var count = info.joints.Length;
            var joints = new Transform[count];
            var skeleton = root.AddComponent<ClientMapSkeleton>();
            skeleton._bindPosition = new Vector3[count];
            skeleton._bindRotation = new Quaternion[count];
            skeleton._bindScale = new float[count];
            for (var j = 0; j < count; j++)
            {
                var joint = info.joints[j];
                if (joint.parent >= j)
                {
                    throw new InvalidOperationException(
                        "client map joint precedes its parent: " + joint.name);
                }

                var go = new GameObject(joint.name);
                go.transform.SetParent(joint.parent < 0 ? root.transform : joints[joint.parent], false);
                skeleton._bindPosition[j] = Vector(joint.position, Vector3.zero);
                skeleton._bindRotation[j] = Rotation(joint.rotation);
                skeleton._bindScale[j] = joint.scale;
                joints[j] = go.transform;
            }
            skeleton.Joints = joints;
            skeleton.Info = info;

            if (info.parts != null)
            {
                for (var i = 0; i < info.parts.Length; i++)
                {
                    var part = info.parts[i];
                    ClientMapNode node;
                    if (!nodes.TryGetValue(part.node, out node) || part.joint < 0 || part.joint >= count)
                    {
                        throw new InvalidOperationException("client map skeleton part is missing: " + part.node);
                    }
                    node.transform.SetParent(joints[part.joint], false);
                }
            }

            if (info.skins != null && info.skins.Length > 0)
            {
                skeleton.BindSkins(root.transform, nodes);
            }

            if (info.clip >= 0)
            {
                if (info.clip >= clips.Length)
                {
                    throw new InvalidOperationException("client map skeleton clip is missing: " + info.name);
                }
                skeleton.Bind(clips[info.clip]);
            }
            skeleton.ResetPose();
            skeleton.Restart(UnityEngine.Random.value);
            return skeleton;
        }

        /// <summary>
        /// Hangs every soft-skinned node at identity under the root and binds
        /// its SkinnedMeshRenderer to the joints with the porter's inverse
        /// bind matrices, so the mesh follows the clip the way SkinnedMesh
        /// blends up to four joints in the client.
        /// </summary>
        private void BindSkins(Transform root, IDictionary<string, ClientMapNode> nodes)
        {
            var count = Joints.Length;
            var bindposes = new Matrix4x4[count];
            for (var j = 0; j < count; j++)
            {
                var values = Info.joints[j].inverseBind;
                if (values == null || values.Length != 16)
                {
                    throw new InvalidOperationException(
                        "client map joint has no inverse bind: " + Info.joints[j].name);
                }
                var m = new Matrix4x4();
                for (var r = 0; r < 4; r++)
                {
                    for (var c = 0; c < 4; c++)
                    {
                        m[r, c] = values[r * 4 + c];
                    }
                }
                bindposes[j] = m;
            }

            for (var i = 0; i < Info.skins.Length; i++)
            {
                ClientMapNode node;
                if (!nodes.TryGetValue(Info.skins[i], out node))
                {
                    throw new InvalidOperationException("client map skin is missing: " + Info.skins[i]);
                }
                var renderer = node.GetComponent<SkinnedMeshRenderer>();
                if (renderer == null || renderer.sharedMesh == null)
                {
                    throw new InvalidOperationException("client map skin has no skinned renderer: " + Info.skins[i]);
                }
                node.transform.SetParent(root, false);
                node.transform.localPosition = Vector3.zero;
                node.transform.localRotation = Quaternion.identity;
                node.transform.localScale = Vector3.one;
                var mesh = renderer.sharedMesh;
                mesh.bindposes = bindposes;
                renderer.bones = Joints;
                renderer.rootBone = root;
                var bounds = mesh.bounds;
                // Room for the clip to sway the mesh out of its bind pose.
                bounds.Expand(bounds.size.magnitude * 0.5f + 0.5f);
                renderer.localBounds = bounds;
            }
        }

        private void Bind(ClientMapClip clip)
        {
            var count = Joints.Length;
            _bound = new bool[count];
            for (var i = 0; i < clip.Channels.Length; i++)
            {
                var channel = clip.Channels[i];
                if (channel.Joint < 0 || channel.Joint >= count || channel.Target < 0 || channel.Target > 2)
                {
                    throw new InvalidOperationException("client map clip channel is unbound: " + Info.name);
                }
                _bound[channel.Joint] = true;
            }
            _clip = clip;
            _position = new Vector3[count];
            _rotation = new Quaternion[count];
            _scale = new Vector3[count];
            _duration = Info.duration > 0f ? Info.duration : clip.Duration;
            _rate = Info.playbackRate;
        }

        /// <summary>
        /// Starts the action over: `random` in [0, 1) picks the share of the
        /// delay variation, as RandFloat does once per scene in the client.
        /// </summary>
        public void Restart(float random)
        {
            Time = 0f;
            _repeatsLeft = Info.repeats;
            _loops = Info.repeats <= 0;
            if (_clip == null || !Info.autoplay)
            {
                State = PlayState.Idle;
                Wait = 0f;
                return;
            }

            State = PlayState.Waiting;
            Wait = Info.delay + Info.delayVariation * Mathf.Clamp01(random);
        }

        private void Update()
        {
            Advance(UnityEngine.Time.deltaTime);
        }

        /// <summary>Moves the action on by a frame of `deltaTime` seconds.</summary>
        public void Advance(float deltaTime)
        {
            if (State == PlayState.Waiting)
            {
                Wait -= deltaTime;
                if (Wait > 0f)
                {
                    return;
                }

                Wait = 0f;
                State = PlayState.Playing;
                Time = 0f;
                Evaluate(Time);
                return;
            }

            if (State != PlayState.Playing)
            {
                return;
            }

            Time += deltaTime * _rate;
            if (Time >= _duration)
            {
                if (!_loops)
                {
                    _repeatsLeft--;
                    if (_repeatsLeft <= 0)
                    {
                        State = PlayState.Stopped;
                    }
                }
                if (State == PlayState.Playing)
                {
                    Time = _duration > 0f ? Mathf.Repeat(Time, _duration) : 0f;
                }
            }
            Evaluate(Time);
        }

        /// <summary>Every joint back in its bind pose.</summary>
        public void ResetPose()
        {
            for (var j = 0; j < Joints.Length; j++)
            {
                Joints[j].localPosition = _bindPosition[j];
                Joints[j].localRotation = _bindRotation[j];
                Joints[j].localScale = Vector3.one * _bindScale[j];
            }
        }

        /// <summary>
        /// The clip's pose at a clip time: a joint the clip drives starts
        /// from identity and takes its channels, the others keep their bind.
        /// </summary>
        public void Evaluate(float time)
        {
            if (_clip == null)
            {
                return;
            }

            for (var j = 0; j < Joints.Length; j++)
            {
                _position[j] = Vector3.zero;
                _rotation[j] = Quaternion.identity;
                _scale[j] = Vector3.one;
            }

            for (var i = 0; i < _clip.Channels.Length; i++)
            {
                var channel = _clip.Channels[i];
                channel.Evaluate(time, _value);
                var j = channel.Joint;
                if (channel.Target == ClientMapChannel.Translation)
                {
                    _position[j] = new Vector3(_value[0], _value[1], _value[2]);
                }
                else if (channel.Target == ClientMapChannel.Rotation)
                {
                    _rotation[j] = new Quaternion(_value[0], _value[1], _value[2], _value[3]);
                }
                else
                {
                    _scale[j] = channel.Dimension >= 3
                        ? new Vector3(_value[0], _value[1], _value[2])
                        : Vector3.one * _value[0];
                }
            }

            for (var j = 0; j < Joints.Length; j++)
            {
                if (!_bound[j])
                {
                    continue;
                }
                Joints[j].localPosition = _position[j];
                Joints[j].localRotation = _rotation[j];
                Joints[j].localScale = _scale[j];
            }
        }

        private static Vector3 Vector(float[] values, Vector3 fallback)
        {
            return values != null && values.Length >= 3
                ? new Vector3(values[0], values[1], values[2])
                : fallback;
        }

        private static Quaternion Rotation(float[] values)
        {
            return values != null && values.Length >= 4
                ? new Quaternion(values[0], values[1], values[2], values[3])
                : Quaternion.identity;
        }
    }
}
