using System;
using System.IO;
using BlitzLegacy.Core.Hangar;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Runtime.Hangar;
using BlitzLegacy.Runtime.Hangar.Halloween;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    public static class ClientHangarRenderCli
    {
        public static void Render()
        {
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
                ClientHangarFogProbe.Validate();
                var folder="Logs/hangar480-render";Directory.CreateDirectory(folder);
                foreach(var premium in new[]{false,true})
                {
                    var world=HangarWorld.Create(null,new SettingsData(),premium:premium);
                    DynamicGI.UpdateEnvironment();
                    var label=premium?"premium":"ordinary";
                    foreach(var effect in world.GetComponentsInChildren<ParticleSystem>(true))
                    {effect.Simulate(6f,true,true,true);effect.Pause();}
                    foreach(var motion in world.GetComponentsInChildren<ClientHangarMotion>(true))motion.Sample(6f);
                    foreach(var yaw in new[]{-145f,135f,45f})
                    {
                        world.Orbit.SetView(yaw,-12f);
                        Capture(world.HangarCamera,folder+"/"+label+"_"+yaw+".png");
                    }
                    world.Orbit.SetView(-145,-12);
                    foreach(var effect in world.GetComponentsInChildren<ParticleSystem>(true))effect.gameObject.SetActive(false);
                    Capture(world.HangarCamera,folder+"/"+label+"_clean.png");
                    Debug.Log("[Blitz] source hangar render "+label+" batches="+world.BakedInstanceCount+" particles="+world.ClientScene.ParticleLayerCount+" bounds="+world.ClientScene.GeometryBounds);
                    UnityEngine.Object.DestroyImmediate(world.gameObject);
                }
                if (ShaderUtil.ShaderHasError(Shader.Find(ClientHangarScene.SurfaceShader)) ||
                    ShaderUtil.ShaderHasError(Shader.Find(ClientHangarScene.ParticleShader)))
                    throw new InvalidOperationException("Hangar shader compilation failed");
                RenderHalloween(folder);
                RenderMiku(folder);
                EditorApplication.Exit(0);
            }
            catch(Exception exception){Debug.LogException(exception);EditorApplication.Exit(1);}
        }
        /// <summary>
        /// The 11.21 Halloween 2026 hangar: the GPU conventions its runtime
        /// pins, then the stand from the 4.8 opening view and around it, and
        /// the frame of the client's own hangar camera for comparison with 11.21,
        /// then close-ups of the logos the port replaces with its own art.
        /// </summary>
        private static void RenderHalloween(string folder)
        {
            foreach (var name in HalloweenHangarScene.ShaderNames)
            {
                var shader = Shader.Find(name);
                if (shader == null || ShaderUtil.ShaderHasError(shader))
                    throw new InvalidOperationException("Halloween hangar shader " + name + " is missing or failed to compile");
            }
            Debug.Log("[Blitz] " + HalloweenHangarProbes.CubeRows());
            Debug.Log("[Blitz] " + HalloweenHangarProbes.Particles());

            var world = HangarWorld.Create(null, new SettingsData(), kind: HangarKind.Halloween2026);
            try
            {
                var scene = world.PortedScene;
                if (scene == null) throw new InvalidOperationException("The Halloween hangar did not come up");
                File.WriteAllLines(folder + "/halloween_report.txt", scene.Report);
                foreach (var warning in scene.Warnings) Debug.LogWarning("[Blitz] 11.21 hangar: " + warning);
                DynamicGI.UpdateEnvironment();
                // Edit mode advances neither particles nor legacy animations.
                foreach (var effect in world.GetComponentsInChildren<ParticleSystem>(true))
                { effect.Simulate(6f, true, true, true); effect.Pause(); }
                foreach (var animation in world.GetComponentsInChildren<Animation>(true))
                    if (animation.playAutomatically && animation.clip != null)
                        animation.clip.SampleAnimation(animation.gameObject, 6f);

                var tank = world.TankModel.transform;
                Debug.Log("[Blitz] 11.21 hangar stand: vehicle at " + tank.position.ToString("F2") +
                    " heading " + world.StandYaw.ToString("F1") + " deg, model forward " +
                    (tank.rotation * Quaternion.Inverse(Quaternion.Euler(0f, HangarWorld.ModelYaw, 0f)) * Vector3.forward).ToString("F3") +
                    ", spawn forward " + (scene.SpawnRotation * Vector3.forward).ToString("F3"));
                foreach (var yaw in new[] { HangarWorld.SourceViewYaw, 135f, 45f, -45f })
                {
                    world.Orbit.SetView(yaw + world.StandYaw, HangarWorld.SourceViewPitch);
                    Capture(world.HangarCamera, folder + "/halloween_" + yaw + ".png");
                }

                var camera = world.HangarCamera;
                var fieldOfView = camera.fieldOfView;
                var pose = camera.transform;
                pose.SetPositionAndRotation(
                    scene.transform.TransformPoint(scene.CameraPosition),
                    Quaternion.LookRotation(scene.transform.TransformDirection(scene.CameraForward)));
                // The client's field of view is horizontal.
                camera.fieldOfView = Camera.HorizontalToVerticalFieldOfView(
                    scene.CameraFieldOfView, CaptureWidth / (float)CaptureHeight);
                var near = camera.nearClipPlane;
                camera.nearClipPlane = scene.CameraNear;
                Capture(camera, folder + "/halloween_client_camera.png");
                camera.nearClipPlane = near;

                // Close-ups of the client logos the port replaces with its own
                // art, from the cameras the exporter places in front of them.
                if (scene.Rebrands.Count == 0)
                    throw new InvalidOperationException("The Halloween hangar export replaces none of the client's logos");
                foreach (var rebrand in scene.Rebrands)
                {
                    pose.SetPositionAndRotation(
                        scene.transform.TransformPoint(rebrand.ViewPosition),
                        Quaternion.LookRotation(scene.transform.TransformDirection(rebrand.ViewForward)));
                    camera.fieldOfView = Camera.HorizontalToVerticalFieldOfView(
                        rebrand.ViewFieldOfView, CaptureWidth / (float)CaptureHeight);
                    Capture(camera, folder + "/halloween_detail_" + rebrand.Name + ".png");
                    Debug.Log("[Blitz] 11.21 hangar detail " + rebrand.Name + ": " + rebrand.Path +
                        " shows " + rebrand.Port + " in place of the " + rebrand.Client);
                }
                camera.fieldOfView = fieldOfView;

                Debug.Log("[Blitz] 11.21 hangar render renderers=" + scene.RendererCount +
                    " triangles=" + scene.TriangleCount + " particle systems=" + scene.ParticleSystemCount +
                    " animations=" + scene.AnimationCount + " states=" + scene.StateSwitcherCount +
                    " lights=" + scene.LightCount + " warnings=" + scene.Warnings.Count);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(world.gameObject);
            }
        }

        /// <summary>
        /// The Miku hangar: the stand from its opening view and around it,
        /// the client's own hangar camera, and close-ups of the clock. The
        /// scenarios run at a fixed evening (night tint of the Earth, the date
        /// 07.11.26 and the time 21:34:56 on the digits) and then at noon.
        /// </summary>
        private static void RenderMiku(string folder)
        {
            var world = HangarWorld.Create(null, new SettingsData(), kind: HangarKind.Miku);
            try
            {
                var scene = world.PortedScene;
                if (scene == null || scene.Export != PortedHangar.Miku)
                    throw new InvalidOperationException("The Miku hangar did not come up");
                File.WriteAllLines(folder + "/miku_report.txt", scene.Report);
                foreach (var warning in scene.Warnings) Debug.LogWarning("[Blitz] Miku hangar: " + warning);
                DynamicGI.UpdateEnvironment();
                var evening = new DateTime(2026, 11, 7, 21, 34, 56);
                foreach (var scenario in scene.Scenarios) scenario.Tick(0f, evening);

                foreach (var yaw in new[] { world.OpeningYaw, 135f, 45f, HangarWorld.SourceViewYaw })
                {
                    world.Orbit.SetView(yaw + world.StandYaw, HangarWorld.SourceViewPitch);
                    Capture(world.HangarCamera, folder + "/miku_" + yaw + ".png");
                }

                var camera = world.HangarCamera;
                var fieldOfView = camera.fieldOfView;
                var near = camera.nearClipPlane;
                // Every view from the client's camera clips at its near plane.
                camera.nearClipPlane = scene.CameraNear;
                var pose = camera.transform;
                var clientPosition = scene.transform.TransformPoint(scene.CameraPosition);
                pose.SetPositionAndRotation(clientPosition,
                    Quaternion.LookRotation(scene.transform.TransformDirection(scene.CameraForward)));
                camera.fieldOfView = Camera.HorizontalToVerticalFieldOfView(
                    scene.CameraFieldOfView, CaptureWidth / (float)CaptureHeight);
                Capture(camera, folder + "/miku_client_camera.png");

                foreach (var group in new[] { "DDMMYY", "HHMMSS" })
                {
                    var bounds = MeshBounds(FindNode(scene.transform, group));
                    var toClock = bounds.center - clientPosition;
                    pose.SetPositionAndRotation(clientPosition, Quaternion.LookRotation(toClock));
                    camera.fieldOfView = 2.4f * Mathf.Atan2(bounds.extents.magnitude, toClock.magnitude) * Mathf.Rad2Deg;
                    Capture(camera, folder + "/miku_clock_" + group + ".png");
                }

                var noon = new DateTime(2026, 11, 7, 12, 0, 9);
                foreach (var scenario in scene.Scenarios) scenario.Tick(0f, noon);
                pose.SetPositionAndRotation(clientPosition,
                    Quaternion.LookRotation(scene.transform.TransformDirection(scene.CameraForward)));
                camera.fieldOfView = Camera.HorizontalToVerticalFieldOfView(
                    scene.CameraFieldOfView, CaptureWidth / (float)CaptureHeight);
                Capture(camera, folder + "/miku_client_camera_noon.png");
                camera.fieldOfView = fieldOfView;
                camera.nearClipPlane = near;

                Debug.Log("[Blitz] Miku hangar render renderers=" + scene.RendererCount +
                    " triangles=" + scene.TriangleCount + " materials=" + scene.MaterialCount +
                    " scenarios=" + scene.ScenarioCount + " lights=" + scene.LightCount +
                    " warnings=" + scene.Warnings.Count);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(world.gameObject);
            }
        }

        private static Transform FindNode(Transform root, string name)
        {
            foreach (var node in root.GetComponentsInChildren<Transform>(true))
                if (node.name == name) return node;
            throw new InvalidOperationException("The hangar has no node " + name);
        }

        // World bounds of the meshes below a node, the hidden ones included.
        private static Bounds MeshBounds(Transform root)
        {
            var bounds = new Bounds();
            var any = false;
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                var local = filter.sharedMesh.bounds;
                for (var corner = 0; corner < 8; corner++)
                {
                    var point = filter.transform.TransformPoint(local.center + Vector3.Scale(local.extents,
                        new Vector3((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f)));
                    if (any) bounds.Encapsulate(point);
                    else { bounds = new Bounds(point, Vector3.zero); any = true; }
                }
            }
            if (!any) throw new InvalidOperationException(root.name + " draws nothing");
            return bounds;
        }

        private const int CaptureWidth = 1196, CaptureHeight = 540;

        private static void Capture(Camera camera,string path)
        {
            const int w=CaptureWidth,h=CaptureHeight;
            var target=new RenderTexture(w,h,24,RenderTextureFormat.ARGB32);
            var image=new Texture2D(w,h,TextureFormat.RGB24,false);
            var previous=RenderTexture.active;
            try
            {
                camera.targetTexture=target;
                // First frames warm the URP resources and the ambient probe.
                for(var i=0;i<4;i++)camera.Render();
                RenderTexture.active=target;image.ReadPixels(new Rect(0,0,w,h),0,0);image.Apply();
                File.WriteAllBytes(path,image.EncodeToPNG());
            }
            finally{camera.targetTexture=null;RenderTexture.active=previous;UnityEngine.Object.DestroyImmediate(image);UnityEngine.Object.DestroyImmediate(target);}
        }
    }
}
