using System;
using System.Collections.Generic;
using UnityEngine;

namespace BlitzLegacy.Runtime.Hangar
{
    /// <summary>Evaluates original DVAF channels and SC2 inverse bind matrices.
    /// The tiny ambient rigs use CPU skinning, without requiring Animator assets
    /// or fabricating motion. Unity-space placement is applied once at the end.</summary>
    public sealed class ClientHangarMotion : MonoBehaviour
    {
        private sealed class Part
        {
            public Mesh Mesh; public Vector3[] Bind, Normal, Vertices, Normals;
            public int[] Joints; public float[] Weights;
        }
        private readonly List<Part> _parts = new List<Part>();
        private ClientHangarAnimation _source;
        private Matrix4x4 _placement;
        private Matrix4x4[] _bind, _inverse, _pose, _skin;
        private ClientHangarTrack[] _tracks;
        private float _elapsed;
        public float Duration { get { return _source.duration; } }

        public void Configure(ClientHangarAnimation source)
        {
            _source = source; _placement = ClientHangarDefinition.M(source.matrix);
            var count = source.joints.Length;
            _bind = new Matrix4x4[count]; _inverse = new Matrix4x4[count];
            _pose = new Matrix4x4[count]; _skin = new Matrix4x4[count];
            _tracks = new ClientHangarTrack[count];
            for (var i=0;i<count;i++)
            {
                _bind[i]=ClientHangarDefinition.M(source.joints[i].bind);
                _inverse[i]=ClientHangarDefinition.M(source.joints[i].inverse);
                for (var j=0;j<source.tracks.Length;j++)
                    if(source.tracks[j].uid==source.joints[i].uid) _tracks[i]=source.tracks[j];
            }
        }
        public void Bind(Mesh mesh, Vector3[] vertices, Vector3[] normals, int[] joints, float[] weights)
        {
            mesh.MarkDynamic();
            _parts.Add(new Part { Mesh=mesh, Bind=vertices, Normal=normals,
                Vertices=new Vector3[vertices.Length], Normals=new Vector3[normals.Length], Joints=joints, Weights=weights });
            Sample(0f);
        }
        private void LateUpdate()
        {
            _elapsed += Time.unscaledDeltaTime;
            Sample(Mathf.Max(0f,_elapsed-_source.delay)*_source.rate);
        }
        public void Sample(float seconds)
        {
            var time = Mathf.Repeat(seconds,Mathf.Max(0.001f,_source.duration));
            for(var i=0;i<_pose.Length;i++)
            {
                var bind=_bind[i];
                var p=new Vector3(bind.m03,bind.m13,bind.m23);
                var scale=bind.lossyScale;
                var q=bind.rotation;
                var track=_tracks[i];
                if(track!=null) foreach(var channel in track.channels)
                {
                    var keys=channel.keys; if(keys.Length==0) continue;
                    int right=0; while(right<keys.Length-1 && keys[right].time<time)right++;
                    var left=Mathf.Max(0,right-1); var span=keys[right].time-keys[left].time;
                    var t=span>0?Mathf.Clamp01((time-keys[left].time)/span):0;
                    var a=keys[left].value; var b=keys[right].value;
                    switch(channel.target)
                    {
                        case "translation": p=Vector3.Lerp(ClientHangarDefinition.V(a),ClientHangarDefinition.V(b),t); break;
                        case "rotation": q=Quaternion.Slerp(new Quaternion(a[0],a[1],a[2],a[3]),new Quaternion(b[0],b[1],b[2],b[3]),t); break;
                        case "scale": scale=Vector3.one*Mathf.Lerp(a[0],b[0],t); break;
                    }
                }
                var local=Matrix4x4.TRS(p,q,scale);
                var parent=_source.joints[i].parent;
                _pose[i]=parent>=0?_pose[parent]*local:local;
                _skin[i]=_placement*_pose[i]*_inverse[i];
            }
            foreach(var part in _parts)
            {
                for(var i=0;i<part.Bind.Length;i++)
                {
                    var v=Vector3.zero; var n=Vector3.zero;
                    for(var j=0;j<4;j++)
                    {
                        var at=i*4+j; var w=part.Weights[at]; if(w<=0)continue;
                        var matrix=_skin[part.Joints[at]];
                        v+=matrix.MultiplyPoint3x4(part.Bind[i])*w;
                        n+=matrix.MultiplyVector(part.Normal[i])*w;
                    }
                    part.Vertices[i]=v;part.Normals[i]=n.normalized;
                }
                part.Mesh.vertices=part.Vertices;part.Mesh.normals=part.Normals;
                part.Mesh.RecalculateBounds();
            }
        }
    }
}
